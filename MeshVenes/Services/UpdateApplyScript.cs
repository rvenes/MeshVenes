using System;
using System.Text;

namespace MeshVenes.Services;

public static class UpdateApplyScript
{
    private static string Encode(string text) => Convert.ToBase64String(Encoding.UTF8.GetBytes(text));

    public static string Build(string staging, string install, string executable, int processId) => $$"""
        $ErrorActionPreference = 'Stop'
        function Decode([string]$text) { [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($text)) }
        $source = Decode '{{Encode(staging)}}'
        $destination = Decode '{{Encode(install)}}'
        $executable = Decode '{{Encode(executable)}}'
        $backup = Join-Path ([IO.Path]::GetDirectoryName($source)) 'backup'
        $log = Join-Path ([IO.Path]::GetDirectoryName($source)) 'apply-result.txt'
        function Assert-OrdinaryPath([string]$path) {
            $cursor = [IO.Path]::GetFullPath($path)
            while ($cursor) {
                if ((Test-Path -LiteralPath $cursor) -and
                    ((Get-Item -LiteralPath $cursor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
                    throw 'Update paths must not contain links or junctions.'
                }
                $cursor = [IO.Path]::GetDirectoryName($cursor)
            }
        }
        $changed = New-Object 'Collections.Generic.List[object]'
        $useLock = $null
        try {
            $app = Get-Process -Id {{processId}} -ErrorAction SilentlyContinue
            if ($app) { $app.WaitForExit() }
            Assert-OrdinaryPath $source
            Assert-OrdinaryPath $destination
            Assert-OrdinaryPath $backup
            $lockPath = Join-Path $destination '.meshvenes-use.lock'
            Assert-OrdinaryPath $lockPath
            $useLock = [IO.File]::Open($lockPath, [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
            $prefix = [IO.Path]::GetFullPath($source).TrimEnd('\') + '\'
            $destinationPrefix = [IO.Path]::GetFullPath($destination).TrimEnd('\') + '\'
            $files = @(Get-ChildItem -LiteralPath $source -File -Recurse -Force)
            $plan = New-Object 'Collections.Generic.List[object]'
            foreach ($file in $files) {
                Assert-OrdinaryPath $file.FullName
                $relative = $file.FullName.Substring($prefix.Length)
                $target = [IO.Path]::GetFullPath((Join-Path $destination $relative))
                if (-not $target.StartsWith($destinationPrefix, [StringComparison]::OrdinalIgnoreCase)) { throw 'Invalid update target.' }
                Assert-OrdinaryPath $target
                if ($relative -eq '.meshvenes-use.lock') { throw 'Update archive contains a reserved file.' }
                $originalHash = $null
                if (Test-Path -LiteralPath $target) {
                    $saved = Join-Path $backup $relative
                    [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($saved))
                    [IO.File]::Copy($target, $saved, $false)
                    $originalHash = (Get-FileHash -LiteralPath $saved -Algorithm SHA256).Hash
                }
                $plan.Add(@{ Path = $relative; OriginalHash = $originalHash })
            }
            [IO.File]::WriteAllText((Join-Path ([IO.Path]::GetDirectoryName($source)) 'recovery-plan.json'),
                (@{ Destination = $destination; Files = @($plan.ToArray()) } | ConvertTo-Json -Depth 5))
            foreach ($file in $files) {
                $relative = $file.FullName.Substring($prefix.Length)
                $target = Join-Path $destination $relative
                $saved = Join-Path $backup $relative
                $changed.Add(@{ Target = $target; Saved = $saved })
                [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($target))
                [IO.File]::Copy($file.FullName, $target, $true)
            }
            [IO.File]::WriteAllText($log, 'Update applied. Backup retained.')
        } catch {
            $failure = $_.Exception.Message
            $recoveryFailed = $false
            foreach ($item in $changed) {
                try {
                    if ([IO.File]::Exists($item.Saved)) { [IO.File]::Copy($item.Saved, $item.Target, $true) }
                    elseif ([IO.File]::Exists($item.Target)) { [IO.File]::Delete($item.Target) }
                } catch { $recoveryFailed = $true }
            }
            $result = if ($recoveryFailed) { 'Update and recovery failed. Backup retained for manual recovery.' } else { 'Update failed; original files restored. ' + $failure }
            [IO.File]::WriteAllText($log, $result)
            exit 1
        } finally {
            if ($useLock) { $useLock.Dispose() }
        }
        Start-Process -FilePath $executable -WorkingDirectory $destination
        """;
}
