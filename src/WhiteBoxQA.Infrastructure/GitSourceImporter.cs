using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using WhiteBoxQA.Core;
using static WhiteBoxQA.Infrastructure.SourceImportLimits;

namespace WhiteBoxQA.Infrastructure;

public sealed class GitSourceImporter
{
    private const int MaxProjectTotalBytes = 2 * 1024 * 1024;
    private const long MaxTemporaryBytes = 256L * 1024 * 1024;
    private static readonly TimeSpan DiskCheckInterval = TimeSpan.FromMilliseconds(500);
    private const int MaxIssueDetails = 200;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    private readonly TimeSpan timeout;
    private readonly GitProcess git;
    private readonly long diskLimitBytes;

    public GitSourceImporter() : this(TimeSpan.FromMinutes(2), null) { }

    internal GitSourceImporter(TimeSpan timeout, Action<ProcessStartInfo>? configureProcess,
        long diskLimitBytes = MaxTemporaryBytes)
    {
        this.timeout = timeout;
        this.diskLimitBytes = diskLimitBytes;
        git = new(configureProcess);
    }

    public static bool IsSupportedUrl(string value) => SourceOrigin.TryGetPublicGitUrl(value, out _);

    public Task<GitImportResult> ReadAsync(string url, IProgress<GitImportProgress>? progress = null,
        CancellationToken cancellationToken = default) =>
        Task.Run(() => Read(url, progress, cancellationToken));

    private GitImportResult Read(string url, IProgress<GitImportProgress>? progress, CancellationToken userToken)
    {
        progress?.Report(new(GitImportStage.Validating));
        if (!SourceOrigin.TryGetPublicGitUrl(url, out var target))
            return new(GitImportStatus.Failed, null, "인증 정보가 없는 공개 HTTPS Git 저장소 주소를 입력하세요.");

        string? commitSha = null;
        string? branch = null;
        var documents = new List<SourceDocument>();
        var inventory = new SourceInventory();
        var issues = new List<AnalysisIssue>();
        var hiddenIssues = 0;
        void AddIssue(string file, string reason)
        {
            if (issues.Count < MaxIssueDetails)
                issues.Add(new(file, reason));
            else
                hiddenIssues++;
        }

        var status = GitImportStatus.Completed;
        var message = "";
        var cleanupFailed = false;
        DirectoryInfo? temp = null;
        using var deadline = new CancellationTokenSource(timeout);
        using var diskLimit = new CancellationTokenSource();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(userToken, deadline.Token, diskLimit.Token);
        var token = linked.Token;
        GitImportStatus CancellationStatus() => userToken.IsCancellationRequested ? GitImportStatus.Cancelled
            : diskLimit.IsCancellationRequested ? GitImportStatus.DiskLimitExceeded : GitImportStatus.TimedOut;

        try
        {
            token.ThrowIfCancellationRequested();
            temp = Directory.CreateTempSubdirectory("whiteboxqa-");
            var repo = Path.Combine(temp.FullName, "repository.git");

            Clone(temp, target, repo, progress, diskLimit, token);

            progress?.Report(new(GitImportStage.Listing));
            var (headExit, head) = ReadGitLine(temp.FullName, repo,
                ["rev-parse", "--verify", "--quiet", "HEAD^{commit}"], token);
            if (headExit == 1)
            {
                AddIssue(target, "Git 저장소가 비어 있거나 기본 브랜치에 커밋이 없습니다.");
            }
            else
            {
                commitSha = head is null ? null : Encoding.ASCII.GetString(head);
                if (headExit != 0 || commitSha is null || !SourceOrigin.IsGitObjectId(commitSha))
                    throw new GitCommandException();

                ImportSources(temp.FullName, repo, target, commitSha, documents, inventory, AddIssue, progress, token);
            }

            branch = ReadBranch(temp.FullName, repo, token);

            progress?.Report(new(GitImportStage.Preparing));
        }
        catch (OperationCanceledException)
        {
            status = CancellationStatus();
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode == 2)
        {
            (status, message) = (GitImportStatus.Failed,
                "Git을 찾을 수 없습니다. Git for Windows를 설치하고 PATH에 등록한 뒤 앱을 다시 시작하세요.");
        }
        catch (Exception exception) when (exception is GitCommandException or Win32Exception
                                           or InvalidDataException or IOException or UnauthorizedAccessException)
        {
            (status, message) = (GitImportStatus.Failed,
                "Git 저장소를 읽을 수 없습니다. 공개 HTTPS 주소와 연결 상태를 확인하세요.");
        }
        finally
        {
            if (temp is not null)
                cleanupFailed = !TryDeleteCloneDirectory(temp);
        }

        if (status == GitImportStatus.Completed && token.IsCancellationRequested)
            status = CancellationStatus();

        if (status != GitImportStatus.Completed)
        {
            message = status switch
            {
                GitImportStatus.Cancelled => "Git 가져오기를 취소했습니다.",
                GitImportStatus.TimedOut => "Git 가져오기가 2분을 초과했습니다. 연결이나 저장소 크기를 확인하세요.",
                GitImportStatus.DiskLimitExceeded =>
                    $"Git 임시 파일이 {diskLimitBytes / (1024d * 1024):0.##} MiB 한도를 초과하여 가져오기를 중단했습니다.",
                _ => message
            };
            if (cleanupFailed)
                message += " 임시 폴더를 자동으로 정리하지 못했습니다.";
            return new(status, null, message);
        }

        if (documents.Count == 0 && issues.Count == 0)
            AddIssue(target, $"기본 브랜치에 가져올 지원 소스 파일({AnalysisScope.SupportedLanguages})이 없습니다.");
        if (hiddenIssues != 0)
            issues.Add(new(target, $"그 외 건너뛴 항목이 {hiddenIssues:N0}개 있습니다."));
        if (cleanupFailed)
            issues.Add(new(target, "Git 임시 폴더를 자동으로 정리하지 못했습니다."));

        progress?.Report(new(GitImportStage.Completed));
        return new(status, new(target, documents, issues)
        {
            Origin = new(SourceKind.Git, target, commitSha, branch, DateTimeOffset.UtcNow),
            LanguageInventory = inventory.Snapshot()
        }, "Git 저장소 가져오기를 완료했습니다.");
    }

