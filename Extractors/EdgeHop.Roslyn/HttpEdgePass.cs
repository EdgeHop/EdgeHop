using EdgeHop.Core;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace EdgeHop.Roslyn;

/// <summary>
/// Cross-tier HTTP phase: derives HTTP_CALLS edges from a client method's
/// <c>HttpClient</c> call to the authored method that registers the matching endpoint —
/// the compile-time-invisible Web→ApiService boundary (README "Cross-tier edges").
/// </summary>
/// <remarks>
/// Two-phase by necessity: callers and endpoints live in DIFFERENT projects, so
/// <see cref="Collect"/> runs once per compilation inside the extractor's project loop
/// and <see cref="Emit"/> matches and emits only after every project contributed.
/// <list type="bullet">
/// <item><description>
/// <b>Endpoint side</b> — two registration styles, one record shape:
/// minimal-API <c>Map{Get,Post,Put,Delete,Patch}</c> invocations (route = the constant
/// <c>pattern</c> argument, composed with <c>MapGroup</c> prefixes reachable through the
/// receiver chain or a same-method group local), attributed to the enclosing emitted
/// method (lambda handlers have no node — the registration method is the stable,
/// queryable anchor); and attribute-routed controller actions
/// (<c>[HttpGet("…")]</c> composed with the class-level <c>[Route]</c>,
/// <c>[controller]</c>/<c>[action]</c> tokens substituted), attributed to the action
/// method itself. Non-constant route patterns are skipped with a log line — an
/// unresolvable route is not a registered route.
/// </description></item>
/// <item><description>
/// <b>Caller side</b> — invocations resolving to <c>System.Net.Http.HttpClient</c> or
/// <c>System.Net.Http.Json.HttpClientJsonExtensions</c> methods whose name starts with
/// Get/Post/Put/Delete/Patch (Send* is skipped: no verb without inspecting the request
/// object). The <c>requestUri</c> argument becomes a route template: string constants
/// verbatim, interpolation holes as single-segment wildcards, a trailing non-constant
/// concat operand (<c>"/courses" + query</c>) as an assumed query-string suffix, and
/// everything from the first <c>?</c> stripped.
/// </description></item>
/// <item><description>
/// <b>Matching</b> — verb equality plus template shape: literal segments compare
/// case-insensitively, a parameter segment (<c>{id:int}</c>, an interpolation hole)
/// matches any single segment, and a catch-all (<c>{**path}</c>) matches the rest.
/// Every match emits one edge — a caller whose template matches two registrations
/// genuinely depends on both.
/// </description></item>
/// <item><description>
/// <b>Routes stamping</b> — each registration method node gets its templates
/// (<c>"GET /league/{id}"</c>, declaration order) on the existing <c>routes</c>
/// property, so <c>find_symbol</c> hits show what an endpoint method serves the same
/// way component nodes show their <c>@page</c> routes.
/// </description></item>
/// </list>
/// </remarks>
internal sealed class HttpEdgePass
{
    private const string HttpClientFullName = "System.Net.Http.HttpClient";
    private const string HttpClientJsonExtensionsFullName = "System.Net.Http.Json.HttpClientJsonExtensions";
    private const string ControllerBaseFullName = "Microsoft.AspNetCore.Mvc.ControllerBase";
    private const string AspNetCoreNamespacePrefix = "Microsoft.AspNetCore";
    private const int MaxGroupDepth = 8;

    /// <summary>Marks an interpolation hole inside a caller-template string; never a
    /// legal route character, so it survives the query-string cut and segment split.</summary>
    private const char Hole = '\u0001';

    private static readonly Dictionary<string, string> MapVerbs = new(StringComparer.Ordinal)
    {
        ["MapGet"] = "GET",
        ["MapPost"] = "POST",
        ["MapPut"] = "PUT",
        ["MapDelete"] = "DELETE",
        ["MapPatch"] = "PATCH",
    };

