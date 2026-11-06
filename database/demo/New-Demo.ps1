param(
    [Parameter(Mandatory)][string]$Database,
    [string]$CorreoAcceso
)
# Base demo con datos 100 % ficticios (datos_demo.sql) para probar el panel.
# - Crea la base si no existe, aplica las migraciones y siembra los datos demo.
# - Si la base ya tiene la organización demo, NO vuelve a sembrar (repetible).
# - Con -CorreoAcceso autoriza esa cuenta (rol operaciones, datos.leer y datos.escribir) en la
#   sucursal demo, para entrar con Google real a la demo. El correo no se guarda en el repositorio.
# - NUNCA borra una base. Solo admite manifiestos_demo o bases temporales manifiestos_test_*;
#   la base "manifiestos" queda excluida.
$ErrorActionPreference = 'Stop'
if ($Database -cnotmatch '^manifiestos_(?:demo|test_[a-z0-9_]+)$') {
    throw "Base no permitida para la demo: '$Database'. Usa manifiestos_demo o manifiestos_test_<sufijo>."
}
$OrganizacionDemo = 'de000000-0000-4000-8000-000000000001'
$SucursalDemo = 'de000000-0000-4000-8000-000000000003'
$root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$compose = Join-Path $root 'compose.yaml'

function Invoke-Psql([string]$Base, [string]$Sql) {
    $anterior = $OutputEncoding
    try {
        $OutputEncoding = [Text.UTF8Encoding]::new($false)
        $salida = $Sql | docker compose -f $compose exec -T postgres psql -X -q -At -v ON_ERROR_STOP=1 -U manifiestos_owner -d $Base
        if ($LASTEXITCODE -ne 0) { throw "psql falló en la base $Base." }
        return $salida
    } finally { $OutputEncoding = $anterior }
}

$existe = Invoke-Psql 'postgres' "SELECT count(*) FROM pg_database WHERE datname = '$Database';"
if ("$existe".Trim() -eq '0') {
    docker compose -f $compose exec -T postgres createdb -U manifiestos_owner $Database
    if ($LASTEXITCODE -ne 0) { throw "No se pudo crear la base $Database." }
    Write-Host "Base $Database creada."
} else {
    Write-Host "La base $Database ya existe; se conserva."
}

& (Join-Path (Split-Path $PSScriptRoot -Parent) 'Migrate.ps1') -Database $Database

$sembrada = Invoke-Psql $Database "SELECT count(*) FROM negocio.organizacion WHERE id = '$OrganizacionDemo';"
if ("$sembrada".Trim() -eq '0') {
    docker compose -f $compose exec -T postgres psql -X -q -v ON_ERROR_STOP=1 -U manifiestos_owner -d $Database -f /workspace/database/demo/datos_demo.sql
    if ($LASTEXITCODE -ne 0) { throw 'Falló la carga de datos demo; transacción revertida.' }
} else {
    Write-Host 'La organización demo ya existe; no se vuelven a sembrar los datos.'
}

if ($CorreoAcceso) {
    $correo = $CorreoAcceso.Trim().ToLowerInvariant()
    if ($correo -notmatch '^[^@\s''\\]+@[^@\s''\\]+\.[^@\s''\\]+$') { throw 'Correo con formato inválido.' }
    # Alta si no existe; no cambia rol ni estado de una entrada ya registrada, solo agrega los permisos.
    $estado = Invoke-Psql $Database @"
BEGIN;
INSERT INTO seguridad.acceso_autorizado (organizacion_id, correo_permitido, activo, rol_codigo)
VALUES ('$OrganizacionDemo', '$correo', true, 'operaciones') ON CONFLICT DO NOTHING;
INSERT INTO seguridad.permiso_sucursal
SELECT '$OrganizacionDemo', '$SucursalDemo', a.id, p
FROM seguridad.acceso_autorizado a, unnest(ARRAY['datos.leer', 'datos.escribir']) p
WHERE a.organizacion_id = '$OrganizacionDemo' AND lower(btrim(a.correo_permitido)) = '$correo'
ON CONFLICT DO NOTHING;
SELECT CASE WHEN activo AND rol_codigo = 'operaciones' THEN 'ok' ELSE 'revisar' END
FROM seguridad.acceso_autorizado WHERE organizacion_id = '$OrganizacionDemo' AND lower(btrim(correo_permitido)) = '$correo';
COMMIT;
"@
    if ("$estado".Trim() -ne 'ok') {
        Write-Warning 'La entrada ya existía con otro rol o inactiva; no se modificó. Revísala en la base demo.'
    } else {
        Write-Host 'Cuenta autorizada en la sucursal demo (operaciones, lectura y escritura).'
    }
}

Write-Host "Demo lista en $Database. Acceso:OrganizacionId = $OrganizacionDemo"
