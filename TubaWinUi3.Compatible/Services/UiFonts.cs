using System;
using System.Drawing;
using System.Drawing.Text;
using System.IO;
using Newtonsoft.Json.Linq;

namespace TubaWinUi3.Compatible.Services
{
    /// <summary>
    /// 兼容版字体统一入口：读取与主程序同一份 Assets/Fonts/app-font.json（唯一权威），
    /// 用 WinForms 私有字体集（PrivateFontCollection）按文件加载随包字体——不依赖系统安装；
    /// 任何环节缺失（json/字体文件缺失、解析失败）回退系统「Microsoft YaHei UI」。
    /// 图标字体（Segoe MDL2 Assets）保持独立，不经过本入口。
    /// </summary>
    internal static class UiFonts
    {
        private const string FallbackFamily = "Microsoft YaHei UI";

        private static readonly PrivateFontCollection Packaged;
        private static readonly FontFamily PackagedFamily;

        static UiFonts()
        {
            PrivateFontCollection pfc = null;
            FontFamily family = null;
            try
            {
                var fontsDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets", "Fonts");
                var specPath = Path.Combine(fontsDir, "app-font.json");
                if (File.Exists(specPath))
                {
                    var spec = JObject.Parse(File.ReadAllText(specPath));
                    pfc = new PrivateFontCollection();
                    foreach (var key in new[] { "regular", "bold" })
                    {
                        var name = (string)spec[key];
                        if (string.IsNullOrWhiteSpace(name)) continue;
                        // 与主程序同一配置校验口径：路径必须留在 Fonts 目录内
                        if (name != Path.GetFileName(name) || name.Contains("..")) continue;
                        var path = Path.Combine(fontsDir, name);
                        if (File.Exists(path)) pfc.AddFontFile(path);
                    }
                    if (pfc.Families.Length > 0) family = pfc.Families[0];
                }
            }
            catch
            {
                pfc = null;
                family = null;
            }
            Packaged = family != null ? pfc : null;
            PackagedFamily = family;
        }

        /// <summary>随包字体是否加载成功（诊断/报告用）。</summary>
        public static bool PackagedLoaded { get { return PackagedFamily != null; } }

        /// <summary>兼容版 UI 字体：随包字体可用时按文件加载（不依赖系统安装），否则回退系统雅黑。</summary>
        public static Font Create(float size, FontStyle style = FontStyle.Regular, bool bold = false)
        {
            if (bold) style = FontStyle.Bold;
            if (PackagedFamily != null)
            {
                try { return new Font(PackagedFamily, size, style); }
                catch { }
            }
            return new Font(FallbackFamily, size, style);
        }
    }
}
