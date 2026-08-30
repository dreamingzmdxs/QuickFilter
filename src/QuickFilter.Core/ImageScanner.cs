namespace QuickFilter.Core;

/// <summary>扫描源目录中的图片并生成排序后的队列。</summary>
public static class ImageScanner
{
    public static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".webp", ".bmp", ".gif", ".heic"
    };

    /// <param name="excludeDirs">需要从扫描中排除的目录（如位于源目录内的"已删除"目录）。</param>
    public static List<ImageItem> Scan(string sourceDir, bool recursive, OrderRule order, IEnumerable<string>? excludeDirs = null)
    {
        sourceDir = Path.GetFullPath(sourceDir);
        var excludes = NormalizeExcludes(sourceDir, excludeDirs);

        var files = new List<string>();
        EnumerateFiles(sourceDir, recursive, files);

        var list = new List<ImageItem>(files.Count);
        foreach (var file in files)
        {
            if (!SupportedExtensions.Contains(Path.GetExtension(file))) continue;
            if (excludes.Any(e => file.StartsWith(e, StringComparison.OrdinalIgnoreCase))) continue;

            var rel = Path.GetRelativePath(sourceDir, file).Replace(Path.DirectorySeparatorChar, '/');
            var fi = new FileInfo(file);
            list.Add(new ImageItem(rel, file, fi.Name, fi.Length, fi.LastWriteTimeUtc));
        }

        IEnumerable<ImageItem> sorted = order switch
        {
            OrderRule.ModifiedTime => list.OrderBy(f => f.ModifiedUtc).ThenBy(f => f.Name, StringComparer.OrdinalIgnoreCase),
            OrderRule.Random => Shuffle(list),
            _ => list.OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase),
        };
        return sorted.ToList();
    }

    private static void EnumerateFiles(string dir, bool recursive, List<string> files)
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(dir)) files.Add(f);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or DirectoryNotFoundException)
        {
            // 无权限或无法访问的目录直接跳过，不中断整体扫描
        }

        if (!recursive) return;
        try
        {
            foreach (var d in Directory.EnumerateDirectories(dir)) EnumerateFiles(d, true, files);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or DirectoryNotFoundException)
        {
            // 同上
        }
    }

    private static List<string> NormalizeExcludes(string sourceDir, IEnumerable<string>? excludeDirs)
    {
        var result = new List<string>();
        if (excludeDirs is null) return result;
        var source = Path.TrimEndingDirectorySeparator(sourceDir);
        foreach (var d in excludeDirs)
        {
            if (string.IsNullOrWhiteSpace(d)) continue;
            var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(d));
            // 只排除位于源目录内部的目录，且不等于源目录本身
            if (full.StartsWith(source + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                result.Add(full + Path.DirectorySeparatorChar);
        }
        return result;
    }

    /// <summary>Fisher-Yates 洗牌（会话内顺序固定，保证续传一致）。</summary>
    private static List<ImageItem> Shuffle(List<ImageItem> list)
    {
        var arr = list.ToArray();
        var rng = new Random();
        for (int i = arr.Length - 1; i > 0; i--)
        {
            int j = rng.Next(i + 1);
            (arr[i], arr[j]) = (arr[j], arr[i]);
        }
        return arr.ToList();
    }
}
