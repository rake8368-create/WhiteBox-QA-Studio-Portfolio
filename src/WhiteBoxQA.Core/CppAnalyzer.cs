using TreeSitter;
using static WhiteBoxQA.Core.TreeSitterSupport;

namespace WhiteBoxQA.Core;

public sealed class CppAnalyzer : ILanguageAnalyzer
{
    internal const int MaxInputCharactersPerFile = 2 * 1024 * 1024;
    internal const int MaxInputCharactersPerBatch = 32 * 1024 * 1024;
    internal const int MaxSyntaxFindingsPerFile = 100;
    internal const int MaxSyntaxFindingsPerBatch = 2_000;
    internal const int MaxRuleFindingsPerFile = 500;
    internal const int MaxRuleFindingsPerBatch = 5_000;

    private const string InputLimitCaution = "이 입력 제한은 네이티브 파싱 시간과 메모리의 하드 상한을 보장하지 않습니다.";
    private static readonly Lazy<Language> CppLanguage = new(() => LoadLanguage("Cpp"));
    private readonly Func<Parser> createParser;
    private readonly Func<Parser, string, Tree?> parse;

    public CppAnalyzer() : this(null) { }

    internal CppAnalyzer(Func<Parser>? createParser, Func<Parser, string, Tree?>? parse = null)
    {
        this.createParser = createParser ?? (() => new Parser(CppLanguage.Value));
        this.parse = parse ?? ((parser, code) => parser.Parse(code));
    }

