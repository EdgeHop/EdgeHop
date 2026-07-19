using EdgeHop.Core;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace EdgeHop.Roslyn;

/// <summary>
/// Razor component phase: derives the edges that exist in component markup but are
/// invisible to the plain C# walk, purely from the Razor-generated C# trees already in
/// the compilation (no Razor markup parsing, no extra packages).
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><description>
/// <b>RENDERS</b> (component → child component): every invocation in a generated tree
/// resolving to the generic <c>RenderTreeBuilder.OpenComponent&lt;T&gt;</c> yields an edge
/// from the tree's component class to <c>T</c> (normalized via
/// <see cref="ISymbol.OriginalDefinition"/>, source-declared targets only — framework and
/// library components such as MudBlazor are skipped). Scanning the whole tree covers
/// nested <c>RenderFragment</c> lambdas (<c>__builder2</c>, …) and the
/// <c>__Blazor.*.TypeInference</c> helper bodies the generator emits for
/// inferred-generic component tags.
/// </description></item>
/// <item><description>
/// <b>Handler bindings reuse CALLS</b> (binding method → handler): markup like
/// <c>@onclick="HandleLogin"</c> compiles to a method-group argument of
/// <c>EventCallbackFactory.Create*</c> /
/// <c>RuntimeHelpers.CreateInferredEventCallback</c>, which the ordinary CALLS walk
/// cannot see (a method group is not an invocation). Each such argument that resolves to
/// a source-declared method produces <c>CALLS</c> from the enclosing emitted method (in
/// practice <c>BuildRenderTree</c>) to the handler — consistent with how lambda handlers
/// (<c>@onclick="() =&gt; Foo()"</c>) already land on the containing method today, and
/// visible to <c>get_callers</c> with no reader changes. Bind lambdas
/// (<c>__value =&gt; _field = __value</c>) resolve to no method group and correctly
/// produce nothing.
/// </description></item>
/// <item><description>
/// Ambiguous method groups (<c>CandidateSymbols</c>) are deliberately ignored — an
/// ambiguous binding is not a resolved binding, matching the CALLS policy.
/// </description></item>
/// </list>
/// </remarks>
internal static class RazorComponentPass
{
    private const string ComponentBaseFullName = "Microsoft.AspNetCore.Components.ComponentBase";
    private const string RenderTreeBuilderFullName = "Microsoft.AspNetCore.Components.Rendering.RenderTreeBuilder";
    private const string EventCallbackFactoryFullName = "Microsoft.AspNetCore.Components.EventCallbackFactory";
    private const string RuntimeHelpersFullName = "Microsoft.AspNetCore.Components.CompilerServices.RuntimeHelpers";

