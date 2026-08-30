using QuickFilter.Core;
using Xunit;

namespace QuickFilter.Core.Tests;

public class SessionTests : IDisposable
{
    private readonly string _root;
    private readonly string _source;
    private readonly string _left;
    private readonly string _right;
    private readonly string _deleted;

    public SessionTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "qf-session-" + Guid.NewGuid().ToString("N"));
        _source = Path.Combine(_root, "src");
        _left = Path.Combine(_root, "left");
        _right = Path.Combine(_root, "right");
        _deleted = Path.Combine(_root, "deleted");
        Directory.CreateDirectory(_source);
        File.WriteAllBytes(Path.Combine(_source, "1.jpg"), new byte[4]);
        File.WriteAllBytes(Path.Combine(_source, "2.png"), new byte[4]);
        File.WriteAllBytes(Path.Combine(_source, "3.webp"), new byte[4]);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

    private Session NewSession()
    {
        var items = ImageScanner.Scan(_source, recursive: false, OrderRule.FileName);
        return Session.Create(_source, _left, _right, _deleted, OrderRule.FileName, true, items);
    }

    [Fact]
    public async Task ExecuteAsync_MovesToTargetDirectories()
    {
        var s = NewSession();
        var r1 = await s.ExecuteAsync(SessionAction.Left);
        Assert.True(r1.Ok);
        Assert.True(File.Exists(Path.Combine(_left, "1.jpg")));
        Assert.False(File.Exists(Path.Combine(_source, "1.jpg")));

        var r2 = await s.ExecuteAsync(SessionAction.Right);
        Assert.True(r2.Ok);
        Assert.True(File.Exists(Path.Combine(_right, "2.png")));

        var r3 = await s.ExecuteAsync(SessionAction.Delete);
        Assert.True(r3.Ok);
        Assert.True(File.Exists(Path.Combine(_deleted, "3.webp")));

        Assert.Equal(3, s.ProcessedCount);
        Assert.Equal(0, s.RemainingCount);
        Assert.Equal(1, s.LeftCount);
        Assert.Equal(1, s.RightCount);
        Assert.Equal(1, s.DeleteCount);
    }

    [Fact]
    public async Task ExecuteAsync_Skip_KeepsFileInSource()
    {
        var s = NewSession();
        var r = await s.ExecuteAsync(SessionAction.Skip);
        Assert.True(r.Ok);
        Assert.True(File.Exists(Path.Combine(_source, "1.jpg")));
        Assert.Equal(1, s.SkipCount);
        Assert.Equal("2.png", s.CurrentRelativePath);
    }

    [Fact]
    public async Task Undo_MovesFileBackAndReappears()
    {
        var s = NewSession();
        await s.ExecuteAsync(SessionAction.Left);
        Assert.True(File.Exists(Path.Combine(_left, "1.jpg")));

        var u = await s.UndoAsync();
        Assert.True(u.Ok);
        Assert.True(File.Exists(Path.Combine(_source, "1.jpg")));
        Assert.False(File.Exists(Path.Combine(_left, "1.jpg")));
        Assert.Equal("1.jpg", s.CurrentRelativePath); // 撤回到该图继续判断
        Assert.Equal(0, s.LeftCount);
    }

    [Fact]
    public async Task Undo_Skip_OnlyClearsRecord()
    {
        var s = NewSession();
        await s.ExecuteAsync(SessionAction.Skip);
        var u = await s.UndoAsync();
        Assert.True(u.Ok);
        Assert.Equal("1.jpg", s.CurrentRelativePath);
        Assert.Equal(0, s.SkipCount);
    }

    [Fact]
    public async Task SaveAndLoad_ResumesCorrectly()
    {
        var s = NewSession();
        await s.ExecuteAsync(SessionAction.Left);   // 1.jpg → left
        await SessionStore.SaveAsync(s);

        Assert.True(Session.HasStateFile(_source));
        var loaded = SessionStore.TryLoad(_source, out var error);
        Assert.NotNull(loaded);
        Assert.Null(error);
        Assert.Equal(1, loaded!.ProcessedCount);
        Assert.Equal("2.png", loaded.CurrentRelativePath);
    }

    [Fact]
    public async Task BackupIfExists_WhenNewSession()
    {
        var s = NewSession();
        await SessionStore.SaveAsync(s);
        SessionStore.BackupIfExists(_source);
        Assert.False(Session.HasStateFile(_source));
        Assert.Single(Directory.GetFiles(_source, "*.bak"));
    }

    [Fact]
    public async Task TryLoad_WrongSourceDir_Fails()
    {
        var s = NewSession();
        await SessionStore.SaveAsync(s);
        var other = Path.Combine(_root, "other");
        Directory.CreateDirectory(other);
        var loaded = SessionStore.TryLoad(other, out var error);
        Assert.Null(loaded);
        Assert.NotNull(error);
    }

    [Fact]
    public async Task Perform_OnMissingSourceFile_FailsGracefully()
    {
        var s = NewSession();
        File.Delete(Path.Combine(_source, "1.jpg"));
        var r = await s.ExecuteAsync(SessionAction.Left);
        Assert.False(r.Ok);
        Assert.NotNull(r.Error);
        Assert.Equal(0, s.ProcessedCount);
    }
}