    private static readonly Dictionary<string, string> HttpMethodAttributeVerbs = new(StringComparer.Ordinal)
    {
        ["Microsoft.AspNetCore.Mvc.HttpGetAttribute"] = "GET",
        ["Microsoft.AspNetCore.Mvc.HttpPostAttribute"] = "POST",
        ["Microsoft.AspNetCore.Mvc.HttpPutAttribute"] = "PUT",
        ["Microsoft.AspNetCore.Mvc.HttpDeleteAttribute"] = "DELETE",
        ["Microsoft.AspNetCore.Mvc.HttpPatchAttribute"] = "PATCH",
    };

    private readonly List<Registration> _registrations = new();
    private readonly HashSet<string> _registrationKeys = new(StringComparer.Ordinal);
    private readonly List<CallSite> _callSites = new();
    private readonly HashSet<string> _callSiteKeys = new(StringComparer.Ordinal);

    /// <summary>One registered route: the emitted method that registers/serves it, the
    /// HTTP verb, the display template and its matchable shape.</summary>
    private sealed record Registration(string MethodId, string Verb, string Template, RouteShape Shape);

    /// <summary>One HttpClient call site: the emitted caller method, the verb implied by
    /// the client method name, the matchable shape and the invocation's document.</summary>
    private sealed record CallSite(string CallerId, string Verb, RouteShape Shape, string? SourceDoc);

    /// <summary>
    /// Collects endpoint registrations and HttpClient call sites from one project's
    /// compilation. Self-no-ops for projects that reference neither ASP.NET Core routing
    /// nor HttpClient.
    /// </summary>
    public void Collect(Compilation compilation, ExtractionContext ctx, Action<string>? log = null)
    {
        foreach (var tree in compilation.SyntaxTrees)
        {
            if (!ctx.IsAuthoredTree(tree))
            {
                continue;
            }

            SemanticModel? semanticModel = null;
            foreach (var invocation in tree.GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                semanticModel ??= compilation.GetSemanticModel(tree);
                if (semanticModel.GetSymbolInfo(invocation).Symbol is not IMethodSymbol callee)
                {
                    continue;
                }

                var definition = (IMethodSymbol)(callee.ReducedFrom ?? callee).OriginalDefinition;
                if (MapVerbs.TryGetValue(definition.Name, out var mapVerb)
                    && IsAspNetCoreSymbol(definition))
                {
                    CollectMinimalApiRegistration(invocation, callee, mapVerb, semanticModel, ctx, log);
                }
                else if (TryGetClientVerb(definition, out var clientVerb))
                {
                    CollectCallSite(invocation, callee, clientVerb, semanticModel, ctx);
                }
            }
        }

        CollectControllers(compilation.Assembly.GlobalNamespace, ctx);
    }

    /// <summary>
    /// Matches every collected call site against every collected registration and emits
    /// HTTP_CALLS edges, then stamps each registration method's templates onto its node's
    /// <c>routes</c> property. Must run after ALL projects were collected.
    /// </summary>
    public void Emit(ExtractionContext ctx, Action<string>? log = null)
    {
        var edges = 0;
        foreach (var site in _callSites)
        {
            foreach (var registration in _registrations)
            {
                if (string.Equals(site.Verb, registration.Verb, StringComparison.Ordinal)
                    && RouteShape.Matches(site.Shape, registration.Shape))
                {
                    ctx.AddEdge(site.CallerId, registration.MethodId, EdgeTypes.HttpCalls, site.SourceDoc);
                    edges++;
                }
            }
        }

        foreach (var group in _registrations.GroupBy(r => r.MethodId, StringComparer.Ordinal))
        {
            ctx.SetRoutes(group.Key, group.Select(r => $"{r.Verb} {r.Template}").ToList());
        }

        if (_registrations.Count > 0 || _callSites.Count > 0)
        {
            log?.Invoke(
                $"HTTP pass: {_registrations.Count} registered route(s), " +
                $"{_callSites.Count} client call site(s), {edges} HTTP_CALLS match(es).");
        }
    }

