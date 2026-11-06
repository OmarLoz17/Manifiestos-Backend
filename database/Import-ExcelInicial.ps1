param(
    [Parameter(Mandatory)][string]$Archivo,
    [string]$SucursalCodigo = 'SANIVAC-NORTE',
    [switch]$Aplicar
)
# Carga inicial desde el Excel "Formato Alta MD Septico 2026" a la base local.
# Sin -Aplicar: VISTA PREVIA. Ejecuta todo dentro de una transacción, muestra lo que se
# crearía y hace ROLLBACK (no escribe nada). Con -Aplicar: misma transacción con COMMIT.
# Repetible: lo que ya existe se reporta y no se duplica.
# Requiere Python con openpyxl. El Excel no se modifica ni se copia al repositorio.
$ErrorActionPreference = 'Stop'
if (-not (Test-Path -LiteralPath $Archivo)) { throw "No existe el archivo: $Archivo" }
$root = Split-Path $PSScriptRoot -Parent
$compose = Join-Path $root 'compose.yaml'
$generador = Join-Path $PSScriptRoot 'importar\excel_inicial.py'

$env:PYTHONIOENCODING = 'utf-8'
$sql = & python -I $generador $Archivo $SucursalCodigo | Out-String
if ($LASTEXITCODE -ne 0) { throw 'No se pudo leer el Excel.' }

$fin = if ($Aplicar) { 'COMMIT;' } else { 'ROLLBACK;' }
$script = "\set ON_ERROR_STOP on`nBEGIN;`n$sql`n$fin`n"
$oldEncoding = $OutputEncoding
try {
    $OutputEncoding = [Text.UTF8Encoding]::new($false)
    [Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
    $script | docker compose -f $compose exec -T postgres psql -X -q -U manifiestos_owner -d manifiestos
    if ($LASTEXITCODE -ne 0) { throw 'Falló la carga; transacción revertida.' }
} finally { $OutputEncoding = $oldEncoding }
if ($Aplicar) { Write-Host 'Carga aplicada.' } else { Write-Host 'VISTA PREVIA: no se escribió nada (ROLLBACK).' }
