param(
    [ValidateSet('x64', 'x86', 'arm64')]
    [string]$Arch = 'x64',
    [string]$ZigPath
)

$ErrorActionPreference = 'Stop'
$LauncherDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$ProjectDir = Split-Path -Parent $LauncherDir

# A portable Zig path can be supplied without installing or changing PATH.
$ZigExe = $ZigPath
if (-not $ZigExe) {
    $wingetLink = Join-Path $env:LOCALAPPDATA 'Microsoft\WinGet\Links\zig.exe'
    if (Test-Path -LiteralPath $wingetLink) { $ZigExe = $wingetLink }
    elseif (Get-Command zig -ErrorAction SilentlyContinue) { $ZigExe = 'zig' }
    else { throw 'Zig not found. Supply an official portable compiler with -ZigPath.' }
}

$Target = switch ($Arch) {
    'x64'   { 'x86_64-windows-gnu' }
    'x86'   { 'x86-windows-gnu' }
    'arm64' { 'aarch64-windows-gnu' }
}

$binDir = Join-Path $LauncherDir 'bin'
$buildDir = Join-Path $binDir ".build-$Arch"
New-Item -ItemType Directory -Path $buildDir -Force | Out-Null
$rcFile = Join-Path $LauncherDir 'launcher.rc'
$rawRc = Join-Path $buildDir 'launcher-preprocessed.rcpp'
$resourceRc = Join-Path $buildDir 'launcher-resource.rcpp'
$resFile = Join-Path $buildDir 'launcher.res'
$outExe = Join-Path $binDir "图吧工具箱WinUI3_$Arch.exe"

Push-Location -LiteralPath $LauncherDir
try {
    # Use the checked-in resource as the single source of version and branding.
    # -P avoids Zig 0.14's resource line-marker bug in paths containing non-ASCII.
    & $ZigExe cc -target $Target -E -P -DRC_INVOKED -x c $rcFile -o $rawRc
    if ($LASTEXITCODE -ne 0) { throw 'Launcher resource preprocessing failed.' }
    $preprocessed = [System.IO.File]::ReadAllText($rawRc)
    # Mingw resource headers can emit C declarations; only the actual RC block is valid here.
    $resourceStart = $preprocessed.IndexOf('IDI_APP ICON ')
    if ($resourceStart -lt 0) { throw 'Launcher resource start marker missing.' }
    $preprocessed = $preprocessed.Substring($resourceStart)
    $iconReference = '..\\TubaWinUi3.WinUI3\\Assets\\AppIcon.ico'
    if (-not $preprocessed.Contains($iconReference)) { throw 'Launcher icon reference missing.' }
    $iconPath = (Join-Path $ProjectDir 'TubaWinUi3.WinUI3\Assets\AppIcon.ico').Replace('\', '\\')
    $preprocessed = $preprocessed.Replace($iconReference, $iconPath)
    [System.IO.File]::WriteAllText($resourceRc, $preprocessed, [System.Text.UTF8Encoding]::new($false))
    & $ZigExe rc '/:no-preprocess' /c 65001 /fo $resFile $resourceRc
    if ($LASTEXITCODE -ne 0) { throw 'Launcher resource compilation failed.' }

    $zigArgs = @(
        'cc', '-target', $Target,
        '-Wl,--subsystem,windows', '-municode', '-O2',
        '-D_UNICODE', '-DUNICODE', '-DNDEBUG',
        '-lshlwapi', '-luser32', '-lshell32',
        (Join-Path $LauncherDir 'launcher.c'), $resFile,
        '-o', $outExe
    )
    & $ZigExe @zigArgs
    if ($LASTEXITCODE -ne 0) { throw 'Launcher compilation failed.' }
    $symbols = [System.IO.Path]::ChangeExtension($outExe, '.pdb')
    if (Test-Path -LiteralPath $symbols) {
        Move-Item -LiteralPath $symbols -Destination (Join-Path $buildDir 'launcher-symbols.pdb') -Force
    }
    $size = [math]::Round((Get-Item -LiteralPath $outExe).Length / 1KB, 1)
    Write-Output "Built $Arch launcher with icon and version resources: $outExe ($size KB)"
} finally {
    Pop-Location
    foreach ($temporaryFile in @($rawRc, $resourceRc, $resFile)) {
        if (Test-Path -LiteralPath $temporaryFile) { Remove-Item -LiteralPath $temporaryFile -Force }
    }
}