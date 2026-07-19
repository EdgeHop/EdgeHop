using EdgeHop.Core;

namespace EdgeHop.Roslyn;

/// <summary>
/// The <see cref="IExtractorProvider"/> for the Roslyn extractor — discovered reflectively by
/// <see cref="ExtractorFactory"/> from this assembly (<c>EdgeHop.Roslyn</c>).
/// </summary>
public sealed class RoslynExtractorProvider : IExtractorProvider
{
    public string ExtractorName => "roslyn";

    public IExtractor Create() => new RoslynExtractor();
}
