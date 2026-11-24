using System.Text.Json;
using WhiteBoxQA.Core;
using WhiteBoxQA.Infrastructure;

namespace WhiteBoxQA.Tests;

public sealed class JsonAnalysisHistoryTests : IDisposable
{
    private readonly DirectoryInfo scratch = Directory.CreateTempSubdirectory("whiteboxqa-history-tests-");
    private readonly CSharpAnalyzer analyzer = new();

    [Fact]
    public void MissingHistoryDirectoryReturnsEmptyWithoutCreatingFiles()
    {
        var directory = Path.Combine(scratch.FullName, "missing");

        var history = new JsonAnalysisHistory(directory).Read();

        Assert.Empty(history.Entries);
        Assert.Equal(0, history.SkippedCount);
        Assert.False(Directory.Exists(directory));
    }

    [Fact]
    public void ReopeningHistoryRestoresFindingsIssuesAndNewestFirstOrder()
    {
        var source = AnalyzerTests.Sample("ThrowCaughtException.cs");
        var earlier = new DateTimeOffset(2026, 11, 16, 9, 0, 0, TimeSpan.FromHours(9));
        var success = analyzer.Analyze(new("예제", [source], [])) with { AnalyzedAt = earlier };
        var partial = analyzer.Analyze(new("부분 성공 예제", [source], [new("Missing.cs", "읽기 실패")]))
            with { AnalyzedAt = earlier.AddMinutes(1) };
        var failure = analyzer.Analyze(new("빈 입력", [new("Empty.cs", "")], []))
            with { AnalyzedAt = earlier.AddMinutes(2) };
        var history = new JsonAnalysisHistory(scratch.FullName);

        history.Save(partial);
        history.Save(failure);
        history.Save(success);

        var reopened = new JsonAnalysisHistory(scratch.FullName).Read();

        Assert.Equal(0, reopened.SkippedCount);
        Assert.Equal([failure.Target, partial.Target, success.Target], reopened.Entries.Select(entry => entry.Target));
        Assert.Equal([AnalysisStatus.Failed, AnalysisStatus.Partial, AnalysisStatus.Succeeded],
            reopened.Entries.Select(entry => entry.Status));
        Assert.Equal(partial.AnalyzedAt, reopened.Entries[1].AnalyzedAt);
        Assert.Equal(partial.AnalyzedAt.Offset, reopened.Entries[1].AnalyzedAt.Offset);
        Assert.Equal(Assert.Single(partial.Findings), Assert.Single(reopened.Entries[1].Findings));
        Assert.Equal(Assert.Single(partial.Issues), Assert.Single(reopened.Entries[1].Issues));
        Assert.Equal(failure.Issues[0], reopened.Entries[0].Issues[0]);
    }

    [Fact]
    public void JsonContainsResultMetadataWithoutEmbeddingSourceText()
    {
        const string marker = "SOURCE_TEXT_SENTINEL";
        var source = AnalyzerTests.Sample("ThrowCaughtException.cs");
        var analysis = analyzer.Analyze(new("예제", [source with { Code = source.Code + $"// {marker}" }], []));

        new JsonAnalysisHistory(scratch.FullName).Save(analysis);

        var file = Assert.Single(scratch.GetFiles());
        Assert.Equal(".json", file.Extension);
        var json = File.ReadAllText(file.FullName);
        Assert.DoesNotContain(marker, json);
        using var document = JsonDocument.Parse(json);
        var result = document.RootElement.GetProperty("result");
        Assert.Equal(1, document.RootElement.GetProperty("version").GetInt32());
        Assert.Equal("예제", result.GetProperty("target").GetString());
        Assert.Equal(1, result.GetProperty("analyzedFileCount").GetInt32());
        Assert.Equal("Low", result.GetProperty("findings")[0].GetProperty("severity").GetString());
    }

    [Fact]
    public void InvalidAndNewerRecordsAreReportedWhileStagingFilesAreIgnored()
    {
        var history = new JsonAnalysisHistory(scratch.FullName);
        var result = analyzer.Analyze(new("정상 기록", [AnalyzerTests.Sample("EmptyCatch.cs")], []));
        history.Save(result);
        var valid = File.ReadAllText(Assert.Single(scratch.GetFiles("*.json")).FullName);
        File.WriteAllText(Path.Combine(scratch.FullName, "invalid.json"), "{");
        File.WriteAllText(Path.Combine(scratch.FullName, "new-schema.json"), valid.Replace("\"version\": 1", "\"version\": 2"));
        File.WriteAllText(Path.Combine(scratch.FullName, "missing-result.json"), "{\"version\": 1}");
        File.WriteAllText(Path.Combine(scratch.FullName, "pending.tmp"), "{");

        var loaded = history.Read();

        Assert.Equal(3, loaded.SkippedCount);
        Assert.Equal(result.Target, Assert.Single(loaded.Entries).Target);
        Assert.True(File.Exists(Path.Combine(scratch.FullName, "invalid.json")));
    }

