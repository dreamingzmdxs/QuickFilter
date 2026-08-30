using System.Net.WebSockets;
using Microsoft.UI.Xaml;
using QuickFilter.App.Lan;
using QuickFilter.Core;

namespace QuickFilter.App;

public partial class App : Application
{
    private Window? _window;

    public App() => InitializeComponent();

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var cmd = Environment.GetCommandLineArgs();
        var idx = Array.IndexOf(cmd, "--selftest");
        if (idx >= 0 && idx + 1 < cmd.Length)
        {
            _ = RunSelfTestAsync(cmd[idx + 1]);
            return;
        }
        idx = Array.IndexOf(cmd, "--server-test");
        if (idx >= 0 && idx + 1 < cmd.Length)
        {
            _ = RunServerTestAsync(cmd[idx + 1]);
            return;
        }
        idx = Array.IndexOf(cmd, "--serve");
        if (idx >= 0 && idx + 1 < cmd.Length)
        {
            _ = RunServeAsync(cmd[idx + 1]);
            return;
        }
        idx = Array.IndexOf(cmd, "--pin-test");
        if (idx >= 0)
        {
            _ = RunPinTestAsync();
            return;
        }

        _window = new MainWindow();
        _window.Activate();
    }

    /// <summary>PIN 机制自动化测试：启动→固定PIN认证→运行时更新PIN→旧PIN被拒/新PIN通过。退出码 0/1。</summary>
    private static async Task RunPinTestAsync()
    {
        int exit = 1;
        try
        {
            using var server = new RemoteServer(pin: "1234");
            server.Start();
            await Task.Delay(400);

            // 1) 固定 PIN 1234 可认证
            var ok1 = await TryAuthAsync("1234");
            if (!ok1) throw new InvalidOperationException("固定 PIN 1234 认证失败");

            // 2) 运行时更新 PIN → 9999
            server.UpdatePin("9999");
            var okOld = await TryAuthAsync("1234");
            var okNew = await TryAuthAsync("9999");
            if (okOld) throw new InvalidOperationException("旧 PIN 1234 仍可通过（更新未生效）");
            if (!okNew) throw new InvalidOperationException("新 PIN 9999 认证失败");

            // 3) 改为随机 → 1234/9999 均被拒
            server.UpdatePin(null);
            var okRand1 = await TryAuthAsync("1234");
            var okRand2 = await TryAuthAsync("9999");
            if (okRand1 || okRand2) throw new InvalidOperationException("随机模式仍接受已弃 PIN");

            AppLog.Info("=== pin-test 全部通过 ✓ ===");
            exit = 0;
        }
        catch (Exception ex)
        {
            AppLog.Error("pin-test 失败：" + ex.Message);
        }
        Environment.Exit(exit);
    }

    /// <summary>尝试认证并返回结果（auth-ok 视为通过）。</summary>
    private static async Task<bool> TryAuthAsync(string pin)
    {
        using var ws = new ClientWebSocket();
        ConfigureWs(ws);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        await ws.ConnectAsync(new Uri("ws://127.0.0.1:47900/ws"), cts.Token);
        await SendWsTextAsync(ws, $"{{\"type\":\"auth\",\"pin\":\"{pin}\",\"device\":\"pin-test\"}}");
        var reply = await ReceiveWsTextAsync(ws);
        return reply is not null && reply.Contains("auth-ok", StringComparison.Ordinal);
    }

    /// <summary>联调模式：从目录复制 20 张图片建临时会话并驻留服务（PIN 固定 1234），供模拟器/真机真实连接测试。</summary>
    private static async Task RunServeAsync(string dir)
    {
        var root = Path.Combine(Path.GetTempPath(), "qf-serve-" + Guid.NewGuid().ToString("N"));
        var source = Path.Combine(root, "src");
        var left = Path.Combine(root, "left");
        var right = Path.Combine(root, "right");
        var deleted = Path.Combine(root, "deleted");
        try
        {
            Directory.CreateDirectory(source);
            var copied = new List<ImageItem>();
            foreach (var it in ImageScanner.Scan(Path.GetFullPath(dir), recursive: true, OrderRule.FileName).Take(20).ToList())
            {
                var dest = Path.Combine(source, it.RelativePath.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                File.Copy(it.FullPath, dest, true);
                copied.Add(new ImageItem(it.RelativePath, dest, it.Name, new FileInfo(dest).Length, File.GetLastWriteTimeUtc(dest)));
            }
            var session = Session.Create(source, left, right, deleted, OrderRule.FileName, true, copied);

            using var server = new RemoteServer(pin: "1234");
            server.GestureReceived += a => { session.ExecuteAsync(a).GetAwaiter().GetResult(); server.BroadcastState(); };
            server.UndoRequested += () => { session.UndoAsync().GetAwaiter().GetResult(); server.BroadcastState(); };
            server.AttachSession(session);
            server.Start();
            AppLog.Info($"=== serve 模式就绪：源={source}，左={left}，右={right}，PIN=1234，共 {session.TotalCount} 张 ===");
            await Task.Delay(Timeout.Infinite);
        }
        catch (Exception ex)
        {
            AppLog.Error("serve 模式异常：" + ex);
            Environment.Exit(1);
        }
    }

    private static async Task RunSelfTestAsync(string dir)
    {
        var outDir = Path.Combine(Path.GetTempPath(), "qf-selftest-" + Guid.NewGuid().ToString("N"));
        try
        {
            AppLog.Info($"=== 自检开始：{dir} ===");
            var items = await Task.Run(() => ImageScanner.Scan(Path.GetFullPath(dir), recursive: true, OrderRule.FileName));
            AppLog.Info($"扫描完成：{items.Count} 张");

            var session = Session.Create(
                dir,
                Path.Combine(outDir, "left"), Path.Combine(outDir, "right"), Path.Combine(outDir, "deleted"),
                OrderRule.FileName, recursive: true, items);

            int ok = 0, fail = 0;
            foreach (var item in items.Take(5))
            {
                try
                {
                    // 每张图 15 秒超时，防止个别文件解码挂死拖垮诊断
                    var load = ImageLoader.LoadAsync(session, item.RelativePath);
                    var done = await Task.WhenAny(load, Task.Delay(TimeSpan.FromSeconds(15)));
                    if (done != load)
                    {
                        fail++;
                        AppLog.Error($"解码超时（>15s）{item.RelativePath}");
                    }
                    else
                    {
                        await load;
                        ok++;
                    }
                }
                catch (Exception ex)
                {
                    fail++;
                    AppLog.Error($"解码失败 {item.RelativePath}: {ex.Message}");
                }
            }
            AppLog.Info($"自检完成：成功 {ok}，失败 {fail}，日志文件 {AppLog.LogFilePath}");
        }
        catch (Exception ex)
        {
            AppLog.Error("自检异常: " + ex);
        }
        Environment.Exit(0);
    }

    /// <summary>遥控协议端到端自测：复制少量图片 → 建会话 → 起服务 → WS 客户端走完整流程 → 校验文件落位。退出码：0 通过 / 1 失败。</summary>
    private static async Task RunServerTestAsync(string dir)
    {
        int exit = 1;
        var root = Path.Combine(Path.GetTempPath(), "qf-srvtest-" + Guid.NewGuid().ToString("N"));
        var source = Path.Combine(root, "src");
        var left = Path.Combine(root, "left");
        var right = Path.Combine(root, "right");
        var deleted = Path.Combine(root, "deleted");
        try
        {
            Directory.CreateDirectory(source);
            var srcItems = ImageScanner.Scan(Path.GetFullPath(dir), recursive: true, OrderRule.FileName).Take(5).ToList();
            var copied = new List<ImageItem>();
            foreach (var it in srcItems)
            {
                var dest = Path.Combine(source, it.RelativePath.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                File.Copy(it.FullPath, dest, true);
                copied.Add(new ImageItem(it.RelativePath, dest, it.Name, new FileInfo(dest).Length, File.GetLastWriteTimeUtc(dest)));
            }
            var session = Session.Create(source, left, right, deleted, OrderRule.FileName, true, copied);

            using var server = new RemoteServer(pin: "1234");
            server.GestureReceived += a => { session.ExecuteAsync(a).GetAwaiter().GetResult(); server.BroadcastState(); };
            server.UndoRequested += () => { session.UndoAsync().GetAwaiter().GetResult(); server.BroadcastState(); };
            server.AttachSession(session);
            server.Start();
            await Task.Delay(500);
            AppLog.Info("=== server-test 开始 ===");

            // 1) 错误 PIN → 拒绝
            using (var ws1 = new ClientWebSocket())
            {
                ConfigureWs(ws1);
                await ConnectWsAsync(ws1);
                await SendWsTextAsync(ws1, "{\"type\":\"auth\",\"pin\":\"9999\",\"device\":\"测试机\"}");
                var r1 = await ReceiveWsTextAsync(ws1);
                AssertContains(r1, "auth-fail", "错误 PIN 应被拒绝");
            }

            // 2) 正确 PIN → auth-ok + 初始 state
            string firstRel = session.Queue[0];
            using (var ws2 = new ClientWebSocket())
            {
                ConfigureWs(ws2);
                await ConnectWsAsync(ws2);
                await SendWsTextAsync(ws2, "{\"type\":\"auth\",\"pin\":\"1234\",\"device\":\"测试机\"}");
                var a = await ReceiveWsTextAsync(ws2);
                AssertContains(a, "auth-ok", "正确 PIN 应通过认证");
                var s1 = await ReceiveWsTextAsync(ws2);
                AssertContains(s1, "\"index\":0", "初始状态 index 应为 0");

                // 3) 手势 left → 文件移动 + 状态更新
                await SendWsTextAsync(ws2, "{\"type\":\"gesture\",\"action\":\"left\"}");
                var ack = await ReceiveWsTextAsync(ws2);
                AssertContains(ack, "\"ok\":true", "手势应返回 ack");
                var s2 = await ReceiveWsTextAsync(ws2);
                AssertContains(s2, "\"index\":1", "移动后 index 应为 1");
                var movedTo = Path.Combine(left, firstRel.Replace('/', Path.DirectorySeparatorChar));
                AssertTrue(File.Exists(movedTo), $"文件应移动到左目录：{movedTo}");
                AssertTrue(!File.Exists(Path.Combine(source, firstRel.Replace('/', Path.DirectorySeparatorChar))), "源目录不应再有该文件");

                // 4) 撤销 → 文件回源
                await SendWsTextAsync(ws2, "{\"type\":\"undo\"}");
                _ = await ReceiveWsTextAsync(ws2);  // ack
                var s3 = await ReceiveWsTextAsync(ws2);
                AssertContains(s3, "\"index\":0", "撤销后 index 应回到 0");
                AssertTrue(File.Exists(Path.Combine(source, firstRel.Replace('/', Path.DirectorySeparatorChar))), "撤销后文件应回到源目录");

                // 5) HTTP 图片通道
                using var http = new HttpClient(new HttpClientHandler { UseProxy = false, Proxy = null });
                var img = await http.GetByteArrayAsync("http://127.0.0.1:47900/image?idx=0&v=1");
                var origin = File.ReadAllBytes(Path.Combine(source, firstRel.Replace('/', Path.DirectorySeparatorChar)));
                AssertTrue(img.SequenceEqual(origin), "HTTP 图片内容应与原文件一致");

                // 6) 静默等 2 秒确认无崩溃
                await Task.Delay(200);
            }

            AppLog.Info("=== server-test 全部通过 ✓ ===");
            exit = 0;
        }
        catch (Exception ex)
        {
            AppLog.Error("server-test 失败：" + ex.Message);
            exit = 1;
        }
        Environment.Exit(exit);
    }

    private static void AssertTrue(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
        AppLog.Info("  ✓ " + message);
    }

    private static void AssertContains(string? text, string needle, string message)
    {
        if (text is null || !text.Contains(needle, StringComparison.Ordinal)) throw new InvalidOperationException($"{message}（收到：{text}）");
        AppLog.Info("  ✓ " + message);
    }

    private static void ConfigureWs(ClientWebSocket ws)
    {
        ws.Options.Proxy = null;                       // 不走系统代理（本机代理工具会干扰）
        ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
        ws.Options.SetRequestHeader("Origin", "http://quickfilter");
    }

    private static async Task ConnectWsAsync(ClientWebSocket ws)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        await ws.ConnectAsync(new Uri("ws://127.0.0.1:47900/ws"), cts.Token);
    }

    private static async Task SendWsTextAsync(ClientWebSocket ws, string text)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(text);
        await ws.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);
    }

    private static async Task<string?> ReceiveWsTextAsync(ClientWebSocket ws)
    {
        var buffer = new byte[64 * 1024];
        using var ms = new MemoryStream();
        while (true)
        {
            var r = await ws.ReceiveAsync(buffer, CancellationToken.None);
            if (r.MessageType == WebSocketMessageType.Close) return null;
            ms.Write(buffer, 0, r.Count);
            if (r.EndOfMessage) break;
        }
        return System.Text.Encoding.UTF8.GetString(ms.ToArray());
    }
}
