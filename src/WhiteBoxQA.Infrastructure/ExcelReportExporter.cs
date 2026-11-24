using ClosedXML.Excel;
using WhiteBoxQA.Core;

namespace WhiteBoxQA.Infrastructure;

public sealed class ExcelReportExporter
{
    public void Export(AnalysisResult result, string path)
    {
        using var workbook = new XLWorkbook();
        workbook.Style.Font.FontName = "맑은 고딕";
        workbook.Style.Font.FontSize = 10;
        AddSummary(workbook, result);
        AddFindings(workbook, result);
        AddNotes(workbook, result);
        AddControlFlow(workbook, result.ControlFlow);

        var folder = Path.GetDirectoryName(Path.GetFullPath(path))!;
        var stagingPath = Path.Combine(folder, $".whiteboxqa-{Guid.NewGuid():N}.xlsx");
        try
        {
            workbook.SaveAs(stagingPath);
            File.Move(stagingPath, path, overwrite: true);
        }
        finally
        {
            File.Delete(stagingPath);
        }
    }

    private static void AddSummary(XLWorkbook workbook, AnalysisResult result)
    {
        var sheet = CreateSheet(workbook, "분석 요약", [26, 85], ["항목", "결과"]);
        string[] labels =
        [
            "분석 일시", "분석 대상", "분석 파일 수", "발견 항목 총수", "높음",
            "보통", "낮음", "참고", "분석 상태", "지원 분석 규칙"
        ];
        for (var index = 0; index < labels.Length; index++)
            sheet.Cell(index + 4, 1).Value = labels[index];

        sheet.Cell(4, 2).Value = result.AnalyzedAt.ToString("yyyy-MM-dd HH:mm:ss zzz", System.Globalization.CultureInfo.InvariantCulture);
        sheet.Cell(5, 2).Value = result.Target;
        sheet.Cell(6, 2).Value = result.AnalyzedFileCount;
        sheet.Cell(7, 2).Value = result.Findings.Count;
        sheet.Cell(8, 2).Value = result.Count(Severity.High);
        sheet.Cell(9, 2).Value = result.Count(Severity.Moderate);
        sheet.Cell(10, 2).Value = result.Count(Severity.Low);
        sheet.Cell(11, 2).Value = result.Count(Severity.Information);
        sheet.Cell(12, 2).Value = result.StatusLabel;
        var rules = new List<string> { AnalysisScope.Rules };
        if (result.HasPythonSources)
            rules.Add(AnalysisScope.PythonRules);
        if (result.HasJavaSources)
            rules.Add(AnalysisScope.JavaRules);
        if (result.HasJavaScriptSources)
            rules.Add(AnalysisScope.JavaScriptRules);
        if (result.HasCppSources)
            rules.Add(AnalysisScope.CppRules);
        if (result.HasTypeScriptSources)
            rules.Add(AnalysisScope.TypeScriptRules);
        sheet.Cell(13, 2).Value = string.Join('\n', rules);
        sheet.Cell(14, 1).Value = "분석 출처";
        sheet.Cell(14, 2).Value = result.Origin?.KindLabel ?? "기록 없음";

        var lastRow = 14;
        if (result.Origin is { Kind: SourceKind.Git } origin)
        {
            sheet.Cell(15, 1).Value = "원격 HTTPS 주소";
            sheet.Cell(15, 2).Value = origin.RemoteUrl;
            sheet.Cell(16, 1).Value = "원본 HEAD 커밋";
            sheet.Cell(16, 2).Value = origin.CommitSha ?? "커밋 없음";
            sheet.Cell(17, 1).Value = "가져온 브랜치";
            sheet.Cell(17, 2).Value = origin.Branch ?? "기록 없음";
            sheet.Cell(18, 1).Value = "가져온 일시";
            sheet.Cell(18, 2).Value = origin.ImportedAt?.ToString("yyyy-MM-dd HH:mm:ss zzz",
                System.Globalization.CultureInfo.InvariantCulture) ?? "기록 없음";
            lastRow = 18;
        }

        if (result.Origin?.HasLocalEdits == true)
        {
            sheet.Cell(++lastRow, 1).Value = "입력 상태";
            sheet.Cell(lastRow, 2).Value = "가져온 뒤 편집됨";
        }

        StyleBody(sheet, 4, lastRow, 2, 26);
        sheet.Row(5).Height = 44;
        sheet.Row(13).Height = 26 * rules.Count;
    }

