namespace EdgeHop.Core;

/// <summary>
/// The input to one <see cref="IExtractor.ExtractAsync"/> call: which solution/repo to index,
/// the branch to stamp, and an optional watch hint. Backend-neutral and language-neutral so
/// every extractor (Roslyn C#/Razor, oxc JS/TS, …) consumes the same request.
/// </summary>
/// <param name="SolutionPath">The <c>.sln</c> being indexed, or <c>null</c> when the index
/// target is a bare source directory (a non-.NET tree — e.g. a pure JS/TS project — with no
/// solution file). An extractor that needs a solution (Roslyn) contributes nothing when this is
/// null; an extractor that works off the repo tree (oxc) always derives its root from
/// <paramref name="SolutionDirectory"/> and is unaffected.</param>
/// <param name="Branch">Branch value stamped into every emitted row.</param>
/// <param name="SolutionDirectory">The root directory extraction walks: the solution's directory
/// in solution mode, or the directory target itself in directory mode. Always set by the indexer
/// host; only null in the degenerate case of a solution path with no directory component.</param>
/// <param name="ChangedPaths">Watch hint: the full paths a debounced batch touched. <c>null</c>
/// means "full extraction" (one-shot runs, the initial watch cycle, branch switches, watcher
/// overflow). A non-null list lets an extractor try an incremental in-memory refresh, falling
/// back to a full extraction when any path is unknown/created/deleted. Extraction stays
/// whole-solution either way — this only optimizes the LOAD.</param>
public sealed record ExtractionRequest(
    string? SolutionPath,
    string Branch,
    string? SolutionDirectory,
    IReadOnlyList<string>? ChangedPaths = null);
