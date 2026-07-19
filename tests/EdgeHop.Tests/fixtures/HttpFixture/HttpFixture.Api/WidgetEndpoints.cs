using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;

namespace HttpFixture.Api;

// Minimal-API registrations. Lambda handlers have no symbol nodes, so HTTP_CALLS edges
// land on MapWidgetEndpoints itself and its `routes` property lists every template
// (verb-prefixed, declaration order) — see EXPECTED-GRAPH.md.
public static class WidgetEndpoints
{
    public static void MapWidgetEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/widgets", () => Store.All());

        // Route constraint: the caller's interpolated `$"/widget/{id}"` must match this
        // template's {id:int} parameter segment.
        app.MapGet("/widget/{id:int}", (int id) => Store.Get(id));

        app.MapPost("/widget", (string name) => Store.Add(name));

        // Group prefix composition through a local: DELETE /admin/widget/{id}.
        var admin = app.MapGroup("/admin");
        admin.MapDelete("/widget/{id}", (int id) => Store.Remove(id));
    }
}