    private static void AddFindings(XLWorkbook workbook, AnalysisResult result)
    {
        const int columns = 11;
        var sheet = CreateSheet(workbook, "상세 분석 결과", [13, 10, 38, 9, 32, 54, 54, 54, 54, 8, 32],
            ["규칙 ID", "위험도", "파일명", "줄 번호", "문제 제목", "문제 설명", "수정 권장사항", "판단 근거", "주의사항",
             "열 번호", "예외 변수 경로 판정"]);

        var row = 4;
        foreach (var finding in result.Findings)
        {
            sheet.Cell(row, 1).Value = finding.RuleId;
            sheet.Cell(row, 2).Value = finding.SeverityLabel;
            sheet.Cell(row, 3).Value = finding.FileName;
            sheet.Cell(row, 4).Value = finding.Line;
            sheet.Cell(row, 5).Value = finding.Title;
            sheet.Cell(row, 6).Value = finding.Description;
            sheet.Cell(row, 7).Value = finding.Recommendation;
            sheet.Cell(row, 8).Value = finding.Evidence;
            sheet.Cell(row, 9).Value = finding.Caution ?? string.Empty;
            sheet.Cell(row, 10).Value = finding.Column;
            if (finding.ExceptionAssignmentLabel is { } assignment)
                sheet.Cell(row, 11).Value = assignment;
            row++;
        }

        if (row == 4)
            sheet.Cell(row++, 5).Value = result.Status == AnalysisStatus.Failed ? "분석 실패" : "발견된 항목 없음";

        StyleBody(sheet, 4, row - 1, columns, 54);
        for (var index = 0; index < result.Findings.Count; index++)
        {
            var (background, foreground) = result.Findings[index].Severity switch
            {
                Severity.High => ("FEE2E2", "991B1B"),
                Severity.Moderate => ("FEF3C7", "92400E"),
                Severity.Low => ("DBEAFE", "1E40AF"),
                _ => ("E2E8F0", "334155")
            };
            var cell = sheet.Cell(index + 4, 2);
            cell.Style.Fill.BackgroundColor = XLColor.FromHtml(background);
            cell.Style.Font.FontColor = XLColor.FromHtml(foreground);
            cell.Style.Font.Bold = true;
            if (result.Findings[index].ExceptionAssignment.HasValue)
                sheet.Row(index + 4).Height = 96;
        }
        sheet.Range(3, 1, row - 1, columns).SetAutoFilter();
    }

