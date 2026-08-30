using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using QuickFilter.Core;
using Windows.Foundation;
using Windows.System;
using Windows.UI.Core;

namespace QuickFilter.App.Views;

public sealed partial class ScreeningView : UserControl
{
    private const double DragThreshold = 40.0;
    private const double DragDominance = 1.5;
    private const double ZoomMin = 0.1;
    private const double ZoomMax = 8.0;
    private const int CacheLimit = 6;

    /// <summary>固定缩放档位（相对真实 1:1 的倍率）。滚轮与 +/− 只在档位间跳转。</summary>
    private static readonly double[] ZoomLevels = { 0.25, 0.33, 0.5, 0.67, 0.75, 1.0, 1.25, 1.5, 2.0, 2.5, 3.0, 4.0, 5.0, 6.0, 8.0 };

    private Session? _session;
    private int _loadVersion;      // 防止陈旧加载结果覆盖当前图片
    private bool _dragActive;
    private Point _dragStart;
    private Point _lastDrag;
    private string? _shownRel;     // 当前已展示的相对路径（用于切换图片时复位缩放）
    private uint _currentPixelWidth;   // 当前图片的原始像素宽（用于真实 1:1 计算）
    private uint _currentPixelHeight;

    private readonly Dictionary<string, ImageLoader.Result> _cache = new();
    private int _cacheClock;
    private bool _privacyMode;

    public event EventHandler? ExportRequested;
    public event EventHandler? StopRequested;
    /// <summary>队列全部处理完成后触发（仅触发一次）。</summary>
    public event EventHandler? SessionCompleted;
    /// <summary>每张图片显示/操作完成后触发（供遥控服务广播状态）。</summary>
    public event EventHandler? StateChanged;

    private bool _completionNotified;

    /// <summary>遥控端触发的手势操作（UI 线程调用）。</summary>
    public Task PerformRemoteAsync(SessionAction action) => PerformAsync(action);

    /// <summary>遥控端触发的撤销（UI 线程调用）。</summary>
    public Task UndoRemoteAsync() => UndoAsync();

    public void SetServerStatus(string text) => ServerStatusText.Text = text;

    /// <summary>隐私模式：电脑端不解码/不显示图片，只显示进度与操作记录（即时生效）。</summary>
    public void SetPrivacyMode(bool enabled)
    {
        _privacyMode = enabled;
        if (_session is not null) _ = ShowCurrentAsync();
    }

    public ScreeningView() => InitializeComponent();

    // ---------- 会话接入 ----------

    public void StartSession(Session session)
    {
        _session = session;
        _shownRel = null;
        _cache.Clear();
        _completionNotified = false;
        ResetZoom();
        BtnLeft.Content = $"◀ 左（{DirName(session.LeftDir)}）";
        BtnRight.Content = $"右（{DirName(session.RightDir)}）▶";
        BtnUndo.IsEnabled = true;
        MessageBar.IsOpen = false;
        FocusSelf();
        _ = ShowCurrentAsync();
    }

    public void ResetView()
    {
        _session = null;
        _loadVersion++;
        _cache.Clear();
        _shownRel = null;
        _completionNotified = false;
        MainImage.Source = null;
        FileNameText.Text = "";
        InfoText.Text = "";
        CountsText.Text = "";
        ProgressBar.Maximum = 1;
        ProgressBar.Value = 0;
        BtnUndo.IsEnabled = false;
        DragHint.Visibility = Visibility.Collapsed;
        MessageBar.IsOpen = false;
        ResetZoom();
    }

    public void ShowMessage(string message, InfoBarSeverity severity)
    {
        MessageBar.Severity = severity;
        MessageBar.Message = message;
        MessageBar.IsOpen = true;
    }

    private static string DirName(string dir) => Path.GetFileName(Path.TrimEndingDirectorySeparator(dir));

    // ---------- 图片展示与预加载 ----------

