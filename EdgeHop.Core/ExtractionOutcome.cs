namespace EdgeHop.Core;

/// <summary>
/// The result of one <see cref="IExtractor.ExtractAsync"/> call: the extracted
/// <see cref="ExtractionResult"/> plus the load diagnostics and timing the host reports and
/// gates on. Keeping diagnostics/timing here (rather than in the host) lets the host stay
/// language-neutral: it aborts on any <see cref="FailureDiagnostics"/> from ANY extractor and
/// prints one aggregate timing line.
/// </summary>
/// <param name="Result">The nodes/edges this extractor produced (empty when it has nothing to
/// contribute — e.g. a JS extractor on a solution with no authored JS).</param>
/// <param name="FailureDiagnostics">Fatal load diagnostics (e.g. MSBuild
/// <c>WorkspaceFailed</c> Failure entries). Any entry means the graph would be silently
/// incomplete — the host treats this as a hard stop (exit code 2).</param>
/// <param name="WarningDiagnostics">Non-fatal load diagnostics; logged, not fatal.</param>
/// <param name="LoadDescription">How the source was obtained this cycle — <c>"load"</c>,
/// <c>"refresh (N doc(s))"</c>, etc. Feeds the timing line.</param>
/// <param name="LoadMs">Milliseconds spent loading/refreshing the source.</param>
/// <param name="ExtractMs">Milliseconds spent walking the source into rows.</param>
public sealed record ExtractionOutcome(
    ExtractionResult Result,
    IReadOnlyList<string> FailureDiagnostics,
    IReadOnlyList<string> WarningDiagnostics,
    string LoadDescription,
    long LoadMs,
    long ExtractMs);
