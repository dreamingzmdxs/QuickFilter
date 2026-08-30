namespace QuickFilter.App;

/// <summary>运行日志：%LOCALAPPDATA%\QuickFilter\log.txt。用于排查启动、扫描与图片解码问题。</summary>
public static class AppLog
{
    private static readonly object Gate = new();
    private static readonly string LogFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "QuickFilter", "log.txt");

    public static void Info(string message) => Write("INFO", message);
    public static void Error(string message) => Write("ERROR", message);
    public static string LogFilePath => LogFile;

    private static void Write(string level, string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LogFile)!);
                File.AppendAllText(LogFile, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // 日志写入失败不影响主流程
        }
    }
}
