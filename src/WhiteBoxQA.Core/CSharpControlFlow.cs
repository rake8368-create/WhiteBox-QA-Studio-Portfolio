using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FlowAnalysis;
using Microsoft.CodeAnalysis.Operations;
using Microsoft.CodeAnalysis.Text;

namespace WhiteBoxQA.Core;

internal static class CSharpControlFlow
{
    public static IReadOnlyList<MethodFlow> Analyze(SemanticModel model, SyntaxNode root)
    {
        var methods = root.DescendantNodes().OfType<MethodDeclarationSyntax>().ToArray();
        if (methods.Length == 0)
            return [];

        var tree = model.SyntaxTree;
        var source = tree.GetText();
        return methods.Select(method => AnalyzeMethod(tree.FilePath, method, model, source)).ToArray();
    }

    private static MethodFlow AnalyzeMethod(string fileName, MethodDeclarationSyntax method, SemanticModel model,
        SourceText source)
    {
        var start = source.Lines.GetLinePosition(method.SpanStart).Line + 1;
        var end = source.Lines.GetLinePosition(Math.Max(method.SpanStart, method.Span.End - 1)).Line + 1;
        var name = method.Identifier.ValueText;
        MethodFlow Unavailable(string reason) =>
            new(fileName, name, start, end, MethodFlowStatus.Unavailable, null, reason, [], []);

        if (method.ContainsDiagnostics)
            return Unavailable("메서드 선언 또는 본문에 C# 구문 오류가 있습니다.");
        if (method.Body is null && method.ExpressionBody is null)
            return Unavailable("분석할 메서드 본문이 없습니다.");

        try
        {
            name = model.GetDeclaredSymbol(method)?.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat) ?? name;
            var error = model.GetDiagnostics(method.Span).FirstOrDefault(item => item.Severity == DiagnosticSeverity.Error);
            if (error is not null)
                return Unavailable($"메서드의 형식이나 참조를 확인할 수 없습니다 ({error.Id}).");
            if (model.GetOperation(method) is not IMethodBodyOperation body)
                return Unavailable("Roslyn에서 메서드 본문의 연산을 얻을 수 없습니다.");

            var graph = ControlFlowGraph.Create(body);
            return Summarize(fileName, name, start, end, method, graph, source);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or NotSupportedException)
        {
            return Unavailable("Roslyn에서 이 메서드의 제어 흐름 그래프를 만들 수 없습니다.");
        }
    }

    private static MethodFlow Summarize(string fileName, string name, int start, int end,
        MethodDeclarationSyntax method, ControlFlowGraph graph, SourceText source)
    {
        var blocks = new List<FlowBlock>(graph.Blocks.Length);
        var decisions = new List<FlowDecision>();
        var limits = method.DescendantNodes(node => node is not (LocalFunctionStatementSyntax or AnonymousFunctionExpressionSyntax))
            .Select(UnsupportedFlow).OfType<string>().Distinct().ToList();
        if (method.Modifiers.Any(SyntaxKind.AsyncKeyword))
            limits.Add("비동기 상태 전이");

        foreach (var block in graph.Blocks)
        {
            var branch = block.BranchValue;
            var conditionalTarget = block.ConditionalSuccessor?.Destination?.Ordinal;
            var nextTarget = block.FallThroughSuccessor?.Destination?.Ordinal;
            var location = branch?.Syntax ?? block.Operations.FirstOrDefault()?.Syntax;
            int? line = location is not null ? Line(source, location) : block.Kind switch
            {
                BasicBlockKind.Entry => start,
                BasicBlockKind.Exit => end,
                _ => null
            };
            blocks.Add(new(block.Ordinal, block.Kind switch
            {
                BasicBlockKind.Entry => "시작",
                BasicBlockKind.Exit => "종료",
                _ => "기본 블록"
            }, line, block.IsReachable, conditionalTarget, nextTarget));

            if (branch is null || block.ConditionKind == ControlFlowConditionKind.None)
                continue;

            var (kind, anchor) = Describe(branch);
            var whenTrue = block.ConditionKind == ControlFlowConditionKind.WhenTrue;
            var include = kind is not null && block.IsReachable && conditionalTarget.HasValue && nextTarget.HasValue;
            if (kind is null && block.IsReachable && !IsImplicitCleanup(block, branch)
                && !limits.Contains("기타 CFG 조건"))
                limits.Add("기타 CFG 조건");
            decisions.Add(new(block.Ordinal, kind ?? "기타 CFG 조건", Line(source, anchor),
                Excerpt(source, anchor), whenTrue ? conditionalTarget : nextTarget,
                whenTrue ? nextTarget : conditionalTarget, block.IsReachable, include));
        }

        return new(fileName, name, start, end, limits.Count == 0 ? MethodFlowStatus.Analyzed : MethodFlowStatus.Limited,
            1 + decisions.Count(item => item.IncludedInComplexity),
            limits.Count == 0 ? null : $"복잡도 집계에서 제외된 흐름 · {string.Join(", ", limits)}",
            blocks.ToArray(), decisions.ToArray());
    }

    private static (string? Kind, SyntaxNode Anchor) Describe(IOperation operation)
    {
        var syntax = operation.Syntax;
        if (operation is IInvocationOperation { IsImplicit: true, TargetMethod.Name: "MoveNext" }
            && syntax.FirstAncestorOrSelf<ForEachStatementSyntax>() is { AwaitKeyword.RawKind: 0 } loop)
            return ("foreach 반복", loop);

        if ((operation.Kind == OperationKind.IsNull && operation.IsImplicit)
            || syntax.AncestorsAndSelf().Any(node => node is CatchFilterClauseSyntax or WhenClauseSyntax))
            return (null, syntax);

        foreach (var node in syntax.AncestorsAndSelf())
        {
            if (UnsupportedFlow(node) is not null && node is not TryStatementSyntax)
                break;

            switch (node)
            {
                case BinaryExpressionSyntax binary when
                    (binary.IsKind(SyntaxKind.LogicalAndExpression) || binary.IsKind(SyntaxKind.LogicalOrExpression))
                    && binary.Left.Span.Contains(syntax.Span):
                    return ($"{binary.OperatorToken.Text} 단락 평가", syntax);
                case IfStatementSyntax conditional when conditional.Condition.Span.Contains(syntax.Span):
                    return ("if 조건", syntax);
                case ForStatementSyntax { Condition: { } condition } when condition.Span.Contains(syntax.Span):
                    return ("for 반복", syntax);
                case WhileStatementSyntax statement when statement.Condition.Span.Contains(syntax.Span):
                    return ("while 반복", syntax);
                case DoStatementSyntax statement when statement.Condition.Span.Contains(syntax.Span):
                    return ("do/while 반복", syntax);
                case CaseSwitchLabelSyntax label:
                    return ("switch 항목", label);
                case ConditionalExpressionSyntax expression when expression.Condition.Span.Contains(syntax.Span):
                    return ("삼항 조건", syntax);
            }
        }

        return (null, syntax);
    }

    private static bool IsImplicitCleanup(BasicBlock block, IOperation branch)
    {
        if (!branch.IsImplicit)
            return false;

        for (var region = block.EnclosingRegion; region is not null; region = region.EnclosingRegion)
        {
            if (region.Kind == ControlFlowRegionKind.Finally)
                return true;
        }

        return false;
    }

    private static string? UnsupportedFlow(SyntaxNode node) => node switch
    {
        TryStatementSyntax => "예외 처리 전이",
        CatchFilterClauseSyntax => "예외 필터",
        SwitchExpressionSyntax or CasePatternSwitchLabelSyntax => "패턴 기반 switch",
        ForEachVariableStatementSyntax or ForEachStatementSyntax { AwaitKeyword.RawKind: not 0 } => "특수 foreach",
        ConditionalAccessExpressionSyntax => "null 조건부 접근",
        BinaryExpressionSyntax binary when binary.IsKind(SyntaxKind.CoalesceExpression) => "null 병합",
        AssignmentExpressionSyntax assignment when assignment.IsKind(SyntaxKind.CoalesceAssignmentExpression) => "null 병합 대입",
        GotoStatementSyntax or YieldStatementSyntax => "goto 또는 yield 전이",
        _ => null
    };

    private static int Line(SourceText source, SyntaxNode node) =>
        source.Lines.GetLinePosition(node.SpanStart).Line + 1;

    private static string Excerpt(SourceText source, SyntaxNode node)
    {
        var span = node is ForEachStatementSyntax loop
            ? TextSpan.FromBounds(loop.SpanStart, loop.CloseParenToken.Span.End)
            : node.Span;
        const int maxLength = 160;
        var excerpt = source.ToString(new TextSpan(span.Start, Math.Min(maxLength, span.Length)))
            .Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ').Trim();
        return span.Length > maxLength ? excerpt + "…" : excerpt;
    }
}
