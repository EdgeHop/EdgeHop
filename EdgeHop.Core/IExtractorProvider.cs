namespace EdgeHop.Core;

/// <summary>
/// The entry point <see cref="ExtractorFactory"/> looks for inside each
/// <c>EdgeHop.&lt;Name&gt;</c> extractor plugin assembly: a single public, parameterless
/// implementation that names its extractor and creates instances. Mirrors
/// <see cref="IGraphStoreProvider"/> on the store side — Core carries no compile-time
/// reference to any extractor implementation (nor to MSBuild), so the indexer host stays
/// language- and toolchain-neutral and the MSBuild JIT-ordering rule cannot be violated by
/// Core or the host.
/// </summary>
public interface IExtractorProvider
{
    /// <summary>The extractor's short name (e.g. <c>"roslyn"</c>, <c>"oxc"</c>). The factory
    /// validates the loaded provider's name matches the requested one.</summary>
    string ExtractorName { get; }

    /// <summary>Creates a fresh extractor instance. Cheap; does no source loading.</summary>
    IExtractor Create();
}
