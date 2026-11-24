using TreeSitter;
using static WhiteBoxQA.Core.TreeSitterSupport;

namespace WhiteBoxQA.Core;

public sealed class JavaScriptAnalyzer : ILanguageAnalyzer
{
    internal const int MaxInputCharactersPerFile = 2 * 1024 * 1024;
    internal const int MaxInputCharactersPerBatch = 32 * 1024 * 1024;
    internal const int MaxSyntaxFindingsPerFile = 100;
    internal const int MaxSyntaxFindingsPerBatch = 2_000;
    internal const int MaxRuleFindingsPerFile = 500;
    internal const int MaxRuleFindingsPerBatch = 5_000;

    private static readonly Lazy<Language> JavaScriptLanguage = new(() => LoadLanguage("JavaScript"));
    private readonly Func<Parser> createParser;
    private readonly Func<Parser, string, Tree?> parse;

    public JavaScriptAnalyzer() : this(null) { }

    internal JavaScriptAnalyzer(Func<Parser>? createParser, Func<Parser, string, Tree?>? parse = null)
    {
        this.createParser = createParser ?? (() => new Parser(JavaScriptLanguage.Value));
        this.parse = parse ?? ((parser, code) => parser.Parse(code));
    }

    public SourceLanguage Language => SourceLanguage.JavaScript;
    public AnalysisCapabilities Capabilities => AnalysisCapabilities.StaticRules;

    public LanguageAnalysis Analyze(IReadOnlyList<SourceDocument> documents)
    {
        var findings = new List<Finding>();
        var issues = new List<AnalysisIssue>();
        var analyzed = 0;
        var remainingCharacters = MaxInputCharactersPerBatch;
        var remainingSyntax = MaxSyntaxFindingsPerBatch;
        var remainingRules = MaxRuleFindingsPerBatch;

        foreach (var document in documents)
        {
            var fileName = document.FileName;
            var code = document.Code;
            if (code.Length > MaxInputCharactersPerFile)
            {
                issues.Add(new(fileName, $"JavaScript 직접 분석 입력이 파일당 {MaxInputCharactersPerFile:N0} UTF-16 코드 단위 제한을 넘어 건너뛰었습니다."));
                continue;
            }
            if (code.Length > remainingCharacters)
            {
                issues.Add(new(fileName, $"JavaScript 직접 분석 입력이 전체 {MaxInputCharactersPerBatch:N0} UTF-16 코드 단위 제한을 넘어 건너뛰었습니다."));
                continue;
            }
            remainingCharacters -= code.Length;

            Parser parser;
            try
            {
                parser = createParser();
            }
            catch (Exception exception) when (IsNativeFailure(exception) || exception is InvalidOperationException)
            {
                issues.Add(new(fileName, $"JavaScript 파서를 초기화할 수 없습니다 ({FailureName(exception)}). 배포 파일과 Windows 아키텍처를 확인하세요."));
                continue;
            }

            var fileFindings = new List<Finding>();
            bool syntaxErrors;
            bool omitted;
            try
            {
                using (parser)
                {
                    using var tree = parse(parser, code);
                    if (tree is null)
                    {
                        issues.Add(new(fileName, "JavaScript 파서가 구문 트리를 생성하지 못했습니다."));
                        continue;
                    }

                    var root = tree.RootNode;
                    syntaxErrors = root.HasError;
                    omitted = syntaxErrors
                        ? Collect(SyntaxNodes(root), Math.Min(MaxSyntaxFindingsPerFile, remainingSyntax), fileFindings,
                            node => SyntaxFinding(fileName, code, node))
                        : Collect(EmptyCatches(root), Math.Min(MaxRuleFindingsPerFile, remainingRules), fileFindings,
                            node => EmptyCatchFinding(fileName, code, node));
                }
            }
            catch (Exception exception) when (IsNativeFailure(exception) || exception is InvalidOperationException)
            {
                issues.Add(new(fileName, $"JavaScript 파서를 실행할 수 없습니다 ({FailureName(exception)})."));
                continue;
            }

            analyzed++;
            findings.AddRange(fileFindings);
            if (syntaxErrors)
            {
                remainingSyntax -= fileFindings.Count;
                issues.Add(new(fileName, "Tree-sitter 구문 오류 또는 누락 토큰이 있어 이 파일의 JS-002 검토를 생략했습니다."));
                if (omitted)
                    issues.Add(new(fileName, $"JavaScript 구문 진단 한도(파일당 {MaxSyntaxFindingsPerFile:N0}개, 분석당 {MaxSyntaxFindingsPerBatch:N0}개)에 도달하여 추가 진단을 생략했습니다."));
            }
            else
            {
                remainingRules -= fileFindings.Count;
                if (omitted)
                    issues.Add(new(fileName, $"JavaScript 정적 규칙 진단 한도(파일당 {MaxRuleFindingsPerFile:N0}개, 분석당 {MaxRuleFindingsPerBatch:N0}개)에 도달하여 추가 진단을 생략했습니다."));
            }
        }

        return new(analyzed, findings, issues);
    }

    private static bool Collect(IEnumerable<Node> nodes, int limit, List<Finding> findings, Func<Node, Finding> createFinding)
    {
        foreach (var node in nodes)
        {
            if (findings.Count == limit)
                return true;
            findings.Add(createFinding(node));
        }
        return false;
    }

    private static IEnumerable<Node> SyntaxNodes(Node root) =>
        Descendants(root, node => node.HasError && !node.IsMissing)
            .Where(node => node.IsError || node.IsMissing)
            .DistinctBy(node => node.StartIndex);

    private static IEnumerable<Node> EmptyCatches(Node root) => Descendants(root)
        .Where(node => node.Type == "catch_clause" && node.GetChildForField("body") is { Type: "statement_block" } body
            && Descendants(body, child => child.Type == "statement_block").All(child =>
                child.Type is "statement_block" or "{" or "}" or "comment" or "html_comment" or "empty_statement"));

    private static Finding SyntaxFinding(string fileName, string code, Node node) =>
        CreateFinding(fileName, code, node, "JS-001", Severity.High, "JavaScript 구문 오류",
            node.IsMissing ? $"필요한 토큰({node.Type})이 누락됐습니다." : "JavaScript 파서가 이 위치의 구문을 해석하지 못했습니다.",
            "표시된 줄과 앞줄의 괄호, 구분 기호, 선언 또는 JSX 태그를 확인하세요.",
            "위치는 Tree-sitter의 구문 복구 결과입니다. 파서가 지원하지 않는 문법에서도 이 진단이 발생할 수 있습니다.");

    private static Finding EmptyCatchFinding(string fileName, string code, Node node) =>
        CreateFinding(fileName, code, node, "JS-002", Severity.Low, "비어 있는 catch",
            "catch 본문에 실행문이나 지역 선언이 없습니다. 주석, 빈 문장과 빈 중첩 블록은 제외합니다.",
            "예상한 예외만 무시하는지 확인하고 필요한 경우 기록, 복구 또는 재전파를 추가하세요.",
            "예외를 의도적으로 무시하는 코드일 수 있습니다.");
}
