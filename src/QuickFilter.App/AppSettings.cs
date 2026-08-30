using System.Text.Json;

namespace QuickFilter.App;

/// <summary>用户设置（%LOCALAPPDATA%\QuickFilter\settings.json）。</summary>
public static class AppSettings
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "QuickFilter", "settings.json");

    public sealed class Data
    {
        /// <summary>固定遥控 PIN；空表示每次启动随机生成。</summary>
        public string FixedPin { get; set; } = "";

        /// <summary>桌面隐私模式：不显示图片，只显示进度与操作记录。</summary>
        public bool PrivacyMode { get; set; }

        /// <summary>上次会话的源目录（用于"继续上次会话"快捷入口）。</summary>
        public string LastSourceDir { get; set; } = "";
    }

    public static Data Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var json = File.ReadAllText(FilePath);
                var d = JsonSerializer.Deserialize<Data>(json);
                if (d is not null) return d;
            }
        }
        catch
        {
            // 设置损坏时使用默认值
        }
        return new Data();
    }

    public static void Save(Data data)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(data, JsonOptions));
        }
        catch (Exception ex)
        {
            AppLog.Error("保存设置失败：" + ex.Message);
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };
}
