using System.Security.AccessControl;
using System.Security.Principal;
using ClosedXML.Excel;
using WhiteBoxQA.Infrastructure;

namespace WhiteBoxQA.Tests;

public sealed class ResultWorkspaceTests : IDisposable
{
    private readonly DirectoryInfo scratch = Directory.CreateTempSubdirectory("whiteboxqa-workspace-");

    [Theory]
    [InlineData(null)]
    [InlineData(" ")]
    public void UsesLocalAppDataForDefaultHistory(string? historyOverride)
    {
        var workspace = Create(historyOverride: historyOverride);
        Assert.Equal(Path.Combine(scratch.FullName, "Local", "WhiteBoxQA", "History"), workspace.HistoryDirectory);
        Assert.False(Directory.Exists(workspace.HistoryDirectory));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \t")]
    public void MissingOrBlankDocumentsOverrideUsesTheWindowsDocumentsFolder(string? documentsOverride)
    {
        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments,
            Environment.SpecialFolderOption.DoNotVerify);
        var history = Path.Combine(scratch.FullName, "테스트 이력");
        var workspace = CreateWithProcessOverrides(documentsOverride, history);

        Assert.Equal(string.IsNullOrWhiteSpace(documents) ? null : Path.Combine(documents, "WhiteBoxQA", "Reports"),
            workspace.DefaultReportsDirectory);
        Assert.Equal(history, workspace.HistoryDirectory);
        Assert.False(Directory.Exists(history));
    }

    [Fact]
    public void DocumentsOverrideKeepsReportAndHistoryFoldersIndependent()
    {
        var documents = scratch.CreateSubdirectory("테스트 문서").FullName;
        var history = Path.Combine(scratch.FullName, "테스트 이력");
        var workspace = CreateWithProcessOverrides(documents, history);

        var location = workspace.ResolveReportSaveLocation();
        Assert.Equal(Path.Combine(documents, "WhiteBoxQA", "Reports"), location.InitialDirectory);
        Assert.Null(location.DefaultDirectoryError);
        Assert.False(Directory.Exists(history));

        var previous = Path.Combine(location.InitialDirectory!, "기존 보고서.xlsx");
        File.WriteAllText(previous, "보존할 보고서");
        workspace.RecordSavedExcel(previous);
        var reopened = CreateWithProcessOverrides(documents, history);
        reopened.RestoreLastExcelPath();
        Assert.Equal(previous, reopened.LastExcelPath);
        Assert.Equal(history, reopened.GetHistoryFolderToOpen());
        Assert.Equal(previous, Assert.Single(Directory.GetFiles(location.InitialDirectory!)));
        Assert.Equal("보존할 보고서", File.ReadAllText(previous));
    }

    [Fact]
    public void RelativeDocumentsOverrideDoesNotCreateReportsInTheWorkingDirectory()
    {
        var history = Path.Combine(scratch.FullName, "테스트 이력");
        var recent = scratch.CreateSubdirectory("이전 보고서").FullName;
        var previous = Path.Combine(recent, "기존 보고서.xlsx");
        File.WriteAllText(previous, "보존할 보고서");
        CreateWithProcessOverrides(null, history).RecordSavedExcel(previous);

        var relative = Path.Combine(".", $"whiteboxqa-workspace-{Guid.NewGuid():N}");
        var reopened = CreateWithProcessOverrides(relative, history);

        var location = reopened.ResolveReportSaveLocation();
        Assert.IsType<ArgumentException>(location.DefaultDirectoryError);
        Assert.Equal(recent, location.InitialDirectory);
        Assert.Equal(previous, reopened.LastExcelPath);
        Assert.False(Directory.Exists(relative));
        Assert.Equal("보존할 보고서", File.ReadAllText(previous));
    }

