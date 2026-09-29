#requires -Version 5.1
<#
Install the unsigned, self-contained Windows x64 release for the current user.
Download this script to disk and inspect it before running. No elevation required.
#>
[CmdletBinding()]
param([switch]$DesktopShortcut, [switch]$Rollback)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Assert-SafeDirectory([string]$Path) {
    $full = [IO.Path]::GetFullPath($Path)
    $cursor = $full
    while ($cursor) {
        if (Test-Path -LiteralPath $cursor) {
            $item = Get-Item -LiteralPath $cursor -Force
            if (-not $item.PSIsContainer -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
                throw "Installation paths must be ordinary directories: $cursor"
            }
        }
        $cursor = [IO.Path]::GetDirectoryName($cursor)
    }
    return $full
}

function Assert-ReleaseManifest($Manifest) {
    if ([string]$Manifest.version -cnotmatch '\A(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\z') {
        throw 'Invalid release version.'
    }
    if (-not [version]::TryParse([string]$Manifest.version, [ref]([version]$null))) {
        throw 'Invalid release version.'
    }
    $expectedUrl = 'https://venes.org/meshvenes/MeshVenes-{0}-win-x64.zip' -f $Manifest.version
    if ([string]$Manifest.url -cne $expectedUrl) { throw 'Unexpected release download URL.' }
    if ([string]$Manifest.sha256 -cnotmatch '\A[a-f0-9]{64}\z') { throw 'Invalid SHA-256.' }
    [long]$size = 0
    if (-not [long]::TryParse([string]$Manifest.sizeBytes, [ref]$size) -or $size -le 0 -or $size -gt 1GB) {
        throw 'Invalid release size (maximum 1 GiB).'
    }
}

function Get-ReleasePublicKey { return '<RSAKeyValue><Modulus>uDMXeybP0llPLe9X9AFYAulekHrQQs+H6kaJmyOn7SQQBwGu+m4o3exFedskOu6RCnIscj5bzDTEByPBiSRndAFD1PZRf7O4fKh1xPIdmsItwa3MAiMzvPvmLNtXxTb7vrMygUcyDwmc/uX+k4qHu+YVQlGfdnNiBInd7b4KLGn/ABRHrYW5ZLruhHqpqVFYgK5VkWvlicFuASRwNCv9C95QtLvmFXTMg8gThZB8eR+nUaL+OTMTnWTuKP0hQA0V9OJ6tAKyQ/Yg2LpQMNv4qSjjk/UVxjCtbc4Dzi1zkOVzjGMEHwcAcGqGrZp0MfwOjC4qGdpUJ0yprByawdPSsUVCaJV9nY3MAcYjuwgkZoN1J+GyKuendUaI3YghGpZbHv4/6sekdDHUtmcQY/g+t7Lvb48A0U5iYZP8G++5ooX9j8CT0UV7ENWrMf48fYqAdFtHqE9J2hSmzmrvJdNqN1TguU7wr4gu09NY+/fxLVw9r6JjvYG7rx3o4Big//xt</Modulus><Exponent>AQAB</Exponent></RSAKeyValue>' }

function Assert-ManifestSignature([byte[]]$ManifestBytes, [byte[]]$Signature) {
    if ($ManifestBytes.Length -eq 0 -or $ManifestBytes.Length -gt 65536 -or $Signature.Length -ne 384) {
        throw 'Invalid signed release metadata size.'
    }
    $rsa = [Security.Cryptography.RSA]::Create()
    try {
        $rsa.FromXmlString((Get-ReleasePublicKey))
        if ($rsa.KeySize -ne 3072 -or -not $rsa.VerifyData($ManifestBytes, $Signature, [Security.Cryptography.HashAlgorithmName]::SHA256, [Security.Cryptography.RSASignaturePadding]::Pkcs1)) {
            throw 'The release manifest signature is invalid.'
        }
    } finally { $rsa.Dispose() }
}

