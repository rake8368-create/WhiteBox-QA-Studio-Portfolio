using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Microsoft.Win32;
using WhiteBoxQA.Core;
using WhiteBoxQA.Infrastructure;

namespace WhiteBoxQA.App;

public partial class MainWindow : Window
{
    private readonly AnalysisOrchestrator analyzer = new();
    private readonly SourceImporter sourceImporter = new();
    private readonly GitSourceImporter gitImporter = new();
    private readonly ExcelReportExporter exporter = new();
    private readonly ResultWorkspace workspace = ResultWorkspace.ForCurrentUser();
    private readonly JsonAnalysisHistory historyStore;

    private SourceDocument[] documents = [];
    private IReadOnlyList<AnalysisIssue> importIssues = [];
    private IReadOnlyList<LanguageFileCount>? languageInventory;
    private SourceOrigin? origin;
    private AnalysisResult? result;
    private AnalysisResult? currentResult;
    private bool showingHistory;
    private string target = "직접 입력";
    private bool updatingEditor;
    private bool hasEdits;
    private CancellationTokenSource? gitCancellation;
    private bool closeAfterGit;

    public MainWindow()
    {
        historyStore = new(workspace.HistoryDirectory);
        InitializeComponent();
        var version = typeof(MainWindow).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        VersionText.Text = version is null ? "버전 정보 없음" : $"v{version}";
        FlowResults.SourceRequested += NavigateToSource;
        SetInput(PastedInput());
    }

    private static SourceInput PastedInput() => new("직접 입력", [new("직접 입력.cs", "")], [])
    {
        Origin = new(SourceKind.DirectInput)
    };

    private void NewInputClick(object sender, RoutedEventArgs e)
    {
        if (CanReplaceInput())
        {
            SetInput(PastedInput());
            Editor.Focus();
        }
    }