    private void CollectMinimalApiRegistration(
        InvocationExpressionSyntax invocation,
        IMethodSymbol callee,
        string verb,
        SemanticModel semanticModel,
        ExtractionContext ctx,
        Action<string>? log)
    {
        if (GetEnclosingEmittedMethodId(invocation, semanticModel, ctx) is not { } methodId)
        {
            return;
        }

        if (FindArgument(invocation, callee, "pattern") is not { } patternExpression
            || semanticModel.GetConstantValue(patternExpression).Value is not string pattern)
        {
            log?.Invoke(
                $"HTTP pass: non-constant route pattern in '{invocation}' " +
                $"({ctx.GetDocForTree(invocation.SyntaxTree)}); endpoint skipped.");
            return;
        }

        var prefix = GetGroupPrefix(invocation, semanticModel, MaxGroupDepth);
        AddRegistration(methodId, verb, CombineTemplates(prefix, pattern));
    }

    /// <summary>
    /// The composed <c>MapGroup</c> prefix reachable from a <c>Map*</c>/<c>MapGroup</c>
    /// invocation's receiver: direct chaining (<c>app.MapGroup("/api").MapGet(…)</c>) and
    /// group locals initialized in the same method (<c>var g = app.MapGroup("/api");
    /// g.MapGet(…)</c>). Anything else (fields, parameters, cross-method flow) yields no
    /// prefix — a best-effort miss produces an unprefixed route, never a wrong edge to an
    /// unrelated endpoint (matching simply fails).
    /// </summary>
    private static string GetGroupPrefix(
        InvocationExpressionSyntax invocation,
        SemanticModel semanticModel,
        int depth)
    {
        if (depth <= 0 || invocation.Expression is not MemberAccessExpressionSyntax memberAccess)
        {
            return string.Empty;
        }

        var receiver = memberAccess.Expression;
        if (receiver is IdentifierNameSyntax identifier
            && semanticModel.GetSymbolInfo(identifier).Symbol is ILocalSymbol local
            && local.DeclaringSyntaxReferences.Length == 1
            && local.DeclaringSyntaxReferences[0].GetSyntax() is VariableDeclaratorSyntax
            {
                Initializer.Value: InvocationExpressionSyntax initializer
            }
            && initializer.SyntaxTree == invocation.SyntaxTree)
        {
            receiver = initializer;
        }

        if (receiver is not InvocationExpressionSyntax receiverInvocation
            || semanticModel.GetSymbolInfo(receiverInvocation).Symbol is not IMethodSymbol receiverMethod)
        {
            return string.Empty;
        }

        var definition = (IMethodSymbol)(receiverMethod.ReducedFrom ?? receiverMethod).OriginalDefinition;
        if (definition.Name != "MapGroup" || !IsAspNetCoreSymbol(definition))
        {
            return string.Empty;
        }

        var own = FindArgument(receiverInvocation, receiverMethod, "prefix") is { } prefixExpression
                  && semanticModel.GetConstantValue(prefixExpression).Value is string prefix
            ? prefix
            : string.Empty;

        return CombineTemplates(GetGroupPrefix(receiverInvocation, semanticModel, depth - 1), own);
    }

    private void CollectCallSite(
        InvocationExpressionSyntax invocation,
        IMethodSymbol callee,
        string verb,
        SemanticModel semanticModel,
        ExtractionContext ctx)
    {
        if (GetEnclosingEmittedMethodId(invocation, semanticModel, ctx) is not { } callerId)
        {
            return;
        }

        if (FindArgument(invocation, callee, "requestUri") is not { } routeExpression
            || BuildCallerTemplate(routeExpression, semanticModel) is not { } template
            || RouteShape.FromCallerTemplate(template) is not { } shape)
        {
            return; // route not statically knowable: no edge is honest, a guess is not.
        }

        if (_callSiteKeys.Add($"{callerId}\n{verb}\n{shape.Key}"))
        {
            _callSites.Add(new CallSite(
                callerId, verb, shape, ctx.GetDocForTree(invocation.SyntaxTree)));
        }
    }