    private async Task ShowCurrentAsync()
    {
        var session = _session;
        if (session is null) return;

        var rel = session.CurrentRelativePath;
        var version = ++_loadVersion;

        if (rel is null)
        {
            FileNameText.Text = "全部处理完成 ✓";
            InfoText.Text = "";
            MainImage.Source = null;
            _shownRel = null;
            UpdateCounts();
            ApplyPrivacyUi(session, visible: null);
            RaiseStateChanged();
            if (!_completionNotified)
            {
                _completionNotified = true;
                SessionCompleted?.Invoke(this, EventArgs.Empty);
            }
            return;
        }

        try
        {
            ProgressBar.Maximum = session.TotalCount;
            ProgressBar.Value = session.ProcessedCount;
            var index = session.PositionOf(rel);

            // 隐私模式：不解码图片，仅进度 + 操作记录
            if (_privacyMode)
            {
                ApplyPrivacyUi(session, visible: true);
                FileNameText.Text = "🔒 隐私模式";
                InfoText.Text = $"第 {index} / {session.TotalCount} 张";
                UpdateCounts();
                RaiseStateChanged();
                return;
            }
            ApplyPrivacyUi(session, visible: false);

            var full = Path.Combine(session.SourceDir, rel.Replace('/', Path.DirectorySeparatorChar));
            FileNameText.Text = Path.GetFileName(full);
            InfoText.Text = $"第 {index} / {session.TotalCount} 张";
            UpdateCounts();

            // 切换到新图片时复位缩放（回到可拖拽分类状态）
            if (_shownRel != rel) ResetZoom();
            _shownRel = rel;

            AppLog.Info($"显示 {rel}（{session.PositionOf(rel)}/{session.TotalCount}）开始…");
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var entry = await GetOrLoadAsync(session, rel);
            if (version != _loadVersion || _session != session) return;

            MainImage.Source = (ImageSource)entry.Source;
            _currentPixelWidth = entry.PixelWidth;
            _currentPixelHeight = entry.PixelHeight;
            InfoText.Text = BuildInfoText(entry, index, session.TotalCount);
            AppLog.Info($"显示 {rel} 完成（{sw.ElapsedMilliseconds} ms）");

            PreloadNext(session, rel);
            FocusSelf();
            RaiseStateChanged();
        }
        catch (Exception ex)
        {
            if (version != _loadVersion || _session != session) return;
            AppLog.Error($"显示失败 {rel}: {ex}");
            MainImage.Source = null;
            InfoText.Text = $"图片加载失败 · 第 {session.PositionOf(rel)} / {session.TotalCount} 张";
            ShowMessage($"无法加载 {Path.GetFileName(rel)}：{ex.Message}\n（若为 .heic 文件，请在微软商店安装“HEIF 图像扩展”）",
                InfoBarSeverity.Warning);
            RaiseStateChanged();
        }
    }

    private void RaiseStateChanged() => StateChanged?.Invoke(this, EventArgs.Empty);

    /// <summary>切换隐私模式相关 UI 可见性并刷新操作记录（visible=null 表示按当前模式处理）。</summary>
    private void ApplyPrivacyUi(Session session, bool? visible)
    {
        var privacy = visible ?? _privacyMode;
        MainImage.Visibility = privacy ? Visibility.Collapsed : Visibility.Visible;
        PrivacyPanel.Visibility = privacy ? Visibility.Visible : Visibility.Collapsed;
        LogPanel.Visibility = privacy ? Visibility.Visible : Visibility.Collapsed;
        if (privacy) RefreshLogList(session);
    }

    /// <summary>操作记录：最近 12 条（最新在上）。</summary>
    private void RefreshLogList(Session session)
    {
        var lines = session.Logs
            .TakeLast(12)
            .Select(l => $"{l.TimestampUtc.ToLocalTime():HH:mm:ss}  {l.Action.Label(),-4}  {l.FileName}  {l.Result}")
            .Reverse()
            .ToList();
        LogList.ItemsSource = lines;
    }

    private static string BuildInfoText(ImageLoader.Result e, int index, int total)
    {
        var sb = new System.Text.StringBuilder();
        if (e.PixelWidth > 0 && e.PixelHeight > 0) sb.Append($"{e.PixelWidth}×{e.PixelHeight} · ");
        sb.Append($"{e.SizeMb:0.0} MB");
        if (e.Taken is DateTime taken) sb.Append($" · 拍摄 {taken:yyyy-MM-dd HH:mm}");
        sb.Append($" · 修改 {e.Modified:yyyy-MM-dd HH:mm}");
        sb.Append($" · 第 {index} / {total} 张");
        return sb.ToString();
    }

    /// <summary>取当前图片解码结果（命中缓存优先），并把后续 1–2 张的预加载任务挂起。</summary>
    private async Task<ImageLoader.Result> GetOrLoadAsync(Session session, string rel)
    {
        if (_cache.TryGetValue(rel, out var cached))
        {
            cached.Clock = ++_cacheClock;
            return cached;
        }
        var entry = await ImageLoader.LoadAsync(session, rel);
        entry.Clock = ++_cacheClock;
        TrimCache();
        return entry;
    }

