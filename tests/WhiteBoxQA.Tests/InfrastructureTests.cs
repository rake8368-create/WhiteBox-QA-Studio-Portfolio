using System.Text;
using ClosedXML.Excel;
using WhiteBoxQA.Core;
using WhiteBoxQA.Infrastructure;

namespace WhiteBoxQA.Tests;

public sealed class InfrastructureTests : IDisposable
{
    private readonly DirectoryInfo scratch = Directory.CreateTempSubdirectory("whiteboxqa-tests-");
    private readonly CSharpAnalyzer analyzer = new();

    [Fact]
    public void LocalFileLoadsUtf8AndContributesFindings()
    {
        var sample = AnalyzerTests.Sample("EmptyCatch.cs");
        var path = Write(sample.FileName, sample.Code, new UTF8Encoding(true));

        var input = new SourceImporter().ReadFiles([path], TestContext.Current.CancellationToken);

        Assert.Equal(path, input.Target);
        Assert.Equal(SourceKind.LocalFiles, input.Origin?.Kind);
        Assert.Equal(sample, Assert.Single(input.Documents));
        Assert.Empty(input.Issues);
        Assert.Equal("WB-001", Assert.Single(analyzer.Analyze(input).Findings).RuleId);
    }

    [Fact]
    public void FolderRecursesAndSkipsBuildOutputWhileReadingPython()
    {
        Write("Main.cs", "class Main {}");
        Write("Nested/Helper.CS", "class Helper {}");
        Write("obj/Generated.cs", "class Generated {}");
        Write("script.py", "print('hello')");

        var input = new SourceImporter().ReadFolder(scratch.FullName, TestContext.Current.CancellationToken);

        Assert.Equal(SourceKind.LocalFolder, input.Origin?.Kind);
        Assert.Equal(new[] { "Main.cs", Path.Combine("Nested", "Helper.CS"), "script.py" },
            input.Documents.Select(document => document.FileName));
        Assert.Empty(input.Issues);
    }

    [Fact]
    public void LocalFilesReportMissingAndInvalidDataWithoutDiscardingReadableFiles()
    {
        var valid = Write("Clean.cs", "class Clean {}", Encoding.Unicode);
        var invalid = Path.Combine(scratch.FullName, "Invalid.cs");
        File.WriteAllBytes(invalid, [0xFF, 0xAA, 0x00]);
        var missing = Path.Combine(scratch.FullName, "Missing.cs");

        var result = analyzer.Analyze(new SourceImporter().ReadFiles([valid, invalid, missing], TestContext.Current.CancellationToken));

        Assert.Equal(AnalysisStatus.Partial, result.Status);
        Assert.Equal(1, result.AnalyzedFileCount);
        Assert.Empty(result.Findings);
        Assert.Equal(new[] { invalid, missing }, result.Issues.Select(issue => issue.FileName));
    }

    [Fact]
    public void MixedFolderReportsLanguageCountsWithOneIssuePerUnsupportedLanguage()
    {
        Write("App.cs", "class App {}");
        for (var index = 0; index < 220; index++)
            Write($"python/File{index}.py", "this is deliberately invalid Python");
        Write("src/legacy.c", "this is deliberately invalid C");
        Write("obj/Excluded.py", "");
        Write("include/ambiguous.h", "");

        var input = new SourceImporter().ReadFolder(scratch.FullName, TestContext.Current.CancellationToken);
        var result = new AnalysisOrchestrator().Analyze(input);

        Assert.Equal(221, input.Documents.Count);
        Assert.Equal(AnalysisStatus.Partial, result.Status);
        Assert.Equal(221, result.AnalyzedFileCount);
        Assert.Equal(220, result.Findings.Count);
        Assert.All(result.Findings, finding => Assert.Equal("PY-001", finding.RuleId));
        Assert.Equal(new LanguageFileCount[] { new(SourceLanguage.CSharp, 1, 0) { AnalyzedFileCount = 1 },
            new(SourceLanguage.Python, 220, 0) { AnalyzedFileCount = 220 },
            new(SourceLanguage.C, 1, 1) { AnalyzedFileCount = 0 } }, result.LanguageInventory);
        Assert.Equal(SourceLanguage.C, Assert.Single(result.Issues).UnsupportedLanguage);
    }

