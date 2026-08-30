using QuickFilter.Core;
using Xunit;

namespace QuickFilter.Core.Tests;

public class FileRouterTests : IDisposable
{
    private readonly string _root;

    public FileRouterTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "qf-router-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    private string NewFile(string name) { var p = Path.Combine(_root, name); File.WriteAllBytes(p, new byte[4]); return p; }

    [Fact]
    public void ResolveConflict_AppendsSequenceNumber()
    {
        var target = Path.Combine(_root, "photo.jpg");
        File.WriteAllBytes(target, new byte[1]);
        Assert.Equal(Path.Combine(_root, "photo_1.jpg"), FileRouter.ResolveConflict(target));
        File.WriteAllBytes(Path.Combine(_root, "photo_1.jpg"), new byte[1]);
        Assert.Equal(Path.Combine(_root, "photo_2.jpg"), FileRouter.ResolveConflict(target));
    }

    [Fact]
    public void MoveWithRename_PreservesSubdirectoryStructure()
    {
        var src = NewFile("a.jpg");
        var targetRoot = Path.Combine(_root, "left");
        var final = FileRouter.MoveWithRename(src, targetRoot, "sub/b.jpg");
        Assert.Equal(Path.Combine(_root, "left", "sub", "b.jpg"), final);
        Assert.True(File.Exists(final));
        Assert.False(File.Exists(src));
    }

    [Fact]
    public void MoveWithRename_ConflictingName_AutoRenamed()
    {
        var src = NewFile("a.jpg");
        var targetRoot = Path.Combine(_root, "left");
        Directory.CreateDirectory(targetRoot);
        File.WriteAllBytes(Path.Combine(targetRoot, "a.jpg"), new byte[1]);

        var final = FileRouter.MoveWithRename(src, targetRoot, "a.jpg");
        Assert.Equal(Path.Combine(_root, "left", "a_1.jpg"), final);
    }

    [Fact]
    public void MoveBack_RestoresOriginalLocation()
    {
        var src = NewFile("a.jpg");
        var targetRoot = Path.Combine(_root, "left");
        var moved = FileRouter.MoveWithRename(src, targetRoot, "a.jpg");

        var entry = new UndoEntry
        {
            RelativePath = "a.jpg",
            SourceFullPath = src,
            TargetFullPath = moved,
            Action = SessionAction.Left,
        };
        var dest = FileRouter.MoveBack(entry);
        Assert.Equal(src, dest);
        Assert.True(File.Exists(src));
        Assert.False(File.Exists(moved));
    }
}