    private static void AddNotes(XLWorkbook workbook, AnalysisResult result)
    {
        var sheet = CreateSheet(workbook, "분석 한계 및 참고사항", [42, 90], ["항목 및 파일", "내용"]);
        var languages = new List<string> { AnalysisScope.Language };
        if (result.HasPythonSources)
            languages.Add("Python");
        if (result.HasJavaSources)
            languages.Add("Java");
        if (result.HasJavaScriptSources)
            languages.Add("JavaScript (JSX 포함)");
        if (result.HasCppSources)
            languages.Add("C++");
        if (result.HasTypeScriptSources)
            languages.Add("TypeScript (TSX 포함)");
        var supported = languages.Count == 1 ? languages[0]
            : $"{string.Join(", ", languages.SkipLast(1))} 및 {languages[^1]}";
        (string Label, string Value)[] notes =
        [
            ("지원 언어", supported),
            ("적용 분석 범위", AnalysisScope.Coverage),
            ("미지원 기능", AnalysisScope.Unsupported),
            ("오탐 가능성", AnalysisScope.Caution),
            ("분석 실패 항목", result.LimitationCount == 0 ? "없음" : $"{result.LimitationCount}개 · 아래 목록 참고")
        ];

        var row = 4;
        foreach (var (label, value) in notes)
        {
            sheet.Cell(row, 1).Value = label;
            sheet.Cell(row++, 2).Value = value;
        }

        foreach (var issue in result.Issues)
        {
            sheet.Cell(row, 1).Value = issue.FileName;
            sheet.Cell(row++, 2).Value = issue.Reason;
        }

        if (result.LanguageInventory is { } inventory)
        {
            foreach (var count in inventory)
            {
                sheet.Cell(row, 1).Value = $"발견한 소스 · {count.Label}";
                sheet.Cell(row++, 2).Value = $"{count.CountLabel} · {count.SupportLabel}";
            }
        }

        if (result.HasPythonSources)
        {
            sheet.Cell(row, 1).Value = "Python 제어 흐름";
            sheet.Cell(row++, 2).Value = AnalysisScope.PythonControlFlow;
        }
        if (result.HasJavaSources)
        {
            sheet.Cell(row, 1).Value = "Java 분석 범위";
            sheet.Cell(row++, 2).Value = AnalysisScope.JavaControlFlow;
        }
        if (result.HasJavaScriptSources)
        {
            sheet.Cell(row, 1).Value = "JavaScript 분석 범위";
            sheet.Cell(row++, 2).Value = AnalysisScope.JavaScriptScope;
        }
        if (result.HasCppSources)
        {
            sheet.Cell(row, 1).Value = "C++ 분석 범위";
            sheet.Cell(row++, 2).Value = AnalysisScope.CppScope;
            sheet.Cell(row, 1).Value = "C++ 전처리기";
            sheet.Cell(row++, 2).Value = AnalysisScope.CppPreprocessor;
        }

        if (result.HasTypeScriptSources)
        {
            sheet.Cell(row, 1).Value = "TypeScript 분석 범위";
            sheet.Cell(row++, 2).Value = AnalysisScope.TypeScriptScope;
        }

        StyleBody(sheet, 4, row - 1, 2, 38);
        var scopeEnd = row - 1;
        if (result.HasTypeScriptSources)
            sheet.Row(scopeEnd--).Height = 56;
        if (result.HasCppSources)
            sheet.Rows(scopeEnd - 1, scopeEnd).Height = 56;
    }

    private static void AddControlFlow(XLWorkbook workbook, IReadOnlyList<MethodFlow>? methods)
    {
        var sheet = CreateSheet(workbook, "제어 흐름 분석", [34, 42, 10, 10, 21, 14, 14, 68, 12],
            ["파일명", "메서드명", "시작 줄", "종료 줄", MethodFlow.ComplexityLabel, "복잡도 평가", "분석 상태", "범위 제한 및 제외 사유", "언어"]);
        sheet.Range(2, 1, 2, 9).Merge().FirstCell().Value =
            $"함수·메서드별 정적 분기 집계 · {MethodFlow.CoverageLabel}";

        var row = 4;
        if (methods is null or { Count: 0 })
            sheet.Cell(row++, 2).Value = methods is null ? MethodFlow.LegacyLabel : "표시할 함수 또는 메서드가 없습니다.";
        else
        {
            foreach (var method in methods)
            {
                sheet.Cell(row, 1).Value = method.FileName;
                sheet.Cell(row, 2).Value = method.MethodName;
                sheet.Cell(row, 3).Value = method.StartLine;
                sheet.Cell(row, 4).Value = method.EndLine;
                if (method.Complexity is int complexity)
                    sheet.Cell(row, 5).Value = complexity;
                sheet.Cell(row, 6).Value = method.Rating;
                sheet.Cell(row, 7).Value = method.StatusLabel;
                sheet.Cell(row, 8).Value = method.Limitation ?? "";
                sheet.Cell(row, 9).Value = method.LanguageLabel;
                row++;
            }
            sheet.Range(3, 1, row - 1, 9).SetAutoFilter();
        }

        StyleBody(sheet, 4, row - 1, 9, 30);

        if (methods?.Any(method => method.Language == SourceLanguage.Python && method.Decisions.Count > 0) == true)
            AddPythonDecisions(sheet, methods, row + 1);
    }

