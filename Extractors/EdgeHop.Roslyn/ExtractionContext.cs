using EdgeHop.Core;
using Microsoft.CodeAnalysis;

namespace EdgeHop.Roslyn;

/// <summary>
/// Mutable per-run extraction state: the branch, the solution directory, the deduped
/// node/edge accumulators, and the per-tree document cache that applies the Razor
/// generated-file remap. Branch is constant for the whole run, so deduping nodes by
/// <c>Id</c> and edges by <c>(Type, FromId, ToId)</c> is exactly the required
/// <c>(Branch, Id)</c> / <c>(Branch, FromId, ToId, Type)</c> dedupe.
/// </summary>
internal sealed class ExtractionContext
{
    private readonly HashSet<string> _edgeKeys = new(StringComparer.Ordinal);
    private readonly Dictionary<SyntaxTree, string?> _treeDocs = new();
    private readonly Dictionary<SyntaxTree, bool> _authoredTrees = new();

    public ExtractionContext(string branch, string? solutionDirectory)
    {
        Branch = branch;
        SolutionDirectory = solutionDirectory;
    }

    public string Branch { get; }

    public string? SolutionDirectory { get; }

    /// <summary>Emitted nodes keyed by stable ID (insertion order preserved).</summary>
    public Dictionary<string, NodeRow> Nodes { get; } = new(StringComparer.Ordinal);

    /// <summary>Candidate edges; endpoints are re-checked against <see cref="Nodes"/> at the end.</summary>
    public List<EdgeRow> Edges { get; } = new();

    private readonly HashSet<string> _interopKeys = new(StringComparer.Ordinal);

    /// <summary>C# JS-interop call sites collected by <see cref="JsInteropPass"/>; the host
    /// correlates them against the oxc extractor's JS exports to derive JS_CALLS. Deduped by
    /// <c>(CallerId, FunctionName, ModuleLeaf)</c>.</summary>
    public List<CsJsInteropSite> InteropSites { get; } = new();

    /// <summary>Records a C# → JS interop call site (first writer wins on the dedupe key).</summary>
    public void AddInteropSite(string callerId, string functionName, string? moduleLeaf, string? sourceDoc)
    {
        if (_interopKeys.Add($"{callerId}\n{functionName}\n{moduleLeaf}"))
        {
            InteropSites.Add(new CsJsInteropSite(callerId, functionName, moduleLeaf, sourceDoc));
        }
    }

    private readonly HashSet<string> _invokableKeys = new(StringComparer.Ordinal);

    /// <summary>C# <c>[JSInvokable]</c> methods collected by <see cref="JsInteropPass"/>; the host
    /// correlates them against the oxc extractor's <c>DotNet.invoke*</c> call sites to derive
    /// JS_INVOKES. Deduped by method id.</summary>
    public List<CsInvokableTarget> InvokableTargets { get; } = new();

    /// <summary>Records a C# <c>[JSInvokable]</c> target (first writer wins on the method id).</summary>
    public void AddInvokableTarget(string methodId, string identifier, string assembly, bool isStatic, string? sourceDoc)
    {
        if (_invokableKeys.Add(methodId))
        {
            InvokableTargets.Add(new CsInvokableTarget(methodId, identifier, assembly, isStatic, sourceDoc));
        }
    }

    /// <summary>
    /// The stable node id of the emitted C# method enclosing <paramref name="node"/> —
    /// anonymous/local functions unwound to their containing method (the CALLS attribution
    /// rule) — or null when the enclosing symbol is not an authored method. Shared by the
    /// HTTP and JS-interop passes to bind a call site to its caller node.
    /// </summary>
    public string? EnclosingEmittedMethodId(SyntaxNode node, SemanticModel semanticModel)
    {
        var symbol = semanticModel.GetEnclosingSymbol(node.SpanStart);
        while (symbol is IMethodSymbol { MethodKind: MethodKind.AnonymousFunction or MethodKind.LocalFunction } nested)
        {
            symbol = nested.ContainingSymbol;
        }

        return symbol is IMethodSymbol method
               && method.OriginalDefinition is IMethodSymbol definition
               && IsAuthoredSymbol(definition)
            ? SymbolIdFormat.GetId(definition)
            : null;
    }

    /// <summary>
    /// Emits a node for <paramref name="symbol"/> (first writer wins on duplicates)
    /// and returns its stable ID. The symbol is normalized via
    /// <see cref="ISymbol.OriginalDefinition"/>, matching <see cref="SymbolIdFormat.GetId"/>,
    /// so <c>Kind</c> always equals the ID's prefix.
    /// </summary>
    public string AddNode(
        ISymbol symbol,
        string? sourceDoc,
        bool isComponent = false,
        IReadOnlyList<string>? routes = null)
    {
        var definition = symbol.OriginalDefinition;
        var id = SymbolIdFormat.GetId(definition);
        if (!Nodes.ContainsKey(id))
        {
            Nodes.Add(id, new NodeRow(
                Branch,
                id,
                definition.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
                definition.Kind.ToString(),
                sourceDoc,
                definition.ContainingAssembly?.Name ?? "",
                definition.IsAbstract,
                isComponent,
                routes));
        }

        return id;
    }

