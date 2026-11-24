using WhiteBoxQA.Core;

namespace WhiteBoxQA.Tests;

public sealed class PythonControlFlowTests
{
    [Fact]
    public void NestedIfElseAndElifFollowTheirOwnBranchesAndReturnToExit()
    {
        var method = AnalyzeSingle("""
            def choose(x, y):
                if x:
                    if y:
                        return 1
                    else:
                        return 2
                elif y:
                    return 3
                else:
                    return 4
                unreachable()
            """);

        Assert.Equal(("choose", SourceLanguage.Python, 1, 11, MethodFlowStatus.Analyzed, 4),
            (method.MethodName, method.Language, method.StartLine, method.EndLine, method.Status, method.Complexity!.Value));
        Assert.Equal(new[] { (2, 3, 7), (3, 4, 6), (7, 8, 10) },
            method.Decisions.Select(row => (row.Line, TargetLine(method, row.TrueTarget), TargetLine(method, row.FalseTarget))));
        Assert.All(method.Decisions, decision => Assert.True(decision.IncludedInComplexity));
        Assert.Equal("시작", method.Blocks[0].Kind);
        Assert.Equal("종료", method.Blocks[^1].Kind);
        Assert.All(method.Blocks.Where(block => block.Kind == "return"), block =>
            Assert.Equal(method.Blocks[^1].Ordinal, block.NextTarget));
        Assert.False(BlockAt(method, 11).IsReachable);
        Assert.True(method.Blocks[^1].IsReachable);
    }

    [Fact]
    public void LoopsConsumeAnIteratorOnceAndBreakSkipsOnlyItsOwnElse()
    {
        var method = AnalyzeSingle("""
            def route(items, ready, skip, stop):
                for item in items:
                    if skip(item):
                        continue
                    while ready(item):
                        if stop(item):
                            break
                        continue
                    if stop(item):
                        break
                else:
                    return -1
                return 0
            """);

        Assert.Equal(6, method.Complexity);
        var init = Assert.Single(method.Blocks, block => block.Kind == "for 초기화");
        var advance = Assert.Single(method.Blocks, block => block.Kind == "for");
        Assert.Equal(advance.Ordinal, init.NextTarget);
        Assert.Equal(init.Ordinal, method.Blocks[0].NextTarget);
        Assert.Equal(advance.Ordinal, BlockAt(method, 4).NextTarget);
        Assert.Equal(9, TargetLine(method, BlockAt(method, 7).NextTarget));
        Assert.Equal(5, TargetLine(method, BlockAt(method, 8).NextTarget));
        Assert.Equal(13, TargetLine(method, BlockAt(method, 10).NextTarget));
        Assert.Equal(12, TargetLine(method, advance.NextTarget));
    }

    [Fact]
    public void WhileElseHandlesFalseConditionAndBreakBypassesIt()
    {
        var method = AnalyzeSingle("""
            def run(test):
                while test():
                    break
                else:
                    cleanup()
                finish()
            """);

        Assert.Equal(2, method.Complexity);
        var decision = Assert.Single(method.Decisions);
        Assert.Equal((3, 5), (TargetLine(method, decision.TrueTarget), TargetLine(method, decision.FalseTarget)));
        Assert.Equal(6, TargetLine(method, BlockAt(method, 3).NextTarget));
        Assert.Equal(6, TargetLine(method, BlockAt(method, 5).NextTarget));
    }

    [Fact]
    public void LiteralBooleanConditionsPruneEdgesAndUnreachableDecisionsDoNotCount()
    {
        var method = AnalyzeSingle("""
            def run(flag):
                if (False):
                    if flag:
                        return 0
                while True:
                    continue
                return 1
            """);

        Assert.Equal(1, method.Complexity);
        Assert.Null(method.Decisions[0].TrueTarget);
        Assert.False(method.Decisions[1].IsReachable);
        Assert.Null(method.Decisions[2].FalseTarget);
        Assert.False(BlockAt(method, 7).IsReachable);
        Assert.False(method.Blocks[^1].IsReachable);
        Assert.All(method.Decisions, decision => Assert.False(decision.IncludedInComplexity));
    }

    [Fact]
    public void LimitedExpressionsLeaveIndependentVerifiedDecisionsInComplexity()
    {
        var method = AnalyzeSingle("""
            def limited(a, b, c):
                if a and (b or c):
                    work()
                elif a < b < c:
                    work()
                if a < c:
                    work()
                assert a
                return 1 if b else 0
            """);

        Assert.Equal(MethodFlowStatus.Limited, method.Status);
        Assert.Equal(2, method.Complexity);
        Assert.Contains("and/or", method.Limitation);
        Assert.Contains("연쇄 비교", method.Limitation);
        Assert.Contains("assert", method.Limitation);
        Assert.Contains("조건 표현식", method.Limitation);
        Assert.Equal(6, Assert.Single(method.Decisions, decision => decision.IncludedInComplexity).Line);
    }

