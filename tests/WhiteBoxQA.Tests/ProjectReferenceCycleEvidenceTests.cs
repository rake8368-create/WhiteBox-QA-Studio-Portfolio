using ClosedXML.Excel;
using WhiteBoxQA.Core;
using WhiteBoxQA.Infrastructure;

namespace WhiteBoxQA.Tests;

public sealed class ProjectReferenceCycleEvidenceTests
{
    [Fact]
    public void DirectCycleAndAffectedConsumerShowAnActualCyclePath()
    {
        var a = new CSharpProjectNode("A/A.csproj");
        var b = new CSharpProjectNode("B/B.csproj");
        var consumer = new CSharpProjectNode("Consumer/Consumer.csproj");
        var independent = new CSharpProjectNode("Independent/Independent.csproj");
        var edges = new Dictionary<CSharpProjectNode, List<CSharpProjectNode>>
        {
            [consumer] = [a],
            [b] = [a],
            [independent] = [],
            [a] = [b]
        };
        var messages = Prepare(edges);
        const string expectedPath = "A/A.csproj → B/B.csproj → A/A.csproj";

        Assert.Equal(3, messages.Count);
        Assert.All(messages, item => Assert.Contains(expectedPath, item.Reason));
        Assert.Contains(messages, item => item.File == consumer.DisplayPath
            && item.Reason.Contains("대상 프로젝트의 순환 참조", StringComparison.Ordinal));
        Assert.Empty(consumer.Dependencies);
        Assert.Empty(a.Dependencies);
        Assert.Empty(b.Dependencies);
        Assert.Empty(independent.Dependencies);
        Assert.True(consumer.HasLimitations);
        Assert.False(independent.HasLimitations);
    }

    [Fact]
    public void SelfReferenceIsReportedWithItsOwnEdge()
    {
        var a = new CSharpProjectNode("A/A.csproj");
        var consumer = new CSharpProjectNode("Consumer/Consumer.csproj");
        var messages = Prepare(new Dictionary<CSharpProjectNode, List<CSharpProjectNode>>
        {
            [consumer] = [a],
            [a] = [a]
        });

        Assert.Equal(2, messages.Count);
        Assert.Contains(messages, item => item.File == a.DisplayPath
            && item.Reason.Contains("자기 참조", StringComparison.Ordinal)
            && item.Reason.Contains("A/A.csproj → A/A.csproj", StringComparison.Ordinal));
        Assert.Contains(messages, item => item.File == consumer.DisplayPath
            && item.Reason.Contains("A/A.csproj → A/A.csproj", StringComparison.Ordinal));
    }

    [Fact]
    public void BranchingComponentUsesRealEdgesInsteadOfAlphabeticalNodeOrder()
    {
        var a = new CSharpProjectNode("A.csproj");
        var b = new CSharpProjectNode("B.csproj");
        var c = new CSharpProjectNode("C.csproj");
        var d = new CSharpProjectNode("D.csproj");
        var edges = new Dictionary<CSharpProjectNode, List<CSharpProjectNode>>
        {
            [d] = [a],
            [b] = [d],
            [c] = [a],
            [a] = [b, c]
        };
        var messages = Prepare(edges);

        Assert.Equal(5, messages.Count);
        Assert.All(messages, item => Assert.Contains("A.csproj → C.csproj → A.csproj", item.Reason));
        Assert.DoesNotContain(messages, item => item.Reason.Contains("A.csproj → B.csproj → A.csproj", StringComparison.Ordinal));
    }

    [Fact]
    public void CycleMessagesAndDependencyOrderDoNotDependOnDictionaryInsertionOrder()
    {
        static (string[] Messages, string[] Order) Run(bool reversed)
        {
            var a = new CSharpProjectNode("A.csproj");
            var b = new CSharpProjectNode("B.csproj");
            var consumer = new CSharpProjectNode("Consumer.csproj");
            var leaf = new CSharpProjectNode("Leaf.csproj");
            var items = new List<KeyValuePair<CSharpProjectNode, List<CSharpProjectNode>>>
            {
                new(a, [b]),
                new(b, [a]),
                new(consumer, [a, leaf]),
                new(leaf, [])
            };
            if (reversed)
                items.Reverse();
            var edges = items.ToDictionary(item => item.Key, item => item.Value);
            var messages = Prepare(edges);
            var order = edges.Keys.OrderBy(item => item.DisplayPath, StringComparer.Ordinal)
                .Select(item => $"{item.DisplayPath}:{item.DependencyOrder}").ToArray();
            return (messages.Select(item => $"{item.File}|{item.Reason}").ToArray(), order);
        }

        var forward = Run(false);
        var backward = Run(true);
        Assert.Equal(forward.Messages, backward.Messages);
        Assert.Equal(forward.Order, backward.Order);
    }

