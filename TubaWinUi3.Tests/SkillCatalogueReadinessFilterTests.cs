using TubaWinUi3.Services.Agent;

namespace TubaWinUi3.Tests;

public sealed class SkillCatalogueReadinessFilterTests
{
    private static SkillCatalogItem Entry(string id, bool loadable, string category = "development",
        string description = "", string details = "", long? stars = null) => new()
        {
            Id = id, DisplayName = id, CanInstall = loadable, Category = category,
            Description = description, Details = details, RepoStars = stars
        };

    [Fact]
    public void AllDirectoryPreservesEveryEntryAndOrder()
    {
        var items = Enumerable.Range(0, 1944).Select(i => Entry("skill_" + i, i < 232)).ToArray();

        Assert.Equal(items, SkillCatalogueReadinessFilter.Filter(items, "all", "", SkillCatalogueReadiness.All));
        Assert.Equal(new SkillCatalogueReadinessCounts(1944, 232, 1712), SkillCatalogueReadinessFilter.Count(items));
    }

    [Fact]
    public void LoadingFlagPartitionsTheEntireDirectoryWithoutUsingPopularity()
    {
        SkillCatalogItem[] items = [Entry("loadable_no_stars", true), Entry("reference_high_stars", false, stars: 100000),
            Entry("loadable_high_stars", true, stars: 100000), Entry("reference_no_stars", false)];

        Assert.Equal(new[] { items[0], items[2] }, SkillCatalogueReadinessFilter.Filter(items, "all", "", SkillCatalogueReadiness.Loadable));
        Assert.Equal(new[] { items[1], items[3] }, SkillCatalogueReadinessFilter.Filter(items, "all", "", SkillCatalogueReadiness.Reference));
    }

    [Fact]
    public void ReadinessCombinesWithCategoryAndFullDirectorySearch()
    {
        var items = Enumerable.Range(0, 1944).Select(i => Entry("skill_" + i, i == 1943,
            category: i == 1943 ? "game" : "development", details: i == 1943 ? "Godot animation" : "")).ToArray();

        Assert.Equal(new[] { items[^1] }, SkillCatalogueReadinessFilter.Filter(items, "game", "  GODOT  ", SkillCatalogueReadiness.Loadable));
        Assert.Empty(SkillCatalogueReadinessFilter.Filter(items, "development", "GODOT", SkillCatalogueReadiness.Loadable));
        Assert.Empty(SkillCatalogueReadinessFilter.Filter(items, "game", "GODOT", SkillCatalogueReadiness.Reference));
    }

    [Fact]
    public void ReturningToAllDoesNotDropReferenceEntriesOrAlterSource()
    {
        SkillCatalogItem[] items = [Entry("one", true), Entry("two", false)];

        Assert.Single(SkillCatalogueReadinessFilter.Filter(items, "all", "", SkillCatalogueReadiness.Loadable));
        Assert.Equal(items, SkillCatalogueReadinessFilter.Filter(items, "all", "", SkillCatalogueReadiness.All));
        Assert.Equal(2, items.Length);
        Assert.False(items[1].CanInstall);
    }

    [Fact]
    public void EmptyDirectoryHasZeroCountsAndNoMatches()
    {
        Assert.Equal(new SkillCatalogueReadinessCounts(0, 0, 0), SkillCatalogueReadinessFilter.Count([]));
        Assert.Empty(SkillCatalogueReadinessFilter.Filter([], "all", "", SkillCatalogueReadiness.All));
    }

    [Fact]
    public void UnknownViewModeKeepsTheFullDirectoryVisible()
    {
        SkillCatalogItem[] items = [Entry("one", true), Entry("two", false)];
        Assert.Equal(items, SkillCatalogueReadinessFilter.Filter(items, "all", "", (SkillCatalogueReadiness)99));
    }
}
