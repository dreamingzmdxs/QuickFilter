namespace QuickFilter.Core;

/// <summary>文件移动、冲突重命名与撤销回移。</summary>
public static class FileRouter
{
    /// <summary>操作对应的目标根目录（左/右/删除）。</summary>
    public static string TargetDirectoryFor(SessionAction action, Session session) => action switch
    {
        SessionAction.Left => session.LeftDir,
        SessionAction.Right => session.RightDir,
        SessionAction.Delete => session.DeletedDir,
        _ => throw new ArgumentOutOfRangeException(nameof(action), "跳过操作不需要目标目录"),
    };

    /// <summary>根据目标根目录与相对路径计算目标完整路径，保留子目录结构。</summary>
    public static string BuildTargetPath(string targetRoot, string relativePath)
    {
        if (string.IsNullOrEmpty(targetRoot)) throw new ArgumentException("目标目录为空", nameof(targetRoot));
        var sub = Path.GetDirectoryName(relativePath);
        var targetDir = string.IsNullOrEmpty(sub)
            ? targetRoot
            : Path.Combine(targetRoot, sub.Replace('/', Path.DirectorySeparatorChar));
        return Path.Combine(targetDir, Path.GetFileName(relativePath));
    }

    /// <summary>目标已存在同名文件时生成不冲突路径：name.ext → name_1.ext → name_2.ext …</summary>
    public static string ResolveConflict(string targetFile)
    {
        if (!File.Exists(targetFile)) return targetFile;
        var dir = Path.GetDirectoryName(targetFile) ?? "";
        var name = Path.GetFileNameWithoutExtension(targetFile);
        var ext = Path.GetExtension(targetFile);
        for (var i = 1; ; i++)
        {
            var candidate = Path.Combine(dir, $"{name}_{i}{ext}");
            if (!File.Exists(candidate)) return candidate;
        }
    }

    /// <summary>把源文件移动到目标根目录（保留相对路径结构），同名自动加序号，返回最终目标路径。</summary>
    public static string MoveWithRename(string sourceFile, string targetRoot, string relativePath)
    {
        var target = BuildTargetPath(targetRoot, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        target = ResolveConflict(target);
        File.Move(sourceFile, target);
        return target;
    }

    /// <summary>撤销时把文件移回原位置；若原位置已被占用则自动加序号。</summary>
    public static string MoveBack(UndoEntry entry)
    {
        if (!File.Exists(entry.TargetFullPath))
            throw new FileNotFoundException("待恢复的文件不存在", entry.TargetFullPath);
        Directory.CreateDirectory(Path.GetDirectoryName(entry.SourceFullPath)!);
        var dest = ResolveConflict(entry.SourceFullPath);
        File.Move(entry.TargetFullPath, dest);
        return dest;
    }
}
