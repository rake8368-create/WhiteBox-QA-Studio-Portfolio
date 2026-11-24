using WhiteBoxQA.Core;

namespace WhiteBoxQA.Infrastructure;

internal sealed class SourceInventory
{
    private readonly Dictionary<SourceLanguage, int> counts = [];

    public void Add(SourceLanguage language) => counts[language] = counts.GetValueOrDefault(language) + 1;

    public IReadOnlyList<LanguageFileCount> Snapshot() => counts.OrderBy(item => item.Key)
        .Select(item => new LanguageFileCount(item.Key, item.Value, LanguageIdentification.HasSourceReader(item.Key) ? 0 : item.Value))
        .ToArray();
}
