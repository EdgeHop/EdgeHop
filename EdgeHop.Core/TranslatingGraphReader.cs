namespace EdgeHop.Core;

/// <summary>
/// An <see cref="IGraphReader"/> decorator that converts a backend's native runtime
/// exception into a Core <see cref="GraphStoreException"/>, so no front end has to
/// reference a driver exception type to handle "the store failed" (the pluggable-backend
/// isolation guarantee — see <see cref="GraphStoreException"/>).
/// <para>
/// Each backend store wraps its own concrete reader with this decorator, supplying the
/// predicate that recognizes ITS driver exception (e.g. <c>ex is Neo4jException</c>,
/// <c>ex is SqliteException</c>). The predicate is defined in the backend assembly — the
/// only place that references the driver type — so Core stays driver-free. Argument
/// validation exceptions (<see cref="ArgumentException"/> and friends) are never matched
/// by a backend predicate, so they propagate unchanged for the caller's validation catch.
/// </para>
/// </summary>
public sealed class TranslatingGraphReader : IGraphReader
{
    private readonly IGraphReader _inner;
    private readonly Func<Exception, bool> _isBackendException;

    /// <param name="inner">The concrete backend reader.</param>
    /// <param name="isBackendException">Recognizes the backend's native driver exception;
    /// a matching exception is rethrown as <see cref="GraphStoreException"/>. Anything else
    /// (validation, cancellation) propagates unchanged.</param>
    public TranslatingGraphReader(IGraphReader inner, Func<Exception, bool> isBackendException)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(isBackendException);
        _inner = inner;
        _isBackendException = isBackendException;
    }

    public Task<IReadOnlyList<SymbolHit>> FindSymbolsAsync(
        string branch, string query, string? kind = null, int limit = 25, CancellationToken ct = default) =>
        Guard(() => _inner.FindSymbolsAsync(branch, query, kind, limit, ct));

    public Task<IReadOnlyList<SymbolHit>> GetCallersAsync(
        string branch, string symbolId, int depth = 1, CancellationToken ct = default) =>
        Guard(() => _inner.GetCallersAsync(branch, symbolId, depth, ct));

    public Task<IReadOnlyList<RelationshipHit>> GetRelationshipsAsync(
        string branch, string symbolId, RelationshipDirection direction, string? edgeType,
        int depth, int limit, CancellationToken ct = default) =>
        Guard(() => _inner.GetRelationshipsAsync(branch, symbolId, direction, edgeType, depth, limit, ct));

    public Task<PathResult> GetPathAsync(
        string branch, string fromId, string toId, string? edgeType, int maxLength, CancellationToken ct = default) =>
        Guard(() => _inner.GetPathAsync(branch, fromId, toId, edgeType, maxLength, ct));

    public Task<GraphStatsResult> GetStatsAsync(
        string branch, int topN, CancellationToken ct = default) =>
        Guard(() => _inner.GetStatsAsync(branch, topN, ct));

    private async Task<T> Guard<T>(Func<Task<T>> operation)
    {
        try
        {
            return await operation().ConfigureAwait(false);
        }
        catch (Exception ex) when (_isBackendException(ex))
        {
            throw new GraphStoreException(ex.Message, ex);
        }
    }
}
