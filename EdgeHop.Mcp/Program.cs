// EdgeHop.Mcp — minimal MCP server over stdio (Phase 3).
//
// Thin standalone entry point. The server body lives in McpApp.RunAsync so the unified
// `edgehop mcp` dispatcher command (EdgeHop.Tool) runs the identical server. STDIO hygiene
// and all wiring rationale are documented on McpApp.

using EdgeHop.Mcp;

return await McpApp.RunAsync(args);
