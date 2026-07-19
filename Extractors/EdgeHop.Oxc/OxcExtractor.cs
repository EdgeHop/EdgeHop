using System.Diagnostics;
using System.Text;
using System.Text.Json;
using EdgeHop.Core;

namespace EdgeHop.Oxc;

/// <summary>
/// The oxc JS/TS extractor behind the <see cref="IExtractor"/> seam: discovers JS/TS (and
/// inline <c>&lt;script&gt;</c>) under the solution root, hands them to the vendored native
/// <c>edgehop-oxc</c> binary over stdin/stdout, and maps its graph JSON into
/// <see cref="NodeRow"/>/<see cref="EdgeRow"/> (stamped with the run's branch). Every JS node id
/// carries a <c>js|</c> tier tag, so the rows merge with the C# graph in one branch with no
/// collision. Contributes nothing (an empty result) on a solution with no authored JS, so a
/// pure-C# index is unchanged.
/// </summary>
public sealed class OxcExtractor : IExtractor
{
    private const string BinaryName = "edgehop-oxc.exe";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public string Name => "oxc";

    public async Task<ExtractionOutcome> ExtractAsync(
        ExtractionRequest request, Action<string>? log = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var stopwatch = Stopwatch.StartNew();
        // Directory mode sets SolutionDirectory to the root; solution mode leaves it set to the
        // .sln's directory. The SolutionPath fallback only matters for a legacy request that
        // supplied a solution path but no directory — and SolutionPath may itself be null now
        // (a bare-directory target always carries its root in SolutionDirectory instead).
        var root = request.SolutionDirectory
                   ?? (request.SolutionPath is { } sln ? Path.GetDirectoryName(Path.GetFullPath(sln)) : null);
        if (root is null || !Directory.Exists(root))
        {
            return Empty("oxc (no source root)", stopwatch.ElapsedMilliseconds);
        }

        var modules = JsModuleDiscovery.Discover(root, log);
        var loadMs = stopwatch.ElapsedMilliseconds;
        if (modules.Count == 0)
        {
            // Pure-C# solution: JS extraction is a no-op and the merged reconcile is C# only.
            return Empty("oxc (no JS/TS)", loadMs);
        }

        var binary = LocateBinary();
        if (binary is null)
        {
            // Vendored, so normally present. If absent, skip JS with a warning rather than fail
            // the whole index — but the caller still sees no JS nodes this run.
            log?.Invoke($"oxc: {BinaryName} not found next to the executable; skipping JS/TS extraction.");
            return new ExtractionOutcome(
                new ExtractionResult([], []),
                FailureDiagnostics: [],
                WarningDiagnostics: [$"oxc: {BinaryName} not deployed; {modules.Count} JS/TS module(s) skipped."],
                LoadDescription: "oxc (binary missing)",
                LoadMs: loadMs,
                ExtractMs: 0);
        }

        stopwatch.Restart();
        OxcResponse response;
        try
        {
            response = await RunBinaryAsync(binary, modules, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // A binary run-and-error is a hard failure for the JS tier: report it as a Failure so
            // the host aborts rather than reconciling a JS-less set that would prune prior JS nodes.
            return new ExtractionOutcome(
                new ExtractionResult([], []),
                FailureDiagnostics: [$"oxc extraction failed: {ex.Message}"],
                WarningDiagnostics: [],
                LoadDescription: "oxc",
                LoadMs: loadMs,
                ExtractMs: stopwatch.ElapsedMilliseconds);
        }

        var extractMs = stopwatch.ElapsedMilliseconds;
        var (nodes, edges, exports, dotnetCalls, droppedEdgeTypes) = Map(response, request.Branch);

        var warnings = new List<string>();
        foreach (var diagnostic in response.Diagnostics)
        {
            log?.Invoke($"oxc: {diagnostic}");
        }

        if (droppedEdgeTypes.Count > 0)
        {
            warnings.Add($"oxc: dropped edges of unknown type(s): {string.Join(", ", droppedEdgeTypes)}.");
        }

        log?.Invoke($"oxc: {modules.Count} module(s) → {nodes.Count} nodes, {edges.Count} edges.");

        // JS halves of the interop surface: exported symbols (matched against C# IJSRuntime call
        // sites → JS_CALLS) and DotNet.invoke* call sites (matched against C# [JSInvokable]
        // methods → JS_INVOKES). Null when the module contributes neither.
        var interop = exports.Count > 0 || dotnetCalls.Count > 0
            ? new InteropSurface([], exports, dotnetCalls, [])
            : null;

        return new ExtractionOutcome(
            new ExtractionResult(nodes, edges, interop),
            FailureDiagnostics: [],
            WarningDiagnostics: warnings,
            LoadDescription: $"oxc ({modules.Count} module(s))",
            LoadMs: loadMs,
            ExtractMs: extractMs);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static ExtractionOutcome Empty(string description, long loadMs) =>
        new(new ExtractionResult([], []), [], [], description, loadMs, 0);

    /// <summary>The vendored native binary deployed next to the running executable.</summary>
    private static string? LocateBinary()
    {
        var candidate = Path.Combine(AppContext.BaseDirectory, BinaryName);
        return File.Exists(candidate) ? candidate : null;
    }

    private static async Task<OxcResponse> RunBinaryAsync(
        string binary, IReadOnlyList<OxcModuleInput> modules, CancellationToken ct)
    {
        var requestJson = JsonSerializer.Serialize(new OxcRequest { Modules = modules }, JsonOptions);

        var startInfo = new ProcessStartInfo
        {
            FileName = binary,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardInputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            StandardOutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
        };

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"failed to start {BinaryName}");

        // Read stdout/stderr concurrently with writing stdin so a large graph cannot deadlock on
        // a full pipe buffer.
        var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = process.StandardError.ReadToEndAsync(ct);

        await process.StandardInput.WriteAsync(requestJson.AsMemory(), ct).ConfigureAwait(false);
        process.StandardInput.Close();

        await process.WaitForExitAsync(ct).ConfigureAwait(false);
        var stdout = await stdoutTask.ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"{BinaryName} exited {process.ExitCode}: {stderr.Trim()}");
        }

