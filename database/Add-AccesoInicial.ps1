param(
    [string]$Correo = $env:SEED_ADMIN_EMAIL,
    [Parameter(Mandatory)][string]$Organizacion,
    [Parameter(Mandatory)][string]$EmpresaCodigo,
    [Parameter(Mandatory)][string]$SucursalCodigo,
    [Parameter(Mandatory)][string]$SucursalNombre,
    [string]$Rol = 'operaciones'
)
# Alta inicial en la base local: organización, empresa, sucursal y una entrada de la
# lista de autorizados con permiso de lectura en esa sucursal. Repetible: reutiliza lo
# existente y no cambia rol ni estado de una entrada ya registrada.
# El correo llega por parámetro o SEED_ADMIN_EMAIL; nunca se escribe en el repositorio.
# Empresa y sucursal son inmutables en la base: revisar los nombres antes de ejecutar.
$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($Correo)) { throw 'Indica -Correo o la variable SEED_ADMIN_EMAIL.' }
$Correo = $Correo.Trim().ToLowerInvariant()
if ($Correo -notmatch '^[^@\s]+@[^@\s]+\.[^@\s]+$') { throw 'Correo con formato inválido.' }
foreach ($v in $Organizacion, $EmpresaCodigo, $SucursalCodigo, $SucursalNombre, $Rol) {
    if ([string]::IsNullOrWhiteSpace($v) -or $v.Contains("'") -or $v.Contains('\')) { throw "Valor no permitido: '$v'" }
}
$root = Split-Path $PSScriptRoot -Parent
$compose = Join-Path $root 'compose.yaml'
$sql = @"
\set ON_ERROR_STOP on
\set correo '$Correo'
\set org '$Organizacion'
\set emp '$EmpresaCodigo'
\set suc '$SucursalCodigo'
\set sucnom '$SucursalNombre'
\set rol '$Rol'
BEGIN;
SELECT count(*) AS n FROM negocio.organizacion WHERE nombre = :'org' \gset
\if :n
\else
INSERT INTO negocio.organizacion(nombre) VALUES (:'org');
\endif
SELECT id AS org_id FROM negocio.organizacion WHERE nombre = :'org' \gset
INSERT INTO negocio.empresa(organizacion_id, codigo) VALUES (:'org_id', :'emp') ON CONFLICT DO NOTHING;
SELECT id AS emp_id FROM negocio.empresa WHERE organizacion_id = :'org_id' AND codigo = :'emp' \gset
INSERT INTO negocio.sucursal(organizacion_id, empresa_id, codigo, nombre)
VALUES (:'org_id', :'emp_id', :'suc', :'sucnom') ON CONFLICT DO NOTHING;
SELECT id AS suc_id FROM negocio.sucursal WHERE organizacion_id = :'org_id' AND empresa_id = :'emp_id' AND codigo = :'suc' \gset
INSERT INTO negocio.empresa_sucursal VALUES (:'org_id', :'suc_id', :'emp_id') ON CONFLICT DO NOTHING;
INSERT INTO seguridad.acceso_autorizado(organizacion_id, correo_permitido, activo, rol_codigo)
VALUES (:'org_id', :'correo', true, :'rol') ON CONFLICT DO NOTHING;
SELECT id AS acc_id FROM seguridad.acceso_autorizado WHERE organizacion_id = :'org_id' AND lower(btrim(correo_permitido)) = :'correo' \gset
INSERT INTO seguridad.permiso_sucursal VALUES (:'org_id', :'suc_id', :'acc_id', 'datos.leer') ON CONFLICT DO NOTHING;
COMMIT;
\echo ORGANIZACION_ID=:org_id
"@
$salida = $sql | docker compose -f $compose exec -T postgres psql -X -q -At -U manifiestos_owner -d manifiestos
if ($LASTEXITCODE -ne 0) { throw 'Falló el alta inicial; transacción revertida.' }
$id = ($salida | Select-String '^ORGANIZACION_ID=(.+)$').Matches[0].Groups[1].Value
Write-Host "Alta inicial lista. Acceso:OrganizacionId = $id"
$id
