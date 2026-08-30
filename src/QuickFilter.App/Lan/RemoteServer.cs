using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using QuickFilter.Core;

namespace QuickFilter.App.Lan;

/// <summary>
/// 局域网遥控服务（M3）：
/// - UDP 47800：发现广播（beacon，每 3 秒）并响应 discover-query
/// - TCP 47900：同一端口处理 WebSocket 命令通道（/ws）与 HTTP 图片通道（/image）
/// - PIN 配对；单设备连接（新设备自动替换旧设备）
/// </summary>
public sealed class RemoteServer : IDisposable
{
    public const int Port = 47900;
    public const int DiscoveryPort = 47800;
    private const string WsGuid = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";

    private string _pin;
    private readonly object _gate = new();
    private TcpListener? _tcp;
    private UdpClient? _udp;
    private CancellationTokenSource? _cts;
    private Session? _session;
    private ClientConnection? _controller;

    public string Pin => _pin;
    public string MachineName { get; } = Environment.MachineName;

    /// <summary>状态变化（未连接 / 已连接设备等），在后台线程触发。</summary>
    public event Action<string>? StatusChanged;
    /// <summary>收到遥控手势（后台线程触发）。</summary>
    public event Action<SessionAction>? GestureReceived;
    /// <summary>收到撤销指令（后台线程触发）。</summary>
    public event Action? UndoRequested;

    public RemoteServer(string? pin = null)
    {
        _pin = pin ?? Random.Shared.Next(1000, 10000).ToString();
    }

    /// <summary>更新 PIN（固定 PIN 设置保存后即时生效，无需重启程序）。</summary>
    public void UpdatePin(string? fixedPin)
    {
        lock (_gate)
        {
            _pin = string.IsNullOrWhiteSpace(fixedPin)
                ? Random.Shared.Next(1000, 10000).ToString()
                : fixedPin.Trim();
        }
        RaiseStatus();
        AppLog.Info($"遥控 PIN 已更新为 {_pin}");
    }

    public void Start()
    {
        if (_tcp is not null) return;
        _cts = new CancellationTokenSource();
        _tcp = new TcpListener(IPAddress.Any, Port);
        _tcp.Start();
        _udp = new UdpClient(new IPEndPoint(IPAddress.Any, DiscoveryPort)) { EnableBroadcast = true };

        _ = AcceptLoopAsync(_cts.Token);
        _ = DiscoveryReceiveLoopAsync(_cts.Token);
        _ = BeaconLoopAsync(_cts.Token);
        _ = StatePumpAsync(_cts.Token);
        RaiseStatus();
        AppLog.Info($"遥控服务启动：{string.Join(", ", GetIPv4Addresses())}:{Port}（{Port} WS/HTTP，{DiscoveryPort} UDP）PIN={_pin}");
    }

