using System.Diagnostics;
using System.Globalization;
using System.Text;
using WhiteBoxQA.Core;
using WhiteBoxQA.Infrastructure;

namespace WhiteBoxQA.Tests;

public sealed class GitSourceImporterTests : IDisposable
{
    private const string FixtureUrl = "https://example.invalid/fixture.git";
    private readonly DirectoryInfo scratch = Directory.CreateTempSubdirectory("whiteboxqa-git-test-");

    [Fact]
    public async Task LocalGitFixtureUsesOneBatchAndReportsEachFile()
    {
        var repo = CreateRepository();
        Write(repo, "A.cs", "class A {}");
        Write(repo, "하위/B.cs", "class B {}");
        Write(repo, "ignore.txt", "ignored");
        CommitFiles(repo);
        var stages = new ProgressRecorder();
        var batches = 0;
        string? temp = null;
        var importer = new GitSourceImporter(TimeSpan.FromSeconds(25), start =>
        {
            if (RedirectFixtureClone(start, repo))
                temp = start.WorkingDirectory;
            if (start.ArgumentList.Contains("cat-file"))
                batches++;
        });

        var result = await importer.ReadAsync(FixtureUrl, stages, TestContext.Current.CancellationToken);

        Assert.Equal(GitImportStatus.Completed, result.Status);
        Assert.NotNull(result.Input);
        Assert.Equal(new[] { "A.cs", "하위/B.cs" }, result.Input.Documents.Select(file => file.FileName));
        Assert.Empty(result.Input.Issues);
        Assert.Equal(1, batches);
        Assert.Contains(stages.Entries, entry => entry.Stage == GitImportStage.Cloning && entry.TemporaryBytes > 0);
        Assert.Equal(new[] { GitImportStage.Validating, GitImportStage.Cloning, GitImportStage.Listing,
            GitImportStage.Reading, GitImportStage.Preparing, GitImportStage.Completed },
            stages.Entries.Select(entry => entry.Stage).Distinct());
        Assert.Equal(new[] { 0, 1, 2 },
            stages.Entries.Where(entry => entry.Stage == GitImportStage.Reading).Select(entry => entry.Processed));
        Assert.Equal("소스 파일 2 / 2개를 처리했습니다.", stages.Entries.Single(
            entry => entry.Stage == GitImportStage.Reading && entry.Processed == 2).Message);
        Assert.NotNull(temp);
        Assert.False(Directory.Exists(temp));
    }