    [Fact]
    public async Task ParallelSavesRetainEveryCompleteRecord()
    {
        var history = new JsonAnalysisHistory(scratch.FullName);
        var result = analyzer.Analyze(new("예제", [AnalyzerTests.Sample("CleanCode.cs")], []));
        var targets = Enumerable.Range(1, 8).Select(index => $"대상 {index}").ToArray();

        await Task.WhenAll(targets.Select(target => Task.Run(() => history.Save(result with { Target = target }))));

        var loaded = history.Read();
        Assert.Equal(0, loaded.SkippedCount);
        Assert.Equal(targets, loaded.Entries.Select(entry => entry.Target).Order());
        Assert.Empty(scratch.GetFiles("*.tmp"));
    }

    [Fact]
    public void RoundTripRestoresMethodBlocksAndDecisions()
    {
        var result = analyzer.Analyze(new("예제", [new("Flow.cs",
            "class Example { int Run(int value) => value < 3 ? 1 : 0; }")], []));
        var history = new JsonAnalysisHistory(scratch.FullName);
        history.Save(result);

        var loaded = history.Read();
        Assert.Equal(0, loaded.SkippedCount);
        var expected = Assert.Single(result.ControlFlow!);
        var actual = Assert.Single(Assert.Single(loaded.Entries).ControlFlow!);
        Assert.Equal(expected with { Blocks = actual.Blocks, Decisions = actual.Decisions }, actual);
        Assert.Equal(expected.Blocks.AsEnumerable(), actual.Blocks.AsEnumerable());
        Assert.Equal(expected.Decisions.AsEnumerable(), actual.Decisions.AsEnumerable());
    }

    [Fact]
    public void VersionOneHistoryWithoutFlowLoadsWithoutInventingComplexity()
    {
        File.WriteAllText(Path.Combine(scratch.FullName, "legacy.json"), """
            {
              "version": 1,
              "result": {
                "analyzedAt": "2026-11-16T09:00:00+09:00",
                "target": "이전 이력",
                "analyzedFileCount": 1,
                "findings": [],
                "issues": []
              }
            }
            """);

        var loaded = new JsonAnalysisHistory(scratch.FullName).Read();

        Assert.Equal(0, loaded.SkippedCount);
        var result = Assert.Single(loaded.Entries);
        Assert.Equal("이전 이력", result.Target);
        Assert.Null(result.ControlFlow);
        Assert.Null(result.Origin);

        var path = Path.Combine(scratch.FullName, "legacy.xlsx");
        new ExcelReportExporter().Export(result, path);
        using var workbook = new ClosedXML.Excel.XLWorkbook(path);
        Assert.Equal(MethodFlow.LegacyLabel, workbook.Worksheet(4).Cell("B4").GetString());
        Assert.True(workbook.Worksheet(4).Cell("E4").IsEmpty());
    }

    [Fact]
    public void HistoryRestoresOriginAndUnsupportedLanguageWithoutSavingSource()
    {
        var origin = new SourceOrigin(SourceKind.Git, "https://example.org/repo.git",
            new string('a', 40), "main", DateTimeOffset.UtcNow) { HasLocalEdits = true };
        var result = new AnalysisOrchestrator([new CSharpAnalyzer(), new PythonAnalyzer(), new JavaAnalyzer()]).Analyze(new(origin.RemoteUrl!,
            [new("Good.cs", "class UniqueSourceText {}"), new("script.js", "print('example')")], [])
        {
            Origin = origin
        });
        var history = new JsonAnalysisHistory(scratch.FullName);
        history.Save(result);

        var json = File.ReadAllText(Assert.Single(Directory.GetFiles(scratch.FullName, "*.json")));
        Assert.DoesNotContain("UniqueSourceText", json);
        Assert.DoesNotContain("print(", json);
        var loaded = history.Read();
        Assert.Equal(0, loaded.SkippedCount);
        var entry = Assert.Single(loaded.Entries);
        Assert.Equal(origin, entry.Origin);
        Assert.Equal(SourceLanguage.JavaScript, Assert.Single(entry.Issues).UnsupportedLanguage);
        Assert.False(entry.HasJavaScriptSources);
        Assert.Contains(new LanguageFileCount(SourceLanguage.JavaScript, 1, 1) { AnalyzedFileCount = 0 },
            entry.LanguageInventory!);
        Assert.Equal(AnalysisStatus.Partial, entry.Status);
    }