    /// <summary>
    /// Replaces the <see cref="NodeRow.Routes"/> of an already-emitted node. Used by the
    /// HTTP pass to stamp endpoint route templates onto registration-method nodes AFTER
    /// the symbol walk emitted them (AddNode is first-writer-wins, so the routes cannot
    /// ride along on emission). No-ops for unknown ids — the caller's symbol may have
    /// been skipped by the authored test.
    /// </summary>
    public void SetRoutes(string nodeId, IReadOnlyList<string>? routes)
    {
        if (Nodes.TryGetValue(nodeId, out var node))
        {
            Nodes[nodeId] = node with { Routes = routes };
        }
    }

    /// <summary>Adds an edge, deduped by <c>(Branch, FromId, ToId, Type)</c>; first writer wins.</summary>
    public void AddEdge(string fromId, string toId, string type, string? sourceDoc)
    {
        if (_edgeKeys.Add($"{type}\n{fromId}\n{toId}"))
        {
            Edges.Add(new EdgeRow(Branch, fromId, toId, type, sourceDoc));
        }
    }

    /// <summary>
    /// The SourceDoc for everything declared in <paramref name="tree"/>, cached per tree.
    /// Razor-generated trees (<c>*_razor.g.cs</c>) are remapped to the authored
    /// <c>.razor</c> document via their <c>#pragma checksum</c> directive (see
    /// <see cref="RazorGeneratedDocs"/>); all other trees use their own file path. Either
    /// way the path is normalized by <see cref="ToRelativeDoc"/>.
    /// </summary>
    public string? GetDocForTree(SyntaxTree? tree)
    {
        if (tree is null)
        {
            return null;
        }

        if (_treeDocs.TryGetValue(tree, out var cached))
        {
            return cached;
        }

        var doc = RazorGeneratedDocs.TryGetRazorPath(tree, out var razorPath)
            ? ToRelativeDoc(razorPath)
            : ToRelativeDoc(tree.FilePath);
        _treeDocs.Add(tree, doc);
        return doc;
    }

    /// <summary>
    /// True when <paramref name="tree"/> holds authored code, cached per tree.
    /// Razor-generated trees count as authored (they remap to their <c>.razor</c>
    /// document); trees whose document sits under an <c>obj</c>/<c>bin</c> path segment
    /// are SDK-injected build artifacts (e.g. the Razor SDK's
    /// <c>obj/.../EmbeddedAttribute.cs</c>) and are NOT authored — symbols declared only
    /// there get no node.
    /// </summary>
    public bool IsAuthoredTree(SyntaxTree tree)
    {
        if (_authoredTrees.TryGetValue(tree, out var cached))
        {
            return cached;
        }

        bool authored;
        if (RazorGeneratedDocs.TryGetRazorPath(tree, out _))
        {
            authored = true;
        }
        else
        {
            var doc = GetDocForTree(tree);
            authored = doc is null || !HasBuildArtifactSegment(doc);
        }

        _authoredTrees.Add(tree, authored);
        return authored;
    }

    /// <summary>
    /// The single authored-source test used for node emission and edge targets:
    /// declared in source, not compiler-generated, and at least one declaring tree is
    /// authored (see <see cref="IsAuthoredTree"/>).
    /// </summary>
    public bool IsAuthoredSymbol(ISymbol symbol) =>
        !symbol.IsImplicitlyDeclared
        && symbol.Locations.Any(l => l.IsInSource
            && l.SourceTree is { } tree
            && IsAuthoredTree(tree));

    private static bool HasBuildArtifactSegment(string doc) =>
        doc.Split('/').Any(s =>
            s.Equals("obj", StringComparison.OrdinalIgnoreCase)
            || s.Equals("bin", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Normalizes a document path: relative to the solution directory (when known) with
    /// backslashes converted to forward slashes. Falls back to the absolute path (still
    /// forward-slashed) when the solution has no file path; null for empty paths
    /// (e.g. generated syntax trees without a file path).
    /// </summary>
    public string? ToRelativeDoc(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }

        var full = Path.GetFullPath(path);
        var relative = SolutionDirectory is null
            ? full
            : Path.GetRelativePath(SolutionDirectory, full);
        return relative.Replace('\\', '/');
    }
}
