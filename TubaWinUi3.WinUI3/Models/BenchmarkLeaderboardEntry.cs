using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace TubaWinUi3.Models;

public sealed class BenchmarkLeaderboardEntry
{
    public int Rank { get; set; }

    public BenchmarkReportEntry Report { get; set; } = new();

    public string SortBy { get; set; } = "gaming";
    public int SelectedScore => SortBy switch
    {
        "office" => Report.OfficeScore, "cpu" => Report.CpuMultiCoreScore, "gpu" => Report.GpuRenderScore,
        "memory" => Report.MemoryCapacityScore, "disk" => Report.DiskSeqReadScore,
        "browser" => Report.BrowserTotalScore, "win" => Report.WinFinalScore, _ => Report.GamingScore
    };
    public string SelectedScoreLabel => Services.PerfTexts.T(SortBy switch
    {
        "office" => "办公性能", "cpu" => "CPU多核", "gpu" => "GPU渲染", "memory" => "内存容量",
        "disk" => "硬盘读", "browser" => "浏览器", "win" => "Win 性能", _ => "游戏性能"
    });

    public Brush RankBrush
    {
        get
        {
            switch (Rank)
            {
                case 1:
                    return new SolidColorBrush(Color.FromArgb(255, 212, 175, 55));
                case 2:
                    return new SolidColorBrush(Color.FromArgb(255, 192, 192, 192));
                case 3:
                    return new SolidColorBrush(Color.FromArgb(255, 205, 127, 50));
                default:
                    if (Microsoft.UI.Xaml.Application.Current.Resources.TryGetValue("AccentTextFillColorPrimaryBrush", out object value) && value is Brush brush)
                        return brush;
                    return new SolidColorBrush(Color.FromArgb(255, 0, 99, 177));
            }
        }
    }
}