    [Fact]
    public void HistorySkipsAnOriginWithAnAuthenticationUrl()
    {
        var origin = new SourceOrigin(SourceKind.Git, "https://example.org/repo.git", new string('a', 40));
        var result = new AnalysisOrchestrator().Analyze(new(origin.RemoteUrl!,
            [new("A.cs", "class A {}")], []) { Origin = origin });
        var history = new JsonAnalysisHistory(scratch.FullName);
        history.Save(result);
        var valid = File.ReadAllText(Assert.Single(Directory.GetFiles(scratch.FullName, "*.json")));
        File.WriteAllText(Path.Combine(scratch.FullName, "invalid.json"),
            valid.Replace("https://example.org/repo.git", "https://fixture-user@example.org/repo.git"));

        var loaded = history.Read();

        Assert.Equal(1, loaded.SkippedCount);
        Assert.Equal(origin, Assert.Single(loaded.Entries).Origin);
    }

    [Fact]
    public void RethrowPathAndInventoryRoundTripWithExistingExcelColumns()
    {
        var result = new AnalysisOrchestrator().Analyze(new("혼합 입력",
            [new("Path.cs", """
                using System;
                class A { void M(bool change) { try {} catch (Exception ex) { if (change) ex = new Exception(); throw ex; } } }
                """)], [])
        {
            LanguageInventory = [new(SourceLanguage.CSharp, 1, 0), new(SourceLanguage.Python, 5, 5)]
        });
        var history = new JsonAnalysisHistory(scratch.FullName);
        history.Save(result);
        var saved = Assert.Single(history.Read().Entries);
        var finding = Assert.Single(saved.Findings);
        Assert.Equal(RethrowAssignment.SomePaths, finding.ExceptionAssignment);
        Assert.Equal(result.LanguageInventory, saved.LanguageInventory);

        var path = Path.Combine(scratch.FullName, "with-path.xlsx");
        new ExcelReportExporter().Export(saved, path);
        using var workbook = new ClosedXML.Excel.XLWorkbook(path);
        Assert.Equal(4, workbook.Worksheets.Count);
        var details = workbook.Worksheet("상세 분석 결과");
        Assert.Equal("WB-002", details.Cell("A4").GetString());
        Assert.Equal("Path.cs", details.Cell("C4").GetString());
        Assert.Equal(finding.Line, details.Cell("D4").GetValue<int>());
        Assert.Equal(finding.Column, details.Cell("J4").GetValue<int>());
        Assert.Equal("일부 경로에서 재대입", details.Cell("K4").GetString());
        Assert.Equal("발견 5개 · 분석 0개 · 확장자 식별만 지원", workbook.Worksheet("분석 한계 및 참고사항").Cell("B11").GetString());
    }

    [Fact]
    public void LegacyRethrowFindingRestoresWithoutInventingPathAnalysis()
    {
        File.WriteAllText(Path.Combine(scratch.FullName, "day03.json"), """
            {
              "version": 1,
              "result": {
                "analyzedAt": "2026-11-18T09:30:00+09:00",
                "target": "기존 결과",
                "analyzedFileCount": 1,
                "findings": [{
                  "ruleId": "WB-002", "severity": "Low", "title": "예외 재던지기", "fileName": "A.cs",
                  "line": 3, "column": 4, "description": "기존 설명", "evidence": "기존 근거",
                  "recommendation": "기존 권장사항", "caution": "기존 주의사항"
                }],
                "issues": []
              }
            }
            """);
        var history = new JsonAnalysisHistory(scratch.FullName).Read();
        Assert.Equal(0, history.SkippedCount);
        var result = Assert.Single(history.Entries);
        Assert.Null(result.LanguageInventory);
        var finding = Assert.Single(result.Findings);
        Assert.Null(finding.ExceptionAssignment);
        Assert.Equal("기존 주의사항", finding.Caution);

        var path = Path.Combine(scratch.FullName, "legacy-finding.xlsx");
        new ExcelReportExporter().Export(result, path);
        using var workbook = new ClosedXML.Excel.XLWorkbook(path);
        Assert.Equal(4, workbook.Worksheets.Count);
        var details = workbook.Worksheet("상세 분석 결과");
        Assert.Equal("기존 주의사항", details.Cell("I4").GetString());
        Assert.True(details.Cell("K4").IsEmpty());
    }

    public void Dispose() => scratch.Delete(recursive: true);
}
