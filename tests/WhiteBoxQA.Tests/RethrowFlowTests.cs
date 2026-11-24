using WhiteBoxQA.Core;

namespace WhiteBoxQA.Tests;

public sealed class RethrowFlowTests
{
    [Theory]
    [InlineData("ex = new Exception(); throw ex;", RethrowAssignment.AllPaths)]
    [InlineData("if (flag) ex = new Exception(); else ex = new InvalidOperationException(); throw ex;", RethrowAssignment.AllPaths)]
    [InlineData("if (flag) ex = new Exception(); throw ex;", RethrowAssignment.SomePaths)]
    [InlineData("while (flag) { ex = new Exception(); flag = false; } throw ex;", RethrowAssignment.SomePaths)]
    [InlineData("if (true) ex = new Exception(); throw ex;", RethrowAssignment.AllPaths)]
    [InlineData("if (false) ex = new Exception(); throw ex;", RethrowAssignment.None)]
    [InlineData("if (flag) { ex = new Exception(); return; } throw ex;", RethrowAssignment.None)]
    [InlineData("throw ex; ex = new Exception();", RethrowAssignment.None)]
    [InlineData("if (flag) throw (ex); ex = new Exception();", RethrowAssignment.None)]
    [InlineData("(ex, _) = (new Exception(), 1); throw ex;", RethrowAssignment.AllPaths)]
    [InlineData("ex ??= new Exception(); throw ex;", RethrowAssignment.None)]
    [InlineData("try { throw new Exception(); } catch (Exception inner) { inner = new Exception(); } throw ex;", RethrowAssignment.None)]
    [InlineData("try { ex = new Exception(); } catch (Exception inner) { inner = new Exception(); } throw ex;", RethrowAssignment.Unknown)]
    [InlineData("void Change() { ex = new Exception(); } throw ex;", RethrowAssignment.None)]
    [InlineData("Console.WriteLine(1); throw ex; void Change() { ex = new Exception(); }", RethrowAssignment.None)]
    [InlineData("Change(); throw ex; void Change() { ex = new Exception(); }", RethrowAssignment.Unknown)]
    [InlineData("void Change() { ex = new Exception(); } Change(); throw ex;", RethrowAssignment.Unknown)]
    [InlineData("Action change = () => ex = new Exception(); change(); throw ex;", RethrowAssignment.Unknown)]
    [InlineData("Replace(ref ex); throw ex;", RethrowAssignment.Unknown)]
    [InlineData("ref Exception alias = ref ex; alias = new Exception(); throw ex;", RethrowAssignment.Unknown)]
    [InlineData("try {} finally { ex = new Exception(); } throw ex;", RethrowAssignment.Unknown)]
    [InlineData("try { throw ex; } finally { ex = new Exception(); }", RethrowAssignment.None)]
    public void ClassifiesWritesInCatchBodyAtTheThrow(string body, RethrowAssignment expected)
    {
        var finding = Assert.Single(Analyze(body));
        Assert.Equal(expected, finding.ExceptionAssignment);
        Assert.Equal(Severity.Low, finding.Severity);
        Assert.Contains("스택 추적", finding.Description);
        Assert.Contains("Roslyn 심볼", finding.Evidence);
        if (expected != RethrowAssignment.None)
            Assert.Contains("어느 쪽", finding.Recommendation);
        if (expected == RethrowAssignment.Unknown)
            Assert.Contains("검토 필요", finding.ExceptionAssignmentLabel);
    }

    [Theory]
    [InlineData("when ((ex = new Exception()) != null)", RethrowAssignment.AllPaths)]
    [InlineData("when (flag || (ex = new Exception()) != null)", RethrowAssignment.SomePaths)]
    [InlineData("when (flag && (ex = new Exception()) != null)", RethrowAssignment.AllPaths)]
    [InlineData("when (true || (ex = new Exception()) != null)", RethrowAssignment.None)]
    [InlineData("when (flag || ((ex = new Exception()) == null && false))", RethrowAssignment.None)]
    [InlineData("when (false)", RethrowAssignment.Unknown)]
    public void OnlyTheAcceptingFilterPathsReachTheThrow(string filter, RethrowAssignment expected) =>
        Assert.Equal(expected, Assert.Single(Analyze("throw ex;", filter)).ExceptionAssignment);

    [Fact]
    public void DifferentThrowPointsInOneCatchHaveDifferentStates()
    {
        var findings = Analyze("""
            if (flag) throw ex;
            ex = new InvalidOperationException();
            throw ex;
            """);

        Assert.Equal(new RethrowAssignment?[] { RethrowAssignment.None, RethrowAssignment.AllPaths },
            findings.Select(finding => finding.ExceptionAssignment));
        Assert.True(findings[0].Line < findings[1].Line);
    }

    [Fact]
    public void NestedCatchVariablesAndLocalFunctionCatchHaveIndependentStates()
    {
        var findings = Analyze("""
            try { throw new Exception(); }
            catch (Exception inner)
            {
                if (flag) inner = new InvalidOperationException();
                throw inner;
            }
            """, suffix: """
            void Later()
            {
                try { throw new Exception(); }
                catch (Exception separate)
                {
                    separate = new Exception();
                    throw separate;
                }
            }
            """);

        Assert.Equal(new RethrowAssignment?[] { RethrowAssignment.SomePaths, RethrowAssignment.AllPaths },
            findings.Select(finding => finding.ExceptionAssignment));
    }

    private static Finding[] Analyze(string body, string filter = "", string suffix = "")
    {
        var result = new CSharpAnalyzer().Analyze(new("경로 분석",
            [new("Path.cs", $$"""
                using System;
                class Sample
                {
                    static void Replace(ref Exception exception) => exception = new Exception();
                    void Check(bool flag)
                    {
                        try { throw new Exception(); }
                        catch (Exception ex) {{filter}}
                        {
                            {{body}}
                        }
                        {{suffix}}
                    }
                }
                """)], []));
        Assert.Empty(result.Issues);
        Assert.DoesNotContain(result.Findings, finding => finding.RuleId == "WB-003");
        return result.Findings.Where(finding => finding.RuleId == "WB-002").ToArray();
    }
}
