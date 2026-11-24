using TreeSitter;

namespace WhiteBoxQA.Core;

internal sealed class PythonControlFlow
{
    internal const int MaxNesting = 96;
    internal const int MaxBatchMethods = 20_000;
    private const int MaxFunctionBlocks = 4_096;
    private const int MaxBatchBlocks = 100_000;
    private int remainingMethods = MaxBatchMethods;
    private int remainingBlocks = MaxBatchBlocks;

    public int FilesWithOmittedFunctions { get; private set; }

    public IEnumerable<MethodFlow> Analyze(string fileName, string code, Node root, bool hasSyntaxErrors)
    {
        var isStub = Path.GetExtension(fileName).Equals(".pyi", StringComparison.OrdinalIgnoreCase);
        var scopes = new Stack<(string Name, int End)>();
        foreach (var node in TreeSitterSupport.Descendants(root, node => !node.IsError && !node.IsMissing))
        {
            if (node.Type is not ("function_definition" or "class_definition")
                || node.GetChildForField("name") is not { IsMissing: false } name)
                continue;

            var start = node.StartIndex;
            while (scopes.TryPeek(out var previous) && start >= previous.End)
                scopes.Pop();
            var qualifiedName = scopes.TryPeek(out var parent) ? $"{parent.Name}.{name.Text}" : name.Text;
            scopes.Push((qualifiedName, node.EndIndex));
            if (node.Type == "class_definition")
                continue;
            if (remainingMethods == 0)
            {
                FilesWithOmittedFunctions++;
                yield break;
            }
            remainingMethods--;
            var startLine = node.StartPosition.Row + 1;
            var end = node.EndPosition;
            var endLine = Math.Max(startLine, end.Row + (end.Column == 0 ? 0 : 1));
            MethodFlow Unavailable(string reason) => new(fileName, qualifiedName, startLine, endLine,
                MethodFlowStatus.Unavailable, null, reason, [], []);

            if (hasSyntaxErrors || node.GetChildForField("body") is not { } body)
            {
                yield return Unavailable("파일에 구문 오류가 있어 함수의 제어 흐름을 만들 수 없습니다.");
                continue;
            }
            if (isStub)
            {
                yield return Unavailable("Python 스텁(.pyi)은 타입 선언을 제공하므로 실행 흐름 분석 범위에서 제외됩니다.");
                continue;
            }

            var reasons = UnsupportedReasons(node, body);
            if (reasons.Count != 0)
            {
                yield return Unavailable(string.Join(" · ", reasons));
                continue;
            }

            var graph = new Graph(code, remainingBlocks);
            MethodFlow result;
            try
            {
                result = graph.Build(fileName, qualifiedName, node, body, startLine, endLine);
                remainingBlocks -= result.Blocks.Count;
            }
            catch (FlowLimitException exception)
            {
                result = Unavailable(exception.Message);
            }
            yield return result;
        }
    }

    private static HashSet<string> UnsupportedReasons(Node function, Node body)
    {
        var reasons = new HashSet<string>(StringComparer.Ordinal);
        if (PythonAnalyzer.StartsWithToken(function, "async"))
            reasons.Add("비동기 함수 및 await 흐름 미지원");

        foreach (var node in ExecutableNodes(body))
        {
            var reason = node.Type switch
            {
                "try_statement" or "raise_statement" => "예외 처리 및 예외 전파 흐름 미지원",
                "match_statement" => "패턴 매칭 흐름 미지원",
                "with_statement" => "with의 진입·정리 흐름 미지원",
                "await" => "비동기 함수 및 await 흐름 미지원",
                "for_statement" or "for_in_clause" when PythonAnalyzer.StartsWithToken(node, "async")
                    => "비동기 반복 흐름 미지원",
                "yield" => "제너레이터의 중단·재개 흐름 미지원",
                "class_definition" => "함수 안에서 실행되는 클래스 본문 흐름 미지원",
                "exec_statement" or "print_statement" => "구형 Python 문장 흐름 미지원",
                _ => null
            };
            if (reason is not null)
                reasons.Add(reason);
        }
        return reasons;
    }

    private static IEnumerable<Node> ExecutableNodes(Node root) => TreeSitterSupport.Descendants(root, node =>
        node == root || node.Type != "lambda"
            && (node.Type != "block" || node.Parent?.Type is not ("function_definition" or "class_definition")));

    private static HashSet<string> ExpressionLimits(Node node)
    {
        var reasons = new HashSet<string>(StringComparer.Ordinal);
        foreach (var part in ExecutableNodes(node))
        {
            if (ExpressionLimit(part) is { } reason)
                reasons.Add(reason);
        }
        return reasons;
    }

