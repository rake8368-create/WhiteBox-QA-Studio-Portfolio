using TreeSitter;
using static WhiteBoxQA.Core.TreeSitterSupport;

namespace WhiteBoxQA.Core;

public sealed class JavaAnalyzer : ILanguageAnalyzer
{
    internal const int MaxSyntaxFindingsPerFile = 100;
    internal const int MaxSyntaxFindingsPerBatch = 2_000;
    // A direct caller bypasses SourceImporter, so also bound strings passed to JavaAnalyzer.
    internal const int MaxInputCharactersPerFile = 2 * 1024 * 1024;
    internal const int MaxInputCharactersPerBatch = 32 * 1024 * 1024;
    internal const int MaxRuleFindingsPerFile = 500;
    internal const int MaxRuleFindingsPerBatch = 5_000;

    private static readonly Lazy<Language> JavaLanguage = new(() => LoadLanguage("Java"));
    private readonly Func<Parser> createParser;
    private readonly Func<Parser, string, Tree?> parse;
    private readonly int maxInputCharactersPerFile;
    private readonly int maxInputCharactersPerBatch;
    private readonly int maxRuleFindingsPerFile;
    private readonly int maxRuleFindingsPerBatch;

    public JavaAnalyzer() : this(null) { }

    internal JavaAnalyzer(Func<Parser>? createParser, Func<Parser, string, Tree?>? parse = null,
        int maxInputCharactersPerFile = MaxInputCharactersPerFile,
        int maxInputCharactersPerBatch = MaxInputCharactersPerBatch,
        int maxRuleFindingsPerFile = MaxRuleFindingsPerFile,
        int maxRuleFindingsPerBatch = MaxRuleFindingsPerBatch)
    {
        if (maxInputCharactersPerFile <= 0 || maxInputCharactersPerBatch <= 0 ||
            maxRuleFindingsPerFile <= 0 || maxRuleFindingsPerBatch <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxInputCharactersPerFile), "분석 한도는 양수여야 합니다.");
        this.createParser = createParser ?? (() => new Parser(JavaLanguage.Value));
        this.parse = parse ?? ((parser, code) => parser.Parse(code));
        this.maxInputCharactersPerFile = maxInputCharactersPerFile;
        this.maxInputCharactersPerBatch = maxInputCharactersPerBatch;
        this.maxRuleFindingsPerFile = maxRuleFindingsPerFile;
        this.maxRuleFindingsPerBatch = maxRuleFindingsPerBatch;
    }

    public SourceLanguage Language => SourceLanguage.Java;
    public AnalysisCapabilities Capabilities => AnalysisCapabilities.StaticRules;

    public LanguageAnalysis Analyze(IReadOnlyList<SourceDocument> documents)
    {
        var analyzed = 0;
        var remainingSyntaxFindings = MaxSyntaxFindingsPerBatch;
        var remainingInputCharacters = maxInputCharactersPerBatch;
        var remainingRuleFindings = maxRuleFindingsPerBatch;
        var findings = new List<Finding>();
        var issues = new List<AnalysisIssue>();
        foreach (var document in documents)
        {
            // These limits cover direct SourceInput callers too. They count UTF-16 code units,
            // not source bytes, and cannot enforce a hard native parser time/memory limit.
            if (document.Code.Length > maxInputCharactersPerFile)
            {
                issues.Add(new(document.FileName,
                    $"Java 직접 분석 입력이 파일당 {maxInputCharactersPerFile:N0}자 제한을 넘어 건너뛰었습니다."));
                continue;
            }
            if (document.Code.Length > remainingInputCharacters)
            {
                issues.Add(new(document.FileName,
                    $"Java 직접 분석 입력이 전체 {maxInputCharactersPerBatch:N0}자 제한을 넘어 건너뛰었습니다."));
                continue;
            }
            remainingInputCharacters -= document.Code.Length;

            Parser parser;
            try
            {
                parser = createParser();
            }
            catch (Exception exception) when (IsNativeFailure(exception) || exception is InvalidOperationException)
            {
                issues.Add(new(document.FileName,
                    $"Java 파서를 초기화할 수 없습니다 ({FailureName(exception)}). 배포 파일과 Windows 아키텍처를 확인하세요."));
                continue;
            }

            var fileFindings = new List<Finding>();
            var hasSyntaxErrors = false;
            var omitted = false;
            var omittedRuleFindings = false;
            try
            {
                // A failed or interrupted parse can leave state on the native parser.
                // Give each file a fresh parser while sharing the loaded grammar.
                using (parser)
                {
                    var code = document.Code.Replace("\r\n", "\n").Replace('\r', '\n');
                    using var tree = parse(parser, code);
                    if (tree is null)
                    {
                        issues.Add(new(document.FileName, "Java 파서가 구문 트리를 생성하지 못했습니다."));
                        continue;
                    }

                    var root = tree.RootNode;
                    hasSyntaxErrors = root.HasError;
                    if (hasSyntaxErrors)
                        omitted = InspectSyntax(document.FileName, code, root,
                            Math.Min(MaxSyntaxFindingsPerFile, remainingSyntaxFindings), fileFindings);
                    else
                    {
                        var candidates = new List<Finding>(2);
                        foreach (var handler in Descendants(root).Where(node => node.Type == "catch_clause"))
                        {
                            candidates.Clear();
                            InspectHandler(document.FileName, code, handler, candidates);
                            var available = Math.Min(maxRuleFindingsPerFile - fileFindings.Count, remainingRuleFindings);
                            if (candidates.Count > available)
                            {
                                if (available > 0)
                                    fileFindings.AddRange(candidates.Take(available));
                                omittedRuleFindings = true;
                                break;
                            }
                            fileFindings.AddRange(candidates);
                        }
                    }
                }
            }
            catch (Exception exception) when (IsNativeFailure(exception) || exception is InvalidOperationException)
            {
                issues.Add(new(document.FileName, $"Java 파서를 실행할 수 없습니다 ({FailureName(exception)})."));
                continue;
            }

            findings.AddRange(fileFindings);
            analyzed++;
            if (hasSyntaxErrors)
                remainingSyntaxFindings -= fileFindings.Count;
            else
                remainingRuleFindings -= fileFindings.Count;
            if (omittedRuleFindings)
                issues.Add(new(document.FileName,
                    $"Java 정적 규칙 진단 한도(파일당 {maxRuleFindingsPerFile:N0}건, 분석당 {maxRuleFindingsPerBatch:N0}건)에 도달하여 추가 진단을 생략했습니다."));
            if (omitted)
                issues.Add(new(document.FileName,
                    $"Java 구문 진단 한도(파일당 {MaxSyntaxFindingsPerFile:N0}개, 분석당 {MaxSyntaxFindingsPerBatch:N0}개)에 도달하여 추가 진단을 생략했습니다."));
        }
        return new(analyzed, findings, issues);
    }

    private static bool InspectSyntax(string fileName, string code, Node root, int limit, List<Finding> findings)
    {
        var offsets = new HashSet<int>();
        foreach (var node in Descendants(root, node => node.HasError && !node.IsMissing))
        {
            if (!(node.IsError || node.IsMissing) || !offsets.Add(node.StartIndex))
                continue;
            if (findings.Count == limit)
                return true;

            var description = node.IsMissing
                ? $"Java 문법에서 필요한 토큰({node.Type})이 누락됐습니다."
                : "Java 파서가 이 위치의 구문을 해석하지 못했습니다.";
            findings.Add(CreateFinding(fileName, code, node, "JAVA-001", Severity.High, "Java 구문 오류", description,
                "표시된 줄과 앞줄의 괄호, 구분 기호, 선언을 확인하고 다시 검증하세요.",
                "위치는 Tree-sitter의 구문 복구 결과입니다. Java 컴파일러에서는 위치와 진단이 다를 수 있습니다."));
        }
        return false;
    }

    private static void InspectHandler(string fileName, string code, Node handler, List<Finding> findings)
    {
        if (handler.GetChildForField("body") is { Type: "block" } body &&
            Descendants(body, node => node.Type == "block")
                .All(node => node.Type is "block" or "{" or "}" or ";" or "line_comment" or "block_comment"))
        {
            findings.Add(CreateFinding(fileName, code, handler, "JAVA-002", Severity.Low, "비어 있는 catch",
                "catch 본문에 실행문이나 지역 선언이 없습니다. 주석, 빈 문장과 빈 중첩 블록은 제외합니다.",
                "예상한 예외만 무시하는지 확인하고 필요한 경우 기록, 복구 또는 재전파를 추가하세요.",
                "예외를 의도적으로 무시하는 코드일 수 있습니다."));
        }

        var parameter = NamedChildren(handler).FirstOrDefault(node => node.Type == "catch_formal_parameter");
        var types = parameter is null ? null : NamedChildren(parameter).FirstOrDefault(node => node.Type == "catch_type");
        if (types is null)
            return;

        // Prefer an explicit spelling if both forms are present in a union.
        var alternatives = NamedChildren(types);
        var qualified = alternatives.FirstOrDefault(IsQualifiedThrowable);
        var throwable = qualified ?? alternatives.FirstOrDefault(node => node.Type == "type_identifier" && node.Text == "Throwable");
        if (throwable is not null)
        {
            var spelling = qualified is null ? "단순 이름 Throwable" : "한정 이름 java.lang.Throwable";
            var caution = qualified is null
                ? "단순 이름 기준의 검토 항목입니다. 같은 이름의 사용자 타입을 가리킬 수 있으므로 실제 타입과 다중 catch의 유효성을 확인하세요."
                : "한정 이름 기준의 검토 항목입니다. 실제 타입 바인딩과 다중 catch의 유효성을 확인하세요.";
            findings.Add(CreateFinding(fileName, code, throwable, "JAVA-003", Severity.Moderate, "Throwable 포착 검토",
                $"catch 타입 목록에 {spelling} 표기가 있습니다. 표준 타입으로 해석되면 Exception과 Error를 모두 포착합니다.",
                "실제로 처리하려는 예외 타입을 확인하고 가능한 경우 해당 타입으로 범위를 좁히세요.",
                caution));
        }
    }

    private static bool IsQualifiedThrowable(Node node) => node.Type == "scoped_type_identifier" &&
        Descendants(node, part => part.Type == "scoped_type_identifier")
            .Where(part => part.IsNamed && !part.IsExtra &&
                part.Type is not ("scoped_type_identifier" or "annotation" or "marker_annotation"))
            .Select(part => part.Type == "type_identifier" ? part.Text : null)
            .SequenceEqual(["java", "lang", "Throwable"]);
}
