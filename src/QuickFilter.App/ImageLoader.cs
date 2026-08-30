using Microsoft.UI.Xaml.Media.Imaging;
using QuickFilter.Core;
using System.Diagnostics;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Foundation;
using Windows.Graphics.Imaging;

namespace QuickFilter.App;

/// <summary>
/// 图片解码：头部读取尺寸与 EXIF 拍摄时间、按预览宽度降采样、GIF 首帧。
/// 所有文件流一律使用异步 IO 打开，避免同步磁盘读取阻塞 UI 线程。
/// </summary>
public static class ImageLoader
{
    public sealed class Result
    {
        public required object Source { get; init; }          // BitmapImage 或 SoftwareBitmapSource
        public required uint PixelWidth { get; init; }
        public required uint PixelHeight { get; init; }
        public required double SizeMb { get; init; }
        public required DateTime Modified { get; init; }
        public DateTime? Taken { get; init; }

        /// <summary>LRU 缓存时间戳（由调用方维护）。</summary>
        public int Clock { get; set; }
    }

    public static async Task<Result> LoadAsync(Session session, string relativePath)
    {
        var full = Path.Combine(session.SourceDir, relativePath.Replace('/', Path.DirectorySeparatorChar));
        var info = new FileInfo(full);
        var sw = Stopwatch.StartNew();

        uint w = 0, h = 0;
        DateTime? taken = null;
        try
        {
            var t0 = sw.ElapsedMilliseconds;
            // 关键：探测（尺寸 + EXIF）在后台线程执行——部分相机 JPEG 的元数据读取会
            // 同步阻塞调用线程（WIC 调用死锁），若在 UI 线程上执行则整个界面假死。
            (w, h, taken) = await Task.Run(() => ProbeAsync(full));
            AppLog.Info($"解码 {relativePath}: 探测完成 {w}×{h}（{sw.ElapsedMilliseconds - t0} ms）");
        }
        catch (Exception ex)
        {
            AppLog.Info($"尺寸/EXIF 读取失败 {relativePath}: {ex.Message}");
        }

        int? decodeWidth = session.State.PreviewWidth > 0 && w > session.State.PreviewWidth ? session.State.PreviewWidth : null;
        object source;
        var ext = Path.GetExtension(full).ToLowerInvariant();

        if (ext == ".gif" && !session.State.PlayGifAnimation)
        {
            // 只显示首帧：SoftwareBitmapSource
            using var fs = OpenRead(full);
            var dec = await BitmapDecoder.CreateAsync(fs.AsRandomAccessStream());
            var frame = await dec.GetFrameAsync(0);
            var transform = new BitmapTransform();
            if (decodeWidth is int dw) transform.ScaledWidth = (uint)dw;
            var bmp = await frame.GetSoftwareBitmapAsync(
                BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, transform,
                ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage);
            var sbs = new SoftwareBitmapSource();
            await sbs.SetBitmapAsync(bmp);
            source = sbs;
        }
        else
        {
            var fs = OpenRead(full);
            try
            {
                var bitmap = new BitmapImage();
                if (decodeWidth is int dw) bitmap.DecodePixelWidth = dw;
                bitmap.ImageOpened += (_, _) => fs.Dispose();
                bitmap.ImageFailed += (_, _) => fs.Dispose();
                AppLog.Info($"解码 {relativePath}: SetSourceAsync 开始");
                var t2 = sw.ElapsedMilliseconds;
                await bitmap.SetSourceAsync(fs.AsRandomAccessStream());
                AppLog.Info($"解码 {relativePath}: SetSourceAsync 完成（{sw.ElapsedMilliseconds - t2} ms）");
                source = bitmap;
            }
            catch
            {
                fs.Dispose();
                throw;
            }
        }

        AppLog.Info($"解码成功 {relativePath}（{sw.ElapsedMilliseconds} ms，{w}×{h}）");
        return new Result
        {
            Source = source,
            PixelWidth = w,
            PixelHeight = h,
            SizeMb = info.Length / 1024.0 / 1024.0,
            Modified = info.LastWriteTime,
            Taken = taken,
        };
    }

    /// <summary>后台线程执行：打开只读流 → BitmapDecoder → 尺寸 → EXIF（带超时）。</summary>
    private static async Task<(uint w, uint h, DateTime? taken)> ProbeAsync(string full)
    {
        using var probe = OpenRead(full);
        var decoder = await BitmapDecoder.CreateAsync(probe.AsRandomAccessStream());
        var w = decoder.PixelWidth;
        var h = decoder.PixelHeight;
        var taken = await ReadExifWithTimeoutAsync(decoder, 2000);
        return (w, h, taken);
    }

    /// <summary>以异步 IO + 顺序扫描打开只读流（关键：否则解码器在 UI 线程做同步磁盘读取）。</summary>
    private static FileStream OpenRead(string fullPath) => new(
        fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
        bufferSize: 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);

    /// <summary>
    /// EXIF 拍摄时间读取。部分相机/手机 JPEG 的元数据失效时 WIC 会无限阻塞，
    /// 因此限制超时：超时后放弃读取（拍摄时间不显示），绝不影响图片显示。
    /// </summary>
    private static async Task<DateTime?> ReadExifWithTimeoutAsync(BitmapDecoder decoder, int timeoutMs)
    {
        try
        {
            var task = ReadExifDateAsync(decoder);
            var completed = await Task.WhenAny(task, Task.Delay(timeoutMs));
            if (completed != task)
            {
                AppLog.Info("EXIF 读取超时，跳过拍摄时间");
                return null;
            }
            return await task;
        }
        catch (Exception ex)
        {
            AppLog.Info($"EXIF 读取异常：{ex.Message}");
            return null;
        }
    }

    private static async Task<DateTime?> ReadExifDateAsync(BitmapDecoder decoder)
    {
        try
        {
            var props = await decoder.BitmapProperties.GetPropertiesAsync(new[] { "System.Photo.DateTaken" });
            if (props.TryGetValue("System.Photo.DateTaken", out var value))
            {
                // CsWinRT 投影中 WinRT DateTime 以 DateTimeOffset 装箱
                if (value.Value is DateTimeOffset dto)
                    return dto.ToLocalTime().DateTime;
                if (value.Value is DateTime dt)
                    return dt;
            }
        }
        catch
        {
            // 无 EXIF 或属性不可读时忽略
        }
        return null;
    }
}
