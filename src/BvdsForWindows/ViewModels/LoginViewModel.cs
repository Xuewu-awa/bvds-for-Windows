using System.IO;
using System.Windows.Media.Imaging;
using BvdsForWindows.Core;

namespace BvdsForWindows.ViewModels;

/// <summary>登录页 VM</summary>
public sealed class LoginViewModel : ViewModelBase
{
    private readonly BvdsService _service;
    private readonly Action<string> _toast;

    private BitmapImage? _qrImage;
    public BitmapImage? QrImage
    {
        get => _qrImage;
        private set
        {
            if (Set(ref _qrImage, value))
                OnPropertyChanged(nameof(HasQr));   // 同步通知可见性
        }
    }

    public bool HasQr => QrImage != null;

    private string _statusText = "未登录";
    public string StatusText
    {
        get => _statusText;
        private set => Set(ref _statusText, value);
    }

    private string _statusColor = "#8A8982";
    public string StatusColor
    {
        get => _statusColor;
        private set => Set(ref _statusColor, value);
    }

    /// <summary>状态胶囊底色（kt 版彩色浅底）</summary>
    private string _statusBg = "Transparent";
    public string StatusBg
    {
        get => _statusBg;
        private set => Set(ref _statusBg, value);
    }

    private bool _isLoggedIn;
    public bool IsLoggedIn
    {
        get => _isLoggedIn;
        private set => Set(ref _isLoggedIn, value);
    }

    private string _userName = "";
    public string UserName
    {
        get => _userName;
        private set => Set(ref _userName, value);
    }

    public AsyncRelayCommand StartLoginCmd { get; }
    public RelayCommand CancelLoginCmd { get; }
    public RelayCommand LogoutCmd { get; }

    /// <summary>登录状态变化（成功/退出）时触发，供主 VM 刷新全局登录态</summary>
    public event Action? LoginStateChanged;

    public LoginViewModel(BvdsService service, Action<string> toast)
    {
        _service = service;
        _toast = toast;
        StartLoginCmd = new AsyncRelayCommand(_ => StartLoginAsync());
        CancelLoginCmd = new RelayCommand(_ => _service.CancelLogin());
        LogoutCmd = new RelayCommand(_ =>
        {
            _service.Logout();
            SetLoggedOut();
            toast("已退出登录");
            LoginStateChanged?.Invoke();
        });
        _ = CheckAsync();
    }

    private async Task CheckAsync()
    {
        var result = await _service.CheckLoginAsync();
        if (result is LoginResult.LoggedIn li)
        {
            IsLoggedIn = true;
            UserName = li.Name;
            StatusText = $"已登录: {li.Name}";
            StatusColor = "#16A34A";
            StatusBg = "#1416A34A";
            LoginStateChanged?.Invoke();
        }
        else
        {
            SetLoggedOut();
        }
    }

    private void SetLoggedOut()
    {
        IsLoggedIn = false;
        UserName = "";
        QrImage = null;
        StatusText = "未登录";
        StatusColor = "#8A8982";
        StatusBg = "Transparent";
    }

    private async Task StartLoginAsync()
    {
        _service.StartLogin(
            onStatus: status =>
            {
                System.Windows.Application.Current.Dispatcher.BeginInvoke(() =>
                {
                    switch (status)
                    {
                        case LoginStatus.Generating:
                            StatusText = "正在生成二维码..."; StatusColor = "#8A8982"; StatusBg = "Transparent"; break;
                        case LoginStatus.WaitingScan:
                            StatusText = "等待扫码..."; StatusColor = "#6FAFAF"; StatusBg = "#336FAFAF"; break;
                        case LoginStatus.Scanned:
                            StatusText = "已扫描，请在手机上确认"; StatusColor = "#D97706"; StatusBg = "#14D97706"; break;
                        case LoginStatus.Expired:
                            StatusText = "二维码已过期，正在重新生成..."; StatusColor = "#DC2626"; StatusBg = "#14DC2626"; break;
                        case LoginStatus.Success:
                            StatusText = "登录成功！"; StatusColor = "#16A34A"; StatusBg = "#1416A34A";
                            _ = CheckAsync();   // 刷新登录态显示
                            break;
                        case LoginStatus.Timeout:
                            StatusText = "登录超时，请重试"; StatusColor = "#DC2626"; StatusBg = "#14DC2626"; break;
                        case LoginStatus.QRError:
                            StatusText = "二维码获取失败"; StatusColor = "#DC2626"; StatusBg = "#14DC2626"; break;
                        case LoginStatus.Cancelled:
                            StatusText = "已取消"; StatusColor = "#8A8982"; StatusBg = "Transparent"; break;
                    }
                });
            },
            onQrImage: png =>
            {
                System.Windows.Application.Current.Dispatcher.BeginInvoke(() =>
                {
                    QrImage = PngToBitmap(png);
                });
            });
    }

    private static BitmapImage PngToBitmap(byte[] png)
    {
        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        bmp.StreamSource = new MemoryStream(png);
        bmp.EndInit();
        bmp.Freeze();
        return bmp;
    }
}