    /// <summary>每 5 秒向已连接设备推送一次状态（兼作心跳，保持中间网络设备/客户端不掉线）。</summary>
    private async Task StatePumpAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                ClientConnection? c;
                lock (_gate) c = _controller;
                if (c is not null) BroadcastState();
            }
        }
        catch (OperationCanceledException) { }
    }

    public void Stop()
    {
        _cts?.Cancel();
        _tcp?.Stop();
        _udp?.Dispose();
        ClientConnection? c;
        lock (_gate) { c = _controller; _controller = null; }
        c?.Dispose();
        AppLog.Info("遥控服务已停止");
    }

    public void Dispose() => Stop();

    /// <summary>绑定当前筛选会话（图片通道与状态广播的数据来源）。</summary>
    public void AttachSession(Session? session)
    {
        lock (_gate) _session = session;
        BroadcastState();
    }

    /// <summary>向已连接设备推送最新状态（任意线程可调用）。</summary>
    public void BroadcastState()
    {
        ClientConnection? c;
        Session? s;
        lock (_gate) { c = _controller; s = _session; }
        if (c is null) return;
        _ = c.SendTextAsync(BuildStateJson(s));
    }

    // ---------- 状态与地址 ----------

    private static List<string> GetIPv4Addresses()
    {
        var list = new List<string>();
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up) continue;
                if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                foreach (var addr in nic.GetIPProperties().UnicastAddresses)
                {
                    if (addr.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    if (addr.Address.GetAddressBytes()[0] == 169) continue; // APIPA
                    list.Add(addr.Address.ToString());
                }
            }
        }
        catch { }
        return list;
    }

    private void RaiseStatus()
    {
        string device;
        lock (_gate) device = _controller?.DeviceName ?? "";
        var ips = string.Join(", ", GetIPv4Addresses());
        var text = device.Length > 0
            ? $"📱 遥控已连接：{device}"
            : $"📱 遥控待连接：{ips} : {Port} · PIN {_pin}";
        StatusChanged?.Invoke(text);
    }

    private string BuildStateJson(Session? s)
    {
        if (s is null)
            return "{\"type\":\"state\",\"done\":true,\"index\":-1,\"total\":0,\"filename\":\"\",\"counts\":{\"left\":0,\"right\":0,\"delete\":0,\"skip\":0},\"image\":null}";
        var rel = s.CurrentRelativePath;
        var idx = rel is null ? -1 : s.PositionOf(rel) - 1;
        var obj = new
        {
            type = "state",
            index = idx,
            total = s.TotalCount,
            filename = rel is null ? "" : Path.GetFileName(rel.Replace('/', Path.DirectorySeparatorChar)),
            counts = new { left = s.LeftCount, right = s.RightCount, delete = s.DeleteCount, skip = s.SkipCount },
            image = idx >= 0 ? $"/image?idx={idx}&v={s.ProcessedCount}" : (string?)null,
            done = s.RemainingCount == 0,
        };
        return JsonSerializer.Serialize(obj);
    }

    // ---------- 监听循环 ----------

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _tcp!.AcceptTcpClientAsync(ct); }
            catch { break; }
            _ = Task.Run(() => HandleClientAsync(client, ct));
        }
    }

    private async Task DiscoveryReceiveLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var r = await _udp!.ReceiveAsync(ct);
                var text = Encoding.UTF8.GetString(r.Buffer);
                if (text.Contains("discover-query", StringComparison.OrdinalIgnoreCase))
                {
                    var beacon = Encoding.UTF8.GetBytes(BeaconJson());
                    _udp.Send(beacon, beacon.Length, r.RemoteEndPoint);
                    AppLog.Info($"收到发现查询，已回复 {r.RemoteEndPoint}");
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { }
        }
    }

    private async Task BeaconLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(3));
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                try
                {
                    var bytes = Encoding.UTF8.GetBytes(BeaconJson());
                    foreach (var ip in GetIPv4Addresses())
                    {
                        // 255.255.255.255 与各接口子网广播
                        _udp!.Send(bytes, bytes.Length, new IPEndPoint(IPAddress.Broadcast, DiscoveryPort));
                        _udp.Send(bytes, bytes.Length, new IPEndPoint(IPAddress.Parse(ip), DiscoveryPort));
                    }
                }
                catch { }
            }
        }
        catch (OperationCanceledException) { }
    }

    private string BeaconJson() => JsonSerializer.Serialize(new
    {
        type = "discover",
        app = "QuickFilter",
        port = Port,
        machine = MachineName,
    });

    // ---------- 客户端连接 ----------

    private async Task HandleClientAsync(TcpClient tcp, CancellationToken ct)
    {
        try
        {
            using (tcp)
            {
                var stream = tcp.GetStream();
                var req = await ReadHttpRequestAsync(stream, ct);
                if (req is null) return;

                if (req.Method == "GET" && req.Path.StartsWith("/image", StringComparison.OrdinalIgnoreCase))
                {
                    await ServeImageAsync(stream, req.Path, ct);
                    return;
                }
                if (req.Method == "GET" && req.Path == "/ws" && req.IsWebSocketUpgrade)
                {
                    await HandleWebSocketAsync(stream, req, ct);
                    return;
                }
                await WriteHttpAsync(stream, 404, "Not Found", "text/plain; charset=utf-8", "not found", ct);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            AppLog.Info($"客户端连接异常：{ex.Message}");
        }
    }

    private async Task HandleWebSocketAsync(NetworkStream stream, HttpRequest req, CancellationToken ct)
    {
        var accept = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(req.WebSocketKey + WsGuid)));
        await WriteRawAsync(stream,
            $"HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: {accept}\r\n\r\n", ct);

        var conn = new ClientConnection(stream);

        // ----- 认证 -----
        var first = await conn.ReadTextAsync(ct);
        if (first is null) { conn.Dispose(); return; }
        SessionAction? firstAction = null;
        try
        {
            var node = JsonNode.Parse(first)?.AsObject();
            var type = node?["type"]?.GetValue<string>();
            if (type != "auth")
            {
                await conn.SendTextAsync("{\"type\":\"auth-fail\",\"error\":\"请先发送认证消息\"}");
                conn.Dispose();
                return;
            }
            var pin = node!["pin"]?.GetValue<string>();
            if (pin != _pin)
            {
                AppLog.Info($"认证失败：收到 PIN={pin}，期望 PIN={_pin}（来自 {conn.DeviceName}）");
                await conn.SendTextAsync("{\"type\":\"auth-fail\",\"error\":\"PIN 不正确\"}");
                conn.Dispose();
                return;
            }
            conn.DeviceName = node["device"]?.GetValue<string>() ?? "未知设备";

            // 单设备：替换旧连接
            ClientConnection? old;
            lock (_gate) { old = _controller; _controller = conn; }
            old?.Dispose();

            await conn.SendTextAsync("{\"type\":\"auth-ok\"}");
            BroadcastState();
            RaiseStatus();
            AppLog.Info($"遥控设备已连接：{conn.DeviceName}");
        }
        catch (Exception ex)
        {
            AppLog.Info($"认证异常：{ex.Message}");
            conn.Dispose();
            return;
        }

        // ----- 消息循环 -----
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var text = await conn.ReadTextAsync(ct);
                if (text is null) break;
                try
                {
                    var m = JsonNode.Parse(text)?.AsObject();
                    switch (m?["type"]?.GetValue<string>())
                    {
                        case "gesture":
                            var action = m["action"]?.GetValue<string>() switch
                            {
                                "left" => SessionAction.Left,
                                "right" => SessionAction.Right,
                                "delete" => SessionAction.Delete,
                                "skip" => SessionAction.Skip,
                                _ => (SessionAction?)null,
                            };
                            if (action is null)
                            {
                                await conn.SendTextAsync("{\"type\":\"ack\",\"ok\":false,\"error\":\"未知操作\"}");
                                break;
                            }
                            await conn.SendTextAsync($"{{\"type\":\"ack\",\"action\":\"{m["action"]}\",\"ok\":true}}");
                            GestureReceived?.Invoke(action.Value);
                            break;
                        case "undo":
                            await conn.SendTextAsync("{\"type\":\"ack\",\"action\":\"undo\",\"ok\":true}");
                            UndoRequested?.Invoke();
                            break;
                        case "ping":
                            await conn.SendTextAsync("{\"type\":\"pong\"}");
                            break;
                        default:
                            break;
                    }
                }
                catch (JsonException) { }
            }
        }
        catch (Exception ex)
        {
            AppLog.Info($"消息循环异常：{ex.Message}");
        }
        finally
        {
            lock (_gate) if (_controller == conn) { _controller = null; RaiseStatus(); AppLog.Info($"遥控设备已断开：{conn.DeviceName}"); }
            conn.Dispose();
        }
    }

    // ---------- HTTP 图片通道 ----------

    private async Task ServeImageAsync(NetworkStream stream, string path, CancellationToken ct)
    {
        var qs = path.Split('?', 2);
        var query = qs.Length > 1 ? ParseQuery(qs[1]) : new Dictionary<string, string>();

        if (!int.TryParse(query.GetValueOrDefault("idx"), out var idx) || idx < 0)
        {
            await WriteHttpAsync(stream, 400, "Bad Request", "text/plain; charset=utf-8", "idx 参数无效", ct);
            return;
        }
        Session? s;
        lock (_gate) s = _session;
        if (s is null || idx >= s.TotalCount)
        {
            await WriteHttpAsync(stream, 404, "Not Found", "text/plain; charset=utf-8", "无会话或索引越界", ct);
            return;
        }
        var rel = s.Queue[idx];
        var full = Path.Combine(s.SourceDir, rel.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(full))
        {
            await WriteHttpAsync(stream, 404, "Not Found", "text/plain; charset=utf-8", "文件不存在", ct);
            return;
        }

        var bytes = await File.ReadAllBytesAsync(full, ct);
        var head = $"HTTP/1.1 200 OK\r\nContent-Type: {ContentTypeFor(full)}\r\nContent-Length: {bytes.Length}\r\nCache-Control: no-store\r\nConnection: close\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(head), ct);
        await stream.WriteAsync(bytes, ct);
        await stream.FlushAsync(ct);
        AppLog.Info($"图片通道：idx={idx} {Path.GetFileName(full)}（{bytes.Length / 1024} KB）");
    }

    private static string ContentTypeFor(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".jpg" or ".jpeg" => "image/jpeg",
        ".png" => "image/png",
        ".gif" => "image/gif",
        ".webp" => "image/webp",
        ".bmp" => "image/bmp",
        ".heic" => "image/heic",
        _ => "application/octet-stream",
    };

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = pair.Split('=', 2);
            if (kv.Length == 2) dict[kv[0]] = Uri.UnescapeDataString(kv[1]);
        }
        return dict;
    }

    private static async Task WriteHttpAsync(NetworkStream stream, int status, string reason, string contentType, string body, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        var head = $"HTTP/1.1 {status} {reason}\r\nContent-Type: {contentType}\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(head), ct);
        await stream.WriteAsync(bytes, ct);
        await stream.FlushAsync(ct);
    }

    private static async Task WriteRawAsync(NetworkStream stream, string text, CancellationToken ct)
    {
        var bytes = Encoding.ASCII.GetBytes(text);
        await stream.WriteAsync(bytes, ct);
        await stream.FlushAsync(ct);
    }

    // ---------- HTTP 请求头解析 ----------

    private sealed record HttpRequest(string Method, string Path, Dictionary<string, string> Headers)
    {
        public bool IsWebSocketUpgrade =>
            Headers.TryGetValue("upgrade", out var u) && u.Contains("websocket", StringComparison.OrdinalIgnoreCase);
        public string WebSocketKey => Headers.TryGetValue("sec-websocket-key", out var k) ? k.Trim() : "";
    }

    private static async Task<HttpRequest?> ReadHttpRequestAsync(NetworkStream stream, CancellationToken ct)
    {
        var sb = new StringBuilder();
        var buf = new byte[1024];
        int total = 0;
        while (total < 16 * 1024)
        {
            int n;
            try
            {
                n = await stream.ReadAsync(buf.AsMemory(0, buf.Length), ct);
            }
            catch (Exception) { return null; }
            if (n <= 0) return null;
            total += n;
            sb.Append(Encoding.ASCII.GetString(buf, 0, n));
            if (sb.ToString().Contains("\r\n\r\n", StringComparison.Ordinal)) break;
        }
        var raw = sb.ToString();
        var lines = raw.Split("\r\n", StringSplitOptions.None);
        var requestLine = lines[0].Split(' ');
        if (requestLine.Length < 2) return null;
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 1; i < lines.Length; i++)
        {
            var idx = lines[i].IndexOf(':');
            if (idx <= 0) continue;
            headers[lines[i][..idx].Trim()] = lines[i][(idx + 1)..].Trim();
        }
        return new HttpRequest(requestLine[0], requestLine[1], headers);
    }

    // ---------- WebSocket 帧 ----------

    private sealed class ClientConnection : IDisposable
    {
        private readonly NetworkStream _stream;
        private readonly SemaphoreSlim _sendLock = new(1, 1);
        public string DeviceName { get; set; } = "";

        public ClientConnection(NetworkStream stream) => _stream = stream;

        public async Task<string?> ReadTextAsync(CancellationToken ct)
        {
            try
            {
                while (true)
                {
                    var h = await ReadExactAsync(_stream, 2, ct);
                    if (h is null) return null;
                    var opcode = h[0] & 0x0F;
                    var masked = (h[1] & 0x80) != 0;
                    var len = (long)(h[1] & 0x7F);
                    if (len == 126)
                    {
                        var b = await ReadExactAsync(_stream, 2, ct);
                        if (b is null) return null;
                        len = (b[0] << 8) | b[1];
                    }
                    else if (len == 127)
                    {
                        var b = await ReadExactAsync(_stream, 8, ct);
                        if (b is null) return null;
                        len = BitConverter.ToInt64(b, 0);
                    }
                    if (len < 0 || len > 8 * 1024 * 1024) return null;

                    byte[]? mask = null;
                    if (masked)
                    {
                        mask = await ReadExactAsync(_stream, 4, ct);
                        if (mask is null) return null;
                    }
                    var payload = await ReadExactAsync(_stream, (int)len, ct);
                    if (payload is null) return null;
                    if (mask is not null)
                        for (int i = 0; i < payload.Length; i++) payload[i] ^= mask[i & 3];

                    if (opcode == 0x9)
                    {
                        // ping → pong，继续读下一帧（保持长连接）
                        AppLog.Info($"收到 ping（{payload.Length} B），回 pong");
                        await SendFrameAsync(payload, 0xA);
                        continue;
                    }
                    return opcode switch
                    {
                        0x1 => Encoding.UTF8.GetString(payload),   // text
                        0x8 => null,                                // close
                        _ => null,
                    };
                }
            }
            catch (Exception) { return null; }
        }

        public async Task SendTextAsync(string text)
        {
            AppLog.Info($"→发送帧（{text.Length} B）：{text[..Math.Min(48, text.Length)]}…");
            await SendFrameAsync(Encoding.UTF8.GetBytes(text), 0x1);
        }

        private async Task<string?> SendFrameAsync(byte[] payload, byte opcode)
        {
            await _sendLock.WaitAsync();
            try
            {
                await _stream.WriteAsync(new byte[] { (byte)(0x80 | opcode) });
                if (payload.Length < 126)
                {
                    await _stream.WriteAsync(new byte[] { (byte)payload.Length });
                }
                else if (payload.Length <= ushort.MaxValue)
                {
                    await _stream.WriteAsync(new byte[] { 126, (byte)(payload.Length >> 8), (byte)(payload.Length & 0xFF) });
                }
                else
                {
                    var lenBytes = BitConverter.GetBytes((long)payload.Length);
                    if (BitConverter.IsLittleEndian) Array.Reverse(lenBytes);
                    await _stream.WriteAsync(new byte[] { 127 });
                    await _stream.WriteAsync(lenBytes);
                }
                await _stream.WriteAsync(payload);
                await _stream.FlushAsync();
            }
            catch
            {
                // 发送失败（对端已断开等）静默忽略
            }
            finally { _sendLock.Release(); }
            return null;
        }

        public void Dispose()
        {
            try
            {
                _stream.Dispose();
            }
            catch { }
        }

        private static async Task<byte[]?> ReadExactAsync(NetworkStream stream, int count, CancellationToken ct)
        {
            var buf = new byte[count];
            int read = 0;
            while (read < count)
            {
                int n;
                try { n = await stream.ReadAsync(buf.AsMemory(read, count - read), ct); }
                catch { return null; }
                if (n <= 0) return null;
                read += n;
            }
            return buf;
        }
    }
}
