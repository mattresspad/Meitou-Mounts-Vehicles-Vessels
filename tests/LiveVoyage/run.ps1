# Run release acceptance in a fresh directory using the install's read-only data and private mods, settings and saves.
param(
    [Parameter(Mandatory)][ValidateSet('core','garru','bare','fit','bags','bull','mixed','stoat','example','old-save')][string]$Scenario,
    [Parameter(Mandatory)][string]$Kenshi,
    [Parameter(Mandatory)][string]$Game,
    [Parameter(Mandatory)][string]$TrialDirectory,
    [string]$OldSave,
    [string]$CacheDirectory,
    [string]$CpuVulkanIcd
)
$ErrorActionPreference = 'Stop'
$trial = [IO.Path]::GetFullPath($TrialDirectory)
if (Test-Path -LiteralPath $trial) { throw "Use a fresh trial directory: $trial" }
$gameExe = (Resolve-Path (Join-Path $Game 'MeitouClient.exe')).Path
$source = (Resolve-Path $Kenshi).Path
$install = Join-Path $trial 'install'
New-Item -ItemType Directory -Path $install -Force | Out-Null
New-Item -ItemType Junction -Path (Join-Path $install 'data') -Target (Join-Path $source 'data') | Out-Null
foreach ($file in 'resources.cfg','fcs.def','controls.cfg','screens.cfg','kenshi.cfg','settings.cfg','currentVersion.txt','__tutorials.data') {
    if (Test-Path -LiteralPath (Join-Path $source $file)) { Copy-Item -LiteralPath (Join-Path $source $file) -Destination $install }
}
$staging = switch ($Scenario) { 'old-save' { 'example' } 'bare' { 'garru' } 'fit' { 'garru' } 'bags' { 'garru' } default { $Scenario } }
& python (Join-Path $PSScriptRoot 'prepare.py') $staging (Join-Path $install 'mods')
if ($LASTEXITCODE -ne 0) { throw 'Could not stage release ZIPs.' }
$list = @('Meitou Transport.mod')
if ($staging -in 'garru','mixed','stoat') { $list += 'Garru Saddle.mod' }
if ($staging -eq 'stoat') { $list += 'Stoat Desert Ride.mod' }
if ($staging -eq 'bull') { $list += 'Pack Bull Saddle.mod' }
if ($staging -in 'example','mixed') { $list += 'Western Hive Skiff.mod' }
$cfg = Join-Path $trial 'mods.cfg'
[IO.File]::WriteAllLines($cfg,[string[]]$list)
$scratch = (New-Item -ItemType Directory -Path (Join-Path $trial 'temp')).FullName
$control = (New-Item -ItemType Directory -Path (Join-Path $trial 'control')).FullName
$clientEnv = @{
    KENSHI_PATH = $install
    MEITOU_MODS_CFG = $cfg
    DOTNET_STARTUP_HOOKS = (Resolve-Path (Join-Path $PSScriptRoot 'bin/Release/net11.0/Meitou.Transport.LiveVoyage.dll')).Path
    DOTNET_PROCESSOR_COUNT = [string][Math]::Min(4, [Environment]::ProcessorCount)
    TEMP = $scratch
    TMP = $scratch
    MEITOU_TRANSPORT_RIDE = $null
    MEITOU_TRANSPORT_LIVE_TEST = $null
    MEITOU_TRANSPORT_OLD_SAVE = $null
    MEITOU_TRANSPORT_TEST_CPU_VULKAN = $null
}
if ($CacheDirectory) {
    $clientEnv.MEITOU_IMPOSTOR_CACHE = Join-Path $CacheDirectory 'impostors'
    $clientEnv.MEITOU_OBJECT_LOD_CACHE = Join-Path $CacheDirectory 'objlods'
    $clientEnv.MEITOU_CHARACTER_LOD_CACHE = Join-Path $CacheDirectory 'charlods'
}
if ($Scenario -in 'garru','bare','fit','bags','bull','mixed','stoat') {
    $clientEnv.MEITOU_TRANSPORT_RIDE = $Scenario
    $start = switch ($Scenario) {
        'fit' { '--sandbox 2 --sandbox-character 1995-gamedata.base' }
        'stoat' { '--new-game "Stoat Desert Ride"' }
        default { '--new-game "The Wandering Trader"' }
    }
} else {
    $clientEnv.MEITOU_TRANSPORT_LIVE_TEST = '1'
    $clientEnv.MEITOU_TRANSPORT_LIVE_PACK = if ($Scenario -eq 'core') { 'none' } else { 'Western Hive Skiff' }
    $clientEnv.MEITOU_TRANSPORT_LIVE_SAVE = '1'
    $start = '--new-game Wanderer'
    if ($Scenario -eq 'old-save') {
        $original = (Resolve-Path $OldSave).Path
        $slot = Join-Path $trial 'old-slot'
        Copy-Item -LiteralPath $original -Destination $slot -Recurse
        $clientEnv.MEITOU_TRANSPORT_OLD_SAVE = $slot
        $start = '--load "' + $slot + '"'
    }
}
[IO.File]::WriteAllText((Join-Path $trial 'meitou.user.json'),'{"version":2,"fullScreen":false,"fpsLimit":30,"vSync":false,"splashScreens":false,"sandboxPanelCollapsed":true,"simThreads":4}')
$log = Join-Path $trial 'client.log'
$options = '--headless --no-load-cache --no-precompile --no-foliage --no-gi --no-audio --water-reflection 0'
if ($CpuVulkanIcd) {
    $icd = (Resolve-Path -LiteralPath $CpuVulkanIcd).ProviderPath
    $clientEnv.VK_DRIVER_FILES = $icd
    $clientEnv.VK_ICD_FILENAMES = $icd
    $clientEnv.VK_LOADER_LAYERS_DISABLE = '~implicit~'
    $clientEnv.MEITOU_TRANSPORT_TEST_CPU_VULKAN = '1'
    $clientEnv.MEITOU_VRAM_BUDGET_MB = '768'
    $options += ' --no-objects --distant-range 0 --radius 0.5 --shadow-range 0 --texture-quality 3 --fps-limit 12 --size 640x448'
} else {
    $options += ' --shadow-quality 0 --texture-quality 1 --fps-limit 24 --size 1100x760'
}
$arguments = "$start $options" + ' --control-dir "' + $control + '" --mods-cfg "' + $cfg + '"'
# Start-Process inherits the environment on Windows PowerShell 5.1. Restore it immediately so
# later launches in the same shell use their ordinary install, settings, and startup hooks.
$previous = @{}
try {
    foreach ($key in $clientEnv.Keys) {
        $previous[$key] = [Environment]::GetEnvironmentVariable($key)
        [Environment]::SetEnvironmentVariable($key, $clientEnv[$key])
    }
    $process = Start-Process -FilePath $gameExe -ArgumentList $arguments -WorkingDirectory $trial -WindowStyle Hidden -PassThru -RedirectStandardOutput $log -RedirectStandardError (Join-Path $trial 'errors.log')
} finally {
    foreach ($key in $previous.Keys) { [Environment]::SetEnvironmentVariable($key, $previous[$key]) }
}
Write-Output "QA PID $($process.Id) in $trial"
try {
    if ($CpuVulkanIcd -and !$process.HasExited) {
        $remaining = $process.ProcessorAffinity.ToInt64()
        $mask = 0L
        for ($i = 0; $i -lt 4 -and $remaining; $i++) {
            $bit = $remaining -band (-$remaining)
            $mask = $mask -bor $bit
            $remaining = $remaining -bxor $bit
        }
        $process.ProcessorAffinity = [IntPtr]::new($mask)
        $process.PriorityClass = 'BelowNormal'
    }
    $minutes = if ($CpuVulkanIcd) { 12 } else { 7 }
    $deadline = [DateTime]::UtcNow.AddMinutes($minutes)
    while (!$process.HasExited -and [DateTime]::UtcNow -lt $deadline) {
        if (Select-String -Path $log -Pattern 'LIVE (RIDE|VOYAGE) (PASS|FAIL)' -Quiet) { break }
        Start-Sleep -Milliseconds 750
    }
    Get-Content -LiteralPath $log -Tail 8
    if (!(Select-String -Path $log -Pattern 'LIVE (RIDE|VOYAGE) PASS' -Quiet)) { throw "Live verification failed. See $trial" }
} finally {
    if (!$process.HasExited) {
        [IO.File]::WriteAllText((Join-Path $control 'cmd'), "qa-end quit`n")
        if (!$process.WaitForExit(15000)) { $process.Kill(); $process.WaitForExit() }
    }
}
