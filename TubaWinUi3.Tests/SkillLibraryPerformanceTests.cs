using TubaWinUi3.Services.Agent;

namespace TubaWinUi3.Tests;

public sealed class SkillLibraryPerformanceTests
{
    private static SkillCatalogItem Item(string id, string details = "") => new()
        { Id = id, DisplayName = id, Category = "development", Details = details, CanInstall = true };

    [Fact]
    public void SearchingLongDescriptionsDoesNotCopyEveryDocument()
    {
        // The former field concatenation allocated a new 64 KiB document for each
        // row on every keystroke, before allocating its intermediate result array.
        var details = new string('x', 64 * 1024);
        var rows = Enumerable.Range(0, 2000).Select(i => Item("skill_" + i, details)).ToArray();
        SkillCatalogueReadinessFilter.Filter([], "all", "missing", SkillCatalogueReadiness.All);
        var before = GC.GetAllocatedBytesForCurrentThread();
        var matches = SkillCatalogueReadinessFilter.Filter(rows, "all", "missing", SkillCatalogueReadiness.All);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Empty(matches);
        Assert.True(allocated < 2 * 1024 * 1024, $"Filtering copied catalogue text: {allocated} bytes.");
    }

    [Fact]
    public void ObsoleteSearchStopsEnumeratingBeforeCompletingTheCatalogue()
    {
        using var stop = new CancellationTokenSource();
        int visited = 0;
        IEnumerable<SkillCatalogItem> Rows()
        {
            for (int index = 0; index < 10000; index++)
            {
                visited++;
                if (index == 100) stop.Cancel();
                yield return Item("skill_" + index);
            }
        }
        Assert.Throws<OperationCanceledException>(() =>
            SkillCatalogueReadinessFilter.Filter(Rows(), "all", "skill", SkillCatalogueReadiness.All, stop.Token));
        Assert.InRange(visited, 100, 102);
    }

    [Fact]
    public async Task CancelledOldRequestCannotAdvanceTheRemountedCatalogue()
    {
        var loader = new SkillCatalogLoader();
        using var stop = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var oldResponse = new TaskCompletionSource<SkillCatalog>(TaskCreationOptions.RunContinuationsAsynchronously);
        var oldProgress = 0;
        var oldLoad = loader.LoadAsync((page, token) =>
        {
            Assert.Equal(0, page);
            entered.TrySetResult();
            return oldResponse.Task; // Deliberately simulates a transport ignoring cancellation.
        }, _ => oldProgress++, stop.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        stop.Cancel();
        var newProgress = new List<SkillCatalogLoadProgress>();
        var newLoad = loader.LoadAsync((page, token) => Task.FromResult(new SkillCatalog(1, "new",
            [Item("new_skill")], false, page)), newProgress.Add, CancellationToken.None);
        oldResponse.SetResult(new(1, "old", [Item("old_skill")], false, 0));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => oldLoad);
        await newLoad.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(0, oldProgress);
        Assert.Equal("new_skill", Assert.Single(loader.Items).Id);
        Assert.True(loader.IsComplete);
        Assert.Single(newProgress);
    }

    [Fact]
    public async Task BrowserPreloadsTwoPagesThenOnePageWithoutReadingTheEntireIndex()
    {
        var loader = new SkillCatalogLoader();
        var requests = new List<int>();
        Task<SkillCatalog> Fetch(int page, CancellationToken token)
        {
            requests.Add(page);
            return Task.FromResult(new SkillCatalog(1, "stable", [Item("page_" + page)], page < 4, page));
        }
        await loader.LoadAsync(Fetch, _ => { }, CancellationToken.None, maxAdditionalPages: 2);
        Assert.Equal(new[] { 0, 1 }, requests);
        Assert.False(loader.IsComplete);
        var firstSnapshot = loader.Items;
        await loader.LoadAsync(Fetch, _ => { }, CancellationToken.None, maxAdditionalPages: 1);
        Assert.Equal(new[] { 0, 1, 2 }, requests);
        Assert.Equal(2, firstSnapshot.Length);
        Assert.Equal(3, loader.Items.Length);
        await loader.LoadAsync(Fetch, _ => { }, CancellationToken.None);
        Assert.Equal(new[] { 0, 1, 2, 3, 4 }, requests);
        Assert.True(loader.IsComplete);
        Assert.Equal(5, loader.Items.Select(item => item.Id).Distinct().Count());
    }

    [Fact]
    public async Task InterruptedPageReadResumesWithoutDroppingEarlierItems()
    {
        var loader = new SkillCatalogLoader();
        await Assert.ThrowsAsync<HttpRequestException>(() => loader.LoadAsync((page, token) =>
            page == 0 ? Task.FromResult(new SkillCatalog(1, "stable", [Item("first")], true, 0))
                : Task.FromException<SkillCatalog>(new HttpRequestException("Synthetic interruption")),
            _ => { }, CancellationToken.None));
        Assert.Equal("first", Assert.Single(loader.Items).Id);
        var pages = new List<int>();
        await loader.LoadAsync((page, token) =>
        {
            pages.Add(page);
            return Task.FromResult(new SkillCatalog(1, "stable", [Item("last")], false, page));
        }, _ => { }, CancellationToken.None);
        Assert.Equal(new[] { 1 }, pages);
        Assert.Equal(new[] { "first", "last" }, loader.Items.Select(item => item.Id));
        Assert.True(loader.IsComplete);
    }
}
