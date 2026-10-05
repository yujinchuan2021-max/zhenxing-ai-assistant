using TubaWinUi3.Services;

namespace TubaWinUi3.Tests;

[Collection("GlobalConfigTests")]
public sealed class CloudToolStorageUsageTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "zxai-cloud-storage-" + Guid.NewGuid().ToString("N"));
    private readonly string? _oldDataRoot = DataRoots.TestRootOverrideForTest;

    public CloudToolStorageUsageTests()
    {
        Directory.CreateDirectory(_root);
        DataRoots.TestRootOverrideForTest = _root;
        Assert.Equal(_root, DataRoots.EffectiveTestRoot);
    }

    [Fact]
    public void CloudToolLocationsAreMeasuredSeparatelyAndCannotBeDeleted()
    {
        var installed = Path.Combine(_root, "CloudTools", "Installed");
        var backups = Path.Combine(_root, "CloudTools", "Backups");
        Directory.CreateDirectory(Path.Combine(installed, "sample"));
        Directory.CreateDirectory(Path.Combine(backups, "sample-old"));
        File.WriteAllBytes(Path.Combine(installed, "sample", "app.exe"), [1, 2, 3]);
        File.WriteAllBytes(Path.Combine(backups, "sample-old", "user.ini"), [4, 5, 6, 7]);

        var items = StorageUsageService.CreateCloudToolStorageItems(DataRoots.EffectiveTestRoot!);
        Assert.Equal(2, items.Count);
        var current = Assert.Single(items, item => item.Id == "cloud-tools-installed");
        var backup = Assert.Single(items, item => item.Id == "cloud-tools-backups");
        Assert.Equal(installed, Assert.Single(current.Paths));
        Assert.Equal(backups, Assert.Single(backup.Paths));
        foreach (var item in items)
        {
            Assert.Equal(StorageGroupKind.Program, item.Kind);
            Assert.False(item.CanDelete);
            Assert.False(item.DefaultChecked);
        }
        Assert.Equal((3L, 1), StorageUsageService.MeasureItem(current));
        Assert.Equal((4L, 1), StorageUsageService.MeasureItem(backup));

        var result = StorageUsageService.Delete(items);
        Assert.Equal(0, result.DeletedCount);
        Assert.Equal(0L, result.FreedBytes);
        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(Path.Combine(installed, "sample", "app.exe")));
        Assert.Equal(new byte[] { 4, 5, 6, 7 }, File.ReadAllBytes(Path.Combine(backups, "sample-old", "user.ini")));
    }

    [Fact]
    public void MissingCloudToolDirectoriesStayReadOnlyWithoutInventingPaths()
    {
        var items = StorageUsageService.CreateCloudToolStorageItems(DataRoots.EffectiveTestRoot!);
        Assert.Equal(2, items.Count);
        Assert.All(items, item =>
        {
            Assert.Equal(StorageGroupKind.Program, item.Kind);
            Assert.Empty(item.Paths);
            Assert.False(item.CanDelete);
        });
        Assert.False(Directory.Exists(Path.Combine(_root, "CloudTools")));
    }

    [Fact]
    public void DeleteProtectsProgramContentButStillDeletesOrdinaryCacheInSameFixture()
    {
        var program = Path.Combine(_root, "program");
        var cache = Path.Combine(_root, "cache");
        Directory.CreateDirectory(program);
        Directory.CreateDirectory(cache);
        File.WriteAllText(Path.Combine(program, "keep.dat"), "owned tool and user data");
        File.WriteAllBytes(Path.Combine(cache, "discard.tmp"), [10, 11, 12]);
        var programItem = new StorageItem { Id = "fake-program", Kind = StorageGroupKind.Program, Paths = [program] };
        var cacheItem = new StorageItem { Id = "fake-cache", Kind = StorageGroupKind.Cache, Paths = [cache] };
        Assert.False(programItem.CanDelete);
        Assert.True(cacheItem.CanDelete);

        var result = StorageUsageService.Delete([programItem, cacheItem]);
        Assert.Equal("owned tool and user data", File.ReadAllText(Path.Combine(program, "keep.dat")));
        Assert.False(Directory.Exists(cache));
        Assert.Equal(1, result.DeletedCount);
        Assert.Equal(3L, result.FreedBytes);
        Assert.Empty(result.FailedPaths);
    }

    public void Dispose()
    {
        DataRoots.TestRootOverrideForTest = _oldDataRoot;
        // Only this GUID-owned fixture is removed; product paths were never supplied to Delete.
        try { Directory.Delete(_root, recursive: true); } catch { }
    }
}
