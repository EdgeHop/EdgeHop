namespace EdgeHop.Core;

/// <summary>How aggressively C#→JS interop call sites are matched to JS exports.</summary>
public enum JsInteropMode
{
    /// <summary>No <c>JS_CALLS</c> edges are derived.</summary>
    Off,

    /// <summary>
    /// Emit an edge only when the target is unambiguous: a call site imported from a specific
    /// module (<c>ModuleLeaf</c> set) matches the single export of that name in that module;
    /// a global call (no module) matches only when exactly one JS export in the whole solution
    /// carries the name. Ambiguous names produce no edge. The default — every edge is one a
    /// reader can trust.
    /// </summary>
    Precise,

    /// <summary>
    /// Name-only matching: a call site fans out to <em>every</em> JS export sharing its name,
    /// ignoring module correlation. Surfaces more edges (including plausible-but-wrong ones when
    /// two modules export the same name) — opt in via <c>EDGEHOP_JS_INTEROP=broad</c>.
    /// </summary>
    Broad,
}

/// <summary>
/// Correlates the interop call sites and targets gathered across all extractors
/// (see <see cref="InteropSurface"/>) into cross-tier edges, in both directions:
/// <see cref="Match"/> emits C#→JS <c>JS_CALLS</c>, <see cref="MatchDotNetInvokes"/> emits
/// JS→C# <c>JS_INVOKES</c>. Pure and backend-neutral: the host runs these after merging every
/// extractor's rows, so both endpoints already coexist as nodes in one branch.
/// </summary>
public static class JsInteropMatcher
{
    /// <summary>Env var selecting the match mode (no credentials): <c>precise</c> (default),
    /// <c>broad</c>, or <c>off</c>.</summary>
    public const string ModeEnvVar = "EDGEHOP_JS_INTEROP";

    /// <summary>Resolves <see cref="ModeEnvVar"/> to a mode; unset/unrecognized → precise.</summary>
    public static JsInteropMode ResolveMode(string? raw) =>
        (raw ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "off" or "none" or "false" => JsInteropMode.Off,
            "broad" => JsInteropMode.Broad,
            _ => JsInteropMode.Precise, // "precise", "", anything else
        };

    /// <summary>Resolves the mode from the process environment.</summary>
    public static JsInteropMode ResolveMode() =>
        ResolveMode(Environment.GetEnvironmentVariable(ModeEnvVar));

    /// <summary>
    /// Derives the <c>JS_CALLS</c> edges for <paramref name="branch"/> from
    /// <paramref name="surface"/>. An edge is emitted only when both endpoints exist in
    /// <paramref name="existingNodeIds"/> (the caller and the exported JS symbol), so a dropped
    /// node can never leave a dangling edge. Deduped by <c>(FromId, ToId)</c>. Empty when the
    /// mode is <see cref="JsInteropMode.Off"/> or either half of the surface is empty.
    /// </summary>
    public static IReadOnlyList<EdgeRow> Match(
        InteropSurface surface,
        IReadOnlySet<string> existingNodeIds,
        string branch,
        JsInteropMode mode,
        Action<string>? log = null)
    {
        ArgumentNullException.ThrowIfNull(surface);
        ArgumentNullException.ThrowIfNull(existingNodeIds);
        ArgumentException.ThrowIfNullOrWhiteSpace(branch);

        if (mode == JsInteropMode.Off
            || surface.CsCallSites.Count == 0
            || surface.JsExports.Count == 0)
        {
            return [];
        }

        // Index exports by name (one name may be exported by several modules).
        var exportsByName = new Dictionary<string, List<JsInteropExport>>(StringComparer.Ordinal);
        foreach (var export in surface.JsExports)
        {
            if (!exportsByName.TryGetValue(export.Name, out var list))
            {
                exportsByName[export.Name] = list = new List<JsInteropExport>();
            }

            list.Add(export);
        }

        var edges = new List<EdgeRow>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var ambiguous = 0;
        foreach (var site in surface.CsCallSites)
        {
            if (!existingNodeIds.Contains(site.CallerId)
                || !exportsByName.TryGetValue(site.FunctionName, out var named))
            {
                continue; // caller not emitted, or no JS export of that name: no honest edge.
            }

            foreach (var target in ResolveTargets(site, named, mode, ref ambiguous))
            {
                if (existingNodeIds.Contains(target.SymbolId)
                    && seen.Add($"{site.CallerId}\n{target.SymbolId}"))
                {
                    edges.Add(new EdgeRow(
                        branch, site.CallerId, target.SymbolId, EdgeTypes.JsCalls, site.SourceDoc));
                }
            }
        }

        if (edges.Count > 0 || ambiguous > 0)
        {
            log?.Invoke(
                $"JS interop ({mode.ToString().ToLowerInvariant()}): {surface.CsCallSites.Count} C# call site(s), " +
                $"{surface.JsExports.Count} JS export(s), {edges.Count} JS_CALLS edge(s)" +
                (ambiguous > 0 ? $", {ambiguous} skipped as ambiguous" : "") + ".");
        }

        return edges;
    }