    private async void LoadFilesClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = $"소스 파일 열기 · {AnalysisScope.SupportedLanguages} 정적 분석",
            Filter = SourceFileFilter(),
            Multiselect = true
        };

        if (dialog.ShowDialog(this) == true && CanReplaceInput())
        {
            var paths = dialog.FileNames;
            await RunAsync("파일을 읽는 중입니다…", "파일을 불러오지 못했습니다.",
                async () => SetInput(await Task.Run(() => sourceImporter.ReadFiles(paths))));
        }
    }

    private string SourceFileFilter()
    {
        static string Pattern(SourceLanguage? language) =>
            string.Join(';', LanguageIdentification.GetExtensions(language).Select(extension => "*" + extension));

        var filters = new List<string> { $"인식 가능한 소스 파일|{Pattern(null)}" };
        foreach (var language in Enum.GetValues<SourceLanguage>().Where(language => language != SourceLanguage.Unknown))
        {
            var pattern = Pattern(language);
            var support = analyzer.GetCapabilities(language).HasFlag(AnalysisCapabilities.StaticRules) ? "정적 분석" : "확장자 식별";
            filters.Add($"{LanguageIdentification.GetLabel(language)} ({pattern}) · {support}|{pattern}");
        }
        return string.Join('|', filters);
    }

    private async void LoadFolderClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "소스 폴더 열기" };

        if (dialog.ShowDialog(this) == true && CanReplaceInput())
        {
            var path = dialog.FolderName;
            await RunAsync("폴더에서 소스 언어와 C# 프로젝트를 확인하는 중입니다…", "폴더를 불러오지 못했습니다.",
                async () => SetInput(await Task.Run(() => sourceImporter.ReadFolder(path))));
        }
    }

    private async void LoadGitClick(object sender, RoutedEventArgs e)
    {
        if (gitCancellation is not null)
            return;

        var dialog = new GitUrlDialog { Owner = this };
        if (dialog.ShowDialog() != true || !CanReplaceInput())
            return;

        using var cancellation = new CancellationTokenSource();
        gitCancellation = cancellation;
        CancelGitButton.Visibility = Visibility.Visible;
        CancelGitButton.IsEnabled = true;
        var progress = new Progress<GitImportProgress>(update =>
        {
            if (gitCancellation != cancellation || cancellation.IsCancellationRequested)
                return;
            StatusText.Text = update.Message;
            WorkProgress.IsIndeterminate = update.Stage != GitImportStage.Reading || update.Total == 0;
            WorkProgress.Maximum = Math.Max(1, update.Total);
            WorkProgress.Value = update.Processed;
        });

        try
        {
            await RunAsync("Git 주소를 확인하는 중입니다…", "Git 저장소를 불러오지 못했습니다.", async () =>
            {
                var outcome = await gitImporter.ReadAsync(dialog.Address, progress, cancellation.Token);
                if (outcome.Input is { } input && !cancellation.IsCancellationRequested)
                {
                    SetInput(input);
                    StatusText.Text = $"Git 가져오기 완료 · {StatusText.Text}";
                }
                else
                {
                    StatusText.Text = outcome.Status == GitImportStatus.Completed
                        ? "Git 가져오기를 취소했습니다." : outcome.Message;
                }
            });
        }
        finally
        {
            gitCancellation = null;
            CancelGitButton.IsEnabled = false;
            CancelGitButton.Visibility = Visibility.Collapsed;
            WorkProgress.IsIndeterminate = true;
            if (closeAfterGit)
                Close();
        }
    }

    private async void CancelGitClick(object sender, RoutedEventArgs e) => await RequestGitCancellationAsync();

    private async Task RequestGitCancellationAsync()
    {
        if (gitCancellation is not { IsCancellationRequested: false } cancellation)
            return;

        CancelGitButton.IsEnabled = false;
        StatusText.Text = "Git 가져오기를 취소하고 임시 파일을 정리하는 중입니다…";
        try { await cancellation.CancelAsync(); }
        catch (AggregateException)
        {
            if (gitCancellation == cancellation)
                StatusText.Text = "Git 프로세스 정리를 기다리는 중입니다…";
        }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (e.Cancel || gitCancellation is null)
            return;

        e.Cancel = true;
        closeAfterGit = true;
        _ = RequestGitCancellationAsync();
    }

    private void SetInput(SourceInput input)
    {
        target = input.Target;
        documents = input.Documents.ToArray();
        importIssues = input.Issues;
        languageInventory = input.LanguageInventory;
        origin = input.Origin;
        TargetText.Text = target;
        hasEdits = false;

        updatingEditor = true;
        try
        {
            DocumentPicker.ItemsSource = documents;
            DocumentPicker.SelectedIndex = documents.Length > 0 ? 0 : -1;
            ShowDocument(documents.FirstOrDefault());
        }
        finally
        {
            updatingEditor = false;
        }

        ClearResult();
        if (documents.Length == 0)
        {
            var failed = analyzer.Analyze(input);
            ShowCurrentResult(failed);
        }
        else
        {
            var identifiedOnly = languageInventory?.Sum(item => item.MetadataOnlyCount) ?? 0;
            StatusText.Text = input.Target == "직접 입력"
                ? "C# 코드를 입력한 뒤 검증을 실행하세요."
                : $"소스 파일 {documents.Length}개를 불러왔습니다. 검증을 실행하세요."
                    + (identifiedOnly > 0 ? $" 다른 언어 {identifiedOnly:N0}개는 확장자만 식별했습니다." : "")
                    + (importIssues.Count > 0 ? $" 건너뛴 항목 {importIssues.Count}개는 분석 참고에서 확인할 수 있습니다." : "");
        }
    }

    private bool CanReplaceInput() => !hasEdits
        || MessageBox.Show(this, "편집 중인 코드가 새 입력으로 바뀝니다. 계속하시겠습니까?",
            "분석 대상 변경", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes;

    private void DocumentChanged(object sender, SelectionChangedEventArgs e)
    {
        if (updatingEditor || DocumentPicker.SelectedIndex < 0)
            return;

        updatingEditor = true;
        try
        {
            ShowDocument(documents[DocumentPicker.SelectedIndex]);
        }
        finally
        {
            updatingEditor = false;
        }
    }

    private void ShowDocument(SourceDocument? document)
    {
        ShowEditorLanguage(document);
        Editor.IsUndoEnabled = false;
        Editor.Text = document?.Code ?? "";
        Editor.IsUndoEnabled = true;
        Editor.ScrollToHome();
    }

    private void ShowEditorLanguage(SourceDocument? document)
    {
        var language = document is null ? SourceLanguage.Unknown : LanguageIdentification.Identify(document.FileName);
        EditorLanguageText.Text = language switch
        {
            SourceLanguage.CSharp => document?.CSharpProject?.LanguageLabel ?? AnalysisScope.Language,
            SourceLanguage.Python when string.Equals(Path.GetExtension(document?.FileName), ".pyi", StringComparison.OrdinalIgnoreCase)
                => "Python 스텁 · 구문·정적 규칙",
            SourceLanguage.Python => "Python · 정적 규칙 · 함수 CFG",
            SourceLanguage.Java => "Java · 정적 규칙",
            SourceLanguage.JavaScript when string.Equals(Path.GetExtension(document?.FileName), ".jsx", StringComparison.OrdinalIgnoreCase)
                => "JavaScript (JSX) · 정적 규칙",
            SourceLanguage.JavaScript => "JavaScript · 정적 규칙",
            SourceLanguage.Cpp => "C++ · 정적 규칙",
            SourceLanguage.TypeScript when string.Equals(Path.GetExtension(document?.FileName), ".tsx", StringComparison.OrdinalIgnoreCase)
                => "TypeScript (TSX) · 정적 규칙",
            SourceLanguage.TypeScript => "TypeScript · 정적 규칙",
            _ => ""
        };
        EditorLanguageText.ToolTip = language switch
        {
            SourceLanguage.Python => AnalysisScope.PythonControlFlow,
            SourceLanguage.Java => AnalysisScope.JavaControlFlow,
            SourceLanguage.JavaScript => AnalysisScope.JavaScriptScope,
            SourceLanguage.Cpp => AnalysisScope.CppScope,
            SourceLanguage.TypeScript => AnalysisScope.TypeScriptScope,
            _ => document?.CSharpProject?.ProjectPath
        };
    }

    private void EditorChanged(object sender, TextChangedEventArgs e)
    {
        if (updatingEditor || !IsInitialized)
            return;

        if (documents.Length == 0)
        {
            target = "직접 입력";
            TargetText.Text = target;
            importIssues = [];
            languageInventory = null;
            origin = new(SourceKind.DirectInput);
            documents = [new("직접 입력.cs", Editor.Text)];
            updatingEditor = true;
            try
            {
                DocumentPicker.ItemsSource = documents;
                DocumentPicker.SelectedIndex = 0;
                ShowEditorLanguage(documents[0]);
            }
            finally
            {
                updatingEditor = false;
            }
        }

        var index = DocumentPicker.SelectedIndex;
        if (index >= 0)
            documents[index] = documents[index] with { Code = Editor.Text };

        if (origin is { Kind: not SourceKind.DirectInput, HasLocalEdits: false })
            origin = origin with { HasLocalEdits = true };

        if (!hasEdits || result is not null)
            ClearResult();

        hasEdits = true;
        StatusText.Text = "코드가 변경되었습니다. 검증을 실행하세요.";
    }

    private async void AnalyzeClick(object sender, RoutedEventArgs e)
    {
        var input = new SourceInput(target, documents.ToArray(), importIssues)
        {
            Origin = origin,
            LanguageInventory = languageInventory
        };
        ClearResult();
        await RunAsync("정적 분석 규칙을 실행하는 중입니다…", "분석에 실패했습니다.", async () =>
        {
            AnalysisResult analysis;
            try
            {
                analysis = await Task.Run(() => analyzer.Analyze(input));
            }
            catch (Exception exception)
            {
                analysis = new(DateTimeOffset.Now, input.Target, 0, [],
                    [.. input.Issues, new(input.Target, $"분석을 완료하지 못했습니다. 오류 유형 {exception.GetType().Name}")])
                {
                    Origin = input.Origin,
                    LanguageInventory = input.LanguageInventory
                };
            }

            var historySaved = true;
            try
            {
                await Task.Run(() => historyStore.Save(analysis));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                                               or ArgumentException or NotSupportedException)
            {
                historySaved = false;
            }

            ShowCurrentResult(analysis);
            if (!historySaved)
                StatusText.Text += " · 이력 저장 실패. 이력 폴더 경로와 쓰기 권한을 확인하세요.";
        });
    }

    private async void OpenHistoryClick(object sender, RoutedEventArgs e)
    {
        HistoryLoadResult? history = null;
        var statusBefore = StatusText.Text;
        await RunAsync("분석 이력을 읽는 중입니다…", "분석 이력을 읽을 수 없습니다. 폴더 경로와 읽기 권한을 확인하세요.", async () =>
        {
            history = await Task.Run(historyStore.Read);
            StatusText.Text = statusBefore;
        });

        if (history is null)
            return;

        var dialog = new HistoryWindow(history) { Owner = this };
        if (dialog.ShowDialog() == true && dialog.SelectedResult is { } archived)
            ShowResult(archived, fromHistory: true);
    }

    private void ReturnToCurrentClick(object sender, RoutedEventArgs e)
    {
        if (currentResult is { } analysis)
        {
            ShowResult(analysis);
        }
        else
        {
            ClearResult();
            StatusText.Text = "현재 입력을 분석하려면 검증을 실행하세요.";
        }
    }

    private async void OpenResultWorkspaceClick(object sender, RoutedEventArgs e)
    {
        var previousStatus = StatusText.Text;
        string? restoreError = null;
        await RunAsync("결과 관리 경로를 확인하는 중입니다…", "결과 관리 경로를 확인할 수 없습니다.", async () =>
        {
            try
            {
                await Task.Run(workspace.RestoreLastExcelPath);
            }
            catch (Exception exception) when (ResultWorkspace.IsPathError(exception))
            {
                restoreError = $"이전 Excel 경로를 읽을 수 없습니다. {ResultWorkspaceWindow.PathErrorMessage(exception)}";
            }
            StatusText.Text = previousStatus;
        });
        new ResultWorkspaceWindow(workspace, restoreError) { Owner = this }.ShowDialog();
    }

    private async void ExportClick(object sender, RoutedEventArgs e)
    {
        if (result is not { } snapshot)
            return;

        var previousStatus = StatusText.Text;
        (string? InitialDirectory, Exception? DefaultDirectoryError) location = default;
        await RunAsync("기본 보고서 폴더를 확인하는 중입니다…", "기본 보고서 폴더를 사용할 수 없습니다.", async () =>
        {
            location = await Task.Run(workspace.ResolveReportSaveLocation);
            StatusText.Text = location.DefaultDirectoryError is { } error
                ? $"기본 보고서 폴더를 사용할 수 없습니다. {ResultWorkspaceWindow.PathErrorMessage(error)}"
                : previousStatus;
        });

        var dialog = new SaveFileDialog
        {
            Title = location.DefaultDirectoryError is null
                ? "분석 결과 Excel 저장" : "Excel 저장 위치 선택 · 기본 보고서 폴더 사용 불가",
            Filter = "Excel 통합 문서 (*.xlsx)|*.xlsx",
            FileName = $"WhiteBoxQA_{snapshot.AnalyzedAt:yyyyMMdd_HHmmss}.xlsx",
            InitialDirectory = location.InitialDirectory ?? "",
            DefaultExt = ".xlsx",
            AddExtension = true,
            OverwritePrompt = true
        };

        if (dialog.ShowDialog(this) == true)
        {
            var path = dialog.FileName;
            await RunAsync("Excel 파일을 만드는 중입니다…", "Excel 저장에 실패했습니다. 폴더 권한과 파일 사용 상태를 확인하세요.", async () =>
            {
                await Task.Run(() => exporter.Export(snapshot, path));
                StatusText.Text = $"Excel 저장 완료 · {path}";
                try
                {
                    await Task.Run(() => workspace.RecordSavedExcel(path));
                }
                catch (Exception exception) when (ResultWorkspace.IsPathError(exception))
                {
                    StatusText.Text += " · 다음 실행에 사용할 경로를 저장하지 못했습니다. 이력 폴더 권한을 확인하세요.";
                }
            });
        }
    }

    private async Task RunAsync(string waiting, string failed, Func<Task> action)
    {
        SetBusy(true);
        StatusText.Text = waiting;
        try
        {
            await action();
        }
        catch (Exception exception)
        {
            StatusText.Text = $"{failed} 오류 유형 {exception.GetType().Name}";
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void SetBusy(bool busy)
    {
        Toolbar.IsEnabled = !busy;
        Editor.IsReadOnly = busy;
        DocumentPicker.IsEnabled = !busy;
        AnalyzeButton.IsEnabled = !busy;
        ResultsGrid.IsEnabled = !busy;
        DetailTabs.IsEnabled = !busy;
        FlowResults.IsEnabled = !busy;
        ReturnToCurrentButton.IsEnabled = !busy;
        SeverityFilter.IsEnabled = FindingSearchBox.IsEnabled = !busy && result is { Findings.Count: > 0 };
        UpdateCopyButton();
        WorkProgress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ClearResult()
    {
        result = null;
        currentResult = null;
        SetHistoryMode(false);
        ExportButton.IsEnabled = false;
        SeverityFilter.IsEnabled = FindingSearchBox.IsEnabled = false;
        FilterCount.Text = "";
        ResultsGrid.ItemsSource = null;
        FlowResults.Show(null);
        CppScopePanel.Visibility = Visibility.Collapsed;
        TypeScriptScopePanel.Visibility = Visibility.Collapsed;
        DetailsPlaceholder.Text = "결과를 선택하면 원인과 수정 방법이 표시됩니다.";
        TotalCount.Text = HighCount.Text = ModerateCount.Text = LowCount.Text = InfoCount.Text = "—";
        CoverageText.Text = "검증 대기";
        UpdateIssues(importIssues);
        ShowOrigin(origin);
        ShowInventory(languageInventory);
        DetailTabs.SelectedItem = FindingTab;
    }

    private void ShowCurrentResult(AnalysisResult analysis)
    {
        currentResult = analysis;
        ShowResult(analysis);
    }

    private void ShowResult(AnalysisResult analysis, bool fromHistory = false)
    {
        result = analysis;
        SetHistoryMode(fromHistory);
        ExportButton.IsEnabled = true;
        SeverityFilter.IsEnabled = FindingSearchBox.IsEnabled = ResultsGrid.IsEnabled && analysis.Findings.Count > 0;
        ResultsGrid.ItemsSource = new ListCollectionView(analysis.Findings.ToList());
        FlowResults.Show(analysis.ControlFlow, fromHistory, analysis.HasPythonSources, analysis.HasJavaSources,
            analysis.HasJavaScriptSources, analysis.HasCppSources, analysis.HasTypeScriptSources);
        CppScopePanel.Visibility = analysis.HasCppSources ? Visibility.Visible : Visibility.Collapsed;
        TypeScriptScopePanel.Visibility = analysis.HasTypeScriptSources ? Visibility.Visible : Visibility.Collapsed;
        UpdateFindingFilter();
        TotalCount.Text = CountText(analysis.Findings.Count);
        HighCount.Text = CountText(analysis.Count(Severity.High));
        ModerateCount.Text = CountText(analysis.Count(Severity.Moderate));
        LowCount.Text = CountText(analysis.Count(Severity.Low));
        InfoCount.Text = CountText(analysis.Count(Severity.Information));
        CoverageText.Text = $"분석 {analysis.StatusLabel} · 파일 {analysis.AnalyzedFileCount}개";
        UpdateIssues(analysis.Issues);
        ShowOrigin(analysis.Origin);
        ShowInventory(analysis.LanguageInventory);

        StatusText.Text = analysis.Status switch
        {
            AnalysisStatus.Failed => "분석 실패 · 분석 참고에서 원인을 확인하세요.",
            AnalysisStatus.Partial => $"부분 성공 · 발견 {analysis.Findings.Count}건 · 분석 제한 {analysis.LimitationCount}개",
            _ when analysis.Findings.Count == 0 => "분석 완료 · 적용한 규칙에서 발견된 항목이 없습니다.",
            _ => $"분석 완료 · 발견 {analysis.Findings.Count}건"
        };

        if (fromHistory)
            StatusText.Text = $"저장된 분석 이력 · {analysis.StatusLabel} · 전체 발견 {analysis.Findings.Count:N0}건";

        DetailTabs.SelectedItem = analysis.Issues.Count > 0 && analysis.Findings.Count == 0
            ? NotesTab : FindingTab;
    }

    private void SetHistoryMode(bool enabled)
    {
        showingHistory = enabled;
        ReturnToCurrentButton.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
        GoToFindingButton.Visibility = enabled ? Visibility.Collapsed : Visibility.Visible;
        ArchivedTargetText.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
        ArchivedTargetText.Text = enabled && result is { } archived
            ? $"저장된 결과 · {archived.Target} · {archived.AnalyzedAt:yyyy-MM-dd HH:mm}"
            : "";
    }

    private void SeverityFilterChanged(object sender, SelectionChangedEventArgs e) => UpdateFindingFilter();

    private void FindingSearchChanged(object sender, TextChangedEventArgs e) => UpdateFindingFilter();

    private void UpdateFindingFilter()
    {
        if (result is not { } analysis || ResultsGrid.ItemsSource is not ListCollectionView view)
            return;

        var selected = ResultsGrid.SelectedItem;
        var severity = (SeverityFilter.SelectedItem as ComboBoxItem)?.Tag as Severity?;
        var searchText = FindingSearchBox.Text;
        var filter = FindingPresentation.CreateFilter(severity, searchText);
        view.Filter = item => filter((Finding)item);
        FilterCount.Text = $"표시 {view.Count:N0} / 전체 {analysis.Findings.Count:N0}건";

        if (view.IsEmpty)
        {
            ResultsGrid.SelectedIndex = -1;
            DetailsPlaceholder.Text = analysis.Status switch
            {
                AnalysisStatus.Failed => "분석을 완료할 수 없습니다.",
                _ when analysis.Findings.Count == 0 => "분석한 파일에서 발견된 항목이 없습니다.",
                _ when !string.IsNullOrWhiteSpace(searchText) => "검색 조건에 맞는 항목이 없습니다.",
                _ => "선택한 위험도에 해당하는 항목이 없습니다."
            };
        }
        else
        {
            ResultsGrid.SelectedItem = selected is not null && view.Contains(selected)
                ? selected : view.GetItemAt(0);
        }
    }

    private static string CountText(int count) => count.ToString("N0", CultureInfo.CurrentCulture);

    private void ShowInventory(IReadOnlyList<LanguageFileCount>? counts)
    {
        InventoryPanel.Visibility = counts is { Count: > 0 } ? Visibility.Visible : Visibility.Collapsed;
        InventoryText.Text = counts is null ? "" : string.Join(Environment.NewLine,
            counts.Select(item => $"{item.Label} · {item.CountLabel} · {item.SupportLabel}"));
    }

    private void ShowOrigin(SourceOrigin? source)
    {
        var lines = new List<string> { source?.KindLabel ?? "기록 없음" };
        if (source is { Kind: SourceKind.Git })
        {
            lines.Add(source.RemoteUrl!);
            lines.Add($"원본 HEAD · {source.CommitSha ?? "커밋 없음"}");
            if (source.Branch is { } branch)
                lines.Add($"브랜치 · {branch}");
            if (source.ImportedAt is { } importedAt)
                lines.Add($"가져온 일시 · {importedAt.ToLocalTime():yyyy-MM-dd HH:mm:ss zzz}");
        }
        if (source?.HasLocalEdits == true)
            lines.Add("가져온 뒤 편집됨");

        OriginText.Text = string.Join(Environment.NewLine, lines);
    }

    private void UpdateIssues(IReadOnlyList<AnalysisIssue> issues)
    {
        IssuesList.ItemsSource = issues;
        IssueHeading.Visibility = issues.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        NotesTab.Header = issues.Count > 0 ? $"분석 참고 ({issues.Count})" : "분석 참고";
    }

    private void FindingChanged(object sender, SelectionChangedEventArgs e)
    {
        var finding = ResultsGrid.SelectedItem as Finding;
        UpdateCopyButton();
        FindingPanel.DataContext = finding;
        FindingPanel.Visibility = finding is null ? Visibility.Collapsed : Visibility.Visible;
        DetailsPlaceholder.Visibility = finding is null ? Visibility.Visible : Visibility.Collapsed;

        if (finding is not null)
        {
            FindingLocation.Text = $"{finding.FileName} · {finding.Line}줄 {finding.Column}열";
            DetailTabs.SelectedItem = FindingTab;
            DetailScroll.ScrollToTop();
        }
    }

    private void UpdateCopyButton() =>
        CopyFindingButton.IsEnabled = ResultsGrid.IsEnabled && ResultsGrid.SelectedItem is Finding;

    private void CopyFindingClick(object sender, RoutedEventArgs e)
    {
        if (ResultsGrid.SelectedItem is not Finding finding)
            return;

        try
        {
            Clipboard.SetText(FindingPresentation.FormatForCopy(finding));
            StatusText.Text = $"선택한 결과를 복사했습니다. ({finding.RuleId})";
        }
        catch (ExternalException)
        {
            StatusText.Text = "클립보드에 복사할 수 없습니다. 잠시 후 다시 시도하세요.";
        }
    }

    private void GoToFindingClick(object sender, RoutedEventArgs e)
    {
        if (showingHistory || ResultsGrid.SelectedItem is not Finding finding)
            return;

        NavigateToSource(finding.FileName, finding.Line);
    }

    private void NavigateToSource(string fileName, int lineNumber)
    {
        var index = Array.FindIndex(documents, document => document.FileName == fileName);
        if (index < 0)
            return;

        DocumentPicker.SelectedIndex = index;
        Editor.UpdateLayout();

        var line = Math.Clamp(lineNumber - 1, 0, Math.Max(0, Editor.LineCount - 1));
        var start = Editor.GetCharacterIndexFromLineIndex(line);
        var length = Editor.GetLineLength(line);
        if (start >= 0 && length >= 0)
        {
            var trimmed = Editor.Text.AsSpan(start, length).TrimEnd("\r\n");
            Editor.ScrollToLine(line);
            Editor.Focus();
            Editor.Select(start, trimmed.Length);
        }
    }
}
