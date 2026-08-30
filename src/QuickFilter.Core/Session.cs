namespace QuickFilter.Core;

/// <summary>
/// 一次筛选会话：目录、扫描队列、已处理记录、撤销栈与操作日志。
/// 队列顺序在会话创建时固定，续传时不重复移动、不遗漏。
/// </summary>
public sealed class Session
{
    public const string StateFileName = ".quickfilter-session.json";
    public const int MaxUndoSteps = 100;

    public Session(SessionState state) => State = state;

    public SessionState State { get; }

    public string SourceDir => State.SourceDir;
    public string LeftDir => State.LeftDir;
    public string RightDir => State.RightDir;
    public string DeletedDir => State.DeletedDir;
    public OrderRule OrderRule => State.OrderRule;

    public IReadOnlyList<string> Queue => State.Queue;
    public IReadOnlyDictionary<string, SessionAction> Processed => State.Processed;
    public IReadOnlyList<LogEntry> Logs => State.Logs;

    /// <summary>相对路径在队列中的位置（从 1 开始）；不存在时为 0。</summary>
    public int PositionOf(string relativePath) => State.Queue.IndexOf(relativePath) + 1;

    public int TotalCount => State.Queue.Count;
    public int ProcessedCount => State.Processed.Count;
    public int RemainingCount => TotalCount - ProcessedCount;
    public int LeftCount => CountOf(SessionAction.Left);
    public int RightCount => CountOf(SessionAction.Right);
    public int DeleteCount => CountOf(SessionAction.Delete);
    public int SkipCount => CountOf(SessionAction.Skip);

    /// <summary>当前应展示的相对路径（队列中第一个未处理的项）；全部完成时为 null。</summary>
    public string? CurrentRelativePath
    {
        get
        {
            int idx = NextPendingIndex();
            return idx < 0 ? null : State.Queue[idx];
        }
    }

    public static string StateFilePath(string sourceDir) => Path.Combine(sourceDir, StateFileName);
    public static bool HasStateFile(string sourceDir) => File.Exists(StateFilePath(sourceDir));

    /// <summary>新建会话（dirs 已做基本校验；目录不存在时由调用方创建）。</summary>
    public static Session Create(string sourceDir, string leftDir, string rightDir, string deletedDir,
        OrderRule orderRule, bool recursive, List<ImageItem> items,
        int previewWidth = 1920, bool playGif = false)
    {
        var state = new SessionState
        {
            SessionId = Guid.NewGuid().ToString("N"),
            StartTimeUtc = DateTime.UtcNow,
            SourceDir = Path.GetFullPath(sourceDir),
            LeftDir = Path.GetFullPath(leftDir),
            RightDir = Path.GetFullPath(rightDir),
            DeletedDir = Path.GetFullPath(deletedDir),
            OrderRule = orderRule,
            Recursive = recursive,
            PreviewWidth = previewWidth,
            PlayGifAnimation = playGif,
            Queue = items.Select(i => i.RelativePath).ToList(),
        };
        return new Session(state);
    }

    /// <summary>对当前图片执行一次操作（移动文件 / 记录跳过），成功后内部状态推进。</summary>
    public async Task<ActionResult> ExecuteAsync(SessionAction action, CancellationToken ct = default)
    {
        var rel = CurrentRelativePath;
        if (rel is null) return ActionResult.Failure("没有待处理的图片", null);

        var source = Path.Combine(SourceDir, RelToFs(rel));

        if (action == SessionAction.Skip)
        {
            State.Processed[rel] = action;
            PushUndo(new UndoEntry { RelativePath = rel, Action = action, IsUnused = true, TimestampUtc = DateTime.UtcNow });
            Log(rel, action, source, "", "已完成");
            return ActionResult.Success(rel);
        }

        if (!File.Exists(source))
            return ActionResult.Failure($"源文件不存在：{source}", rel);

        var targetRoot = FileRouter.TargetDirectoryFor(action, this);
        string target;
        try
        {
            target = await Task.Run(() => FileRouter.MoveWithRename(source, targetRoot, rel), ct).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            Log(rel, action, source, targetRoot, $"失败：{ex.Message}");
            return ActionResult.Failure(ex.Message, rel);
        }

        State.Processed[rel] = action;
        PushUndo(new UndoEntry
        {
            RelativePath = rel,
            SourceFullPath = source,
            TargetFullPath = target,
            Action = action,
            TimestampUtc = DateTime.UtcNow,
        });
        Log(rel, action, source, target, "已完成");
        return ActionResult.Success(rel);
    }

    /// <summary>撤销最近一步：文件移回源目录原位置（若已被占用则加序号），跳过类操作仅取消记录。</summary>
    public async Task<ActionResult> UndoAsync()
    {
        if (State.UndoStack.Count == 0) return ActionResult.Failure("没有可撤销的操作", null);

        var entry = State.UndoStack[^1];
        State.UndoStack.RemoveAt(State.UndoStack.Count - 1);

        if (entry.IsUnused)
        {
            State.Processed.Remove(entry.RelativePath);
            Log(entry.RelativePath, SessionAction.Skip, "", "", "已撤销");
            return ActionResult.Success(entry.RelativePath);
        }

        try
        {
            var dest = await Task.Run(() => FileRouter.MoveBack(entry)).ConfigureAwait(true);
            State.Processed.Remove(entry.RelativePath);
            Log(entry.RelativePath, entry.Action, entry.TargetFullPath, dest, "已撤销");
            return ActionResult.Success(entry.RelativePath);
        }
        catch (Exception ex)
        {
            Log(entry.RelativePath, entry.Action, entry.TargetFullPath, entry.SourceFullPath, $"撤销失败：{ex.Message}");
            return ActionResult.Failure(ex.Message, entry.RelativePath);
        }
    }

    private int NextPendingIndex() => State.Queue.FindIndex(p => !State.Processed.ContainsKey(p));

    private int CountOf(SessionAction action) => State.Processed.Values.Count(v => v == action);

    private void PushUndo(UndoEntry entry)
    {
        State.UndoStack.Add(entry);
        while (State.UndoStack.Count > MaxUndoSteps) State.UndoStack.RemoveAt(0);
    }

    private void Log(string rel, SessionAction action, string sourcePath, string targetPath, string result)
    {
        State.Logs.Add(new LogEntry
        {
            TimestampUtc = DateTime.UtcNow,
            FileName = Path.GetFileName(RelToFs(rel)),
            RelativePath = rel,
            Action = action,
            SourcePath = sourcePath,
            TargetPath = targetPath,
            Result = result,
        });
    }

    private static string RelToFs(string relativePath) => relativePath.Replace('/', Path.DirectorySeparatorChar);
}
