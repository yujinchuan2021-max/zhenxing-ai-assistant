using System.Text;
using System.Text.RegularExpressions;

namespace TubaWinUi3.Services;

/// <summary>
/// Matches known display templates and binds captured values by placeholder name.
/// Exact-text and fallback translation policies remain with each caller.
/// </summary>
internal sealed class DisplayTemplateTranslator
{
    private readonly Template[] _templates;

    internal DisplayTemplateTranslator(IReadOnlyDictionary<string, string> translations)
    {
        _templates = translations
            .Where(pair => pair.Key.Contains('{'))
            .OrderByDescending(pair => TemplateLiteralLength(pair.Key))
            .ThenByDescending(pair => pair.Key.Length)
            .Select(pair => new Template(BuildRx(pair.Key), TemplateTokens(pair.Key).ToArray(), pair.Value))
            .ToArray();
    }

    internal bool TryTranslate(string text, out string translated)
    {
        foreach (var template in _templates)
        {
            var match = template.Pattern.Match(text);
            if (!match.Success) continue;

            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            for (var i = 0; i < template.Tokens.Length; i++)
                if (i + 1 < match.Groups.Count)
                    values.TryAdd(template.Tokens[i], match.Groups[i + 1].Value);

            var result = new StringBuilder();
            var cursor = 0;
            var holeIndex = 0;
            while (true)
            {
                var open = template.English.IndexOf('{', cursor);
                if (open < 0)
                {
                    result.Append(template.English, cursor, template.English.Length - cursor);
                    break;
                }
                var close = template.English.IndexOf('}', open);
                if (close < 0)
                {
                    result.Append(template.English, cursor, template.English.Length - cursor);
                    break;
                }

                result.Append(template.English, cursor, open - cursor);
                var token = template.English[(open + 1)..close];
                holeIndex++;
                // Preserve legacy entries with differently named English placeholders.
                if (!values.TryGetValue(token, out var value))
                    value = holeIndex < match.Groups.Count ? match.Groups[holeIndex].Value : "";
                result.Append(value);
                cursor = close + 1;
            }

            translated = result.ToString();
            return true;
        }

        translated = text;
        return false;
    }

    private static int TemplateLiteralLength(string text)
    {
        var parts = text.Split('{');
        var length = parts[0].Length;
        for (var i = 1; i < parts.Length; i++)
        {
            var close = parts[i].IndexOf('}');
            length += close < 0 ? parts[i].Length + 1 : parts[i].Length - close - 1;
        }
        return length;
    }

    private static IEnumerable<string> TemplateTokens(string text)
    {
        var parts = text.Split('{');
        for (var i = 1; i < parts.Length; i++)
        {
            var close = parts[i].IndexOf('}');
            if (close >= 0) yield return parts[i][..close];
        }
    }

    private static Regex BuildRx(string text)
    {
        var parts = text.Split('{');
        var pattern = new StringBuilder("^");
        pattern.Append(Regex.Escape(parts[0]));
        for (var i = 1; i < parts.Length; i++)
        {
            var close = parts[i].IndexOf('}');
            if (close < 0)
            {
                pattern.Append(Regex.Escape("{" + parts[i]));
                continue;
            }
            pattern.Append("(.*?)");
            pattern.Append(Regex.Escape(parts[i][(close + 1)..]));
        }
        pattern.Append('$');
        return new Regex(pattern.ToString(), RegexOptions.Singleline);
    }

    private sealed record Template(Regex Pattern, string[] Tokens, string English);
}
