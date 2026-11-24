namespace WhiteBoxQA.Core;

public static class FindingPresentation
{
    public static Predicate<Finding> CreateFilter(Severity? severity, string searchText)
    {
        var term = searchText.Trim();
        return finding => (severity is null || finding.Severity == severity)
            && (term.Length == 0
                || finding.RuleId.Contains(term, StringComparison.OrdinalIgnoreCase)
                || finding.Title.Contains(term, StringComparison.OrdinalIgnoreCase)
                || finding.FileName.Contains(term, StringComparison.OrdinalIgnoreCase));
    }

    public static string FormatForCopy(Finding finding) =>
        string.Join(Environment.NewLine,
        [
            $"규칙 ID: {finding.RuleId}",
            $"위험도: {finding.SeverityLabel}",
            $"파일명: {finding.FileName}",
            $"줄 번호: {finding.Line}",
            $"문제 제목: {finding.Title}",
            $"문제 원인: {finding.Description}",
            $"개선 권장사항: {finding.Recommendation}"
        ]);
}