    [Fact]
    public void LargeCycleEvidenceIsBoundedWithoutDiscardingItsLimitations()
    {
        var nodes = Enumerable.Range(0, 20)
            .Select(index => new CSharpProjectNode($"P{index:D2}/" + new string('x', 35) + ".csproj"))
            .ToArray();
        var edges = nodes.ToDictionary(node => node, _ => new List<CSharpProjectNode>());
        for (var index = 0; index < nodes.Length; index++)
            edges[nodes[index]].Add(nodes[(index + 1) % nodes.Length]);
        var messages = Prepare(edges);

        Assert.Equal(nodes.Length, messages.Count);
        Assert.All(messages, item =>
        {
            Assert.Contains("경로 일부", item.Reason);
            Assert.True(item.Reason.Length < 500);
        });
        Assert.All(nodes, node => Assert.True(node.HasLimitations));
    }

    [Fact]
    public void CycleEvidenceSurvivesFullImportJsonAndFourExcelSheets()
    {
        var scratch = Directory.CreateTempSubdirectory("whiteboxqa-cycle-evidence-");
        try
        {
            static void Write(string root, string name, string content)
            {
                var path = Path.Combine(root, name);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, content);
            }

            Write(scratch.FullName, "A/A.csproj",
                """<Project Sdk="Microsoft.NET.Sdk"><ItemGroup><ProjectReference Include="../B/B.csproj" /></ItemGroup></Project>""");
            Write(scratch.FullName, "B/B.csproj",
                """<Project Sdk="Microsoft.NET.Sdk"><ItemGroup><ProjectReference Include="../A/A.csproj" /></ItemGroup></Project>""");
            Write(scratch.FullName, "A/Code.cs", "public class A { public int Check(bool ok) => ok ? 1 : 0; }");
            Write(scratch.FullName, "B/Code.cs", "public class B { public int Check(bool ok) => ok ? 1 : 0; }");
            var input = new SourceImporter().ReadFolder(scratch.FullName, TestContext.Current.CancellationToken);
            var result = new AnalysisOrchestrator().Analyze(input);
            var a = Path.Combine("A", "A.csproj");
            var b = Path.Combine("B", "B.csproj");
            var expected = $"{a} → {b} → {a}";

            Assert.Equal(AnalysisStatus.Partial, result.Status);
            Assert.Equal(2, result.AnalyzedFileCount);
            Assert.Equal(2, result.ControlFlow!.Count(item => item is { Status: MethodFlowStatus.Analyzed, Complexity: 2 }));
            Assert.Contains(result.Issues, item => item.Reason.Contains(expected, StringComparison.Ordinal));

            var history = new JsonAnalysisHistory(Path.Combine(scratch.FullName, "history"));
            history.Save(result);
            var loaded = Assert.Single(history.Read().Entries);
            Assert.Contains(loaded.Issues, item => item.Reason.Contains(expected, StringComparison.Ordinal));

            var output = Path.Combine(scratch.FullName, "result.xlsx");
            new ExcelReportExporter().Export(loaded, output);
            using var excel = new XLWorkbook(output);
            Assert.Equal(4, excel.Worksheets.Count);
            Assert.Contains(excel.Worksheet("분석 한계 및 참고사항").Column(2).CellsUsed(),
                cell => cell.GetString().Contains(expected, StringComparison.Ordinal));
        }
        finally
        {
            scratch.Delete(recursive: true);
        }
    }

    private static List<(string File, string Reason)> Prepare(
        Dictionary<CSharpProjectNode, List<CSharpProjectNode>> edges)
    {
        var messages = new List<(string File, string Reason)>();
        CSharpProjectGraph.Prepare(edges, (path, reason) => messages.Add((path, reason)),
            TestContext.Current.CancellationToken);
        return messages;
    }
}