    [Fact]
    public async Task ImportCapturesActualHeadAndReadsThatSnapshot()
    {
        var repo = CreateRepository();
        RunGit(repo, ["branch", "-m", "qa/snapshot"]);
        Write(repo, "AtHead.cs", "class AtHead {}");
        CommitFiles(repo);
        var expectedHead = RunGit(repo, ["rev-parse", "HEAD"]).Trim();
        string? listedRevision = null;
        var importer = new GitSourceImporter(TimeSpan.FromSeconds(25), start =>
        {
            RedirectFixtureClone(start, repo);
            if (start.ArgumentList.Contains("ls-tree"))
                listedRevision = start.ArgumentList[^1];
        });

        var beforeImport = DateTimeOffset.UtcNow;
        var result = await importer.ReadAsync(FixtureUrl, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(GitImportStatus.Completed, result.Status);
        Assert.NotNull(result.Input);
        var origin = Assert.IsType<SourceOrigin>(result.Input.Origin);
        Assert.Equal(SourceKind.Git, origin.Kind);
        Assert.Equal(FixtureUrl, origin.RemoteUrl);
        Assert.Equal(expectedHead, origin.CommitSha);
        Assert.Equal(expectedHead, listedRevision);
        Assert.Equal("qa/snapshot", origin.Branch);
        Assert.NotNull(origin.ImportedAt);
        Assert.InRange(origin.ImportedAt.Value, beforeImport, DateTimeOffset.UtcNow);
        Assert.Equal("AtHead.cs", Assert.Single(result.Input.Documents).FileName);
        Assert.Equal(origin, new AnalysisOrchestrator().Analyze(result.Input).Origin);
    }

    [Fact]
    public async Task EmptyRepositoryReportsNoCommitsWithoutBatchRead()
    {
        var repo = CreateRepository();
        var importer = ForRepository(repo);

        var result = await importer.ReadAsync(FixtureUrl, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(GitImportStatus.Completed, result.Status);
        Assert.NotNull(result.Input);
        Assert.Empty(result.Input.Documents);
        Assert.Contains("커밋이 없습니다", Assert.Single(result.Input.Issues).Reason);
        Assert.Null(Assert.IsType<SourceOrigin>(result.Input.Origin).CommitSha);
        Assert.Equal(AnalysisStatus.Failed, new CSharpAnalyzer().Analyze(result.Input).Status);
    }

    [Fact]
    public async Task GitSnapshotUsesTrackedProjectSettingsAndSummarizesUnsupportedSources()
    {
        var repo = CreateRepository();
        Write(repo, "App/App.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><DefineConstants>IN_SNAPSHOT</DefineConstants></PropertyGroup>
            </Project>
            """);
        Write(repo, "App/A.cs", "#if IN_SNAPSHOT\nclass A { void M() { try {} catch {} } }\n#endif");
        Write(repo, "tools/one.py", "try: run()\nexcept Exception: pass");
        Write(repo, "tools/two.pyi", "def run() -> None: ...");
        Write(repo, "web/main.mjs", "export const value = 1;");
        Write(repo, "web/view.tsx", "const view = <span />; try {} catch {}");
        Write(repo, "web/legacy.c", "invalid as code");
        Write(repo, "obj/ignored.ts", "");
        CommitFiles(repo);

        var import = await ForRepository(repo).ReadAsync(FixtureUrl, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(GitImportStatus.Completed, import.Status);
        Assert.NotNull(import.Input);
        Assert.Equal(5, import.Input.Documents.Count);
        var document = Assert.Single(import.Input.Documents, entry => entry.FileName.EndsWith(".cs"));
        Assert.All(import.Input.Documents.Where(entry => entry != document), entry => Assert.Null(entry.CSharpProject));
        Assert.Equal("App/App.csproj", document.CSharpProject?.ProjectPath);
        var analysis = new AnalysisOrchestrator().Analyze(import.Input);
        Assert.Equal(AnalysisStatus.Partial, analysis.Status);
        Assert.Equal(5, analysis.AnalyzedFileCount);
        Assert.Equal(new[] { "WB-001", "PY-002", "TS-002" }, analysis.Findings.Select(finding => finding.RuleId));
        Assert.Equal(new LanguageFileCount[] { new(SourceLanguage.CSharp, 1, 0) { AnalyzedFileCount = 1 },
            new(SourceLanguage.Python, 2, 0) { AnalyzedFileCount = 2 },
            new(SourceLanguage.JavaScript, 1, 0) { AnalyzedFileCount = 1 },
            new(SourceLanguage.TypeScript, 1, 0) { AnalyzedFileCount = 1 },
            new(SourceLanguage.C, 1, 1) { AnalyzedFileCount = 0 } }, analysis.LanguageInventory);
        Assert.Equal(SourceLanguage.C, Assert.Single(analysis.Issues).UnsupportedLanguage);
    }

    [Fact]
    public async Task JavaUsesHeadSnapshotAndReportsSkippedSourceWithoutLosingItsInventory()
    {
        var repo = CreateRepository();
        Write(repo, "src/Sample.java", "class Sample { void run() { try { work(); } catch (Throwable e) {} } }");
        Write(repo, "src/Pointer.java", "version https://git-lfs.github.com/spec/v1\n");
        Write(repo, "tools/worker.js", "export const job = true;");
        CommitFiles(repo);
        var commit = RunGit(repo, ["rev-parse", "HEAD"]).Trim();
        Write(repo, "src/Sample.java", "class LocalEdits {}");

        var outcome = await ForRepository(repo).ReadAsync(FixtureUrl, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(GitImportStatus.Completed, outcome.Status);
        Assert.NotNull(outcome.Input);
        Assert.Equal(commit, outcome.Input.Origin!.CommitSha);
        Assert.Equal(new[] { "src/Sample.java", "tools/worker.js" }, outcome.Input.Documents.Select(file => file.FileName));
        var result = new AnalysisOrchestrator().Analyze(outcome.Input);
        Assert.Equal(AnalysisStatus.Partial, result.Status);
        Assert.Equal(2, result.AnalyzedFileCount);
        Assert.Contains(new LanguageFileCount(SourceLanguage.Java, 2, 0) { AnalyzedFileCount = 1 }, result.LanguageInventory!);
        Assert.Contains(new LanguageFileCount(SourceLanguage.JavaScript, 1, 0) { AnalyzedFileCount = 1 }, result.LanguageInventory!);
        Assert.Contains(result.Issues, issue => issue.FileName == "src/Pointer.java" && issue.Reason.Contains("LFS"));
        Assert.Equal(new[] { "JAVA-002", "JAVA-003" }, result.Findings.Select(finding => finding.RuleId).Order());
        Assert.Empty(result.ControlFlow!);
    }

    [Fact]
    public async Task CppUsesHeadSnapshotAndKeepsDiscoveredReadAndAnalyzedCounts()
    {
        var repo = CreateRepository();
        Write(repo, "include/Api.HXX", "struct Api {};");
        Write(repo, "src/Broken.CPP", "int broken = ;");
        Write(repo, "src/Oversized.CC", new string('x', SourceImportLimits.MaxFileBytes + 1));
        Write(repo, "src/Main.CXX", "void run() { try {} catch (...) {} }");
        Write(repo, "nested/NoDe_MoDuLeS/Hidden.hpp", "int broken = ;");
        Write(repo, "include/Shared.h", "int shared;");
        Write(repo, "src/Legacy.c", "int legacy;");
        Write(repo, "CMakeLists.txt", "message(FATAL_ERROR \"fixture\")");
        Write(repo, "Makefile", "all:\n\t@exit 1");
        CommitFiles(repo);
        var head = RunGit(repo, ["rev-parse", "HEAD"]).Trim();
        Write(repo, "src/Main.CXX", "int localEdit;");

        var outcome = await ForRepository(repo).ReadAsync(FixtureUrl, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(GitImportStatus.Completed, outcome.Status);
        Assert.NotNull(outcome.Input);
        Assert.Equal(head, outcome.Input.Origin?.CommitSha);
        Assert.Equal(new[] { "include/Api.HXX", "src/Broken.CPP", "src/Main.CXX" },
            outcome.Input.Documents.Select(document => document.FileName));
        Assert.Equal(new LanguageFileCount[] { new(SourceLanguage.C, 1, 1), new(SourceLanguage.Cpp, 4, 0) },
            outcome.Input.LanguageInventory);
        Assert.Contains(outcome.Input.Issues,
            issue => issue.FileName == "src/Oversized.CC" && issue.Reason == SourceImportLimits.FileSizeLimit);

        var result = new AnalysisOrchestrator().Analyze(outcome.Input);

        Assert.Equal(3, result.AnalyzedFileCount);
        Assert.Equal(AnalysisStatus.Partial, result.Status);
        Assert.Contains(new LanguageFileCount(SourceLanguage.Cpp, 4, 0) { AnalyzedFileCount = 3 }, result.LanguageInventory!);
        Assert.Equal(new[] { ("src/Broken.CPP", "CPP-001"), ("src/Main.CXX", "CPP-002") },
            result.Findings.Select(finding => (finding.FileName, finding.RuleId)));
        Assert.Empty(result.ControlFlow!);
    }

    [Fact]
    public async Task GitHeadExcludesNestedDependenciesBeforeDiscoveryAndSourceBudgets()
    {
        var repo = CreateRepository();
        for (var index = 0; index < SourceImportLimits.MaxFiles; index++)
            Write(repo, $"a/NoDe_MoDuLeS/pkg/{index:D4}.js", "const x = ;");
        Write(repo, "a/NoDe_MoDuLeS/pkg/large.jsx", new string('x', SourceImportLimits.MaxFileBytes + 1));
        Write(repo, "node_modules/Hidden.cs", "class Hidden {}");
        Write(repo, "node_modules/hidden.py", "");
        Write(repo, "node_modules/Hidden.java", "");
        Write(repo, "node_modules/Hidden.tsx", "");
        Write(repo, "node_modules_extra/Keep.cs", "class Keep {}");
        Write(repo, "dist/main.MJS", "export const x = 1;");
        Write(repo, "build/main.CJS", "module.exports = true;");
        Write(repo, "vendor/main.js", "const x = 1;");
        Write(repo, "z/nested/View.JSX", "const view = <span />;");
        CommitFiles(repo);
        var head = RunGit(repo, ["rev-parse", "HEAD"]).Trim();
        Write(repo, "z/nested/View.JSX", "const broken = ;");

        var result = await ForRepository(repo).ReadAsync(FixtureUrl, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(GitImportStatus.Completed, result.Status);
        Assert.NotNull(result.Input);
        Assert.Equal(head, result.Input.Origin?.CommitSha);
        Assert.Equal(5, result.Input.Documents.Count);
        var analysis = new AnalysisOrchestrator().Analyze(result.Input);
        Assert.Empty(analysis.Issues);
        Assert.Empty(analysis.Findings);
        Assert.Equal(5, analysis.AnalyzedFileCount);
        Assert.Equal(new LanguageFileCount[]
        {
            new(SourceLanguage.CSharp, 1, 0) { AnalyzedFileCount = 1 },
            new(SourceLanguage.JavaScript, 4, 0) { AnalyzedFileCount = 4 }
        }, analysis.LanguageInventory);
    }

    [Fact]
    public async Task MixedHeadSnapshotKeepsJavaRecoveryScopedToItsFile()
    {
        var repo = CreateRepository();
        Write(repo, "src/Broken.java", "class Broken {\r\n\tString text = \"한😀\"\r\n}");
        Write(repo, "src/Healthy.java", "class Healthy { void run() { try {} catch (java.lang.Throwable e) {} } }");
        Write(repo, "src/Route.cs", "class Route { int Decide(bool enabled) => enabled ? 1 : 0; }");
        Write(repo, "tools/check.py", "def route(a, b):\n    if a and b: return 1\n    return 0\n");
        CommitFiles(repo);
        var commit = RunGit(repo, ["rev-parse", "HEAD"]).Trim();
        Write(repo, "src/Broken.java", "class LocalEdits {}");

        var imported = await ForRepository(repo).ReadAsync(FixtureUrl, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(GitImportStatus.Completed, imported.Status);
        Assert.NotNull(imported.Input);
        var result = new AnalysisOrchestrator().Analyze(imported.Input);
        Assert.Equal(commit, result.Origin?.CommitSha);
        Assert.Equal(AnalysisStatus.Succeeded, result.Status);
        Assert.Equal(4, result.AnalyzedFileCount);
        Assert.Equal(new[] { ("src/Broken.java", "JAVA-001"), ("src/Healthy.java", "JAVA-003"), ("src/Healthy.java", "JAVA-002") },
            result.Findings.Select(finding => (finding.FileName, finding.RuleId)));
        var missing = result.Findings[0];
        Assert.Equal((2, "\tString text = \"한😀\"".Length + 1), (missing.Line, missing.Column));
        Assert.Equal(new[] { (SourceLanguage.CSharp, 2), (SourceLanguage.Python, 3) },
            result.ControlFlow!.Select(method => (method.Language, method.Complexity!.Value)));
    }

    [Fact]
    public async Task GitHeadAndLocalFolderConnectTheSameProjectChain()
    {
        var repo = CreateRepository();
        Write(repo, "App/App.csproj", """
            <Project Sdk="Microsoft.NET.Sdk"><ItemGroup><ProjectReference Include="../Core/Core.csproj" /></ItemGroup></Project>
            """);
        Write(repo, "Core/Core.csproj", """
            <Project Sdk="Microsoft.NET.Sdk"><ItemGroup><ProjectReference Include="..\Shared\Shared.csproj" /></ItemGroup></Project>
            """);
        Write(repo, "Shared/Shared.csproj", """<Project Sdk="Microsoft.NET.Sdk" />""");
        Write(repo, "Shared/Flag.cs", "public record Flag(bool Enabled);");
        Write(repo, "Core/Api.cs", "public static class Api { public static Flag Read() => new(true); }");
        Write(repo, "App/Run.cs", "class Runner { int Run() => Api.Read().Enabled ? 1 : 0; }");
        Write(repo, "test.py", "def check(x):\n    return 1 if x else 0\n");
        CommitFiles(repo);
        var analyzer = new AnalysisOrchestrator();
        var local = analyzer.Analyze(new SourceImporter().ReadFolder(repo, TestContext.Current.CancellationToken));
        // The working copy can change after the commit without affecting the imported snapshot.
        Write(repo, "Core/Core.csproj", "<Project><ItemGroup>");

        var imported = await ForRepository(repo).ReadAsync(FixtureUrl, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(GitImportStatus.Completed, imported.Status);
        Assert.NotNull(imported.Input);
        var result = analyzer.Analyze(imported.Input);
        Assert.NotNull(result.Origin?.CommitSha);
        Assert.Equal(AnalysisStatus.Succeeded, result.Status);
        Assert.Equal(local.Status, result.Status);
        Assert.Equal(local.Issues.Select(issue => issue.Reason.Replace('\\', '/')).Order(),
            result.Issues.Select(issue => issue.Reason).Order());
        Assert.Equal(local.ControlFlow!.Select(flow => (flow.MethodName, flow.Status, flow.Complexity)),
            result.ControlFlow!.Select(flow => (flow.MethodName, flow.Status, flow.Complexity)));
        Assert.Equal(2, Assert.Single(result.ControlFlow!, flow => flow.MethodName.Contains(".Run(")).Complexity);
        Assert.Equal(2, result.Issues.Count);
    }

    [Theory]
    [InlineData("Missing")]
    [InlineData("core")]
    public async Task GitReferenceMustExistWithExactCaseInHead(string folder)
    {
        var repo = CreateRepository();
        Write(repo, "App/App.csproj", $"""
            <Project Sdk="Microsoft.NET.Sdk"><ItemGroup><ProjectReference Include="../{folder}/Core.csproj" /></ItemGroup></Project>
            """);
        Write(repo, "Core/Core.csproj", """<Project Sdk="Microsoft.NET.Sdk" />""");
        Write(repo, "Core/Api.cs", "public class Api {}");
        Write(repo, "App/Run.cs", "class Runner { Api Bad() => null; int Good(bool b) => b ? 1 : 0; }");
        CommitFiles(repo);
        var importer = new GitSourceImporter(TimeSpan.FromSeconds(25), start =>
        {
            RedirectFixtureClone(start, repo);
            if (start.ArgumentList.Contains("ls-tree"))
                Write(start.WorkingDirectory, $"{folder}/Core.csproj", """<Project Sdk="Microsoft.NET.Sdk" />""");
        });

        var imported = await importer.ReadAsync(FixtureUrl, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(GitImportStatus.Completed, imported.Status);
        Assert.NotNull(imported.Input);
        var result = new AnalysisOrchestrator().Analyze(imported.Input);
        Assert.Contains(result.Issues, issue => issue.FileName == "App/App.csproj" &&
            issue.Reason.Contains("대상을 찾을 수 없음"));
        Assert.Equal(MethodFlowStatus.Unavailable, Assert.Single(result.ControlFlow!, flow => flow.MethodName.Contains(".Bad(")).Status);
        Assert.Equal(2, Assert.Single(result.ControlFlow!, flow => flow.MethodName.Contains(".Good(")).Complexity);
        Assert.DoesNotContain(result.Issues, issue => issue.IsInformational);
    }

    [Fact]
    public async Task InvalidTextLfsSymlinksAndSubmodulesLeaveReadableSourceAvailable()
    {
        var repo = CreateRepository();
        Write(repo, "Valid.cs", "class Valid {}");
        CommitFiles(repo);
        var previousCommit = RunGit(repo, ["rev-parse", "HEAD"]).Trim();
        File.WriteAllBytes(Path.Combine(repo, "Binary.cs"), [0xFF, 0xFF, 0x00]);
        Write(repo, "Pointer.cs", "version https://git-lfs.github.com/spec/v1\n");
        RunGit(repo, ["add", "--", "Binary.cs", "Pointer.cs"]);
        var link = RunGit(repo, ["hash-object", "-w", "--stdin"], "Valid.cs").Trim();
        RunGit(repo, ["update-index", "--add", "--cacheinfo", $"120000,{link},Linked.cs"]);
        RunGit(repo, ["update-index", "--add", "--cacheinfo", $"160000,{previousCommit},Submodule"]);
        Commit(repo);

        var result = await ForRepository(repo).ReadAsync(FixtureUrl, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(GitImportStatus.Completed, result.Status);
        Assert.NotNull(result.Input);
        Assert.Equal("Valid.cs", Assert.Single(result.Input.Documents).FileName);
        Assert.Contains(result.Input.Issues, issue => issue.FileName == "Binary.cs" && issue.Reason.Contains("해석"));
        Assert.Contains(result.Input.Issues, issue => issue.FileName == "Pointer.cs" && issue.Reason.Contains("LFS"));
        Assert.Contains(result.Input.Issues, issue => issue.FileName == "Linked.cs" && issue.Reason.Contains("심볼릭"));
        Assert.Contains(result.Input.Issues, issue => issue.FileName == "Submodule" && issue.Reason.Contains("서브모듈"));
        Assert.Equal(AnalysisStatus.Partial, new CSharpAnalyzer().Analyze(result.Input).Status);
    }

    [Fact]
    public async Task PerFileAndAggregateByteLimitsPreventAdditionalBlobReads()
    {
        var repo = CreateRepository();
        var max = 2 * 1024 * 1024;
        Write(repo, "A00.cs", new string(' ', max + 1));
        Write(repo, "B00.py", new string(' ', max + 1));
        Write(repo, "F00.cs", new string(' ', max));
        CommitFiles(repo);
        var blob = RunGit(repo, ["rev-parse", "HEAD:F00.cs"]).Trim();
        for (var index = 1; index <= 16; index++)
            RunGit(repo, ["update-index", "--add", "--cacheinfo", $"100644,{blob},F{index:00}.cs"]);
        Commit(repo);

        var result = await ForRepository(repo).ReadAsync(FixtureUrl, cancellationToken: TestContext.Current.CancellationToken);

        Assert.NotNull(result.Input);
        Assert.Equal(16, result.Input.Documents.Count);
        Assert.Contains(result.Input.Issues, issue => issue.FileName == "A00.cs" && issue.Reason.Contains("2 MiB"));
        Assert.Contains(result.Input.Issues, issue => issue.FileName == "B00.py" && issue.Reason.Contains("2 MiB"));
        Assert.Contains(result.Input.Issues, issue => issue.FileName == "F16.cs" && issue.Reason.Contains("32 MiB"));
    }

    [Fact]
    public async Task FileCountLimitReportsSkippedFiles()
    {
        var repo = CreateRepository();
        var blob = RunGit(repo, ["hash-object", "-w", "--stdin"], "class Fixture {}").Trim();
        var entries = string.Concat(Enumerable.Range(0, 2_001)
            .Select(index => $"100644 {blob}\tFile{index:0000}.cs\n"));
        RunGit(repo, ["update-index", "--index-info"], entries);
        Commit(repo);

        var result = await ForRepository(repo).ReadAsync(FixtureUrl, cancellationToken: TestContext.Current.CancellationToken);

        Assert.NotNull(result.Input);
        Assert.Equal(2_000, result.Input.Documents.Count);
        Assert.Contains("1개를 건너뛰었습니다", Assert.Single(result.Input.Issues).Reason);
    }

    [Fact]
    public async Task CancelStopsProcessTreeCleansTempAndAllowsRetry()
    {
        var repo = CreateRepository();
        Write(repo, "Retry.cs", "class Retry {}");
        Write(repo, "retry.py", "try: run()\nexcept: recover()");
        CommitFiles(repo);
        var marker = Path.Combine(scratch.FullName, "child.pid");
        string? temp = null;
        var blockFirstClone = true;
        var importer = new GitSourceImporter(TimeSpan.FromSeconds(25), start =>
        {
            if (!start.ArgumentList.Contains("clone"))
                return;
            if (blockFirstClone)
            {
                blockFirstClone = false;
                temp = start.WorkingDirectory;
                ConfigureBlockingClone(start, marker);
            }
            else
            {
                RedirectFixtureClone(start, repo);
            }
        });

        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var operation = importer.ReadAsync(FixtureUrl, cancellationToken: cancellation.Token);
        int childId;
        try
        {
            using var startLimit = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            startLimit.CancelAfter(TimeSpan.FromSeconds(12));
            while (!File.Exists(marker) ||
                   !int.TryParse(await File.ReadAllTextAsync(marker, startLimit.Token),
                       CultureInfo.InvariantCulture, out childId))
                await Task.Delay(40, startLimit.Token);
        }
        finally
        {
            await cancellation.CancelAsync();
        }

        var cancelled = await operation.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Equal(GitImportStatus.Cancelled, cancelled.Status);
        Assert.Null(cancelled.Input);
        Assert.Contains("취소", cancelled.Message);
        Assert.NotNull(temp);
        Assert.False(Directory.Exists(temp));
        Assert.True(SpinWait.SpinUntil(() => ProcessEnded(childId), TimeSpan.FromSeconds(5)));

        var retry = await importer.ReadAsync(FixtureUrl, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(new[] { "Retry.cs", "retry.py" }, retry.Input!.Documents.Select(document => document.FileName));
        Assert.Equal("PY-003", Assert.Single(new AnalysisOrchestrator().Analyze(retry.Input).Findings).RuleId);
    }

    [Fact]
    public async Task DiskLimitStopsActiveProcessTreeWithoutReplacingInputAndAllowsRetry()
    {
        var repo = CreateRepository();
        Write(repo, "Retry.cs", "class Retry {}");
        CommitFiles(repo);

        var marker = Path.Combine(scratch.FullName, "disk-child.pid");
        string? temp = null;
        const int limit = 1024 * 1024;
        var importer = new GitSourceImporter(TimeSpan.FromSeconds(25), start =>
        {
            if (!start.ArgumentList.Contains("clone"))
                return;
            if (temp is not null)
            {
                RedirectFixtureClone(start, repo);
                return;
            }

            temp = start.WorkingDirectory;
            ConfigureBlockingClone(start, marker);
        }, diskLimitBytes: limit);

        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var operation = importer.ReadAsync(FixtureUrl, cancellationToken: cancellation.Token);
        int childId;
        GitImportResult stopped;
        try
        {
            using var startLimit = CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token);
            startLimit.CancelAfter(TimeSpan.FromSeconds(12));
            while (!File.Exists(marker) ||
                   !int.TryParse(await File.ReadAllTextAsync(marker, startLimit.Token), CultureInfo.InvariantCulture, out childId))
                await Task.Delay(40, startLimit.Token);
            using (var pack = File.Create(Path.Combine(temp!, "large.pack")))
            {
                pack.SetLength(limit + 1);
                using var stopLimit = CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token);
                stopLimit.CancelAfter(TimeSpan.FromSeconds(8));
                while (!ProcessEnded(childId))
                    await Task.Delay(40, stopLimit.Token);
                // Windows cannot remove this file until the test releases the handle.
                await Task.Delay(200, stopLimit.Token);
                Assert.False(operation.IsCompleted);
            }
            stopped = await operation.WaitAsync(TimeSpan.FromSeconds(8), cancellation.Token);
        }
        finally
        {
            await cancellation.CancelAsync();
            await operation;
        }

        Assert.Equal(GitImportStatus.DiskLimitExceeded, stopped.Status);
        Assert.Contains("임시 파일", stopped.Message);
        Assert.Null(stopped.Input);
        Assert.False(Directory.Exists(temp));
        Assert.True(SpinWait.SpinUntil(() => ProcessEnded(childId), TimeSpan.FromSeconds(5)));

        var retry = await importer.ReadAsync(FixtureUrl, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(GitImportStatus.Completed, retry.Status);
        Assert.Equal("Retry.cs", Assert.Single(retry.Input!.Documents).FileName);
    }

    [Fact]
    public async Task DeadlineHasDifferentStatusAndCleansTemporaryDirectory()
    {
        string? temp = null;
        var importer = new GitSourceImporter(TimeSpan.FromSeconds(1), start =>
        {
            temp = start.WorkingDirectory;
            ConfigurePowerShell(start, "Start-Sleep -Seconds 30");
        });

        var result = await importer.ReadAsync(FixtureUrl, cancellationToken: TestContext.Current.CancellationToken)
            .WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        Assert.Equal(GitImportStatus.TimedOut, result.Status);
        Assert.Contains("초과", result.Message);
        Assert.Null(result.Input);
        Assert.NotNull(temp);
        Assert.False(Directory.Exists(temp));
    }

    [Fact]
    public async Task InvalidAddressDoesNotStartGit()
    {
        var importer = new GitSourceImporter(TimeSpan.FromSeconds(5),
            _ => throw new InvalidOperationException("Git unexpectedly started"));
        var progress = new ProgressRecorder();

        var result = await importer.ReadAsync("https://token@example.invalid/fixture.git", progress,
            TestContext.Current.CancellationToken);

        Assert.Equal(GitImportStatus.Failed, result.Status);
        Assert.Null(result.Input);
        Assert.Equal(GitImportStage.Validating, Assert.Single(progress.Entries).Stage);
        Assert.DoesNotContain("token", result.Message);
    }

    private GitSourceImporter ForRepository(string repo) =>
        new(TimeSpan.FromSeconds(30), start => RedirectFixtureClone(start, repo));

    private static bool RedirectFixtureClone(ProcessStartInfo start, string repo)
    {
        if (!start.ArgumentList.Contains("clone"))
            return false;

        Assert.Equal("https", start.Environment["GIT_ALLOW_PROTOCOL"]);
        Assert.Equal("0", start.Environment["GIT_TERMINAL_PROMPT"]);
        Assert.Equal(start.WorkingDirectory, start.Environment["USERPROFILE"]);
        Assert.Contains("http.followRedirects=false", start.ArgumentList);
        Assert.Contains("credential.helper=", start.ArgumentList);
        start.ArgumentList[^2] = new Uri(repo).AbsoluteUri;
        start.Environment["GIT_ALLOW_PROTOCOL"] = "file";
        return true;
    }

    private static void ConfigureBlockingClone(ProcessStartInfo start, string marker)
    {
        var escaped = marker.Replace("'", "''");
        ConfigurePowerShell(start, $"""
            $child = Start-Process -FilePath powershell.exe -ArgumentList '-NoProfile','-NonInteractive','-Command','Start-Sleep -Seconds 30' -WindowStyle Hidden -PassThru
            [IO.File]::WriteAllText('{escaped}', $child.Id.ToString())
            Wait-Process -Id $child.Id
            """);
    }

    private static void ConfigurePowerShell(ProcessStartInfo start, string script)
    {
        start.FileName = "powershell.exe";
        start.ArgumentList.Clear();
        foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-EncodedCommand",
                     Convert.ToBase64String(Encoding.Unicode.GetBytes(script)) })
            start.ArgumentList.Add(arg);
    }

    private static bool ProcessEnded(int id)
    {
        try
        {
            using var process = Process.GetProcessById(id);
            return process.HasExited;
        }
        catch (ArgumentException) { return true; }
    }

    private string CreateRepository()
    {
        var repo = Directory.CreateDirectory(Path.Combine(scratch.FullName, "fixture")).FullName;
        RunGit(repo, ["init", "--quiet", "--template=", "--initial-branch=main"]);
        return repo;
    }

    private static void Write(string repo, string relative, string code)
    {
        var path = Path.Combine(repo, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, code);
    }

    private static void CommitFiles(string repo)
    {
        RunGit(repo, ["add", "--all"]);
        Commit(repo);
    }

    private static void Commit(string repo) =>
        RunGit(repo, ["-c", "user.name=WhiteBox Fixture", "-c", "user.email=fixture@example.invalid",
            "commit", "--quiet", "-m", "Fixture"]);

    private static string RunGit(string repo, string[] args, string? input = null)
    {
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = repo,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = input is not null,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        if (input is not null)
            start.StandardInputEncoding = Encoding.ASCII;
        foreach (var key in start.Environment.Keys.Where(key => key.StartsWith("GIT_", StringComparison.OrdinalIgnoreCase)).ToArray())
            start.Environment.Remove(key);
        start.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
        start.Environment["GIT_CONFIG_GLOBAL"] = OperatingSystem.IsWindows() ? "NUL" : "/dev/null";
        foreach (var arg in args)
            start.ArgumentList.Add(arg);

        using var process = Process.Start(start)!;
        var error = process.StandardError.ReadToEndAsync();
        var output = process.StandardOutput.ReadToEndAsync();
        if (input is not null)
        {
            process.StandardInput.Write(input);
            process.StandardInput.Close();
        }
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, error.GetAwaiter().GetResult());
        return output.GetAwaiter().GetResult();
    }

    public void Dispose() => GitSourceImporter.DeleteTempDirectory(scratch);

    private sealed class ProgressRecorder : IProgress<GitImportProgress>
    {
        public List<GitImportProgress> Entries { get; } = [];
        public void Report(GitImportProgress value) => Entries.Add(value);
    }
}
