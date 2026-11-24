using System.Text;
using ClosedXML.Excel;
using WhiteBoxQA.Core;
using WhiteBoxQA.Infrastructure;

namespace WhiteBoxQA.Tests;

public sealed class PythonIntegrationTests : IDisposable
{
    private readonly DirectoryInfo scratch = Directory.CreateTempSubdirectory("whiteboxqa-python-");

    [Fact]
    public void MixedLocalFolderRoundTripsJsonV1AndExportsFourSheets()
    {
        Write("App.cs", "class App { void Run() { try {} catch {} } }");
        Write("payload.py", "try: work()\nexcept: recover()\ndef run(flag, enabled):\n    if flag and enabled: return 1\n    return 0\n");
        Write("api.pyi", "async def fetch() -> None: ...\n");

        var importer = new SourceImporter();
        var input = importer.ReadFolder(scratch.FullName, TestContext.Current.CancellationToken);
        var repository = new SourceOrigin(SourceKind.Git, "https://example.invalid/project.git",
            new string('a', 40), "main", DateTimeOffset.UtcNow);
        var analysis = new AnalysisOrchestrator().Analyze(input with { Origin = repository });
        Assert.Equal(AnalysisStatus.Succeeded, analysis.Status);
        Assert.Equal(3, analysis.AnalyzedFileCount);
        Assert.Equal(2, analysis.LanguageInventory!.Single(count => count.Language == SourceLanguage.Python).AnalyzedFileCount);
        Assert.Equal(new[] { "WB-001", "PY-003" }, analysis.Findings.Select(finding => finding.RuleId));
        var flows = Assert.IsAssignableFrom<IReadOnlyList<MethodFlow>>(analysis.ControlFlow);
        Assert.Equal(3, flows.Count);
        Assert.Contains(flows, method => method is { FileName: "App.cs", Language: SourceLanguage.CSharp });
        Assert.Contains(flows, method => method is { FileName: "payload.py", Language: SourceLanguage.Python, Complexity: 3 });
        Assert.Contains(flows, method => method is { FileName: "api.pyi", Complexity: null, Status: MethodFlowStatus.Unavailable });

        var history = new JsonAnalysisHistory(Path.Combine(scratch.FullName, "history"));
        history.Save(analysis);
        var restored = Assert.Single(history.Read().Entries);
        Assert.Equal(analysis.Origin, restored.Origin);
        Assert.Equal(analysis.Findings, restored.Findings);
        Assert.Equal(analysis.LanguageInventory, restored.LanguageInventory);
        var savedFlow = Assert.Single(restored.ControlFlow!, method => method.FileName == "payload.py");
        Assert.Equal(3, savedFlow.Complexity);
        var originalFlow = flows.Single(method => method.FileName == "payload.py");
        Assert.Equal(originalFlow.Decisions, savedFlow.Decisions);
        Assert.Equal(originalFlow.Blocks, savedFlow.Blocks);
        var python = Assert.Single(restored.Findings, finding => finding.RuleId == "PY-003");
        Assert.Equal(("payload.py", 2, 1, "except: recover()"),
            (python.FileName, python.Line, python.Column, python.Evidence));
        Assert.True(FindingPresentation.CreateFilter(Severity.Moderate, "py-003")(python));
        Assert.False(FindingPresentation.CreateFilter(Severity.Low, "payload.py")(python));

        var path = Path.Combine(scratch.FullName, "mixed.xlsx");
        new ExcelReportExporter().Export(restored, path);
        using var workbook = new XLWorkbook(path);
        Assert.Equal(4, workbook.Worksheets.Count);
        Assert.Equal(repository.CommitSha, workbook.Worksheet(1).Cell("B16").GetString());
        var details = workbook.Worksheet(2);
        Assert.Equal("WB-001", details.Cell("A4").GetString());
        Assert.Equal("PY-003", details.Cell("A5").GetString());
        Assert.Equal("보통", details.Cell("B5").GetString());
        Assert.Equal(python.Column, details.Cell("J5").GetValue<int>());
        Assert.Equal(python.Evidence, details.Cell("H5").GetString());
        Assert.True(details.Cell("K5").IsEmpty());
        Assert.Contains(workbook.Worksheet(3).Column(2).CellsUsed(), cell =>
            cell.GetString() == AnalysisScope.PythonControlFlow);
        var flow = workbook.Worksheet(4);
        Assert.Equal("언어", flow.Cell("I3").GetString());
        var rows = flow.Rows(4, 6).ToDictionary(row => row.Cell(1).GetString());
        Assert.Equal("C#", rows["App.cs"].Cell(9).GetString());
        Assert.Equal("Python", rows["payload.py"].Cell(9).GetString());
        Assert.Equal(3, rows["payload.py"].Cell(5).GetValue<int>());
        Assert.True(rows["api.pyi"].Cell(5).IsEmpty());
        Assert.Equal("Python 조건 및 분기", flow.Cell("A8").GetString());
        Assert.Equal("조건", flow.Cell("H9").GetString());
        Assert.Equal("거짓 블록", flow.Cell("I9").GetString());
        AssertDecisionRow(flow.Row(10), savedFlow, savedFlow.Decisions[0]);
        AssertDecisionRow(flow.Row(11), savedFlow, savedFlow.Decisions[1]);
    }

