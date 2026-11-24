using WhiteBoxQA.Core;

namespace WhiteBoxQA.Tests;

public sealed class PythonShortCircuitTests
{
    [Theory]
    [InlineData("and")]
    [InlineData("or")]
    public void IfEvaluatesTheLeftCallBeforeTheRightCall(string operation)
    {
        var method = Analyze($"""
            def choose():
                if first() {operation} second():
                    return 1
                else:
                    return 0
            """);

        Assert.Equal(MethodFlowStatus.Analyzed, method.Status);
        Assert.Null(method.Limitation);
        Assert.Equal(3, method.Complexity);
        Assert.Collection(method.Decisions,
            left => Assert.Equal(($"{operation} 왼쪽", 2, "first()"), (left.Kind, left.Line, left.Condition)),
            right => Assert.Equal(($"{operation} 오른쪽", 2, "second()"), (right.Kind, right.Line, right.Condition)));
        var left = method.Decisions[0];
        var right = method.Decisions[1];
        Assert.Equal(left.Block, method.Blocks[0].NextTarget);
        Assert.Equal(BlockAt(method, 3).Ordinal, right.TrueTarget);
        Assert.Equal(BlockAt(method, 5).Ordinal, right.FalseTarget);
        Assert.Equal(operation == "and" ? right.Block : right.TrueTarget, left.TrueTarget);
        Assert.Equal(operation == "and" ? right.FalseTarget : right.Block, left.FalseTarget);
        Assert.All(method.Decisions, decision => Assert.True(decision.IncludedInComplexity));
        Assert.All(method.Blocks.Where(block => block.Kind == "return"), block =>
            Assert.Equal(method.Blocks[^1].Ordinal, block.NextTarget));
    }

    [Theory]
    [InlineData("and")]
    [InlineData("or")]
    public void WhileRechecksTheLeftOperandAndPreservesContinueBreakAndLoopElse(string operation)
    {
        var method = Analyze($"""
            def route(a, b, skip, stop):
                while a {operation} b:
                    if skip:
                        continue
                    if stop:
                        break
                    consume()
                else:
                    exhausted()
                return 0
            """);

        Assert.Equal(MethodFlowStatus.Analyzed, method.Status);
        Assert.Equal(5, method.Complexity);
        var left = method.Decisions[0];
        var right = method.Decisions[1];
        Assert.Equal((2, 2, "a", "b"), (left.Line, right.Line, left.Condition, right.Condition));
        Assert.Equal(left.Block, method.Blocks[0].NextTarget);
        Assert.Equal(BlockAt(method, 3).Ordinal, right.TrueTarget);
        Assert.Equal(BlockAt(method, 9).Ordinal, right.FalseTarget);
        Assert.Equal(operation == "and" ? right.Block : right.TrueTarget, left.TrueTarget);
        Assert.Equal(operation == "and" ? right.FalseTarget : right.Block, left.FalseTarget);
        Assert.Equal(left.Block, BlockAt(method, 4).NextTarget);
        Assert.Equal(left.Block, BlockAt(method, 7).NextTarget);
        Assert.Equal(BlockAt(method, 10).Ordinal, BlockAt(method, 6).NextTarget);
        Assert.Equal(BlockAt(method, 10).Ordinal, BlockAt(method, 9).NextTarget);
    }

    [Theory]
    [InlineData("False", "and", false, false, true, 1)]
    [InlineData("True", "or", false, true, false, 1)]
    [InlineData("True", "and", true, true, true, 2)]
    [InlineData("False", "or", true, true, true, 2)]
    public void ALeftLiteralPrunesTheImpossibleEdgeAndMaySkipTheRightOperand(
        string literal, string operation, bool rightReachable, bool whenTrueReachable, bool whenFalseReachable, int complexity)
    {
        var method = Analyze($"""
            def choose():
                if {literal} {operation} second():
                    answer()
                else:
                    other()
            """);

        Assert.Equal(MethodFlowStatus.Analyzed, method.Status);
        Assert.Equal(complexity, method.Complexity);
        var left = method.Decisions[0];
        var right = method.Decisions[1];
        Assert.Equal(literal, left.Condition);
        Assert.Equal("단일 경로", left.Inclusion);
        Assert.True(left.IsConstant);
        Assert.Null(literal == "True" ? left.FalseTarget : left.TrueTarget);
        var activeTarget = literal == "True" ? left.TrueTarget : left.FalseTarget;
        Assert.Equal(rightReachable ? right.Block : BlockAt(method, literal == "True" ? 3 : 5).Ordinal, activeTarget);
        Assert.Equal(rightReachable, right.IsReachable);
        Assert.Equal(rightReachable, right.IncludedInComplexity);
        Assert.Equal(whenTrueReachable, BlockAt(method, 3).IsReachable);
        Assert.Equal(whenFalseReachable, BlockAt(method, 5).IsReachable);
    }