    private static string? ExpressionLimit(Node node) => node.Type switch
    {
        "boolean_operator" => "and/or 단락 평가 제외",
        "comparison_operator" when node.GetChildrenForField("operators").Count > 1 => "연쇄 비교 단락 평가 제외",
        "conditional_expression" => "조건 표현식 내부 분기 제외",
        "list_comprehension" or "set_comprehension" or "dictionary_comprehension" or "generator_expression"
            => "컴프리헨션·제너레이터 표현식 내부 분기 제외",
        "assert_statement" => "assert의 검사 및 예외 경로 제외",
        _ => null
    };

    private static bool IsSimpleOperand(Node node) =>
        node.Type is ("identifier" or "call" or "attribute" or "subscript" or "comparison_operator" or "true" or "false")
        && !TreeSitterSupport.Descendants(node).Any(part => part.Type is "not_operator" or "lambda" || ExpressionLimit(part) is not null);

    private sealed class Graph(string code, int blockBudget)
    {
        private readonly List<Block> blocks = [];
        private readonly HashSet<string> limits = new(StringComparer.Ordinal);
        private Block exit = null!;

        public MethodFlow Build(string fileName, string name, Node definition, Node body, int startLine, int endLine)
        {
            var entry = Add(definition, "시작");
            exit = Add(definition, "종료", endLine);
            entry.Next = Suite(body, exit, null, 0);

            var ordered = blocks.Where(block => block != entry && block != exit)
                .OrderBy(block => block.Offset).Prepend(entry).Append(exit).ToArray();
            for (var index = 0; index < ordered.Length; index++)
                ordered[index].Ordinal = index;

            var pending = new Stack<Block>();
            pending.Push(entry);
            while (pending.TryPop(out var current))
            {
                if (current.Reachable)
                    continue;
                current.Reachable = true;
                if (current.Branch is { } whenTrue)
                    pending.Push(whenTrue);
                if (current.Next is { } next)
                    pending.Push(next);
            }

            var decisions = ordered.Where(block => block.Condition is not null).Select(block => new FlowDecision(
                block.Ordinal, block.Kind, block.Line, block.Condition!, block.Branch?.Ordinal, block.Next?.Ordinal,
                block.Reachable, block.Reachable && block.Counts) { IsConstant = block.KnownTruth.HasValue }).ToArray();
            var items = ordered.Select(block => new FlowBlock(block.Ordinal, block.Kind, block.Line, block.Reachable,
                block.Branch?.Ordinal, block.Next?.Ordinal)).ToArray();
            var status = limits.Count == 0 ? MethodFlowStatus.Analyzed : MethodFlowStatus.Limited;
            var limitation = limits.Count == 0 ? null : string.Join(" · ", limits);
            return new(fileName, name, startLine, endLine, status,
                1 + decisions.Count(decision => decision.IncludedInComplexity), limitation, items, decisions);
        }

        private Block Suite(Node? suite, Block next, Loop? loop, int depth)
        {
            if (depth > MaxNesting)
                throw new FlowLimitException($"함수의 CFG 중첩 한도({MaxNesting}단계)를 초과했습니다.");
            if (suite is null)
                return next;
            foreach (var statement in PythonAnalyzer.Statements(suite).Reverse())
                next = Statement(statement, next, loop, depth);
            return next;
        }

        private Block Statement(Node node, Block next, Loop? loop, int depth)
        {
            if (node.Type == "if_statement")
                return If(node, next, loop, depth);
            if (node.Type is "while_statement" or "for_statement")
                return LoopStatement(node, next, loop, depth);

            limits.UnionWith(ExpressionLimits(node));
            var kind = node.Type switch
            {
                "return_statement" => "return",
                "break_statement" => "break",
                "continue_statement" => "continue",
                "function_definition" or "decorated_definition" => "함수 선언",
                _ => "실행문"
            };
            var block = Add(node, kind);
            block.Next = kind switch
            {
                "return" => exit,
                "break" => loop?.Exit ?? throw new FlowLimitException("반복 본문 밖의 break를 연결할 수 없습니다."),
                "continue" => loop?.Continue ?? throw new FlowLimitException("반복 본문 밖의 continue를 연결할 수 없습니다."),
                _ => next
            };
            return block;
        }

        private Block If(Node node, Block next, Loop? loop, int depth)
        {
            var otherwise = next;
            foreach (var clause in node.GetChildrenForField("alternative").Reverse())
            {
                otherwise = clause.Type == "else_clause"
                    ? Suite(clause.GetChildForField("body"), next, loop, depth + 1)
                    : Condition(clause, "elif", Suite(RequireField(clause, "consequence"), next, loop, depth + 1), otherwise);
            }
            return Condition(node, "if", Suite(RequireField(node, "consequence"), next, loop, depth + 1), otherwise);
        }

