using EdgeHop.Core;

namespace EdgeHop.Oxc;

/// <summary>
/// The <see cref="IExtractorProvider"/> for the oxc JS/TS extractor — discovered reflectively
/// by <see cref="ExtractorFactory"/> from this assembly (<c>EdgeHop.Oxc</c>).
/// </summary>
public sealed class OxcExtractorProvider : IExtractorProvider
{
    public string ExtractorName => "oxc";

    public IExtractor Create() => new OxcExtractor();
}