    private (int ExitCode, byte[]? Line) ReadGitLine(string home, string repo, string[] args, CancellationToken token)
    {
        byte[]? line = null;
        var exit = git.Run(home, repo, args, token, (_, output) =>
        {
            line = GitProcess.ReadRecord(output, (byte)'\n', 16 * 1024, token);
            if (output.ReadByte() != -1)
                throw new InvalidDataException();
        });
        return (exit, line);
    }

    private string? ReadBranch(string home, string repo, CancellationToken token)
    {
        var (exit, line) = ReadGitLine(home, repo, ["symbolic-ref", "--quiet", "HEAD"], token);
        const string prefix = "refs/heads/";
        if (exit != 0 || line is null)
            return null;

        try
        {
            var reference = StrictUtf8.GetString(line);
            return reference.StartsWith(prefix, StringComparison.Ordinal) ? reference[prefix.Length..] : null;
        }
        catch (DecoderFallbackException)
        {
            return null;
        }
    }

    private void Clone(DirectoryInfo temp, string target, string repo, IProgress<GitImportProgress>? progress,
        CancellationTokenSource diskLimit, CancellationToken token)
    {
        progress?.Report(new(GitImportStage.Cloning));
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(token);
        var monitor = MonitorCloneDiskAsync(temp, progress, diskLimit, stop.Token);
        int exit;
        try
        {
            exit = git.Run(temp.FullName, temp.FullName,
                ["clone", "--quiet", "--bare", "--no-local", "--depth", "1", "--single-branch",
                 "--no-tags", "--template=", "--", target, repo], token);
        }
        finally
        {
            stop.Cancel();
            monitor.GetAwaiter().GetResult();
        }

        token.ThrowIfCancellationRequested();
        // A short clone can finish before the first sample.
        CheckCloneDisk(temp, progress, diskLimit, token);
        token.ThrowIfCancellationRequested();
        if (exit != 0)
            throw new GitCommandException();
    }

    private async Task MonitorCloneDiskAsync(DirectoryInfo temp, IProgress<GitImportProgress>? progress,
        CancellationTokenSource diskLimit, CancellationToken stop)
    {
        using var timer = new PeriodicTimer(DiskCheckInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(stop).ConfigureAwait(false))
                CheckCloneDisk(temp, progress, diskLimit, stop);
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
    }

