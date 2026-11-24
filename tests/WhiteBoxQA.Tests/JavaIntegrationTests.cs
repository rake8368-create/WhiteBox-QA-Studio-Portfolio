using System.Text.Json;
using ClosedXML.Excel;
using WhiteBoxQA.Core;
using WhiteBoxQA.Infrastructure;

namespace WhiteBoxQA.Tests;

public sealed class JavaIntegrationTests : IDisposable
{
    private readonly DirectoryInfo scratch = Directory.CreateTempSubdirectory("whiteboxqa-java-");
    private readonly SourceImporter importer = new();

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FileSelectionReadsJavaBodiesAndKeepsUnsupportedInventory(bool multiple)
    {
        var java = Write("Sample.JAVA", "class Sample { void run() { try { work(); } catch (Exception e) {} } }");
        var script = Write("screen.c", "int visible = 1;");
        var input = importer.ReadFiles(multiple ? [java, script] : [java], TestContext.Current.CancellationToken);
        var document = Assert.Single(input.Documents);
        Assert.Contains("catch", document.Code);
        Assert.Equal(multiple ? java : "Sample.JAVA", document.FileName);
        var result = new AnalysisOrchestrator().Analyze(input);
        Assert.Equal("JAVA-002", Assert.Single(result.Findings).RuleId);
        Assert.Contains(new LanguageFileCount(SourceLanguage.Java, 1, 0) { AnalyzedFileCount = 1 }, result.LanguageInventory!);
        Assert.Equal(multiple ? AnalysisStatus.Partial : AnalysisStatus.Succeeded, result.Status);
        Assert.Empty(result.ControlFlow!);
        if (multiple)
            Assert.Equal(SourceLanguage.C, Assert.Single(result.Issues).UnsupportedLanguage);
    }

    [Fact]
    public void MixedFolderPreservesCSharpReferencesPythonFlowAndJavaResultsAcrossReports()
    {
        Write("App/App.csproj", """
            <Project Sdk="Microsoft.NET.Sdk"><ItemGroup><ProjectReference Include="../Core/Core.csproj" /></ItemGroup></Project>
            """);
        Write("App/Program.cs", "class App { int Decide() => Flag.Enabled ? 1 : 0; }");
        Write("Core/Core.csproj", """<Project Sdk="Microsoft.NET.Sdk" />""");
        Write("Core/Flag.cs", "public static class Flag { public static bool Enabled => true; }");
        Write("tools/check.py", "def route(a, b):\n    if a and b: return 1\n    return 0\n");
        Write("java/Sample.java", """
            class Sample {
                void run() {
                    try { work(); }
                    catch (Throwable failure) { /* intentionally empty */ }
                }
            }
            """);

        var input = importer.ReadFolder(scratch.FullName, TestContext.Current.CancellationToken);
        var result = new AnalysisOrchestrator().Analyze(input);
        Assert.Equal(AnalysisStatus.Succeeded, result.Status);
        Assert.Equal(4, result.AnalyzedFileCount);
        var notes = Assert.Single(result.Issues);
        Assert.True(notes.IsInformational);
        Assert.Contains("연결됨", notes.Reason);
        Assert.Equal(new[] { SourceLanguage.CSharp, SourceLanguage.Python, SourceLanguage.Java },
            result.LanguageInventory!.Select(item => item.Language));
        Assert.All(result.LanguageInventory!, item => Assert.Equal(item.FileCount, item.AnalyzedFileCount));
        Assert.Equal(2, Assert.Single(result.ControlFlow!, item => item.Language == SourceLanguage.CSharp).Complexity);
        Assert.Equal(3, Assert.Single(result.ControlFlow!, item => item.Language == SourceLanguage.Python).Complexity);
        Assert.Equal(2, result.ControlFlow!.Count);

        var historyPath = Path.Combine(scratch.FullName, "history");
        var history = new JsonAnalysisHistory(historyPath);
        history.Save(result);
        using var json = JsonDocument.Parse(File.ReadAllText(Assert.Single(Directory.GetFiles(historyPath))));
        Assert.Equal(1, json.RootElement.GetProperty("version").GetInt32());
        Assert.False(json.RootElement.GetProperty("result").TryGetProperty("hasJavaSources", out _));
        var restored = Assert.Single(history.Read().Entries);
        Assert.True(restored.HasJavaSources);
        Assert.True(restored.HasPythonSources);
        Assert.Equal(result.Findings, restored.Findings);
        Assert.Equal(result.Issues, restored.Issues);
        Assert.Equal(result.LanguageInventory, restored.LanguageInventory);
        Assert.Equal(result.ControlFlow!.Select(flow => (flow.MethodName, flow.Complexity)),
            restored.ControlFlow!.Select(flow => (flow.MethodName, flow.Complexity)));

        var path = Path.Combine(scratch.FullName, "mixed.xlsx");
        new ExcelReportExporter().Export(restored, path);
        using var workbook = new XLWorkbook(path);
        Assert.Equal(new[] { "분석 요약", "상세 분석 결과", "분석 한계 및 참고사항", "제어 흐름 분석" },
            workbook.Worksheets.Select(sheet => sheet.Name));
        Assert.Contains(AnalysisScope.JavaRules, workbook.Worksheet(1).Cell("B13").GetString());
        var details = workbook.Worksheet(2);
        foreach (var (finding, index) in restored.Findings.Select((finding, index) => (finding, index)))
        {
            var row = details.Row(4 + index);
            Assert.Equal(finding.RuleId, row.Cell(1).GetString());
            Assert.Equal(finding.Line, row.Cell(4).GetValue<int>());
            Assert.Equal(finding.FileName, row.Cell(3).GetString());
            Assert.Equal(finding.Evidence, row.Cell(8).GetString());
            Assert.Equal(finding.Column, row.Cell(10).GetValue<int>());
        }
        var noteValues = workbook.Worksheet(3).Column(2).CellsUsed().Select(cell => cell.GetString()).ToArray();
        Assert.Contains(AnalysisScope.JavaControlFlow, noteValues);
        Assert.Contains(notes.Reason, noteValues);
        Assert.DoesNotContain(workbook.Worksheet(4).Column(1).CellsUsed(), cell => cell.GetString().EndsWith(".java"));
    }