    /// <summary>
    /// Derives the JS→C# <c>JS_INVOKES</c> edges for <paramref name="branch"/> from
    /// <paramref name="surface"/>: each JS <c>DotNet.invokeMethod[Async]</c> /
    /// <c>objRef.invokeMethod[Async]</c> site matched to the C# <c>[JSInvokable]</c> method it
    /// targets. Precise matches a static call on <c>(assembly, identifier)</c> — Blazor's unique
    /// key — and an instance call on a solution-unique identifier; broad fans a call out to every
    /// invokable method sharing its identifier. Both endpoints must exist in
    /// <paramref name="existingNodeIds"/>; deduped by <c>(FromId, ToId)</c>.
    /// </summary>
    public static IReadOnlyList<EdgeRow> MatchDotNetInvokes(
        InteropSurface surface,
        IReadOnlySet<string> existingNodeIds,
        string branch,
        JsInteropMode mode,
        Action<string>? log = null)
    {
        ArgumentNullException.ThrowIfNull(surface);
        ArgumentNullException.ThrowIfNull(existingNodeIds);
        ArgumentException.ThrowIfNullOrWhiteSpace(branch);

        if (mode == JsInteropMode.Off
            || surface.JsDotNetCalls.Count == 0
            || surface.CsInvokableTargets.Count == 0)
        {
            return [];
        }

        // Three views of the invokable targets: static keyed by (assembly, identifier) — Blazor's
        // per-assembly-unique key; instance keyed by identifier; and everything by identifier for
        // the broad name-only fan-out.
        var staticByKey = new Dictionary<(string, string), List<CsInvokableTarget>>();
        var instanceById = new Dictionary<string, List<CsInvokableTarget>>(StringComparer.Ordinal);
        var allById = new Dictionary<string, List<CsInvokableTarget>>(StringComparer.Ordinal);
        foreach (var target in surface.CsInvokableTargets)
        {
            Add(allById, target.Identifier, target);
            if (target.IsStatic)
            {
                if (!staticByKey.TryGetValue((target.Assembly, target.Identifier), out var s))
                {
                    staticByKey[(target.Assembly, target.Identifier)] = s = new List<CsInvokableTarget>();
                }

                s.Add(target);
            }
            else
            {
                Add(instanceById, target.Identifier, target);
            }
        }

        var edges = new List<EdgeRow>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var ambiguous = 0;
        foreach (var call in surface.JsDotNetCalls)
        {
            if (!existingNodeIds.Contains(call.CallerId))
            {
                continue;
            }

            foreach (var target in ResolveInvokeTargets(call, staticByKey, instanceById, allById, mode, ref ambiguous))
            {
                if (existingNodeIds.Contains(target.MethodId)
                    && seen.Add($"{call.CallerId}\n{target.MethodId}"))
                {
                    edges.Add(new EdgeRow(
                        branch, call.CallerId, target.MethodId, EdgeTypes.JsInvokes, call.SourceDoc));
                }
            }
        }

        if (edges.Count > 0 || ambiguous > 0)
        {
            log?.Invoke(
                $"DotNet interop ({mode.ToString().ToLowerInvariant()}): {surface.JsDotNetCalls.Count} JS call site(s), " +
                $"{surface.CsInvokableTargets.Count} [JSInvokable] method(s), {edges.Count} JS_INVOKES edge(s)" +
                (ambiguous > 0 ? $", {ambiguous} skipped as ambiguous" : "") + ".");
        }

        return edges;
    }

    /// <summary>The invokable methods one JS call resolves to under the given mode. Broad fans out
    /// by identifier across all targets; precise keys a static call on <c>(assembly, identifier)</c>
    /// and an instance call on a unique identifier.</summary>
    private static IEnumerable<CsInvokableTarget> ResolveInvokeTargets(
        JsDotNetCall call,
        Dictionary<(string, string), List<CsInvokableTarget>> staticByKey,
        Dictionary<string, List<CsInvokableTarget>> instanceById,
        Dictionary<string, List<CsInvokableTarget>> allById,
        JsInteropMode mode,
        ref int ambiguous)
    {
        if (mode == JsInteropMode.Broad)
        {
            return allById.TryGetValue(call.Identifier, out var all) ? all : [];
        }

        // Precise.
        if (call.IsStatic && call.Assembly is { } assembly)
        {
            // (assembly, identifier) is unique per Blazor's rule → at most one.
            return staticByKey.TryGetValue((assembly, call.Identifier), out var exact) && exact.Count == 1
                ? exact
                : [];
        }

        // Instance (or a static call with no resolvable assembly): a unique identifier only.
        if (instanceById.TryGetValue(call.Identifier, out var candidates))
        {
            if (candidates.Count == 1)
            {
                return candidates;
            }

            ambiguous++;
        }

        return [];
    }

    private static void Add(Dictionary<string, List<CsInvokableTarget>> index, string key, CsInvokableTarget target)
    {
        if (!index.TryGetValue(key, out var list))
        {
            index[key] = list = new List<CsInvokableTarget>();
        }

        list.Add(target);
    }

    /// <summary>The exports one call site resolves to under the given mode (see
    /// <see cref="JsInteropMode"/>). Precise yields at most one; broad fans out.</summary>
    private static IEnumerable<JsInteropExport> ResolveTargets(
        CsJsInteropSite site, List<JsInteropExport> named, JsInteropMode mode, ref int ambiguous)
    {
        if (mode == JsInteropMode.Broad)
        {
            return named; // name-only, every module.
        }

        // Precise: disambiguate by the imported module when the site knows one.
        var candidates = site.ModuleLeaf is { } leaf
            ? named.Where(e => string.Equals(Leaf(e.ModuleId), leaf, StringComparison.OrdinalIgnoreCase)).ToList()
            : named;

        if (candidates.Count == 1)
        {
            return candidates;
        }

        if (candidates.Count > 1)
        {
            ambiguous++;
        }

        return []; // 0 (no module match) or >1 (ambiguous): no edge under precise.
    }

    /// <summary>The trailing path segment of a module id (its file name).</summary>
    private static string Leaf(string moduleId)
    {
        var cut = moduleId.LastIndexOfAny(['/', '\\']);
        return cut < 0 ? moduleId : moduleId[(cut + 1)..];
    }
}