    private void PreloadNext(Session session, string currentRel)
    {
        int pos = session.PositionOf(currentRel) - 1;
        var pending = session.Queue
            .Skip(pos + 1)
            .Where(p => !session.Processed.ContainsKey(p))
            .Take(2)
            .ToList();
        foreach (var next in pending)
        {
            if (_cache.ContainsKey(next)) continue;
            _ = PreloadSafeAsync(session, next);
        }
    }

    private async Task PreloadSafeAsync(Session session, string rel)
    {
        try { await GetOrLoadAsync(session, rel); }
        catch (Exception ex) { AppLog.Info($"预加载失败 {rel}: {ex.Message}"); }
    }

    private void TrimCache()
    {
        if (_cache.Count <= CacheLimit) return;
        foreach (var key in _cache.OrderBy(kv => kv.Value.Clock).Take(_cache.Count - CacheLimit).Select(kv => kv.Key).ToList())
            _cache.Remove(key);
    }

    private void UpdateCounts()
    {
        var s = _session;
        if (s is null) return;
        CountsText.Text = $"左 {s.LeftCount} · 右 {s.RightCount} · 删除 {s.DeleteCount} · 跳过 {s.SkipCount} · 剩余 {s.RemainingCount}";
        ProgressBar.Maximum = s.TotalCount;
        ProgressBar.Value = s.ProcessedCount;
    }

    // ---------- 操作执行 ----------

    private async Task PerformAsync(SessionAction action)
    {
        var session = _session;
        if (session is null) return;
        if (session.CurrentRelativePath is null) return;
        try
        {
            var result = await session.ExecuteAsync(action);
            if (!result.Ok)
            {
                ShowMessage(result.Error ?? "操作失败", InfoBarSeverity.Error);
                return;
            }
            await SessionStore.SaveAsync(session);
            await ShowCurrentAsync();
        }
        catch (Exception ex)
        {
            AppLog.Error($"操作失败 {action}: {ex}");
            ShowMessage("操作失败：" + ex.Message, InfoBarSeverity.Error);
        }
    }

    private async Task UndoAsync()
    {
        var session = _session;
        if (session is null) return;
        try
        {
            var result = await session.UndoAsync();
            if (!result.Ok)
            {
                ShowMessage(result.Error ?? "无法撤销", InfoBarSeverity.Warning);
                return;
            }
            await SessionStore.SaveAsync(session);
            await ShowCurrentAsync();
        }
        catch (Exception ex)
        {
            AppLog.Error($"撤销失败: {ex}");
            ShowMessage("撤销失败：" + ex.Message, InfoBarSeverity.Error);
        }
    }

    // ---------- 缩放与平移 ----------

    private bool IsZoomed => Math.Abs(ZoomScale.ScaleX - 1.0) > 0.001;

    /// <summary>
    /// 真实 1:1 需要的相对缩放比例：1 个图像像素 = 1 个物理屏幕像素。
    /// ZoomScale=1 是"适应窗口"，因此 1:1 = 自然尺寸(DIP) / 适应尺寸(DIP)。
    /// </summary>
    private double OneToOneScale()
    {
        if (_currentPixelWidth == 0 || MainImage.ActualWidth <= 0) return 1.0;
        var raster = XamlRoot?.RasterizationScale ?? 1.0;
        var naturalDips = _currentPixelWidth / raster;
        return naturalDips / MainImage.ActualWidth;
    }

    private double ZoomPercent => ZoomScale.ScaleX / OneToOneScale() * 100.0;

    /// <summary>按固定档位缩放：direction = +1 放大一档，-1 缩小一档（以光标/中心为锚点）。</summary>
    private void ZoomStepByLevel(int direction, Point? anchor = null)
    {
        var oneToOne = OneToOneScale();
        var current = ZoomScale.ScaleX / oneToOne;   // 当前倍率（相对 1:1）
        double target;
        if (direction > 0)
            target = ZoomLevels.FirstOrDefault(l => l > current * 1.02);
        else
            target = ZoomLevels.LastOrDefault(l => l < current * 0.98);
        if (target <= 0)
            target = direction > 0 ? ZoomLevels[^1] : ZoomLevels[0];
        ZoomTo(target * oneToOne, anchor);
    }

