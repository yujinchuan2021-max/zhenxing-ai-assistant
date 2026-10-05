<#
.SYNOPSIS
  ZXAI A16: prepare the app-private dsh runtime (run once before packaging).

.DESCRIPTION
  Produces into the target runtime dir:
    runtime\node.exe                    standalone Node (no system/Hermes dependency)
    runtime\dsh\lib\bin.js              standalone dsh + its full node_modules closure
    runtime\versions.json               version lock manifest

  Locked combo (validated locally):
    - Node  v22.23.2
    - dsh   @deepseek-ai/dsh 0.1.5-rc.2

  Evidence: zxai-docs/dsh-protocol-reference-2026-09-21.md ("standalone runtime"):
    launch by absolute path: <node.exe> <dsh>\lib\bin.js --profile acp --patch ...
    set child env DSH_HOME=<app data root>; never share the Hermes profile module mirror.

.NOTES
  FAIL-CLOSED (release behaviour): a missing node, a missing dsh, or ANY version that does
  not match the lock writes an error and exits non-zero. There is deliberately no
  warning-and-continue path -- a shipped runtime that is not the validated combo must never
  be produced silently. Re-locking is an explicit act: pass -LockedNode / -LockedDsh with
  the newly validated values (and re-run the offline smoke test).
  Only copies/assembles/registers versions; never downloads Node.
  Provide -NodeZip or -NodeExe for Node, and -DshSource for dsh.
  ASCII-only source on purpose: avoids PowerShell 5.1 encoding hazards.
#>
param(
    [string]$RepoRoot   = "",
    [string]$NodeZip    = "",
    [string]$NodeExe    = "",
    [string]$DshSource  = "",
    [string]$RuntimeDir = "",
    [string]$LockedNode = "v22.23.2",
    [string]$LockedDsh  = "0.1.5-rc.2"
)

$ErrorActionPreference = "Stop"

function Fail-Closed([string]$Message) {
    [Console]::Error.WriteLine("[A16][FAIL] $Message")
    [Console]::Error.WriteLine("[A16][FAIL] fail-closed: runtime NOT assembled (exit 1). Fix the input or the lock, then re-run.")
    exit 1
}

if ([string]::IsNullOrWhiteSpace($RepoRoot)) {
    $RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
}
if ([string]::IsNullOrWhiteSpace($RuntimeDir)) {
    $RuntimeDir = Join-Path $RepoRoot "TubaWinUi3.WinUI3\runtime"
}

# (0) lock must be explicit: an empty lock means "anything goes" -- never acceptable for release
if ([string]::IsNullOrWhiteSpace($LockedNode)) { Fail-Closed "LockedNode is empty; an unlocked Node version is not releaseable (pass the validated version, e.g. -LockedNode v22.23.2)" }
if ([string]::IsNullOrWhiteSpace($LockedDsh))  { Fail-Closed "LockedDsh is empty; an unlocked dsh version is not releaseable (pass the validated version, e.g. -LockedDsh 0.1.5-rc.2)" }

$runtimeDir = $RuntimeDir
New-Item -ItemType Directory -Force -Path $runtimeDir | Out-Null
Write-Host "[A16] target runtime dir: $runtimeDir"
Write-Host "[A16] lock: node=$LockedNode dsh=$LockedDsh (fail-closed)"

# ---- (1) Node ----
$nodeTarget = Join-Path $runtimeDir "node.exe"
if (-not [string]::IsNullOrWhiteSpace($NodeZip)) {
    if (-not (Test-Path $NodeZip)) { Fail-Closed "NodeZip not found: $NodeZip" }
    $tmp = Join-Path $env:TEMP ("zxai-node-" + [guid]::NewGuid().ToString("N").Substring(0, 8))
    Expand-Archive -Path $NodeZip -DestinationPath $tmp -Force
    $found = Get-ChildItem -Path $tmp -Recurse -Filter node.exe | Select-Object -First 1
    if (-not $found) { Fail-Closed "node.exe not found in zip: $NodeZip" }
    Copy-Item $found.FullName $nodeTarget -Force
    Remove-Item $tmp -Recurse -Force
    Write-Host "[A16] node.exe copied from zip"
} elseif (-not [string]::IsNullOrWhiteSpace($NodeExe)) {
    if (-not (Test-Path $NodeExe)) { Fail-Closed "NodeExe not found: $NodeExe" }
    Copy-Item $NodeExe $nodeTarget -Force
    Write-Host "[A16] node.exe copied from -NodeExe"
} elseif (Test-Path $nodeTarget) {
    Write-Host "[A16] reusing existing runtime\node.exe"
} else {
    Fail-Closed "no Node provided (-NodeZip/-NodeExe) and runtime\node.exe is missing; a standalone runtime MUST carry its own node.exe"
}