    /// <summary>
    /// The caller's route expression as a template string: constants verbatim,
    /// interpolation holes as <see cref="Hole"/> markers, a non-constant trailing concat
    /// operand as an assumed query-string suffix (dropped — the
    /// <c>"/courses" + query</c> shape). Null when no leading literal part is knowable.
    /// </summary>
    private static string? BuildCallerTemplate(ExpressionSyntax expression, SemanticModel semanticModel)
    {
        if (semanticModel.GetConstantValue(expression).Value is string constant)
        {
            return constant;
        }

        switch (expression)
        {
            case InterpolatedStringExpressionSyntax interpolated:
            {
                var builder = new System.Text.StringBuilder();
                foreach (var content in interpolated.Contents)
                {
                    switch (content)
                    {
                        case InterpolatedStringTextSyntax text:
                            builder.Append(text.TextToken.ValueText);
                            break;

                        case InterpolationSyntax interpolation:
                            if (semanticModel.GetConstantValue(interpolation.Expression).Value is { } value)
                            {
                                builder.Append(value);
                            }
                            else
                            {
                                builder.Append(Hole);
                            }

                            break;
                    }
                }

                return builder.ToString();
            }

            case BinaryExpressionSyntax binary when binary.IsKind(SyntaxKind.AddExpression):
            {
                if (BuildCallerTemplate(binary.Left, semanticModel) is not { } left)
                {
                    return null; // unknowable prefix: the path itself is unknown.
                }

                // A knowable right side concatenates; an unknowable one is assumed to be
                // a query-string suffix ("/courses" + query) and contributes nothing.
                return BuildCallerTemplate(binary.Right, semanticModel) is { } right
                    ? left + right
                    : left;
            }

            default:
                return null;
        }
    }

    private void CollectControllers(INamespaceSymbol ns, ExtractionContext ctx)
    {
        foreach (var child in ns.GetNamespaceMembers())
        {
            CollectControllers(child, ctx);
        }

        foreach (var type in ns.GetTypeMembers())
        {
            CollectControllerType(type, ctx);
        }
    }

    private void CollectControllerType(INamedTypeSymbol type, ExtractionContext ctx)
    {
        foreach (var nested in type.GetTypeMembers())
        {
            CollectControllerType(nested, ctx);
        }

        if (type.IsAbstract || !InheritsControllerBase(type) || !ctx.IsAuthoredSymbol(type))
        {
            return;
        }

        var classTemplates = GetRouteAttributeTemplates(type);
        if (classTemplates.Count == 0)
        {
            classTemplates = [string.Empty];
        }

        foreach (var member in type.GetMembers().OfType<IMethodSymbol>())
        {
            if (member.MethodKind != MethodKind.Ordinary || !ctx.IsAuthoredSymbol(member))
            {
                continue;
            }

            foreach (var attribute in member.GetAttributes())
            {
                var attributeName = attribute.AttributeClass?.OriginalDefinition.ToDisplayString();
                if (attributeName is null
                    || !HttpMethodAttributeVerbs.TryGetValue(attributeName, out var verb))
                {
                    continue;
                }

                var actionTemplate = attribute.ConstructorArguments.Length > 0
                                     && attribute.ConstructorArguments[0].Value is string t
                    ? t
                    : string.Empty;

                var methodId = SymbolIdFormat.GetId(member.OriginalDefinition);
                foreach (var classTemplate in classTemplates)
                {
                    var combined = SubstituteRouteTokens(
                        CombineTemplates(classTemplate, actionTemplate), type, member);
                    AddRegistration(methodId, verb, combined);
                }
            }
        }
    }

    private void AddRegistration(string methodId, string verb, string template)
    {
        var normalized = NormalizeTemplate(template);
        if (_registrationKeys.Add($"{methodId}\n{verb}\n{normalized}"))
        {
            _registrations.Add(new Registration(
                methodId, verb, normalized, RouteShape.FromEndpointTemplate(normalized)));
        }
    }

    private static bool InheritsControllerBase(INamedTypeSymbol type)
    {
        for (var current = type.BaseType; current is not null; current = current.BaseType)
        {
            if (current.OriginalDefinition.ToDisplayString() == ControllerBaseFullName)
            {
                return true;
            }
        }

        return false;
    }

