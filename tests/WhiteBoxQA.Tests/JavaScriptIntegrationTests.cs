using System.Text;
using System.Text.Json;
using ClosedXML.Excel;
using WhiteBoxQA.Core;
using WhiteBoxQA.Infrastructure;

namespace WhiteBoxQA.Tests;

public sealed class JavaScriptIntegrationTests : IDisposable
{
    private readonly DirectoryInfo scratch = Directory.CreateTempSubdirectory("whiteboxqa-js-");
    private readonly SourceImporter importer = new();

    [Fact]
    public void FolderExcludesNestedDependenciesForEveryLanguageButExplicitFileIsReadable()
    {
        Write("a/NoDe_MoDuLeS/pkg/large.js", new string('x', SourceImportLimits.MaxFileBytes + 1));
        Write("node_modules/Hidden.cs", "class Hidden {}");
        Write("node_modules/hidden.py", "");
        Write("node_modules/Hidden.java", "");
        Write("node_modules/Hidden.tsx", "");
        var selected = Write("a/NoDe_MoDuLeS/pkg/selected.JSX", "const view = <span />;");
        Write("node_modules_extra/Keep.cs", "class Keep {}");
        Write("dist/main.MJS", "export const x = 1;");
        Write("build/main.CJS", "module.exports = true;");
        Write("vendor/main.js", "const x = 1;");
        Write("src/view.jsx", "const view = <span />;");

        var input = importer.ReadFolder(scratch.FullName, TestContext.Current.CancellationToken);
        var result = new AnalysisOrchestrator().Analyze(input);

        Assert.Equal(5, input.Documents.Count);
        Assert.Empty(result.Issues);
        Assert.Equal(5, result.AnalyzedFileCount);
        Assert.Equal(new LanguageFileCount[]
        {
            new(SourceLanguage.CSharp, 1, 0) { AnalyzedFileCount = 1 },
            new(SourceLanguage.JavaScript, 4, 0) { AnalyzedFileCount = 4 }
        }, result.LanguageInventory);

        var dependencyFolder = importer.ReadFolder(Path.GetDirectoryName(selected)!, TestContext.Current.CancellationToken);
        Assert.Empty(dependencyFolder.Documents);
        Assert.Contains("node_modules", Assert.Single(dependencyFolder.Issues).Reason);
        var explicitFile = new AnalysisOrchestrator().Analyze(importer.ReadFiles([selected], TestContext.Current.CancellationToken));
        Assert.Empty(explicitFile.Issues);
        Assert.Equal(1, explicitFile.AnalyzedFileCount);
        Assert.Equal(new(SourceLanguage.JavaScript, 1, 0) { AnalyzedFileCount = 1 },
            Assert.Single(explicitFile.LanguageInventory!));
    }

