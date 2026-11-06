$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$local = Join-Path $root '.local'
New-Item -ItemType Directory -Force -Path $local | Out-Null
$secret = Join-Path $local 'postgres_password'
if (-not (Test-Path -LiteralPath $secret)) {
    $bytes = New-Object byte[] 36
    $rng = [System.Security.Cryptography.RandomNumberGenerator]::Create()
    try { $rng.GetBytes($bytes) } finally { $rng.Dispose() }
    [IO.File]::WriteAllText($secret, [Convert]::ToBase64String($bytes), [Text.UTF8Encoding]::new($false))
}
Write-Host 'Configuración local preparada; secreto conservado fuera de los archivos versionables.'

