using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using WhiteBoxQA.Core;

namespace WhiteBoxQA.App;

public partial class FlowResultsView : UserControl
{
    private bool isArchived;

    public FlowResultsView()
    {
        InitializeComponent();
        Show(null);
    }

    public event Action<string, int>? SourceRequested;

    public void Show(IReadOnlyList<MethodFlow>? methods, bool fromHistory = false,
        bool hasPythonSources = false, bool hasJavaSources = false, bool hasJavaScriptSources = false,
        bool hasCppSources = false, bool hasTypeScriptSources = false)
    {
        isArchived = fromHistory;
        MethodsGrid.ItemsSource = methods;
        MethodsGrid.SelectedIndex = methods is { Count: > 0 } ? 0 : -1;
        Results.Visibility = MethodsGrid.SelectedIndex >= 0 ? Visibility.Visible : Visibility.Collapsed;
        SummaryText.Text = methods switch
        {
            null when fromHistory => MethodFlow.LegacyLabel,
            null => "소스를 분석하면 함수·메서드별 제어 흐름이 표시됩니다.",
            { Count: 0 } => "분석한 소스에 표시할 함수 또는 메서드가 없습니다.",
            _ => $"{MethodFlow.ComplexityLabel} · 함수·메서드 {methods.Count:N0}개"
        };
        if (hasPythonSources && methods?.Any(method => method.Language == SourceLanguage.Python) != true)
            SummaryText.Text += "\n이 결과에 기록된 Python 함수 제어 흐름이 없습니다.";
        if (hasJavaSources)
            SummaryText.Text += $"\n{AnalysisScope.JavaControlFlow}";
        if (hasJavaScriptSources)
            SummaryText.Text += $"\n{AnalysisScope.JavaScriptScope}";
        if (hasCppSources)
            SummaryText.Text += $"\n{AnalysisScope.CppControlFlow}";
        if (hasTypeScriptSources)
            SummaryText.Text += $"\n{AnalysisScope.TypeScriptControlFlow}";
        BranchesGrid.ToolTip = fromHistory
            ? "저장된 이력에서 분기 위치를 확인할 수 있습니다."
            : "분기를 선택하면 소스 줄로 이동합니다.";
    }

    private void MethodChanged(object sender, SelectionChangedEventArgs e)
    {
        var method = MethodsGrid.SelectedItem as MethodFlow;
        BranchesGrid.ItemsSource = method?.Decisions;
        BranchesGrid.SelectedIndex = -1;
        BlocksGrid.ItemsSource = method?.Blocks;
        BlocksGrid.SelectedIndex = -1;
        BranchesTab.Header = $"조건 및 분기 ({method?.Decisions.Count ?? 0})";
        BlocksTab.Header = $"기본 블록 ({method?.Blocks.Count ?? 0})";
        MethodSummary.Text = method is null ? "" : $"{method.LanguageLabel} · {method.MethodName} · {method.StatusLabel}";
        Explanation.Text = method switch
        {
            { Complexity: 1 } => "집계 범위에서 도달 가능한 조건 분기가 없습니다.",
            { Complexity: > 5 } => "여러 조건에서 실행 흐름이 달라집니다. 조건별 테스트 사례를 확인하세요.",
            { Complexity: > 1 } => "조건에 따라 실행 흐름이 달라집니다. 참과 거짓 경로를 확인할 수 있습니다.",
            _ => ""
        };
        Limitation.Text = method?.Limitation ?? "";
        MethodSummary.ToolTip = method?.Language == SourceLanguage.Python ? AnalysisScope.PythonControlFlow : null;
        Limitation.Visibility = string.IsNullOrEmpty(Limitation.Text) ? Visibility.Collapsed : Visibility.Visible;
    }

    private void BlockChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!isArchived && MethodsGrid.SelectedItem is MethodFlow method
            && BlocksGrid.SelectedItem is FlowBlock { Line: int line })
            SourceRequested?.Invoke(method.FileName, line);
    }

    private void BranchChanged(object sender, SelectionChangedEventArgs e) => NavigateToSelectedBranch();

    private void BranchDoubleClick(object sender, MouseButtonEventArgs e) => NavigateToSelectedBranch();

    private void NavigateToSelectedBranch()
    {
        if (!isArchived && MethodsGrid.SelectedItem is MethodFlow method
            && BranchesGrid.SelectedItem is FlowDecision branch)
            SourceRequested?.Invoke(method.FileName, branch.Line);
    }
}