    [Theory]
    [InlineData("and", "False", false)]
    [InlineData("or", "True", true)]
    public void ARightLiteralCanDetermineTheOutcomeWithoutRemovingTheLeftDecision(
        string operation, string literal, bool whenTrueReachable)
    {
        var method = Analyze($"""
            def choose(a):
                if a {operation} {literal}:
                    answer()
                else:
                    other()
            """);

        Assert.Equal(2, method.Complexity);
        Assert.True(method.Decisions[0].IncludedInComplexity);
        var right = method.Decisions[1];
        Assert.True(right.IsReachable);
        Assert.True(right.IsConstant);
        Assert.False(right.IncludedInComplexity);
        Assert.Null(whenTrueReachable ? right.FalseTarget : right.TrueTarget);
        Assert.Equal(whenTrueReachable, BlockAt(method, 3).IsReachable);
        Assert.Equal(!whenTrueReachable, BlockAt(method, 5).IsReachable);
    }

    [Fact]
    public void FalseAndInWhileBypassesTheRightOperandAndBodyForItsElse()
    {
        var method = Analyze("""
            def route():
                while False and check():
                    consume()
                else:
                    done()
            """);

        Assert.Equal(1, method.Complexity);
        var left = method.Decisions[0];
        Assert.Null(left.TrueTarget);
        Assert.Equal(BlockAt(method, 5).Ordinal, left.FalseTarget);
        Assert.False(method.Decisions[1].IsReachable);
        Assert.False(BlockAt(method, 3).IsReachable);
        Assert.True(BlockAt(method, 5).IsReachable);
    }

    [Fact]
    public void AParenthesizedMultilineElifPreservesTheOperandsSourceLines()
    {
        var method = Analyze("""
            def choose(skip):
                if skip:
                    return 0
                elif (
                    check('한글😀')
                    and (other())
                ):
                    return 1
                return 2
            """);

        Assert.Equal(4, method.Complexity);
        Assert.Collection(method.Decisions,
            initial => Assert.Equal((2, "skip", method.Decisions[1].Block), (initial.Line, initial.Condition, initial.FalseTarget!.Value)),
            left => Assert.Equal((5, "check('한글😀')"), (left.Line, left.Condition)),
            right => Assert.Equal((6, "other()"), (right.Line, right.Condition)));
    }

    [Theory]
    [InlineData("a and b and c")]
    [InlineData("a and (b or c)")]
    [InlineData("not (a and b)")]
    [InlineData("a or not b")]
    [InlineData("a and (b < c < d)")]
    [InlineData("a and check(b or c)")]
    public void CompositeConditionsRetainTheirLimitationWithoutAFalsePartialExpansion(string expression)
    {
        var method = Analyze($"def choose(a, b, c, d):\n    if {expression}:\n        return 1\n    return 0");

        Assert.Equal(MethodFlowStatus.Limited, method.Status);
        Assert.Contains("and/or 단락 평가 제외", method.Limitation);
        Assert.Equal(1, method.Complexity);
        var original = Assert.Single(method.Decisions);
        Assert.Equal(("if", expression, false), (original.Kind, original.Condition, original.IncludedInComplexity));
    }

    [Fact]
    public void AHandledPairAndACompositeConditionAreClassifiedIndependently()
    {
        var method = Analyze("""
            def choose(a, b, c):
                if a and b:
                    work()
                if a and (b or c):
                    recover()
            """);

        Assert.Equal(MethodFlowStatus.Limited, method.Status);
        Assert.Equal("and/or 단락 평가 제외", method.Limitation);
        Assert.Equal(3, method.Complexity);
        Assert.Collection(method.Decisions,
            left => Assert.True(left.IncludedInComplexity),
            right => Assert.True(right.IncludedInComplexity),
            composite => Assert.False(composite.IncludedInComplexity));
    }

    [Fact]
    public void BooleanExpressionsUsedAsValuesRemainOutsideTheSupportedFlow()
    {
        var method = Analyze("def choose(a, b):\n    result = a and b\n    return result or a");

        Assert.Equal(MethodFlowStatus.Limited, method.Status);
        Assert.Contains("and/or 단락 평가 제외", method.Limitation);
        Assert.Empty(method.Decisions);
        Assert.Equal(1, method.Complexity);
    }

    private static FlowBlock BlockAt(MethodFlow method, int line) =>
        Assert.Single(method.Blocks, block => block.Line == line && block.Kind is not ("시작" or "종료"));

    private static MethodFlow Analyze(string source)
    {
        var result = new PythonAnalyzer().Analyze([new("short_circuit.py", source)]);
        Assert.Empty(result.Findings);
        Assert.Empty(result.Issues);
        return Assert.Single(result.ControlFlow!);
    }
}
