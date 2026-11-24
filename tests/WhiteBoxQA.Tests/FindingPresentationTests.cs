using ClosedXML.Excel;
using WhiteBoxQA.Core;
using WhiteBoxQA.Infrastructure;

namespace WhiteBoxQA.Tests;

public sealed class FindingPresentationTests
{
    private static AnalysisResult AnalyzeSamples() => new CSharpAnalyzer().Analyze(new("예제", [
        AnalyzerTests.Sample("EmptyCatch.cs"),
        AnalyzerTests.Sample("ThrowCaughtException.cs"),
        AnalyzerTests.Sample("SyntaxError.cs")
    ], []));

    [Fact]
    public void SearchMatchesRuleTitleAndFileName()
    {
        var analysis = AnalyzeSamples();

        Assert.Equal("WB-003", Assert.Single(Filter(analysis, null, "  wb-003 ")).RuleId);
        Assert.Equal("WB-001", Assert.Single(Filter(analysis, null, "아무 처리도")).RuleId);
        Assert.Equal("WB-002", Assert.Single(Filter(analysis, null, "throwcaughtexception.CS")).RuleId);
        Assert.Empty(Filter(analysis, null, "스택 추적 정보가 손실"));
    }

    [Fact]
    public void SearchAndSeverityFilterRequireBothConditions()
    {
        var analysis = AnalyzeSamples();

        Assert.Equal(2, Filter(analysis, null, "catch").Count());
        Assert.Equal("WB-001", Assert.Single(Filter(analysis, Severity.Moderate, "catch")).RuleId);
        Assert.Empty(Filter(analysis, Severity.High, "catch"));
    }

    [Fact]
    public void ClearingSearchRestoresTheOriginalResultsForTheSelectedSeverity()
    {
        var analysis = AnalyzeSamples();

        Assert.Single(Filter(analysis, null, "WB-002"));
        Assert.Equal(new[] { "WB-003", "WB-001", "WB-002" },
            Filter(analysis, null, "").Select(finding => finding.RuleId));
        Assert.Equal("WB-003", Assert.Single(Filter(analysis, Severity.High, " ")).RuleId);
        Assert.Equal(1, analysis.Count(Severity.High));
        Assert.Equal(1, analysis.Count(Severity.Moderate));
        Assert.Equal(1, analysis.Count(Severity.Low));
        Assert.Equal(0, analysis.Count(Severity.Information));
    }

    [Fact]
    public void ExportIncludesEveryOriginalFindingWhenTheViewIsFiltered()
    {
        var analysis = AnalyzeSamples();
        Assert.Single(Filter(analysis, Severity.Low, "catch"));
        var scratch = Directory.CreateTempSubdirectory("whiteboxqa-filter-test-");

        try
        {
            var path = Path.Combine(scratch.FullName, "filtered-view.xlsx");
            new ExcelReportExporter().Export(analysis, path);

            using var workbook = new XLWorkbook(path);
            var summary = workbook.Worksheet("분석 요약");
            Assert.Equal(3, summary.Cell("B7").GetValue<int>());
            Assert.Equal(new[] { 1, 1, 1, 0 },
                Enumerable.Range(8, 4).Select(row => summary.Cell(row, 2).GetValue<int>()));
            var detail = workbook.Worksheet("상세 분석 결과");
            Assert.Equal(new[] { "WB-003", "WB-001", "WB-002" },
                Enumerable.Range(4, 3).Select(row => detail.Cell(row, 1).GetString()));
        }
        finally
        {
            scratch.Delete(recursive: true);
        }
    }

    [Fact]
    public void CopyTextIncludesExactlyTheRequestedFields()
    {
        var finding = new Finding("WB-001", Severity.Moderate, "빈 catch", "Example.cs", 8, 9,
            "예외가 무시될 수 있습니다.", "실행문 0개", "예외를 기록하세요.", "의도를 확인하세요.");
        string[] expected = [
            "규칙 ID: WB-001",
            "위험도: 보통",
            "파일명: Example.cs",
            "줄 번호: 8",
            "문제 제목: 빈 catch",
            "문제 원인: 예외가 무시될 수 있습니다.",
            "개선 권장사항: 예외를 기록하세요."
        ];

        Assert.Equal(string.Join(Environment.NewLine, expected), FindingPresentation.FormatForCopy(finding));
    }

    private static IEnumerable<Finding> Filter(AnalysisResult result, Severity? severity, string term)
    {
        var filter = FindingPresentation.CreateFilter(severity, term);
        return result.Findings.Where(filter.Invoke);
    }
}
