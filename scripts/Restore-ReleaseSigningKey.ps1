#requires -Version 7.4
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$RecoveryKeyPath,
    [Security.SecureString]$RecoveryPassword,
    [string]$KeyDirectory = (Join-Path $env:LOCALAPPDATA 'MeshVenesReleaseSigning')
)
$ErrorActionPreference = 'Stop'
if (Test-Path -LiteralPath $KeyDirectory) { throw 'Refusing to overwrite an existing signing directory. Choose a new empty path.' }
if (-not $RecoveryPassword) { $RecoveryPassword = Read-Host 'Recovery key password' -AsSecureString }
Add-Type -AssemblyName System.Security.Cryptography.ProtectedData
$rsa = [Security.Cryptography.RSA]::Create()
$pointer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($RecoveryPassword)
try {
    $password = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($pointer)
    $consumed = 0
    $encrypted = [IO.File]::ReadAllBytes([IO.Path]::GetFullPath($RecoveryKeyPath))
    $rsa.ImportEncryptedPkcs8PrivateKey([string]$password, $encrypted, [ref]$consumed)
    if ($rsa.KeySize -ne 3072 -or $consumed -ne $encrypted.Length) { throw 'Unexpected recovery key format.' }
    [void][IO.Directory]::CreateDirectory($KeyDirectory)
    $acl = Get-Acl -LiteralPath $KeyDirectory
    $acl.SetAccessRuleProtection($true, $false)
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent().User
    $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($identity, 'FullControl', 'ContainerInherit,ObjectInherit', 'None', 'Allow'))
    $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new([Security.Principal.SecurityIdentifier]::new('S-1-5-18'), 'FullControl', 'ContainerInherit,ObjectInherit', 'None', 'Allow'))
    Set-Acl -LiteralPath $KeyDirectory -AclObject $acl
    $privateBytes = $rsa.ExportPkcs8PrivateKey()
    try {
        $protected = [Security.Cryptography.ProtectedData]::Protect($privateBytes, $null, [Security.Cryptography.DataProtectionScope]::CurrentUser)
        [IO.File]::WriteAllBytes((Join-Path $KeyDirectory 'release-key.dpapi'), $protected)
        [IO.File]::WriteAllText((Join-Path $KeyDirectory 'public-key.xml'), $rsa.ToXmlString($false))
    } finally { [Array]::Clear($privateBytes, 0, $privateBytes.Length) }
    Write-Host 'Signing key restored for this Windows account. Verify its public key against the repository before signing.'
} finally {
    $rsa.Dispose()
    $password = $null
    [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($pointer)
}
