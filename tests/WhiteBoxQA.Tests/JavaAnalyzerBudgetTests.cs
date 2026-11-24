using WhiteBoxQA.Core;

namespace WhiteBoxQA.Tests;

public sealed class JavaAnalyzerBudgetTests
{
    [Fact]
    public void DirectOversizedJavaInputIsRejectedBeforeNativeParserStarts()
    {
        var java = new JavaAnalyzer(() => throw new DllNotFoundException(),
            maxInputCharactersPerFile: 40);
        var result = java.Analyze([new("Huge.java", new string('x', 41))]);

        Assert.Equal(0, result.AnalyzedFileCount);
        Assert.Empty(result.Findings);
        Assert.Contains("파일당 40자 제한", Assert.Single(result.Issues).Reason);
        Assert.DoesNotContain(result.Issues, issue => issue.Reason.Contains("파서를 초기화", StringComparison.Ordinal));
    }

    [Fact]
    public void PerFileInputLimitKeepsSmallerJavaCSharpAndPythonAnalysisAvailable()
    {
        var java = new JavaAnalyzer(null, maxInputCharactersPerFile: 96);
        var analyzer = new AnalysisOrchestrator([java, new CSharpAnalyzer(), new PythonAnalyzer()]);
        var input = new SourceInput("Java budget",
        [
            new("Huge.java", "class Huge {}" + new string(' ', 96)),
            new("Healthy.java", "class Healthy { void check() { try {} catch (Exception e) {} } }"),
            new("Check.cs", "class Check { int M(bool enabled) => enabled ? 1 : 0; }"),
            new("check.py", "def route(a, b):\n    if a and b: return 1\n    return 0\n")
        ], []);
        var result = analyzer.Analyze(input);

        Assert.Equal(3, result.AnalyzedFileCount);
        Assert.Equal(AnalysisStatus.Partial, result.Status);
        Assert.Equal("Huge.java", Assert.Single(result.Issues).FileName);
        Assert.Equal("JAVA-002", Assert.Single(result.Findings).RuleId);
        Assert.Equal(new[] { 2, 3 }, result.ControlFlow!.Select(flow => flow.Complexity!.Value).Order());
    }

    [Fact]
    public void BatchInputLimitDoesNotPunishLaterSmallFiles()
    {
        var first = "class First {}" + new string(' ', 35);
        var second = "class Second {}" + new string(' ', 35);
        var third = "class Third {}";
        var java = new JavaAnalyzer(null, maxInputCharactersPerFile: 90, maxInputCharactersPerBatch: 70);
        var result = java.Analyze([
            new("First.java", first),
            new("Second.java", second),
            new("Third.java", third)
        ]);

        Assert.Equal(2, result.AnalyzedFileCount);
        Assert.Empty(result.Findings);
        Assert.Equal("Second.java", Assert.Single(result.Issues).FileName);
        Assert.Contains("전체 70자 제한", result.Issues[0].Reason);
    }

    [Fact]
    public void ExactRuleFindingCapIsNotReportedAsAnOmission()
    {
        var java = new JavaAnalyzer(null, maxRuleFindingsPerFile: 2, maxRuleFindingsPerBatch: 2);
        var code = "class Sample { void check() { try {} catch (Throwable failure) {} } }";
        var result = java.Analyze([new("Sample.java", code)]);

        Assert.Equal(1, result.AnalyzedFileCount);
        Assert.Equal(new[] { "JAVA-002", "JAVA-003" }, result.Findings.Select(finding => finding.RuleId));
        Assert.Empty(result.Issues);
    }

    [Fact]
    public void PerFileRuleFindingLimitTruncatesOnlyFurtherFindings()
    {
        var java = new JavaAnalyzer(null, maxRuleFindingsPerFile: 3, maxRuleFindingsPerBatch: 10);
        var code = "class Sample { void check() { " +
            "try {} catch (Throwable first) {} " +
            "try {} catch (Throwable second) {} " +
            "try {} catch (Exception third) {} " +
            "} }";
        var result = java.Analyze([new("Sample.java", code)]);

        Assert.Equal(1, result.AnalyzedFileCount);
        Assert.Equal(3, result.Findings.Count);
        Assert.Equal(2, result.Findings.Count(finding => finding.RuleId == "JAVA-002"));
        Assert.Equal(1, result.Findings.Count(finding => finding.RuleId == "JAVA-003"));
        Assert.Contains("정적 규칙 진단 한도", Assert.Single(result.Issues).Reason);
    }

    [Fact]
    public void BatchRuleLimitIsPartialOnlyForFilesWithOmittedFindings()
    {
        var java = new JavaAnalyzer(null, maxRuleFindingsPerFile: 5, maxRuleFindingsPerBatch: 2);
        var result = java.Analyze([
            new("First.java", "class First { void check() { try {} catch (Throwable e) {} } }"),
            new("Second.java", "class Second { void check() { try {} catch (Exception e) {} } }"),
            new("Third.java", "class Third {}")
        ]);

        Assert.Equal(3, result.AnalyzedFileCount);
        Assert.Equal(2, result.Findings.Count);
        Assert.Equal("First.java", result.Findings[0].FileName);
        Assert.Equal("First.java", result.Findings[1].FileName);
        Assert.Equal("Second.java", Assert.Single(result.Issues).FileName);
        Assert.Contains("정적 규칙 진단 한도", result.Issues[0].Reason);
    }
}
