using TubaWinUi3.Services.CloudTools;

namespace TubaWinUi3.Tests;

public sealed class CloudPortableEntryPointTests
{
    [Theory]
    [InlineData("Display Driver Uninstaller.exe", true)]
    [InlineData("DDU/Display Driver Uninstaller.exe", true)]
    [InlineData("HiBitUninstaller-Portable.exe", true)]
    [InlineData("tools/hibituninstaller-portable.EXE", true)]
    [InlineData("bin/Setup.exe", false)]
    [InlineData("bin/tool-install.exe", false)]
    [InlineData("bin/tool-uninstall.exe", false)]
    [InlineData("Display Driver Uninstaller-setup.exe", false)]
    [InlineData("HiBitUninstaller-Portable-Installer.exe", false)]
    [InlineData("../Display Driver Uninstaller.exe", false)]
    [InlineData("C:/Display Driver Uninstaller.exe", false)]
    [InlineData("bin/vcredist.exe", false)]
    public void ReviewedRemovalToolsArePortableEntrancesButInstallerAndUnsafePathsRemainRejected(string path, bool expected)
        => Assert.Equal(expected, CloudToolValidation.IsToolEntryPoint(path));
}