    [Fact]
    public void ExcludedFolderStillHonorsCancellation()
    {
        var source = Write("NoDe_MoDuLeS/ignored.js", "");
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            importer.ReadFolder(Path.GetDirectoryName(source)!, cancellation.Token));
    }

    [Fact]
    public void LocalImportAppliesByteLimitAndKeepsTheUnreadFileInInventory()
    {
        var bytesAtLimit = "/*😀" + new string('x', SourceImportLimits.MaxFileBytes - 8) + "*/";
        Assert.Equal(SourceImportLimits.MaxFileBytes, Encoding.UTF8.GetByteCount(bytesAtLimit));
        var atLimit = Write("at-limit.js", bytesAtLimit);
        var tooLarge = Write("over-limit.jsx", bytesAtLimit + " ");
        var invalid = Write("invalid.cjs", "");
        File.WriteAllBytes(invalid, [0xFF, 0xFF, 0xFF]);
        var result = new AnalysisOrchestrator().Analyze(importer.ReadFiles([atLimit, tooLarge, invalid], TestContext.Current.CancellationToken));

        Assert.Equal(AnalysisStatus.Partial, result.Status);
        Assert.Equal(1, result.AnalyzedFileCount);
        Assert.Empty(result.Findings);
        Assert.Equal(new(SourceLanguage.JavaScript, 3, 0) { AnalyzedFileCount = 1 },
            Assert.Single(result.LanguageInventory!));
        Assert.Collection(result.Issues,
            issue => Assert.Contains(SourceImportLimits.FileSizeLimit, issue.Reason),
            issue => Assert.Equal(invalid, issue.FileName));
    }

    [Fact]
    public void MixedResultsRoundTripJsonV1AndKeepFourExcelSheets()
    {
        var source = Path.Combine(scratch.FullName, "source");
        Write("source/Route.cs", "class Route { int Decide(bool ready) => ready ? 1 : 0; }");
        Write("source/route.py", "def route(a, b):\n    if a and b: return 1\n    return 0\n");
        Write("source/Sample.java", "class Sample { void run() { try {} catch (Throwable ex) {} } }");
        Write("source/broken.js", "const broken = ;");
        Write("source/main.mjs", "export async function run() { try { await work(); } catch {} }");
        Write("source/main.cjs", "module.exports = true;");
        Write("source/view.jsx", "const view = <span>한글</span>;");

        var result = new AnalysisOrchestrator().Analyze(importer.ReadFolder(source, TestContext.Current.CancellationToken));
        Assert.Equal(7, result.AnalyzedFileCount);
        Assert.Equal(AnalysisStatus.Partial, result.Status);
        Assert.True(result.HasJavaScriptSources);
        Assert.Equal(new LanguageFileCount[]
        {
            new(SourceLanguage.CSharp, 1, 0) { AnalyzedFileCount = 1 },
            new(SourceLanguage.Python, 1, 0) { AnalyzedFileCount = 1 },
            new(SourceLanguage.JavaScript, 4, 0) { AnalyzedFileCount = 4 },
            new(SourceLanguage.Java, 1, 0) { AnalyzedFileCount = 1 }
        }, result.LanguageInventory);
        Assert.Equal(new[] { "JS-001", "JAVA-003", "JS-002", "JAVA-002" }, result.Findings.Select(finding => finding.RuleId));
        Assert.Equal(new[] { (SourceLanguage.CSharp, 2), (SourceLanguage.Python, 3) },
            result.ControlFlow!.Select(method => (method.Language, method.Complexity!.Value)));

        var historyPath = Path.Combine(scratch.FullName, "history");
        var history = new JsonAnalysisHistory(historyPath);
        history.Save(result);
        using var json = JsonDocument.Parse(File.ReadAllText(Assert.Single(Directory.GetFiles(historyPath, "*.json"))));
        Assert.Equal(1, json.RootElement.GetProperty("version").GetInt32());
        Assert.False(json.RootElement.GetProperty("result").TryGetProperty("hasJavaScriptSources", out _));
        var load = history.Read();
        Assert.Equal(0, load.SkippedCount);
        var restored = Assert.Single(load.Entries);
        Assert.True(restored.HasJavaScriptSources);
        Assert.Equal(result.Findings, restored.Findings);
        Assert.Equal(result.Issues, restored.Issues);
        Assert.Equal(result.LanguageInventory, restored.LanguageInventory);

        var report = Path.Combine(scratch.FullName, "report.xlsx");
        new ExcelReportExporter().Export(restored, report);
        using var workbook = new XLWorkbook(report);
        Assert.Equal(new[] { "분석 요약", "상세 분석 결과", "분석 한계 및 참고사항", "제어 흐름 분석" },
            workbook.Worksheets.Select(sheet => sheet.Name));
        Assert.Contains(AnalysisScope.JavaScriptRules, workbook.Worksheet(1).Cell("B13").GetString());
        Assert.Equal("JS-001", workbook.Worksheet(2).Cell("A4").GetString());
        Assert.Equal("C# 14, Python, Java 및 JavaScript (JSX 포함)", workbook.Worksheet(3).Cell("B4").GetString());
        Assert.Contains(workbook.Worksheet(3).CellsUsed(), cell => cell.GetString() == AnalysisScope.JavaScriptScope);
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
