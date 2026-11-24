using System.Text;
using TreeSitter;
using static WhiteBoxQA.Core.PythonAnalyzer;
using static WhiteBoxQA.Core.TreeSitterSupport;

namespace WhiteBoxQA.Core;

internal sealed class PythonRethrowRule(string fileName, string code, List<Finding> findings)
{
    public void Inspect(Node handler, Node body)
    {
        if (IsStarredHandler(handler))
            return;
        var value = handler.GetChildForField("value");
        var alias = value?.Type == "as_pattern" ? value.GetChildForField("alias") : handler.GetChildForField("alias");
        var name = alias?.Type == "as_pattern_target" ? Statements(alias).FirstOrDefault() : alias;
        if (name?.Type == "identifier")
            InspectSuite(body, IdentifierName(name), 0);
    }

    private void InspectSuite(Node? body, string name, int depth)
    {
        if (body is null || depth > PythonControlFlow.MaxNesting)
            return;

        foreach (var statement in Statements(body))
        {
            switch (statement.Type)
            {
                case "raise_statement":
                    var operand = UnwrapParentheses(Statements(statement).FirstOrDefault());
                    if (statement.GetChildForField("cause") is null && IsIdentifier(operand, name))
                    {
                        findings.Add(CreateFinding(fileName, code, statement, "PY-004", Severity.Low, "잡은 예외 변수 재던지기",
                            "같은 except에서 잡은 예외 변수를 raise에 직접 지정해 현재 위치가 traceback에 추가될 수 있습니다.",
                            "현재 처리 중인 예외를 그대로 재전파하려는 경우 변수 없는 raise를 검토하세요.",
                            "의도한 traceback과 예외 전파 동작을 확인하세요."));
                    }
                    return;
                case "break_statement" or "continue_statement" or "return_statement":
                    return;
                case "if_statement":
                    InspectAlternatives(statement, name, depth + 1);
                    break;
            }

            if (MayRebind(statement, name))
                return;
        }
    }

    private void InspectAlternatives(Node statement, string name, int depth)
    {
        // Siblings are mutually exclusive. A write in a condition can also affect later clauses.
        foreach (var clause in statement.GetChildrenForField("alternative").Prepend(statement))
        {
            var condition = clause.GetChildForField("condition");
            if (condition is not null && MayRebind(condition, name))
                return;

            var constant = UnwrapParentheses(condition)?.Type;
            var field = clause.Type == "else_clause" ? "body" : "consequence";
            if (constant != "false")
                InspectSuite(clause.GetChildForField(field), name, depth);
            if (constant == "true")
                return;
        }
    }

    private static bool MayRebind(Node statement, string name)
    {
        foreach (var node in Descendants(statement, part => part.Type != "lambda"
            && (part.Type != "block" || part.Parent?.Type != "function_definition")))
        {
            if (node.Type is "import_statement" or "import_from_statement" or "future_import_statement")
            {
                if (Statements(node).Any(part => part.Type == "wildcard_import")
                    || node.GetChildrenForField("name").Any(import => IsIdentifier(import.Type == "aliased_import"
                        ? import.GetChildForField("alias") : import.FirstNamedChild, name)))
                    return true;
                continue;
            }

            if (node.Type == "function_definition" && MayWriteOuterName(node, name))
                return true;
            if (node.Type == "lambda" && node.GetChildForField("parameters") is { } parameters && MayRebind(parameters, name))
                return true;

            var target = node.Type switch
            {
                "assignment" or "augmented_assignment" or "for_statement" or "type_alias_statement" => node.GetChildForField("left"),
                "named_expression" or "function_definition" or "class_definition" => node.GetChildForField("name"),
                "as_pattern" or "except_clause" => node.GetChildForField("alias"),
                "delete_statement" => node,
                _ => null
            };
            if (BindsName(target, name))
                return true;

            // A match captures names with pattern-specific syntax. Do not infer later alias identity.
            if (node.Type is "case_pattern" or "global_statement" or "nonlocal_statement"
                && Descendants(node).Any(part => IsIdentifier(part, name)))
                return true;
        }
        return false;
    }

    private static bool MayWriteOuterName(Node function, string name) =>
        function.GetChildForField("body") is { } body && Descendants(body).Any(node =>
            node.Type is "global_statement" or "nonlocal_statement" && Statements(node).Any(part => IsIdentifier(part, name)));

    private static bool BindsName(Node? target, string name)
    {
        if (target is null)
            return false;
        return Descendants(target, part => part.Type is "tuple" or "list" or "tuple_pattern" or "list_pattern"
            or "list_splat_pattern" or "list_splat" or "parenthesized_expression" or "expression_list" or "pattern_list"
            or "as_pattern_target" or "delete_statement" or "type" or "generic_type")
            .Any(part => IsIdentifier(part, name));
    }

    private static bool IsIdentifier(Node? node, string name) => node?.Type == "identifier" && IdentifierName(node) == name;

    private static string IdentifierName(Node node) => node.Text.Normalize(NormalizationForm.FormKC);
}
