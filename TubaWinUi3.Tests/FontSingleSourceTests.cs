using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using TubaWinUi3.Services;
using Xunit;

namespace TubaWinUi3.Tests
{
    /// <summary>
    /// 字体单一入口（Assets/Fonts/app-font.json，v2 目录化）的校验、候选选择解析、生成物一致性与源码字面量审计。
    /// 口径：json = 唯一权威（choices[] 顺序 = 设置页列表顺序，首项 = 默认；mono = 等宽独立；uiStackSuffix = UI 栈回退尾）；
    /// AppFontFallback.g.cs / app-font.css / app-font-package.props = 生成物（scripts/generate-app-font.py），
    /// 本组测试钉住其与 json 不漂移；App.xaml 由 &lt;svc:AppFontDictionary/&gt;（解析期按已保存选择注入）承接，
    /// 结构由本组测试钉住；页面/服务不得再写字体字面量。
    /// 整套测试与宿主用例串行（同一 Collection），避免并发触碰 AppFonts 进程级状态。
    /// </summary>
    [Collection("FontSingleSource")]
    public class FontSingleSourceTests
    {
        internal static string RepoRoot { get; } = FindRepoRoot();
        internal const string WinUi3Rel = "TubaWinUi3.WinUI3";
        private static string FontsDir => Path.Combine(RepoRoot, WinUi3Rel, "Assets", "Fonts");

