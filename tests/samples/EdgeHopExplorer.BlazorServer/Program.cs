using EdgeHopExplorer.BlazorServer.Components;
using EdgeHopExplorer.BlazorServer.Domain;
using EdgeHopExplorer.BlazorServer.Endpoints;
using EdgeHopExplorer.BlazorServer.Services;

// Entry point for the runnable EdgeHop Explorer (Blazor Server). Run it with:
//   dotnet run --project tests/samples/EdgeHopExplorer.BlazorServer
// then browse to the printed URL for a self-guided tour of what EdgeHop indexes.

var builder = WebApplication.CreateBuilder(args);

// Interactive server components so the @onclick handlers and the JS-interop handshake run live.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// The in-memory feature catalog (the domain the whole app is built around) ...
builder.Services.AddSingleton<IFeatureCatalog, FeatureCatalog>();

// ... and the typed HttpClient the UI uses to call THIS app's own minimal API. That round trip is
// what makes the cross-tier HTTP_CALLS edges real: FeatureApiClient's verb-named calls match the
// endpoints registered in FeatureEndpoints.MapFeatureEndpoints by verb + route-template shape.
builder.Services.AddHttpClient<FeatureApiClient>(client =>
    client.BaseAddress = new Uri(builder.Configuration["ApiBaseAddress"] ?? "http://localhost:5080"));

var app = builder.Build();

app.UseAntiforgery();

// Serve static web assets — wwwroot files AND the framework's _framework/blazor.web.js — through
// the .NET 9+ endpoint-based static-assets pipeline. Plain app.UseStaticFiles() does NOT serve
// _framework/blazor.web.js on .NET 10, so Blazor interactivity (and the JS-interop module import)
// would 404. MapStaticAssets serves each asset at its plain route (js/explorer.js resolves too).
//
// Run this sample with `dotnet run`, which reads Properties/launchSettings.json and starts it in the
// Development environment. That environment matters: MapStaticAssets consumes the build-time asset
// manifest via a dev-time handler that serves framework assets (blazor.web.js) out of the shared
// framework. Running the *un-published* build output in Production instead makes that handler look
// for _framework/blazor.web.js under wwwroot — where it does not exist — and throw a 500
// (FileNotFoundException). `dotnet publish` bakes a self-contained manifest that needs no such
// handler; only the un-published run is environment-sensitive.
app.MapStaticAssets();

// Minimal-API registration lives in its own method so that method is the stable, queryable anchor
// the HTTP_CALLS edges point at (a minimal-API lambda handler has no symbol of its own).
app.MapFeatureEndpoints();

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
