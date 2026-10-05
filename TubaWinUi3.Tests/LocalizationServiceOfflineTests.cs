using System;
using System.IO;
using System.Threading.Tasks;
using WinUI3Localizer;
using Xunit;

namespace TubaWinUi3.Tests
{
    /// <summary>
    /// 离线验证 WinUI3Localizer 的资源装载与语言切换核心行为（不启动 GUI）：
    /// LocalizerBuilder 从 Strings/&lt;lang&gt;/Resources.resw 读取词条，SetLanguage 后
    /// GetLocalizedString 返回目标语言值——即顶栏切换"实时生效"所依赖的库级机制。
    /// 使用独立临时目录，不触碰产品资源与用户数据。
    /// </summary>
    public class LocalizationServiceOfflineTests
    {
        private static string ReswXml(string value)
            => "<?xml version=\"1.0\" encoding=\"utf-8\"?>\r\n" +
               "<root>\r\n" +
               "  <data name=\"Test_SwitchKey\" xml:space=\"preserve\">\r\n" +
               "    <value>" + value + "</value>\r\n" +
               "  </data>\r\n" +
               "</root>\r\n";

        [Fact]
        public async Task LocalizerBuilder_LoadsResw_And_SwitchesLanguage_Offline()
        {
            string dir = Path.Combine(Path.GetTempPath(), "zxai-loc-offline-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(dir, "zh-CN"));
            Directory.CreateDirectory(Path.Combine(dir, "en-US"));
            File.WriteAllText(Path.Combine(dir, "zh-CN", "Resources.resw"), ReswXml("你好"));
            File.WriteAllText(Path.Combine(dir, "en-US", "Resources.resw"), ReswXml("Hello"));
            try
            {
                ILocalizer localizer = await new LocalizerBuilder()
                    .AddStringResourcesFolderForLanguageDictionaries(dir)
                    .SetOptions(options => options.DefaultLanguage = "zh-CN")
                    .Build();

                Assert.Equal("你好", localizer.GetLocalizedString("Test_SwitchKey"));

                await localizer.SetLanguage("en-US");
                Assert.Equal("Hello", localizer.GetLocalizedString("Test_SwitchKey"));

                await localizer.SetLanguage("zh-CN");
                Assert.Equal("你好", localizer.GetLocalizedString("Test_SwitchKey"));

                // 缺键行为：返回空串（产品侧 LocalizationService.L 借此回退 fallback）
                Assert.True(string.IsNullOrEmpty(localizer.GetLocalizedString("No_Such_Key")));
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch { }
            }
        }
    }
}
