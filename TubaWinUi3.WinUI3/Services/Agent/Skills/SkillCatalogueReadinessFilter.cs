namespace TubaWinUi3.Services.Agent;

internal enum SkillCatalogueReadiness { All, Loadable, Reference }

internal sealed record SkillCatalogueReadinessCounts(int Total, int Loadable, int Reference);

/// <summary>
/// Describes the catalogue's loading flag only. It does not infer effectiveness,
/// host compatibility or a quality score from repository popularity or approval.
/// </summary>
internal static class SkillCatalogueReadinessFilter
{
    internal static SkillCatalogItem[] Filter(IEnumerable<SkillCatalogItem> items, string category,
        string search, SkillCatalogueReadiness readiness, CancellationToken token = default)
    {
        search = search.Trim();
        var result = new List<SkillCatalogItem>();
        token.ThrowIfCancellationRequested();
        foreach (var item in items)
        {
            token.ThrowIfCancellationRequested();
            if (SkillCatalogLoader.Matches(item, category, search) && (readiness switch
            {
                SkillCatalogueReadiness.Loadable => item.CanInstall,
                SkillCatalogueReadiness.Reference => !item.CanInstall,
                _ => true
            })) result.Add(item);
        }
        return result.ToArray();
    }

    internal static SkillCatalogueReadinessCounts Count(IEnumerable<SkillCatalogItem> items)
    {
        int total = 0, loadable = 0;
        foreach (var item in items)
        {
            total++;
            if (item.CanInstall) loadable++;
        }
        return new(total, loadable, total - loadable);
    }
}
