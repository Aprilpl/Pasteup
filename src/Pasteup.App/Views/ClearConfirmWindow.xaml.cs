using System.Windows;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using Pasteup.App.Services;

namespace Pasteup.App.Views;

/// <summary>清空历史二次确认（方案 B 卡片式）。
/// 复选框「清空置顶的项目」默认不勾：不勾 = 保留置顶；勾选 = 连置顶一起全清。
/// 键位 Esc 取消 / Enter 清空 / Space 切换勾选（焦点在复选框上时走默认行为，避免双触发）。</summary>
public partial class ClearConfirmWindow : Window
{
    private readonly int _total;
    private readonly int _keep;
    private readonly int _favorite;

    /// <summary>用户勾选了「清空置顶/收藏的项目」→ 引擎应传 includePinned: true。</summary>
    public bool ClearPinned => ClearPinCheck.IsChecked == true;

    /// <param name="keep">置顶 ∪ 收藏 条目数（默认清空时保留的数量）。</param>
    /// <param name="favorite">收藏条目数（&gt;0 时选项文案提及「收藏」）。</param>
    public ClearConfirmWindow(int total, int keep, int favorite)
    {
        InitializeComponent();
        _total = total;
        _keep = keep;
        _favorite = favorite;

        var show = ClearConfirmText.ShouldShowCheckbox(keep);
        OptionRow.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        PinCount.Text = keep.ToString();
        OptionLabel.Text = favorite > 0 ? "清空置顶/收藏的项目" : "清空置顶的项目";
        RenderDesc();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        // 模态对话框：可激活（与预览窗的 SetNoActivate 相反），圆角 + 原生 DWM 阴影同款
        Services.Win32Input.PreferRoundedCorners(this);
        Services.Win32Input.EnableDwmShadow(this);
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                DialogResult = false;
                e.Handled = true;
                break;
            case Key.Enter:
                DialogResult = true;
                e.Handled = true;
                break;
            case Key.Space:
                // 复选框自身有默认 Space 行为，别替它触发两次
                if (!ReferenceEquals(Keyboard.FocusedElement, ClearPinCheck))
                {
                    ClearPinCheck.IsChecked = ClearPinCheck.IsChecked != true;
                    e.Handled = true;
                }
                break;
        }
        base.OnPreviewKeyDown(e);
    }

    private void OnCheckChanged(object sender, RoutedEventArgs e) => RenderDesc();

    private void RenderDesc()
    {
        Desc.Inlines.Clear();
        foreach (var seg in ClearConfirmText.Segments(_total, _keep, _favorite, ClearPinned))
        {
            var run = new Run(seg.Text);
            if (seg.IsMono)
            {
                run.FontFamily = (FontFamily)FindResource("FontMono");
                run.FontSize = 11.5;
                run.FontWeight = FontWeights.Medium;
                run.Foreground = (Brush)FindResource("TextBrush");
            }
            Desc.Inlines.Add(run);
        }
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => DialogResult = false;

    private void OnConfirmClick(object sender, RoutedEventArgs e) => DialogResult = true;
}