    [Fact]
    public void OldHistoryKeepsJavaMetadataOnlyWhenItNeverAnalyzedTheSource()
    {
        Write("legacy.json", """
            {
              "version": 1,
              "result": {
                "analyzedAt": "2026-11-20T09:00:00+09:00",
                "target": "Java 확장자만 식별한 이전 이력",
                "analyzedFileCount": 0,
                "findings": [],
                "issues": [{"fileName": "Java", "reason": "확장자만 식별", "unsupportedLanguage": "Java"}],
                "languageInventory": [{"language": "Java", "fileCount": 1, "metadataOnlyCount": 1, "analyzedFileCount": 0}]
              }
            }
            """);
        var history = new JsonAnalysisHistory(scratch.FullName).Read();
        var restored = Assert.Single(history.Entries);
        Assert.Equal(0, history.SkippedCount);
        Assert.False(restored.HasJavaSources);
        Assert.Null(restored.ControlFlow);
        Assert.Equal(SourceLanguage.Java, Assert.Single(restored.Issues).UnsupportedLanguage);
    }

    [Fact]
    public void ImportSizeLimitAndSyntaxDiagnosticLimitAreIndependent()
    {
        var brokenCode = "class Broken {\n" +
            string.Concat(Enumerable.Repeat("    int value = 0\n", JavaAnalyzer.MaxSyntaxFindingsPerFile + 1)) + "}";
        var sizeLimit = SourceImportLimits.MaxFileBytes;
        var atLimit = brokenCode + "/*" + new string('x', sizeLimit - brokenCode.Length - 4) + "*/";
        Write("AtLimit.java", atLimit);
        Write("TooLarge.java", atLimit + " ");
        Write("Healthy.java", "class Healthy { void run() { try {} catch (Exception e) {} } }");

        var input = importer.ReadFolder(scratch.FullName, TestContext.Current.CancellationToken);
        Assert.Equal(2, input.Documents.Count);
        Assert.Equal(sizeLimit, Assert.Single(input.Documents, file => file.FileName == "AtLimit.java").Code.Length);
        Assert.Equal("TooLarge.java", Assert.Single(input.Issues).FileName);

        var result = new AnalysisOrchestrator().Analyze(input);
        Assert.Equal(AnalysisStatus.Partial, result.Status);
        Assert.Equal(2, result.AnalyzedFileCount);
        Assert.Equal(JavaAnalyzer.MaxSyntaxFindingsPerFile, result.Findings.Count(finding => finding.RuleId == "JAVA-001"));
        Assert.Equal("Healthy.java", Assert.Single(result.Findings, finding => finding.RuleId == "JAVA-002").FileName);
        Assert.Contains("추가 진단을 생략", Assert.Single(result.Issues, issue => issue.FileName == "AtLimit.java").Reason);
    }

    private string Write(string relative, string contents)
    {
        var path = Path.Combine(scratch.FullName, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, contents);
        return path;
    }

    public void Dispose() => scratch.Delete(recursive: true);
}
