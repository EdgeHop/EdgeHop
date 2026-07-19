using EdgeHopExplorer.BlazorServer.Domain;

namespace EdgeHopExplorer.BlazorServer.Endpoints;

/// <summary>
/// Registers the read-only feature API. All three routes are composed under a single
/// <c>MapGroup("/api/features")</c>, so this ONE registration method carries three verb-prefixed
/// route templates (its <c>routes</c> property once indexed) and is the queryable anchor the
/// HTTP_CALLS edges from <see cref="Services.FeatureApiClient"/> resolve to — a minimal-API lambda
/// handler has no symbol node of its own.
/// </summary>
public static class FeatureEndpoints
{
    public static IEndpointRouteBuilder MapFeatureEndpoints(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api/features");

        // GET /api/features/all
        api.MapGet("/all", (IFeatureCatalog catalog) =>
            catalog.All.Select(feature => new FeatureInfo(feature.Name, feature.Area.ToString())));

        // GET /api/features/{name} — the interpolation hole in the caller's URI matches {name}.
        api.MapGet("/{name}", (string name, IFeatureCatalog catalog) =>
            catalog.Find(name) is { } found
                ? Results.Ok(new FeatureInfo(found.Name, found.Area.ToString()))
                : Results.NotFound());

        // POST /api/features/search
        api.MapPost("/search", (SearchRequest request, IFeatureCatalog catalog) =>
            catalog.All.Where(feature =>
                feature.Name.Contains(request.Term, StringComparison.OrdinalIgnoreCase)));

        return app;
    }
}