    [Fact]
    public void SelectedUnsupportedSourcesAreIdentifiedWithoutReadingTheirText()
    {
        var path = Write("App.cs", "class App {}");
        var script = Write("module.c", "");
        File.WriteAllBytes(script, [0xFF, 0xFF, 0x00, 0x00, 0x00]);
        var importer = new SourceImporter();

        var mixed = analyzer.Analyze(importer.ReadFiles([path, script], TestContext.Current.CancellationToken));
        Assert.Equal(AnalysisStatus.Partial, mixed.Status);
        Assert.Equal(SourceLanguage.C, Assert.Single(mixed.Issues).UnsupportedLanguage);
        var unsupported = analyzer.Analyze(importer.ReadFiles([script], TestContext.Current.CancellationToken));
        Assert.Equal(AnalysisStatus.Failed, unsupported.Status);
        Assert.Equal(0, unsupported.AnalyzedFileCount);
    }

    [Fact]
    public void GitUrlRequiresHttpsWithoutEmbeddedCredentials()
    {
        Assert.True(GitSourceImporter.IsSupportedUrl("https://github.com/example/project.git"));
        Assert.False(GitSourceImporter.IsSupportedUrl("http://example.org/project"));
        Assert.False(GitSourceImporter.IsSupportedUrl("https://token@example.org/project"));
        Assert.False(GitSourceImporter.IsSupportedUrl("https://example.org/project?token=value"));
        Assert.False(GitSourceImporter.IsSupportedUrl("https://@example.org/project"));
        Assert.False(GitSourceImporter.IsSupportedUrl("https://example.org/project#token"));
        Assert.False(GitSourceImporter.IsSupportedUrl("file:///C:/source"));
    }

    [Fact]
    public void ExportRetainsThreeOriginalSheetsAndAppendsControlFlow()
    {
        var result = analyzer.Analyze(new("예제", [AnalyzerTests.Sample("CleanCode.cs")], []));
        var path = Path.Combine(scratch.FullName, "결과.xlsx");

        new ExcelReportExporter().Export(result, path);

        Assert.True(new FileInfo(path).Length > 0);
        using var workbook = new XLWorkbook(path);
        Assert.Equal(new[] { "분석 요약", "상세 분석 결과", "분석 한계 및 참고사항", "제어 흐름 분석" },
            workbook.Worksheets.Select(sheet => sheet.Name));
        Assert.Equal("발견된 항목 없음", workbook.Worksheet(2).Cell("E4").GetString());
    }

    [Fact]
    public void ExportPreservesCountsLocationsAdviceAndIssuesAsCellValues()
    {
        var sample = AnalyzerTests.Sample("ThrowCaughtException.cs") with { FileName = "=2+2.cs" };
        var result = analyzer.Analyze(new("보고서 대상", [sample], [new("Missing.cs", "읽기 실패")]));
        var path = Path.Combine(scratch.FullName, "상세 결과.xlsx");

        new ExcelReportExporter().Export(result, path);
        using var workbook = new XLWorkbook(path);

        var summary = workbook.Worksheet("분석 요약");
        Assert.Equal("보고서 대상", summary.Cell("B5").GetString());
        Assert.Equal(1, summary.Cell("B6").GetValue<int>());
        Assert.Equal(1, summary.Cell("B7").GetValue<int>());
        Assert.Equal(1, summary.Cell("B10").GetValue<int>());
        Assert.Equal("부분 성공", summary.Cell("B12").GetString());
        Assert.Contains("WB-003", summary.Cell("B13").GetString());

        var details = workbook.Worksheet("상세 분석 결과");
        Assert.Equal("WB-002", details.Cell("A4").GetString());
        Assert.Equal("낮음", details.Cell("B4").GetString());
        Assert.Equal("=2+2.cs", details.Cell("C4").GetString());
        Assert.False(details.Cell("C4").HasFormula);
        Assert.Equal(10, details.Cell("D4").GetValue<int>());
        Assert.Contains("throw;", details.Cell("G4").GetString());
        Assert.Contains("Roslyn", details.Cell("H4").GetString());

        var notes = workbook.Worksheet("분석 한계 및 참고사항");
        Assert.Equal("C# 14", notes.Cell("B4").GetString());
        Assert.Equal("Missing.cs", notes.Cell("A9").GetString());
        Assert.Equal("읽기 실패", notes.Cell("B9").GetString());
    }

