using System.Text;
using System.Text.Json;
using ClosedXML.Excel;
using WhiteBoxQA.Core;
using WhiteBoxQA.Infrastructure;

namespace WhiteBoxQA.Tests;

public sealed class CppIntegrationTests : IDisposable
{
    private readonly DirectoryInfo scratch = Directory.CreateTempSubdirectory("whiteboxqa-cpp-");
    private readonly SourceImporter importer = new();

    [Fact]
    public void ExplicitFilesKeepCMetadataOnlyAndPlainHeadersUnidentified()
    {
        var cpp = Write("nested/NoDe_MoDuLeS/selected.HXX", "void run() { try {} catch (...) {} }");
        var c = Write("legacy.C", "int legacy;");
        var header = Write("shared.H", "struct Shared;");
        var token = TestContext.Current.CancellationToken;

        var single = importer.ReadFiles([cpp], token);
        Assert.Equal("selected.HXX", Assert.Single(single.Documents).FileName);
        Assert.Equal("CPP-002", Assert.Single(new AnalysisOrchestrator().Analyze(single).Findings).RuleId);

        var input = importer.ReadFiles([cpp, c, header], token);
        Assert.Equal(cpp, Assert.Single(input.Documents).FileName);
        Assert.Equal(SourceLanguage.Unknown, Assert.Single(input.Issues).UnsupportedLanguage);
        Assert.False(LanguageIdentification.HasSourceReader(SourceLanguage.C));
        Assert.False(LanguageIdentification.HasSourceReader(SourceLanguage.Unknown));

        var result = new AnalysisOrchestrator().Analyze(input);
        Assert.Equal(1, result.AnalyzedFileCount);
        Assert.Equal(new LanguageFileCount[]
        {
            new(SourceLanguage.C, 1, 1) { AnalyzedFileCount = 0 },
            new(SourceLanguage.Cpp, 1, 0) { AnalyzedFileCount = 1 }
        }, result.LanguageInventory);
        Assert.Contains(result.Issues, issue => issue.UnsupportedLanguage == SourceLanguage.C);

        using var cancelled = CancellationTokenSource.CreateLinkedTokenSource(token);
        cancelled.Cancel();
        Assert.Throws<OperationCanceledException>(() => importer.ReadFiles([cpp], cancelled.Token));
    }

    [Fact]
    public void FolderDistinguishesDiscoveredReadAndAnalyzedCppFilesAndKeepsImportLimits()
    {
        const int maxBytes = SourceImportLimits.MaxFileBytes;
        var atLimit = "/*😀" + new string('x', maxBytes - 8) + "*/";
        Assert.Equal(maxBytes, Encoding.UTF8.GetByteCount(atLimit));
        Write("source/at-limit.cpp", atLimit);
        Write("source/over-limit.HPP", atLimit + " ");
        var invalid = Write("source/invalid.HH", "");
        File.WriteAllBytes(invalid, [0xFF, 0xFF, 0xFF]);
        Write("source/broken.CXX", "int broken = ;");
        Write("source/nested/NoDe_MoDuLeS/more.cc", "int broken = ;");
        Write("source/shared.h", "int shared;");
        Write("source/legacy.c", "int legacy;");
        Write("source/CMakeLists.txt", "message(FATAL_ERROR \"fixture\")");
        Write("source/Makefile", "all:\n\t@exit 1");
        var token = TestContext.Current.CancellationToken;
        var root = Path.Combine(scratch.FullName, "source");

        var input = importer.ReadFolder(root, token);

        Assert.Equal(new[] { "at-limit.cpp", "broken.CXX" }, input.Documents.Select(document => document.FileName));
        Assert.Equal(new LanguageFileCount[] { new(SourceLanguage.C, 1, 1), new(SourceLanguage.Cpp, 4, 0) },
            input.LanguageInventory);
        Assert.Contains(input.Issues, issue => issue.FileName == "over-limit.HPP" && issue.Reason == SourceImportLimits.FileSizeLimit);
        Assert.Contains(input.Issues, issue => issue.FileName == "invalid.HH");

        var result = new AnalysisOrchestrator().Analyze(input);

        Assert.Equal(AnalysisStatus.Partial, result.Status);
        Assert.Equal(2, result.AnalyzedFileCount);
        Assert.Equal("CPP-001", Assert.Single(result.Findings).RuleId);
        Assert.Contains(new LanguageFileCount(SourceLanguage.Cpp, 4, 0) { AnalyzedFileCount = 2 }, result.LanguageInventory!);
        Assert.Contains(result.Issues, issue => issue.FileName == "broken.CXX" && issue.Reason.Contains("CPP-002"));

        using var cancelled = CancellationTokenSource.CreateLinkedTokenSource(token);
        cancelled.Cancel();
        Assert.Throws<OperationCanceledException>(() => importer.ReadFolder(root, cancelled.Token));
    }