    private static List<string> GetRouteAttributeTemplates(INamedTypeSymbol type)
    {
        var templates = new List<string>();
        foreach (var attribute in type.GetAttributes())
        {
            if (attribute.AttributeClass?.OriginalDefinition.ToDisplayString()
                    == "Microsoft.AspNetCore.Mvc.RouteAttribute"
                && attribute.ConstructorArguments.Length > 0
                && attribute.ConstructorArguments[0].Value is string template)
            {
                templates.Add(template);
            }
        }

        return templates;
    }

    /// <summary>MVC route-token substitution: <c>[controller]</c> → class name minus the
    /// <c>Controller</c> suffix, <c>[action]</c> → method name (both case-insensitive,
    /// matching the framework's replacement rules for the tokens EdgeHop supports).</summary>
    private static string SubstituteRouteTokens(string template, INamedTypeSymbol type, IMethodSymbol action)
    {
        var controller = type.Name.EndsWith("Controller", StringComparison.Ordinal)
            ? type.Name[..^"Controller".Length]
            : type.Name;
        return template
            .Replace("[controller]", controller, StringComparison.OrdinalIgnoreCase)
            .Replace("[action]", action.Name, StringComparison.OrdinalIgnoreCase);
    }

    private static string CombineTemplates(string prefix, string suffix)
    {
        var left = prefix.Trim('/');
        var right = suffix.Trim('/');
        return left.Length == 0 ? "/" + right
            : right.Length == 0 ? "/" + left
            : $"/{left}/{right}";
    }

    private static string NormalizeTemplate(string template) => CombineTemplates(template, string.Empty);

    private static bool IsAspNetCoreSymbol(IMethodSymbol method) =>
        method.ContainingNamespace?.ToDisplayString()
            .StartsWith(AspNetCoreNamespacePrefix, StringComparison.Ordinal) == true;

    private static bool TryGetClientVerb(IMethodSymbol definition, out string verb)
    {
        verb = string.Empty;
        var containingType = definition.ContainingType?.OriginalDefinition.ToDisplayString();
        if (containingType is not (HttpClientFullName or HttpClientJsonExtensionsFullName))
        {
            return false;
        }

        verb = definition.Name switch
        {
            var n when n.StartsWith("Get", StringComparison.Ordinal) => "GET",
            var n when n.StartsWith("Post", StringComparison.Ordinal) => "POST",
            var n when n.StartsWith("Put", StringComparison.Ordinal) => "PUT",
            var n when n.StartsWith("Delete", StringComparison.Ordinal) => "DELETE",
            var n when n.StartsWith("Patch", StringComparison.Ordinal) => "PATCH",
            _ => string.Empty, // Send*/others: verb unknowable from the name.
        };
        return verb.Length > 0;
    }

    /// <summary>
    /// The argument bound to <paramref name="parameterName"/> of <paramref name="callee"/>
    /// in <paramref name="invocation"/> — named form first, else positional against the
    /// EXACT symbol form the invocation resolved to (reduced extension methods exclude
    /// the receiver, static calls include it, so positions always line up).
    /// </summary>
    private static ExpressionSyntax? FindArgument(
        InvocationExpressionSyntax invocation,
        IMethodSymbol callee,
        string parameterName)
    {
        var arguments = invocation.ArgumentList.Arguments;
        foreach (var argument in arguments)
        {
            if (argument.NameColon?.Name.Identifier.ValueText == parameterName)
            {
                return argument.Expression;
            }
        }

        for (var i = 0; i < callee.Parameters.Length && i < arguments.Count; i++)
        {
            if (callee.Parameters[i].Name == parameterName)
            {
                return arguments[i].NameColon is null ? arguments[i].Expression : null;
            }
        }

        return null;
    }

