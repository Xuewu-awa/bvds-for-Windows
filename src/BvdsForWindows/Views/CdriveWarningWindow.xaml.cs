using System.IO;
using System.Windows;

namespace BvdsForWindows.Views;

/// <summary>
/// C 盘下载目录提醒窗口。
/// 返回 true = 继续下载（可能已更换目录），false = 取消本次下载。
/// </summary>
public partial class CdriveWarningWindow : Window
{
    private readonly string _currentDir;
    private string _newDir = "";

    public CdriveWarningWindow(string currentDir)
    {
        InitializeComponent();
        _currentDir = currentDir;
        DirText.Text = $"当前目录: {currentDir}";
    }

    /// <summary>用户是否勾选了"以后不再提醒"</summary>
    public bool DontAskAgain => DontAskBox.IsChecked == true;

    /// <summary>用户选择的新下载目录（未更换则为空）</summary>
    public string NewDir => _newDir;

    /// <summary>是否已更换目录</summary>
    public bool ChangedDir => _newDir.Length > 0;

    private void Continue_Click(object sender, RoutedEventArgs e) => DialogResult = true;

    private void ChangeDir_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "选择新的下载目录",
            // 初始目录必须存在，否则 IFileDialog 抛异常
            InitialDirectory = Directory.Exists(_currentDir) ? _currentDir : "",
        };
        if (dlg.ShowDialog() == true)
        {
            _newDir = dlg.FolderName;
            DialogResult = true;   // 已更换目录，继续下载
        }
        // 取消选择则停留在窗口
    }
}
