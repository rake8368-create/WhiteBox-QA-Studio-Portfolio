using TreeSitter;

namespace WhiteBoxQA.Core;

internal sealed class PythonSyntaxDiagnostics(string fileName, string code)
{
    private readonly List<Finding> findings = [];
    private readonly HashSet<int> reportedOffsets = [];

    public IReadOnlyList<Finding> Analyze(Node root)
    {
        if (root.HasError)
        {
            foreach (var node in TreeSitterSupport.Descendants(root, node => node.HasError && !node.IsError && !node.IsMissing))
            {
                if (node.IsMissing)
                    Report(node, $"Python 문법에서 필요한 토큰({node.Type})이 누락됐습니다.");
                else if (node.IsError)
                    Report(node, "Python 파서가 이 위치의 구문을 해석하지 못했습니다.");
            }
        }
        else
        {
            var continuations = code.Contains("\\\n", StringComparison.Ordinal)
                ? TreeSitterSupport.Descendants(root).Where(node => node.Type == "line_continuation")
                    .Select(node => node.EndPosition.Row).ToHashSet()
                : [];
            foreach (var node in TreeSitterSupport.Descendants(root))
            {
                if (node.Type is "module" or "block")
                    CheckIndentation(node, continuations);
                else if (node.Type == "try_statement")
                    CheckHandlers(node);
            }
        }
        return findings;
    }

    private void CheckHandlers(Node statement)
    {
        var clauses = PythonAnalyzer.Statements(statement).ToArray();
        var handlers = clauses.Where(node => node.Type == "except_clause").ToArray();
        if (handlers.Length == 0)
        {
            if (!clauses.Any(node => node.Type == "finally_clause") || clauses.Any(node => node.Type == "else_clause"))
                Report(statement, "try에는 except 또는 finally가 필요하며 else는 except와 함께 사용해야 합니다.");
            return;
        }

        var firstIsStarred = PythonAnalyzer.IsStarredHandler(handlers[0]);
        for (var index = 0; index < handlers.Length; index++)
        {
            var handler = handlers[index];
            var isStarred = PythonAnalyzer.IsStarredHandler(handler);
            var hasType = handler.GetChildForField("value") is not null;
            if (isStarred && !hasType)
                Report(handler, "except*에는 처리할 예외 타입을 지정해야 합니다.");
            else if (isStarred != firstIsStarred)
                Report(handler, "하나의 try에 except와 except*를 함께 사용할 수 없습니다.");
            else if (!hasType && index != handlers.Length - 1)
                Report(handler, "예외 타입을 생략한 except는 마지막 핸들러여야 합니다.");
        }
    }

    private void CheckIndentation(Node suite, HashSet<int> continuations)
    {
        using var statements = TreeSitterSupport.NamedChildren(suite).Where(node => !node.IsExtra).GetEnumerator();
        var header = suite.Type == "block" ? suite.Parent : null;
        if (!statements.MoveNext())
        {
            // The grammar deliberately permits empty suites for editor recovery.
            if (header is not null)
                Report(header, "이 문장에 실행문을 포함한 들여쓰기 블록이 필요합니다.");
            return;
        }

        var first = statements.Current;
        if (header is not null && (suite.PreviousSibling?.EndPosition.Row == first.StartPosition.Row
            || continuations.Contains(first.StartPosition.Row)))
            return; // An inline suite uses the header's logical line.

        var expected = default(Indentation);
        if (header is not null)
        {
            TryIndentAt(first.StartIndex, out expected);
            TryIndentAt(header.StartIndex, out var outer);
            if (expected.Width <= outer.Width || expected.AlternateWidth <= outer.AlternateWidth)
                Report(first, "블록의 첫 실행문을 헤더보다 한 단계 더 들여써야 합니다.");
        }

        do
        {
            var statement = statements.Current;
            if (!continuations.Contains(statement.StartPosition.Row)
                && TryIndentAt(statement.StartIndex, out var indent) && indent != expected)
                Report(statement, "실행문의 들여쓰기가 구문 트리에서 같은 블록에 속한 다른 실행문과 맞지 않습니다.");
        } while (statements.MoveNext());
    }

    private bool TryIndentAt(int index, out Indentation indentation)
    {
        var width = 0;
        var alternateWidth = 0;
        for (var cursor = TreeSitterSupport.LineStart(code, index); cursor < index; cursor++)
        {
            switch (code[cursor])
            {
                case ' ': width++; alternateWidth++; break;
                case '\t': width += 8 - width % 8; alternateWidth++; break;
                case '\f': width = alternateWidth = 0; break;
                default:
                    indentation = new(width, alternateWidth);
                    return false;
            }
        }
        indentation = new(width, alternateWidth);
        return true;
    }

    private void Report(Node node, string description)
    {
        if (reportedOffsets.Add(node.StartIndex))
        {
            findings.Add(TreeSitterSupport.CreateFinding(fileName, code, node, "PY-001", Severity.High,
                "Python 구문 오류", description, "표시된 줄과 바로 앞줄의 괄호, 따옴표 및 들여쓰기를 확인하세요.",
                "구문을 수정한 뒤 이 파일의 예외 처리 규칙과 함수 제어 흐름을 다시 검증하세요."));
        }
    }

    private readonly record struct Indentation(int Width, int AlternateWidth);
}
