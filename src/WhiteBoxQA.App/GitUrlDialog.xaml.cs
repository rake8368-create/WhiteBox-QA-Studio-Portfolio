using System.Windows;
using WhiteBoxQA.Infrastructure;

namespace WhiteBoxQA.App;

public partial class GitUrlDialog : Window
{
    public GitUrlDialog()
    {
        InitializeComponent();
        Loaded += (_, _) => UrlBox.Focus();
    }

    public string Address => UrlBox.Text.Trim();

    private void AcceptClick(object sender, RoutedEventArgs e)
    {
        if (GitSourceImporter.IsSupportedUrl(Address))
            DialogResult = true;
        else
            ErrorText.Text = "사용자 정보나 토큰이 포함되지 않은 공개 HTTPS 주소를 입력하세요.";
    }
}