    /// <summary>
    /// The emitted method an invocation belongs to (anonymous/local functions unwound to
    /// their containing method, mirroring the CALLS attribution rule), as a stable node
    /// id — or null when the enclosing symbol is not an authored method.
    /// </summary>
    private static string? GetEnclosingEmittedMethodId(
        SyntaxNode node,
        SemanticModel semanticModel,
        ExtractionContext ctx)
    {
        var symbol = semanticModel.GetEnclosingSymbol(node.SpanStart);
        while (symbol is IMethodSymbol { MethodKind: MethodKind.AnonymousFunction or MethodKind.LocalFunction } nested)
        {
            symbol = nested.ContainingSymbol;
        }

        return symbol is IMethodSymbol method
               && method.OriginalDefinition is IMethodSymbol definition
               && ctx.IsAuthoredSymbol(definition)
            ? SymbolIdFormat.GetId(definition)
            : null;
    }

    /// <summary>
    /// A route template reduced to its matchable shape: an ordered list of segments that
    /// are either literals or single-segment wildcards (endpoint parameters like
    /// <c>{id:int}</c>, caller interpolation holes), plus an optional catch-all tail
    /// (<c>{**path}</c>) that matches every remaining segment.
    /// </summary>
    private sealed class RouteShape
    {
        /// <summary>Literal text per segment; null marks a single-segment wildcard.</summary>
        private readonly string?[] _segments;
        private readonly bool _catchAllTail;

        private RouteShape(string?[] segments, bool catchAllTail)
        {
            _segments = segments;
            _catchAllTail = catchAllTail;
        }

        /// <summary>Stable identity for call-site dedupe.</summary>
        public string Key =>
            string.Join('/', _segments.Select(s => s ?? "\u0001")) + (_catchAllTail ? "/**" : "");

        /// <summary>Shape of an endpoint template: <c>{…}</c> segments are wildcards
        /// (constraints need no stripping — the whole segment matches anything),
        /// <c>{**…}</c> is a catch-all tail.</summary>
        public static RouteShape FromEndpointTemplate(string template)
        {
            var raw = template.Trim('/');
            if (raw.Length == 0)
            {
                return new RouteShape([], catchAllTail: false);
            }

            var parts = raw.Split('/');
            var segments = new List<string?>(parts.Length);
            var catchAll = false;
            foreach (var part in parts)
            {
                // {*slug} and {**slug} are both catch-alls in ASP.NET Core routing.
                if (part.StartsWith("{*", StringComparison.Ordinal))
                {
                    catchAll = true;
                    break;
                }

                segments.Add(part.Contains('{', StringComparison.Ordinal) ? null : part);
            }

            return new RouteShape(segments.ToArray(), catchAll);
        }

        /// <summary>Shape of a caller template: everything from the first <c>?</c> is
        /// dropped, segments containing an interpolation hole are wildcards. Null for an
        /// empty path (nothing honest to match).</summary>
        public static RouteShape? FromCallerTemplate(string template)
        {
            var queryStart = template.IndexOf('?', StringComparison.Ordinal);
            var path = (queryStart < 0 ? template : template[..queryStart]).Trim('/');
            if (path.Length == 0)
            {
                return null;
            }

            var segments = path
                .Split('/')
                .Select(part => part.Contains(Hole) ? null : part)
                .ToArray();
            return new RouteShape(segments, catchAllTail: false);
        }

        /// <summary>Verbless template-shape match: equal segment counts (unless the
        /// endpoint ends in a catch-all), literals equal case-insensitively, a wildcard
        /// on either side matches any single segment.</summary>
        public static bool Matches(RouteShape caller, RouteShape endpoint)
        {
            if (endpoint._catchAllTail
                ? caller._segments.Length < endpoint._segments.Length
                : caller._segments.Length != endpoint._segments.Length)
            {
                return false;
            }

            for (var i = 0; i < endpoint._segments.Length; i++)
            {
                var callerSegment = caller._segments[i];
                var endpointSegment = endpoint._segments[i];
                if (callerSegment is not null
                    && endpointSegment is not null
                    && !string.Equals(callerSegment, endpointSegment, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }

            return true;
        }
    }
}
