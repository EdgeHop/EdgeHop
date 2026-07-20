// edgehop — the single unified command shipped as the `EdgeHop` .NET global tool.
//
// .NET tool packages allow exactly one command per package, so this thin dispatcher is the
// one `edgehop` command: it routes the first token to the existing entry points and passes
// the FULL argument vector through unchanged (each sub-app already dispatches on args[0], so
// the verb name is preserved end-to-end).
//
//   edgehop index|prune|branches|install-hooks|uninstall-hooks ...   -> ExtractorApp (indexer)
//   edgehop find-symbol|get-callers|get-relationships|get-path|stats -> CliApp (query CLI)
//   edgehop mcp                                                       -> McpApp (stdio server)
//
// The three sub-apps live in their own assemblies (referenced here) and keep their own
// standalone .exe entry points, which the test suite drives directly — this project adds the
// unified head without changing them.

using EdgeHop.Cli;
using EdgeHop.Mcp;
using EdgeHop.Roslyn;

// Verb -> which sub-app owns it. Kept explicit (rather than "try each") so an unknown verb
// gets a clear error instead of being silently swallowed by whichever app is tried first.
string[] indexVerbs = ["index", "prune", "branches", "install-hooks", "uninstall-hooks"];
string[] queryVerbs = ["find-symbol", "get-callers", "get-relationships", "get-path", "stats"];

var verb = args.Length > 0 ? args[0].ToLowerInvariant() : "";

if (Array.IndexOf(indexVerbs, verb) >= 0)
{
    // Indexer parses its own verb from args[0]; pass the whole vector through.
    return await ExtractorApp.RunAsync(args);
}

if (Array.IndexOf(queryVerbs, verb) >= 0)
{
    return await CliApp.RunAsync(args);
}

if (verb == "mcp")
{
    // The stdio server takes no verb of its own; hand it everything after "mcp".
    // Nothing here writes to stdout — the JSON-RPC channel stays protocol-clean.
    return await McpApp.RunAsync(args[1..]);
}

if (verb is "" or "help" or "-h" or "--help")
{
    PrintUsage(Console.Out);
    return 0;
}

if (verb is "--version")
{
    var informational = typeof(Program).Assembly
        .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
        is [System.Reflection.AssemblyInformationalVersionAttribute a, ..]
        ? a.InformationalVersion
        : typeof(Program).Assembly.GetName().Version?.ToString() ?? "unknown";
    Console.WriteLine(informational);
    return 0;
}

await Console.Error.WriteLineAsync($"edgehop: unknown command '{verb}'.");
PrintUsage(Console.Error);
return 1;

static void PrintUsage(TextWriter w)
{
    w.WriteLine("edgehop — code-graph indexer, query CLI, and MCP server.");
    w.WriteLine();
    w.WriteLine("Usage: edgehop <command> [options]");
    w.WriteLine();
    w.WriteLine("Indexing:");
    w.WriteLine("  index <sln|dir> [--branch b] [--dry-run] [--watch] [--no-worktree]");
    w.WriteLine("  prune --branch <b> --yes");
    w.WriteLine("  branches");
    w.WriteLine("  install-hooks <sln|dir> [--repo p]");
    w.WriteLine("  uninstall-hooks [--repo p]");
    w.WriteLine();
    w.WriteLine("Querying:");
    w.WriteLine("  find-symbol <query> [--kind k] [--limit n] [--branch b] [--json]");
    w.WriteLine("  get-callers <symbolId> [--depth 1-10] [--branch b] [--json]");
    w.WriteLine("  get-relationships <symbolId> [--direction out|in|both] [--edge-type T] [--depth 1-10] [--branch b] [--json]");
    w.WriteLine("  get-path <fromId> <toId> [--edge-type T] [--max-length 1-15] [--branch b] [--json]");
    w.WriteLine("  stats [--top n] [--branch b] [--json]");
    w.WriteLine();
    w.WriteLine("MCP server:");
    w.WriteLine("  mcp                         run the stdio MCP server (used by Claude Code)");
    w.WriteLine();
    w.WriteLine("  --version                   print the tool version");
}

// Present so typeof(Program) resolves the entry assembly for --version.
partial class Program { }
