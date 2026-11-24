using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using WhiteBoxQA.Core;
using WhiteBoxQA.Infrastructure;

namespace WhiteBoxQA.Tests;

public sealed class ProjectImportTests : IDisposable
{
    private readonly DirectoryInfo scratch = Directory.CreateTempSubdirectory("whiteboxqa-project-");
    private readonly SourceImporter importer = new();
    private readonly CSharpAnalyzer analyzer = new();

    [Fact]
    public void ProjectsWithSameTypeAndMemberNamesUseDifferentCompilationsAndSymbols()
    {
        Write("A/A.csproj", Project("<PropertyGroup><DefineConstants>FLAG</DefineConstants></PropertyGroup>"));
        Write("B/B.csproj", Project(""));
        foreach (var project in new[] { "A", "B" })
        {
            Write($"{project}/Run.cs", """
                partial class Sample
                {
                    int Run(bool flag) => flag ? GetValue() : 0;
                    void Check()
                    {
                #if FLAG
                        try {} catch {}
                #endif
                    }
                }
                """);
            Write($"{project}/Value.cs", "partial class Sample { int GetValue() => 1; }");
        }

        var input = importer.ReadFolder(scratch.FullName, TestContext.Current.CancellationToken);
        var result = analyzer.Analyze(input);

        Assert.Equal(4, result.AnalyzedFileCount);
        Assert.Empty(result.Issues);
        Assert.Equal(Path.Combine("A", "Run.cs"), Assert.Single(result.Findings).FileName);
        var runs = result.ControlFlow!.Where(method => method.MethodName.Contains(".Run(")).ToArray();
        Assert.Equal(2, runs.Length);
        Assert.All(runs, method =>
        {
            Assert.Equal(MethodFlowStatus.Analyzed, method.Status);
            Assert.Equal(2, method.Complexity);
        });
    }

    [Fact]
    public void SelectedFileUsesNearestProjectAndPinnedLanguageVersion()
    {
        Write("App.csproj", Project("<PropertyGroup><LangVersion>11.0</LangVersion></PropertyGroup>"));
        var path = Write("Nested/Feature.cs", "class C { int Get() { int[] values = [1, 2]; return values[0]; } }");

        var input = importer.ReadFiles([path], TestContext.Current.CancellationToken);
        Assert.Equal(LanguageVersion.CSharp11, Assert.Single(input.Documents).CSharpProject?.Settings.LanguageVersion);
        Assert.Equal(MethodFlowStatus.Unavailable, Assert.Single(analyzer.Analyze(input).ControlFlow!).Status);
        Assert.Equal(MethodFlowStatus.Analyzed,
            Assert.Single(analyzer.Analyze(input with { Documents = [input.Documents[0] with { CSharpProject = null }] })
                .ControlFlow!).Status);
    }

    [Fact]
    public void StaticCompileIncludesAndRemovesLinkFilesWithoutDefaultGlobs()
    {
        Write("Project/App.csproj", Project("""
            <PropertyGroup><EnableDefaultCompileItems>false</EnableDefaultCompileItems><DefineConstants>FLAG</DefineConstants></PropertyGroup>
            <ItemGroup><Compile Include="../Linked.cs;Removed.cs" /><Compile Remove="Removed.cs" /></ItemGroup>
            """));
        const string code = "#if FLAG\nclass C { void M() { try {} catch {} } }\n#endif";
        Write("Linked.cs", code);
        Write("Project/Removed.cs", code);
        Write("Project/Other.cs", code);
        var input = importer.ReadFolder(scratch.FullName, TestContext.Current.CancellationToken);

        Assert.Equal("Linked.cs", Assert.Single(input.Documents, file => file.CSharpProject is not null).FileName);
        Assert.Equal("Linked.cs", Assert.Single(analyzer.Analyze(input).Findings).FileName);
    }

    [Fact]
    public void ConditionsDynamicsImportsAndTargetsAreNeverEvaluated()
    {
        Write("App.csproj", Project("""
            <PropertyGroup><DefineConstants>DIRECT</DefineConstants></PropertyGroup>
            <PropertyGroup Condition="'$(Configuration)' == 'Debug'"><DefineConstants>CONDITIONAL</DefineConstants></PropertyGroup>
            <PropertyGroup><DefineConstants>$(DefineConstants);DYNAMIC</DefineConstants></PropertyGroup>
            <Import Project="Imported.props" />
            <Target Name="BeforeBuild"><WriteLinesToFile File="must-not-exist.txt" Lines="executed" /></Target>
            """));
        Write("Imported.props", "<Project><PropertyGroup><DefineConstants>IMPORTED</DefineConstants></PropertyGroup></Project>");
        Write("A.cs", """
            class A {
              void M() {
            #if DIRECT
                try {} catch {}
            #endif
            #if CONDITIONAL || DYNAMIC || IMPORTED
                unexpected } invalid {
            #endif
              }
            }
            """);
        var input = importer.ReadFolder(scratch.FullName, TestContext.Current.CancellationToken);
        var result = analyzer.Analyze(input);
        Assert.Equal(AnalysisStatus.Partial, result.Status);
        Assert.Equal("WB-001", Assert.Single(result.Findings).RuleId);
        Assert.Contains(result.Issues, issue => issue.Reason.Contains("조건부 또는 동적"));
        Assert.Contains(result.Issues, issue => issue.Reason.Contains("가져온 설정"));
        Assert.False(File.Exists(Path.Combine(scratch.FullName, "must-not-exist.txt")));
    }

