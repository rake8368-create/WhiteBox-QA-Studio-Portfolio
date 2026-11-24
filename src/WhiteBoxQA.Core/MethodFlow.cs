using System.Text.Json.Serialization;

namespace WhiteBoxQA.Core;

public enum MethodFlowStatus { Analyzed, Limited, Unavailable }

public sealed record FlowBlock(int Ordinal, string Kind, int? Line, bool IsReachable,
    int? ConditionalTarget, int? NextTarget)
{
    public string Reachability => IsReachable ? "도달 가능" : "도달 불가";
}

public sealed record FlowDecision(int Block, string Kind, int Line, string Condition,
    int? TrueTarget, int? FalseTarget, bool IsReachable, bool IncludedInComplexity)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool IsConstant { get; init; }

    public string Inclusion => IncludedInComplexity ? "집계"
        : !IsReachable ? "도달 불가"
        : IsConstant ? "단일 경로" : "범위 밖";
}

public sealed record MethodFlow(string FileName, string MethodName, int StartLine, int EndLine,
    MethodFlowStatus Status, int? Complexity, string? Limitation,
    IReadOnlyList<FlowBlock> Blocks, IReadOnlyList<FlowDecision> Decisions)
{
    public const string ComplexityLabel = "기초 복잡도(MVP)";
    public const string CoverageLabel = "테스트 커버리지 미측정";
    public SourceLanguage Language => LanguageIdentification.Identify(FileName);
    public string LanguageLabel => LanguageIdentification.GetLabel(Language);

    public const string LegacyLabel = "이 분석 이력에는 제어 흐름 결과가 없습니다.";

    public string StatusLabel => Status switch
    {
        MethodFlowStatus.Analyzed => "분석 완료",
        MethodFlowStatus.Limited => "범위 제한",
        _ => "분석 불가"
    };

    public string Rating => Complexity is int value ? ComplexityBands.Default.Rate(value) : "—";
}

public sealed record ComplexityBands(int SimpleMax, int ModerateMax, int ComplexMax)
{
    public static ComplexityBands Default { get; } = new(5, 10, 20);

    public string Rate(int complexity) =>
        complexity <= SimpleMax ? "단순"
        : complexity <= ModerateMax ? "보통"
        : complexity <= ComplexMax ? "복잡" : "매우 복잡";
}
