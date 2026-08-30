using System.Text;

namespace QuickFilter.Core;

/// <summary>操作日志导出为 CSV（UTF-8 带 BOM，Excel 可直接打开）。</summary>
public static class LogExporter
{
    private static readonly string[] Headers = { "时间", "文件名", "相对路径", "操作", "源路径", "目标路径", "结果" };

    public static string BuildCsv(IEnumerable<LogEntry> logs)
    {
        var sb = new StringBuilder();
        sb.AppendLine(string.Join(",", Headers));
        foreach (var e in logs)
        {
            var fields = new[]
            {
                e.TimestampUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"),
                e.FileName,
                e.RelativePath,
                e.Action.Label(),
                e.SourcePath,
                e.TargetPath,
                e.Result,
            };
            sb.AppendLine(string.Join(",", fields.Select(CsvEscape)));
        }
        return sb.ToString();
    }

    private static string CsvEscape(string v)
    {
        v ??= "";
        return v.Contains('"') || v.Contains(',') || v.Contains('\n')
            ? "\"" + v.Replace("\"", "\"\"") + "\""
            : v;
    }
}
