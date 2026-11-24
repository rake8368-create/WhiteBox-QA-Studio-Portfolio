using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Security;
using System.Windows;
using WhiteBoxQA.Infrastructure;

namespace WhiteBoxQA.App;

public partial class ResultWorkspaceWindow : Window
{
    private readonly ResultWorkspace workspace;

    internal ResultWorkspaceWindow(ResultWorkspace workspace, string? restoreError)
    {
        this.workspace = workspace;
        InitializeComponent();
        HistoryPathText.Text = workspace.HistoryDirectory.Length > 0
            ? workspace.HistoryDirectory : "이력 폴더를 찾을 수 없습니다.";
        LastExcelPathText.Text = workspace.LastExcelPath ?? "저장한 Excel 경로가 없습니다.";
        DefaultReportsPathText.Text = workspace.DefaultReportsDirectory is { } path
            ? $"기본 보고서 폴더 · {path}" : "Windows 문서 폴더를 찾을 수 없습니다.";
        FolderStatusText.Text = restoreError ?? "";
    }

    private async void OpenHistoryFolderClick(object sender, RoutedEventArgs e) =>
        await OpenFolderAsync("이력 폴더", workspace.GetHistoryFolderToOpen);

    private async void OpenReportFolderClick(object sender, RoutedEventArgs e) =>
        await OpenFolderAsync("보고서 폴더", workspace.GetReportFolderToOpen);

    private async Task OpenFolderAsync(string label, Func<string> getPath)
    {
        OpenHistoryFolderButton.IsEnabled = OpenReportFolderButton.IsEnabled = false;
        FolderStatusText.Text = $"{label}를 여는 중입니다…";
        try
        {
            var path = await Task.Run(() =>
            {
                var directory = getPath();
                using var process = Process.Start(new ProcessStartInfo(directory) { UseShellExecute = true });
                return directory;
            });
            FolderStatusText.Text = $"열기 요청 완료 · {path}";
        }
        catch (Exception exception) when (ResultWorkspace.IsPathError(exception) || exception is Win32Exception)
        {
            FolderStatusText.Text = $"{label}를 열 수 없습니다. {PathErrorMessage(exception)}";
        }
        finally
        {
            OpenHistoryFolderButton.IsEnabled = OpenReportFolderButton.IsEnabled = true;
        }
    }

    internal static string PathErrorMessage(Exception exception) => exception switch
    {
        UnauthorizedAccessException or SecurityException or Win32Exception { NativeErrorCode: 5 } => "폴더 접근 권한을 확인하세요.",
        FileNotFoundException or DirectoryNotFoundException => "폴더가 없거나 이동되었습니다.",
        ArgumentException or NotSupportedException => "폴더 경로가 올바른지 확인하세요.",
        _ => exception.Message
    };
}