function Save-ReleaseDownload([string]$Url, [string]$Destination, [long]$MaximumBytes) {
    # Disable redirects: neither metadata nor executables may move to another origin.
    $handler = New-Object Net.Http.HttpClientHandler
    $handler.AllowAutoRedirect = $false
    $client = New-Object Net.Http.HttpClient($handler)
    $client.Timeout = [TimeSpan]::FromMinutes(10)
    $response = $null; $inputStream = $null; $outputStream = $null
    try {
        $response = $client.GetAsync($Url, [Net.Http.HttpCompletionOption]::ResponseHeadersRead).GetAwaiter().GetResult()
        if ([int]$response.StatusCode -ne 200) { throw "Download failed: HTTP $([int]$response.StatusCode)." }
        if ($response.Content.Headers.ContentLength -gt $MaximumBytes) { throw 'Download is too large.' }
        $inputStream = $response.Content.ReadAsStreamAsync().GetAwaiter().GetResult()
        $outputStream = [IO.File]::Open($Destination, [IO.FileMode]::CreateNew)
        $buffer = New-Object byte[] 81920
        [long]$total = 0
        $deadline = [DateTime]::UtcNow.AddMinutes(10)
        while ($true) {
            $readTask = $inputStream.ReadAsync($buffer, 0, $buffer.Length)
            if (-not $readTask.Wait(30000)) { throw 'Download stalled.' }
            $count = $readTask.GetAwaiter().GetResult()
            if ($count -eq 0) { break }
            $total += $count
            if ($total -gt $MaximumBytes) { throw 'Download exceeded its size limit.' }
            if ([DateTime]::UtcNow -gt $deadline) { throw 'Download timed out.' }
            $outputStream.Write($buffer, 0, $count)
        }
    } finally {
        if ($outputStream) { $outputStream.Dispose() }
        if ($inputStream) { $inputStream.Dispose() }
        if ($response) { $response.Dispose() }
        $client.Dispose()
    }
}

