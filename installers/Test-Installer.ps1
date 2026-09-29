#requires -Version 5.1
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'install-windows.ps1')
Add-Type -AssemblyName System.IO.Compression.FileSystem
Add-Type -AssemblyName System.IO.Compression
$testRoot = Join-Path ([IO.Path]::GetTempPath()) ('meshvenes-installer-test-' + [guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($testRoot)
$script:checks = 0
function Assert-Rejected([scriptblock]$Action) {
    $rejected = $false
    try { & $Action } catch { $rejected = $true }
    if (-not $rejected) { throw 'Unsafe input was accepted.' }
    $script:checks++
}
function New-TestArchive([string[]]$Names) {
    $path = Join-Path $testRoot ([guid]::NewGuid().ToString('N') + '.zip')
    $archive = [IO.Compression.ZipFile]::Open($path, [IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($name in $Names) {
            $writer = New-Object IO.StreamWriter($archive.CreateEntry($name).Open())
            try { $writer.Write('fixture') } finally { $writer.Dispose() }
        }
    } finally { $archive.Dispose() }
    return $path
}
function New-TestManifest([string]$Path) {
    return [pscustomobject]@{
        version = '1.5.1'; url = 'https://venes.org/meshvenes/MeshVenes-1.5.1-win-x64.zip'
        sha256 = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
        sizeBytes = (Get-Item -LiteralPath $Path).Length
    }
}
try {
    $zip = New-TestArchive @('MeshVenes.exe', 'MeshVenes.pri', 'Assets/Map/map.html')
    $manifest = New-TestManifest $zip
    $output = Join-Path $testRoot ("valid [x] & ' " + [char]0xe6 + ' %PATH%')
    Expand-VerifiedRelease $zip $output $manifest
    if (-not (Test-Path -LiteralPath (Join-Path $output 'Assets\Map\map.html'))) { throw 'Valid extraction failed.' }
    $script:checks++
    foreach ($url in 'http://venes.org/meshvenes/MeshVenes-1.5.1-win-x64.zip', 'https://evil.example/release.zip', 'file:///C:/release.zip') {
        $bad = New-TestManifest $zip; $bad.url = $url
        Assert-Rejected { Assert-ReleaseManifest $bad }
    }
    foreach ($version in '../1.5.1', "1.5.1`n", '1.5.1%PATH%', '1.5.1-beta') {
        $bad = New-TestManifest $zip; $bad.version = $version
        Assert-Rejected { Assert-ReleaseManifest $bad }
    }
    $bad = New-TestManifest $zip; $bad.sha256 = 'a' * 64
    Assert-Rejected { Expand-VerifiedRelease $zip (Join-Path $testRoot 'hash') $bad }
    $bad = New-TestManifest $zip; $bad.sizeBytes++
    Assert-Rejected { Expand-VerifiedRelease $zip (Join-Path $testRoot 'size') $bad }
    foreach ($name in '../outside.txt', '..\outside.txt', '/absolute.txt', 'C:/absolute.txt', 'a.txt:stream',
        'NUL.txt', 'a./b.txt', 'a /b.txt', 'payload.msix', 'MESHVENES.EXE') {
        $badZip = New-TestArchive @('MeshVenes.exe', 'MeshVenes.pri', $name)
        $bad = New-TestManifest $badZip
        $destination = Join-Path $testRoot ([guid]::NewGuid().ToString('N'))
        Assert-Rejected { Expand-VerifiedRelease $badZip $destination $bad }
        if (Test-Path -LiteralPath $destination) { throw 'Invalid archive was written before validation.' }
    }
    $badZip = New-TestArchive @('MeshVenes.exe')
    Assert-Rejected { Expand-VerifiedRelease $badZip (Join-Path $testRoot 'missing') (New-TestManifest $badZip) }
    $link = Join-Path $testRoot 'junction'
    $linkTarget = [IO.Directory]::CreateDirectory((Join-Path $testRoot 'junction-target')).FullName
    [void](New-Item -ItemType Junction -Path $link -Target $linkTarget)
    Assert-Rejected { Assert-SafeDirectory (Join-Path $link 'nested') }
    # Delete only the junction itself, never recurse through it.
    [IO.Directory]::Delete($link)
    $shortcut = Join-Path $testRoot 'MeshVenes.lnk'
    Assert-Rejected { Set-MeshVenesShortcut $shortcut (Join-Path $output 'MeshVenes.exe') }
    $shortcutExe = Join-Path $testRoot ("app [x] & ' " + [char]0xe6 + '.exe')
    [IO.File]::WriteAllText($shortcutExe, 'fixture')
    Set-MeshVenesShortcut $shortcut $shortcutExe
    $shell = New-Object -ComObject WScript.Shell
    try {
        if ($shell.CreateShortcut($shortcut).TargetPath -ne $shortcutExe) { throw 'Shortcut target was altered.' }
        $script:checks++
    } finally { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($shell) }
    # Exercise the real activation/state code inside isolated folders. Only the
    # downloader and folder lookup are replaced; extraction and shortcuts are real.
    $script:fixtureRoot = Join-Path $testRoot 'integration'
    $script:fixtureMenu = [IO.Directory]::CreateDirectory((Join-Path $testRoot 'menu')).FullName
    $script:fixtureDesktop = [IO.Directory]::CreateDirectory((Join-Path $testRoot 'desktop')).FullName
    function Get-MeshVenesFolders { @{ Root = $script:fixtureRoot; Menu = $script:fixtureMenu; Desktop = $script:fixtureDesktop } }
    $fixtureExe = Join-Path $testRoot 'fixture.exe'
    $newFixtureExe = Join-Path $testRoot 'fixture-new.exe'
    # The Framework compiler emits native file-version resources; PowerShell 7's
    # Add-Type compiler does not. Use the Windows compiler for both test hosts.
    $fixtureBuilder = Join-Path $testRoot 'build-fixtures.ps1'
    [IO.File]::WriteAllText($fixtureBuilder, @'
param([string]$FirstPath, [string]$SecondPath)
$ErrorActionPreference = 'Stop'
Add-Type -TypeDefinition '[assembly: System.Reflection.AssemblyFileVersion("1.5.1.0")] public class InstallerFixture { public static void Main() {} }' -OutputAssembly $FirstPath -OutputType Library
Add-Type -TypeDefinition '[assembly: System.Reflection.AssemblyFileVersion("1.5.2.0")] public class InstallerFixtureNew { public static void Main() {} }' -OutputAssembly $SecondPath -OutputType Library
'@)
    & "$env:SystemRoot\System32\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -ExecutionPolicy Bypass -File $fixtureBuilder -FirstPath $fixtureExe -SecondPath $newFixtureExe
    if ($LASTEXITCODE -ne 0) { throw 'Failed to compile installer fixtures.' }
    $script:fixtureZip = New-TestArchive @('MeshVenes.pri')
    $archive = [IO.Compression.ZipFile]::Open($script:fixtureZip, [IO.Compression.ZipArchiveMode]::Update)
    try { [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $fixtureExe, 'MeshVenes.exe') }
    finally { $archive.Dispose() }
    $script:fixtureManifest = New-TestManifest $script:fixtureZip
    $script:testSigningKey = New-Object Security.Cryptography.RSACryptoServiceProvider(3072)
    $script:testSigningKey.PersistKeyInCsp = $false
    function Get-ReleasePublicKey { $script:testSigningKey.ToXmlString($false) }
    $signedBytes = [Text.Encoding]::UTF8.GetBytes(($script:fixtureManifest | ConvertTo-Json))
    $signature = $script:testSigningKey.SignData($signedBytes, [Security.Cryptography.HashAlgorithmName]::SHA256, [Security.Cryptography.RSASignaturePadding]::Pkcs1)
    Assert-ManifestSignature $signedBytes $signature
    $script:checks++
    $tampered = $signedBytes.Clone(); $tampered[10] = $tampered[10] -bxor 1
    Assert-Rejected { Assert-ManifestSignature $tampered $signature }
    Assert-Rejected { Assert-ManifestSignature $signedBytes (New-Object byte[] 384) }
    function Save-ReleaseDownload([string]$Url, [string]$Destination, [long]$MaximumBytes) {
        if ($Url.Contains('version.json.sig')) {
            $manifestBytes = [Text.Encoding]::UTF8.GetBytes(($script:fixtureManifest | ConvertTo-Json))
            [IO.File]::WriteAllBytes($Destination, $script:testSigningKey.SignData($manifestBytes, [Security.Cryptography.HashAlgorithmName]::SHA256, [Security.Cryptography.RSASignaturePadding]::Pkcs1))
        }
        elseif ($Url.Contains('version.json')) { [IO.File]::WriteAllText($Destination, ($script:fixtureManifest | ConvertTo-Json)) }
        else { [IO.File]::Copy($script:fixtureZip, $Destination) }
    }
    Invoke-MeshVenesInstall
    $statePath = Join-Path $script:fixtureRoot 'install-state.json'
    $first = Get-Content -LiteralPath $statePath -Raw | ConvertFrom-Json
    Invoke-MeshVenesInstall
    $second = Get-Content -LiteralPath $statePath -Raw | ConvertFrom-Json
    if ($second.current -ne $first.current -or $second.previous) { throw 'Repeated install was not idempotent.' }
    $script:checks++
    $script:fixtureZip = New-TestArchive @('MeshVenes.pri')
    $archive = [IO.Compression.ZipFile]::Open($script:fixtureZip, [IO.Compression.ZipArchiveMode]::Update)
    try { [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $newFixtureExe, 'MeshVenes.exe') }
    finally { $archive.Dispose() }
    $script:fixtureManifest = New-TestManifest $script:fixtureZip
    $script:fixtureManifest.version = '1.5.2'
    $script:fixtureManifest.url = 'https://venes.org/meshvenes/MeshVenes-1.5.2-win-x64.zip'
    Invoke-MeshVenesInstall
    $upgraded = Get-Content -LiteralPath $statePath -Raw | ConvertFrom-Json
    if ($upgraded.previous -ne $first.current -or $upgraded.current -eq $first.current) { throw 'Previous version not retained.' }
    $script:checks++
    $Rollback = $true
    Invoke-MeshVenesInstall
    $rolledBack = Get-Content -LiteralPath $statePath -Raw | ConvertFrom-Json
    if ($rolledBack.current -ne $first.current) { throw 'Rollback did not select the original installation.' }
    $Rollback = $false
    $script:checks++
    $originalShortcutFunction = ${function:Set-MeshVenesShortcut}
    function Set-MeshVenesShortcut([string]$Path, [string]$Executable) { throw 'Simulated shortcut failure.' }
    $stateBefore = [IO.File]::ReadAllText($statePath)
    Assert-Rejected { Invoke-MeshVenesInstall }
    if ([IO.File]::ReadAllText($statePath) -cne $stateBefore) { throw 'Failed activation changed the selected installation.' }
    ${function:Set-MeshVenesShortcut} = $originalShortcutFunction
    Write-Host "Passed $script:checks installer checks."
} finally {
    $resolved = [IO.Path]::GetFullPath($testRoot)
    $prefix = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\meshvenes-installer-test-'
    if ($resolved.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}
