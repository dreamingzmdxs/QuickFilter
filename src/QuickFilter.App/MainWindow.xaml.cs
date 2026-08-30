using System.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using QuickFilter.App.Lan;
using QuickFilter.App.Views;
using QuickFilter.Core;
using Windows.Storage;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace QuickFilter.App;

public sealed partial class MainWindow : Window
{
    private Session? _session;
    private readonly RemoteServer _remoteServer;
    private readonly AppSettings.Data _settings = AppSettings.Load();

    public MainWindow()
    {
        InitializeComponent();
        Title = "图片快速筛选";

        SetupView.BrowseRequested += OnBrowseRequested;
        SetupView.StartRequested += OnStartRequested;
        SetupView.SettingsSaved += OnSettingsSaved;
        SetupView.ResumeRequested += OnResumeRequested;
        ScreeningView.ExportRequested += OnExportRequested;
        ScreeningView.StopRequested += OnStopRequested;
        ScreeningView.SessionCompleted += OnSessionCompleted;
        ScreeningView.StateChanged += (_, _) => _remoteServer.BroadcastState();

        // 回填设置 + 隐私模式即时生效
        SetupView.SetSettings(_settings.FixedPin, _settings.PrivacyMode);
        ScreeningView.SetPrivacyMode(_settings.PrivacyMode);
        AppLog.Info($"设置：固定PIN={(string.IsNullOrWhiteSpace(_settings.FixedPin) ? "（随机）" : _settings.FixedPin)}，隐私模式={_settings.PrivacyMode}");

        // "继续上次会话"入口：上次目录存在状态文件时展示
        var lastDir = _settings.LastSourceDir;
        if (!string.IsNullOrWhiteSpace(lastDir) && Directory.Exists(lastDir) && Session.HasStateFile(lastDir))
        {
            var resumed = SessionStore.TryLoad(lastDir, out var resumeError);
            if (resumed is not null)
            {
                SetupView.SetDirectory(BrowseSlot.Source, lastDir);
                SetupView.SetResumeInfo(lastDir, resumed.ProcessedCount, resumed.TotalCount);
                AppLog.Info($"继续入口已就绪：{lastDir}（{resumed.ProcessedCount}/{resumed.TotalCount}）");
            }
            else
            {
                AppLog.Info($"继续入口不可用：{lastDir} 原因：{resumeError}");
            }
        }
        else
        {
            AppLog.Info($"继续入口不可用：lastDir={(string.IsNullOrWhiteSpace(lastDir) ? "（空）" : lastDir)}");
        }

        // 局域网遥控服务（M3）：启动时应用固定 PIN（若有）
        _remoteServer = new RemoteServer(string.IsNullOrWhiteSpace(_settings.FixedPin) ? null : _settings.FixedPin);
        _remoteServer.StatusChanged += s => DispatcherQueue.TryEnqueue(() =>
        {
            SetupView.SetServerStatus(s);
            ScreeningView.SetServerStatus(s);
        });
        _remoteServer.GestureReceived += action => DispatcherQueue.TryEnqueue(async () =>
        {
            try { await ScreeningView.PerformRemoteAsync(action); }
            catch (Exception ex) { AppLog.Error($"遥控手势执行失败：{ex.Message}"); }
        });
        _remoteServer.UndoRequested += () => DispatcherQueue.TryEnqueue(async () =>
        {
            try { await ScreeningView.UndoRemoteAsync(); }
            catch (Exception ex) { AppLog.Error($"遥控撤销执行失败：{ex.Message}"); }
        });
        _remoteServer.Start();

        Closed += (_, _) => _remoteServer.Dispose();
    }