        internal static string FindRepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null)
            {
                if (File.Exists(Path.Combine(dir.FullName, WinUi3Rel, "TubaWinUi3.csproj")))
                    return dir.FullName;
                dir = dir.Parent;
            }
            throw new InvalidOperationException("未找到仓库根（缺少 TubaWinUi3.WinUI3/TubaWinUi3.csproj）");
        }

        // ---------- 1) 配置加载与负控 ----------

        [Fact]
        public void LoadFrom_ValidCatalog_ReadsAllFields()
        {
            var (root, dir) = NewTempFontsDir();
            try
            {
                WriteValidCatalog(dir);
                var cat = AppFontCatalog.LoadFrom(dir);
                Assert.Equal(2, cat.Choices.Count);
                Assert.Equal("alpha", cat.Choices[0].Id);
                Assert.Equal("候选甲", cat.Choices[0].DisplayName);
                Assert.Equal("Alpha Family", cat.Choices[0].FamilyName);
                Assert.Equal("Alpha-Regular.ttf", cat.Choices[0].RegularFileName);
                Assert.Equal("Alpha-Bold.ttf", cat.Choices[0].BoldFileName);
                Assert.Equal("Alpha-OFL.txt", cat.Choices[0].LicenseFileName);
                Assert.Equal("Alpha Author", cat.Choices[0].Attribution.Author);
                Assert.Equal("https://example.com/alpha", cat.Choices[0].Attribution.Url);
                Assert.Equal("甲许可", cat.Choices[0].Attribution.LicenseText);
                Assert.Equal("'Alpha Family', sans-serif", cat.UiStackSuffix);
                Assert.Equal("'Mono Family', monospace", cat.MonoStack);
                Assert.Equal("Mono Family", cat.Mono.FamilyName);
                Assert.Equal("Mono-Regular.ttf", cat.Mono.RegularFileName);
                Assert.Equal("alpha", cat.Find("alpha")!.Id);
                Assert.Null(cat.Find("nope"));
                Assert.Null(cat.Find(null));
            }
            finally { TryDelete(root); }
        }

        [Theory]
        [InlineData("nojson", "字体配置不存在")]
        [InlineData("badjson", "不是合法 JSON")]
        [InlineData("nochoices", "choices")]
        [InlineData("emptychoices", "choices")]
        [InlineData("badid", ".id")]
        [InlineData("badid2", ".id")]
        [InlineData("dupid", "重复")]
        [InlineData("blankdisplay", "displayName")]
        [InlineData("badfamily", "familyName")]
        [InlineData("commafamily", "familyName")]
        [InlineData("regulartraversal", "留在 Fonts 目录内")]
        [InlineData("regularfile", "不存在")]
        [InlineData("badattrurl", "attribution.url 必须是")]
        [InlineData("blankauthor", "attribution.author")]
        [InlineData("attrcontrol", "控制字符")]
        [InlineData("nomono", "mono")]
        [InlineData("monofile", "不存在")]
        [InlineData("monostackmismatch", "monoStack")]
        [InlineData("blankmonostack", "monoStack")]
        [InlineData("suffixcontrol", "uiStackSuffix")]
        public void LoadFrom_InvalidConfigs_ThrowInvalidData(string scenario, string expectedFragment)
        {
            var (root, dir) = NewTempFontsDir();
            try
            {
                WriteDummyFontFiles(dir);   // 先把基础文件就位：负控要打的是「配置缺陷」，不是「文件缺失」
                var json = ValidCatalogJson();
                switch (scenario)
                {
                    case "nojson":
                        File.Delete(Path.Combine(dir, "app-font.json"));
                        break;
                    case "badjson": File.WriteAllText(Path.Combine(dir, "app-font.json"), "{ not json"); break;
                    case "nochoices": json = json.Replace("\"choices\"", "\"choicesX\""); break;
                    case "emptychoices":
                        {
                            var ci = json.IndexOf("\"choices\"", StringComparison.Ordinal);
                            var lb = json.IndexOf('[', ci);
                            var rb = json.IndexOf(']', lb);
                            json = json[..lb] + "[]" + json[(rb + 1)..];
                        }
                        break;
                    case "badid": json = json.Replace("\"id\": \"alpha\"", "\"id\": \"Alpha!\" "); break;
                    case "badid2": json = json.Replace("\"id\": \"alpha\"", "\"id\": \"\" "); break;
                    case "dupid": json = json.Replace("\"id\": \"beta\"", "\"id\": \"alpha\""); break;
                    case "blankdisplay": json = json.Replace("\"displayName\": \"候选甲\"", "\"displayName\": \"\" "); break;
                    case "badfamily": json = json.Replace("\"familyName\": \"Alpha Family\"", "\"familyName\": \"A#B\" "); break;
                    case "commafamily": json = json.Replace("\"familyName\": \"Alpha Family\"", "\"familyName\": \"A,B\" "); break;
                    case "regulartraversal": json = json.Replace("\"regular\": \"Alpha-Regular.ttf\"", "\"regular\": \"../evil.ttf\" "); break;
                    case "regularfile": File.Delete(Path.Combine(dir, "Alpha-Regular.ttf")); break;
                    case "badattrurl": json = json.Replace("\"url\": \"https://example.com/alpha\"", "\"url\": \"not-a-url\" "); break;
                    case "blankauthor": json = json.Replace("\"author\": \"Alpha Author\"", "\"author\": \"\" "); break;
                    case "attrcontrol": json = json.Replace("\"licenseText\": \"甲许可\"", "\"licenseText\": \"甲\\u0001许可\" "); break;
                    case "nomono": json = json.Replace("\"mono\":", "\"monoX\":"); break;
                    case "monofile": File.Delete(Path.Combine(dir, "Mono-Regular.ttf")); break;
                    case "monostackmismatch": json = json.Replace("\"monoStack\": \"'Mono Family', monospace\"", "\"monoStack\": \"'Other', monospace\" "); break;
                    case "blankmonostack": json = json.Replace("\"monoStack\": \"'Mono Family', monospace\"", "\"monoStack\": \"\" "); break;
                    case "suffixcontrol": json = json.Replace("\"uiStackSuffix\": \"'Alpha Family', sans-serif\"", "\"uiStackSuffix\": \"bad\\u0001suffix\" "); break;
                }
                if (!File.Exists(Path.Combine(dir, "app-font.json")) && !scenario.Equals("nojson", StringComparison.Ordinal))
                    File.WriteAllText(Path.Combine(dir, "app-font.json"), json);
                else if (!scenario.Equals("nojson", StringComparison.Ordinal) && !scenario.Equals("badjson", StringComparison.Ordinal))
                    File.WriteAllText(Path.Combine(dir, "app-font.json"), json);

                var ex = Assert.Throws<InvalidDataException>(() => AppFontCatalog.LoadFrom(dir));
                Assert.Contains(expectedFragment, ex.Message);
            }
            finally { TryDelete(root); }
        }

        [Theory]
        [InlineData("'Maple Mono CN', sans-serif", "Maple Mono CN")]
        [InlineData("\"A B\", serif", "A B")]
        [InlineData("X", "X")]
        public void PrimaryFamilyOf_ParsesFirstFamily(string stack, string expected)
            => Assert.Equal(expected, AppFontSpec.PrimaryFamilyOf(stack));

        // ---------- 2) 真实仓库配置：合法 + 顺序/默认钉死 + 与编译期镜像一致 ----------

        [Fact]
        public void RepoConfig_ChoiceOrderAndDefaults_ArePinned()
        {
            var cat = AppFontCatalog.LoadFrom(FontsDir); // 文件缺失/非法会直接抛
            Assert.Equal(new[] { "sarasa", "noto", "harmony" }, cat.Choices.Select(c => c.Id).ToArray());
            Assert.Equal(new[] { "更纱黑体 Sarasa UI SC", "Noto Sans SC", "HarmonyOS Sans SC" },
                cat.Choices.Select(c => c.DisplayName).ToArray());
            // 默认（无已保存选择）= 首项 = 更纱
            Assert.Equal("sarasa", cat.Choices[0].Id);
            Assert.Equal("Sarasa UI SC", cat.Choices[0].FamilyName);
            Assert.Equal("Noto Sans SC", cat.Choices[1].FamilyName);
            Assert.Equal("HarmonyOS Sans SC", cat.Choices[2].FamilyName);
            // 持久化键值本身也钉住（改名会让已保存的选择失效）
            Assert.Equal("UiFontChoice", AppFonts.UiFontChoiceKey);
            // 等宽独立（Maple 保持随包）
            Assert.Equal("Maple Mono CN", cat.Mono.FamilyName);
            Assert.Equal("'Maple Mono CN', 'Cascadia Mono', 'Consolas', 'Courier New', ui-monospace, monospace", cat.MonoStack);
            // OTF 的 css format 不得写死 truetype（Noto = OTF/CFF）
            Assert.EndsWith(".otf", cat.Choices[1].RegularFileName, StringComparison.OrdinalIgnoreCase);
            Assert.Equal("truetype", AppFonts.FormatOf(cat.Choices[0].RegularFileName));
            Assert.Equal("opentype", AppFonts.FormatOf(cat.Choices[1].RegularFileName));
        }

        [Fact]
        public void RepoConfig_IsValid_And_MatchesFallbackMirror()
        {
            var cat = AppFontCatalog.LoadFrom(FontsDir);
            var mirror = AppFontCatalog.Fallback;
            Assert.Equal(mirror.UiStackSuffix, cat.UiStackSuffix);
            Assert.Equal(mirror.MonoStack, cat.MonoStack);
            Assert.Equal(mirror.Mono.FamilyName, cat.Mono.FamilyName);
            Assert.Equal(mirror.Mono.RegularFileName, cat.Mono.RegularFileName);
            Assert.Equal(mirror.Mono.BoldFileName, cat.Mono.BoldFileName);
            Assert.Equal(mirror.Mono.LicenseFileName, cat.Mono.LicenseFileName);
            Assert.Equal(mirror.Choices.Count, cat.Choices.Count);
            for (var i = 0; i < cat.Choices.Count; i++)
            {
                var m = mirror.Choices[i];
                var c = cat.Choices[i];
                Assert.Equal(m.Id, c.Id);
                Assert.Equal(m.DisplayName, c.DisplayName);
                Assert.Equal(m.FamilyName, c.FamilyName);
                Assert.Equal(m.RegularFileName, c.RegularFileName);
                Assert.Equal(m.BoldFileName, c.BoldFileName);
                Assert.Equal(m.LicenseFileName, c.LicenseFileName);
                Assert.Equal(m.Attribution.Author, c.Attribution.Author);
                Assert.Equal(m.Attribution.Url, c.Attribution.Url);
                Assert.Equal(m.Attribution.LicenseText, c.Attribution.LicenseText);
            }
        }

        // ---------- 3) 生成物与配置一致（不过期检查；字节级幂等另跑 generate-app-font.py --check） ----------

        [Fact]
        public void GeneratedArtifacts_MatchConfig()
        {
            var cat = AppFontCatalog.LoadFrom(FontsDir);

            var css = File.ReadAllText(Path.Combine(FontsDir, "app-font.css"));
            foreach (var c in cat.Choices)
            {
                Assert.Contains($"font-family: \"{c.FamilyName}\";", css);
                Assert.Contains($"url(\"{c.RegularFileName}\")", css);
                Assert.Contains($"url(\"{c.BoldFileName}\")", css);
                Assert.Contains($"format(\"{AppFonts.FormatOf(c.RegularFileName)}\")", css);
            }
            Assert.Contains("font-weight: 400", css);
            Assert.Contains("font-weight: 700", css);
            Assert.Contains(cat.Choices[0].FamilyName, css);
            Assert.Contains(cat.UiStackSuffix, css);
            Assert.Contains(cat.MonoStack, css);

            var gcs = File.ReadAllText(Path.Combine(RepoRoot, WinUi3Rel, "Services", "AppFontFallback.g.cs"));
            foreach (var c in cat.Choices)
            {
                Assert.Contains($"Id = \"{c.Id}\"", gcs);
                Assert.Contains($"DisplayName = \"{c.DisplayName}\"", gcs);
                Assert.Contains($"FamilyName = \"{c.FamilyName}\"", gcs);
                Assert.Contains($"RegularFileName = \"{c.RegularFileName}\"", gcs);
                Assert.Contains($"BoldFileName = \"{c.BoldFileName}\"", gcs);
                Assert.Contains($"LicenseFileName = \"{c.LicenseFileName}\"", gcs);
                Assert.Contains($"Author = \"{c.Attribution.Author}\"", gcs);
                Assert.Contains($"Url = \"{c.Attribution.Url}\"", gcs);
                Assert.Contains($"LicenseText = \"{c.Attribution.LicenseText}\"", gcs);
            }
            Assert.Contains(cat.MonoStack, gcs);
            Assert.Contains(cat.Mono.FamilyName, gcs);
        }

        // ---------- 3b) 兼容版随包清单（app-font-package.props，生成物）与配置一致 ----------

        [Fact]
        public void CompatPackageManifest_MatchesConfig()
        {
            var compatDir = Path.Combine(RepoRoot, "TubaWinUi3.Compatible");
            var propsPath = Path.Combine(compatDir, "app-font-package.props");
            Assert.True(File.Exists(propsPath), "兼容版随包清单缺失（应由生成器产出）：" + propsPath);
            var props = File.ReadAllText(propsPath);
            var cat = AppFontCatalog.LoadFrom(FontsDir);

            // 清单条目 = json + 每个候选的 regular/bold/license（按目录顺序），链接路径逐一对应
            var items = new List<string> { "app-font.json" };
            foreach (var c in cat.Choices) items.AddRange(new[] { c.RegularFileName, c.BoldFileName, c.LicenseFileName });
            Assert.Equal(items.Count, Regex.Matches(props, "<Content Include=").Count);
            foreach (var name in items)
            {
                Assert.Contains($"<Link>Assets\\Fonts\\{name}</Link>", props);
                Assert.Contains($"TubaWinUi3.WinUI3\\Assets\\Fonts\\{name}", props);
            }
            Assert.Contains("NotoSansSC-Regular.otf", props);
            Assert.Contains("HarmonyOS_Sans_SC_Regular.ttf", props);
            Assert.Contains("SarasaUiSC-Regular.ttf", props);

            // 清单必须真的被兼容版工程导入（否则只是死文件）
            var csproj = File.ReadAllText(Path.Combine(compatDir, "TubaWinUi3.Compatible.csproj"));
            Assert.Contains("app-font-package.props", csproj);
        }

        // ---------- 3c) 生成器可复核：--check / --selftest 真实进程 ----------

        [Fact]
        public void Generator_CheckAndSelftest_Pass()
        {
            var check = RunGenerator("--check");
            Assert.True(check.ExitCode == 0,
                $"generate-app-font.py --check 非零退出（{check.ExitCode}）——生成物与 app-font.json 失配：\n{check.Output}");
            Assert.Contains("[CHECK-OK]", check.Output);

            var selftest = RunGenerator("--selftest");
            Assert.True(selftest.ExitCode == 0,
                $"generate-app-font.py --selftest 非零退出（{selftest.ExitCode}）：\n{selftest.Output}");
            Assert.Contains("[SELFTEST-OK]", selftest.Output);
        }

        private static (int ExitCode, string Output) RunGenerator(string args)
        {
            var script = Path.Combine(RepoRoot, "scripts", "generate-app-font.py");
            Assert.True(File.Exists(script), "生成器脚本缺失：" + script);
            var explicitPython = Environment.GetEnvironmentVariable("TUBA_TEST_PYTHON");
            if (!string.IsNullOrWhiteSpace(explicitPython))
            {
                Assert.True(File.Exists(explicitPython), "TUBA_TEST_PYTHON 指定的解释器文件不存在：" + explicitPython);
                try
                {
                    var probe = RunPythonProcess(explicitPython, false, "-c", "import sys; print(sys.version_info.major)");
                    Assert.True(probe.ExitCode == 0 && probe.Output.Trim() == "3",
                        $"TUBA_TEST_PYTHON 指定的解释器不可用或不是 Python 3（{probe.ExitCode}）：{probe.Output}");
                    return RunPythonProcess(explicitPython, false, script, args);
                }
                catch (System.ComponentModel.Win32Exception ex)
                {
                    Assert.Fail("TUBA_TEST_PYTHON 指定的解释器无法执行：" + explicitPython + "\n" + ex.Message);
                    return default;
                }
            }

            foreach (var candidate in new[] { "python", "python3", "py" })
            {
                try
                {
                    // 先只探测解释器，排除 Windows Store 的 Python 占位别名；
                    // 一旦解释器有效，生成器的任何非零退出都原样返回，不能换解释器吞掉失败。
                    var probe = RunPythonProcess(candidate, candidate == "py", "-c", "import sys; print(sys.version_info.major)");
                    if (probe.ExitCode == 9009) continue;
                    Assert.True(probe.ExitCode == 0 && probe.Output.Trim() == "3",
                        $"Python 解释器探测失败（{candidate}, {probe.ExitCode}）：{probe.Output}");
                    return RunPythonProcess(candidate, candidate == "py", script, args);
                }
                catch (System.ComponentModel.Win32Exception)
                {
                    // 该解释器不可用，尝试下一个
                }
            }
            Assert.Fail("未找到可用的 Python 解释器（python/python3/py -3）——生成器可复核检查需要 Python");
            return default;
        }

        private static (int ExitCode, string Output) RunPythonProcess(string executable, bool usePyLauncher, params string[] arguments)
        {
            var psi = new System.Diagnostics.ProcessStartInfo(executable)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = System.Text.Encoding.UTF8,
                StandardErrorEncoding = System.Text.Encoding.UTF8,
                CreateNoWindow = true,
                WorkingDirectory = RepoRoot,
            };
            psi.Environment["PYTHONIOENCODING"] = "utf-8";
            if (usePyLauncher) psi.ArgumentList.Add("-3");
            foreach (var argument in arguments) psi.ArgumentList.Add(argument);
            using var p = System.Diagnostics.Process.Start(psi);
            Assert.NotNull(p);
            var stdout = p.StandardOutput.ReadToEndAsync();
            var stderr = p.StandardError.ReadToEndAsync();
            Assert.True(p.WaitForExit(120_000), $"Python 运行超时（{executable}）");
            return (p.ExitCode, stdout.GetAwaiter().GetResult() + stderr.GetAwaiter().GetResult());
        }

        // ---------- 4) 候选选择：settings.json 读取与解析（纯字符串断言，不触发 WinUI 类型） ----------

        [Fact]
        public void SavedChoice_ReadFromSettingsFile()
        {
            var dir = Path.Combine(Path.GetTempPath(), "zxai-font-settings-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(dir);
            try
            {
                var p = Path.Combine(dir, "settings.json");
                File.WriteAllText(p, "{}");
                Assert.Null(AppFonts.ReadSavedChoiceIdFrom(p));
                File.WriteAllText(p, "{ \"UiFontChoice\": \"noto\", \"Other\": \"x\" }");
                Assert.Equal("noto", AppFonts.ReadSavedChoiceIdFrom(p));
                File.WriteAllText(p, "{ \"UiFontChoice\": \"  harmony  \" }");
                Assert.Equal("harmony", AppFonts.ReadSavedChoiceIdFrom(p));
                File.WriteAllText(p, "{ \"UiFontChoice\": \"\" }");
                Assert.Null(AppFonts.ReadSavedChoiceIdFrom(p));
                File.WriteAllText(p, "{ \"UiFontChoice\": 123 }");
                Assert.Null(AppFonts.ReadSavedChoiceIdFrom(p));
                File.WriteAllText(p, "{ not json");
                Assert.Null(AppFonts.ReadSavedChoiceIdFrom(p));
                Assert.Null(AppFonts.ReadSavedChoiceIdFrom(Path.Combine(dir, "missing.json")));
                Assert.Null(AppFonts.ReadSavedChoiceIdFrom(""));
            }
            finally { TryDelete(dir); }
        }

        [Fact]
        public void PrepareForStartup_ResolvesChoice_NeverThrows()
        {
            try
            {
                AppFonts.ResetCachesForTest();
                var cat = AppFontCatalog.LoadFrom(FontsDir);

                var uri1 = AppFonts.PrepareForStartup("noto");
                Assert.Equal(AppFonts.BuildXamlFontUri(cat.Choices[1]), uri1);
                Assert.Equal("noto", AppFonts.ChoiceId);

                var uriDefault = AppFonts.PrepareForStartup(null);
                Assert.Equal(AppFonts.BuildXamlFontUri(cat.Choices[0]), uriDefault);
                Assert.Equal("sarasa", AppFonts.ChoiceId);

                var uriUnknown = AppFonts.PrepareForStartup("unknown-id");
                Assert.Equal(AppFonts.BuildXamlFontUri(cat.Choices[0]), uriUnknown);
                Assert.Equal("sarasa", AppFonts.ChoiceId);

                var uriBlank = AppFonts.PrepareForStartup("   ");
                Assert.Equal(AppFonts.BuildXamlFontUri(cat.Choices[0]), uriBlank);
            }
            finally { AppFonts.ResetCachesForTest(); }
        }

        // ---------- 4b) AppFonts 对外 API 全部由配置推导（生效候选随 SetChoice 变化） ----------

        [Fact]
        public void AppFonts_DerivedValues_FollowEffectiveChoice()
        {
            try
            {
                AppFonts.ResetCachesForTest();
                AppFonts.InitializeFromDirectory(Path.Combine(RepoRoot, WinUi3Rel));
                Assert.Null(AppFonts.LoadError);

                var cat = AppFontCatalog.LoadFrom(FontsDir);
                Assert.Equal(cat.Choices.Count, AppFonts.Choices.Count);

                foreach (var c in cat.Choices)
                {
                    AppFonts.SetChoice(c.Id);
                    Assert.Equal(c.Id, AppFonts.ChoiceId);
                    Assert.Equal(c.FamilyName, AppFonts.FamilyName);
                    Assert.Equal(c.RegularFileName, Path.GetFileName(AppFonts.RegularFilePath));
                    Assert.Equal(c.BoldFileName, Path.GetFileName(AppFonts.BoldFilePath));
                    Assert.Equal(c.LicenseFileName, Path.GetFileName(AppFonts.LicensePath));
                    Assert.Equal(AppFonts.BuildXamlFontUri(c), AppFonts.XamlFontUri);
                    Assert.Equal(c.Attribution.Author, AppFonts.AttributionAuthor);
                    Assert.Equal(c.Attribution.Url, AppFonts.AttributionUrl);
                    Assert.Equal(c.Attribution.LicenseText, AppFonts.AttributionLicenseText);
                    Assert.True(File.Exists(AppFonts.RegularFilePath), "Regular 字体缺失：" + AppFonts.RegularFilePath);
                    Assert.True(File.Exists(AppFonts.BoldFilePath), "Bold 字体缺失：" + AppFonts.BoldFilePath);
                    Assert.True(File.Exists(AppFonts.LicensePath), "许可文件缺失：" + AppFonts.LicensePath);
                    Assert.Equal($"'{c.FamilyName}', {cat.UiStackSuffix}", AppFonts.WebFontStack);
                }

                AppFonts.SetChoice(null);
                Assert.Equal(cat.Choices[0].Id, AppFonts.ChoiceId);

                Assert.Equal(cat.MonoStack, AppFonts.WebMonoStack);
                Assert.Equal(0, AppFonts.IndexOfChoice(cat.Choices[0].Id));
                Assert.Equal(2, AppFonts.IndexOfChoice(cat.Choices[2].Id));
                Assert.Equal(-1, AppFonts.IndexOfChoice("nope"));
                Assert.Equal(-1, AppFonts.IndexOfChoice(null));

                // Web @font-face：覆盖全部候选 + 等宽；format 由扩展名推导
                var face = AppFonts.WebFontFaceCss("/fonts");
                foreach (var c in cat.Choices)
                {
                    Assert.Contains($"/fonts/{c.RegularFileName}", face);
                    Assert.Contains($"/fonts/{c.BoldFileName}", face);
                    Assert.Contains($"format('{AppFonts.FormatOf(c.RegularFileName)}')", face);
                }
                Assert.Contains($"/fonts/{cat.Mono.RegularFileName}", face);

                // Web 注入脚本：生效栈（JS 转义后原样出现）
                AppFonts.SetChoice("noto");
                var script = AppFonts.WebFontOverrideScript();
                Assert.Contains("--app-font", script);
                var jsStack = AppFonts.WebFontStack.Replace("\\", "\\\\").Replace("'", "\\'");
                Assert.Contains(jsStack, script);

                // 导出报告 face：生效候选（相对 + 绝对兜底）
                var export = AppFonts.ExportFontFaceCss();
                Assert.Contains(AppFonts.RegularFileName, export);
                Assert.Contains(AppFonts.BoldFileName, export);
                Assert.Contains("file:///", export);
                Assert.Contains($"format('{AppFonts.FormatOf(AppFonts.RegularFileName)}')", export);
            }
            finally { AppFonts.ResetCachesForTest(); }
        }

        [Fact]
        public void EnsureExportFont_CopiesEffectiveRegularBoldAndLicense()
        {
            var outDir = Path.Combine(Path.GetTempPath(), "zxai-font-export-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(outDir);
            try
            {
                AppFonts.ResetCachesForTest();
                AppFonts.InitializeFromDirectory(Path.Combine(RepoRoot, WinUi3Rel));
                AppFonts.SetChoice(null); // 默认（更纱）
                AppFonts.EnsureExportFont(outDir);
                var regular = Path.Combine(outDir, AppFonts.RegularFileName);
                var bold = Path.Combine(outDir, AppFonts.BoldFileName);
                var license = Path.Combine(outDir, AppFonts.LicenseFileName);
                Assert.True(File.Exists(regular), "Regular 未随附");
                Assert.True(File.Exists(bold), "Bold 未随附");
                Assert.True(File.Exists(license), "许可未随附");
                Assert.True(new FileInfo(regular).Length > 1_000_000, "Regular 文件过小，疑似复制失败");
            }
            finally
            {
                TryDelete(outDir);
                AppFonts.ResetCachesForTest();
            }
        }

        // ---------- 4c) 设置页接线：界面字体卡片 + 「开源与致谢」 = 单一入口 ----------

        [Fact]
        public void SettingsUiFontCard_IsWiredToCatalog()
        {
            // 源码接线：候选文本/顺序来自 AppFonts.Choices；保存键来自 AppFonts.UiFontChoiceKey；页面不得硬编码字体名
            var cs = File.ReadAllText(Path.Combine(RepoRoot, WinUi3Rel, "Pages", "SettingsPage.xaml.cs"));
            Assert.Contains("InitUiFontSettings();", cs);                      // 有调用点，不是死代码
            Assert.Contains("foreach (var choice in AppFonts.Choices)", cs);
            Assert.Contains("AppFonts.UiFontChoiceKey", cs);
            Assert.Contains("private void UiFontComboBox_SelectionChanged", cs);

            var xaml = File.ReadAllText(Path.Combine(RepoRoot, WinUi3Rel, "Pages", "SettingsPage.xaml"));
            Assert.Contains("x:Name=\"UiFontComboBox\"", xaml);
            Assert.Contains("SelectionChanged=\"UiFontComboBox_SelectionChanged\"", xaml);
            Assert.Contains("重启应用生效", xaml);                               // 重启生效的界面说明（需求）
            Assert.DoesNotContain("更纱", xaml);                                  // 候选文字不得出现在页面（来自目录）
            Assert.DoesNotContain("HarmonyOS", xaml);
            Assert.DoesNotContain("Noto", xaml);

            // 设置仅保留界面字体，不再提供截图水印字体设置。
            Assert.DoesNotContain("WatermarkFontComboBox", xaml);
            Assert.DoesNotContain("ScreenshotWatermark", cs);
        }

        [Fact]
        public void SettingsAttribution_PullsFromSingleSource()
        {
            // 值往返：设置页三控件绑定的属性全部由 app-font.json（生效候选）推导
            try
            {
                AppFonts.ResetCachesForTest();
                AppFonts.InitializeFromDirectory(Path.Combine(RepoRoot, WinUi3Rel));
                Assert.Null(AppFonts.LoadError);
                var cat = AppFontCatalog.LoadFrom(FontsDir);
                foreach (var c in cat.Choices)
                {
                    AppFonts.SetChoice(c.Id);
                    Assert.Equal(c.FamilyName, AppFonts.FamilyName);
                    Assert.Equal(c.Attribution.Author, AppFonts.AttributionAuthor);
                    Assert.Equal(c.Attribution.Url, AppFonts.AttributionUrl);
                    Assert.Equal(c.Attribution.LicenseText, AppFonts.AttributionLicenseText);
                    Assert.Equal($"{c.Attribution.Author} — 界面字体（{c.Attribution.LicenseText}）", AppFonts.AttributionDetail);
                    Assert.StartsWith("http", AppFonts.AttributionUrl);
                }
            }
            finally
            {
                AppFonts.SetChoice(null);
                AppFonts.ResetCachesForTest();
            }

            // 源码接线：ApplyFontCredit 的三个赋值必须来自 AppFonts（换选择即随动）；页面不得硬编码字体名
            var cs = File.ReadAllText(Path.Combine(RepoRoot, WinUi3Rel, "Pages", "SettingsPage.xaml.cs"));
            Assert.Contains("FontCreditName.Text = AppFonts.FamilyName;", cs);
            Assert.Contains("FontCreditDetail.Text = AppFonts.AttributionDetail;", cs);
            Assert.Contains("Uri.TryCreate(AppFonts.AttributionUrl", cs);
            Assert.Contains("ApplyFontCredit();", cs);

            var xaml = File.ReadAllText(Path.Combine(RepoRoot, WinUi3Rel, "Pages", "SettingsPage.xaml"));
            Assert.Contains("x:Name=\"FontCreditName\"", xaml);
            Assert.Contains("x:Name=\"FontCreditDetail\"", xaml);
            Assert.Contains("x:Name=\"FontCreditLink\"", xaml);
            Assert.DoesNotContain("Maple", xaml); // 署名卡区域不得残留字体名字面量
        }

        // ---------- 4d) App.xaml：资源字典根 = AppFontDictionary（解析期注入；无静态字体字面量） ----------

        [Fact]
        public void AppXaml_UsesFontDictionaryRoot()
        {
            var xamlPath = Path.Combine(RepoRoot, WinUi3Rel, "App.xaml");
            var xaml = File.ReadAllText(xamlPath);
            Assert.Contains("xmlns:svc=\"using:TubaWinUi3.Services\"", xaml);
            Assert.Contains("<svc:AppFontDictionary>", xaml);
            Assert.Contains("</svc:AppFontDictionary>", xaml);
            Assert.Contains("<svc:AppFontDictionary.MergedDictionaries>", xaml);
            Assert.DoesNotContain("APP-FONT", xaml);                       // 旧生成块已退场
            Assert.DoesNotContain("ms-appx:///Assets/Fonts/", xaml);       // 不再有静态字体 URI
            Assert.DoesNotContain("FontFamily x:Key", xaml);               // 字体键由构造函数注入
            Assert.Contains("<Setter Property=\"FontFamily\" Value=\"{ThemeResource AppFontFamily}\" />", xaml);
            Assert.Contains("ContentControlThemeFontSize", xaml);

            // 2026-09-23 实机启动崩溃教训（key=null → ResourceDictionary.Add）：
            // 自定义字典根的直接子项只允许「合并字典声明」或「带 x:Key 的条目」；
            // 无 x:Key 的隐式样式必须位于合并层的内置 ResourceDictionary 中。
            var doc = System.Xml.Linq.XDocument.Load(xamlPath);
            System.Xml.Linq.XNamespace xNs = "http://schemas.microsoft.com/winfx/2006/xaml";
            System.Xml.Linq.XNamespace svcNs = "using:TubaWinUi3.Services";
            var root = doc.Descendants(svcNs + "AppFontDictionary").Single();
            foreach (var child in root.Elements())
            {
                // MergedDictionaries 在 XML 里写作 <svc:AppFontDictionary.MergedDictionaries>
                var isMerged = child.Name.LocalName.EndsWith("MergedDictionaries", StringComparison.Ordinal);
                var hasKey = child.Attribute(xNs + "Key") != null;
                Assert.True(isMerged || hasKey,
                    $"AppFontDictionary 直接子项缺 x:Key（自定义字典根不允许隐式键条目，实机崩溃路径）：{child.Name.LocalName}");
            }
            var keylessStyles = doc.Descendants()
                .Where(e => e.Name.LocalName == "Style" && e.Attribute(xNs + "Key") == null)
                .ToList();
            Assert.NotEmpty(keylessStyles);   // 隐式样式仍要存在（全局 TextBlock/RichTextBlock 字体）
            foreach (var style in keylessStyles)
            {
                var inBuiltinDict = style.Ancestors()
                    .Any(a => a.Name.LocalName == "ResourceDictionary" && a.Name.NamespaceName != "using:TubaWinUi3.Services");
                Assert.True(inBuiltinDict,
                    "隐式样式必须包在内置 ResourceDictionary（合并层）里，不能直接挂在 svc:AppFontDictionary 根下");
            }

            var dictCs = File.ReadAllText(Path.Combine(RepoRoot, WinUi3Rel, "Services", "AppFontDictionary.cs"));
            Assert.Contains("class AppFontDictionary : ResourceDictionary", dictCs);
            Assert.Contains("\"ContentControlThemeFontFamily\"", dictCs);
            Assert.Contains("\"AppFontFamily\"", dictCs);
            Assert.Contains("AppFonts.PrepareForStartup", dictCs);
            Assert.Contains("AppFonts.TryReadSavedChoiceId", dictCs);
        }

        // ---------- 5) 源码字体字面量审计（允许清单显式列出，新增命中必须先说明理由） ----------

        private static readonly string[] FontLiteralTokens =
        {
            "Maple Mono CN", "MapleMono", "Segoe UI(?! Variable| Symbol)", "微软雅黑", "Microsoft YaHei",
            "Consolas", "Cascadia", "Noto Sans", "HarmonyOS", "MiSans", "XamlAutoFontFamily",
            "宋体", "黑体", "楷体", "仿宋", "PingFang",
        };

        /// <summary>允许清单：文件（相对仓库根）→ 允许出现的字面量（每个都有既定语义，见注释）。</summary>
        private static readonly Dictionary<string, string[]> LiteralAllowlist = new(StringComparer.OrdinalIgnoreCase)
        {
            // 机制说明注释里的“代码块 Consolas 不受影响”示例（非渲染取值）
            [@"TubaWinUi3.WinUI3\App.xaml"] = new[] { "Consolas" },
            // 注释中说明报告字体随附机制（机制文字，非渲染取值）
            [@"TubaWinUi3.WinUI3\Pages\HardwareDetailPage.xaml.cs"] = new[] { "Maple Mono CN" },
            // 截图水印字体候选列表（用户自选水印字体 = 独立语义，不经过界面字体目录）；
            // 另含「开源引用」字体署名/许可链接区（第三方字体名称仅作出处与许可展示文本，不是 UI 字体配置入口）
            [@"TubaWinUi3.WinUI3\Pages\SettingsPage.xaml.cs"] = new[] { "微软雅黑", "宋体", "黑体", "楷体", "仿宋", "Segoe UI", "Noto Sans", "HarmonyOS", "Maple Mono CN" },
            // GDI 私有字体加载失败时的系统回退（唯一例外）
            [@"TubaWinUi3.WinUI3\Services\AppFonts.cs"] = new[] { "Segoe UI" },
            // 注释：框架命名文本样式基类自带 XamlAutoFontFamily 字面量的陷阱说明
            [@"TubaWinUi3.WinUI3\Styles\FluentTokens.xaml"] = new[] { "XamlAutoFontFamily" },
            // 用户文档渲染引擎（内容字体独立语义，不在应用默认字体范围内）
            [@"TubaWinUi3.WinUI3\Assets\DocEngine\doceng.html"] = new[] { "Microsoft YaHei", "Segoe UI", "Consolas", "PingFang" },
            // 导出 .docx / 报表 HTML：在外部软件里渲染，包内字体到不了（保持系统字体栈）
            [@"TubaWinUi3.WinUI3\Services\DocxWriter.cs"] = new[] { "Consolas", "Microsoft YaHei" },
            [@"TubaWinUi3.WinUI3\Services\TabularConvert.cs"] = new[] { "Microsoft YaHei", "Segoe UI", "Consolas" },
            // WinForms 兼容版：私有字体加载失败时的系统回退（唯一例外；正常路径经 app-font.json + 字体文件加载）
            [@"TubaWinUi3.Compatible\Services\UiFonts.cs"] = new[] { "Microsoft YaHei" },
        };

        // 字体选择名称/署名/许可说明的翻译条目，只允许这三条完整的既有字典行。
        // 不允许在该文件的其他行增加字体配置或 FontFamily 字面量。
        private static readonly Dictionary<string, HashSet<string>> DisplayLiteralLineAllowlist = new(StringComparer.OrdinalIgnoreCase)
        {
            [@"TubaWinUi3.WinUI3\Services\MiscTexts.cs"] = new(StringComparer.Ordinal)
            {
                "[\"更纱黑体 Sarasa UI SC\"] = \"Sarasa UI SC\",",
                "[\"华为终端有限公司（HarmonyOS Sans）\"] = \"Huawei Device Co., Ltd. (HarmonyOS Sans)\",",
                "[\"HarmonyOS Sans Fonts License Agreement（华为自有许可，非开源；随包附协议原文）\"] = \"HarmonyOS Sans Fonts License Agreement (Huawei proprietary license, not open source; full text bundled)\",",
            },
        };

        [Fact]
        public void AppSources_NoStrayFontLiterals_OutsideAllowlist()
        {
            var regex = new Regex(string.Join("|", FontLiteralTokens.Select(t => "(" + t + ")")), RegexOptions.Compiled);
            var scanRoots = new[]
            {
                Path.Combine(RepoRoot, "TubaWinUi3.WinUI3"),
                Path.Combine(RepoRoot, "TubaWinUi3.Compatible"),
            };
            var exts = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".cs", ".xaml", ".html", ".css", ".js", ".json" };
            var skipDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "bin", "obj", "artifacts", "node_modules", "TestResults", "AppPackages", ".git", ".vs",
                "Tools",     // 随包第三方工具自带文件（非应用源码；如 ventoy 语言包）
                "DocEngine", // 用户文档渲染引擎（内容字体独立语义，font-maintenance.md §4.4/§7 例外区）
            };
            var generatedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "app-font.css", "app-font.json", "AppFontFallback.g.cs", // 生成物（由 RepoConfig/GeneratedArtifacts 测试钉住）
            };

            var hits = new List<string>();
            foreach (var root in scanRoots)
            {
                if (!Directory.Exists(root)) continue;
                foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                {
                    if (!exts.Contains(Path.GetExtension(file))) continue;
                    var parts = file.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                    if (parts.Any(p => skipDirs.Contains(p))) continue;
                    if (generatedFiles.Contains(Path.GetFileName(file))) continue;
                    if (Path.GetFileName(file).Contains(".min.", StringComparison.OrdinalIgnoreCase)) continue; // vendored 压缩库

                    var rel = Path.GetRelativePath(RepoRoot, file);
                    var allowed = LiteralAllowlist.TryGetValue(rel, out var list) ? list : Array.Empty<string>();
                    var lineNo = 0;
                    foreach (var line in File.ReadLines(file))
                    {
                        lineNo++;
                        if (DisplayLiteralLineAllowlist.TryGetValue(rel, out var displayLines) && displayLines.Contains(line.Trim())) continue;
                        foreach (Match m in regex.Matches(line))
                        {
                            if (allowed.Any(a => string.Equals(a, m.Value, StringComparison.OrdinalIgnoreCase))) continue;
                            var snippet = line.Trim();
                            if (snippet.Length > 160) snippet = snippet.Substring(0, 160) + "…";
                            hits.Add($"{rel}:{lineNo}: [{m.Value}] {snippet}");
                        }
                    }
                }
            }
            Assert.True(hits.Count == 0,
                "发现允许清单之外的字体字面量（若属独立语义/既定例外，请加入本测试的 LiteralAllowlist 并写明理由）：\n"
                + string.Join("\n", hits));
        }

        // ---------- 辅助 ----------

        private static (string Root, string FontsDir) NewTempFontsDir()
        {
            var root = Path.Combine(Path.GetTempPath(), "zxai-font-tests-" + Guid.NewGuid().ToString("N")[..10]);
            var fonts = Path.Combine(root, "Assets", "Fonts");
            Directory.CreateDirectory(fonts);
            return (root, fonts);
        }

        private static string ValidCatalogJson()
        {
            return """
            {
              "uiStackSuffix": "'Alpha Family', sans-serif",
              "monoStack": "'Mono Family', monospace",
              "mono": {
                "familyName": "Mono Family",
                "regular": "Mono-Regular.ttf",
                "bold": "Mono-Bold.ttf",
                "license": "Mono-License.txt"
              },
              "choices": [
                {
                  "id": "alpha",
                  "displayName": "候选甲",
                  "familyName": "Alpha Family",
                  "regular": "Alpha-Regular.ttf",
                  "bold": "Alpha-Bold.ttf",
                  "license": "Alpha-OFL.txt",
                  "attribution": { "author": "Alpha Author", "url": "https://example.com/alpha", "licenseText": "甲许可" }
                },
                {
                  "id": "beta",
                  "displayName": "候选乙",
                  "familyName": "Beta Family",
                  "regular": "Beta-Regular.ttf",
                  "bold": "Beta-Bold.ttf",
                  "license": "Beta-OFL.txt",
                  "attribution": { "author": "Beta Author", "url": "https://example.com/beta", "licenseText": "乙许可" }
                }
              ]
            }
            """;
        }

        private static void WriteValidCatalog(string fontsDir)
        {
            File.WriteAllText(Path.Combine(fontsDir, "app-font.json"), ValidCatalogJson());
            WriteDummyFontFiles(fontsDir);
        }

        private static void WriteDummyFontFiles(string fontsDir)
        {
            foreach (var name in new[]
                     {
                         "Alpha-Regular.ttf", "Alpha-Bold.ttf", "Alpha-OFL.txt",
                         "Beta-Regular.ttf", "Beta-Bold.ttf", "Beta-OFL.txt",
                         "Mono-Regular.ttf", "Mono-Bold.ttf", "Mono-License.txt",
                     })
            {
                File.WriteAllText(Path.Combine(fontsDir, name), "dummy");
            }
        }

        private static void TryDelete(string dir)
        {
            try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { }
        }
    }

    /// <summary>独立 WinUI 宿主（TubaWinUi3.XamlHostRunner）上的字体单一入口验收：F1 用例。</summary>
    [Collection("FontSingleSource")]
    public class FontSingleSourceHostTests
    {
        [Fact]
        public void F1_FontSingleSource_VisibleNodes()
        {
            var r = XamlHostProcess.RunCase("F1_FontSingleSource_VisibleNodes", 240);
            Assert.True(r.Passed, r.Details);
        }

        [Fact]
        public void F2_AppFontDictionary_InjectKeys()
        {
            var r = XamlHostProcess.RunCase("F2_AppFontDictionary_InjectKeys", 240);
            Assert.True(r.Passed, r.Details);
        }

        [Fact]
        public void F3_MergedImplicitStyle()
        {
            var r = XamlHostProcess.RunCase("F3_MergedImplicitStyle", 240);
            Assert.True(r.Passed, r.Details);
        }
    }
}