        return JsonSerializer.Deserialize<OxcResponse>(stdout, JsonOptions)
            ?? throw new InvalidOperationException($"{BinaryName} returned empty output");
    }

    /// <summary>Maps the binary's rows to <see cref="NodeRow"/>/<see cref="EdgeRow"/>: JS nodes
    /// have no assembly and are never abstract/components; edges are kept only when both endpoints
    /// are emitted nodes and the type is a known <see cref="EdgeTypes"/> (the writer rejects a
    /// batch with any unknown type, so we filter here). Interop exports and DotNet.invoke* call
    /// sites are kept only when their JS node was actually emitted (so a derived JS_CALLS /
    /// JS_INVOKES edge can never dangle).</summary>
    private static (IReadOnlyList<NodeRow> Nodes, IReadOnlyList<EdgeRow> Edges, IReadOnlyList<JsInteropExport> Exports, IReadOnlyList<JsDotNetCall> DotNetCalls, IReadOnlySet<string> DroppedEdgeTypes) Map(
        OxcResponse response, string branch)
    {
        var nodes = new List<NodeRow>(response.Nodes.Count);
        var nodeIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var node in response.Nodes)
        {
            if (nodeIds.Add(node.Id))
            {
                nodes.Add(new NodeRow(
                    branch, node.Id, node.Name, node.Kind, node.SourceDoc, Assembly: "", IsAbstract: false));
            }
        }

        var edges = new List<EdgeRow>(response.Edges.Count);
        var edgeKeys = new HashSet<string>(StringComparer.Ordinal);
        var droppedTypes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var edge in response.Edges)
        {
            if (!EdgeTypes.IsValid(edge.Type))
            {
                droppedTypes.Add(edge.Type);
                continue;
            }

            if (nodeIds.Contains(edge.FromId)
                && nodeIds.Contains(edge.ToId)
                && edgeKeys.Add($"{edge.Type}\n{edge.FromId}\n{edge.ToId}"))
            {
                edges.Add(new EdgeRow(branch, edge.FromId, edge.ToId, edge.Type, edge.SourceDoc));
            }
        }

        var exports = new List<JsInteropExport>();
        var exportKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var export in response.InteropExports)
        {
            if (nodeIds.Contains(export.SymbolId) && exportKeys.Add(export.SymbolId))
            {
                exports.Add(new JsInteropExport(
                    export.Name, export.ModuleId, export.SymbolId, export.SourceDoc));
            }
        }

        var dotnetCalls = new List<JsDotNetCall>();
        var dotnetKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var call in response.DotNetCalls)
        {
            if (nodeIds.Contains(call.CallerId)
                && dotnetKeys.Add($"{call.CallerId}\n{call.IsStatic}\n{call.Assembly}\n{call.Identifier}"))
            {
                dotnetCalls.Add(new JsDotNetCall(
                    call.CallerId, call.Assembly, call.Identifier, call.IsStatic, call.SourceDoc));
            }
        }

        return (nodes, edges, exports, dotnetCalls, droppedTypes);
    }
}