    /// <summary>点击"继续上次会话"：直接恢复上次源目录的会话。</summary>
    private async void OnResumeRequested(object? sender, EventArgs e)
    {
        var dir = _settings.LastSourceDir;
        if (string.IsNullOrWhiteSpace(dir) || !Session.HasStateFile(dir))
        {
            await ShowInfoAsync("无法继续", "上次会话文件不存在或已被移动（可重新开始新会话）。");
            return;
        }
        var session = SessionStore.TryLoad(dir, out var loadError);
        if (session is null)
        {
            await ShowInfoAsync("无法继续", loadError ?? "会话文件损坏，将开始新会话（旧文件已备份）。");
            SessionStore.BackupIfExists(dir);
            return;
        }
        EnterScreening(session);
    }

    /// <summary>保存设置：固定 PIN 即时生效，隐私模式即时切换。</summary>
    private void OnSettingsSaved(object? sender, EventArgs e)
    {
        var pin = SetupView.FixedPinText.Trim();
        _settings.FixedPin = pin;
        _settings.PrivacyMode = SetupView.PrivacyEnabled;
        AppSettings.Save(_settings);

        _remoteServer.UpdatePin(string.IsNullOrWhiteSpace(pin) ? null : pin);
        ScreeningView.SetPrivacyMode(_settings.PrivacyMode);
        SetupView.ShowSettingsSaved($"已保存：固定 PIN = {(pin.Length > 0 ? pin : "（随机，每次启动变化）")} · 隐私模式 {(_settings.PrivacyMode ? "开" : "关")} · 手机端请使用该 PIN 连接");
    }

    // ---------- 目录选择 ----------

    private async void OnBrowseRequested(object? sender, BrowseSlot slot)
    {
        var picker = new FolderPicker();
        picker.FileTypeFilter.Add("*");
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
        var folder = await picker.PickSingleFolderAsync();
        if (folder is not null) SetupView.SetDirectory(slot, folder.Path);
    }

    // ---------- 开始会话 ----------

    private async void OnStartRequested(object? sender, SetupRequest req)
    {
        try
        {
            var source = Path.GetFullPath(req.SourceDir);
            var left = Path.GetFullPath(req.LeftDir);
            var right = Path.GetFullPath(req.RightDir);
            var deleted = string.IsNullOrWhiteSpace(req.DeletedDir)
                ? Path.Combine(source, "已删除")
                : Path.GetFullPath(req.DeletedDir);

            SetupView.SetStatus("正在检查目录…");
            AppLog.Info($"开始会话：source={source} | left={left} | right={right} | deleted={deleted} | 排序={req.OrderRule} | 递归={req.Recursive}");

            if (!Directory.Exists(source))
            {
                await ShowInfoAsync("源目录不存在", $"请检查路径：\n{source}");
                SetupView.SetStatus("");
                return;
            }

            var err = ValidateDirs(source, left, right, deleted);
            if (err is not null)
            {
                await ShowInfoAsync("目录配置无效", err);
                SetupView.SetStatus("");
                return;
            }

            Directory.CreateDirectory(left);
            Directory.CreateDirectory(right);
            Directory.CreateDirectory(deleted);

            // 续传检查：源目录存在未完成会话时询问
            if (Session.HasStateFile(source))
            {
                var choice = await AskResumeAsync(source);
                if (choice == ResumeChoice.Cancel) { SetupView.SetStatus(""); return; }
                if (choice == ResumeChoice.New) { SessionStore.BackupIfExists(source); }
                else
                {
                    var loaded = SessionStore.TryLoad(source, out var loadError);
                    if (loaded is not null)
                    {
                        EnterScreening(loaded);
                        return;
                    }
                    await ShowInfoAsync("恢复会话失败", $"{loadError}\n将备份旧会话文件并新建会话。");
                    SessionStore.BackupIfExists(source);
                }
            }

            SetupView.SetStatus("正在扫描图片…");
            var items = await Task.Run(() => ImageScanner.Scan(source, req.Recursive, req.OrderRule, new[] { deleted }));
            AppLog.Info($"扫描完成：{items.Count} 张");
            if (items.Count == 0)
            {
                SetupView.SetStatus("");
                await ShowInfoAsync("没有可筛选的图片", "源目录中未找到支持的图片（jpg/png/webp/bmp/gif/heic）。");
                return;
            }

            var session = Session.Create(source, left, right, deleted, req.OrderRule, req.Recursive, items,
                previewWidth: req.FastPreview ? 960 : 1920, playGif: req.PlayGif);
            await SessionStore.SaveAsync(session);
            SetupView.SetStatus("");
            EnterScreening(session);
        }
        catch (Exception ex)
        {
            SetupView.SetStatus("");
            await ShowInfoAsync("启动会话失败", ex.Message);
        }
    }

