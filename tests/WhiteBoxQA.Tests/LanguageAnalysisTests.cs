using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using WhiteBoxQA.Core;

namespace WhiteBoxQA.Tests;

public sealed class LanguageAnalysisTests
{
    [Theory]
    [InlineData("nested/Main.CS", SourceLanguage.CSharp)]
    [InlineData("script.py", SourceLanguage.Python)]
    [InlineData("api.pyi", SourceLanguage.Python)]
    [InlineData("script.mjs", SourceLanguage.JavaScript)]
    [InlineData("Component.tsx", SourceLanguage.TypeScript)]
    [InlineData("Main.java", SourceLanguage.Java)]
    [InlineData("library.c", SourceLanguage.C)]
    [InlineData("header.hpp", SourceLanguage.Cpp)]
    [InlineData("shared.h", SourceLanguage.Unknown)]
    [InlineData("image.png", SourceLanguage.Unknown)]
    public void LanguageIdentificationDoesNotRequireAnAnalyzer(string fileName, SourceLanguage language) =>
        Assert.Equal(language, LanguageIdentification.Identify(fileName));

    [Fact]
    public void DefaultRegistrySupportsSixLanguagesAndReportsUnsupportedLanguages()
    {
        var analyzer = new AnalysisOrchestrator();
        Assert.Equal(AnalysisCapabilities.StaticRules | AnalysisCapabilities.ControlFlow,
            analyzer.GetCapabilities(SourceLanguage.CSharp));
        Assert.Equal(AnalysisCapabilities.StaticRules | AnalysisCapabilities.ControlFlow, analyzer.GetCapabilities(SourceLanguage.Python));
        Assert.Equal(AnalysisCapabilities.StaticRules, analyzer.GetCapabilities(SourceLanguage.Java));
        Assert.Equal(AnalysisCapabilities.StaticRules, analyzer.GetCapabilities(SourceLanguage.JavaScript));
        Assert.Equal(AnalysisCapabilities.StaticRules, analyzer.GetCapabilities(SourceLanguage.Cpp));
        Assert.Equal(AnalysisCapabilities.StaticRules, analyzer.GetCapabilities(SourceLanguage.TypeScript));
        Assert.All(Enum.GetValues<SourceLanguage>().Where(language =>
            language is not (SourceLanguage.CSharp or SourceLanguage.Python or SourceLanguage.Java or SourceLanguage.JavaScript
                or SourceLanguage.Cpp or SourceLanguage.TypeScript)),
            language => Assert.Equal(AnalysisCapabilities.None, analyzer.GetCapabilities(language)));

        var unsupported = new SourceDocument("script.c", "int value = 1;");
        var result = analyzer.Analyze(new("mixed", [new("Valid.cs", "class Valid {}"), unsupported], []));
        Assert.Equal(AnalysisStatus.Partial, result.Status);
        Assert.Equal(1, result.AnalyzedFileCount);
        Assert.Equal(SourceLanguage.C, Assert.Single(result.Issues).UnsupportedLanguage);
        Assert.Contains("아직 지원하지 않습니다", result.Issues[0].Reason);

        var failed = analyzer.Analyze(new("unsupported only", [unsupported, new("README", "text")], []));
        Assert.Equal(AnalysisStatus.Failed, failed.Status);
        Assert.Equal(0, failed.AnalyzedFileCount);
        Assert.Equal(new SourceLanguage?[] { SourceLanguage.C, SourceLanguage.Unknown },
            failed.Issues.Select(issue => issue.UnsupportedLanguage));
    }

    [Fact]
    public void RegisteredAnalyzerReceivesTheEntireLanguageBatch()
    {
        var probe = new RecordingAnalyzer();
        var analyzer = new AnalysisOrchestrator([new CSharpAnalyzer(), probe]);
        var result = analyzer.Analyze(new("mixed",
            [new("a.py", "a"), new("Main.cs", "class Main {}"), new("b.PY", "b")], []));

        Assert.Equal(AnalysisCapabilities.StaticRules, analyzer.GetCapabilities(SourceLanguage.Python));
        Assert.Equal(1, probe.Calls);
        Assert.Equal(new[] { "a.py", "b.PY" }, probe.Files);
        Assert.Equal(3, result.AnalyzedFileCount);
        Assert.Equal(AnalysisStatus.Succeeded, result.Status);
    }

    [Fact]
    public void SharedCompilationBindsCrossFileSymbolsAndKeepsMethodsFromOtherFiles()
    {
        var result = new CSharpAnalyzer().Analyze(new("bundle",
            [
                new("Broken.cs", "class Broken { int Bad() => ; }"),
                new("Checks.cs", """
                    partial class Sample
                    {
                        int Evaluate() => Allowed() ? 1 : 0;
                        void Handle() { try { throw new CustomException(); } catch (CustomException ex) { throw ex; } }
                        void Swallow() { try { throw new CustomException(); } catch {} }
                    }
                    """),
                new("Types.cs", """
                    partial class Sample { static bool Allowed() => System.DateTime.Now.Ticks > 0; }
                    class CustomException : System.Exception {}
                    """)
            ], []));

        Assert.Equal(3, result.AnalyzedFileCount);
        Assert.Equal(new[] { "WB-003", "WB-001", "WB-002" }, result.Findings.Select(finding => finding.RuleId));
        var flows = Assert.IsAssignableFrom<IReadOnlyList<MethodFlow>>(result.ControlFlow);
        var evaluate = Assert.Single(flows, method => method.MethodName.Contains("Evaluate"));
        Assert.Equal("Checks.cs", evaluate.FileName);
        Assert.Equal(MethodFlowStatus.Analyzed, evaluate.Status);
        Assert.Equal(2, evaluate.Complexity);
        Assert.Equal(MethodFlowStatus.Unavailable, Assert.Single(flows, method => method.FileName == "Broken.cs").Status);
    }

    [Fact]
    public void CallerCanSupplyPreprocessorSymbolsAndAdditionalReferences()
    {
        var tree = CSharpSyntaxTree.ParseText("public static class Library { public static bool Enabled => true; }",
            cancellationToken: TestContext.Current.CancellationToken);
        var reference = CSharpCompilation.Create("SyntheticProject", [tree],
            [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)],
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)).ToMetadataReference();
        var input = new SourceInput("conditional", [new("Conditional.cs", """
            class Conditional
            {
            #if FEATURE
                int Evaluate() => Library.Enabled ? 1 : 0;
            #endif
            }
            """)], []);

        var defaultResult = new CSharpAnalyzer().Analyze(input);
        Assert.Empty(defaultResult.ControlFlow!);

        var configured = new CSharpAnalyzer(new()
        {
            PreprocessorSymbols = ["FEATURE"],
            AdditionalReferences = [reference]
        }).Analyze(input);
        var method = Assert.Single(configured.ControlFlow!);
        Assert.Equal(MethodFlowStatus.Analyzed, method.Status);
        Assert.Equal(2, method.Complexity);
        Assert.Empty(configured.Findings);
    }

    private sealed class RecordingAnalyzer : ILanguageAnalyzer
    {
        public SourceLanguage Language => SourceLanguage.Python;
        public AnalysisCapabilities Capabilities => AnalysisCapabilities.StaticRules;
        public int Calls { get; private set; }
        public string[] Files { get; private set; } = [];

        public LanguageAnalysis Analyze(IReadOnlyList<SourceDocument> documents)
        {
            Calls++;
            Files = documents.Select(document => document.FileName).ToArray();
            return new(documents.Count, [], []);
        }
    }
}