        private Block LoopStatement(Node node, Block next, Loop? outerLoop, int depth)
        {
            var exhausted = Suite(node.GetChildForField("alternative")?.GetChildForField("body"), next, outerLoop, depth + 1);
            var suite = RequireField(node, "body");
            if (node.Type == "while_statement")
            {
                var condition = PrepareCondition(node, "while");
                var entry = condition.Left;
                var loopBody = Suite(suite, entry, new(next, entry), depth + 1);
                condition.Connect(loopBody, exhausted);
                return entry;
            }

            var init = Add(node, "for 초기화");
            var test = Add(node, "for");
            var body = Suite(suite, test, new(next, test), depth + 1);
            var iterable = RequireField(node, "right");
            limits.UnionWith(ExpressionLimits(iterable));
            limits.UnionWith(ExpressionLimits(RequireField(node, "left")));
            init.Next = test;
            test.Condition = $"다음 항목 · {Snippet(iterable)}";
            test.Branch = body;
            test.Next = exhausted;
            test.Counts = true;
            return init;
        }

        private Block Condition(Node node, string kind, Block whenTrue, Block whenFalse)
        {
            var condition = PrepareCondition(node, kind);
            condition.Connect(whenTrue, whenFalse);
            return condition.Left;
        }

        private ConditionTest PrepareCondition(Node header, string kind)
        {
            var condition = RequireField(header, "condition");
            var expression = PythonAnalyzer.UnwrapParentheses(condition) ?? condition;
            if (expression.Type == "boolean_operator"
                && expression.GetChildForField("operator")?.Type is ("and" or "or") and var operation
                && PythonAnalyzer.UnwrapParentheses(expression.GetChildForField("left")) is { } left
                && PythonAnalyzer.UnwrapParentheses(expression.GetChildForField("right")) is { } right
                && IsSimpleOperand(left) && IsSimpleOperand(right))
            {
                return new(Decision(left, $"{operation} 왼쪽", left, left.Type, true),
                    Decision(right, $"{operation} 오른쪽", right, right.Type, true), operation == "and");
            }

            var omitted = ExpressionLimits(condition);
            limits.UnionWith(omitted);
            return new(Decision(header, kind, condition, expression.Type, omitted.Count == 0));
        }

        private Block Decision(Node source, string kind, Node expression, string expressionType, bool counts)
        {
            var block = Add(source, kind);
            block.Condition = Snippet(expression);
            block.KnownTruth = expressionType switch { "true" => true, "false" => false, _ => null };
            block.Counts = counts && block.KnownTruth is null;
            return block;
        }

        private string Snippet(Node node)
        {
            var start = node.StartIndex;
            var end = Math.Min(node.EndIndex, start + 120);
            if (end < node.EndIndex && char.IsLowSurrogate(code[end]))
                end--;
            return code[start..end].Replace('\n', ' ').Trim() + (end < node.EndIndex ? "…" : "");
        }

        private static Node RequireField(Node node, string field) => node.GetChildForField(field)
            ?? throw new FlowLimitException("제어 흐름에 필요한 구문 트리 필드를 확인하지 못했습니다.");

        private Block Add(Node node, string kind, int? line = null)
        {
            if (blocks.Count >= MaxFunctionBlocks)
                throw new FlowLimitException($"함수의 CFG 블록 한도({MaxFunctionBlocks:N0}개)를 초과했습니다.");
            if (blocks.Count >= blockBudget)
                throw new FlowLimitException($"Python 분석의 전체 CFG 블록 한도({MaxBatchBlocks:N0}개)에서 이 함수의 그래프를 완료할 수 없습니다.");
            var block = new Block(node.StartIndex, line ?? node.StartPosition.Row + 1, kind);
            blocks.Add(block);
            return block;
        }
    }

    private sealed class Block(int offset, int line, string kind)
    {
        public int Offset { get; } = offset;
        public int Line { get; } = line;
        public string Kind { get; } = kind;
        public int Ordinal { get; set; }
        public bool Reachable { get; set; }
        public Block? Branch { get; set; }
        public Block? Next { get; set; }
        public string? Condition { get; set; }
        public bool? KnownTruth { get; set; }
        public bool Counts { get; set; }

        public void ConnectDecision(Block whenTrue, Block whenFalse)
        {
            Branch = KnownTruth == false ? null : whenTrue;
            Next = KnownTruth == true ? null : whenFalse;
        }
    }

    private readonly record struct ConditionTest(Block Left, Block? Right = null, bool IsAnd = false)
    {
        public void Connect(Block whenTrue, Block whenFalse)
        {
            if (Right is null)
            {
                Left.ConnectDecision(whenTrue, whenFalse);
                return;
            }

            Right.ConnectDecision(whenTrue, whenFalse);
            // Branch directly to the outcome on the short-circuiting edge.
            Left.ConnectDecision(IsAnd ? Right : whenTrue, IsAnd ? whenFalse : Right);
        }
    }

    private sealed record Loop(Block Exit, Block Continue);
    private sealed class FlowLimitException(string message) : Exception(message);
}
