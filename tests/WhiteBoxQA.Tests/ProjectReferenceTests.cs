using System.Text.Json;
using ClosedXML.Excel;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using WhiteBoxQA.Core;
using WhiteBoxQA.Infrastructure;

namespace WhiteBoxQA.Tests;

public sealed class ProjectReferenceTests : IDisposable
{
    private readonly DirectoryInfo scratch = Directory.CreateTempSubdirectory("whiteboxqa-references-");

    [Fact]
    public void DirectReferenceBindsPublicSymbolsAndFeedsControlFlow()
    {
        Project("App/App", References("../Core/Core.csproj", "../Core/./Core.csproj", @"..\Core\Core.csproj"));
        Project("Core/Core");
        Write("Core/Api.cs", """
            namespace Core;
            public sealed class Api
            {
                public static bool Flag => true;
                public static int Read(Api value) => 1;
            }
            """);
        Write("App/Run.cs", """
            class Runner
            {
                int Run(Core.Api value) => Core.Api.Flag ? Core.Api.Read(value) : 0;
            }
            """);
        var input = Import();
        var result = new AnalysisOrchestrator().Analyze(input);

        var note = Assert.Single(result.Issues);
        Assert.True(note.IsInformational);
        Assert.Contains("연결됨", note.Reason);
        Assert.Equal(AnalysisStatus.Succeeded, result.Status);
        var run = Flow(result, ".Run(");
        Assert.Equal(MethodFlowStatus.Analyzed, run.Status);
        Assert.Equal(2, run.Complexity);
        Assert.Contains("Core.Api.Flag", Assert.Single(run.Decisions).Condition);

        var compilations = new CSharpAnalyzer().PrepareCompilations(input.Documents, []);
        Assert.Equal(2, compilations.Count);
        var tree = Assert.Single(compilations[1].SyntaxTrees);
        var model = compilations[1].GetSemanticModel(tree);
        var token = TestContext.Current.CancellationToken;
        var root = tree.GetRoot(token);
        var call = Assert.Single(root.DescendantNodes().OfType<InvocationExpressionSyntax>());
        var branch = Assert.Single(root.DescendantNodes().OfType<ConditionalExpressionSyntax>());
        var method = Assert.IsAssignableFrom<IMethodSymbol>(model.GetSymbolInfo(call, token).Symbol);
        var property = Assert.IsAssignableFrom<IPropertySymbol>(model.GetSymbolInfo(branch.Condition, token).Symbol);
        Assert.Equal("Read", method.Name);
        Assert.Equal("Flag", property.Name);
        Assert.Equal("Core.Api", method.Parameters[0].Type.ToDisplayString());
        Assert.Equal(compilations[0].AssemblyName, method.ContainingAssembly.Name);
        Assert.True(SymbolEqualityComparer.Default.Equals(method.ContainingType, property.ContainingType));
        Assert.Single(compilations[1].References.OfType<CompilationReference>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TransitivePublicSignatureBindsThroughChainOrDiamond(bool diamond)
    {
        Project("App/App", diamond
            ? References("../Bridge/Bridge.csproj", "../Other/Other.csproj", "../Shared/Shared.csproj")
            : References("../Bridge/Bridge.csproj"));
        Project("Bridge/Bridge", References("../Shared/Shared.csproj"));
        Project("Shared/Shared");
        Write("Shared/Token.cs", "namespace Shared; public record Token(bool Active);");
        Write("Bridge/Factory.cs", "public static class Factory { public static Shared.Token Get() => new(true); }");
        if (diamond)
        {
            Project("Other/Other", References("../Shared/Shared.csproj"));
            Write("Other/Other.cs", "public static class Other { public static Shared.Token Get() => new(false); }");
        }
        Write("App/Run.cs", """
            class Runner
            {
                int Run(Shared.Token token) =>
                    Factory.Get().Active && token.Active ? 1 : 0;
            }
            """);
        var input = Import();
        var result = new AnalysisOrchestrator().Analyze(input);

        Assert.Equal(AnalysisStatus.Succeeded, result.Status);
        Assert.All(result.Issues, issue => Assert.True(issue.IsInformational));
        Assert.Equal(diamond ? 5 : 2, result.Issues.Count);
        Assert.Equal(3, Flow(result, ".Run(").Complexity);
        var compilations = new CSharpAnalyzer().PrepareCompilations(input.Documents, []);
        Assert.Equal(diamond ? 3 : 2, compilations[^1].References.OfType<CompilationReference>().Count());
    }

    [Fact]
    public void SameProjectAndTypeNamesKeepTheirOwnSettingsAndAssemblyIdentity()
    {
        Project("App/App", References("../Left/Lib.csproj") + Options(11, "APP"));
        Project("Left/Lib", Options(12, "LEFT"));
        Project("Right/Lib", Options(11, "RIGHT"));
        Write("Left/Api.cs", """
            #if LEFT && !APP && !RIGHT
            public static class Api
            {
                public static bool Check() { int[] values = [1]; return values.Length > 0; }
            }
            #endif
            """);
        Write("Right/Api.cs", """
            #if RIGHT && !LEFT && !APP
            public static class Api { public static bool Check() => false; }
            #endif
            """);
        Write("App/Run.cs", """
            #if APP && !LEFT && !RIGHT
            class Runner { int Run() => Api.Check() ? 1 : 0; }
            #endif
            """);
        var input = Import();
        var result = new AnalysisOrchestrator().Analyze(input);

        Assert.Equal(AnalysisStatus.Succeeded, result.Status);
        Assert.Equal(2, Flow(result, ".Run(").Complexity);
        Assert.Equal(2, result.ControlFlow!.Count(flow => flow.MethodName.Contains(".Check(")));
        var compilations = new CSharpAnalyzer().PrepareCompilations(input.Documents, []);
        Assert.Equal(3, compilations.Select(compilation => compilation.AssemblyName).Distinct().Count());
        var app = Assert.Single(compilations, compilation => compilation.SyntaxTrees.Single().FilePath.EndsWith("Run.cs"));
        var reference = Assert.Single(app.References.OfType<CompilationReference>());
        Assert.Equal(Path.Combine("Left", "Api.cs"), Assert.Single(reference.Compilation.SyntaxTrees).FilePath);
        Assert.Equal(LanguageVersion.CSharp11, app.LanguageVersion);
        Assert.Equal(LanguageVersion.CSharp12, Assert.IsType<CSharpCompilation>(reference.Compilation).LanguageVersion);
    }

    [Theory]
    [InlineData(1, "자기 참조")]
    [InlineData(2, "순환 참조")]
    [InlineData(3, "순환 참조")]
    public void CyclesAreIsolatedAndAffectedConsumersAreReported(int length, string expected)
    {
        for (var index = 0; index < length; index++)
        {
            var next = (index + 1) % length;
            Project($"P{index}/P", References($"../P{next}/P.csproj"));
            Write($"P{index}/Api.cs", $"public class T{index} {{ public T{next} Read() => null; }}");
        }
        Project("Consumer/Consumer", References("../P0/P.csproj"));
        Write("Consumer/Run.cs", "class Runner { T0 Bad() => null; int Good(bool b) => b ? 1 : 0; }");
        Project("Independent/Independent", References("../Core/Core.csproj"));
        Write("Independent/Check.cs", "class Independent { int Check() => Api.Flag ? 1 : 0; }");
        Project("Core/Core");
        Write("Core/Api.cs", "public static class Api { public static bool Flag => true; }");

        var result = Analyze();

        Assert.Equal(AnalysisStatus.Partial, result.Status);
        Assert.Equal(length, result.Issues.Count(issue => issue.FileName.StartsWith('P') && issue.Reason.Contains(expected)));
        Assert.Contains(result.Issues, issue => issue.FileName.StartsWith("Consumer") && issue.Reason.Contains("순환 참조"));
        Assert.Contains(result.Issues, issue => issue.FileName.StartsWith("Independent") && issue.IsInformational);
        Assert.Equal(MethodFlowStatus.Unavailable, Flow(result, ".Bad(").Status);
        Assert.Equal(2, Flow(result, ".Good(").Complexity);
        Assert.Equal(2, Flow(result, ".Check(").Complexity);
    }

    [Theory]
    [InlineData("missing", "대상을 찾을 수 없음")]
    [InlineData("broken", "XML을 읽거나 해석하지 못함")]
    [InlineData("large", "용량 한도로 생략됨")]
    [InlineData("empty", "확보한 컴파일 대상 소스 없음")]
    public void UnavailableProjectsDoNotContributeMetadata(string mode, string reason)
    {
        Project("App/App", References("../Core/Core.csproj"));
        Write("App/Run.cs", "class Runner { Api Bad() => null; int Good(bool b) => b ? 1 : 0; }");
        switch (mode)
        {
            case "broken":
                Write("Core/Core.csproj", "<Project><ItemGroup>");
                break;
            case "large":
                Project("Core/Core", new string(' ', 256 * 1024));
                break;
            case "empty":
                Project("Core/Core", "<PropertyGroup><EnableDefaultCompileItems>false</EnableDefaultCompileItems></PropertyGroup>");
                break;
        }
        Write("Core/Api.cs", "public class Api {}");

        var result = Analyze();

        Assert.Contains(result.Issues, issue => issue.FileName.StartsWith("App") && issue.Reason.Contains(reason));
        Assert.Equal(AnalysisStatus.Partial, result.Status);
        Assert.Equal(MethodFlowStatus.Unavailable, Flow(result, ".Bad(").Status);
        Assert.Equal(2, Flow(result, ".Good(").Complexity);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PartialSourceCoverageFlowsToAncestorsWhileUsableTypesRemainLinked(bool explicitInclude)
    {
        Project("App/App", References("../Bridge/Bridge.csproj"));
        Project("Bridge/Bridge", References("../Core/Core.csproj"));
        Project("Core/Core", explicitInclude ? """<ItemGroup><Compile Include="Missing.cs" /></ItemGroup>""" : "");
        if (!explicitInclude)
            Write("Core/Missing.cs", new string(' ', 2 * 1024 * 1024 + 1));
        Write("Core/Api.cs", "public record Api(bool Flag);");
        Write("Bridge/Factory.cs", "public static class Factory { public static Api Get() => new(true); }");
        Write("App/Run.cs", "class Runner { int Run() => Factory.Get().Flag ? 1 : 0; Missing Bad() => null; }");

        var result = Analyze();

        Assert.Equal(AnalysisStatus.Partial, result.Status);
        Assert.Contains(result.Issues, issue => issue.FileName.StartsWith("Core") && issue.Reason.Contains("소스 일부"));
        Assert.Equal(2, result.Issues.Count(issue => issue.Reason.Contains("부분 연결") && !issue.IsInformational));
        Assert.Equal(2, Flow(result, ".Run(").Complexity);
        Assert.Equal(MethodFlowStatus.Unavailable, Flow(result, ".Bad(").Status);
    }

    [Fact]
    public void ErrorInReferencedProjectProducesPartialConnectionAndPreservesRules()
    {
        Project("App/App", References("../Core/Core.csproj"));
        Project("Core/Core");
        Write("Core/Api.cs", """
            public static class Api
            {
                public static bool Flag => true;
                static void Empty() { try {} catch {} }
                static void Rethrow() { try {} catch (System.Exception ex) { throw ex; } }
                static void Broken() { int bad = ; }
            }
            """);
        Write("App/Run.cs", "class Runner { int Run() => Api.Flag ? 1 : 0; }");

        var result = Analyze();

        Assert.Equal(new[] { "WB-001", "WB-002", "WB-003" },
            result.Findings.Select(finding => finding.RuleId).Distinct().Order());
        Assert.Contains(result.Issues, issue => issue.FileName.StartsWith("Core") && issue.Reason.Contains("CS1525"));
        Assert.Contains(result.Issues, issue => issue.FileName.StartsWith("App") && issue.Reason.Contains("부분 연결"));
        Assert.Equal(2, Flow(result, ".Run(").Complexity);
    }

    [Fact]
    public void UnsupportedDeclarationsAndEscapingPathsNeverLinkExistingProjects()
    {
        Project("Import/App/App", """
            <ItemGroup Condition="'$(Configuration)' == 'Debug'">
                <ProjectReference Include="../Core/Core.csproj" />
            </ItemGroup>
            <ItemGroup>
                <ProjectReference Include="../Core/Core.csproj" Condition="'1' == '1'" />
                <ProjectReference Include="$(CoreRoot)/Core.csproj" />
                <ProjectReference Include="C:/Core/Core.csproj" />
                <ProjectReference Include="../../Outer/Outer.csproj" />
                <ProjectReference Include="../Core/Core.csproj"><ReferenceOutputAssembly>false</ReferenceOutputAssembly></ProjectReference>
                <ProjectReference Include="../Other/Other.vbproj" />
            </ItemGroup>
            <Choose><When Condition="'1' == '1'">
              <ItemGroup><ProjectReference Include="../Core/Core.csproj" /></ItemGroup>
            </When></Choose>
            """);
        Project("Import/Core/Core");
        Project("Outer/Outer");
        Write("Import/Core/Api.cs", "public class Api {}");
        Write("Outer/Outside.cs", "public class Outside {}");
        Write("Import/App/Run.cs", "class Runner { Api Bad() => null; Outside Out() => null; }");
        var input = new SourceImporter().ReadFolder(Path.Combine(scratch.FullName, "Import"),
            TestContext.Current.CancellationToken);
        var result = new AnalysisOrchestrator().Analyze(input);

        Assert.Equal(2, input.Documents.Count);
        Assert.All(result.ControlFlow!, item => Assert.Equal(MethodFlowStatus.Unavailable, item.Status));
        foreach (var reason in new[] { "조건부", "동적", "루트 밖", "메타데이터", "중첩", ".csproj" })
            Assert.Contains(result.Issues, issue => issue.Reason.Contains(reason));
        Assert.DoesNotContain(result.Issues, issue => issue.IsInformational);
    }

    [Fact]
    public void ProjectReferenceRemovePreventsAnUnverifiedConnection()
    {
        Project("App/App", References("../Core/Core.csproj") + """
            <ItemGroup><ProjectReference Remove="../Core/Core.csproj" /></ItemGroup>
            """);
        Project("Core/Core");
        Write("Core/Api.cs", "public class Api {}");
        Write("App/Run.cs", "class Runner { Api Run() => null; }");

        var result = Analyze();

        Assert.Contains(result.Issues, issue => issue.Reason.Contains("Remove 또는 Update"));
        Assert.Equal(MethodFlowStatus.Unavailable, Flow(result, ".Run(").Status);
    }

    [Fact]
    public void FileSelectionUsesNearestSettingsWithoutOpeningTheReferenceProject()
    {
        Project("App/App", Options(11, "LOCAL") + References("../Core/Core.csproj"));
        Project("Core/Core");
        Write("Core/Api.cs", "public class Api {}");
        var file = Write("App/Run.cs", """
            #if LOCAL
            class Runner { Api Bad() => null; }
            #endif
            """);

        var input = new SourceImporter().ReadFiles([file], TestContext.Current.CancellationToken);
        var result = new AnalysisOrchestrator().Analyze(input);

        Assert.Single(input.Documents);
        Assert.Contains(result.Issues, issue => issue.Reason.Contains("파일 선택"));
        Assert.Equal(MethodFlowStatus.Unavailable, Flow(result, ".Bad(").Status);
    }

    [Fact]
    public void ProjectInputCapDistinguishesOmittedAndMissingTargets()
    {
        var names = Enumerable.Range(0, 128).Select(index => $"Libraries/P{index:000}/Lib").ToArray();
        Project("App", References([.. names.Select(name => name + ".csproj"), "Never.csproj"]));
        foreach (var (name, index) in names.Select((name, index) => (name, index)))
        {
            Project(name);
            Write(name + ".cs", $"public class T{index} {{ }}");
        }
        Write("Run.cs", $"class Runner {{ int Good() => 1; {string.Join(" ", names.Select((_, index) => $"T{index} Use{index}() => null;"))} }}");

        var result = Analyze();

        Assert.Contains(result.Issues, issue => issue.FileName == "App.csproj" &&
            issue.Reason.Contains("개수 한도로 생략됨"));
        Assert.Contains(result.Issues, issue => issue.FileName == "App.csproj" &&
            issue.Reason.Contains("Never.csproj") && issue.Reason.Contains("대상을 찾을 수 없음"));
        Assert.Equal(MethodFlowStatus.Analyzed, Flow(result, ".Good(").Status);
        var unavailable = Assert.Single(result.ControlFlow!, flow => flow.Status == MethodFlowStatus.Unavailable);
        Assert.StartsWith("Runner.Use", unavailable.MethodName);
    }

    [Fact]
    public void MixedProjectReferenceResultRoundTripsThroughJsonV1AndFourExcelSheets()
    {
        Project("App/App", References("../Core/Core.csproj"));
        Project("Core/Core");
        Write("Core/Api.cs", "public static class Api { public static bool Flag => true; }");
        Write("App/Run.cs", "class Runner { int Run() => Api.Flag ? 1 : 0; }");
        Write("tool.py", """
            try: work()
            except Exception: pass
            try: work()
            except: recover()
            try: work()
            except Exception as error: raise error

            def classify(flag, enabled):
                if flag and enabled:
                    return 1
                return 0
            """);
        Write("invalid.pyi", "data = (\n");
        var analysis = Analyze();
        Assert.Equal(new[] { "PY-001", "PY-002", "PY-003", "PY-004" },
            analysis.Findings.Select(finding => finding.RuleId).Order());

        var historyDirectory = Path.Combine(scratch.FullName, "history");
        var history = new JsonAnalysisHistory(historyDirectory);
        history.Save(analysis);
        using var saved = JsonDocument.Parse(File.ReadAllText(Assert.Single(Directory.GetFiles(historyDirectory))));
        Assert.Equal(1, saved.RootElement.GetProperty("version").GetInt32());
        Write("history/legacy.json", """
            {
              "version": 1,
              "result": {
                "analyzedAt": "2026-11-16T09:00:00+09:00",
                "target": "이전 이력",
                "analyzedFileCount": 1,
                "findings": [],
                "issues": [{"fileName": "Missing.cs", "reason": "읽기 실패"}]
              }
            }
            """);
        var entries = history.Read();
        Assert.Equal(0, entries.SkippedCount);
        var legacy = Assert.Single(entries.Entries, result => result.Target == "이전 이력");
        Assert.Equal(AnalysisStatus.Partial, legacy.Status);
        Assert.False(Assert.Single(legacy.Issues).IsInformational);
        var loaded = Assert.Single(entries.Entries, result => result.Target == analysis.Target);
        Assert.Equal(AnalysisStatus.Succeeded, loaded.Status);
        Assert.Equal(Assert.Single(analysis.Issues), Assert.Single(loaded.Issues));
        Assert.Equal(2, Flow(loaded, ".Run(").Complexity);
        Assert.Equal(3, Assert.Single(loaded.ControlFlow!, flow => flow.MethodName == "classify").Complexity);

        var path = Path.Combine(scratch.FullName, "export.xlsx");
        new ExcelReportExporter().Export(loaded, path);
        using var workbook = new XLWorkbook(path);
        Assert.Equal(new[] { "분석 요약", "상세 분석 결과", "분석 한계 및 참고사항", "제어 흐름 분석" },
            workbook.Worksheets.Select(sheet => sheet.Name));
        var notes = workbook.Worksheet(3);
        Assert.Equal("분석 실패 항목", notes.Cell("A8").GetString());
        Assert.Equal("없음", notes.Cell("B8").GetString());
        Assert.Contains(notes.Column(2).CellsUsed(), cell => cell.GetString() == loaded.Issues[0].Reason);
    }

    [Fact]
    public void ReanalysisUsesEditedDependencySource()
    {
        Project("App/App", References("../Core/Core.csproj"));
        Project("Core/Core");
        Write("Core/Api.cs", "public static class Api { public static bool Flag => true; }");
        Write("App/Run.cs", "class Runner { int Run() => Api.Flag ? 1 : 0; }");
        var input = Import();
        var analyzer = new AnalysisOrchestrator();
        Assert.Equal(2, Flow(analyzer.Analyze(input), ".Run(").Complexity);

        var edited = input with
        {
            Documents = input.Documents.Select(document => document.FileName.EndsWith("Api.cs", StringComparison.Ordinal)
                ? document with { Code = "public static class Api { public static string Flag => \"changed\"; }" } : document).ToArray()
        };
        Assert.Equal(MethodFlowStatus.Unavailable, Flow(analyzer.Analyze(edited), ".Run(").Status);
    }

    private SourceInput Import() => new SourceImporter().ReadFolder(scratch.FullName, TestContext.Current.CancellationToken);

    private AnalysisResult Analyze() => new AnalysisOrchestrator().Analyze(Import());

    private static MethodFlow Flow(AnalysisResult result, string name) =>
        Assert.Single(result.ControlFlow!, method => method.MethodName.Contains(name));

    private static string References(params string[] paths) =>
        $"<ItemGroup>{string.Concat(paths.Select(path => $"""<ProjectReference Include="{path}" />"""))}</ItemGroup>";

    private static string Options(int version, string symbols) =>
        $"<PropertyGroup><LangVersion>{version}.0</LangVersion><DefineConstants>{symbols}</DefineConstants></PropertyGroup>";

    private void Project(string name, string elements = "") =>
        Write(name + ".csproj", $"""<Project Sdk="Microsoft.NET.Sdk">{elements}</Project>""");

    private string Write(string name, string text)
    {
        var path = Path.Combine(scratch.FullName, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return path;
    }

    public void Dispose() => scratch.Delete(recursive: true);
}