    [Theory]
    [InlineData("def f():\n    try: work()\n    except Exception: recover()", "예외 처리")]
    [InlineData("def f():\n    match x:\n        case _: pass", "패턴 매칭")]
    [InlineData("async def f():\n    return await work()", "비동기")]
    [InlineData("def f():\n    yield 1", "제너레이터")]
    [InlineData("def f():\n    with resource(): use()", "with")]
    public void UnavailableFunctionsHaveReasonsWithoutAnInventedGraphOrComplexity(string code, string reason)
    {
        var method = AnalyzeSingle(code);
        Assert.Equal(MethodFlowStatus.Unavailable, method.Status);
        Assert.Contains(reason, method.Limitation);
        Assert.Null(method.Complexity);
        Assert.Empty(method.Decisions);
        Assert.Empty(method.Blocks);
    }

    [Fact]
    public void NestedFunctionBodiesDoNotContaminateTheirEnclosingGraph()
    {
        var result = Analyze("""
            class Tools:
                def outer(self, flag):
                    async def deferred():
                        return await work()
                    if flag:
                        return deferred
                    return None
            """);

        Assert.Collection(result.ControlFlow!,
            method => Assert.Equal(("Tools.outer", MethodFlowStatus.Analyzed, 2),
                (method.MethodName, method.Status, method.Complexity!.Value)),
            method => Assert.Equal(("Tools.outer.deferred", MethodFlowStatus.Unavailable),
                (method.MethodName, method.Status)));
    }

    [Fact]
    public void FunctionGraphsUseSourceLinesWithTabsUnicodeAndCrLf()
    {
        var method = AnalyzeSingle("def 검사(값):\r\n\tif 값 == '한글😀':\r\n\t\treturn 값\r\n\treturn None\r\n");
        var decision = Assert.Single(method.Decisions);
        Assert.Equal((2, "값 == '한글😀'", 3, 4),
            (decision.Line, decision.Condition, TargetLine(method, decision.TrueTarget), TargetLine(method, decision.FalseTarget)));
    }

    [Fact]
    public void AFunctionThatExceedsTheGraphBudgetHasNoPartialNumbers()
    {
        var method = AnalyzeSingle("def large():\n" + string.Concat(Enumerable.Repeat("    work()\n", 4_096)));
        Assert.Equal(MethodFlowStatus.Unavailable, method.Status);
        Assert.Contains("블록 한도", method.Limitation);
        Assert.Empty(method.Blocks);
        Assert.Null(method.Complexity);
    }

    [Fact]
    public void BatchRetainsHealthyGraphsAndAvoidsRecoveredGraphsAcrossHundredsOfFiles()
    {
        const string valid = "def f(flag):\n    if flag: return 1\n    return 0\n";
        var input = Enumerable.Range(0, 512)
            .Select(index => new SourceDocument($"Source{index}.py", index % 16 == 0 ? valid + "broken = (\n" : valid))
            .ToArray();

        var result = new PythonAnalyzer().Analyze(input);

        Assert.Equal(input.Length, result.AnalyzedFileCount);
        Assert.Empty(result.Issues);
        Assert.Equal(32, result.Findings.Count);
        Assert.All(result.Findings, finding => Assert.Equal("PY-001", finding.RuleId));
        Assert.Equal(512, result.ControlFlow!.Count);
        Assert.Equal(480, result.ControlFlow!.Count(method => method is { Status: MethodFlowStatus.Analyzed, Complexity: 2 }));
        Assert.Equal(32, result.ControlFlow!.Count(method => method is { Status: MethodFlowStatus.Unavailable, Complexity: null }));
    }

    private static FlowBlock BlockAt(MethodFlow method, int line) =>
        Assert.Single(method.Blocks, block => block.Line == line && block.Kind is not ("시작" or "종료"));
    private static int TargetLine(MethodFlow method, int? ordinal) => method.Blocks[Assert.IsType<int>(ordinal)].Line!.Value;

    private static MethodFlow AnalyzeSingle(string code)
    {
        var result = Analyze(code);
        Assert.Empty(result.Findings);
        Assert.Empty(result.Issues);
        return Assert.Single(result.ControlFlow!);
    }

    private static LanguageAnalysis Analyze(string code) => new PythonAnalyzer().Analyze([new("sample.py", code)]);
}
