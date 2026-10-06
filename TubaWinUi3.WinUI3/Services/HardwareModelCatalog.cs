using System;
using System.Collections.Generic;
using System.Linq;

namespace TubaWinUi3.Services;

public enum HardwareModelCategory
{
    Cpu,
    Motherboard,
    Gpu,
    Memory,
    Monitor,
    Disk
}

/// <summary>A suggested display name, with its manufacturer and official reference.</summary>
public sealed record HardwareModelPreset(
    HardwareModelCategory Category,
    string Name,
    string Manufacturer,
    string SourceUrl)
{
    public override string ToString() => Name;
}

/// <summary>
/// Offline suggestions for the configuration display editor. This is a curated set of
/// current and recent mainstream product families, not a compatibility or live stock list.
/// Memory and storage names identify product families without assuming a capacity or speed.
/// Only the display editor consumes these names; presets do not change hardware capabilities.
/// </summary>
public static class HardwareModelCatalog
{
    public static DateOnly VerifiedOn { get; } = new(2026, 10, 6);

    private const string IntelUltra = "https://www.intel.com/content/www/us/en/ark/products/series/241071/intel-core-ultra-processors-series-2.html";
    private const string IntelCore14 = "https://www.intel.com/content/www/us/en/newsroom/news/intel-core-14th-gen-desktop-processors.html";
    private const string AmdRyzen9000 = "https://www.amd.com/en/partner/articles/ryzen-9000-series-processors.html";
    private const string Nvidia50 = "https://www.nvidia.com/en-us/geforce/graphics-cards/50-series/";
    private const string Nvidia40 = "https://www.nvidia.com/en-us/geforce/graphics-cards/40-series/";
    private const string AmdRadeon9000 = "https://www.amd.com/en/products/specifications/graphics.html";
    private const string AmdRadeon7000 = "https://www.amd.com/en/products/graphics/desktops/radeon/7000-series.html";
    private const string IntelArc = "https://www.intel.com/content/www/us/en/ark/products/series/227960/intel-arc-dedicated-graphics-family.html";
    private const string CorsairMemory = "https://www.corsair.com/us/en/c/memory";
    private const string GSkillMemory = "https://www.gskill.com/products/68/165/Desktop-Memory-U-DIMM-CU-DIMM";
    private const string SamsungStorage = "https://semiconductor.samsung.com/consumer-storage/support/documents/";
    private const string WdBlackStorage = "https://shop.sandisk.com/content/dam/sandisk/en-us/assets/promo/wd-black-gaming-yepromo/wd-black-gaming-yepromo-tnc.pdf";
    private const string CrucialPro = "https://investors.micron.com/news/press-release/2024/Crucial-Pro-Series-Supercharges-Portfolio-with-DDR5-Overclocking-Memory-and-Worlds-Fastest-Gen5-SSD-02-20-2024/default.aspx";

