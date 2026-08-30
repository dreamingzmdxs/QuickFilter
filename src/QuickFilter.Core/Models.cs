namespace QuickFilter.Core;

/// <summary>排序规则。</summary>
public enum OrderRule { FileName, ModifiedTime, Random }

/// <summary>筛选操作：左目录 / 右目录 / 删除 / 跳过。</summary>
public enum SessionAction { Left, Right, Delete, Skip }

/// <summary>一次操作的结果。</summary>
public sealed record ActionResult(bool Ok, string? Error, string? RelativePath)
{
    public static ActionResult Success(string? relativePath) => new(true, null, relativePath);
    public static ActionResult Failure(string error, string? relativePath) => new(false, error, relativePath);
}

/// <summary>源目录中的一个图片文件。</summary>
public sealed record ImageItem(string RelativePath, string FullPath, string Name, long SizeBytes, DateTime ModifiedUtc)
{
    public string Extension => Path.GetExtension(Name);
}

/// <summary>可撤销的操作记录。</summary>
public sealed class UndoEntry
{
    public string RelativePath { get; set; } = "";
    public string SourceFullPath { get; set; } = "";
    public string TargetFullPath { get; set; } = "";
    public SessionAction Action { get; set; }
    public DateTime TimestampUtc { get; set; }

    /// <summary>无文件移动的操作（如跳过），撤销时无需移动文件。</summary>
    public bool IsUnused { get; set; }
}

/// <summary>一条操作日志。</summary>
public sealed class LogEntry
{
    public DateTime TimestampUtc { get; set; }
    public string FileName { get; set; } = "";
    public string RelativePath { get; set; } = "";
    public SessionAction Action { get; set; }
    public string SourcePath { get; set; } = "";
    public string TargetPath { get; set; } = "";
    public string Result { get; set; } = "";
}

/// <summary>持久化的会话状态（JSON 序列化）。</summary>
public sealed class SessionState
{
    public int Version { get; set; } = 1;
    public string SessionId { get; set; } = "";
    public DateTime StartTimeUtc { get; set; }
    public string SourceDir { get; set; } = "";
    public string LeftDir { get; set; } = "";
    public string RightDir { get; set; } = "";
    public string DeletedDir { get; set; } = "";
    public OrderRule OrderRule { get; set; } = OrderRule.FileName;
    public bool Recursive { get; set; } = true;

    /// <summary>预览解码宽度（px）；0 表示按原始尺寸解码。快速预览模式建议 960。</summary>
    public int PreviewWidth { get; set; } = 1920;

    /// <summary>是否播放 GIF 动画；false 时只显示首帧。</summary>
    public bool PlayGifAnimation { get; set; }

    /// <summary>扫描得到的相对路径列表（/ 分隔的绝对顺序），即待处理队列。</summary>
    public List<string> Queue { get; set; } = new();

    /// <summary>相对路径 → 已执行的操作。</summary>
    public Dictionary<string, SessionAction> Processed { get; set; } = new();

    public List<UndoEntry> UndoStack { get; set; } = new();
    public List<LogEntry> Logs { get; set; } = new();
}

/// <summary>操作的显示名称。</summary>
public static class ActionLabels
{
    public static string Label(this SessionAction action) => action switch
    {
        SessionAction.Left => "左目录",
        SessionAction.Right => "右目录",
        SessionAction.Delete => "删除",
        SessionAction.Skip => "跳过",
        _ => action.ToString(),
    };
}
