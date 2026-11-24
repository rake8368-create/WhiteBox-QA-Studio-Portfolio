using WhiteBoxQA.Core;

namespace WhiteBoxQA.Tests;

public sealed class AnalyzerTests
{
    private readonly CSharpAnalyzer analyzer = new();

    [Fact]
    public void EmptyCatchReportsRuleAndCatchLine()
    {
        var result = Analyze(Sample("EmptyCatch.cs"));

        Assert.Equal(AnalysisStatus.Succeeded, result.Status);
        var finding = Assert.Single(result.Findings);
        Assert.Equal("WB-001", finding.RuleId);
        Assert.Equal(Severity.Moderate, finding.Severity);
        Assert.Equal("EmptyCatch.cs", finding.FileName);
        Assert.Equal(8, finding.Line);
        Assert.False(string.IsNullOrWhiteSpace(finding.Evidence));
        Assert.NotNull(finding.Caution);
    }

    [Fact]
    public void CatchWithHandlingDoesNotReportEmptyCatch()
    {
        var result = Analyze(Sample("HandledCatch.cs"));

        Assert.DoesNotContain(result.Findings, finding => finding.RuleId == "WB-001");
    }

    [Fact]
    public void ThrowCaughtVariableReportsRuleAndThrowLine()
    {
        var result = Analyze(Sample("ThrowCaughtException.cs"));

        var finding = Assert.Single(result.Findings);
        Assert.Equal("WB-002", finding.RuleId);
        Assert.Equal(Severity.Low, finding.Severity);
        Assert.Equal(10, finding.Line);
        Assert.Contains("throw;", finding.Recommendation);
    }

    [Fact]
    public void BareRethrowDoesNotReportCaughtVariable()
    {
        var result = Analyze(Sample("HandledCatch.cs"));

        Assert.DoesNotContain(result.Findings, finding => finding.RuleId == "WB-002");
    }

    [Fact]
    public void NestedCatchAndLocalFunctionDoNotUseOuterCatchVariableForRethrow()
    {
        const string code = """
            void Run()
            {
                try { throw new System.Exception(); }
                catch (System.Exception outer)
                {
                    try { throw new System.Exception(); }
                    catch (System.Exception) { throw outer; }
                    void ThrowLater() { throw outer; }
                    System.Action callback = () => { throw outer; };
                    throw new System.Exception("wrapped", outer);
                }
            }
            """;

        Assert.Empty(Analyze(new SourceDocument("Nested.cs", code)).Findings);
    }

    [Fact]
    public void ParenthesizedCaughtVariableStillResolvesToSameSymbol()
    {
        var sample = Sample("ThrowCaughtException.cs");

        var finding = Assert.Single(Analyze(sample with { Code = sample.Code.Replace("throw ex;", "throw (ex);") }).Findings);
        Assert.Equal("WB-002", finding.RuleId);
    }

    [Fact]
    public void SyntaxErrorIncludesRoslynIdAndPhysicalLocation()
    {
        var result = Analyze(Sample("SyntaxError.cs"));

        Assert.Equal(AnalysisStatus.Succeeded, result.Status);
        var finding = Assert.Single(result.Findings);
        Assert.Equal("WB-003", finding.RuleId);
        Assert.Equal(Severity.High, finding.Severity);
        Assert.Equal(5, finding.Line);
        Assert.Contains("CS1002", finding.Title);
        Assert.NotEmpty(finding.Description);
    }

    [Fact]
    public void MissingReferencesAreExcludedFromSyntaxDiagnostics()
    {
        const string code = "using External.Package; class Demo { UnknownType value; }";

        var result = Analyze(new SourceDocument("References.cs", code));

        Assert.Equal(AnalysisStatus.Succeeded, result.Status);
        Assert.Empty(result.Findings);
    }

    [Fact]
    public void ValidCodeSucceedsWithNoFindings()
    {
        var result = Analyze(Sample("CleanCode.cs"));

        Assert.Equal(AnalysisStatus.Succeeded, result.Status);
        Assert.Equal(1, result.AnalyzedFileCount);
        Assert.Empty(result.Findings);
        Assert.Empty(result.Issues);
    }

    [Fact]
    public void BlankInputFailsWithReason()
    {
        var result = Analyze(new SourceDocument("Blank.cs", " \r\n\t "));

        Assert.Equal(AnalysisStatus.Failed, result.Status);
        Assert.Equal(0, result.AnalyzedFileCount);
        Assert.Empty(result.Findings);
        Assert.Contains("비어", Assert.Single(result.Issues).Reason);
    }

    [Fact]
    public void SeveralRulesAcrossFilesAreSortedBySeverity()
    {
        var result = Analyze(Sample("ThrowCaughtException.cs"), Sample("EmptyCatch.cs"), Sample("SyntaxError.cs"));

        Assert.Equal(3, result.AnalyzedFileCount);
        Assert.Equal(new[] { "WB-003", "WB-001", "WB-002" }, result.Findings.Select(finding => finding.RuleId));
        Assert.Equal(1, result.Count(Severity.High));
        Assert.Equal(1, result.Count(Severity.Moderate));
        Assert.Equal(1, result.Count(Severity.Low));
        Assert.Equal(0, result.Count(Severity.Information));
    }

    [Fact]
    public void UnsupportedFileMakesOtherwiseValidAnalysisPartial()
    {
        var result = Analyze(new SourceDocument("script.py", "print('hello')"), Sample("CleanCode.cs"));

        Assert.Equal(AnalysisStatus.Partial, result.Status);
        Assert.Equal(1, result.AnalyzedFileCount);
        Assert.Equal("script.py", Assert.Single(result.Issues).FileName);
    }

    private AnalysisResult Analyze(params SourceDocument[] documents) =>
        analyzer.Analyze(new("단위 테스트", documents, []));

    internal static SourceDocument Sample(string name) =>
        new(name, File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "samples", name)));
}
