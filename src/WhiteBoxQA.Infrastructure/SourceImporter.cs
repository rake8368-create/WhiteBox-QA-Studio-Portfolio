using System.Text;
using WhiteBoxQA.Core;

namespace WhiteBoxQA.Infrastructure;

public sealed class SourceImporter
{
    private const string NodeModulesFolder = "node_modules";
    private static readonly HashSet<string> ExcludedFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".vs", "bin", "obj", NodeModulesFolder
    };

    public SourceInput ReadFiles(IEnumerable<string> paths) => ReadFiles(paths, CancellationToken.None);

    public SourceInput ReadFiles(IEnumerable<string> paths, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var files = paths.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var target = files.Length == 1 ? files[0] : $"로컬 파일 {files.Length}개";
        var documents = new List<SourceDocument>();
        var issues = new List<AnalysisIssue>();
        var inventory = new SourceInventory();
        var budget = new LocalReadBudget();

        foreach (var path in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var fileName = files.Length == 1 ? Path.GetFileName(path) : path;
            var language = LanguageIdentification.Identify(path);
            if (language != SourceLanguage.Unknown)
                inventory.Add(language);
            if (LanguageIdentification.HasSourceReader(language))
                budget.ReadDocument(path, fileName, language, documents, issues);
            else if (language == SourceLanguage.Unknown)
                issues.Add(AnalysisOrchestrator.Unsupported(fileName));
        }

        var csharp = documents.Where(document => LanguageIdentification.Identify(document.FileName) == SourceLanguage.CSharp);
        var selected = files.Length == 1 ? files[0] : null;
        var projects = DiscoverSelectedProjects(csharp.Select(document => selected ?? document.FileName), issues, cancellationToken);
        CSharpProjectReader.Attach(null, documents, projects, (path, reason) => issues.Add(new(path, reason)),
            singleFile: selected, cancellationToken: cancellationToken);
        budget.ReportSkipped(target, issues);

        return new(target, documents, issues)
        {
            Origin = new(SourceKind.LocalFiles),
            LanguageInventory = inventory.Snapshot()
        };
    }

    public SourceInput ReadFolder(string root) => ReadFolder(root, CancellationToken.None);

    public SourceInput ReadFolder(string root, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var documents = new List<SourceDocument>();
        var issues = new List<AnalysisIssue>();
        var projects = new List<CSharpProjectReader.ProjectSource>();
        var inventory = new SourceInventory();
        var budget = new LocalReadBudget();
        var omittedProjects = 0;
        var coverage = new CSharpImportCoverage(OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        var pending = new Stack<string>();
        var resolvedRoot = Path.GetFullPath(root);
        if (resolvedRoot.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Contains(NodeModulesFolder, StringComparer.OrdinalIgnoreCase))
        {
            return new(root, [], [new(root, "node_modules 아래는 폴더 자동 탐색에서 제외됩니다. 필요한 파일은 개별 선택하세요.")])
            {
                Origin = new(SourceKind.LocalFolder)
            };
        }
        pending.Push(resolvedRoot);

        while (pending.TryPop(out var folder))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                foreach (var entry in new DirectoryInfo(folder).EnumerateFileSystemInfos())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var attributes = entry.Attributes;
                    var isDirectory = attributes.HasFlag(FileAttributes.Directory);
                    var language = isDirectory ? SourceLanguage.Unknown : LanguageIdentification.Identify(entry.Name);
                    var isProject = !isDirectory && CSharpProjectReader.IsProject(entry.Name);
                    if (isDirectory ? ExcludedFolders.Contains(entry.Name) : language == SourceLanguage.Unknown && !isProject)
                        continue;

                    if (language != SourceLanguage.Unknown)
                        inventory.Add(language);
                    if (language != SourceLanguage.Unknown && !LanguageIdentification.HasSourceReader(language))
                        continue;

                    var relativePath = Path.GetRelativePath(resolvedRoot, entry.FullName);
                    if (attributes.HasFlag(FileAttributes.ReparsePoint))
                    {
                        issues.Add(new(relativePath, "링크 또는 연결 지점은 폴더 가져오기에서 건너뜁니다."));
                        if (language == SourceLanguage.CSharp)
                            coverage.Sources.Add(relativePath);
                    }
                    else if (isDirectory)
                        pending.Push(entry.FullName);
                    else if (isProject)
                    {
                        if (projects.Count < CSharpProjectReader.MaxProjects)
                            projects.Add(CSharpProjectReader.ReadLocal(entry.FullName, relativePath, issues));
                        else
                        {
                            omittedProjects++;
                            coverage.Projects.Add(relativePath);
                        }
                    }
                    else if (!budget.ReadDocument(entry.FullName, relativePath, language, documents, issues)
                        && language == SourceLanguage.CSharp)
                        coverage.Sources.Add(relativePath);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                coverage.Sources.HasUntrackedPaths = true;
                issues.Add(new(Path.GetRelativePath(resolvedRoot, folder), "폴더의 항목을 읽을 수 없습니다. 경로와 접근 권한을 확인하세요."));
            }
        }

        if (omittedProjects > 0)
            issues.Add(new(root, $"프로젝트 설정 {omittedProjects:N0}개를 가져오기 한도로 건너뛰었습니다."));
        CSharpProjectReader.Attach(resolvedRoot, documents, projects, (path, reason) => issues.Add(new(path, reason)),
            cancellationToken: cancellationToken, coverage: coverage);
        budget.ReportSkipped(root, issues);

        return new(root, documents.OrderBy(document => document.FileName, StringComparer.OrdinalIgnoreCase).ToArray(), issues)
        {
            Origin = new(SourceKind.LocalFolder),
            LanguageInventory = inventory.Snapshot()
        };
    }

    private static List<CSharpProjectReader.ProjectSource> DiscoverSelectedProjects(IEnumerable<string> files,
        List<AnalysisIssue> issues, CancellationToken cancellationToken)
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var folders = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (var folder = Path.GetDirectoryName(Path.GetFullPath(file)); folder is not null; folder = Path.GetDirectoryName(folder))
            {
                if (!folders.TryGetValue(folder, out var candidates))
                {
                    try
                    {
                        candidates = Directory.EnumerateFiles(folder, "*.csproj")
                            .Take(CSharpProjectReader.MaxProjects + 1).ToArray();
                    }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                    {
                        candidates = [];
                    }
                    folders.Add(folder, candidates);
                }

                paths.UnionWith(candidates);
                if (candidates.Length > 0 || Directory.Exists(Path.Combine(folder, ".git")) || File.Exists(Path.Combine(folder, ".git")))
                    break;
            }
            if (paths.Count > CSharpProjectReader.MaxProjects)
            {
                issues.Add(new("로컬 파일", "프로젝트 수 한도를 넘어 나머지 설정 검색을 중단했습니다."));
                break;
            }
        }
        return paths.Order(StringComparer.OrdinalIgnoreCase).Take(CSharpProjectReader.MaxProjects)
            .Select(path => CSharpProjectReader.ReadLocal(path, path, issues)).ToList();
    }

    internal static bool IsExcludedRepositoryPath(string path) =>
        path.Split('/').SkipLast(1).Any(ExcludedFolders.Contains);

    private sealed class LocalReadBudget
    {
        private int attempted;
        private int skipped;
        private long totalBytes;

        public bool ReadDocument(string path, string fileName, SourceLanguage language,
            List<SourceDocument> documents, List<AnalysisIssue> issues)
        {
            if (attempted == SourceImportLimits.MaxFiles)
            {
                skipped++;
                return false;
            }

            attempted++;
            try
            {
                using var stream = File.OpenRead(path);
                var size = stream.Length;
                if (size > SourceImportLimits.MaxFileBytes)
                    throw new InvalidDataException(SourceImportLimits.FileSizeLimit);
                if (size + totalBytes > SourceImportLimits.MaxTotalBytes)
                    throw new InvalidDataException(SourceImportLimits.TotalSizeLimit);

                totalBytes += size;
                var bytes = new byte[(int)size];
                stream.ReadExactly(bytes);
                if (stream.ReadByte() != -1)
                    throw new InvalidDataException("파일 크기가 읽는 동안 변경되었습니다. 다시 시도하세요.");
                documents.Add(new(fileName, DecodeSource(bytes, language)));
                return true;
            }
            catch (InvalidDataException exception)
            {
                issues.Add(new(fileName, exception.Message));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                issues.Add(new(fileName, "파일을 읽을 수 없습니다. 파일의 경로와 접근 권한을 확인하세요."));
            }
            return false;
        }

        public void ReportSkipped(string target, List<AnalysisIssue> issues)
        {
            if (skipped > 0)
                issues.Add(new(target, SourceImportLimits.FileCountLimit(skipped)));
        }
    }

    internal static string DecodeSource(ReadOnlySpan<byte> bytes, SourceLanguage language = SourceLanguage.Unknown)
    {
        var code = language == SourceLanguage.Python ? PythonSourceEncoding.Decode(bytes) : DecodeUnicode(bytes);
        if (code.Contains('\0'))
            throw new InvalidDataException("텍스트로 읽을 수 없는 데이터가 포함되어 있습니다.");

        if (code.StartsWith("version https://git-lfs.github.com/spec/v1", StringComparison.Ordinal))
            throw new InvalidDataException("Git LFS 포인터입니다. 원본 소스 파일을 로컬에서 불러오세요.");

        return code;
    }

    private static string DecodeUnicode(ReadOnlySpan<byte> bytes)
    {
        Encoding encoding;
        int bomLength;
        if (bytes.StartsWith(new byte[] { 0xFF, 0xFE, 0, 0 }))
            (encoding, bomLength) = (new UTF32Encoding(false, true, true), 4);
        else if (bytes.StartsWith(new byte[] { 0, 0, 0xFE, 0xFF }))
            (encoding, bomLength) = (new UTF32Encoding(true, true, true), 4);
        else if (bytes.StartsWith(new byte[] { 0xFF, 0xFE }))
            (encoding, bomLength) = (new UnicodeEncoding(false, true, true), 2);
        else if (bytes.StartsWith(new byte[] { 0xFE, 0xFF }))
            (encoding, bomLength) = (new UnicodeEncoding(true, true, true), 2);
        else
            (encoding, bomLength) = (new UTF8Encoding(false, true),
                bytes.StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }) ? 3 : 0);

        try
        {
            return encoding.GetString(bytes[bomLength..]);
        }
        catch (DecoderFallbackException)
        {
            throw new InvalidDataException("파일을 해석할 수 없습니다. UTF-8 또는 BOM이 포함된 유니코드로 저장한 뒤 다시 시도하세요.");
        }
    }
}
