using System.Security;

namespace WhiteBoxQA.Infrastructure;

public sealed class ResultWorkspace
{
    private const string LastReportFileName = "last-excel-path.txt";
    private readonly string documentsDirectory;

    public string HistoryDirectory { get; }
    public string? DefaultReportsDirectory { get; }
    public string? LastExcelPath { get; private set; }

    public static ResultWorkspace ForCurrentUser()
    {
        var documents = Environment.GetEnvironmentVariable("WHITEBOXQA_DOCUMENTS_DIR");
        if (string.IsNullOrWhiteSpace(documents))
            documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments, Environment.SpecialFolderOption.DoNotVerify);

        return new(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), documents,
            Environment.GetEnvironmentVariable("WHITEBOXQA_HISTORY_DIR"));
    }

    internal ResultWorkspace(string localAppData, string documents, string? historyOverride)
    {
        documentsDirectory = documents;
        HistoryDirectory = !string.IsNullOrWhiteSpace(historyOverride) ? historyOverride
            : string.IsNullOrWhiteSpace(localAppData) ? "" : Path.Combine(localAppData, "WhiteBoxQA", "History");
        DefaultReportsDirectory = string.IsNullOrWhiteSpace(documents)
            ? null : Path.Combine(documents, "WhiteBoxQA", "Reports");
    }

    public string PrepareDefaultReportsDirectory()
    {
        var path = DefaultReportsDirectory
            ?? throw new DirectoryNotFoundException("Windows 문서 폴더를 찾을 수 없습니다.");
        if (!Path.IsPathFullyQualified(documentsDirectory))
            throw new ArgumentException("문서 폴더는 절대 경로여야 합니다.");
        if (!Directory.Exists(path))
        {
            // The special-folder setting can point to a disconnected or relocated Documents directory.
            RequireDirectory(documentsDirectory);
            Directory.CreateDirectory(path);
        }
        return RequireDirectory(path);
    }

    public (string? InitialDirectory, Exception? DefaultDirectoryError) ResolveReportSaveLocation()
    {
        try
        {
            return (PrepareDefaultReportsDirectory(), null);
        }
        catch (Exception defaultError) when (IsPathError(defaultError))
        {
            try
            {
                RestoreLastExcelPath();
                return (LastExcelPath is null ? null : GetReportFolderToOpen(), defaultError);
            }
            catch (Exception recentError) when (IsPathError(recentError))
            {
                return (null, defaultError);
            }
        }
    }

    public static bool IsPathError(Exception exception) => exception is
        IOException or UnauthorizedAccessException or SecurityException or ArgumentException or NotSupportedException;

    public string GetHistoryFolderToOpen() => RequireDirectory(HistoryDirectory);

    public string GetReportFolderToOpen() => LastExcelPath is { } path
        ? RequireDirectory(Path.GetDirectoryName(path)!) : PrepareDefaultReportsDirectory();

    public void RestoreLastExcelPath()
    {
        if (LastExcelPath is not null || HistoryDirectory.Length == 0)
            return;

        try
        {
            var path = File.ReadAllText(Path.Combine(HistoryDirectory, LastReportFileName));
            if (Path.IsPathFullyQualified(path) && !string.IsNullOrEmpty(Path.GetFileName(path)))
                LastExcelPath = path;
        }
        catch (FileNotFoundException) { }
        catch (DirectoryNotFoundException) { }
    }

    public void RecordSavedExcel(string path)
    {
        LastExcelPath = Path.GetFullPath(path);
        var directory = Directory.CreateDirectory(HistoryDirectory).FullName;
        var staging = Path.Combine(directory, $"{Guid.NewGuid():N}.tmp");
        var stream = new FileStream(staging, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        try
        {
            using (stream)
            using (var writer = new StreamWriter(stream, leaveOpen: true))
                writer.Write(LastExcelPath);
            File.Move(staging, Path.Combine(directory, LastReportFileName), overwrite: true);
        }
        finally
        {
            File.Delete(staging);
        }
    }

    private static string RequireDirectory(string path)
    {
        if (path.Length == 0)
            throw new DirectoryNotFoundException("저장 폴더 경로를 찾을 수 없습니다.");
        var fullPath = Path.GetFullPath(path);
        if ((File.GetAttributes(fullPath) & FileAttributes.Directory) == 0)
            throw new IOException("경로가 폴더를 가리키지 않습니다.");
        // Metadata can be readable even when Explorer cannot list the directory.
        using var entries = Directory.EnumerateFileSystemEntries(fullPath).GetEnumerator();
        _ = entries.MoveNext();
        return fullPath;
    }
}
