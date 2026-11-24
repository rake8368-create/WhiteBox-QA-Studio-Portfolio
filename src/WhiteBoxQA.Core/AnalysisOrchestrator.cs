namespace WhiteBoxQA.Core;

public sealed class AnalysisOrchestrator(IEnumerable<ILanguageAnalyzer> analyzers)
{
    private readonly Dictionary<SourceLanguage, ILanguageAnalyzer> registry =
        analyzers.ToDictionary(analyzer => analyzer.Language);

    public AnalysisOrchestrator() : this([new CSharpAnalyzer(), new PythonAnalyzer(), new JavaAnalyzer(),
        new JavaScriptAnalyzer(), new CppAnalyzer(), new TypeScriptAnalyzer()]) { }

    public AnalysisCapabilities GetCapabilities(SourceLanguage language) =>
        language != SourceLanguage.Unknown && registry.TryGetValue(language, out var analyzer)
            ? analyzer.Capabilities : AnalysisCapabilities.None;

    public AnalysisResult Analyze(SourceInput input)
    {
        var analyzed = 0;
        var findings = new List<Finding>();
        var issues = new List<AnalysisIssue>(input.Issues);
        var flow = new List<MethodFlow>();
        var analyzedByLanguage = new Dictionary<SourceLanguage, int>();
        var inventory = input.LanguageInventory?.ToDictionary(item => item.Language) ?? [];

        foreach (var language in inventory.Values.Where(item => item.MetadataOnlyCount > 0))
        {
            issues.Add(new(language.Label, $"파일 {language.MetadataOnlyCount:N0}개는 확장자만 식별했고 분석 범위에서 제외했습니다.")
                { UnsupportedLanguage = language.Language });
        }

        foreach (var group in input.Documents.DistinctBy(document => document.FileName, StringComparer.Ordinal)
            .GroupBy(document => LanguageIdentification.Identify(document.FileName)))
        {
            var language = group.Key;
            var documents = group.ToArray();
            if (language == SourceLanguage.Unknown || !registry.TryGetValue(language, out var analyzer)
                || analyzer.Capabilities == AnalysisCapabilities.None)
            {
                if (language != SourceLanguage.Unknown)
                    inventory.TryAdd(language, new(language, documents.Length, documents.Length));
                issues.AddRange(documents.Select(document => Unsupported(document.FileName)));
                continue;
            }

            inventory.TryAdd(language, new(language, documents.Length, 0));
            var result = analyzer.Analyze(documents);
            analyzedByLanguage[language] = result.AnalyzedFileCount;
            analyzed += result.AnalyzedFileCount;
            findings.AddRange(result.Findings);
            issues.AddRange(result.Issues);
            if (result.ControlFlow is { } methods)
                flow.AddRange(methods);
        }

        if (analyzed == 0 && issues.Count == 0)
            issues.Add(new(input.Target, "분석할 소스 코드가 없습니다."));

        return new(DateTimeOffset.Now, input.Target, analyzed,
            findings.OrderBy(finding => finding.Severity)
                .ThenBy(finding => finding.FileName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(finding => finding.Line).ThenBy(finding => finding.Column).ToArray(),
            issues.ToArray())
        {
            Origin = input.Origin,
            LanguageInventory = inventory.Count == 0 ? input.LanguageInventory : inventory.Values.OrderBy(item => item.Language)
                .Select(item => item with { AnalyzedFileCount = analyzedByLanguage.GetValueOrDefault(item.Language) }).ToArray(),
            ControlFlow = flow.OrderBy(method => method.FileName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(method => method.StartLine).ToArray()
        };
    }

    public static AnalysisIssue Unsupported(string fileName)
    {
        var language = LanguageIdentification.Identify(fileName);
        var reason = language == SourceLanguage.Unknown
            ? "파일 확장자로 소스 언어를 식별할 수 없습니다."
            : $"{LanguageIdentification.GetLabel(language)} 분석은 아직 지원하지 않습니다.";
        return new(fileName, reason) { UnsupportedLanguage = language };
    }
}