    private void ZoomTo(double newScale, Point? anchor = null)
    {
        var s = Math.Clamp(newScale, ZoomMin, ZoomMax);
        if (Math.Abs(s - ZoomScale.ScaleX) < 0.0001) return;

        var center = new Point(ImageHost.ActualWidth / 2, ImageHost.ActualHeight / 2);
        var p = anchor ?? center;
        // 保持锚点（光标）下的图像点不动：T' = T + (s - s')(p - C)
        var dx = (ZoomScale.ScaleX - s) * (p.X - center.X);
        var dy = (ZoomScale.ScaleX - s) * (p.Y - center.Y);
        ZoomScale.ScaleX = ZoomScale.ScaleY = s;
        ZoomPan.X += dx;
        ZoomPan.Y += dy;
        ClampPan();
        UpdateZoomUi();
    }

    private void PanBy(double dx, double dy)
    {
        ZoomPan.X += dx;
        ZoomPan.Y += dy;
        ClampPan();
    }

    private void ClampPan()
    {
        var limitX = ImageHost.ActualWidth * ZoomScale.ScaleX;
        var limitY = ImageHost.ActualHeight * ZoomScale.ScaleY;
        ZoomPan.X = Math.Clamp(ZoomPan.X, -limitX, limitX);
        ZoomPan.Y = Math.Clamp(ZoomPan.Y, -limitY, limitY);
    }

    private void ResetZoom()
    {
        ZoomScale.ScaleX = ZoomScale.ScaleY = 1.0;
        ZoomPan.X = 0;
        ZoomPan.Y = 0;
        UpdateZoomUi();
    }

    private void UpdateZoomUi()
    {
        var oneToOne = OneToOneScale();
        var atOneToOne = Math.Abs(ZoomScale.ScaleX - oneToOne) < 0.01;
        if (IsZoomed || atOneToOne)
        {
            ZoomBadge.Visibility = Visibility.Visible;
            ZoomBadgeText.Text = atOneToOne ? "100% (1:1)" : $"{ZoomPercent:0}%";
            HintText.Text = "缩放模式：拖拽平移 / 滚轮或 +− 缩放 / 1:1 真实像素 / 双击或 Esc 适应窗口；分类请用 ←→↑↓ 或 WASD";
        }
        else
        {
            ZoomBadge.Visibility = Visibility.Collapsed;
            HintText.Text = "拖拽或按键：←/A 左　→/D 右　↑/W 删除　↓/S 跳过　Ctrl+Z 撤销　+/− 缩放";
        }
    }

