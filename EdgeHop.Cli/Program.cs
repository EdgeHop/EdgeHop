// edgehop — local CLI front end over the EdgeHop query backend.
//
// The MCP server (EdgeHop.Mcp) and this CLI are thin heads over the SAME backend,
// EdgeHop.Core.EdgeHopQueryService: identical search semantics, validation, limit
// clamping and truncation behavior. This head only parses arguments, reads the NEO4J_*
// environment variables, and renders results.
//
// Output discipline: results go to STDOUT; usage text for errors, and all error messages,
// go to STDERR. With --json, stdout carries EXACTLY one JSON document (the same wire shape
// the MCP tools return) and nothing else, so output can be piped into other tools.
// Exit codes: 0 success (including 0 matches); 1 usage, configuration or validation error.

using EdgeHop.Cli;

return await CliApp.RunAsync(args);
