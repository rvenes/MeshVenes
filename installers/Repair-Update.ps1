#requires -Version 5.1
<# Restore a retained, hash-checked update backup after an interrupted copy. #>
[CmdletBinding()]
param([Parameter(Mandatory)][string]$UpdateDirectory, [Parameter(Mandatory)][string]$InstallDirectory)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath($InstallDirectory).TrimEnd('\')
$update = [IO.Path]::GetFullPath($UpdateDirectory).TrimEnd('\')
function Assert-Ordinary([string]$Path) {
    $cursor = $Path
    while ($cursor) {
        if ((Test-Path -LiteralPath $cursor) -and ((Get-Item -LiteralPath $cursor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Recovery paths cannot contain links.' }
        $cursor = [IO.Path]::GetDirectoryName($cursor)
    }
}
Assert-Ordinary $root
Assert-Ordinary $update
$planPath = Join-Path $update 'recovery-plan.json'
Assert-Ordinary $planPath
$plan = Get-Content -LiteralPath $planPath -Raw | ConvertFrom-Json
if ([IO.Path]::GetFullPath($plan.Destination).TrimEnd('\') -ine $root) { throw 'Recovery plan does not match the explicitly selected installation.' }
if (-not ($plan.Files | Where-Object Path -eq 'MeshVenes.exe') -or -not ($plan.Files | Where-Object Path -eq 'MeshVenes.pri')) { throw 'Not a MeshVenes recovery plan.' }
$validated = @()
foreach ($entry in $plan.Files) {
    if ($entry.Path -match '(^|[\\/])\.\.([\\/]|$)|[:*?]|^[/\\]' -or $entry.Path -eq '.meshvenes-use.lock') { throw 'Invalid recovery path.' }
    $target = [IO.Path]::GetFullPath((Join-Path $root $entry.Path))
    $saved = [IO.Path]::GetFullPath((Join-Path (Join-Path $update 'backup') $entry.Path))
    if (-not $target.StartsWith($root + '\', [StringComparison]::OrdinalIgnoreCase) -or -not $saved.StartsWith($update + '\backup\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Recovery path escapes its root.' }
    Assert-Ordinary $target
    Assert-Ordinary $saved
    if ($entry.OriginalHash -and (Get-FileHash -LiteralPath $saved -Algorithm SHA256).Hash -cne $entry.OriginalHash) { throw 'Backup hash mismatch; nothing was restored.' }
    $validated += @{ Target = $target; Saved = $saved; Existed = [bool]$entry.OriginalHash }
}
$lockPath = Join-Path $root '.meshvenes-use.lock'
Assert-Ordinary $lockPath
$lock = [IO.File]::Open($lockPath, [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
try {
    foreach ($item in $validated) {
        if ($item.Existed) { [IO.File]::Copy($item.Saved, $item.Target, $true) }
        elseif ([IO.File]::Exists($item.Target)) { [IO.File]::Delete($item.Target) }
    }
    Write-Host 'Original program files restored. Backups and user data were retained.'
} finally { $lock.Dispose() }