    private void CheckCloneDisk(DirectoryInfo temp, IProgress<GitImportProgress>? progress,
        CancellationTokenSource diskLimit, CancellationToken stop)
    {
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = false,
            AttributesToSkip = FileAttributes.ReparsePoint
        };
        var bytes = 0L;
        try
        {
            foreach (var file in temp.EnumerateFiles("*", options))
            {
                stop.ThrowIfCancellationRequested();
                bytes += file.Length;
                if (bytes > diskLimitBytes)
                {
                    diskLimit.Cancel();
                    return;
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A pack may be renamed between enumeration and measurement.
            progress?.Report(new(GitImportStage.Cloning));
            return;
        }

        progress?.Report(new(GitImportStage.Cloning, TemporaryBytes: bytes));
    }

    private void ImportSources(string home, string repo, string target, string commitSha,
        List<SourceDocument> documents, SourceInventory inventory, Action<string, string> addIssue,
        IProgress<GitImportProgress>? progress, CancellationToken token)
    {
        var sources = new List<RepositoryFile>();
        var projects = new List<RepositoryFile>();
        var coverage = new CSharpImportCoverage(StringComparer.Ordinal);
        var listExit = git.Run(home, repo, ["ls-tree", "-r", "-l", "-z", commitSha], token,
            (_, stream) => ListSources(target, stream, sources, projects, inventory, coverage, addIssue, token));
        if (listExit != 0)
            throw new GitCommandException();

        progress?.Report(new(GitImportStage.Reading, 0, sources.Count));
        if (sources.Count == 0 && projects.Count == 0)
            return;

        var projectSources = new List<CSharpProjectReader.ProjectSource>();
        try
        {
            var exit = git.Run(home, repo, ["cat-file", "--batch"], token, (process, output) =>
            {
                using var input = process.StandardInput;
                input.NewLine = "\n";
                input.AutoFlush = true;
                var totalBytes = 0L;
                var processed = 0;
                foreach (var source in sources)
                {
                    token.ThrowIfCancellationRequested();
                    string? text = null;
                    if (source.Size > MaxFileBytes)
                        addIssue(source.Path, FileSizeLimit);
                    else if (totalBytes + source.Size > MaxTotalBytes)
                        addIssue(source.Path, TotalSizeLimit);
                    else
                    {
                        totalBytes += source.Size;
                        text = ReadBlob(input, output, source, addIssue, token);
                    }
                    if (text is not null)
                        documents.Add(new(source.Path, text));
                    else if (LanguageIdentification.Identify(source.Path) == SourceLanguage.CSharp)
                        coverage.Sources.Add(source.Path);

                    progress?.Report(new(GitImportStage.Reading, ++processed, sources.Count));
                }

                var projectBytes = 0L;
                foreach (var project in projects)
                {
                    token.ThrowIfCancellationRequested();
                    string? xml = null;
                    var exceedsByteLimit = project.Size > CSharpProjectReader.MaxProjectBytes ||
                        projectBytes + project.Size > MaxProjectTotalBytes;
                    if (exceedsByteLimit)
                        addIssue(project.Path, "프로젝트 설정의 개별 256 KiB 또는 전체 2 MiB 제한을 넘어 건너뛰었습니다.");
                    else
                    {
                        projectBytes += project.Size;
                        xml = ReadBlob(input, output, project, addIssue, token);
                    }
                    projectSources.Add(new(project.Path, xml, exceedsByteLimit));
                }
            }, redirectInput: true);

            if (exit != 0)
                throw new GitCommandException();
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or GitCommandException)
        {
            token.ThrowIfCancellationRequested();
            coverage.Sources.HasUntrackedPaths = true;
            coverage.Projects.HasUntrackedPaths = true;
            addIssue(target, "Git 파일 일괄 읽기가 중단되었습니다. 앞서 읽은 파일은 분석할 수 있습니다.");
        }

        token.ThrowIfCancellationRequested();
        CSharpProjectReader.Attach(null, documents, projectSources, addIssue, useLocalReferences: false,
            cancellationToken: token, coverage: coverage, inputLabel: target);
    }

    private static void ListSources(string target, Stream output, List<RepositoryFile> sources,
        List<RepositoryFile> projects, SourceInventory inventory, CSharpImportCoverage coverage,
        Action<string, string> addIssue, CancellationToken token)
    {
        var beyondLimit = 0;
        var beyondProjectLimit = 0;
        while (GitProcess.ReadRecord(output, 0, 64 * 1024, token) is { } record)
        {
            var separator = Array.IndexOf(record, (byte)'\t');
            if (separator < 0)
                throw new InvalidDataException();

            var metadata = Encoding.ASCII.GetString(record, 0, separator)
                .Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (metadata.Length != 4)
                throw new InvalidDataException();

            string path;
            try { path = StrictUtf8.GetString(record, separator + 1, record.Length - separator - 1); }
            catch (DecoderFallbackException)
            {
                addIssue(target, "UTF-8로 해석할 수 없는 Git 파일 경로를 건너뛰었습니다.");
                continue;
            }

            var isSubmodule = metadata[1] == "commit";
            if (SourceImporter.IsExcludedRepositoryPath(isSubmodule ? path + "/" : path))
                continue;

            if (isSubmodule)
            {
                addIssue(path, "Git 서브모듈은 가져오기 범위에서 제외됩니다.");
                continue;
            }

            var language = LanguageIdentification.Identify(path);
            var isProject = CSharpProjectReader.IsProject(path);
            if (language == SourceLanguage.Unknown && !isProject)
                continue;
            if (language != SourceLanguage.Unknown)
                inventory.Add(language);
            if (!LanguageIdentification.HasSourceReader(language) && !isProject)
                continue;

            if (metadata[0] == "120000")
            {
                addIssue(path, "Git에 기록된 심볼릭 링크는 건너뜁니다.");
                if (language == SourceLanguage.CSharp)
                    coverage.Sources.Add(path);
                continue;
            }

            if (metadata[1] != "blob" || !SourceOrigin.IsGitObjectId(metadata[2]) ||
                !long.TryParse(metadata[3], NumberStyles.None, CultureInfo.InvariantCulture, out var size))
            {
                addIssue(path, "Git 파일 객체와 크기를 확인할 수 없습니다.");
                if (language == SourceLanguage.CSharp)
                    coverage.Sources.Add(path);
                continue;
            }

            if (isProject)
            {
                if (projects.Count >= CSharpProjectReader.MaxProjects)
                {
                    beyondProjectLimit++;
                    coverage.Projects.Add(path);
                }
                else
                    projects.Add(new(path, metadata[2], size));
            }
            else if (sources.Count >= MaxFiles)
            {
                beyondLimit++;
                if (language == SourceLanguage.CSharp)
                    coverage.Sources.Add(path);
            }
            else
                sources.Add(new(path, metadata[2], size));
        }

        if (beyondLimit != 0)
            addIssue(target, FileCountLimit(beyondLimit));
        if (beyondProjectLimit != 0)
            addIssue(target, $"프로젝트 설정 {beyondProjectLimit:N0}개를 가져오기 한도로 건너뛰었습니다.");
    }

    private static string? ReadBlob(StreamWriter input, Stream output, RepositoryFile source,
        Action<string, string> addIssue, CancellationToken token)
    {
        input.WriteLine(source.ObjectId);
        var record = GitProcess.ReadRecord(output, (byte)'\n', 256, token) ?? throw new EndOfStreamException();
        var header = Encoding.ASCII.GetString(record).Split(' ');
        if (header.Length == 2 && header[0] == source.ObjectId && header[1] == "missing")
        {
            addIssue(source.Path, "Git 객체가 누락되어 파일을 읽을 수 없습니다.");
            return null;
        }

        if (header.Length != 3 || header[0] != source.ObjectId || header[1] != "blob" ||
            !long.TryParse(header[2], NumberStyles.None, CultureInfo.InvariantCulture, out var size) ||
            size != source.Size)
            throw new InvalidDataException();

        var bytes = new byte[(int)size];
        output.ReadExactly(bytes);
        if (output.ReadByte() != '\n')
            throw new InvalidDataException();

        try { return SourceImporter.DecodeSource(bytes, LanguageIdentification.Identify(source.Path)); }
        catch (InvalidDataException exception)
        {
            addIssue(source.Path, exception.Message);
            return null;
        }
    }

    private static bool TryDeleteCloneDirectory(DirectoryInfo temp)
    {
        const int lastRetryDelay = 800;
        for (var delay = 100; ; delay *= 2)
        {
            try
            {
                DeleteTempDirectory(temp);
                return true;
            }
            catch (DirectoryNotFoundException) when (!Directory.Exists(temp.FullName))
            {
                return true;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                if (delay > lastRetryDelay)
                    return false;

                // A terminated process or a filesystem scanner may briefly hold a pack handle open.
                Thread.Sleep(delay);
            }
        }
    }

    internal static void DeleteTempDirectory(DirectoryInfo temp)
    {
        var options = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint };
        foreach (var file in temp.EnumerateFiles("*", options))
        {
            // Git marks pack files and their indexes read-only on Windows.
            if (file.IsReadOnly)
                file.IsReadOnly = false;
        }

        temp.Delete(recursive: true);
    }

    private sealed record RepositoryFile(string Path, string ObjectId, long Size);

    private sealed class GitCommandException : Exception;
}
