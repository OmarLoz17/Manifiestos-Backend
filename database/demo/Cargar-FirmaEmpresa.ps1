param(
    [Parameter(Mandatory)][string]$Database,
    [Parameter(Mandatory)][string]$Png,
    [string]$Nombre = 'Sanivac del Norte S.A. de C.V.'
)
# Carga la FIRMA PRECARGADA de la empresa (generador de clientes generales) sin guardarla en el repositorio.
# - Copia el PNG (fondo transparente, tinta negra) al almacén local de archivos (Archivos:Ruta o backend/.local/archivos).
# - Registra el archivo y una revisión NUEVA y NO provisional de negocio.firma_generador_precargada por cada
#   empresa/sucursal que ya tiene una (las revisiones anteriores no se reescriben).
# - Repetible: si la huella del PNG ya está cargada para esa empresa, no vuelve a registrar.
# Uso: .\Cargar-FirmaEmpresa.ps1 -Database manifiestos_ejemplo -Png ..\..\..\.local\firma-empresa-negra.png
$ErrorActionPreference = 'Stop'
if ($Database -notmatch '^[a-z0-9_]+$') { throw "Nombre de base no válido: '$Database'." }
if (-not (Test-Path $Png)) { throw "No existe el archivo $Png." }
$Png = (Resolve-Path $Png).Path
$bytes = [IO.File]::ReadAllBytes($Png)
if ($bytes.Length -lt 8 -or $bytes[1] -ne 0x50 -or $bytes[2] -ne 0x4E -or $bytes[3] -ne 0x47) { throw 'El archivo no es un PNG.' }
$hash = (Get-FileHash $Png -Algorithm SHA256).Hash.ToLowerInvariant()
$root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$compose = Join-Path $root 'compose.yaml'
$almacen = Join-Path $root '.local\archivos'
$id = [guid]::NewGuid().ToString()
$clave = "firma-empresa/$id.png"
$destino = Join-Path $almacen ($clave -replace '/', '\')
New-Item -ItemType Directory -Force (Split-Path $destino) | Out-Null
Copy-Item $Png $destino
$nombreSql = $Nombre.Replace("'", "''")
$sql = @"
DO `$`$
DECLARE f record; a uuid;
BEGIN
  FOR f IN SELECT DISTINCT ON (organizacion_id, sucursal_id, empresa_id) * FROM negocio.firma_generador_precargada
           ORDER BY organizacion_id, sucursal_id, empresa_id, revision DESC LOOP
    IF EXISTS (SELECT 1 FROM negocio.archivo ar WHERE (ar.organizacion_id, ar.sucursal_id, ar.id) = (f.organizacion_id, f.sucursal_id, f.archivo_id)
               AND ar.sha256 = decode('$hash', 'hex')) THEN CONTINUE; END IF;
    INSERT INTO negocio.archivo (organizacion_id, sucursal_id, id, sha256, tamano_bytes, mime)
    VALUES (f.organizacion_id, f.sucursal_id, '$id', decode('$hash', 'hex'), $($bytes.Length), 'image/png') ON CONFLICT DO NOTHING;
    INSERT INTO negocio.ubicacion_archivo (organizacion_id, sucursal_id, archivo_id, proveedor, clave_externa, verificada_en)
    VALUES (f.organizacion_id, f.sucursal_id, '$id', 'vps', '$clave', now()) ON CONFLICT DO NOTHING;
    INSERT INTO negocio.firma_generador_precargada (organizacion_id, sucursal_id, empresa_id, revision, nombre_firmante, cargo, archivo_id, es_provisional)
    VALUES (f.organizacion_id, f.sucursal_id, f.empresa_id, f.revision + 1, '$nombreSql', f.cargo, '$id', false);
  END LOOP;
END `$`$;
SELECT count(*) FILTER (WHERE NOT es_provisional) AS firmas_reales FROM negocio.firma_generador_precargada;
"@
$anterior = $OutputEncoding
try {
    $OutputEncoding = [Text.UTF8Encoding]::new($false)
    $salida = $sql | docker compose -f $compose exec -T postgres psql -X -q -At -v ON_ERROR_STOP=1 -U manifiestos_owner -d $Database
    if ($LASTEXITCODE -ne 0) { throw "psql falló en la base $Database." }
    Write-Host "Firma precargada cargada en ${Database}: $salida firma(s) real(es) registrada(s)."
} finally { $OutputEncoding = $anterior }

