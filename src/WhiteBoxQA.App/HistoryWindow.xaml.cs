using System.Windows;
using System.Windows.Controls;
using WhiteBoxQA.Core;
using WhiteBoxQA.Infrastructure;

namespace WhiteBoxQA.App;

public partial class HistoryWindow : Window
{
    public HistoryWindow(HistoryLoadResult history)
    {
        InitializeComponent();
        HistoryStatus.Text = history.Entries.Count == 0
            ? "표시할 분석 이력이 없습니다."
            : $"저장된 분석 {history.Entries.Count:N0}건";
        if (history.SkippedCount > 0)
            HistoryStatus.Text += $" · 읽을 수 없는 기록 {history.SkippedCount:N0}건";

        HistoryGrid.ItemsSource = history.Entries;
        HistoryGrid.SelectedIndex = history.Entries.Count > 0 ? 0 : -1;
    }

    public AnalysisResult? SelectedResult => HistoryGrid.SelectedItem as AnalysisResult;

    private void SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        OpenResultButton.IsEnabled = SelectedResult is not null;

    private void OpenResultClick(object sender, RoutedEventArgs e)
    {
        if (SelectedResult is not null)
            DialogResult = true;
    }
}
