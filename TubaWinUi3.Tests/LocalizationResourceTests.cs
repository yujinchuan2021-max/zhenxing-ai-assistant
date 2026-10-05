using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Xunit;

namespace TubaWinUi3.Tests
{
    /// <summary>
    /// V0.1 中英资源一致性：双语 RESW 键集一致、无重复键、占位符一致；
    /// XAML 中全部 l:Uids.Uid 与代码中全部 L() / Ui() 字面量键在双语资源中都有条目。
    /// 口径：只读仓库内源码与 Strings/*/Resources.resw，不启动 GUI、不联网。
    /// </summary>
    public class LocalizationResourceTests
    {
        private static string WinUi3Root => Path.Combine(FontSingleSourceTests.RepoRoot, "TubaWinUi3.WinUI3");
        private static string ZhResw => Path.Combine(WinUi3Root, "Strings", "zh-CN", "Resources.resw");
        private static string EnResw => Path.Combine(WinUi3Root, "Strings", "en-US", "Resources.resw");

        private static Dictionary<string, string> LoadResw(string path)
        {
            var doc = XDocument.Load(path);
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var data in doc.Root!.Elements("data"))
            {
                string name = data.Attribute("name")!.Value;
                string value = data.Element("value")?.Value ?? string.Empty;
                result[name] = value;
            }
            return result;
        }

        private static IEnumerable<string> EnumerateSourceFiles(string root, string extension)
            => Directory.EnumerateFiles(root, "*" + extension, SearchOption.AllDirectories)
                .Where(p => !p.Contains(@"\obj\") && !p.Contains(@"\bin\") && !p.Contains(@"\artifacts\"));

        [Fact]
        public void BilingualResw_HaveIdenticalKeySets()
        {
            var zh = LoadResw(ZhResw);
            var en = LoadResw(EnResw);
            var zhOnly = zh.Keys.Except(en.Keys).OrderBy(k => k, StringComparer.Ordinal).ToArray();
            var enOnly = en.Keys.Except(zh.Keys).OrderBy(k => k, StringComparer.Ordinal).ToArray();
            Assert.True(zhOnly.Length == 0, "仅中文资源存在: " + string.Join(", ", zhOnly.Take(20)));
            Assert.True(enOnly.Length == 0, "仅英文资源存在: " + string.Join(", ", enOnly.Take(20)));
        }

        [Fact]
        public void BilingualResw_HaveNoDuplicateNames()
        {
            foreach (var path in new[] { ZhResw, EnResw })
            {
                var names = XDocument.Load(path).Root!.Elements("data").Select(d => d.Attribute("name")!.Value).ToArray();
                var dups = names.GroupBy(n => n).Where(g => g.Count() > 1).Select(g => g.Key).ToArray();
                Assert.True(dups.Length == 0, Path.GetFileName(Path.GetDirectoryName(path)) + " 重复键: " + string.Join(", ", dups));
            }
        }

        [Fact]
        public void BilingualResw_PlaceholdersMatch()
        {
            var zh = LoadResw(ZhResw);
            var en = LoadResw(EnResw);
            var mismatches = new List<string>();
            foreach (var pair in zh)
            {
                if (!en.TryGetValue(pair.Key, out var enVal)) continue;
                var a = Regex.Matches(pair.Value, @"\{\d+\}").Select(m => m.Value).OrderBy(x => x, StringComparer.Ordinal);
                var b = Regex.Matches(enVal, @"\{\d+\}").Select(m => m.Value).OrderBy(x => x, StringComparer.Ordinal);
                if (!a.SequenceEqual(b)) mismatches.Add(pair.Key);
            }
            Assert.True(mismatches.Count == 0, "占位符不一致: " + string.Join(", ", mismatches.Take(20)));
        }

        [Fact]
        public void EveryXamlUid_HasBilingualEntries()
        {
            var zh = LoadResw(ZhResw);
            var en = LoadResw(EnResw);
            var missing = new List<string>();
            var uidRe = new Regex("Uids\\.Uid=\"([^\"]+)\"");
            foreach (var file in EnumerateSourceFiles(WinUi3Root, ".xaml"))
            {
                var text = File.ReadAllText(file);
                foreach (Match m in uidRe.Matches(text))
                {
                    string uid = m.Groups[1].Value;
                    bool Has(Dictionary<string, string> d)
                        => d.ContainsKey(uid) || d.Keys.Any(k => k.StartsWith(uid + ".", StringComparison.Ordinal));
                    if (!Has(zh) || !Has(en))
                        missing.Add(Path.GetFileName(file) + ":" + uid);
                }
            }
            Assert.True(missing.Count == 0, "Uid 缺资源: " + string.Join("; ", missing.Take(20)));
        }

        [Fact]
        public void EveryCodeLocalizedKey_HasBilingualEntries()
        {
            var zh = LoadResw(ZhResw);
            var en = LoadResw(EnResw);
            var lRe = new Regex("L\\(\\s*\"([A-Za-z0-9_.]+)\"");
            var uiRe = new Regex("Ui\\(\\s*\"([A-Za-z0-9_.]+)\"");
            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var file in EnumerateSourceFiles(WinUi3Root, ".cs"))
            {
                var text = File.ReadAllText(file);
                foreach (Match m in lRe.Matches(text)) keys.Add(m.Groups[1].Value);
                foreach (Match m in uiRe.Matches(text)) keys.Add("AiAgent_" + m.Groups[1].Value);
            }
            keys.RemoveWhere(k => k.EndsWith("_", StringComparison.Ordinal)); // 字符串拼接前缀残片
            var missing = keys.Where(k => !zh.ContainsKey(k) || !en.ContainsKey(k))
                              .OrderBy(k => k, StringComparer.Ordinal).ToArray();
            Assert.True(missing.Length == 0, "代码键缺资源: " + string.Join(", ", missing.Take(20)));
        }

        [Fact]
        public void MainPathKeys_AreCoveredInBothLanguages()
        {
            var zh = LoadResw(ZhResw);
            var en = LoadResw(EnResw);
            var keys = new[]
            {
                "App_Title", "MainWindow_SplashTitle.Text", "Shell_LanguageToggle",
                "Shell_NavHome", "Shell_NavFavorites", "Shell_NavAiTools", "Shell_NavTubaTools",
                "Wizard_Step0Title", "Wizard_Next", "Wizard_Finish",
                "Settings_UpdateChecking", "Settings_UpdateFound", "Settings_UpdateUpToDate", "Settings_UpdateFailed",
                "UpdateBanner_NewPortableVersion", "UpdateBanner_DownloadPackageButton", "UpdateBanner_PortableReady",
                "AiAgent_WelcomeTitle", "AiAgent_InputPlaceholder", "AiCategory_assistant_Title",
            };
            var missing = keys.Where(k => !zh.ContainsKey(k) || !en.ContainsKey(k)).ToArray();
            Assert.True(missing.Length == 0, "主路径键缺资源: " + string.Join(", ", missing));
        }
    }
}
