// edgehop-extract — the EdgeHop indexer host (app-shell).
//
// Verb dispatch and the load->extract->reconcile orchestration only. Source extractors
// (Roslyn C#/Razor, oxc JS/TS) and the graph store are BOTH reflection-loaded via
// EdgeHop.Core's ExtractorFactory / GraphStoreFactory, so this host references neither
// MSBuild nor any store driver. The README "MSBuild gotcha" is therefore contained inside the
// Roslyn extractor plugin, which registers the MSBuild locator at its ExtractAsync entry —
// nothing on the host path resolves an MSBuildWorkspace type.
//
// Exit codes: 0 success, 1 usage/configuration error, 2 workspace Failure diagnostics.

using EdgeHop.Roslyn;

internal static class Program
{
    private static Task<int> Main(string[] args) => ExtractorApp.RunAsync(args);
}