if (-not (Test-Path $nodeTarget)) { Fail-Closed "runtime\node.exe is missing after assembly: $nodeTarget" }
$nodeVer = (& $nodeTarget --version).Trim()
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($nodeVer)) {
    Fail-Closed "runtime\node.exe cannot execute (--version failed, exit=$LASTEXITCODE): $nodeTarget"
}
Write-Host "[A16] node version: $nodeVer"
if ($nodeVer -ne $LockedNode) {
    Fail-Closed "Node version mismatch: expected $LockedNode, got $nodeVer (re-lock explicitly if this is intended)"
}

# ---- (2) dsh ----
$dshTarget = Join-Path $runtimeDir "dsh"
if (-not [string]::IsNullOrWhiteSpace($DshSource)) {
    $binJsCheck = Join-Path $DshSource "lib\bin.js"
    if (-not (Test-Path $binJsCheck)) { Fail-Closed "DshSource is not a valid dsh dir (lib\bin.js missing): $DshSource" }
    if (Test-Path $dshTarget) { Remove-Item $dshTarget -Recurse -Force }
    Copy-Item $DshSource $dshTarget -Recurse -Force
    Write-Host "[A16] dsh copied: $dshTarget"
} elseif (Test-Path (Join-Path $dshTarget "lib\bin.js")) {
    Write-Host "[A16] reusing existing runtime\dsh"
} else {
    Fail-Closed "no DshSource provided and runtime\dsh\lib\bin.js is missing; a standalone runtime MUST carry its own dsh"
}

$binJs = Join-Path $dshTarget "lib\bin.js"
if (-not (Test-Path $binJs)) { Fail-Closed "runtime\dsh\lib\bin.js is missing after assembly: $binJs" }

# ---- (3) dsh version check (fail-closed) + version manifest ----
$dshVer = ""
try {
    $pkg = Get-Content (Join-Path $dshTarget "package.json") -Raw | ConvertFrom-Json
    $dshVer = [string]$pkg.version
} catch {
    Fail-Closed "cannot read dsh version from runtime\dsh\package.json: $($_.Exception.Message)"
}
if ([string]::IsNullOrWhiteSpace($dshVer)) {
    Fail-Closed "runtime\dsh\package.json has no version field: $(Join-Path $dshTarget 'package.json')"
}
Write-Host "[A16] dsh version: $dshVer"
if ($dshVer -ne $LockedDsh) {
    Fail-Closed "dsh version mismatch: expected $LockedDsh, got $dshVer (re-lock explicitly if this is intended)"
}

$manifest = [ordered]@{
    schema      = "zxai-dsh-runtime/1"
    node        = $nodeVer
    dsh         = $dshVer
    lockedNode  = $LockedNode
    lockedDsh   = $LockedDsh
    failClosed  = $true
    generatedAt = (Get-Date -Format "yyyy-MM-dd HH:mm:ss")
    note        = "ZXAI A16 version lock: app-private Node+dsh runtime; changing it requires re-running this script and full regression. Assembly fails closed (missing node/dsh or version drift => non-zero exit)."
}
$jsonText = $manifest | ConvertTo-Json
[System.IO.File]::WriteAllText((Join-Path $runtimeDir "versions.json"), $jsonText)
Write-Host "[A16] versions.json written (UTF-8 no BOM)"

Write-Host "[A16] done. Packaging output with DshRuntimeResolver will take the standalone branch."
exit 0