    private static void AddPythonDecisions(IXLWorksheet sheet, IReadOnlyList<MethodFlow> methods, int row)
    {
        string[] headers = ["파일명", "함수명", "함수 시작", "조건 줄", "종류", "결정 블록", "참 블록", "조건",
            "거짓 블록", "도달성", "복잡도 집계"];
        sheet.Column(10).Width = 13;
        sheet.Column(11).Width = 15;
        var heading = sheet.Range(row, 1, row, headers.Length).Merge();
        heading.FirstCell().Value = "Python 조건 및 분기";
        heading.Style.Fill.BackgroundColor = XLColor.FromHtml("142233");
        heading.Style.Font.FontColor = XLColor.White;
        heading.Style.Font.Bold = true;
        sheet.Row(row++).Height = 26;

        for (var column = 0; column < headers.Length; column++)
            sheet.Cell(row, column + 1).Value = headers[column];
        StyleHeader(sheet, row++, headers.Length);
        var first = row;
        foreach (var method in methods.Where(method => method.Language == SourceLanguage.Python))
        {
            foreach (var decision in method.Decisions)
            {
                sheet.Cell(row, 1).Value = method.FileName;
                sheet.Cell(row, 2).Value = method.MethodName;
                sheet.Cell(row, 3).Value = method.StartLine;
                sheet.Cell(row, 4).Value = decision.Line;
                sheet.Cell(row, 5).Value = decision.Kind;
                sheet.Cell(row, 6).Value = decision.Block;
                if (decision.TrueTarget is int whenTrue)
                    sheet.Cell(row, 7).Value = whenTrue;
                sheet.Cell(row, 8).Value = decision.Condition;
                if (decision.FalseTarget is int whenFalse)
                    sheet.Cell(row, 9).Value = whenFalse;
                sheet.Cell(row, 10).Value = decision.IsReachable ? "도달 가능" : "도달 불가";
                sheet.Cell(row, 11).Value = decision.Inclusion;
                row++;
            }
        }
        StyleBody(sheet, first, row - 1, headers.Length, 30);
    }

    private static IXLWorksheet CreateSheet(XLWorkbook workbook, string name, double[] widths, string[] headers)
    {
        var sheet = workbook.Worksheets.Add(name);
        for (var index = 0; index < headers.Length; index++)
        {
            sheet.Column(index + 1).Width = widths[index];
            sheet.Cell(3, index + 1).Value = headers[index];
        }

        var title = sheet.Range(1, 1, 1, headers.Length).Merge();
        title.FirstCell().Value = name;
        title.Style.Fill.BackgroundColor = XLColor.FromHtml("142233");
        title.Style.Font.FontColor = XLColor.White;
        title.Style.Font.Bold = true;
        title.Style.Font.FontSize = 15;
        title.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
        sheet.Row(1).Height = 34;
        StyleHeader(sheet, 3, headers.Length);
        sheet.SheetView.FreezeRows(3);
        return sheet;
    }

    private static void StyleHeader(IXLWorksheet sheet, int row, int columns)
    {
        var header = sheet.Range(row, 1, row, columns);
        header.Style.Fill.BackgroundColor = XLColor.FromHtml("275D60");
        header.Style.Font.FontColor = XLColor.White;
        header.Style.Font.Bold = true;
        header.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
        sheet.Row(row).Height = 24;
    }

    private static void StyleBody(IXLWorksheet sheet, int first, int last, int columns, double height)
    {
        var body = sheet.Range(first, 1, last, columns);
        body.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
        body.Style.Alignment.WrapText = true;
        for (var row = first; row <= last; row++)
        {
            sheet.Row(row).Height = height;
            if (row % 2 == 0)
                sheet.Range(row, 1, row, columns).Style.Fill.BackgroundColor = XLColor.FromHtml("EEF4F8");
        }
    }
}