    [Fact]
    public void ConstantAndUnreachableDecisionsSurviveJsonAndExcelRoundTrip()
    {
        var result = new AnalysisOrchestrator().Analyze(new("logic",
            [new("logic.py", "def choose():\n    if True or work(): return 1\n    return 0\n")], []));
        var history = new JsonAnalysisHistory(Path.Combine(scratch.FullName, "history"));
        history.Save(result);

        var restored = Assert.Single(history.Read().Entries);
        var method = Assert.Single(restored.ControlFlow!);
        Assert.Equal(1, method.Complexity);
        Assert.Equal(Assert.Single(result.ControlFlow!).Decisions, method.Decisions);
        var left = method.Decisions[0];
        var right = method.Decisions[1];
        Assert.True(left.IsConstant);
        Assert.Equal("단일 경로", left.Inclusion);
        Assert.False(right.IsReachable);
        Assert.Equal("도달 불가", right.Inclusion);

        var path = Path.Combine(scratch.FullName, "short-circuit.xlsx");
        new ExcelReportExporter().Export(restored, path);
        using var workbook = new XLWorkbook(path);
        Assert.Equal(4, workbook.Worksheets.Count);
        var sheet = workbook.Worksheet("제어 흐름 분석");
        AssertDecisionRow(sheet.Row(8), method, left);
        AssertDecisionRow(sheet.Row(9), method, right);
        Assert.True(sheet.Cell("I8").IsEmpty());
    }

    private static void AssertDecisionRow(IXLRow row, MethodFlow method, FlowDecision decision)
    {
        Assert.Equal(method.FileName, row.Cell(1).GetString());
        Assert.Equal(method.MethodName, row.Cell(2).GetString());
        Assert.Equal(method.StartLine, row.Cell(3).GetValue<int>());
        Assert.Equal(decision.Line, row.Cell(4).GetValue<int>());
        Assert.Equal(decision.Kind, row.Cell(5).GetString());
        Assert.Equal(decision.Block, row.Cell(6).GetValue<int>());
        Assert.Equal(decision.TrueTarget, row.Cell(7).GetValue<int?>());
        Assert.Equal(decision.Condition, row.Cell(8).GetString());
        Assert.Equal(decision.FalseTarget, row.Cell(9).GetValue<int?>());
        Assert.Equal(decision.IsReachable ? "도달 가능" : "도달 불가", row.Cell(10).GetString());
        Assert.Equal(decision.Inclusion, row.Cell(11).GetString());
    }

    [Fact]
    public void InvalidEncodingsAndOversizedPythonDoNotDiscardReadableSources()
    {
        var blank = Write("empty.py", "");
        var legacy = Write("encoded.pyi", "#!/usr/bin/env python\r\n# coding: cp949\r\n값 = '한글'\r\n",
            CodePagesEncodingProvider.Instance.GetEncoding(949));
        var bad = Path.Combine(scratch.FullName, "invalid.py");
        File.WriteAllBytes(bad, [0xFF, 0xAA, 0x00]);
        var unsupported = Write("unknown.py", "# coding: false_codec\n");
        var mixedBom = Write("conflicting.py", "# coding: latin-1\nvalue = 1", new UTF8Encoding(true));
        var oversized = Path.Combine(scratch.FullName, "oversized.py");
        using (var stream = File.Create(oversized))
            stream.SetLength(SourceImportLimits.MaxFileBytes + 1);

        var input = new SourceImporter().ReadFiles([blank, blank, legacy, bad, unsupported, mixedBom, oversized],
            TestContext.Current.CancellationToken);
        var result = new AnalysisOrchestrator().Analyze(input);

        Assert.Equal(AnalysisStatus.Partial, result.Status);
        Assert.Empty(result.Findings);
        Assert.Equal(2, result.AnalyzedFileCount);
        Assert.Contains("값 = '한글'", Assert.Single(input.Documents, document => document.FileName == legacy).Code);
        var python = Assert.Single(result.LanguageInventory!);
        Assert.Equal(6, python.FileCount);
        Assert.Equal(2, python.AnalyzedFileCount);
        Assert.Collection(result.Issues,
            issue => Assert.Contains("기본 UTF-8", issue.Reason),
            issue => Assert.Contains("지원하지 않는 Python 소스 인코딩", issue.Reason),
            issue => Assert.Contains("BOM", issue.Reason),
            issue => Assert.Contains("2 MiB", issue.Reason));
    }

    private string Write(string name, string code, Encoding? encoding = null)
    {
        var path = Path.Combine(scratch.FullName, name);
        File.WriteAllText(path, code, encoding ?? new UTF8Encoding(false));
        return path;
    }

    public void Dispose() => scratch.Delete(recursive: true);
}
