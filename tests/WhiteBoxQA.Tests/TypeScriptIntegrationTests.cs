using System.Text.Json;
using ClosedXML.Excel;
using WhiteBoxQA.Core;
using WhiteBoxQA.Infrastructure;

namespace WhiteBoxQA.Tests;

public sealed class TypeScriptIntegrationTests : IDisposable
{
    private readonly DirectoryInfo scratch = Directory.CreateTempSubdirectory("whiteboxqa-ts-");

    [Fact]
    public void MixedFolderKeepsSixLanguageResultsAcrossJsonAndExcel()
    {
        Write("src/Route.cs", "class Route { int Decide(bool ready) => ready ? 1 : 0; }");
        Write("src/route.py", "def route(a, b):\n    if a and b: return 1\n    return 0\n");
        Write("src/Java.java", "class Java { void run() { try {} catch (Exception e) {} } }");
        Write("src/View.jsx", "const view = <span />; try {} catch {}");
        Write("src/Cpp.cpp", "void run() { try {} catch (...) {} }");
        var tsx = Write("src/Empty.TSX", "const view = <span />;\ntry {} catch { ; }");
        Write("src/broken.mts", "const broken = ;");
        var unreadable = Write("src/unreadable.cts", "");
        File.WriteAllBytes(unreadable, [0xFF, 0xFF, 0xFF]);
        Write("src/NoDe_MoDuLeS/hidden.ts", "const hidden = ;");

        var importer = new SourceImporter();
        var analyzer = new AnalysisOrchestrator();
        var token = TestContext.Current.CancellationToken;
        var single = analyzer.Analyze(importer.ReadFiles([tsx], token));
        Assert.Equal("TS-002", Assert.Single(single.Findings).RuleId);
        var input = importer.ReadFolder(Path.Combine(scratch.FullName, "src"), token);
        var mixed = analyzer.Analyze(input);

        Assert.Equal(7, input.Documents.Count);
        Assert.Equal(7, mixed.AnalyzedFileCount);
        Assert.Equal(AnalysisStatus.Partial, mixed.Status);
        Assert.Equal(6, mixed.LanguageInventory!.Count);
        Assert.Contains(new LanguageFileCount(SourceLanguage.TypeScript, 3, 0) { AnalyzedFileCount = 2 }, mixed.LanguageInventory);
        Assert.Equal(new[] { "TS-001", "CPP-002", "TS-002", "JAVA-002", "JS-002" },
            mixed.Findings.Select(finding => finding.RuleId));
        Assert.Equal(new[] { (SourceLanguage.CSharp, 2), (SourceLanguage.Python, 3) },
            mixed.ControlFlow!.Select(flow => (flow.Language, flow.Complexity!.Value)));

        var historyDir = Path.Combine(scratch.FullName, "history");
        var history = new JsonAnalysisHistory(historyDir);
        history.Save(mixed);
        using var saved = JsonDocument.Parse(File.ReadAllText(Assert.Single(Directory.GetFiles(historyDir, "*.json"))));
        Assert.Equal(1, saved.RootElement.GetProperty("version").GetInt32());
        Assert.False(saved.RootElement.GetProperty("result").TryGetProperty("hasTypeScriptSources", out _));
        var restored = Assert.Single(history.Read().Entries);
        Assert.True(restored.HasTypeScriptSources);
        Assert.Equal(mixed.Findings, restored.Findings);
        Assert.Equal(mixed.Issues, restored.Issues);
        Assert.Equal(mixed.LanguageInventory, restored.LanguageInventory);

        var report = Path.Combine(scratch.FullName, "혼합 분석.xlsx");
        new ExcelReportExporter().Export(restored, report);
        using var workbook = new XLWorkbook(report);
        Assert.Equal(new[] { "분석 요약", "상세 분석 결과", "분석 한계 및 참고사항", "제어 흐름 분석" },
            workbook.Worksheets.Select(sheet => sheet.Name));
        Assert.Contains(AnalysisScope.TypeScriptRules, workbook.Worksheet(1).Cell("B13").GetString());
        var findings = workbook.Worksheet(2);
        Assert.Equal(("TS-001", "높음", "broken.mts", 1),
            (findings.Cell("A4").GetString(), findings.Cell("B4").GetString(), findings.Cell("C4").GetString(), findings.Cell("D4").GetValue<int>()));
        Assert.Equal(("TS-002", "낮음", "Empty.TSX", 2),
            (findings.Cell("A6").GetString(), findings.Cell("B6").GetString(), findings.Cell("C6").GetString(), findings.Cell("D6").GetValue<int>()));
        var notes = workbook.Worksheet(3);
        Assert.Equal(AnalysisScope.SupportedLanguages, notes.Cell("B4").GetString());
        Assert.Contains(notes.CellsUsed(), cell => cell.GetString() == AnalysisScope.TypeScriptScope);
    }

    [Fact]
    public void LegacyTypeScriptInventoryStaysMetadataOnlyInJsonAndReports()
    {
        Write("history/legacy.json", """
            {
              "version": 1,
              "result": {
                "analyzedAt": "2026-11-24T09:00:00+09:00", "target": "legacy", "analyzedFileCount": 0,
                "findings": [], "issues": [{ "fileName": "TypeScript", "reason": "확장자만 식별", "unsupportedLanguage": "TypeScript" }],
                "languageInventory": [{ "language": "TypeScript", "fileCount": 2, "metadataOnlyCount": 2, "analyzedFileCount": 0 }]
              }
            }
            """);
        var result = Assert.Single(new JsonAnalysisHistory(Path.Combine(scratch.FullName, "history")).Read().Entries);

        Assert.Equal(AnalysisStatus.Failed, result.Status);
        Assert.False(result.HasTypeScriptSources);
        Assert.Equal("확장자 식별만 지원", Assert.Single(result.LanguageInventory!).SupportLabel);
        Assert.Equal(0, result.AnalyzedFileCount);
        var report = Path.Combine(scratch.FullName, "legacy.xlsx");
        new ExcelReportExporter().Export(result, report);
        using var workbook = new XLWorkbook(report);
        Assert.DoesNotContain("TS-001", workbook.Worksheet(1).Cell("B13").GetString());
        Assert.DoesNotContain("TypeScript", workbook.Worksheet(3).Cell("B4").GetString());
    }

    private string Write(string relativePath, string code)
    {
        var path = Path.Combine(scratch.FullName, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, code);
        return path;
    }

    public void Dispose() => scratch.Delete(recursive: true);
}