    [Fact]
    public void MissingReferencesAreReportedAndIndependentMethodsStillWork()
    {
        Write("App.csproj", Project("""
            <ItemGroup>
                <ProjectReference Include="Missing.csproj" />
                <PackageReference Include="Fixture.Package" Version="1.0.0" />
                <Reference Include="MissingBinary"><HintPath>Missing.dll</HintPath></Reference>
            </ItemGroup>
            """));
        Write("A.cs", "class A { int Good(bool flag) => flag ? 1 : 0; Missing Bad() => null; }");

        var result = analyzer.Analyze(importer.ReadFolder(scratch.FullName, TestContext.Current.CancellationToken));

        Assert.Contains(result.Issues, issue => issue.Reason.Contains("Missing.csproj"));
        Assert.Contains(result.Issues, issue => issue.Reason.Contains("PackageReference"));
        Assert.Contains(result.Issues, issue => issue.Reason.Contains("HintPath"));
        Assert.Equal(2, Assert.Single(result.ControlFlow!, method => method.MethodName.Contains(".Good(")).Complexity);
        Assert.Equal(MethodFlowStatus.Unavailable,
            Assert.Single(result.ControlFlow!, method => method.MethodName.Contains(".Bad(")).Status);
    }

    [Fact]
    public void ReadsLocalManagedHintPathAsMetadataAndDoesNotLoadTheAssembly()
    {
        var tree = CSharpSyntaxTree.ParseText("public class FixtureType { public static bool Flag => true; }",
            cancellationToken: TestContext.Current.CancellationToken);
        var library = CSharpCompilation.Create("WhiteBoxProjectMetadataFixture", [tree],
            [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)],
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var dll = Path.Combine(scratch.FullName, "Fixture.dll");
        Assert.True(library.Emit(dll, cancellationToken: TestContext.Current.CancellationToken).Success);
        Write("App.csproj", Project("""
            <ItemGroup><Reference Include="WhiteBoxProjectMetadataFixture"><HintPath>Fixture.dll</HintPath></Reference></ItemGroup>
            """));
        Write("A.cs", "class A { int M() => FixtureType.Flag ? 1 : 0; }");

        var result = analyzer.Analyze(importer.ReadFolder(scratch.FullName, TestContext.Current.CancellationToken));

        Assert.Empty(result.Issues);
        Assert.Equal(2, Assert.Single(result.ControlFlow!).Complexity);
        Assert.DoesNotContain(AppDomain.CurrentDomain.GetAssemblies(),
            assembly => assembly.GetName().Name == "WhiteBoxProjectMetadataFixture");
    }

    [Theory]
    [InlineData("<Project><PropertyGroup>")]
    [InlineData("<!DOCTYPE Project [<!ENTITY blocked SYSTEM 'file:///nonexistent.txt'>]><Project>&blocked;</Project>")]
    public void InvalidProjectXmlDoesNotDiscardSource(string xml)
    {
        Write("App.csproj", xml);
        Write("A.cs", "class A { void M() { try {} catch {} } }");

        var result = analyzer.Analyze(importer.ReadFolder(scratch.FullName, TestContext.Current.CancellationToken));

        Assert.Equal("WB-001", Assert.Single(result.Findings).RuleId);
        Assert.Contains("프로젝트 XML", Assert.Single(result.Issues).Reason);
    }

    [Fact]
    public void OversizedProjectConfigurationKeepsTheSourceAvailable()
    {
        Write("App.csproj", Project(new string(' ', 256 * 1024)));
        Write("A.cs", "class A { void M() { try {} catch {} } }");

        var result = analyzer.Analyze(importer.ReadFolder(scratch.FullName, TestContext.Current.CancellationToken));

        Assert.Equal(1, result.AnalyzedFileCount);
        Assert.Equal("WB-001", Assert.Single(result.Findings).RuleId);
        Assert.Contains("256 KiB", Assert.Single(result.Issues).Reason);
    }

    private static string Project(string elements) => $"""<Project Sdk="Microsoft.NET.Sdk">{elements}</Project>""";

    private string Write(string name, string text)
    {
        var path = Path.Combine(scratch.FullName, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return path;
    }

    public void Dispose() => scratch.Delete(recursive: true);
}
