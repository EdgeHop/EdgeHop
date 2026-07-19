using EdgeHop.Core;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace EdgeHop.Roslyn;

/// <summary>
/// Cross-tier JS-interop phase (the C# side of both directions):
/// <list type="bullet">
/// <item><description>C#→JS call sites — Blazor <c>IJSRuntime</c>/<c>IJSObjectReference</c>
/// <c>InvokeAsync</c>/<c>InvokeVoidAsync</c> with a compile-time-constant identifier, bound to the
/// enclosing emitted C# method (matched later against oxc's JS exports → <c>JS_CALLS</c>).</description></item>
/// <item><description>C#→JS-invokable targets — <c>[JSInvokable]</c> methods (matched later
/// against oxc's <c>DotNet.invoke*</c> call sites → <c>JS_INVOKES</c>).</description></item>
/// </list>
/// The JS halves come from the oxc extractor, so the actual edges are derived in the host after
/// both extractors run — this pass only records the C# sides.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><description>
/// <b>Interop detection</b> — an invocation resolving to a method named <c>InvokeAsync</c> or
/// <c>InvokeVoidAsync</c> whose containing type lives in <c>Microsoft.JSInterop</c> (the
/// interfaces and their extension classes). This deliberately excludes
/// <c>ComponentBase.InvokeAsync(StateHasChanged)</c> — the render-dispatcher overload in
/// <c>Microsoft.AspNetCore.Components</c>, a different namespace — which is not JS interop.
/// </description></item>
/// <item><description>
/// <b>Identifier</b> — the first (positional) argument, taken only when it is a constant string
/// (<see cref="SemanticModel.GetConstantValue(SyntaxNode, System.Threading.CancellationToken)"/>).
/// A runtime-computed identifier is not statically knowable and yields no site (hence no edge).
/// The special identifier <c>"import"</c> is not a call: it establishes a module reference (see
/// below). Call sites using named arguments are skipped (interop calls are positional in
/// practice) to keep the positional read unambiguous.
/// </description></item>
/// <item><description>
/// <b>Module correlation</b> — <c>module = await JS.InvokeAsync&lt;IJSObjectReference&gt;("import",
/// "./widget.js")</c> maps the assigned field/local symbol to the module's leaf file name
/// (<c>widget.js</c>). A later <c>module.InvokeAsync("getWidget")</c> whose receiver resolves to
/// that symbol carries the leaf, letting the host's precise matcher disambiguate same-named
/// exports across modules. A global <c>JS.InvokeVoidAsync("fn")</c> (receiver is the runtime)
/// carries no leaf. Correlation is best-effort: only direct assignment / declarator targets are
/// tracked; anything else simply yields no leaf (never a wrong one).
/// </description></item>
/// </list>
/// </remarks>
internal sealed class JsInteropPass
{
    private const string JsInteropNamespace = "Microsoft.JSInterop";
    private const string JsInvokableAttribute = "Microsoft.JSInterop.JSInvokableAttribute";
    private const string ImportIdentifier = "import";
    private static readonly HashSet<string> InvokeNames = new(StringComparer.Ordinal)
    {
        "InvokeAsync", "InvokeVoidAsync",
    };

    /// <summary>
    /// Collects this compilation's interop call sites into <paramref name="ctx"/>. Two sweeps
    /// over the authored trees: first build the receiver→module map (imports), then record the
    /// call sites resolving their receiver against it. Self-no-ops for projects that never
    /// touch <c>Microsoft.JSInterop</c>.
    /// </summary>
    public void Collect(Compilation compilation, ExtractionContext ctx, Action<string>? log = null)
    {
        // receiver symbol -> imported module leaf, across all authored trees (a field imported
        // in one partial-class tree can be invoked from another; symbol identity is shared).
        var moduleTargets = new Dictionary<ISymbol, string>(SymbolEqualityComparer.Default);
        var authoredTrees = compilation.SyntaxTrees.Where(ctx.IsAuthoredTree).ToList();

        foreach (var tree in authoredTrees)
        {
            SemanticModel? model = null;
            foreach (var invocation in InteropInvocations(tree))
            {
                model ??= compilation.GetSemanticModel(tree);
                CollectImport(invocation, model, moduleTargets);
            }
        }

        foreach (var tree in authoredTrees)
        {
            SemanticModel? model = null;
            foreach (var invocation in InteropInvocations(tree))
            {
                model ??= compilation.GetSemanticModel(tree);
                CollectCallSite(invocation, model, moduleTargets, ctx);
            }
        }

        // The JS→C# half's C# side: [JSInvokable] methods, the DotNet.invoke* targets.
        CollectInvokables(compilation.Assembly.GlobalNamespace, compilation.Assembly.Name, ctx);
    }

