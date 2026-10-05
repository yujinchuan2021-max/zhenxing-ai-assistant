using System.IO.Enumeration;

namespace TubaWinUi3.Services.AppManagement;

/// <summary>The application center's records are candidates; executable metadata supplies the identity evidence.</summary>
internal static class InstalledToolReuse
{
    internal static string? FindRegisteredPath(SystemInstaller.ToolAccessMetadata metadata,
        IEnumerable<SoftRecord> records, Func<string, IEnumerable<string>> expandCandidates,
        Func<string, string?> readProductName)
    {
        foreach (var record in records)
        {
            // Name, Source and the user-entered Version never establish executable identity.
            var path = record.Path ?? "";
            if (!Path.IsPathFullyQualified(path)) continue;
            foreach (var candidate in expandCandidates(path))
            {
                if (!Path.IsPathFullyQualified(candidate) ||
                    !metadata.ExecutableNames.Any(pattern => FileSystemName.MatchesSimpleExpression(
                        pattern, Path.GetFileName(candidate), ignoreCase: true))) continue;
                if (MatchesProductIdentity(metadata, readProductName(candidate))) return candidate;
            }
        }
        return null;
    }

    internal static bool MatchesProductIdentity(SystemInstaller.ToolAccessMetadata metadata, string? product)
    {
        if (string.IsNullOrWhiteSpace(product)) return false;
        var actual = Compact(product);
        if (metadata.TargetKey == "claude-desktop") return actual is "claude" or "claudedesktop";
        if (metadata.TargetKey == "claude-code") return actual == "claudecode";
        var expected = Compact(metadata.Name);
        return actual == expected || actual.StartsWith(expected, StringComparison.Ordinal)
            || metadata.TargetKey == "vscode" && actual is "microsoftvisualstudiocode" or "visualstudiocode";
    }

    internal static bool MatchesOsDisplayName(SystemInstaller.ToolAccessMetadata metadata, string display)
    {
        if (metadata.TargetKey == "claude-desktop")
            return System.Text.RegularExpressions.Regex.IsMatch(display.Trim(),
                @"\AClaude(?: Desktop)?(?:\s+v?\d+(?:\.\d+)*(?:[-+][0-9A-Za-z.-]+)?)?\z",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);
        var names = metadata.TargetKey switch
        {
            "vscode" => new[] { metadata.Name, "Microsoft Visual Studio Code" },
            "python" => new[] { "Python" },
            "node" => new[] { "Node.js" },
            _ => new[] { metadata.Name },
        };
        return names.Any(name => display.Equals(name, StringComparison.OrdinalIgnoreCase)
            || display.StartsWith(name + " ", StringComparison.OrdinalIgnoreCase));
    }

    private static string Compact(string text) => new(text.Where(char.IsLetterOrDigit)
        .Select(char.ToLowerInvariant).ToArray());
}
