using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using QuickFilter.Core;

namespace QuickFilter.App.Views;

public enum BrowseSlot { Source, Left, Right, Deleted }

public sealed record SetupRequest(string SourceDir, string LeftDir, string RightDir, string DeletedDir,
    OrderRule OrderRule, bool Recursive, bool FastPreview, bool PlayGif);

public sealed partial class SetupView : UserControl
{
    public event EventHandler<BrowseSlot>? BrowseRequested;
    public event EventHandler<SetupRequest>? StartRequested;
    /// <summary>固定 PIN / 隐私模式设置保存。</summary>
    public event EventHandler? SettingsSaved;
    /// <summary>点击"继续上次会话"。</summary>
    public event EventHandler? ResumeRequested;

    /// <summary>显示"继续上次会话"入口（上次目录仍可恢复时由主窗口调用）。</summary>
    public void SetResumeInfo(string dir, int processed, int total)
    {
        ResumePanel.Visibility = Visibility.Visible;
        ResumeInfoText.Text = $"上次筛选目录：{dir}\n已处理 {processed} / {total} 张，可直接继续（进度实时保存在源目录）。";
    }

    private void Resume_Click(object sender, RoutedEventArgs e) => ResumeRequested?.Invoke(this, EventArgs.Empty);

    public SetupView()
    {
        InitializeComponent();
        // PIN 输入即生效（带防抖），无需再点保存按钮
        FixedPinBox.TextChanged += OnPinTextChanged;
    }

    private bool _initializing;
    private CancellationTokenSource? _pinDebounce;

    /// <summary>回填已保存的设置（在主窗口构造时调用）。</summary>
    public void SetSettings(string fixedPin, bool privacyMode)
    {
        _initializing = true;
        FixedPinBox.Text = fixedPin;
        PrivacyCheck.IsChecked = privacyMode;
        _initializing = false;
    }

    public string FixedPinText => FixedPinBox.Text.Trim();
    public bool PrivacyEnabled => PrivacyCheck.IsChecked == true;

    public void ShowSettingsSaved(string message)
    {
        SettingsStatusText.Text = message;
        SettingsStatusText.Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 46, 158, 46));
    }

    private void ShowSettingsError(string message)
    {
        SettingsStatusText.Text = message;
        SettingsStatusText.Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 200, 40, 40));
    }

    /// <summary>PIN 输入变化：校验通过后 300ms 防抖保存（用户停止输入即生效）。</summary>
    private void OnPinTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_initializing) return;
        _pinDebounce?.Cancel();
        _pinDebounce = new CancellationTokenSource();
        var token = _pinDebounce.Token;

        var pin = FixedPinBox.Text.Trim();
        if (pin.Length > 0 && !pin.All(char.IsDigit))
        {
            ShowSettingsError("PIN 只能包含数字（4-8 位）");
            return;
        }
        _ = DebounceSaveAsync(token);
    }

    private async Task DebounceSaveAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(300, token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        SettingsSaved?.Invoke(this, EventArgs.Empty);
    }

    private void SaveSettings_Click(object sender, RoutedEventArgs e)
    {
        var pin = FixedPinBox.Text.Trim();
        if (pin.Length > 0 && !pin.All(char.IsDigit))
        {
            ShowSettingsError("PIN 只能包含数字（4-8 位）");
            return;
        }
        SettingsSaved?.Invoke(this, EventArgs.Empty);
    }

    public void SetDirectory(BrowseSlot slot, string path)
    {
        var box = slot switch
        {
            BrowseSlot.Source => SourceBox,
            BrowseSlot.Left => LeftBox,
            BrowseSlot.Right => RightBox,
            _ => DeletedBox,
        };
        box.Text = path;
    }

    public void SetStatus(string text) => StatusText.Text = text;

    public void SetServerStatus(string text) => ServerStatusText.Text = text;

    private void BrowseSource_Click(object sender, RoutedEventArgs e) => BrowseRequested?.Invoke(this, BrowseSlot.Source);
    private void BrowseLeft_Click(object sender, RoutedEventArgs e) => BrowseRequested?.Invoke(this, BrowseSlot.Left);
    private void BrowseRight_Click(object sender, RoutedEventArgs e) => BrowseRequested?.Invoke(this, BrowseSlot.Right);
    private void BrowseDeleted_Click(object sender, RoutedEventArgs e) => BrowseRequested?.Invoke(this, BrowseSlot.Deleted);

    private void Start_Click(object sender, RoutedEventArgs e)
    {
        var order = OrderModified.IsChecked == true ? OrderRule.ModifiedTime
                  : OrderRandom.IsChecked == true ? OrderRule.Random
                  : OrderRule.FileName;

        var req = new SetupRequest(
            SourceDir: SourceBox.Text.Trim(),
            LeftDir: LeftBox.Text.Trim(),
            RightDir: RightBox.Text.Trim(),
            DeletedDir: DeletedBox.Text.Trim(),
            OrderRule: order,
            Recursive: RecursiveCheck.IsChecked == true,
            FastPreview: FastPreviewCheck.IsChecked == true,
            PlayGif: PlayGifCheck.IsChecked == true);
        StartRequested?.Invoke(this, req);
    }
}
