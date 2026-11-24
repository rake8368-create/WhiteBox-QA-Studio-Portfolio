using WhiteBoxQA.Core;

namespace WhiteBoxQA.Tests;

public sealed class ControlFlowTests
{
    [Theory]
    [InlineData("return n;", 1, "")]
    [InlineData("if (n > 0) return 1; else return 0;", 2, "if 조건")]
    [InlineData("if (n > 0) { if (n > 1) return 2; } return 0;", 3, "if 조건")]
    [InlineData("for (int i = 0; i < n; i++) { n -= i; } return n;", 2, "for 반복")]
    [InlineData("while (n > 0) { n--; } return n;", 2, "while 반복")]
    [InlineData("do { n--; } while (n > 0); return n;", 2, "do/while 반복")]
    [InlineData("foreach (int value in new[] { n }) { n += value; } return n;", 2, "foreach 반복")]
    [InlineData("foreach (int value in new System.Collections.Generic.List<int> { n }) n += value; return n;", 2, "foreach 반복")]
    [InlineData("switch (n) { case 0: return 2; case 1: return 3; default: return 4; }", 3, "switch 항목")]
    [InlineData("if (n > 0 && n < 3 || n == 5) return 1; return 0;", 4, "&& 단락 평가")]
    [InlineData("return n > 0 ? 1 : 0;", 2, "삼항 조건")]
    public void CountsSupportedDecisionBlocksOnce(string body, int score, string branchKind)
    {
        var method = SingleMethod($"class Example {{ int Run(int n) {{ {body} }} }}");

        Assert.Equal(MethodFlowStatus.Analyzed, method.Status);
        Assert.Equal(score, method.Complexity);
        var counted = method.Decisions.Where(decision => decision.IncludedInComplexity).ToArray();
        Assert.Equal(score - 1, counted.Select(decision => decision.Block).Distinct().Count());
        if (score > 1)
            Assert.Contains(counted, decision => decision.Kind == branchKind);
    }

    [Fact]
    public void KeepsMethodGraphsIndependentAndMapsBranchToSourceAndSuccessors()
    {
        var result = Analyze("""
            class Example
            {
                int First(int value)
                {
                    if (value > 0)
                        return 1;
                    return 0;
                }

                int Second() => 2;
            }
            """);

        Assert.Collection(result.ControlFlow!,
            first =>
            {
                Assert.Contains("Example.First", first.MethodName);
                Assert.Equal(("Flow.cs", 3, 8, 2), (first.FileName, first.StartLine, first.EndLine, first.Complexity));
                Assert.Equal("시작", first.Blocks[0].Kind);
                Assert.Equal("종료", first.Blocks[^1].Kind);
                var branch = Assert.Single(first.Decisions);
                Assert.Equal((5, "value > 0", true), (branch.Line, branch.Condition, branch.IsReachable));
                Assert.Equal(6, Assert.Single(first.Blocks, block => block.Ordinal == branch.TrueTarget).Line);
                Assert.Equal(7, Assert.Single(first.Blocks, block => block.Ordinal == branch.FalseTarget).Line);
            },
            second =>
            {
                Assert.Contains("Example.Second", second.MethodName);
                Assert.Equal(1, second.Complexity);
                Assert.Equal(0, second.Blocks[0].Ordinal);
                Assert.Empty(second.Decisions);
            });
    }

    [Fact]
    public void ExplicitMoveNextInsideForeachRemainsAnIfAtItsOwnLine()
    {
        var method = SingleMethod("""
            class Example
            {
                void Run(int[] values, System.Collections.IEnumerator other)
                {
                    foreach (var value in values)
                        if (other.MoveNext())
                            System.Console.WriteLine(value);
                }
            }
            """);

        Assert.Equal(3, method.Complexity);
        var decisions = method.Decisions.Where(decision => decision.IncludedInComplexity);
        Assert.Collection(decisions,
            loop => Assert.Equal(("foreach 반복", 5), (loop.Kind, loop.Line)),
            condition => Assert.Equal(("if 조건", 6), (condition.Kind, condition.Line)));
    }

    [Fact]
    public void UnreachableBranchDoesNotIncreaseComplexity()
    {
        var method = SingleMethod("class Example { int Run(int n) { return n; if (n > 0) return 1; } }");

        Assert.Equal(1, method.Complexity);
        var branch = Assert.Single(method.Decisions);
        Assert.False(branch.IsReachable);
        Assert.False(branch.IncludedInComplexity);
    }

    [Theory]
    [InlineData("abstract class Example { public abstract int Run(); }", "본문")]
    [InlineData("class Example { Missing Run() => new Missing(); }", "CS0246")]
    public void UnavailableMethodHasNoInventedScore(string source, string reason)
    {
        var method = SingleMethod(source);

        Assert.Equal(MethodFlowStatus.Unavailable, method.Status);
        Assert.Null(method.Complexity);
        Assert.Contains(reason, method.Limitation);
        Assert.Empty(method.Blocks);
        Assert.Empty(method.Decisions);
    }

    [Fact]
    public void SyntaxErrorLeavesRuleFindingsAndOtherMethodsUsable()
    {
        var result = Analyze("""
            class Example
            {
                void Rules() { try { } catch { } }
                void Rethrow() { try { } catch (System.Exception e) { throw e; } }
                int Broken() { return ; int variable = ; }
                int Good() => 1;
            }
            """);

        Assert.Contains(result.Findings, finding => finding.RuleId == "WB-001");
        Assert.Contains(result.Findings, finding => finding.RuleId == "WB-002");
        Assert.Contains(result.Findings, finding => finding.RuleId == "WB-003");
        var broken = Assert.Single(result.ControlFlow!, method => method.MethodName.Contains("Broken"));
        Assert.Equal(MethodFlowStatus.Unavailable, broken.Status);
        Assert.Null(broken.Complexity);
        Assert.Equal(1, Assert.Single(result.ControlFlow!, method => method.MethodName.Contains("Good")).Complexity);
    }

    [Fact]
    public void ExceptionTransitionsAreIdentifiedAsOutsideComplexityScope()
    {
        var method = SingleMethod("""
            class Example
            {
                int Run(bool flag, bool enabled)
                {
                    try { if (flag) return 1; }
                    catch (System.Exception) when (flag && enabled) { return 2; }
                    return 0;
                }
            }
            """);

        Assert.Equal(MethodFlowStatus.Limited, method.Status);
        Assert.Equal(2, method.Complexity);
        Assert.Contains("예외 처리 전이", method.Limitation);
        Assert.Contains("예외 필터", method.Limitation);
        Assert.Equal("if 조건", Assert.Single(method.Decisions, decision => decision.IncludedInComplexity).Kind);
    }

    [Theory]
    [InlineData(1, "단순")]
    [InlineData(5, "단순")]
    [InlineData(6, "보통")]
    [InlineData(10, "보통")]
    [InlineData(11, "복잡")]
    [InlineData(20, "복잡")]
    [InlineData(21, "매우 복잡")]
    public void RatesScoresAtProjectThresholds(int score, string rating) =>
        Assert.Equal(rating, ComplexityBands.Default.Rate(score));

    private static MethodFlow SingleMethod(string source) => Assert.Single(Analyze(source).ControlFlow!);

    private static AnalysisResult Analyze(string source) =>
        new CSharpAnalyzer().Analyze(new("제어 흐름 예제", [new("Flow.cs", source)], []));
}
