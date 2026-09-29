#requires -Version 7.4
[CmdletBinding()]
param(
    [switch]$Initialize,
    [string]$ManifestPath,
    [string]$PublicKeyOutput,
    [string]$KeyDirectory = (Join-Path $env:LOCALAPPDATA 'MeshVenesReleaseSigning')
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Security.Cryptography.ProtectedData
$keyFile = Join-Path $KeyDirectory 'release-key.dpapi'
if ($Initialize) {
    if (Test-Path -LiteralPath $KeyDirectory) { throw 'Refusing to overwrite an existing signing directory.' }
    [void][IO.Directory]::CreateDirectory($KeyDirectory)
    $acl = Get-Acl -LiteralPath $KeyDirectory
    $acl.SetAccessRuleProtection($true, $false)
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent().User
    $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($identity, 'FullControl', 'ContainerInherit,ObjectInherit', 'None', 'Allow'))
    $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new([Security.Principal.SecurityIdentifier]::new('S-1-5-18'), 'FullControl', 'ContainerInherit,ObjectInherit', 'None', 'Allow'))
    Set-Acl -LiteralPath $KeyDirectory -AclObject $acl
    $rsa = [Security.Cryptography.RSA]::Create(3072)
    try {
        $privateBytes = $rsa.ExportPkcs8PrivateKey()
        try {
            $protected = [Security.Cryptography.ProtectedData]::Protect($privateBytes, $null, [Security.Cryptography.DataProtectionScope]::CurrentUser)
            [IO.File]::WriteAllBytes($keyFile, $protected)
        } finally { [Array]::Clear($privateBytes, 0, $privateBytes.Length) }
        # Portable recovery copy, independent of Windows DPAPI. Keep the password
        # separately in a password manager and move the encrypted key to offline storage.
        $password = [Convert]::ToBase64String([Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
        $pbe = [Security.Cryptography.PbeParameters]::new([Security.Cryptography.PbeEncryptionAlgorithm]::Aes256Cbc, [Security.Cryptography.HashAlgorithmName]::SHA256, 600000)
        $encrypted = $rsa.ExportEncryptedPkcs8PrivateKey([string]$password, $pbe)
        [IO.File]::WriteAllBytes((Join-Path $KeyDirectory 'recovery-key.pk8'), $encrypted)
        [IO.File]::WriteAllText((Join-Path $KeyDirectory 'recovery-password.txt'), $password)
        [IO.File]::WriteAllText((Join-Path $KeyDirectory 'public-key.xml'), $rsa.ToXmlString($false))
    } finally { $rsa.Dispose(); $password = $null }
    Write-Host 'Signing key created with a restricted ACL. Keep the recovery key and password in separate safe backups.'
}
if ($PublicKeyOutput) {
    [IO.File]::Copy((Join-Path $KeyDirectory 'public-key.xml'), [IO.Path]::GetFullPath($PublicKeyOutput), $false)
}
if ($ManifestPath) {
    $bytes = [IO.File]::ReadAllBytes([IO.Path]::GetFullPath($ManifestPath))
    if ($bytes.Length -gt 65536) { throw 'Release manifest is too large.' }
    $privateBytes = [Security.Cryptography.ProtectedData]::Unprotect([IO.File]::ReadAllBytes($keyFile), $null, [Security.Cryptography.DataProtectionScope]::CurrentUser)
    $rsa = [Security.Cryptography.RSA]::Create()
    try {
        $consumed = 0
        $rsa.ImportPkcs8PrivateKey($privateBytes, [ref]$consumed)
        $signature = $rsa.SignData($bytes, [Security.Cryptography.HashAlgorithmName]::SHA256, [Security.Cryptography.RSASignaturePadding]::Pkcs1)
        [IO.File]::WriteAllBytes(([IO.Path]::GetFullPath($ManifestPath) + '.sig'), $signature)
        if (-not $rsa.VerifyData($bytes, $signature, [Security.Cryptography.HashAlgorithmName]::SHA256, [Security.Cryptography.RSASignaturePadding]::Pkcs1)) { throw 'Signature self-check failed.' }
    } finally { $rsa.Dispose(); [Array]::Clear($privateBytes, 0, $privateBytes.Length) }
    Write-Host 'Detached manifest signature created and verified.'
}
