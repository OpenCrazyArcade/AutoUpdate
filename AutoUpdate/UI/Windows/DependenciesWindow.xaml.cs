using System;
using System.Diagnostics;
using System.Windows;
using OpenCrazyArcade.AutoUpdate.Managers;

namespace OpenCrazyArcade.AutoUpdate.UI.Windows;

internal partial class DependenciesWindow : Window
{
    private readonly UIManager _uiManager;

    public DependenciesWindow(UIManager uiManager)
    {
        _uiManager = uiManager;
        InitializeComponent();
    }

    private void BtnDownload_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "https://go.microsoft.com/fwlink/p/?LinkId=2124703",
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Không thể mở trình duyệt: {ex.Message}", "Lỗi", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void BtnRetry_Click(object sender, RoutedEventArgs e)
    {
        if (_uiManager.CheckingWebview2Runtime())
        {
            Close();
            await _uiManager.ActivateLauncher();
        }
        else
        {
            MessageBox.Show("Vẫn chưa tìm thấy WebView2 Runtime. Hãy hoàn tất việc cài đặt rồi thử lại!", "Thông báo", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void BtnExit_Click(object sender, RoutedEventArgs e)
    {
        Application.Current.Shutdown();
    }
}
