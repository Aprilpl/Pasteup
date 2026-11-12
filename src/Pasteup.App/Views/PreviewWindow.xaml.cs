using System.Windows;
using System.Windows.Media.Imaging;
using Pasteup.App.Services;
using Pasteup.App.ViewModels;
using Pasteup.Core.Capture;
using Pasteup.Core.History;

namespace Pasteup.App.Views;

/// <summary>空格预览窗口：悬于面板之上的浮层，内容随面板选中条目切换。
/// 点击不激活（WS_EX_NOACTIVATE + ShowActivated=False），键盘焦点始终留在面板。</summary>
public partial class PreviewWindow : Window
{
    // 窗口宽度 == 可见卡片（720 / 600）+ 左右 1px 边框；无阴影画布边距（非分层窗，系统 DWM 阴影画在窗口外）
    public const double ImageWindowWidth = 722;
    public const double TextWindowWidth = 602;

    private PreviewViewModel? _viewModel;
    private MonitorRect? _monitor;
    private PreviewMode _lastMode;

    /// <summary>用户点了标题栏收起钮（生命周期归面板管，窗口自己只发信号）。</summary>
    public event Action? CloseRequested;

    public PreviewViewModel ViewModel
    {
        get => _viewModel!;
        set
        {
            if (_viewModel is not null)
            {
                _viewModel.PropertyChanged -= OnVmPropertyChanged;
                _viewModel.CancelPendingImageLoad(); // 换 VM 前取消旧图解码（结果不再回写）
            }
            _viewModel = value;
            _viewModel.PropertyChanged += OnVmPropertyChanged;
        }
    }

    public PreviewWindow()
    {
        InitializeComponent();
        DataContext = this;
    }

    protected override void OnClosed(EventArgs e)
    {
        // 预览窗关闭（空闲深度清理后不再复用）时这张全尺寸图已无人看：取消在途解码，
        // 避免解完的白内存，也让迟到的结果不再写进即将被弃置的 VM（token + ReferenceEquals 双兜底）。
        _viewModel?.CancelPendingImageLoad();
        if (_viewModel is not null) _viewModel.PropertyChanged -= OnVmPropertyChanged;
        base.OnClosed(e);
    }

    /// <summary>图像后台加载完成态驱动占位文案：解码完成且无图才显示「缓存已被清理」，加载中不显示
    ///（图像区高度恒 420，图晚到不改变窗口高度，落位无需重跑）。</summary>
    private void OnVmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (nameof(PreviewViewModel.ImageSource) or nameof(PreviewViewModel.IsImageMissing)))
            return;
        ImgMissing.Visibility = ViewModel.Mode == PreviewMode.Image && ViewModel.IsImageMissing
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        Services.Win32Input.PreferRoundedCorners(this);
        // 非分层不透明窗，圆角+阴影由 DWM 系统：ExtendFrame 1px 引出原生软阴影，系统描边关闭走令牌
        Services.Win32Input.EnableDwmShadow(this);
        // 点击不激活：被点击时仍收鼠标输入（收起钮、滚轮滚动文本），但不抢前台——
        // 面板 Deactivated 即隐藏，预览一旦夺走激活整个交互就塌了
        Services.Win32Input.SetNoActivate(this);
    }

    /// <summary>用面板选中条目更新内容并（在需要时）重新落位。
    /// 打开与跨类型切换（图像↔文本）时整窗重定位；同类型跟随切换只保 Top、按新宽度重新水平居中。</summary>
    public void ApplyEntry(ClipboardEntry entry, MonitorRect? monitor)
    {
        if (monitor is not null) _monitor = monitor;
        ViewModel.Update(entry, LoadImageAsync, LoadAppIcon);

        var isImage = ViewModel.Mode == PreviewMode.Image;
        ImgHost.Visibility = isImage ? Visibility.Visible : Visibility.Collapsed;
        TextHost.Visibility = isImage ? Visibility.Collapsed : Visibility.Visible;
        ImgMissing.Visibility = isImage && ViewModel.IsImageMissing ? Visibility.Visible : Visibility.Collapsed;
        Width = isImage ? ImageWindowWidth : TextWindowWidth;
        // SizeToContent=Height：先强制布局拿到实测高度，落位数学才有效
        UpdateLayout();

        var modeChanged = _lastMode != ViewModel.Mode;
        if (_monitor is { } m)
        {
            Left = PreviewLayout.CenterLeft(m, m.Dpi, Width);
            // 内容增高向下生长：仅在整窗重定位时重取锚点 Top，其余时候钳回屏内即可
            Top = modeChanged || !IsVisible
                ? PreviewLayout.AnchorTop(m, m.Dpi, ActualHeight)
                : PreviewLayout.ClampTopWithin(m, m.Dpi, Top, ActualHeight);
        }
        _lastMode = ViewModel.Mode;
    }

    /// <summary>来源应用图标（与面板行同一服务缓存，缓存命中同步完成）。</summary>
    private static Task<BitmapSource?> LoadAppIcon(ClipboardEntry entry) => App.Current.SourceIcons.GetAsync(entry);

    /// <summary>全尺寸解码挪后台线程：大图 ↑↓ 翻页不再同步冻结 UI；FromPng 产物已 Freeze，跨线程安全。
    /// token 取消（换条目/关窗）：Task.Run 入口即置为取消不再排队执行，读盘/解码前还会再查一次。</summary>
    private static Task<BitmapSource?> LoadImageAsync(ClipboardEntry entry, CancellationToken token)
        => Task.Run(() => LoadImageSource(entry, token), token);

    private static BitmapSource? LoadImageSource(ClipboardEntry entry, CancellationToken token)
    {
        try
        {
            if (token.IsCancellationRequested) return null; // 排队期间已翻走：不读盘
            if (entry.ImageFile is not { } file) return null;
            var png = App.Current.Store.LoadImagePng(file);
            if (png is null) return null;
            if (token.IsCancellationRequested) return null; // 读盘期间已翻走：省掉最贵的一步（全量解码 68ms/4K）
            return ImageCodec.FromPng(png);
        }
        catch
        {
            return null; // 缓存文件缺失/解码失败 → 显示「缓存已清理」占位，不让预览崩
        }
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => CloseRequested?.Invoke();
}