function Expand-VerifiedRelease([string]$ZipPath, [string]$Destination, $Manifest) {
    Assert-ReleaseManifest $Manifest
    if ((Get-Item -LiteralPath $ZipPath).Length -ne [long]$Manifest.sizeBytes) { throw 'Package size mismatch.' }
    if ((Get-FileHash -LiteralPath $ZipPath -Algorithm SHA256).Hash.ToLowerInvariant() -cne $Manifest.sha256) {
        throw 'Package SHA-256 mismatch.'
    }
    $root = (Assert-SafeDirectory $Destination).TrimEnd('\') + '\'
    if (Test-Path -LiteralPath $Destination) { throw 'Extraction requires a new directory.' }
    $archive = [IO.Compression.ZipFile]::OpenRead($ZipPath)
    try {
        $seen = New-Object 'Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
        [long]$expandedSize = 0
        if ($archive.Entries.Count -gt 20000) { throw 'Too many archive entries.' }
        foreach ($entry in $archive.Entries) {
            $name = $entry.FullName.Replace('/', '\')
            $parts = $name.TrimEnd('\').Split('\')
            foreach ($part in $parts) {
                if (-not $part -or $part -in '.', '..' -or $part -match '[<>:"|?*\x00-\x1f]' -or
                    $part.EndsWith('.') -or $part.EndsWith(' ') -or
                    $part -match '^(?i:CON|PRN|AUX|NUL|COM[0-9]|LPT[0-9])(?:\.|$)') {
                    throw 'Unsafe archive path.'
                }
            }
            $target = [IO.Path]::GetFullPath([IO.Path]::Combine($root, $name))
            if (-not $target.StartsWith($root, [StringComparison]::OrdinalIgnoreCase) -or -not $seen.Add($target)) {
                throw 'Duplicate or escaping archive path.'
            }
            $unixType = ($entry.ExternalAttributes -shr 16) -band 0xF000
            if ($unixType -eq 0xA000 -or ($entry.ExternalAttributes -band 0x400)) { throw 'Archive links are not supported.' }
            if ([IO.Path]::GetExtension($name) -in '.msix', '.msixbundle', '.appx', '.appxbundle', '.appinstaller') {
                throw 'Packaged distribution is not supported.'
            }
            $expandedSize += $entry.Length
            if ($expandedSize -gt 4GB) { throw 'Expanded package exceeds 4 GiB.' }
        }
        foreach ($required in 'MeshVenes.exe', 'MeshVenes.pri') {
            $entry = $archive.GetEntry($required)
            if (-not $entry -or $entry.Length -le 0) { throw "Missing release file: $required" }
        }
        # All paths were checked before the first file is written.
        [IO.Compression.ZipFile]::ExtractToDirectory($ZipPath, $Destination)
    } finally { $archive.Dispose() }
}

function Set-MeshVenesShortcut([string]$Path, [string]$Executable) {
    # WScript expands %VARIABLE% in shortcut targets. Fail closed instead of
    # pointing a shortcut somewhere other than the verified install directory.
    if ($Executable.Contains('%')) { throw 'Windows shortcut targets cannot contain percent signs.' }
    $shell = New-Object -ComObject WScript.Shell
    try {
        $shortcut = $shell.CreateShortcut($Path)
        $shortcut.TargetPath = $Executable
        $shortcut.WorkingDirectory = [IO.Path]::GetDirectoryName($Executable)
        $shortcut.Description = 'MeshVenes'
        $shortcut.IconLocation = "$Executable,0"
        $shortcut.Save()
    } finally { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($shell) }
}

function Get-InstalledRelease([string]$Root, [string]$Name) {
    if ($Name -cnotmatch '\A[0-9]+\.[0-9]+\.[0-9]+-[a-f0-9]{32}\z') { throw 'Invalid installed release pointer.' }
    $path = Assert-SafeDirectory (Join-Path $Root "releases\$Name")
    foreach ($required in 'MeshVenes.exe', 'MeshVenes.pri') {
        $file = Get-Item -LiteralPath (Join-Path $path $required) -Force
        if ($file.PSIsContainer -or ($file.Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Invalid installed release.' }
    }
    return $path
}

function Get-MeshVenesFolders {
    return @{
        Root = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'Programs\MeshVenes'
        Menu = [Environment]::GetFolderPath('Programs')
        Desktop = [Environment]::GetFolderPath('DesktopDirectory')
    }
}

function Invoke-MeshVenesInstall {
    if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT -or
        -not [Environment]::Is64BitProcess -or
        [Environment]::OSVersion.Version.Build -lt 19041) {
        throw 'Use 64-bit PowerShell on Windows 10 version 2004 or later.'
    }
    $architecture = [Environment]::GetEnvironmentVariable('PROCESSOR_ARCHITECTURE', 'Machine')
    if ($architecture -ne 'AMD64') { throw 'This release supports Windows x64 only.' }
    Add-Type -AssemblyName System.Net.Http
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    Add-Type -AssemblyName System.IO.Compression
    [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
    $folders = Get-MeshVenesFolders
    $root = Assert-SafeDirectory $folders.Root
    if ($root.Contains('%')) { throw 'The Windows profile path contains percent signs, which are not supported by the shortcut API. Use the portable ZIP.' }
    if (Get-Process -Name MeshVenes -ErrorAction SilentlyContinue) { throw 'Close MeshVenes before installing or rolling back.' }
    [void][IO.Directory]::CreateDirectory($root)
    $lockPath = Join-Path $root 'install.lock'
    if (Test-Path -LiteralPath $lockPath) {
        if ((Get-Item -LiteralPath $lockPath -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Invalid lock file.' }
    }
    $lock = [IO.File]::Open($lockPath, [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
    try {
        $statePath = Join-Path $root 'install-state.json'
        $oldState = $null
        if (Test-Path -LiteralPath $statePath) {
            $oldState = Get-Content -LiteralPath $statePath -Raw | ConvertFrom-Json
            $oldRelease = Get-InstalledRelease $root $oldState.current
        }
        if ($Rollback) {
            if (-not $oldState -or -not $oldState.previous) { throw 'No previous installation is available.' }
            $releaseName = $oldState.previous
            $release = Get-InstalledRelease $root $releaseName
        } else {
            $work = Assert-SafeDirectory (Join-Path $root ('downloads\' + [guid]::NewGuid().ToString('N')))
            [void][IO.Directory]::CreateDirectory($work)
            Write-Host 'Downloading release information...'
            $manifestPath = Join-Path $work 'version.json'
            Save-ReleaseDownload ('https://venes.org/meshvenes/version.json?t=' + [DateTime]::UtcNow.Ticks) $manifestPath 64KB
            $signaturePath = Join-Path $work 'version.json.sig'
            Save-ReleaseDownload ('https://venes.org/meshvenes/version.json.sig?t=' + [DateTime]::UtcNow.Ticks) $signaturePath 384
            Assert-ManifestSignature ([IO.File]::ReadAllBytes($manifestPath)) ([IO.File]::ReadAllBytes($signaturePath))
            $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
            Assert-ReleaseManifest $manifest
            $release = $null
            if ($oldState) {
                $installedVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $oldRelease 'MeshVenes.exe')).FileVersion
                if ($installedVersion -and [version]$installedVersion -gt [version]($manifest.version + '.0')) {
                    throw 'The installed application is newer than the feed. Use -Rollback to select the previous installation.'
                }
                if ($installedVersion -and [version]$installedVersion -eq [version]($manifest.version + '.0')) {
                    Write-Host 'This version is already installed. Refreshing shortcuts.'
                    $releaseName = $oldState.current
                    $release = $oldRelease
                }
            }
            if (-not $release) {
                Write-Host ("Downloading and verifying MeshVenes {0}..." -f $manifest.version)
                $zip = Join-Path $work 'release.zip'
                Save-ReleaseDownload $manifest.url $zip ([long]$manifest.sizeBytes)
                $releaseName = '{0}-{1}' -f $manifest.version, [guid]::NewGuid().ToString('N')
                $release = Assert-SafeDirectory (Join-Path $root "releases\$releaseName")
                Expand-VerifiedRelease $zip $release $manifest
                $fileVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $release 'MeshVenes.exe')).FileVersion
                if (-not $fileVersion -or [version]$fileVersion -ne [version]($manifest.version + '.0')) {
                    throw 'The executable version does not match the release manifest.'
                }
            }
        }
        $exe = Join-Path $release 'MeshVenes.exe'
        $menu = Assert-SafeDirectory $folders.Menu
        $shortcut = Join-Path $menu 'MeshVenes.lnk'
        $desktop = Join-Path (Assert-SafeDirectory $folders.Desktop) 'MeshVenes.lnk'
        $paths = @($shortcut)
        if ($DesktopShortcut -or (Test-Path -LiteralPath $desktop)) { $paths += $desktop }
        $backups = @{}
        foreach ($path in $paths) {
            if (Test-Path -LiteralPath $path) {
                if ((Get-Item -LiteralPath $path -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Invalid shortcut path.' }
                $backups[$path] = [IO.File]::ReadAllBytes($path)
            }
        }
        $previous = if ($oldState) { $oldState.current } else { $null }
        if ($oldState -and $releaseName -eq $oldState.current) { $previous = $oldState.previous }
        $state = @{ current = $releaseName; previous = $previous }
        $temporaryState = Join-Path $root ('state-' + [guid]::NewGuid().ToString('N') + '.json')
        [IO.File]::WriteAllText($temporaryState, ($state | ConvertTo-Json))
        try {
            foreach ($path in $paths) { Set-MeshVenesShortcut $path $exe }
            if (Test-Path -LiteralPath $statePath) { [IO.File]::Replace($temporaryState, $statePath, ($temporaryState + '.previous')) }
            else { [IO.File]::Move($temporaryState, $statePath) }
        } catch {
            foreach ($path in $paths) {
                if ($backups.ContainsKey($path)) { [IO.File]::WriteAllBytes($path, $backups[$path]) }
                elseif (Test-Path -LiteralPath $path) { [IO.File]::Delete($path) }
            }
            throw
        }
        Write-Host "MeshVenes is installed. Start it from the Start menu.`nProgram: $exe"
        Write-Host 'Settings and logs remain in %LOCALAPPDATA%\MeshVenes. Previous releases and downloads are retained.'
    } finally { $lock.Dispose() }
}

# Dot-sourcing exposes the validation functions for offline tests without installing.
if ($MyInvocation.InvocationName -ne '.') { Invoke-MeshVenesInstall }