    [Fact]
    public void MixedFiveLanguageAnalysisRoundTripsTheExistingJsonV1Schema()
    {
        Write("source/Route.cs", "class Route { int Decide(bool ready) => ready ? 1 : 0; }");
        Write("source/route.py", "def route(a, b):\n    if a and b: return 1\n    return 0\n");
        Write("source/Java.java", "class Java { void run() { try {} catch (Exception e) {} } }");
        Write("source/View.JSX", "const view = <span/>; try {} catch {}");
        Write("source/Cpp.hpp", "void run() { try {} catch (...) {} }");
        Write("source/guarded.cc", "#if FEATURE\nvoid run() { try {} catch (...) {} }\n#endif\n");
        var input = importer.ReadFolder(Path.Combine(scratch.FullName, "source"), TestContext.Current.CancellationToken);
        var result = new AnalysisOrchestrator().Analyze(input);

        Assert.Equal(6, result.AnalyzedFileCount);
        Assert.True(result.HasCppSources);
        Assert.Equal(new LanguageFileCount[]
        {
            new(SourceLanguage.CSharp, 1, 0) { AnalyzedFileCount = 1 },
            new(SourceLanguage.Python, 1, 0) { AnalyzedFileCount = 1 },
            new(SourceLanguage.JavaScript, 1, 0) { AnalyzedFileCount = 1 },
            new(SourceLanguage.Java, 1, 0) { AnalyzedFileCount = 1 },
            new(SourceLanguage.Cpp, 2, 0) { AnalyzedFileCount = 2 }
        }, result.LanguageInventory);
        Assert.Equal(new[] { "CPP-002", "JAVA-002", "JS-002" }, result.Findings.Select(finding => finding.RuleId));
        Assert.Equal(new[] { (SourceLanguage.CSharp, 2), (SourceLanguage.Python, 3) },
            result.ControlFlow!.Select(method => (method.Language, method.Complexity!.Value)));
        Assert.Equal("guarded.cc", Assert.Single(result.Issues).FileName);

        var historyDirectory = Path.Combine(scratch.FullName, "history");
        var history = new JsonAnalysisHistory(historyDirectory);
        history.Save(result);
        using var json = JsonDocument.Parse(File.ReadAllText(Assert.Single(Directory.GetFiles(historyDirectory, "*.json"))));
        Assert.Equal(1, json.RootElement.GetProperty("version").GetInt32());
        var saved = json.RootElement.GetProperty("result");
        Assert.False(saved.TryGetProperty("hasCppSources", out _));
        Assert.Contains(saved.GetProperty("languageInventory").EnumerateArray(),
            item => item.GetProperty("language").GetString() == "Cpp");
        var load = history.Read();
        Assert.Equal(0, load.SkippedCount);
        var restored = Assert.Single(load.Entries);
        Assert.True(restored.HasCppSources);
        Assert.Equal(result.Findings, restored.Findings);
        Assert.Equal(result.Issues, restored.Issues);
        Assert.Equal(result.LanguageInventory, restored.LanguageInventory);
        Assert.Equal(result.AnalyzedFileCount, restored.AnalyzedFileCount);
    }

    [Fact]
    public void VersionOneHistoryPreservesCppMetadataOnlyResultsFromBeforeAnalyzerSupport()
    {
        Write("history/legacy.json", """
            {
              "version": 1,
              "result": {
                "analyzedAt": "2026-11-24T09:00:00+09:00",
                "target": "legacy",
                "analyzedFileCount": 0,
                "findings": [],
                "issues": [{ "fileName": "C++", "reason": "확장자만 식별", "unsupportedLanguage": "Cpp" }],
                "languageInventory": [{ "language": "Cpp", "fileCount": 2, "metadataOnlyCount": 2, "analyzedFileCount": 0 }]
              }
            }
            """);

        var loaded = new JsonAnalysisHistory(Path.Combine(scratch.FullName, "history")).Read();

        Assert.Equal(0, loaded.SkippedCount);
        var result = Assert.Single(loaded.Entries);
        Assert.Equal(AnalysisStatus.Failed, result.Status);
        Assert.False(result.HasCppSources);
        Assert.Equal(SourceLanguage.Cpp, Assert.Single(result.Issues).UnsupportedLanguage);
        Assert.Equal(new(SourceLanguage.Cpp, 2, 2) { AnalyzedFileCount = 0 },
            Assert.Single(result.LanguageInventory!));
    }

