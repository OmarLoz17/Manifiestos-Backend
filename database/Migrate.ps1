param([string]$Database = 'manifiestos')
$ErrorActionPreference = 'Stop'
# Solo la base local, la demo (database/demo) y las temporales de prueba.
if ($Database -cnotmatch '^manifiestos(?:_test_[a-z0-9_]+|_demo)?$') { throw 'Base no permitida por el ejecutor local.' }
$root = Split-Path $PSScriptRoot -Parent
$compose = Join-Path $root 'compose.yaml'
$files = @(Get-ChildItem (Join-Path $PSScriptRoot 'migrations') -Filter '*.sql' | Sort-Object Name)
if (-not $files.Count) { throw 'No hay migraciones.' }
$sql = [Collections.Generic.List[string]]::new()
$sql.Add('\set ON_ERROR_STOP on')
$sql.Add('BEGIN;')
$sql.Add("SELECT pg_advisory_xact_lock(824070105);")
$sql.Add('CREATE SCHEMA IF NOT EXISTS infra;')
$sql.Add('CREATE TABLE IF NOT EXISTS infra.migracion (nombre text PRIMARY KEY, sha256 text NOT NULL, aplicada_en timestamptz NOT NULL DEFAULT clock_timestamp());')
foreach ($file in $files) {
    if ($file.Name -notmatch '^\d{4}_[a-z0-9_]+\.sql$') { throw "Nombre inválido: $($file.Name)" }
    $name = $file.Name
    $hash = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    $sql.Add("SELECT EXISTS (SELECT 1 FROM infra.migracion WHERE nombre = '$name') AS aplicada \gset")
    $sql.Add('\if :aplicada')
    $sql.Add("DO `$check`$ BEGIN IF NOT EXISTS (SELECT 1 FROM infra.migracion WHERE nombre = '$name' AND sha256 = '$hash') THEN RAISE EXCEPTION 'Migracion modificada: $name'; END IF; END `$check`$;")
    $sql.Add('\else')
    $sql.Add([IO.File]::ReadAllText($file.FullName))
    $sql.Add("INSERT INTO infra.migracion(nombre, sha256) VALUES ('$name', '$hash');")
    $sql.Add('\endif')
}
$sql.Add('COMMIT;')
$oldEncoding = $OutputEncoding
try {
    $OutputEncoding = [Text.UTF8Encoding]::new($false)
    ($sql -join "`n") | docker compose -f $compose exec -T postgres psql -X -v ON_ERROR_STOP=1 -U manifiestos_owner -d $Database
    if ($LASTEXITCODE -ne 0) { throw 'Migración fallida; transacción revertida.' }
} finally { $OutputEncoding = $oldEncoding }

