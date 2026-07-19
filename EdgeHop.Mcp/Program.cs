// EdgeHop.Mcp — minimal MCP server over stdio (Phase 3).
//
// STDIO HYGIENE (critical): stdout is the JSON-RPC channel. Nothing in this process may
// write to stdout except the MCP stdio transport itself — no Console.WriteLine anywhere,
// and all logging is rerouted to stderr below. A single stray stdout byte corrupts the
// protocol stream and breaks the Claude Code connection.

using EdgeHop.Core;
using EdgeHop.Mcp;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

// Fail fast when the backend's environment variables are missing: write the settings
// error to stderr (it names exactly which variables to set, never any secret value) and
// exit 1 without a stack trace. The backend is resolved ONCE at startup (branch stays
// per-call); store creation never opens a connection.
IGraphStore store;
try
{
    store = GraphStoreFactory.FromEnvironment();
}
catch (InvalidOperationException ex)
{
    await Console.Error.WriteLineAsync(ex.Message).ConfigureAwait(false);
    return 1;
}

// Stderr only — stdout is the JSON-RPC channel and must stay protocol-clean.
await Console.Error.WriteLineAsync($"edgehop: backend {store.Description}").ConfigureAwait(false);

var builder = Host.CreateApplicationBuilder(args);

// Host.CreateApplicationBuilder wires a default console logger that writes to STDOUT,
// which would corrupt JSON-RPC. Replace it with a console logger whose every level is
// redirected to stderr (LogToStandardErrorThreshold = Trace ⇒ nothing ever hits stdout).
builder.Logging.ClearProviders();
builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);

// The store is a process-wide singleton created once (its connection object is
// thread-safe; per-call state such as Neo4j sessions is opened per query internally).
// The DI container owns it and disposes it gracefully (IAsyncDisposable) when the host
// shuts down — i.e. when the client closes stdin and the stdio session ends.
// Factory registration (not AddSingleton(instance)) so the container takes ownership
// and disposes the store on shutdown — instance registrations are never disposed by DI.
builder.Services.AddSingleton<IGraphStore>(_ => store);

// The shared, front-end-neutral query backend (also used by the edgehop CLI). The MCP
// tool layer is a pure adapter over this service — no query logic lives in EdgeHop.Mcp.
builder.Services.AddSingleton(sp =>
    new EdgeHopQueryService(sp.GetRequiredService<IGraphStore>().Reader));

builder.Services
    .AddMcpServer(options => options.ServerInfo = new()
    {
        Name = "edgehop",
        Title = "EdgeHop",
        Version = "0.1.0",
    })
    .WithStdioServerTransport()
    // Exposes EXACTLY the five tools on EdgeHopTools (find_symbol, get_callers,
    // get_relationships, get_path, graph_stats) — auto-registered from the [McpServerTool]
    // methods on the one type. The original "two tools" cap was superseded by owner
    // direction once the read loop was validated (see README). Do not register
    // additional tool types.
    .WithTools<EdgeHopTools>();

await builder.Build().RunAsync().ConfigureAwait(false);
return 0;