    public static IReadOnlyList<HardwareModelPreset> Models { get; } = Array.AsReadOnly<HardwareModelPreset>(
    [
        // CPU: verified launches, plus widely used recent desktop processors.
        new(HardwareModelCategory.Cpu, "Intel Core Ultra 7 270K Plus", "Intel", IntelUltra),
        new(HardwareModelCategory.Cpu, "Intel Core Ultra 5 250K Plus", "Intel", IntelUltra),
        new(HardwareModelCategory.Cpu, "Intel Core Ultra 9 285K", "Intel", IntelUltra),
        new(HardwareModelCategory.Cpu, "Intel Core Ultra 7 265K", "Intel", IntelUltra),
        new(HardwareModelCategory.Cpu, "Intel Core Ultra 7 265KF", "Intel", IntelUltra),
        new(HardwareModelCategory.Cpu, "Intel Core Ultra 5 245K", "Intel", IntelUltra),
        new(HardwareModelCategory.Cpu, "Intel Core Ultra 5 245KF", "Intel", IntelUltra),
        new(HardwareModelCategory.Cpu, "Intel Core i9-14900K", "Intel", IntelCore14),
        new(HardwareModelCategory.Cpu, "Intel Core i7-14700K", "Intel", IntelCore14),
        new(HardwareModelCategory.Cpu, "Intel Core i5-14600K", "Intel", IntelCore14),
        new(HardwareModelCategory.Cpu, "Intel Core i5-14400F", "Intel", "https://cdrdv2-public.intel.com/841556/APP-for-Intel-Core-Processors.pdf"),
        new(HardwareModelCategory.Cpu, "AMD Ryzen 9 9950X3D2 Dual Edition", "AMD", "https://newsroom.amd.com/news/amd-launches-ryzen-9-9950x3d2-dual-edition-processor/"),
        new(HardwareModelCategory.Cpu, "AMD Ryzen 9 9950X3D", "AMD", "https://www.amd.com/en/products/processors/desktops/ryzen/9000-series/amd-ryzen-9-9950x3d.html"),
        new(HardwareModelCategory.Cpu, "AMD Ryzen 9 9900X3D", "AMD", "https://www.amd.com/en/products/processors/desktops/ryzen/9000-series/amd-ryzen-9-9900X3D.html"),
        new(HardwareModelCategory.Cpu, "AMD Ryzen 7 9850X3D", "AMD", "https://www.amd.com/en/products/processors/technologies/3d-v-cache.html"),
        new(HardwareModelCategory.Cpu, "AMD Ryzen 7 9800X3D", "AMD", AmdRyzen9000),
        new(HardwareModelCategory.Cpu, "AMD Ryzen 9 9950X", "AMD", AmdRyzen9000),
        new(HardwareModelCategory.Cpu, "AMD Ryzen 9 9900X", "AMD", AmdRyzen9000),
        new(HardwareModelCategory.Cpu, "AMD Ryzen 7 9700X", "AMD", AmdRyzen9000),
        new(HardwareModelCategory.Cpu, "AMD Ryzen 5 9600X", "AMD", AmdRyzen9000),
        new(HardwareModelCategory.Cpu, "AMD Ryzen 7 7800X3D", "AMD", "https://www.amd.com/en/newsroom/press-releases/2023-1-4-amd-extends-its-leadership-with-the-introduction-o.html"),
        new(HardwareModelCategory.Cpu, "AMD Ryzen 5 7600", "AMD", "https://www.amd.com/en/products/processors/desktops/ryzen/7000-series/amd-ryzen-5-7600.html"),

        // Motherboards: AM5 and Intel platforms from four established board vendors.
        new(HardwareModelCategory.Motherboard, "ASUS ROG STRIX Z890-A GAMING WIFI", "ASUS", "https://rog.asus.com/motherboards/rog-strix/rog-strix-z890-a-gaming-wifi/spec/"),
        new(HardwareModelCategory.Motherboard, "ASUS TUF GAMING B850-PLUS WIFI", "ASUS", "https://www.asus.com/us/motherboards-components/motherboards/tuf-gaming/tuf-gaming-b850-plus-wifi/"),
        new(HardwareModelCategory.Motherboard, "ASUS TUF GAMING B650-PLUS WIFI", "ASUS", "https://www.asus.com/us/motherboards-components/motherboards/tuf-gaming/tuf-gaming-b650-plus-wifi/techspec/"),
        new(HardwareModelCategory.Motherboard, "MSI MAG X870 TOMAHAWK WIFI", "MSI", "https://www.msi.com/Motherboard/MAG-X870-TOMAHAWK-WIFI/Specification"),
        new(HardwareModelCategory.Motherboard, "MSI MAG B850 TOMAHAWK MAX WIFI", "MSI", "https://www.msi.com/Motherboard/MAG-B850-TOMAHAWK-MAX-WIFI/Overview"),
        new(HardwareModelCategory.Motherboard, "MSI MAG B650 TOMAHAWK WIFI", "MSI", "https://www.msi.com/Motherboard/MAG-B650-TOMAHAWK-WIFI"),
        new(HardwareModelCategory.Motherboard, "MSI MAG B760M MORTAR WIFI", "MSI", "https://www.msi.com/Motherboard/MAG-B760M-MORTAR-WIFI"),
        new(HardwareModelCategory.Motherboard, "GIGABYTE B850 AORUS ELITE WIFI7", "GIGABYTE", "https://www.gigabyte.com/Motherboard/B850-AORUS-ELITE-WIFI7-rev-1x/sp?lan=en"),
        new(HardwareModelCategory.Motherboard, "GIGABYTE B650 AORUS ELITE AX", "GIGABYTE", "https://www.gigabyte.com/Motherboard/B650-AORUS-ELITE-AX-rev-12"),
        new(HardwareModelCategory.Motherboard, "GIGABYTE B760M DS3H", "GIGABYTE", "https://www.gigabyte.com/Motherboard/B760M-DS3H-rev-10"),
        new(HardwareModelCategory.Motherboard, "ASRock X870 Steel Legend WiFi", "ASRock", "https://asrock.com/mb/AMD/X870%20Steel%20Legend%20WiFi/index.asp"),
        new(HardwareModelCategory.Motherboard, "ASRock B850 Steel Legend WiFi", "ASRock", "https://asrock.com/mb/AMD/B850%20Steel%20Legend%20WiFi/index.asp"),
        new(HardwareModelCategory.Motherboard, "ASRock Z890 Steel Legend WiFi", "ASRock", "https://www.asrock.com/mb/Intel/Z890%20Steel%20Legend%20WiFi/"),

        // GPUs: desktop product names, without implying a particular partner board.
        new(HardwareModelCategory.Gpu, "NVIDIA GeForce RTX 5090", "NVIDIA", Nvidia50),
        new(HardwareModelCategory.Gpu, "NVIDIA GeForce RTX 5080", "NVIDIA", Nvidia50),
        new(HardwareModelCategory.Gpu, "NVIDIA GeForce RTX 5070 Ti", "NVIDIA", Nvidia50),
        new(HardwareModelCategory.Gpu, "NVIDIA GeForce RTX 5070", "NVIDIA", Nvidia50),
        new(HardwareModelCategory.Gpu, "NVIDIA GeForce RTX 5060 Ti", "NVIDIA", Nvidia50),
        new(HardwareModelCategory.Gpu, "NVIDIA GeForce RTX 5060", "NVIDIA", Nvidia50),
        new(HardwareModelCategory.Gpu, "NVIDIA GeForce RTX 5050", "NVIDIA", Nvidia50),
        new(HardwareModelCategory.Gpu, "NVIDIA GeForce RTX 4090", "NVIDIA", Nvidia40),
        new(HardwareModelCategory.Gpu, "NVIDIA GeForce RTX 4080 SUPER", "NVIDIA", Nvidia40),
        new(HardwareModelCategory.Gpu, "NVIDIA GeForce RTX 4070 Ti SUPER", "NVIDIA", Nvidia40),
        new(HardwareModelCategory.Gpu, "NVIDIA GeForce RTX 4070 SUPER", "NVIDIA", Nvidia40),
        new(HardwareModelCategory.Gpu, "NVIDIA GeForce RTX 4060", "NVIDIA", Nvidia40),
        new(HardwareModelCategory.Gpu, "AMD Radeon RX 9070 XT", "AMD", AmdRadeon9000),
        new(HardwareModelCategory.Gpu, "AMD Radeon RX 9070", "AMD", AmdRadeon9000),
        new(HardwareModelCategory.Gpu, "AMD Radeon RX 9060 XT 16GB", "AMD", AmdRadeon9000),
        new(HardwareModelCategory.Gpu, "AMD Radeon RX 9060 XT 8GB", "AMD", AmdRadeon9000),
        new(HardwareModelCategory.Gpu, "AMD Radeon RX 7900 XTX", "AMD", AmdRadeon7000),
        new(HardwareModelCategory.Gpu, "AMD Radeon RX 7900 XT", "AMD", AmdRadeon7000),
        new(HardwareModelCategory.Gpu, "AMD Radeon RX 7800 XT", "AMD", AmdRadeon7000),
        new(HardwareModelCategory.Gpu, "AMD Radeon RX 7700 XT", "AMD", AmdRadeon7000),
        new(HardwareModelCategory.Gpu, "AMD Radeon RX 7600 XT", "AMD", AmdRadeon7000),
        new(HardwareModelCategory.Gpu, "AMD Radeon RX 7600", "AMD", AmdRadeon7000),
        new(HardwareModelCategory.Gpu, "Intel Arc B580", "Intel", IntelArc),
        new(HardwareModelCategory.Gpu, "Intel Arc B570", "Intel", IntelArc),
        new(HardwareModelCategory.Gpu, "Intel Arc A770", "Intel", IntelArc),
        new(HardwareModelCategory.Gpu, "Intel Arc A750", "Intel", IntelArc),

        // Memory: product family suggestions; existing physical capacity remains separate.
        new(HardwareModelCategory.Memory, "Kingston FURY Beast DDR5", "Kingston", "https://www.kingston.com/en/memory/gaming/kingston-fury-beast-ddr5-memory?cas+latency=36&color=black&dram+density=16gbit&kit=kit+of+2&profile+type=amd+expo+%2F+intel+xmp&speed=6000mt%2Fs&total+%28kit%29+capacity=64gb"),
        new(HardwareModelCategory.Memory, "Kingston FURY Renegade DDR5", "Kingston", "https://www.kingston.com/unitedkingdom/en/memory/gaming/fury-renegade-ddr5?color=black&dram+density=24gbit&kit=single+module&speed=8400mt%2Fs&total+%28kit%29+capacity=24gb"),
        new(HardwareModelCategory.Memory, "Kingston FURY Beast DDR4", "Kingston", "https://www.kingston.com/en/memory/gaming/kingston-fury-beast-ddr4-memory"),
        new(HardwareModelCategory.Memory, "CORSAIR VENGEANCE DDR5", "CORSAIR", CorsairMemory),
        new(HardwareModelCategory.Memory, "CORSAIR VENGEANCE RGB DDR5", "CORSAIR", CorsairMemory),
        new(HardwareModelCategory.Memory, "CORSAIR DOMINATOR TITANIUM RGB DDR5", "CORSAIR", CorsairMemory),
        new(HardwareModelCategory.Memory, "CORSAIR VENGEANCE LPX DDR4", "CORSAIR", CorsairMemory),
        new(HardwareModelCategory.Memory, "G.SKILL Trident Z5 RGB DDR5", "G.SKILL", GSkillMemory),
        new(HardwareModelCategory.Memory, "G.SKILL Trident Z5 Neo RGB DDR5", "G.SKILL", GSkillMemory),
        new(HardwareModelCategory.Memory, "G.SKILL Ripjaws S5 DDR5", "G.SKILL", GSkillMemory),
        new(HardwareModelCategory.Memory, "G.SKILL Flare X5 DDR5", "G.SKILL", GSkillMemory),
        new(HardwareModelCategory.Memory, "Crucial Pro DDR5 Overclocking Edition", "Crucial", CrucialPro),

        // Monitors: gaming, everyday use and creator options.
        new(HardwareModelCategory.Monitor, "ASUS ROG Swift OLED PG27AQDM", "ASUS", "https://rog.asus.com/monitors/27-to-31-5-inches/rog-swift-oled-pg27aqdm/"),
        new(HardwareModelCategory.Monitor, "MSI MAG 274QRF QD E2", "MSI", "https://www.msi.com/Monitor/MAG-274QRF-QD-E2/Specification"),
        new(HardwareModelCategory.Monitor, "MSI MPG 321URX QD-OLED", "MSI", "https://www.msi.com/Monitor/MPG-321URX-QD-OLED/Specification"),
        new(HardwareModelCategory.Monitor, "GIGABYTE M27Q", "GIGABYTE", "https://download.gigabyte.com/FileList/Manual/GIGABYTE_M27Q_20_UM_ENG_20220408.pdf?v=147f79201fcc1f9d58a816159610fbb0"),
        new(HardwareModelCategory.Monitor, "LG UltraGear 27GP850-B", "LG", "https://www.lg.com/us/monitors/lg-27gp850-b-gaming-monitor"),
        new(HardwareModelCategory.Monitor, "LG UltraGear OLED 27GR95QE-B", "LG", "https://www.lg.com/us/monitors/lg-27gr95qe-b-gaming-monitor"),
        new(HardwareModelCategory.Monitor, "Samsung Odyssey OLED G6 G60SD", "Samsung", "https://www.samsung.com/us/monitors/gaming/27-inch-odyssey-oled-g6-g60sd-qhd-360hz-03ms-freesync-sku-ls27dg602snxza/"),
        new(HardwareModelCategory.Monitor, "Dell UltraSharp U2723QE", "Dell", "https://www.dell.com/support/product-details/en-us/product/u2723qe-monitor/manuals"),
        new(HardwareModelCategory.Monitor, "BenQ PD2705U", "BenQ", "https://www.benq.com/en-us/monitor/creative-pro/pd2705u/buy.html"),
        new(HardwareModelCategory.Monitor, "AOC Q27G4F", "AOC", "https://saas.aoc.com/product/Q27G4F"),

        // Storage: NVMe, SATA and HDD families, including familiar recent models.
        new(HardwareModelCategory.Disk, "Samsung SSD 9100 PRO", "Samsung", SamsungStorage),
        new(HardwareModelCategory.Disk, "Samsung SSD 990 PRO", "Samsung", SamsungStorage),
        new(HardwareModelCategory.Disk, "Samsung SSD 990 EVO Plus", "Samsung", SamsungStorage),
        new(HardwareModelCategory.Disk, "Samsung SSD 870 EVO", "Samsung", SamsungStorage),
        new(HardwareModelCategory.Disk, "WD_BLACK SN8100 NVMe SSD", "SanDisk", WdBlackStorage),
        new(HardwareModelCategory.Disk, "WD_BLACK SN850X NVMe SSD", "SanDisk", WdBlackStorage),
        new(HardwareModelCategory.Disk, "WD_BLACK SN7100 NVMe SSD", "SanDisk", WdBlackStorage),
        new(HardwareModelCategory.Disk, "Kingston KC3000", "Kingston", "https://www.kingston.com/en/ssd/kc3000-nvme-m2-solid-state-drive"),
        new(HardwareModelCategory.Disk, "Kingston NV3", "Kingston", "https://www.kingston.com/en/ssd/nv3-nvme-pcie-ssd?capacity=1"),
        new(HardwareModelCategory.Disk, "Crucial T705", "Crucial", CrucialPro),
        new(HardwareModelCategory.Disk, "Crucial T500", "Crucial", "https://www.crucial.com/content/dam/crucial/ssd-products/t500/flyers/b2c/crucial-T500-b2c-product-flyer-en_combination.pdf"),
        new(HardwareModelCategory.Disk, "Seagate FireCuda 530R SSD", "Seagate", "https://www.seagate.com/content/dam/seagate/en/content-fragments/products/datasheets/firecuda-530r-ssd/firecuda-530r-ssd-DS7-1-2403US-en_US.pdf"),
        new(HardwareModelCategory.Disk, "Seagate BarraCuda 3.5 HDD", "Seagate", "https://www.seagate.com/support/internal-hard-drives/desktop-hard-drives/barracuda-3-5/")
    ]);

    private static readonly IReadOnlyDictionary<HardwareModelCategory, IReadOnlyList<HardwareModelPreset>> ModelsByCategory =
        Enum.GetValues<HardwareModelCategory>().ToDictionary(
            category => category,
            category => (IReadOnlyList<HardwareModelPreset>)Array.AsReadOnly(
                Models.Where(model => model.Category == category).ToArray()));

    public static IReadOnlyList<HardwareModelPreset> ForCategory(HardwareModelCategory category) =>
        ModelsByCategory.TryGetValue(category, out var models)
            ? models
            : Array.Empty<HardwareModelPreset>();
}
