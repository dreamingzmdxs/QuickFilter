using System.Diagnostics;
using QuickFilter.Core;
using Xunit;

namespace QuickFilter.Core.Tests;

public class ScannerTests : IDisposable
{
    private readonly string _root;

    public ScannerTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "qf-scan-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(Path.Combine(_root, "sub"));
        File.WriteAllBytes(Path.Combine(_root, "b.jpg"), new byte[10]);
        File.WriteAllBytes(Path.Combine(_root, "a.JPG"), new byte[10]);
        File.WriteAllBytes(Path.Combine(_root, "z.png"), new byte[10]);
        File.WriteAllBytes(Path.Combine(_root, "note.txt"), new byte[10]);
        File.WriteAllBytes(Path.Combine(_root, "sub", "c.jpg"), new byte[10]);
        File.WriteAllBytes(Path.Combine(_root, "sub", "d.webp"), new byte[10]);
    }

    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    [Fact]
    public void Scan_FileNameOrder_Recursive_FiltersExtensions()
    {
        var items = ImageScanner.Scan(_root, recursive: true, OrderRule.FileName);
        var names = items.Select(i => i.Name).ToList();
        Assert.Equal(new[] { "a.JPG", "b.jpg", "c.jpg", "d.webp", "z.png" }, names);
        Assert.DoesNotContain(items, i => i.Extension == ".txt");
        Assert.Contains(items, i => i.RelativePath == "sub/c.jpg");
    }

    [Fact]
    public void Scan_TopLevelOnly_ExcludesSubdirectories()
    {
        var items = ImageScanner.Scan(_root, recursive: false, OrderRule.FileName);
        Assert.Equal(new[] { "a.JPG", "b.jpg", "z.png" }, items.Select(i => i.Name).ToArray());
    }

    [Fact]
    public void Scan_ExcludeDirs_SkipsInternalFolder()
    {
        var deleted = Path.Combine(_root, "已删除");
        Directory.CreateDirectory(deleted);
        File.WriteAllBytes(Path.Combine(deleted, "old.jpg"), new byte[10]);

        var items = ImageScanner.Scan(_root, recursive: true, OrderRule.FileName, new[] { deleted });
        Assert.DoesNotContain(items, i => i.RelativePath.StartsWith("已删除/"));
    }

    [Fact]
    public void Scan_Random_ContainsAllItems()
    {
        var a = ImageScanner.Scan(_root, recursive: false, OrderRule.Random);
        var b = ImageScanner.Scan(_root, recursive: false, OrderRule.Random);
        Assert.Equal(3, a.Count);
        Assert.Equal(new[] { "a.JPG", "b.jpg", "z.png" }, a.Select(i => i.Name).OrderBy(x => x).ToArray());
        Assert.Equal(a.Count, b.Count);
    }

    [Fact]
    public void Scan_ModifiedTime_BySortOrder()
    {
        // 把根目录最早的 z.png 的修改时间调早，应排到最前
        var early = Path.Combine(_root, "z.png");
        File.SetLastWriteTimeUtc(early, DateTime.UtcNow.AddHours(-5));
        var items = ImageScanner.Scan(_root, recursive: false, OrderRule.ModifiedTime);
        Assert.Equal("z.png", items[0].Name);
    }

    [Fact]
    public void Scan_TenThousandFiles_CompletesQuickly()
    {
        var dir = Path.Combine(Path.GetTempPath(), "qf-perf-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            // 1 万个 16×16 BMP（直接写字节，避免 GDI+ 开销）
            var bytes = MiniBmp(1);
            for (int i = 0; i < 10_000; i++)
                File.WriteAllBytes(Path.Combine(dir, $"img_{i:D5}.bmp"), bytes);

            var sw = Stopwatch.StartNew();
            var items = ImageScanner.Scan(dir, recursive: false, OrderRule.FileName);
            sw.Stop();

            Assert.Equal(10_000, items.Count);
            Assert.True(sw.ElapsedMilliseconds < 15_000,
                $"扫描 10000 个文件耗时 {sw.ElapsedMilliseconds} ms，超出 15s 预期");
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    private static byte[] MiniBmp(int seed)
    {
        const int w = 16, h = 16;
        var data = new byte[54 + w * 3 * h];
        data[0] = (byte)'B';
        data[1] = (byte)'M';
        BitConverter.GetBytes(data.Length).CopyTo(data, 2);
        BitConverter.GetBytes(54).CopyTo(data, 10);                    // 像素数据偏移
        BitConverter.GetBytes(40).CopyTo(data, 14);                    // BITMAPINFOHEADER 大小
        BitConverter.GetBytes(w).CopyTo(data, 18);
        BitConverter.GetBytes(h).CopyTo(data, 22);
        BitConverter.GetBytes((short)1).CopyTo(data, 26);              // 位面数
        BitConverter.GetBytes((short)24).CopyTo(data, 28);             // 24bpp
        BitConverter.GetBytes(w * 3 * h).CopyTo(data, 34);             // 像素数据大小
        for (int i = 0; i < h; i++)
            for (int j = 0; j < w; j++)
            {
                int offset = 54 + (i * w + j) * 3;
                data[offset] = (byte)((seed + j) % 256);
                data[offset + 1] = (byte)((seed + i) % 256);
                data[offset + 2] = (byte)((seed * 3) % 256);
            }
        return data;
    }
}