    [Fact]
    public void ExportMarksFailedAnalysisAndExplainsWhy()
    {
        var result = analyzer.Analyze(new("직접 입력", [new("Empty.cs", "")], []));
        var path = Path.Combine(scratch.FullName, "실패.xlsx");

        new ExcelReportExporter().Export(result, path);
        using var workbook = new XLWorkbook(path);

        Assert.Equal("실패", workbook.Worksheet(1).Cell("B12").GetString());
        Assert.Equal("분석 실패", workbook.Worksheet(2).Cell("E4").GetString());
        Assert.Contains("비어", workbook.Worksheet(3).Cell("B9").GetString());
    }

    [Fact]
    public void ExportWritesMethodDetailsAndLeavesUnavailableScoreBlank()
    {
        var result = analyzer.Analyze(new("예제", [new("Methods.cs", """
            abstract class Sample
            {
                int Analyze(bool flag) => flag ? 1 : 0;
                public abstract void Missing();
            }
            """)], []));
        var path = Path.Combine(scratch.FullName, "제어 흐름.xlsx");

        new ExcelReportExporter().Export(result, path);
        using var workbook = new XLWorkbook(path);

        var flow = workbook.Worksheet(4);
        Assert.Contains(MethodFlow.CoverageLabel, flow.Cell("A2").GetString());
        Assert.Equal(MethodFlow.ComplexityLabel, flow.Cell("E3").GetString());
        Assert.Equal("Methods.cs", flow.Cell("A4").GetString());
        Assert.Contains("Sample.Analyze", flow.Cell("B4").GetString());
        Assert.Equal(3, flow.Cell("C4").GetValue<int>());
        Assert.Equal(3, flow.Cell("D4").GetValue<int>());
        Assert.Equal(2, flow.Cell("E4").GetValue<int>());
        Assert.Equal("단순", flow.Cell("F4").GetString());
        Assert.Equal("분석 완료", flow.Cell("G4").GetString());
        Assert.True(flow.Cell("E5").IsEmpty());
        Assert.Equal("분석 불가", flow.Cell("G5").GetString());
        Assert.Contains("본문", flow.Cell("H5").GetString());
    }

    [Fact]
    public void ExportAddsGitOriginAsTextWithinExistingSummary()
    {
        var origin = new SourceOrigin(SourceKind.Git, "https://example.org/project.git", new string('a', 40),
            "qa/snapshot", new DateTimeOffset(2026, 11, 18, 0, 30, 0, TimeSpan.Zero)) { HasLocalEdits = true };
        var result = analyzer.Analyze(new(origin.RemoteUrl!, [new("A.cs", "class A {}")], []) { Origin = origin });
        var path = Path.Combine(scratch.FullName, "git.xlsx");

        new ExcelReportExporter().Export(result, path);
        using var workbook = new XLWorkbook(path);

        Assert.Equal(4, workbook.Worksheets.Count);
        var summary = workbook.Worksheet("분석 요약");
        Assert.Equal("Git 저장소", summary.Cell("B14").GetString());
        Assert.Equal(origin.RemoteUrl, summary.Cell("B15").GetString());
        Assert.Equal(origin.CommitSha, summary.Cell("B16").GetString());
        Assert.Equal(origin.Branch, summary.Cell("B17").GetString());
        Assert.Equal("2026-11-18 00:30:00 +00:00", summary.Cell("B18").GetString());
        Assert.Equal("가져온 뒤 편집됨", summary.Cell("B19").GetString());
        Assert.All(summary.Range("B14:B19").Cells(), cell => Assert.False(cell.HasFormula));
    }

    private string Write(string relativePath, string content, Encoding? encoding = null)
    {
        var path = Path.Combine(scratch.FullName, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content, encoding ?? new UTF8Encoding(false));
        return path;
    }

    public void Dispose() => scratch.Delete(recursive: true);
}