    /// <summary>
    /// True when <paramref name="type"/> inherits (directly or transitively, including
    /// through source-declared intermediate bases) from
    /// <c>Microsoft.AspNetCore.Components.ComponentBase</c>.
    /// </summary>
    public static bool IsComponentType(INamedTypeSymbol type)
    {
        for (var current = type.BaseType; current is not null; current = current.BaseType)
        {
            if (current.OriginalDefinition.ToDisplayString() == ComponentBaseFullName)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Runs the Razor phase over one project's compilation. Self-no-ops when the
    /// compilation contains no Razor-generated trees.
    /// </summary>
    public static void Run(Compilation compilation, ExtractionContext ctx, Action<string>? log = null)
    {
        foreach (var tree in compilation.SyntaxTrees)
        {
            if (!RazorGeneratedDocs.TryGetRazorPath(tree, out _))
            {
                continue;
            }

            ProcessGeneratedTree(tree, compilation, ctx, log);
        }
    }

    private static void ProcessGeneratedTree(
        SyntaxTree tree,
        Compilation compilation,
        ExtractionContext ctx,
        Action<string>? log)
    {
        var semanticModel = compilation.GetSemanticModel(tree);
        var root = tree.GetRoot();

        // A generated component tree declares exactly one component class (plus,
        // optionally, the __Blazor TypeInference helper). Zero components is normal for
        // _Imports_razor.g.cs; anything else is unexpected → skip rather than guess.
        var components = root.DescendantNodes()
            .OfType<ClassDeclarationSyntax>()
            .Select(c => semanticModel.GetDeclaredSymbol(c))
            .OfType<INamedTypeSymbol>()
            .Where(IsComponentType)
            .Distinct(SymbolEqualityComparer.Default)
            .Cast<INamedTypeSymbol>()
            .ToList();

        if (components.Count == 0)
        {
            return; // e.g. _Imports_razor.g.cs — declares no component.
        }

        if (components.Count > 1)
        {
            log?.Invoke(
                $"Razor tree '{tree.FilePath}' declares {components.Count} component " +
                "classes; expected one — skipping its RENDERS/handler edges.");
            return;
        }

        var componentId = SymbolIdFormat.GetId(components[0].OriginalDefinition);
        var razorDoc = ctx.GetDocForTree(tree);

        foreach (var invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            if (semanticModel.GetSymbolInfo(invocation).Symbol is not IMethodSymbol method)
            {
                continue;
            }

            if (IsOpenComponent(method))
            {
                AddRendersEdge(componentId, method, razorDoc, ctx);
            }
            else if (IsEventCallbackFactory(method))
            {
                AddHandlerEdges(invocation, semanticModel, razorDoc, ctx);
            }
        }
    }

    private static bool IsOpenComponent(IMethodSymbol method) =>
        method is { Name: "OpenComponent", IsGenericMethod: true, TypeArguments.Length: 1 }
        && method.ContainingType?.OriginalDefinition.ToDisplayString() == RenderTreeBuilderFullName;

    private static bool IsEventCallbackFactory(IMethodSymbol method)
    {
        var containingType = method.ContainingType?.OriginalDefinition.ToDisplayString();
        return (containingType == EventCallbackFactoryFullName
                && method.Name.StartsWith("Create", StringComparison.Ordinal))
            || (containingType == RuntimeHelpersFullName
                && method.Name == "CreateInferredEventCallback");
    }

    private static void AddRendersEdge(
        string componentId,
        IMethodSymbol openComponent,
        string? razorDoc,
        ExtractionContext ctx)
    {
        if (openComponent.TypeArguments[0].OriginalDefinition is not INamedTypeSymbol child)
        {
            return;
        }

        if (!ctx.IsAuthoredSymbol(child))
        {
            return; // framework/library component (MudBlazor, PageTitle, …): no edge.
        }

        ctx.AddEdge(componentId, SymbolIdFormat.GetId(child), EdgeTypes.Renders, razorDoc);
    }

    private static void AddHandlerEdges(
        InvocationExpressionSyntax factoryCall,
        SemanticModel semanticModel,
        string? razorDoc,
        ExtractionContext ctx)
    {
        foreach (var argument in factoryCall.ArgumentList.Arguments)
        {
            // A method-group argument is a bare identifier or member access — never an
            // invocation, lambda, or `this` (the receiver argument).
            if (argument.Expression is not (IdentifierNameSyntax or MemberAccessExpressionSyntax))
            {
                continue;
            }

            if (semanticModel.GetSymbolInfo(argument.Expression).Symbol is not IMethodSymbol handler)
            {
                continue;
            }

            var target = (IMethodSymbol)(handler.ReducedFrom ?? handler).OriginalDefinition;
            if (!ctx.IsAuthoredSymbol(target))
            {
                continue;
            }

            if (GetEnclosingEmittedMethod(factoryCall, semanticModel) is not { } enclosing)
            {
                continue;
            }

            ctx.AddEdge(
                SymbolIdFormat.GetId(enclosing),
                SymbolIdFormat.GetId(target),
                EdgeTypes.Calls,
                razorDoc);
        }
    }

    /// <summary>
    /// The emitted method a generated expression belongs to: the enclosing symbol with
    /// anonymous functions and local functions unwound to their containing method —
    /// matching how the ordinary CALLS walk attributes lambda-body invocations.
    /// </summary>
    private static IMethodSymbol? GetEnclosingEmittedMethod(
        SyntaxNode node,
        SemanticModel semanticModel)
    {
        var symbol = semanticModel.GetEnclosingSymbol(node.SpanStart);
        while (symbol is IMethodSymbol { MethodKind: MethodKind.AnonymousFunction or MethodKind.LocalFunction } nested)
        {
            symbol = nested.ContainingSymbol;
        }

        return (symbol as IMethodSymbol)?.OriginalDefinition as IMethodSymbol;
    }
}
