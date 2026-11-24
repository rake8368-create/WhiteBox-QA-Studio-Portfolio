using TreeSitter;
using static WhiteBoxQA.Core.TreeSitterSupport;

namespace WhiteBoxQA.Core;

public sealed class PythonAnalyzer : ILanguageAnalyzer
{
    private static readonly Lazy<Language> PythonLanguage = new(() => LoadLanguage("Python"));
    private readonly Func<Parser> createParser;

    public PythonAnalyzer() : this(CreateParser) { }

    internal PythonAnalyzer(Func<Parser> createParser) => this.createParser = createParser;

    public SourceLanguage Language => SourceLanguage.Python;
    public AnalysisCapabilities Capabilities => AnalysisCapabilities.StaticRules | AnalysisCapabilities.ControlFlow;

    public LanguageAnalysis Analyze(IReadOnlyList<SourceDocument> documents)
    {
        if (documents.Count == 0)
            return new(0, [], []) { ControlFlow = [] };

        Parser parser;
        try
        {
            parser = createParser();
        }
        catch (Exception exception) when (IsNativeFailure(exception) || exception is InvalidOperationException)
        {
            var reason = $"Python 파서를 초기화할 수 없습니다 ({FailureName(exception)}). " +
                "배포 파일과 Windows 아키텍처를 확인하세요.";
            return new(0, [], documents.Select(document => new AnalysisIssue(document.FileName, reason)).ToArray()) { ControlFlow = [] };
        }

        using (parser)
        {
            var analyzed = 0;
            var findings = new List<Finding>();
            var issues = new List<AnalysisIssue>();
            var methods = new List<MethodFlow>();
            var controlFlow = new PythonControlFlow();
            foreach (var document in documents)
            {
                // Python treats CR, CRLF and LF as physical line endings.
                var code = document.Code.Replace("\r\n", "\n").Replace('\r', '\n');
                try
                {
                    using var tree = parser.Parse(code);
                    if (tree is null)
                    {
                        parser.Reset();
                        issues.Add(new(document.FileName, "Python 파서가 구문 트리를 생성하지 못했습니다."));
                        continue;
                    }

                    var root = tree.RootNode;
                    var syntax = new PythonSyntaxDiagnostics(document.FileName, code);
                    var syntaxFindings = syntax.Analyze(root);
                    var hasSyntaxErrors = syntaxFindings.Count > 0;
                    var fileMethods = controlFlow.Analyze(document.FileName, code, root, hasSyntaxErrors).ToArray();
                    if (hasSyntaxErrors)
                        findings.AddRange(syntaxFindings);
                    else
                    {
                        var handlerFindings = new List<Finding>();
                        var rethrowRule = new PythonRethrowRule(document.FileName, code, handlerFindings);
                        foreach (var node in Descendants(root))
                            if (node.Type == "except_clause")
                                InspectHandler(document.FileName, code, node, handlerFindings, rethrowRule);
                        findings.AddRange(handlerFindings);
                    }

                    methods.AddRange(fileMethods);
                    analyzed++;
                }
                catch (Exception exception) when (IsNativeFailure(exception))
                {
                    issues.Add(new(document.FileName, $"Python 파서를 실행할 수 없습니다 ({FailureName(exception)})."));
                }
            }
            if (controlFlow.FilesWithOmittedFunctions > 0)
            {
                issues.Add(new("Python 제어 흐름",
                    $"함수 결과 {PythonControlFlow.MaxBatchMethods:N0}개 한도로 Python 파일 {controlFlow.FilesWithOmittedFunctions:N0}개에서 일부 함수의 CFG 결과를 생략했습니다."));
            }
            return new(analyzed, findings, issues) { ControlFlow = methods };
        }
    }

    private static Parser CreateParser()
    {
        var language = PythonLanguage.Value;
        return new Parser(language);
    }

    private static void InspectHandler(string fileName, string code, Node handler, List<Finding> findings, PythonRethrowRule rethrowRule)
    {
        var body = NamedChildren(handler).LastOrDefault(node => node.Type == "block");
        if (body?.Type != "block")
            return;

        var statements = Statements(body).ToArray();
        if (statements.Length > 0 && statements.All(IsInertStatement))
        {
            findings.Add(CreateFinding(fileName, code, handler, "PY-002", Severity.Low, "비어 있는 except",
                "except 본문에는 pass, 생략 기호(…) 또는 평가할 치환식이 없는 문자열만 있습니다.",
                "예상한 예외만 무시하는지 확인하고, 필요한 경우 로그·복구 또는 재전파를 추가하세요.",
                "예외를 의도적으로 무시하는 코드일 수 있어 검토가 필요합니다."));
        }

        if (handler.GetChildForField("value") is null && !IsStarredHandler(handler))
        {
            findings.Add(CreateFinding(fileName, code, handler, "PY-003", Severity.Moderate, "bare except 사용",
                "bare except는 BaseException 전체를 잡으므로 일반 Exception 외의 KeyboardInterrupt와 SystemExit도 처리합니다.",
                "처리할 예외 타입을 명시하세요. 일반 예외를 포괄해야 한다면 except Exception을 검토하세요.",
                "프로그램 종료나 정리를 위해 넓은 범위의 처리가 필요한지 검토하세요."));
        }

        rethrowRule.Inspect(handler, body);
    }

    private static bool IsInertStatement(Node statement)
    {
        var node = UnwrapParentheses(statement);
        return node?.Type switch
        {
            "pass_statement" or "ellipsis" => true,
            "string" or "concatenated_string" => !Descendants(node).Any(child => child.Type == "interpolation"),
            _ => false
        };
    }

    internal static bool StartsWithToken(Node node, string token) => node.FirstChild?.Type == token;

    internal static bool IsStarredHandler(Node node) => node.Children.Any(child => child.Type == "*");

    internal static IEnumerable<Node> Statements(Node root) => NamedChildren(root).Where(node => !node.IsExtra);

    internal static Node? UnwrapParentheses(Node? node)
    {
        while (node?.Type == "parenthesized_expression")
            node = Statements(node).FirstOrDefault();
        return node;
    }
}