    [Fact]
    public void CppProductScopeUsesFindingsWhenHistoryHasNoInventory()
    {
        var result = new AnalysisOrchestrator().Analyze(new("fixture",
            [new("review.CXX", "void run() { try {} catch (...) {} }")], []));
        var withoutInventory = result with { LanguageInventory = null };

        Assert.True(withoutInventory.HasCppSources);
        Assert.Contains("C++", AnalysisScope.SupportedLanguages);
        Assert.Equal("CPP-001 구문 오류, CPP-002 빈 catch", AnalysisScope.CppRules);
        Assert.Contains("제어 흐름(CFG)", AnalysisScope.CppScope);
        Assert.Contains("컴파일러 진단", AnalysisScope.CppScope);
        Assert.Contains("메모리 누수 분석", AnalysisScope.CppScope);
        Assert.Contains("활성 전처리 분기 선택", AnalysisScope.CppPreprocessor);
        Assert.Empty(withoutInventory.ControlFlow!);
    }

    [Fact]
    public void MixedExcelShowsCppRulesSeverityLocationsLimitsAndOnlySupportedFlowLanguages()
    {
        var result = new AnalysisOrchestrator().Analyze(new("mixed",
        [
            new("Route.cs", "class Route { int Decide(bool ready) => ready ? 1 : 0; }"),
            new("route.py", "def route(a, b):\n    if a and b: return 1\n    return 0"),
            new("Java.java", "class Java { void run() { try {} catch (Exception e) {} } }"),
            new("View.jsx", "const view = <span/>; try {} catch {}"),
            new("broken.cpp", "void run() {\n  int broken = ;\n}"),
            new("empty.HPP", "void run() {\n  try {}\n  catch (...) { ; }\n}"),
            new("guarded.cxx", "#if 0\nvoid run() { try {} catch (...) {} }\n#endif\n")
        ], []));
        var directory = Directory.CreateDirectory(Path.Combine(scratch.FullName, "한글 보고서"));
        var path = Path.Combine(directory.FullName, "혼합 분석.xlsx");
        new ExcelReportExporter().Export(result, path);

        using var workbook = new XLWorkbook(path);
        Assert.Equal(new[] { "분석 요약", "상세 분석 결과", "분석 한계 및 참고사항", "제어 흐름 분석" },
            workbook.Worksheets.Select(sheet => sheet.Name));
        var summary = workbook.Worksheet(1);
        Assert.Equal(7, summary.Cell("B6").GetValue<int>());
        Assert.Equal(4, summary.Cell("B7").GetValue<int>());
        Assert.Equal(1, summary.Cell("B8").GetValue<int>());
        Assert.Equal(3, summary.Cell("B10").GetValue<int>());
        Assert.Equal(new[] { AnalysisScope.Rules, AnalysisScope.PythonRules, AnalysisScope.JavaRules,
                AnalysisScope.JavaScriptRules, AnalysisScope.CppRules },
            summary.Cell("B13").GetString().Split('\n'));

        var findings = workbook.Worksheet(2);
        Assert.Equal(new[] { "CPP-001", "CPP-002", "JAVA-002", "JS-002" }, findings.Rows(4, 7)
            .Select(row => row.Cell(1).GetString()));
        Assert.Equal(("높음", "broken.cpp", 2, 14),
            (findings.Cell("B4").GetString(), findings.Cell("C4").GetString(), findings.Cell("D4").GetValue<int>(),
                findings.Cell("J4").GetValue<int>()));
        Assert.Equal(("낮음", "empty.HPP", 3, 3),
            (findings.Cell("B5").GetString(), findings.Cell("C5").GetString(), findings.Cell("D5").GetValue<int>(),
                findings.Cell("J5").GetValue<int>()));

        var notes = workbook.Worksheet(3);
        Assert.Equal("C# 14, Python, Java, JavaScript (JSX 포함) 및 C++", notes.Cell("B4").GetString());
        Assert.Contains(notes.CellsUsed(), cell => cell.GetString() == AnalysisScope.CppScope);
        Assert.Contains(notes.CellsUsed(), cell => cell.GetString() == AnalysisScope.CppPreprocessor);
        Assert.Contains(notes.CellsUsed(), cell => cell.GetString().Contains("발견 3개 · 분석 3개"));
        Assert.Contains(notes.CellsUsed(), cell => cell.GetString().Contains("조건부 전처리 분기에 속하거나"));

        Assert.Equal(2, result.ControlFlow!.Count);
        Assert.Equal(new[] { "C#", "Python" }, workbook.Worksheet(4).Range("I4:I5").Cells()
            .Select(cell => cell.GetString()));
    }

    private string Write(string relative, string code)
    {
        var path = Path.Combine(scratch.FullName, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, code);
        return path;
    }

    public void Dispose() => scratch.Delete(recursive: true);
}
