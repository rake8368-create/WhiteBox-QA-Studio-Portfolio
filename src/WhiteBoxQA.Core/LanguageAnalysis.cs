namespace WhiteBoxQA.Core;

public enum SourceLanguage { Unknown, CSharp, Python, JavaScript, TypeScript, Java, C, Cpp }

public sealed record LanguageFileCount(SourceLanguage Language, int FileCount, int MetadataOnlyCount)
{
    public int? AnalyzedFileCount { get; init; }

    public string Label => LanguageIdentification.GetLabel(Language);
    public string CountLabel => AnalyzedFileCount is int analyzed ? $"발견 {FileCount:N0}개 · 분석 {analyzed:N0}개" : $"발견 {FileCount:N0}개";
    public string SupportLabel => MetadataOnlyCount == FileCount ? "확장자 식별만 지원" : "정적 분석 지원";
}

[Flags]
public enum AnalysisCapabilities { None = 0, StaticRules = 1, ControlFlow = 2 }

public interface ILanguageAnalyzer
{
    SourceLanguage Language { get; }
    AnalysisCapabilities Capabilities { get; }
    LanguageAnalysis Analyze(IReadOnlyList<SourceDocument> documents);
}

public sealed record LanguageAnalysis(
    int AnalyzedFileCount,
    IReadOnlyList<Finding> Findings,
    IReadOnlyList<AnalysisIssue> Issues)
{
    public IReadOnlyList<MethodFlow>? ControlFlow { get; init; }
}

public static class LanguageIdentification
{
    private static readonly Dictionary<string, SourceLanguage> Extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        [".cs"] = SourceLanguage.CSharp,
        [".py"] = SourceLanguage.Python,
        [".pyi"] = SourceLanguage.Python,
        [".js"] = SourceLanguage.JavaScript,
        [".jsx"] = SourceLanguage.JavaScript,
        [".mjs"] = SourceLanguage.JavaScript,
        [".cjs"] = SourceLanguage.JavaScript,
        [".ts"] = SourceLanguage.TypeScript,
        [".tsx"] = SourceLanguage.TypeScript,
        [".mts"] = SourceLanguage.TypeScript,
        [".cts"] = SourceLanguage.TypeScript,
        [".java"] = SourceLanguage.Java,
        [".c"] = SourceLanguage.C,
        // A plain .h header can belong to either C or C++.
        [".cpp"] = SourceLanguage.Cpp,
        [".cc"] = SourceLanguage.Cpp,
        [".cxx"] = SourceLanguage.Cpp,
        [".hpp"] = SourceLanguage.Cpp,
        [".hh"] = SourceLanguage.Cpp,
        [".hxx"] = SourceLanguage.Cpp
    };

    public static bool HasSourceReader(SourceLanguage language) =>
        language is SourceLanguage.CSharp or SourceLanguage.Python or SourceLanguage.Java
            or SourceLanguage.JavaScript or SourceLanguage.Cpp or SourceLanguage.TypeScript;

    public static SourceLanguage Identify(string fileName) => Extensions.GetValueOrDefault(Path.GetExtension(fileName));

    public static IEnumerable<string> GetExtensions(SourceLanguage? language = null) => Extensions
        .Where(item => language is null || item.Value == language).Select(item => item.Key);

    public static string GetLabel(SourceLanguage language) => language switch
    {
        SourceLanguage.CSharp => "C#",
        SourceLanguage.Python => "Python",
        SourceLanguage.JavaScript => "JavaScript",
        SourceLanguage.TypeScript => "TypeScript",
        SourceLanguage.Java => "Java",
        SourceLanguage.C => "C",
        SourceLanguage.Cpp => "C++",
        _ => "알 수 없는 언어"
    };
}