    [Fact]
    public void OverrideIsUsedForOpeningSavingAndRestoringTheSameV1History()
    {
        var history = scratch.CreateSubdirectory("다른 이력").FullName;
        File.WriteAllText(Path.Combine(history, "legacy.json"), """
            {
              "version": 1,
              "result": {
                "analyzedAt": "2026-11-20T09:00:00+09:00",
                "target": "C# 이전 이력",
                "analyzedFileCount": 1,
                "findings": [],
                "issues": []
              }
            }
            """);
        var workspace = Create(historyOverride: history);
        Assert.Equal(history, workspace.GetHistoryFolderToOpen());

        var store = new JsonAnalysisHistory(workspace.HistoryDirectory);
        var legacy = Assert.Single(store.Read().Entries);
        Assert.Null(legacy.ControlFlow);
        store.Save(legacy with { Target = "새 이력" });

        var reopened = new JsonAnalysisHistory(Create(historyOverride: history).HistoryDirectory).Read();
        Assert.Equal(0, reopened.SkippedCount);
        Assert.Contains(reopened.Entries, entry => entry.Target == "C# 이전 이력");
        Assert.Contains(reopened.Entries, entry => entry.Target == "새 이력");
        Assert.Equal(2, reopened.Entries.Count);
    }

    [Fact]
    public void OpeningReportsCreatesTheDefaultUnderTheSuppliedDocumentsSpecialFolder()
    {
        var documents = scratch.CreateSubdirectory("리디렉션된 내 문서").FullName;
        var workspace = Create(documents);
        var expected = Path.Combine(documents, "WhiteBoxQA", "Reports");
        Assert.Equal(expected, workspace.DefaultReportsDirectory);
        Assert.False(Directory.Exists(expected));
        Assert.Equal(expected, workspace.GetReportFolderToOpen());
        Assert.True(Directory.Exists(expected));
        Assert.Null(workspace.LastExcelPath);
    }

    [Fact]
    public void MissingSpecialFoldersCannotFallBackToRelativeStorage()
    {
        var workspace = new ResultWorkspace("", "", null);
        Assert.Null(workspace.DefaultReportsDirectory);
        Assert.Throws<DirectoryNotFoundException>(workspace.PrepareDefaultReportsDirectory);
        Assert.Throws<DirectoryNotFoundException>(workspace.GetHistoryFolderToOpen);
    }

    [Fact]
    public void AnExistingFileInTheDefaultReportPathProducesAnErrorAndIsPreserved()
    {
        var documents = scratch.CreateSubdirectory("문서").FullName;
        var existing = Path.Combine(documents, "WhiteBoxQA");
        File.WriteAllText(existing, "기존 문서");
        var workspace = Create(documents);
        Assert.ThrowsAny<IOException>(workspace.PrepareDefaultReportsDirectory);
        Assert.Equal("기존 문서", File.ReadAllText(existing));
    }

    [Fact]
    public void OpeningAMissingHistoryDirectoryReportsTheErrorWithoutCreatingIt()
    {
        var workspace = Create();
        Assert.ThrowsAny<IOException>(workspace.GetHistoryFolderToOpen);
        Assert.False(Directory.Exists(workspace.HistoryDirectory));
    }