    /// <summary>目录校验：目标不得与源目录相同/为其父目录/位于源目录内部（已删除目录除外）。</summary>
    private static string? ValidateDirs(string source, string left, string right, string deleted)
    {
        if (string.Equals(left, right, StringComparison.OrdinalIgnoreCase))
            return "左目标目录与右目标目录不能相同。";

        static bool SameOrContains(string child, string parent)
        {
            var c = Path.TrimEndingDirectorySeparator(Path.GetFullPath(child));
            var p = Path.TrimEndingDirectorySeparator(Path.GetFullPath(parent));
            return c.Equals(p, StringComparison.OrdinalIgnoreCase)
                || c.StartsWith(p + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }

        // 防止“目标目录是源目录内部”导致重复递归；已删除目录默认在源目录内，扫描时已排除
        if (SameOrContains(left, source)) return "左目标目录不能与源目录相同或位于其内部。";
        if (SameOrContains(right, source)) return "右目标目录不能与源目录相同或位于其内部。";
        if (SameOrContains(deleted, left) || SameOrContains(left, deleted))
            return "已删除目录与左目标目录不能相互嵌套。";
        if (SameOrContains(deleted, right) || SameOrContains(right, deleted))
            return "已删除目录与右目标目录不能相互嵌套。";
        return null;
    }

    private void EnterScreening(Session session)
    {
        AppLog.Info($"进入筛选页：共 {session.TotalCount} 张，已处理 {session.ProcessedCount} 张");
        _session = session;
        _settings.LastSourceDir = session.SourceDir;   // 供下次"继续上次会话"入口
        AppSettings.Save(_settings);
        _remoteServer.AttachSession(session);
        SetupView.Visibility = Visibility.Collapsed;
        ScreeningView.Visibility = Visibility.Visible;
        ScreeningView.StartSession(session);
    }

    // ---------- 导出 CSV ----------

    private async void OnExportRequested(object? sender, EventArgs e)
    {
        if (_session is null) return;
        var path = await ExportCsvAsync(_session);
        if (path is not null)
            ScreeningView.ShowMessage($"日志已导出：{path}", InfoBarSeverity.Success);
    }

    /// <summary>选择保存位置并写出 CSV；成功返回路径，取消/失败返回 null。</summary>
    private async Task<string?> ExportCsvAsync(Session session)
    {
        var file = await PickCsvFileAsync();
        if (file is null) return null;
        try
        {
            var csv = LogExporter.BuildCsv(session.State.Logs);
            await File.WriteAllTextAsync(file.Path, csv, new UTF8Encoding(true));
            return file.Path;
        }
        catch (Exception ex)
        {
            ScreeningView.ShowMessage("导出失败：" + ex.Message, InfoBarSeverity.Error);
            return null;
        }
    }

    private async Task<StorageFile?> PickCsvFileAsync()
    {
        var picker = new FileSavePicker();
        picker.FileTypeChoices.Add("CSV 文件", new List<string> { ".csv" });
        picker.SuggestedFileName = $"筛选日志_{DateTime.Now:yyyyMMdd-HHmmss}";
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
        return await picker.PickSaveFileAsync();
    }

    // ---------- 结束会话 ----------

    private async void OnStopRequested(object? sender, EventArgs e)
    {
        if (_session is null) return;
        var session = _session;

        if (session.RemainingCount > 0)
        {
            var ok = await AskConfirmAsync("结束会话",
                $"还有 {session.RemainingCount} 张未处理（已处理 {session.ProcessedCount} 张）。\n确定结束并返回配置页？（进度已保存，可稍后继续）");
            if (!ok) return;

            var dialog = new ContentDialog
            {
                Title = "会话结束",
                Content = BuildSummaryText(session),
                PrimaryButtonText = "导出 CSV 并结束",
                CloseButtonText = "仅结束",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = RootGrid.XamlRoot,
            };
            if (await dialog.ShowAsync() == ContentDialogResult.Primary)
                await ExportCsvAsync(session);
        }

        FinishSession(session, prefillTargets: true);
    }

    // ---------- 全部处理完成自动提示 ----------

    private async void OnSessionCompleted(object? sender, EventArgs e)
    {
        if (_session is null) return;
        var session = _session;

        var dialog = new ContentDialog
        {
            Title = "全部处理完成 ✓",
            Content = BuildSummaryText(session) + "\n\n选择新目录可继续筛选（目标目录已为您保留），或直接结束。",
            PrimaryButtonText = "选择新目录继续",
            SecondaryButtonText = "导出 CSV 并结束",
            CloseButtonText = "结束",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = RootGrid.XamlRoot,
        };
        var result = await dialog.ShowAsync();

        if (result == ContentDialogResult.Secondary)
            await ExportCsvAsync(session);

        FinishSession(session, prefillTargets: true);
    }

    private static string BuildSummaryText(Session session) =>
        $"共 {session.TotalCount} 张\n左目录 {session.LeftCount} · 右目录 {session.RightCount} · 删除 {session.DeleteCount} · 跳过 {session.SkipCount}";

    /// <summary>结束会话并返回配置页；prefillTargets 时保留本次的左右/已删除目录。</summary>
    private void FinishSession(Session session, bool prefillTargets)
    {
        if (prefillTargets)
        {
            SetupView.SetDirectory(BrowseSlot.Source, "");
            SetupView.SetDirectory(BrowseSlot.Left, session.LeftDir);
            SetupView.SetDirectory(BrowseSlot.Right, session.RightDir);
            SetupView.SetDirectory(BrowseSlot.Deleted, session.DeletedDir == Path.Combine(session.SourceDir, "已删除") ? "" : session.DeletedDir);
        }

        _session = null;
        _remoteServer.AttachSession(null);
        ScreeningView.ResetView();
        ScreeningView.Visibility = Visibility.Collapsed;
        SetupView.Visibility = Visibility.Visible;
        AppLog.Info("会话已结束，返回配置页");
    }

    // ---------- 对话框 ----------

    private enum ResumeChoice { Cancel, Continue, New }

    private async Task<ResumeChoice> AskResumeAsync(string source)
    {
        var existing = SessionStore.TryLoad(source, out _);
        var content = existing is null
            ? "源目录中存在未完成的会话文件，但无法读取。\n建议新建会话（旧会话文件会被备份）。"
            : $"上次会话尚未完成：已处理 {existing.ProcessedCount} / {existing.TotalCount} 张。\n是否继续？";
        var dialog = new ContentDialog
        {
            Title = "发现未完成的会话",
            Content = content,
            PrimaryButtonText = "继续上次会话",
            SecondaryButtonText = "新建会话",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = RootGrid.XamlRoot,
        };
        var result = await dialog.ShowAsync();
        return result switch
        {
            ContentDialogResult.Primary => ResumeChoice.Continue,
            ContentDialogResult.Secondary => ResumeChoice.New,
            _ => ResumeChoice.Cancel,
        };
    }

    private async Task ShowInfoAsync(string title, string message)
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = message,
            CloseButtonText = "确定",
            XamlRoot = RootGrid.XamlRoot,
        };
        await dialog.ShowAsync();
    }

    private async Task<bool> AskConfirmAsync(string title, string message)
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = message,
            PrimaryButtonText = "确定",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = RootGrid.XamlRoot,
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }
}
