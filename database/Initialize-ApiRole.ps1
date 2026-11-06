param([switch]$ShowConnectionString)
# Crea o sincroniza el usuario de base que usa la API local: puede conectarse,
# hereda solo los permisos de manifiestos_app (sin superusuario, DDL ni BYPASSRLS).
# La contraseña se genera en .local/api_password (ignorado por Git) y se envía a
# psql por stdin, no como argumento de línea de comandos. Nunca se imprime.
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$compose = Join-Path $root 'compose.yaml'
$local = Join-Path $root '.local'
New-Item -ItemType Directory -Force -Path $local | Out-Null
$secret = Join-Path $local 'api_password'
if (-not (Test-Path -LiteralPath $secret)) {
    $bytes = New-Object byte[] 32
    $rng = [System.Security.Cryptography.RandomNumberGenerator]::Create()
    try { $rng.GetBytes($bytes) } finally { $rng.Dispose() }
    # Solo caracteres alfanuméricos: evita escapes en la cadena de conexión.
    $pw = -join ($bytes | ForEach-Object { '{0:x2}' -f $_ })
    [IO.File]::WriteAllText($secret, $pw, [Text.UTF8Encoding]::new($false))
}
$pw = [IO.File]::ReadAllText($secret).Trim()
if ($pw -notmatch '^[0-9a-f]{64}$') { throw 'Formato inesperado en .local/api_password.' }
$sql = @"
\set ON_ERROR_STOP on
\set pw '$pw'
SELECT CASE WHEN EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'manifiestos_api')
    THEN format('ALTER ROLE manifiestos_api LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOBYPASSRLS INHERIT PASSWORD %L', :'pw')
    ELSE format('CREATE ROLE manifiestos_api LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOBYPASSRLS INHERIT PASSWORD %L', :'pw')
END \gexec
GRANT manifiestos_app TO manifiestos_api;
"@
$sql | docker compose -f $compose exec -T postgres psql -X -q -U manifiestos_owner -d manifiestos | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'No se pudo preparar el usuario manifiestos_api.' }
Write-Host 'Usuario manifiestos_api preparado; contraseña conservada en .local/api_password.'
if ($ShowConnectionString) {
    # Solo para pegarla en User Secrets en esta máquina; no copiarla a archivos versionados.
    "Host=127.0.0.1;Port=55432;Database=manifiestos;Username=manifiestos_api;Password=$pw"
}