    [Fact]
    public void OpeningAnAccessDeniedHistoryDirectoryPreservesTheAccessError()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var history = scratch.CreateSubdirectory("권한 제한");
        var security = history.GetAccessControl(AccessControlSections.Access);
        var original = security.GetSecurityDescriptorSddlForm(AccessControlSections.Access);
        using var identity = WindowsIdentity.GetCurrent();
        security.AddAccessRule(new FileSystemAccessRule(identity.User!, FileSystemRights.ListDirectory, AccessControlType.Deny));
        history.SetAccessControl(security);
        try
        {
            var workspace = Create(historyOverride: history.FullName);
            Assert.Throws<UnauthorizedAccessException>(workspace.GetHistoryFolderToOpen);
        }
        finally
        {
            security.SetSecurityDescriptorSddlForm(original, AccessControlSections.Access);
            history.SetAccessControl(security);
        }
    }

    [Fact]
    public void CustomExcelExportIsRestoredAfterReopeningAndKeepsFourSheets()
    {
        var documents = scratch.CreateSubdirectory("문서").FullName;
        var custom = scratch.CreateSubdirectory("사용자 선택 보고서").FullName;
        var first = Path.Combine(custom, "기존 보고서.xlsx");
        File.WriteAllText(first, "기존 파일");
        var excel = Path.Combine(custom, "새 보고서.xlsx");
        var workspace = Create(documents);
        workspace.RecordSavedExcel(first);
        new ExcelReportExporter().Export(new(DateTimeOffset.Now, "검증", 1, [], []), excel);
        workspace.RecordSavedExcel(excel);

        var reopened = Create(documents);
        reopened.RestoreLastExcelPath();
        Assert.Equal(excel, reopened.LastExcelPath);
        Assert.Equal(custom, reopened.GetReportFolderToOpen());
        Assert.Equal("기존 파일", File.ReadAllText(first));
        Assert.False(Directory.Exists(workspace.DefaultReportsDirectory));
        Assert.Empty(Directory.GetFiles(workspace.HistoryDirectory, "*.json"));
        using var workbook = new XLWorkbook(excel);
        Assert.Equal(new[] { "분석 요약", "상세 분석 결과", "분석 한계 및 참고사항", "제어 흐름 분석" },
            workbook.Worksheets.Select(sheet => sheet.Name));
    }

    [Fact]
    public void FailureToPersistThePathKeepsTheLastSuccessfulExportAvailableInTheSession()
    {
        var unreadableHistory = Path.Combine(scratch.FullName, "이력 폴더 대신 파일");
        File.WriteAllText(unreadableHistory, "기존 파일");
        var workbook = Path.Combine(scratch.FullName, "보고서.xlsx");
        var workspace = Create(historyOverride: unreadableHistory);
        Assert.ThrowsAny<IOException>(() => workspace.RecordSavedExcel(workbook));
        Assert.Equal(workbook, workspace.LastExcelPath);
        Assert.Equal(scratch.FullName, workspace.GetReportFolderToOpen());
        Assert.Equal("기존 파일", File.ReadAllText(unreadableHistory));
    }

    [Fact]
    public void AvailableRedirectedDocumentsTakesPrecedenceOverTheLastExportFolder()
    {
        var documents = scratch.CreateSubdirectory("다른 드라이브 문서").FullName;
        var previous = scratch.CreateSubdirectory("이전 보고서").FullName;
        var workspace = Create(documents);
        workspace.RecordSavedExcel(Path.Combine(previous, "기존.xlsx"));

        var location = workspace.ResolveReportSaveLocation();
        Assert.Equal(Path.Combine(documents, "WhiteBoxQA", "Reports"), location.InitialDirectory);
        Assert.Null(location.DefaultDirectoryError);
        Assert.True(Directory.Exists(location.InitialDirectory));
        Assert.Empty(Directory.GetFileSystemEntries(location.InitialDirectory!));
    }

    [Fact]
    public void MissingDocumentsLeavesTheSaveLocationOpenWithoutRecreatingDocuments()
    {
        var documents = Path.Combine(scratch.FullName, "연결되지 않은 문서");
        var location = CreateWithProcessOverrides(documents, Path.Combine(scratch.FullName, "테스트 이력"))
            .ResolveReportSaveLocation();

        Assert.Null(location.InitialDirectory);
        Assert.IsAssignableFrom<IOException>(location.DefaultDirectoryError);
        Assert.False(Directory.Exists(documents));
    }

    [Fact]
    public void BlockedDefaultSuggestsTheRestoredFolderAndKeepsExplicitExportSeparate()
    {
        var documents = scratch.CreateSubdirectory("문서").FullName;
        var defaultFolder = Directory.CreateDirectory(Path.Combine(documents, "WhiteBoxQA")).FullName;
        var blockingFile = Path.Combine(defaultFolder, "Reports");
        File.WriteAllText(blockingFile, "사용자 파일");
        var recent = scratch.CreateSubdirectory("이전 보고서").FullName;
        var previousReport = Path.Combine(recent, "기존.xlsx");
        File.WriteAllText(previousReport, "기존 Excel");
        Create(documents).RecordSavedExcel(previousReport);

        var workspace = Create(documents);
        var location = workspace.ResolveReportSaveLocation();
        Assert.Equal(recent, location.InitialDirectory);
        Assert.IsAssignableFrom<IOException>(location.DefaultDirectoryError);
        Assert.Equal(previousReport, workspace.LastExcelPath);
        Assert.Equal(previousReport, Assert.Single(Directory.GetFiles(recent)));

        var selected = Path.Combine(scratch.CreateSubdirectory("지정한 보고서").FullName, "새 결과.xlsx");
        new ExcelReportExporter().Export(new(DateTimeOffset.Now, "검증", 1, [], []), selected);
        workspace.RecordSavedExcel(selected);

        var reopened = Create(documents);
        reopened.RestoreLastExcelPath();
        Assert.Equal(selected, reopened.LastExcelPath);
        Assert.Equal("기존 Excel", File.ReadAllText(previousReport));
        Assert.Equal("사용자 파일", File.ReadAllText(blockingFile));
        using var workbook = new XLWorkbook(selected);
        Assert.Equal(4, workbook.Worksheets.Count);
    }

    [Fact]
    public void MissingRecentFolderLeavesTheDialogFreeToChooseAnotherPath()
    {
        var workspace = Create();
        var oldPath = Path.Combine(scratch.FullName, "이동된 폴더", "기존.xlsx");
        workspace.RecordSavedExcel(oldPath);

        var reopened = Create();
        var location = reopened.ResolveReportSaveLocation();
        Assert.Null(location.InitialDirectory);
        Assert.IsAssignableFrom<IOException>(location.DefaultDirectoryError);
        Assert.Equal(oldPath, reopened.LastExcelPath);
        Assert.False(Directory.Exists(Path.GetDirectoryName(oldPath)));
    }

    [Fact]
    public void DefaultReportCreationDeniedByWindowsAclLeavesTheDialogAvailable()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var documents = scratch.CreateSubdirectory("권한 제한 문서");
        var appFolder = documents.CreateSubdirectory("WhiteBoxQA");
        var security = appFolder.GetAccessControl(AccessControlSections.Access);
        var original = security.GetSecurityDescriptorSddlForm(AccessControlSections.Access);
        using var identity = WindowsIdentity.GetCurrent();
        security.AddAccessRule(new FileSystemAccessRule(identity.User!, FileSystemRights.CreateDirectories, AccessControlType.Deny));
        appFolder.SetAccessControl(security);
        try
        {
            var workspace = CreateWithProcessOverrides(documents.FullName, Path.Combine(scratch.FullName, "테스트 이력"));
            var location = workspace.ResolveReportSaveLocation();
            Assert.Null(location.InitialDirectory);
            Assert.IsType<UnauthorizedAccessException>(location.DefaultDirectoryError);
            Assert.False(Directory.Exists(workspace.DefaultReportsDirectory));
        }
        finally
        {
            security.SetSecurityDescriptorSddlForm(original, AccessControlSections.Access);
            appFolder.SetAccessControl(security);
        }
    }

    [Fact]
    public void LockedExcelTargetKeepsTheExistingReportAndCleansUpTheStagedFile()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var folder = scratch.CreateSubdirectory("보고서");
        var report = Path.Combine(folder.FullName, "기존 보고서.xlsx");
        File.WriteAllText(report, "기존 파일");
        using (File.Open(report, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var exporter = new ExcelReportExporter();
            var error = Record.Exception(() => exporter.Export(new(DateTimeOffset.Now, "검증", 1, [], []), report));
            Assert.True(error is IOException or UnauthorizedAccessException, $"Unexpected error: {error}");
        }
        Assert.Equal("기존 파일", File.ReadAllText(report));
        Assert.Equal(report, Assert.Single(Directory.GetFiles(folder.FullName)));
    }

    private ResultWorkspace Create(string? documents = null, string? historyOverride = null) =>
        new(Path.Combine(scratch.FullName, "Local"), documents ?? Path.Combine(scratch.FullName, "Documents"), historyOverride);

    private static ResultWorkspace CreateWithProcessOverrides(string? documents, string history)
    {
        const string documentsKey = "WHITEBOXQA_DOCUMENTS_DIR";
        const string historyKey = "WHITEBOXQA_HISTORY_DIR";
        var previousDocuments = Environment.GetEnvironmentVariable(documentsKey, EnvironmentVariableTarget.Process);
        var previousHistory = Environment.GetEnvironmentVariable(historyKey, EnvironmentVariableTarget.Process);
        try
        {
            Environment.SetEnvironmentVariable(documentsKey, documents, EnvironmentVariableTarget.Process);
            Environment.SetEnvironmentVariable(historyKey, history, EnvironmentVariableTarget.Process);
            return ResultWorkspace.ForCurrentUser();
        }
        finally
        {
            Environment.SetEnvironmentVariable(documentsKey, previousDocuments, EnvironmentVariableTarget.Process);
            Environment.SetEnvironmentVariable(historyKey, previousHistory, EnvironmentVariableTarget.Process);
        }
    }

    public void Dispose() => scratch.Delete(recursive: true);
}
