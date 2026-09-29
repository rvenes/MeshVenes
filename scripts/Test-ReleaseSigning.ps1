#requires -Version 7.4
$ErrorActionPreference = 'Stop'
$testRoot = Join-Path ([IO.Path]::GetTempPath()) ('meshvenes-signing-test-' + [guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($testRoot)
$rsa = [Security.Cryptography.RSA]::Create(3072)
$password = ConvertTo-SecureString 'fictional test recovery passphrase' -AsPlainText -Force
try {
    $pbe = [Security.Cryptography.PbeParameters]::new([Security.Cryptography.PbeEncryptionAlgorithm]::Aes256Cbc, [Security.Cryptography.HashAlgorithmName]::SHA256, 600000)
    $recoveryPath = Join-Path $testRoot 'fixture.pk8'
    [IO.File]::WriteAllBytes($recoveryPath, $rsa.ExportEncryptedPkcs8PrivateKey('fictional test recovery passphrase', $pbe))
    $keyDirectory = Join-Path $testRoot 'restored'
    & (Join-Path $PSScriptRoot 'Restore-ReleaseSigningKey.ps1') -RecoveryKeyPath $recoveryPath -RecoveryPassword $password -KeyDirectory $keyDirectory
    if ([IO.File]::ReadAllText((Join-Path $keyDirectory 'public-key.xml')) -cne $rsa.ToXmlString($false)) { throw 'Recovery changed the key.' }
    $manifest = Join-Path $testRoot 'version.json'
    $bytes = [Text.Encoding]::UTF8.GetBytes('{"version":"1.5.2"}')
    [IO.File]::WriteAllBytes($manifest, $bytes)
    & (Join-Path $PSScriptRoot 'Release-Signing.ps1') -ManifestPath $manifest -KeyDirectory $keyDirectory
    $signature = [IO.File]::ReadAllBytes($manifest + '.sig')
    if (-not $rsa.VerifyData($bytes, $signature, [Security.Cryptography.HashAlgorithmName]::SHA256, [Security.Cryptography.RSASignaturePadding]::Pkcs1)) { throw 'Recovered key signature failed.' }
    $rejected = $false
    try { & (Join-Path $PSScriptRoot 'Restore-ReleaseSigningKey.ps1') -RecoveryKeyPath $recoveryPath -RecoveryPassword $password -KeyDirectory $keyDirectory } catch { $rejected = $true }
    if (-not $rejected) { throw 'Existing key directory was overwritten.' }
    Write-Host 'Passed signing-key recovery, signing and overwrite-refusal tests with an ephemeral fixture key.'
} finally {
    $rsa.Dispose()
    $password.Dispose()
    $resolved = [IO.Path]::GetFullPath($testRoot)
    $temporaryRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
    if (-not $resolved.StartsWith($temporaryRoot, [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe test cleanup path.' }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
