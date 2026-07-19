namespace EdgeHop.Core;

/// <summary>
/// One pluggable source extractor — Roslyn (C#/Razor), oxc (JS/TS), … — discovered
/// reflectively by <see cref="ExtractorFactory"/> from the assembly
/// <c>EdgeHop.&lt;Name&gt;</c> and run by the indexer host. Each extractor turns an
/// <see cref="ExtractionRequest"/> into an <see cref="ExtractionOutcome"/>; the host merges
/// every loaded extractor's rows into one set and reconciles it once per branch.
/// <para>
/// STATEFUL BY DESIGN (<see cref="IAsyncDisposable"/>): a watch loop keeps one instance alive
/// across cycles so an extractor can hold its loaded workspace/session and honor
/// <see cref="ExtractionRequest.ChangedPaths"/> with an in-memory refresh instead of a full
/// reload. One-shot runs create, use, and dispose an instance immediately.
/// </para>
/// </summary>
public interface IExtractor : IAsyncDisposable
{
    /// <summary>The extractor's short name (e.g. <c>"roslyn"</c>, <c>"oxc"</c>), matching the
    /// <c>EDGEHOP_BACKEND</c>-style selector and its <c>EdgeHop.&lt;Name&gt;</c> assembly.</summary>
    string Name { get; }

    /// <summary>
    /// Extracts nodes/edges for <paramref name="request"/>. Must NOT throw for a source it
    /// simply has nothing to say about (return an empty <see cref="ExtractionResult"/>); real
    /// load failures travel back as <see cref="ExtractionOutcome.FailureDiagnostics"/> so the
    /// host can hard-stop uniformly. May keep internal state (a loaded workspace) alive for the
    /// next call.
    /// </summary>
    Task<ExtractionOutcome> ExtractAsync(
        ExtractionRequest request, Action<string>? log = null, CancellationToken ct = default);
}