    private void ImageHost_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        if (_session is null) return;
        var delta = e.GetCurrentPoint(ImageHost).Properties.MouseWheelDelta;
        if (delta == 0) return;
        e.Handled = true;
        ZoomStepByLevel(delta > 0 ? 1 : -1, e.GetCurrentPoint(ImageHost).Position);
    }

    private void ImageHost_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        e.Handled = true;
        ResetZoom();     // 双击 = 适应窗口大小
        FocusSelf();     // 防止双击后焦点丢失导致快捷键失效
    }

    private void BtnZoomIn_Click(object sender, RoutedEventArgs e) => ZoomStepByLevel(1);
    private void BtnZoomOut_Click(object sender, RoutedEventArgs e) => ZoomStepByLevel(-1);
    private void BtnZoomReset_Click(object sender, RoutedEventArgs e) => ZoomTo(OneToOneScale());
    private void BtnFit_Click(object sender, RoutedEventArgs e) => ResetZoom();

    /// <summary>确保键盘事件可被接收：把焦点交给本视图（Grid 不可聚焦，UserControl 可以）。</summary>
    private void FocusSelf()
    {
        if (!IsLoaded) return;
        _ = this.Focus(FocusState.Programmatic);
    }

    // ---------- 鼠标手势 ----------

    private void ImageHost_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_session is null || _session.CurrentRelativePath is null) return;
        var point = e.GetCurrentPoint(ImageHost);
        _dragActive = true;
        _dragStart = point.Position;
        _lastDrag = point.Position;
        ImageHost.CapturePointer(e.Pointer);
        FocusSelf();   // 点击图片后立即收回焦点，保证快捷键可用
    }

    private void ImageHost_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragActive) return;
        var pos = e.GetCurrentPoint(ImageHost).Position;
        if (IsZoomed)
        {
            // 缩放模式下拖拽 = 平移
            PanBy(pos.X - _lastDrag.X, pos.Y - _lastDrag.Y);
            DragHint.Visibility = Visibility.Collapsed;
        }
        else
        {
            UpdateDragHint(pos.X - _dragStart.X, pos.Y - _dragStart.Y);
        }
        _lastDrag = pos;
    }

    private void ImageHost_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragActive) return;
        _dragActive = false;
        ImageHost.ReleasePointerCapture(e.Pointer);
        DragHint.Visibility = Visibility.Collapsed;
        FocusSelf();

        if (IsZoomed) return; // 缩放模式下拖拽已作为平移处理

        var pos = e.GetCurrentPoint(ImageHost).Position;
        var action = ClassifyDrag(pos.X - _dragStart.X, pos.Y - _dragStart.Y);
        if (action is not null) _ = PerformAsync(action.Value);
    }

    private void ImageHost_PointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        _dragActive = false;
        DragHint.Visibility = Visibility.Collapsed;
    }

    private SessionAction? ClassifyDrag(double dx, double dy)
    {
        var ax = Math.Abs(dx);
        var ay = Math.Abs(dy);
        if (Math.Max(ax, ay) < DragThreshold) return null;
        if (ax > ay * DragDominance) return dx < 0 ? SessionAction.Left : SessionAction.Right;
        if (ay > ax * DragDominance) return dy < 0 ? SessionAction.Delete : SessionAction.Skip;
        return null; // 对角线，视为无效
    }

    private void UpdateDragHint(double dx, double dy)
    {
        var ax = Math.Abs(dx);
        var ay = Math.Abs(dy);
        if (Math.Max(ax, ay) < 20)
        {
            DragHint.Visibility = Visibility.Collapsed;
            return;
        }

        var s = _session;
        string text;
        if (ax > ay)
            text = dx < 0 ? $"◀ {DirName(s?.LeftDir ?? "")}" : $"{DirName(s?.RightDir ?? "")} ▶";
        else
            text = dy < 0 ? "▲ 删除" : "▼ 跳过";
        DragHintText.Text = text;
        DragHint.Visibility = Visibility.Visible;
    }

    // ---------- 键盘 ----------

    private void Root_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (_session is null) return;

        if (e.Key == VirtualKey.Escape)
        {
            if (IsZoomed)
            {
                e.Handled = true;
                ResetZoom();
            }
            return;
        }

        // 缩放快捷键：主键区 = / -（Shift+= 即 +），小键盘 + / -（固定档位）
        if (e.Key == (VirtualKey)187 || e.Key == (VirtualKey)107)   // '=' 或 小键盘 +
        {
            e.Handled = true;
            ZoomStepByLevel(1);
            return;
        }
        if (e.Key == (VirtualKey)189 || e.Key == (VirtualKey)109)   // '-' 或 小键盘 -
        {
            e.Handled = true;
            ZoomStepByLevel(-1);
            return;
        }

        var ctrlDown = (InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control) & CoreVirtualKeyStates.Down) == CoreVirtualKeyStates.Down;

        SessionAction? action = e.Key switch
        {
            VirtualKey.Left or VirtualKey.A => SessionAction.Left,
            VirtualKey.Right or VirtualKey.D => SessionAction.Right,
            VirtualKey.Up or VirtualKey.W => SessionAction.Delete,
            VirtualKey.Down or VirtualKey.S => SessionAction.Skip,
            _ => null,
        };

        if (action is not null)
        {
            e.Handled = true;
            _ = PerformAsync(action.Value);
            return;
        }

        if (ctrlDown && e.Key == VirtualKey.Z)
        {
            e.Handled = true;
            _ = UndoAsync();
        }
    }

    // ---------- 按钮 ----------

    private void BtnLeft_Click(object sender, RoutedEventArgs e) => _ = PerformAsync(SessionAction.Left);
    private void BtnRight_Click(object sender, RoutedEventArgs e) => _ = PerformAsync(SessionAction.Right);
    private void BtnDelete_Click(object sender, RoutedEventArgs e) => _ = PerformAsync(SessionAction.Delete);
    private void BtnSkip_Click(object sender, RoutedEventArgs e) => _ = PerformAsync(SessionAction.Skip);
    private void BtnUndo_Click(object sender, RoutedEventArgs e) => _ = UndoAsync();
    private void BtnExport_Click(object sender, RoutedEventArgs e) => ExportRequested?.Invoke(this, EventArgs.Empty);
    private void BtnStop_Click(object sender, RoutedEventArgs e) => StopRequested?.Invoke(this, EventArgs.Empty);
}
