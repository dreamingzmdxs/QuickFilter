using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace QuickFilter.Core;

/// <summary>会话状态文件的读写（位于源目录，随源目录移动）。</summary>
public static class SessionStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static async Task SaveAsync(Session session, CancellationToken ct = default)
    {
        var path = Session.StateFilePath(session.SourceDir);
        var tmp = path + ".tmp";
        var json = JsonSerializer.Serialize(session.State, JsonOptions);
        await File.WriteAllTextAsync(tmp, json, Encoding.UTF8, ct).ConfigureAwait(false);
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>尝试加载源目录下的会话状态；失败时输出原因并返回 null。</summary>
    public static Session? TryLoad(string sourceDir, out string? error)
    {
        error = null;
        var path = Session.StateFilePath(sourceDir);
        if (!File.Exists(path)) { error = "未找到会话状态文件"; return null; }
        try
        {
            var json = File.ReadAllText(path);
            var state = JsonSerializer.Deserialize<SessionState>(json, JsonOptions);
            if (state is null) { error = "会话文件内容为空"; return null; }
            if (state.Version != 1) { error = $"会话文件版本不受支持：{state.Version}"; return null; }
            if (!string.Equals(Path.GetFullPath(state.SourceDir), Path.GetFullPath(sourceDir), StringComparison.OrdinalIgnoreCase))
            { error = "会话文件指向不同的源目录"; return null; }

            // 重建目标目录（可能已被用户删除）
            foreach (var dir in new[] { state.LeftDir, state.RightDir, state.DeletedDir })
            {
                if (!string.IsNullOrWhiteSpace(dir)) Directory.CreateDirectory(dir);
            }

            return new Session(state);
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return null;
        }
    }

    /// <summary>已存在会话文件时新建会话，旧文件备份为带时间戳的 .bak。</summary>
    public static void BackupIfExists(string sourceDir)
    {
        var path = Session.StateFilePath(sourceDir);
        if (!File.Exists(path)) return;
        var backup = $"{path}.{DateTime.Now:yyyyMMdd-HHmmss}.bak";
        File.Move(path, backup, overwrite: true);
    }
}