    /// <summary>Walks the assembly's types for <c>[JSInvokable]</c> methods, recording each as a
    /// <see cref="CsInvokableTarget"/> (identifier = the attribute argument or the method name).</summary>
    private static void CollectInvokables(INamespaceSymbol ns, string assembly, ExtractionContext ctx)
    {
        foreach (var child in ns.GetNamespaceMembers())
        {
            CollectInvokables(child, assembly, ctx);
        }

        foreach (var type in ns.GetTypeMembers())
        {
            CollectInvokableType(type, assembly, ctx);
        }
    }

    private static void CollectInvokableType(INamedTypeSymbol type, string assembly, ExtractionContext ctx)
    {
        foreach (var nested in type.GetTypeMembers())
        {
            CollectInvokableType(nested, assembly, ctx);
        }

        foreach (var method in type.GetMembers().OfType<IMethodSymbol>())
        {
            if (method.MethodKind != MethodKind.Ordinary || !ctx.IsAuthoredSymbol(method))
            {
                continue;
            }

            foreach (var attribute in method.GetAttributes())
            {
                if (attribute.AttributeClass?.OriginalDefinition.ToDisplayString() != JsInvokableAttribute)
                {
                    continue;
                }

                // [JSInvokable("Name")] overrides the identifier; bare [JSInvokable] uses the method name.
                var identifier = attribute.ConstructorArguments.Length > 0
                                 && attribute.ConstructorArguments[0].Value is string explicitName
                                 && explicitName.Length > 0
                    ? explicitName
                    : method.Name;

                var doc = method.DeclaringSyntaxReferences.Length > 0
                    ? ctx.GetDocForTree(method.DeclaringSyntaxReferences[0].SyntaxTree)
                    : null;
                ctx.AddInvokableTarget(SymbolIdFormat.GetId(method), identifier, assembly, method.IsStatic, doc);
                break;
            }
        }
    }

    private static IEnumerable<InvocationExpressionSyntax> InteropInvocations(SyntaxTree tree) =>
        tree.GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>();

    /// <summary>Records a <c>"import"</c> call's assigned symbol → module leaf, when both the
    /// identifier and the specifier are constants and the result flows into a trackable
    /// (field/local/property) target.</summary>
    private static void CollectImport(
        InvocationExpressionSyntax invocation,
        SemanticModel model,
        Dictionary<ISymbol, string> moduleTargets)
    {
        if (!TryReadInterop(invocation, model, out var identifier, out var arguments)
            || !string.Equals(identifier, ImportIdentifier, StringComparison.Ordinal)
            || arguments.Count < 2
            || model.GetConstantValue(arguments[1].Expression).Value is not string specifier)
        {
            return;
        }

        if (AssignmentTarget(invocation, model) is { } target)
        {
            moduleTargets[target] = Leaf(specifier);
        }
    }

    /// <summary>Records a non-<c>import</c> interop call as a <see cref="CsJsInteropSite"/>,
    /// bound to its enclosing emitted method and (when correlated) its module leaf.</summary>
    private static void CollectCallSite(
        InvocationExpressionSyntax invocation,
        SemanticModel model,
        Dictionary<ISymbol, string> moduleTargets,
        ExtractionContext ctx)
    {
        if (!TryReadInterop(invocation, model, out var identifier, out _)
            || string.Equals(identifier, ImportIdentifier, StringComparison.Ordinal))
        {
            return;
        }

        if (ctx.EnclosingEmittedMethodId(invocation, model) is not { } callerId)
        {
            return; // call sits outside an authored method (e.g. a field initializer): no edge.
        }

        var moduleLeaf = ReceiverModuleLeaf(invocation, model, moduleTargets);
        ctx.AddInteropSite(callerId, identifier, moduleLeaf, ctx.GetDocForTree(invocation.SyntaxTree));
    }

