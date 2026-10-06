using TubaWinUi3.Services;

namespace TubaWinUi3.Tests;

public class HardwareModelCatalogTests
{
    [Theory]
    [InlineData(HardwareModelCategory.Cpu, 16)]
    [InlineData(HardwareModelCategory.Motherboard, 12)]
    [InlineData(HardwareModelCategory.Gpu, 16)]
    [InlineData(HardwareModelCategory.Memory, 12)]
    [InlineData(HardwareModelCategory.Monitor, 10)]
    [InlineData(HardwareModelCategory.Disk, 12)]
    public void EveryHardwareCategory_HasMainstreamSuggestions(HardwareModelCategory category, int minimum)
    {
        var models = HardwareModelCatalog.ForCategory(category);

        Assert.True(models.Count >= minimum, $"{category} needs at least {minimum} suggestions.");
        Assert.All(models, model => Assert.Equal(category, model.Category));
    }

    [Fact]
    public void Presets_HaveUniqueNamesAndOfficialHttpsSources()
    {
        string[] officialDomains =
        [
            "intel.com", "amd.com", "nvidia.com", "asus.com", "msi.com", "gigabyte.com",
            "asrock.com", "kingston.com", "corsair.com", "gskill.com", "micron.com",
            "crucial.com", "lg.com", "samsung.com", "dell.com", "benq.com", "aoc.com",
            "sandisk.com", "seagate.com"
        ];

        Assert.Equal(6, HardwareModelCatalog.Models.Select(model => model.Category).Distinct().Count());
        foreach (var group in HardwareModelCatalog.Models.GroupBy(model => model.Category))
        {
            Assert.Equal(group.Count(), group.Select(model => model.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        }

        Assert.All(HardwareModelCatalog.Models, model =>
        {
            Assert.False(string.IsNullOrWhiteSpace(model.Name));
            Assert.False(string.IsNullOrWhiteSpace(model.Manufacturer));
            Assert.True(Uri.TryCreate(model.SourceUrl, UriKind.Absolute, out var source));
            Assert.Equal(Uri.UriSchemeHttps, source!.Scheme);
            Assert.Contains(officialDomains, domain =>
                source.Host.Equals(domain, StringComparison.OrdinalIgnoreCase) ||
                source.Host.EndsWith($".{domain}", StringComparison.OrdinalIgnoreCase));
        });
    }

    [Fact]
    public void Suggestions_CoverMultipleEstablishedManufacturers()
    {
        AssertManufacturers(HardwareModelCategory.Cpu, "Intel", "AMD");
        AssertManufacturers(HardwareModelCategory.Gpu, "NVIDIA", "AMD", "Intel");
        AssertManufacturers(HardwareModelCategory.Motherboard, "ASUS", "MSI", "GIGABYTE", "ASRock");
        AssertManufacturers(HardwareModelCategory.Memory, "Kingston", "CORSAIR", "G.SKILL", "Crucial");
        AssertManufacturers(HardwareModelCategory.Monitor, "ASUS", "MSI", "GIGABYTE", "LG", "Samsung", "Dell", "BenQ", "AOC");
        AssertManufacturers(HardwareModelCategory.Disk, "Samsung", "SanDisk", "Crucial", "Kingston", "Seagate");
    }

    [Fact]
    public void Suggestions_CannotBeMutatedByAConsumer()
    {
        var allModels = Assert.IsAssignableFrom<IList<HardwareModelPreset>>(HardwareModelCatalog.Models);
        Assert.True(allModels.IsReadOnly);
        Assert.Throws<NotSupportedException>(() => allModels.Clear());

        foreach (var category in Enum.GetValues<HardwareModelCategory>())
        {
            var categoryModels = Assert.IsAssignableFrom<IList<HardwareModelPreset>>(HardwareModelCatalog.ForCategory(category));
            Assert.True(categoryModels.IsReadOnly);
            Assert.Throws<NotSupportedException>(() => categoryModels.Clear());
        }
    }

    [Fact]
    public void InvalidCategory_DoesNotLeakOtherHardwareSuggestions()
    {
        Assert.Empty(HardwareModelCatalog.ForCategory((HardwareModelCategory)(-1)));
    }

    [Fact]
    public void PresetText_IsOnlyTheUserVisibleModelName()
    {
        Assert.All(HardwareModelCatalog.Models, model => Assert.Equal(model.Name, model.ToString()));
    }

    private static void AssertManufacturers(HardwareModelCategory category, params string[] manufacturers)
    {
        var actual = HardwareModelCatalog.ForCategory(category).Select(model => model.Manufacturer).ToArray();
        Assert.All(manufacturers, manufacturer => Assert.Contains(manufacturer, actual));
    }
}
