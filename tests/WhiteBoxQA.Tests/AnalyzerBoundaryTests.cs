using WhiteBoxQA.Core;

namespace WhiteBoxQA.Tests;

public sealed class AnalyzerBoundaryTests
{
    [Theory]
    [InlineData("B101", "try {} catch {}", 1)]
    [InlineData("B102", "try {} catch { /* documented */ }", 1)]
    [InlineData("B103", "try {} catch { System.Console.WriteLine(1); }", 0)]
    [InlineData("B104", "try {} catch (System.Exception e) when (e.Message != null) {}", 1)]
    [InlineData("B105", "try {} catch { try {} catch {} }", 1)]
    [InlineData("B106", "try {} catch { // Best effort, intentionally ignored\n }", 1)]
    [InlineData("B107", "try {} catch { ; { ; {} } }", 1)]
    [InlineData("B108", "try {} catch when () {}", 0)]
    public void EmptyCatchBoundaryCases(string id, string source, int expected)
    {
        var result = Analyze(source);
        var hits = result.Findings.Where(finding => finding.RuleId == "WB-001").ToArray();

        Assert.Equal(AnalysisStatus.Succeeded, result.Status);
        Assert.Equal(expected, hits.Length);
        Assert.All(hits, finding =>
        {
            Assert.Equal(Severity.Moderate, finding.Severity);
            Assert.NotNull(finding.Caution);
        });
        if (id == "B108")
            Assert.Contains(result.Findings, finding => finding.RuleId == "WB-003");
    }

    [Theory]
    [InlineData("try {} catch (System.Exception ex) { throw ex; }", 1)]
    [InlineData("try {} catch (System.Exception ex) { throw; }", 0)]
    [InlineData("try {} catch (System.Exception ex) { throw (ex); }", 1)]
    [InlineData("try {} catch (System.Exception outer) { try {} catch (System.Exception inner) { throw inner; } }", 1)]
    [InlineData("try {} catch (System.Exception outer) { try {} catch { throw outer; } }", 0)]
    [InlineData("try {} catch (System.Exception ex) { void Later() { throw ex; } }", 0)]
    [InlineData("try {} catch (System.Exception ex) { System.Action later = () => { throw ex; }; }", 0)]
    [InlineData("try {} catch (System.Exception ex) { throw new System.Exception(\"wrap\", ex); }", 0)]
    [InlineData("try {} catch (System.Exception ex) { System.Console.WriteLine(ex); } { var ex = new System.Exception(); throw ex; }", 0)]
    public void RethrowUsesTheSymbolAndTheImmediateCatchScope(string source, int expected)
    {
        var result = Analyze(source);
        Assert.Equal(AnalysisStatus.Succeeded, result.Status);
        var hits = result.Findings.Where(finding => finding.RuleId == "WB-002").ToArray();
        Assert.Equal(expected, hits.Length);
        Assert.All(hits, finding => Assert.Equal(Severity.Low, finding.Severity));
    }

    [Theory]
    [InlineData("try {} catch (System.Exception ex) { ex = new System.Exception(); throw ex; }", true)]
    [InlineData("try {} catch (System.Exception ex) { if (System.DateTime.Now.Ticks > 0) ex = new System.Exception(); throw ex; }", true)]
    [InlineData("try {} catch (System.Exception ex) when ((ex = new System.Exception()) != null) { throw ex; }", true)]
    [InlineData("try {} catch (System.Exception ex) { var other = ex; other = new System.Exception(); throw ex; }", false)]
    public void RethrowExplainsPossibleWritesWithoutAssumingTheyExecute(string source, bool mayWrite)
    {
        var hit = Assert.Single(Analyze(source).Findings, finding => finding.RuleId == "WB-002");
        Assert.NotNull(hit.Caution);
        if (mayWrite)
        {
            Assert.Contains("쓰는 코드가 있습니다", hit.Caution);
            Assert.Contains("경로별 확인", hit.Caution);
            Assert.Contains("어느 쪽", hit.Recommendation);
        }
        else
        {
            Assert.Contains("직접 쓰기는 탐지되지", hit.Caution);
        }
    }

    [Theory]
    [InlineData("int total = 3", true)]
    [InlineData("if (true { System.Console.WriteLine(1); }", true)]
    [InlineData("class Good { int Value => 42; }", false)]
    [InlineData("class Box<T> where T : class { T? Value { get; set; } }", false)]
    [InlineData("class Good { async System.Threading.Tasks.Task<int> M() { await System.Threading.Tasks.Task.Yield(); return 1; } }", false)]
    [InlineData("#define ACTIVE\n#if ACTIVE\nint total = 3\n#endif", true)]
    [InlineData("#if NOT_DEFINED\nthis is } invalid {\n#else\nint total = 3;\n#endif", false)]
    [InlineData("#if\n#endif", true)]
    [InlineData("using External.Package; class UsesExternal { MissingReference Field; }", false)]
    public void SyntaxRuleUsesActiveCSharpSyntaxWithoutProjectBinding(string source, bool expected)
    {
        var result = Analyze(source);
        Assert.Equal(AnalysisStatus.Succeeded, result.Status);

        var hits = result.Findings.Where(finding => finding.RuleId == "WB-003").ToArray();
        Assert.Equal(expected, hits.Length != 0);
        Assert.All(hits, finding =>
        {
            Assert.Equal(Severity.High, finding.Severity);
            Assert.StartsWith("Roslyn C# 구문 진단 CS", finding.Evidence);
        });
    }

    private static AnalysisResult Analyze(string code) =>
        new CSharpAnalyzer().Analyze(new("경계 사례", [new("Boundary.cs", code)], []));
}
