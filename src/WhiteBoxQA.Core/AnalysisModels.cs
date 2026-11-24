namespace WhiteBoxQA.Core;

public enum Severity { High, Moderate, Low, Information }

public enum AnalysisStatus { Succeeded, Partial, Failed }
public enum RethrowAssignment { None, AllPaths, SomePaths, Unknown }

public sealed record SourceDocument(string FileName, string Code)
{
    public CSharpProjectContext? CSharpProject { get; init; }
}

public sealed record AnalysisIssue(string FileName, string Reason)
{
    public SourceLanguage? UnsupportedLanguage { get; init; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public bool IsInformational { get; init; }
}

public sealed record SourceInput(
    string Target,
    IReadOnlyList<SourceDocument> Documents,
    IReadOnlyList<AnalysisIssue> Issues)
{
    public SourceOrigin? Origin { get; init; }
    public IReadOnlyList<LanguageFileCount>? LanguageInventory { get; init; }
}

public sealed record Finding(
    string RuleId,
    Severity Severity,
    string Title,
    string FileName,
    int Line,
    int Column,
    string Description,
    string Evidence,
    string Recommendation,
    string? Caution)
{
    public RethrowAssignment? ExceptionAssignment { get; init; }

    public string? ExceptionAssignmentLabel => ExceptionAssignment switch
    {
        RethrowAssignment.None => "재대입 없음",
        RethrowAssignment.AllPaths => "모든 경로에서 재대입",
        RethrowAssignment.SomePaths => "일부 경로에서 재대입",
        RethrowAssignment.Unknown => "판정 불가 · 검토 필요",
        _ => null
    };

    public string SeverityLabel => Severity switch
    {
        Severity.High => "높음",
        Severity.Moderate => "보통",
        Severity.Low => "낮음",
        _ => "참고"
    };
}

public sealed record AnalysisResult(
    DateTimeOffset AnalyzedAt,
    string Target,
    int AnalyzedFileCount,
    IReadOnlyList<Finding> Findings,
    IReadOnlyList<AnalysisIssue> Issues)
{
    public SourceOrigin? Origin { get; init; }
    public IReadOnlyList<LanguageFileCount>? LanguageInventory { get; init; }

    // A missing field represents an analysis saved by a version without CFG results.
    public IReadOnlyList<MethodFlow>? ControlFlow { get; init; }

    public AnalysisStatus Status => AnalyzedFileCount == 0
        ? AnalysisStatus.Failed
        : LimitationCount == 0 ? AnalysisStatus.Succeeded : AnalysisStatus.Partial;

    public int LimitationCount => Issues.Count(issue => !issue.IsInformational);

    public string StatusLabel => Status switch
    {
        AnalysisStatus.Succeeded => "성공",
        AnalysisStatus.Partial => "부분 성공",
        _ => "실패"
    };

    public bool HasPythonSources => HasSources(SourceLanguage.Python);
    public bool HasJavaSources => HasSources(SourceLanguage.Java);
    public bool HasJavaScriptSources => HasSources(SourceLanguage.JavaScript);
    public bool HasCppSources => HasSources(SourceLanguage.Cpp);
    public bool HasTypeScriptSources => HasSources(SourceLanguage.TypeScript);

    private bool HasSources(SourceLanguage language) => LanguageInventory?.Any(item =>
        item.Language == language && item.FileCount > item.MetadataOnlyCount) == true
        || Findings.Any(finding => LanguageIdentification.Identify(finding.FileName) == language);

    public int Count(Severity severity) => Findings.Count(finding => finding.Severity == severity);
}

public static class AnalysisScope
{
    public const string Language = "C# 14";
    public const string SupportedLanguages = "C# 14, Python, Java, JavaScript (JSX 포함), C++ 및 TypeScript (TSX 포함)";
    public const string Rules = "WB-001 빈 catch, WB-002 예외 재던지기, WB-003 구문 오류";
    public const string PythonRules = "PY-001 구문 오류, PY-002 빈 except, PY-003 bare except, PY-004 예외 재던지기";
    public const string JavaRules = "JAVA-001 구문 오류, JAVA-002 빈 catch, JAVA-003 Throwable 포착 검토";
    public const string JavaScriptRules = "JS-001 구문 오류, JS-002 빈 catch";
    public const string CppRules = "CPP-001 구문 오류, CPP-002 빈 catch";
    public const string TypeScriptRules = "TS-001 구문 오류, TS-002 빈 catch";
    public const string TypeScriptControlFlow = "TypeScript와 TSX는 구문·정적 규칙을 지원합니다. 제어 흐름(CFG)과 복잡도는 지원 범위 밖입니다.";
    public const string TypeScriptScope = TypeScriptControlFlow + " 타입 검사·추론, tsconfig·모듈 해석 및 의미 분석은 지원 범위 밖입니다. Tree-sitter는 TSX 태그 이름 불일치를 놓칠 수 있습니다.";
    public const string JavaControlFlow = "Java는 구문·정적 규칙을 지원합니다. Java 제어 흐름과 복잡도는 지원 범위 밖입니다.";
    public const string JavaScriptScope = "JavaScript와 JSX는 Tree-sitter 구문·정적 규칙을 지원합니다. 제어 흐름, 의미 분석, 타입 추론, ECMAScript 조기 오류 및 Node.js 모듈 적합성 검증은 지원 범위 밖입니다.";
    public const string CppControlFlow = "C++는 구문·정적 규칙을 지원합니다. C++ 제어 흐름(CFG)과 복잡도는 지원 범위 밖입니다.";
    public const string CppScope = CppControlFlow + " 헤더 탐색, 컴파일러 진단, 언어 표준 적합성, 의미 분석, 타입 추론 및 메모리 누수 분석은 지원 범위 밖입니다.";
    public const string CppPreprocessor = "매크로 확장과 활성 전처리 분기 선택은 지원 범위 밖입니다. 비활성 분기나 매크로 의존 구문에서 CPP-001이 발생할 수 있습니다. 조건부 분기에 속하거나 본문에 지시문이 있는 catch는 CPP-002 검토를 생략합니다.";
    public const string PythonControlFlow = "Python 함수 CFG는 if/elif/else, for/while, break/continue, return과 단순한 두 피연산자 and/or를 지원합니다. 예외·패턴 매칭·비동기 흐름은 지원 범위 밖입니다.";
    public const string Coverage = "C#은 프로젝트별로 분석하고 확보한 고정 상대 경로의 프로젝트 참조를 연결합니다. 프로젝트가 없는 C# 파일은 한 묶음으로 처리합니다. Python은 파일별 구문·예외 처리 규칙과 함수별 제어 흐름을 분석합니다. Java, JavaScript, C++ 및 TypeScript는 Tree-sitter 구문 진단과 catch 규칙을 제공합니다.";
    public const string Unsupported = "다른 언어는 확장자만 식별합니다. MSBuild 평가, 조건부·동적 프로젝트 참조, 대상 프레임워크 참조 팩, 실제 빌드, Git 서브모듈 및 LFS 파일, 코드 실행과 테스트 커버리지는 지원 범위 밖입니다.";
    public const string Caution = "WB-001·002, PY-002~004, JAVA-002·003, JS-002, CPP-002, TS-002는 검토가 필요한 위험 가능성입니다. 실제 영향은 코드의 의도와 실행 경로에 따라 달라집니다.";
}
