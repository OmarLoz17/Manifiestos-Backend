param(
    [Parameter(Mandatory)][string]$Correo,
    [Parameter(Mandatory)][Guid]$OrganizacionId,
    [ValidatePattern('^manifiestos(?:_demo|_ejemplo|_test_[a-z0-9_]+)?$')][string]$Database = 'manifiestos',
    [Guid]$SucursalId = [Guid]::Empty,
    [Guid]$AccesoOperacionesId = [Guid]::Empty,
    [string]$Nombre,
    [Guid]$InstalacionId = [Guid]::Empty
)
# Alta local explícita, sin credenciales Google ni datos reales versionados.
# Sin Nombre/Sucursal solo autoriza el correo: NO inventa persona ni habilita un teléfono.
# Con Nombre/Sucursal/AccesoOperaciones usa alta_operador y sus controles existentes.
# InstalacionId procede de la app; no es una prueba de identidad ni se autoautoriza al hacer login.
$ErrorActionPreference = 'Stop'
$Correo = $Correo.Trim().ToLowerInvariant()
if ($Correo -notmatch '^[^@\s]+@[^@\s]+\.[^@\s]+$' -or $Correo.Length -gt 200) { throw 'Correo inválido.' }
if ($OrganizacionId -eq [Guid]::Empty) { throw 'Falta organización.' }
if ($Nombre -and ($SucursalId -eq [Guid]::Empty -or $AccesoOperacionesId -eq [Guid]::Empty)) { throw 'Nombre requiere sucursal y acceso de Operaciones que registra el alta.' }
if ($InstalacionId -ne [Guid]::Empty -and -not $Nombre) { throw 'Registrar instalación requiere nombre y sucursal confirmados.' }
$compose = Join-Path (Split-Path $PSScriptRoot -Parent) 'compose.yaml'
$sql = @'
\set ON_ERROR_STOP on
BEGIN;
SELECT set_config('app.organizacion_id', :'org', true) AS registro_org \gset
SELECT set_config('app.acceso_id', :'operaciones', true) AS registro_ops \gset
SELECT set_config('registro.correo', :'correo', true) AS registro_correo \gset
SELECT set_config('registro.nombre', :'nombre', true) AS registro_nombre \gset
SELECT set_config('registro.sucursal', :'sucursal', true) AS registro_sucursal \gset
SELECT set_config('registro.instalacion', :'instalacion', true) AS registro_instalacion \gset
DO $registro$
DECLARE o uuid:=current_setting('app.organizacion_id')::uuid; c text:=current_setting('registro.correo');
    n text:=nullif(btrim(current_setting('registro.nombre')),''); s uuid:=current_setting('registro.sucursal')::uuid;
    i uuid:=current_setting('registro.instalacion')::uuid; a uuid; p uuid; d uuid; anterior record;
BEGIN
    -- Serializa con las altas/ediciones del panel para no vincular el correo a dos personas.
    PERFORM pg_advisory_xact_lock(hashtextextended('cuenta:'||o::text||':'||c,0));
    PERFORM negocio.autorizar_cuenta_operador(o,c);
    SELECT id INTO a FROM seguridad.acceso_autorizado WHERE organizacion_id=o AND lower(btrim(correo_permitido))=c;
    IF EXISTS(SELECT 1 FROM seguridad.permiso_sucursal WHERE organizacion_id=o AND acceso_id=a) THEN
        RAISE EXCEPTION 'La cuenta tiene permisos previos de panel: revisar antes de registrar como operador';
    END IF;
    IF n IS NOT NULL THEN
        IF NOT coalesce(seguridad.puede(o,s,'datos.escribir'),false) OR NOT EXISTS(
            SELECT 1 FROM seguridad.acceso_autorizado x WHERE x.organizacion_id=o AND x.id=seguridad.contexto_acceso() AND x.rol_codigo='operaciones' AND x.activo) THEN
            RAISE EXCEPTION 'Operaciones no tiene escritura en la sucursal';
        END IF;
        SELECT f.persona_id INTO p FROM negocio.operador_ficha f
        WHERE f.organizacion_id=o AND f.sucursal_id=s AND f.correo=c
          AND f.revision=(SELECT max(x.revision) FROM negocio.operador_ficha x WHERE (x.organizacion_id,x.sucursal_id,x.persona_id)=(f.organizacion_id,f.sucursal_id,f.persona_id));
        IF p IS NULL THEN p:=negocio.alta_operador(o,s,n,NULL,c);
        ELSIF NOT EXISTS(SELECT 1 FROM negocio.operador_ficha f WHERE f.organizacion_id=o AND f.sucursal_id=s AND f.persona_id=p
            AND f.revision=(SELECT max(x.revision) FROM negocio.operador_ficha x WHERE (x.organizacion_id,x.sucursal_id,x.persona_id)=(f.organizacion_id,f.sucursal_id,f.persona_id)) AND f.nombre_completo=n) THEN
            RAISE EXCEPTION 'La cuenta ya está vinculada con otro nombre. No se reatribuye';
        END IF;
        IF i<>'00000000-0000-0000-0000-000000000000'::uuid THEN
            SELECT dispositivo_id INTO d FROM seguridad.instalacion_app WHERE organizacion_id=o AND id=i FOR UPDATE;
            IF d IS NULL THEN
                INSERT INTO seguridad.dispositivo(organizacion_id) VALUES(o) RETURNING id INTO d;
                INSERT INTO seguridad.instalacion_app(organizacion_id,id,dispositivo_id,habilitada) VALUES(o,i,d,true);
            END IF;
            PERFORM pg_advisory_xact_lock(hashtextextended('registro-equipo:'||o::text||':'||d::text,0));
            SELECT * INTO anterior FROM seguridad.asignacion_equipo WHERE organizacion_id=o AND dispositivo_id=d ORDER BY revision DESC LIMIT 1;
            IF anterior.id IS NULL THEN
                INSERT INTO seguridad.asignacion_equipo(organizacion_id,sucursal_id,dispositivo_id,acceso_id,persona_id,revision,desde)
                VALUES(o,s,d,a,p,1,clock_timestamp());
            ELSIF anterior.acceso_id<>a OR anterior.persona_id<>p OR anterior.sucursal_id<>s OR anterior.hasta IS NOT NULL OR anterior.desde>now() THEN
                RAISE EXCEPTION 'El equipo tiene otra entrega. Resolver pendientes antes de reasignarlo';
            END IF;
            UPDATE seguridad.instalacion_app SET habilitada=true WHERE organizacion_id=o AND id=i;
        END IF;
    END IF;
END $registro$;
COMMIT;
SELECT 'Cuenta de operador autorizada; sin permisos del panel.';
'@
$argsPsql = @('-X','-q','-v','ON_ERROR_STOP=1','-v',"correo=$Correo",'-v',"org=$OrganizacionId",'-v',"nombre=$Nombre",'-v',"sucursal=$SucursalId",'-v',"operaciones=$AccesoOperacionesId",'-v',"instalacion=$InstalacionId",'-U','manifiestos_owner','-d',$Database)
$antes = $OutputEncoding
try {
    $OutputEncoding = [Text.UTF8Encoding]::new($false)
    $sql | docker compose -f $compose exec -T postgres psql @argsPsql
    if ($LASTEXITCODE -ne 0) { throw 'Alta de operador fallida; transacción revertida.' }
} finally { $OutputEncoding = $antes }