    /// <summary>
    /// Recognizes a JS-interop invocation and reads its constant identifier. True only when the
    /// callee is <c>Invoke[Void]Async</c> in <c>Microsoft.JSInterop</c>, the call is positional,
    /// and the first argument is a constant string. <paramref name="arguments"/> is the raw
    /// argument list (the caller reads the module specifier from it for imports).
    /// </summary>
    private static bool TryReadInterop(
        InvocationExpressionSyntax invocation,
        SemanticModel model,
        out string identifier,
        out SeparatedSyntaxList<ArgumentSyntax> arguments)
    {
        identifier = string.Empty;
        arguments = invocation.ArgumentList.Arguments;

        if (arguments.Count == 0 || arguments.Any(a => a.NameColon is not null))
        {
            return false; // no args, or named args (unsupported positional read).
        }

        if (model.GetSymbolInfo(invocation).Symbol is not IMethodSymbol callee
            || !InvokeNames.Contains(callee.Name)
            || callee.ContainingType?.ContainingNamespace?.ToDisplayString() != JsInteropNamespace)
        {
            return false;
        }

        if (model.GetConstantValue(arguments[0].Expression).Value is not string name)
        {
            return false; // non-constant identifier: not statically knowable.
        }

        identifier = name;
        return true;
    }

    /// <summary>The field/local/property symbol an import's result is stored into —
    /// <c>x = await …import…</c> or <c>var x = await …import…</c> — unwrapping the
    /// <c>await</c>. Null for any other flow (best-effort correlation).</summary>
    private static ISymbol? AssignmentTarget(InvocationExpressionSyntax invocation, SemanticModel model)
    {
        SyntaxNode value = invocation.Parent is AwaitExpressionSyntax awaited ? awaited : invocation;

        return value.Parent switch
        {
            AssignmentExpressionSyntax assignment when assignment.Right == value =>
                model.GetSymbolInfo(assignment.Left).Symbol,
            EqualsValueClauseSyntax { Parent: VariableDeclaratorSyntax declarator } equals
                when equals.Value == value =>
                model.GetDeclaredSymbol(declarator),
            _ => null,
        };
    }

    /// <summary>The module leaf of an interop call's receiver, when the receiver resolves to a
    /// symbol previously imported (<c>module.InvokeAsync(…)</c>); null for a global runtime call
    /// or an uncorrelated receiver.</summary>
    private static string? ReceiverModuleLeaf(
        InvocationExpressionSyntax invocation,
        SemanticModel model,
        Dictionary<ISymbol, string> moduleTargets)
    {
        if (invocation.Expression is MemberAccessExpressionSyntax memberAccess
            && model.GetSymbolInfo(Unwrap(memberAccess.Expression)).Symbol is { } receiver
            && moduleTargets.TryGetValue(receiver, out var leaf))
        {
            return leaf;
        }

        return null;
    }

    /// <summary>Strips parentheses and the null-forgiving <c>!</c> so the receiver of
    /// <c>_module!.InvokeAsync(…)</c> resolves to the underlying field/local symbol.</summary>
    private static ExpressionSyntax Unwrap(ExpressionSyntax expression) => expression switch
    {
        ParenthesizedExpressionSyntax parenthesized => Unwrap(parenthesized.Expression),
        PostfixUnaryExpressionSyntax postfix when postfix.IsKind(SyntaxKind.SuppressNullableWarningExpression) =>
            Unwrap(postfix.Operand),
        _ => expression,
    };

    /// <summary>The trailing path segment of a module specifier (e.g. <c>./js/widget.js</c> →
    /// <c>widget.js</c>), so a web-relative import path matches the disk-relative module id the
    /// oxc extractor emits.</summary>
    private static string Leaf(string specifier)
    {
        var cut = specifier.LastIndexOfAny(['/', '\\']);
        return cut < 0 ? specifier : specifier[(cut + 1)..];
    }
}