    public SourceLanguage Language => SourceLanguage.Cpp;
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
                issues.Add(new(fileName, $"C++ 직접 분석 입력이 파일당 {MaxInputCharactersPerFile:N0} UTF-16 코드 단위 제한을 넘어 건너뛰었습니다. {InputLimitCaution}"));
                continue;
            }
            if (code.Length > remainingCharacters)
            {
                issues.Add(new(fileName, $"C++ 직접 분석 입력이 전체 {MaxInputCharactersPerBatch:N0} UTF-16 코드 단위 제한을 넘어 건너뛰었습니다. {InputLimitCaution}"));
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
                issues.Add(new(fileName, $"C++ 파서를 초기화할 수 없습니다 ({FailureName(exception)}). 배포 파일과 Windows 아키텍처를 확인하세요."));
                continue;
            }

            var fileFindings = new List<Finding>();
            bool syntaxErrors;
            bool limitReached;
            var preprocessorOmissions = 0;
            try
            {
                using (parser)
                {
                    using var tree = parse(parser, code);
                    if (tree is null)
                    {
                        issues.Add(new(fileName, "C++ 파서가 구문 트리를 생성하지 못했습니다."));
                        continue;
                    }

                    var root = tree.RootNode;
                    syntaxErrors = root.HasError;
                    if (syntaxErrors)
                        limitReached = CollectSyntaxFindings(root, document, Math.Min(MaxSyntaxFindingsPerFile, remainingSyntax), fileFindings);
                    else
                        (preprocessorOmissions, limitReached) = CollectCatchFindings(root, document,
                            Math.Min(MaxRuleFindingsPerFile, remainingRules), fileFindings);
                }
            }
            catch (Exception exception) when (IsNativeFailure(exception) || exception is InvalidOperationException)
            {
                issues.Add(new(fileName, $"C++ 파서를 실행할 수 없습니다 ({FailureName(exception)})."));
                continue;
            }

            analyzed++;
            findings.AddRange(fileFindings);
            if (syntaxErrors)
            {
                remainingSyntax -= fileFindings.Count;
                issues.Add(new(fileName, "Tree-sitter 구문 오류 또는 누락 토큰이 있어 이 파일의 CPP-002 검토를 생략했습니다."));
                if (limitReached)
                    issues.Add(new(fileName, $"C++ 구문 진단 한도(파일당 {MaxSyntaxFindingsPerFile:N0}개, 분석당 {MaxSyntaxFindingsPerBatch:N0}개)에 도달하여 추가 진단을 생략했습니다."));
            }
            else
            {
                remainingRules -= fileFindings.Count;
                if (preprocessorOmissions > 0)
                    issues.Add(new(fileName, $"조건부 전처리 분기에 속하거나 본문에 전처리 지시문이 있는 catch {preprocessorOmissions:N0}개의 CPP-002 검토를 생략했습니다. 활성 분기 선택과 매크로 확장은 수행하지 않습니다."));
                if (limitReached)
                    issues.Add(new(fileName, $"C++ 정적 규칙 진단 한도(파일당 {MaxRuleFindingsPerFile:N0}개, 분석당 {MaxRuleFindingsPerBatch:N0}개)에 도달하여 추가 진단을 생략했습니다."));
            }
        }

        return new(analyzed, findings, issues);
    }

    private static bool CollectSyntaxFindings(Node root, SourceDocument document, int limit, List<Finding> findings)
    {
        var syntaxNodes = Descendants(root, node => node.HasError && !node.IsMissing)
            .Where(node => node.IsError || node.IsMissing)
            .DistinctBy(node => node.StartIndex);
        foreach (var node in syntaxNodes)
        {
            if (findings.Count == limit)
                return true;
            findings.Add(SyntaxFinding(document, node));
        }
        return false;
    }

    private static (int PreprocessorOmissions, bool LimitReached) CollectCatchFindings(
        Node root, SourceDocument document, int limit, List<Finding> findings)
    {
        var catches = new List<(Node Clause, Node Body)>();
        var directiveStarts = new List<int>();
        var conditionalEnd = 0;
        var preprocessorOmissions = 0;

        // Source-order traversal keeps directive offsets sorted and lets nested catch bodies
        // share the same directive lookup instead of repeatedly scanning their subtrees.
        foreach (var node in Descendants(root))
        {
            var type = node.Type;
            if (type.StartsWith("preproc_", StringComparison.Ordinal))
            {
                directiveStarts.Add(node.StartIndex);
                if (type is "preproc_if" or "preproc_ifdef" or "preproc_elif" or "preproc_elifdef" or "preproc_else")
                    conditionalEnd = Math.Max(conditionalEnd, node.EndIndex);
            }
            else if (type == "catch_clause" && node.GetChildForField("body") is { Type: "compound_statement" } body)
            {
                if (node.StartIndex < conditionalEnd)
                    preprocessorOmissions++;
                else
                    catches.Add((node, body));
            }
        }

        var limitReached = false;
        foreach (var (clause, body) in catches)
        {
            var directive = directiveStarts.BinarySearch(body.StartIndex);
            if (directive < 0)
                directive = ~directive;

            if (directive < directiveStarts.Count && directiveStarts[directive] < body.EndIndex)
                preprocessorOmissions++;
            else if (!limitReached && IsEmptyBody(body))
            {
                if (findings.Count == limit)
                    limitReached = true;
                else
                    findings.Add(EmptyCatchFinding(document, clause));
            }
        }

        return (preprocessorOmissions, limitReached);
    }

    private static bool IsEmptyBody(Node body) =>
        Descendants(body, child => child.Type == "compound_statement").All(child => child.Type switch
        {
            "compound_statement" or "{" or "}" or "comment" => true,
            "expression_statement" => IsEmptyStatement(child),
            _ => false
        });

    private static bool IsEmptyStatement(Node statement)
    {
        using var cursor = statement.Walk();
        if (!cursor.GotoFirstChild())
            return false;

        var hasSemicolon = false;
        do
        {
            switch (cursor.CurrentNode.Type)
            {
                case "comment":
                    break;
                case ";" when !hasSemicolon:
                    hasSemicolon = true;
                    break;
                default:
                    return false;
            }
        } while (cursor.GotoNextSibling());

        return hasSemicolon;
    }

    private static Finding SyntaxFinding(SourceDocument document, Node node) =>
        CreateFinding(document.FileName, document.Code, node, "CPP-001", Severity.High, "C++ 구문 오류",
            node.IsMissing ? $"필요한 토큰({node.Type})이 누락됐습니다." : "C++ 파서가 이 위치의 구문을 해석하지 못했습니다.",
            "표시된 줄과 앞줄의 괄호, 구분 기호, 선언 및 전처리 지시문을 확인하세요.",
            "위치는 Tree-sitter의 구문 복구 결과입니다. 비활성 전처리 분기, 매크로 확장에 의존하는 구문 및 파서가 지원하지 않는 문법에서도 진단이 발생할 수 있습니다.");

    private static Finding EmptyCatchFinding(SourceDocument document, Node node) =>
        CreateFinding(document.FileName, document.Code, node, "CPP-002", Severity.Low, "비어 있는 catch",
            "catch 본문에 실행문이나 지역 선언이 없습니다. 주석, 빈 문장과 빈 중첩 블록은 제외합니다.",
            "예상한 예외만 무시하는지 확인하고 필요한 경우 기록, 복구 또는 재전파를 추가하세요.",
            "예외를 의도적으로 무시하는 코드일 수 있습니다.");
}
