\set ON_ERROR_STOP on
CREATE FUNCTION pg_temp.uid(t text) RETURNS uuid LANGUAGE sql IMMUTABLE AS $$ SELECT md5(t)::uuid $$;
CREATE TEMP TABLE comprobacion(nombre text);
GRANT INSERT, SELECT ON comprobacion TO manifiestos_app;
CREATE FUNCTION pg_temp.afirmar(ok boolean,n text) RETURNS void LANGUAGE plpgsql AS $$
BEGIN IF ok IS DISTINCT FROM true THEN RAISE EXCEPTION 'FALLO: %',n; END IF; INSERT INTO comprobacion VALUES(n); END $$;
CREATE FUNCTION pg_temp.rechaza(q text,estado text,n text) RETURNS void LANGUAGE plpgsql AS $$
DECLARE obtenido text;
BEGIN
    BEGIN EXECUTE q; EXCEPTION WHEN OTHERS THEN GET STACKED DIAGNOSTICS obtenido=RETURNED_SQLSTATE; END;
    PERFORM pg_temp.afirmar(obtenido=estado,n||' ['||coalesce(obtenido,'sin error')||']');
END $$;
BEGIN;
INSERT INTO negocio.organizacion VALUES (pg_temp.uid('oa'),'Grupo Ficticio A'),(pg_temp.uid('ob'),'Grupo Ficticio B');
INSERT INTO negocio.empresa VALUES (pg_temp.uid('oa'),pg_temp.uid('ea'),'EMP-A'),(pg_temp.uid('oa'),pg_temp.uid('ec'),'CLIENTE'),(pg_temp.uid('ob'),pg_temp.uid('eb'),'EMP-B');
INSERT INTO negocio.sucursal(organizacion_id,id,empresa_id,codigo,nombre) VALUES
(pg_temp.uid('oa'),pg_temp.uid('sa'),pg_temp.uid('ea'),'A','Sucursal ficticia A'),
(pg_temp.uid('oa'),pg_temp.uid('sa2'),pg_temp.uid('ea'),'A2','Sucursal ficticia A2'),
(pg_temp.uid('ob'),pg_temp.uid('sb'),pg_temp.uid('eb'),'B','Sucursal ficticia B');
INSERT INTO negocio.empresa_sucursal VALUES
(pg_temp.uid('oa'),pg_temp.uid('sa'),pg_temp.uid('ea')),(pg_temp.uid('oa'),pg_temp.uid('sa'),pg_temp.uid('ec')),
(pg_temp.uid('oa'),pg_temp.uid('sa2'),pg_temp.uid('ea')),(pg_temp.uid('ob'),pg_temp.uid('sb'),pg_temp.uid('eb'));
INSERT INTO negocio.ficha_empresa(organizacion_id,id,empresa_id,revision,nombre_fiscal,calle) VALUES
(pg_temp.uid('oa'),pg_temp.uid('fa'),pg_temp.uid('ea'),1,'Empresa Ficticia A','Calle original'),
(pg_temp.uid('oa'),pg_temp.uid('fc'),pg_temp.uid('ec'),1,'Cliente Ficticio','Calle del cliente');
INSERT INTO negocio.cliente VALUES
(pg_temp.uid('oa'),pg_temp.uid('sa'),pg_temp.uid('ca'),pg_temp.uid('ec'),'C-A'),
(pg_temp.uid('oa'),pg_temp.uid('sa2'),pg_temp.uid('ca2'),pg_temp.uid('ea'),'C-A2'),
(pg_temp.uid('ob'),pg_temp.uid('sb'),pg_temp.uid('cb'),pg_temp.uid('eb'),'C-B');
INSERT INTO negocio.vehiculo VALUES(pg_temp.uid('oa'),pg_temp.uid('sa'),pg_temp.uid('va'),'V-TEST');
INSERT INTO negocio.ficha_vehiculo(organizacion_id,sucursal_id,id,vehiculo_id,revision,capacidad_id) VALUES
(pg_temp.uid('oa'),pg_temp.uid('sa'),pg_temp.uid('fv'),pg_temp.uid('va'),1,1);
INSERT INTO seguridad.persona VALUES(pg_temp.uid('oa'),pg_temp.uid('p1'),'Persona ficticia uno'),(pg_temp.uid('oa'),pg_temp.uid('p2'),'Persona ficticia dos');
INSERT INTO seguridad.dispositivo VALUES(pg_temp.uid('oa'),pg_temp.uid('d'),'TEST-01');
INSERT INTO seguridad.instalacion_app VALUES(pg_temp.uid('oa'),pg_temp.uid('i'),pg_temp.uid('d'),true);
INSERT INTO seguridad.acceso_autorizado(organizacion_id,id,correo_permitido,activo) VALUES(pg_temp.uid('oa'),pg_temp.uid('a'),'equipo@example.invalid',true);
INSERT INTO seguridad.permiso_sucursal VALUES(pg_temp.uid('oa'),pg_temp.uid('sa'),pg_temp.uid('a'),'datos.leer'),(pg_temp.uid('oa'),pg_temp.uid('sa'),pg_temp.uid('a'),'datos.escribir');
INSERT INTO seguridad.asignacion_equipo(organizacion_id,sucursal_id,id,dispositivo_id,acceso_id,persona_id,revision,desde) VALUES
(pg_temp.uid('oa'),pg_temp.uid('sa'),pg_temp.uid('as1'),pg_temp.uid('d'),pg_temp.uid('a'),pg_temp.uid('p1'),1,'2026-01-01Z');
INSERT INTO negocio.orden_servicio VALUES(pg_temp.uid('oa'),pg_temp.uid('sa'),pg_temp.uid('o'),pg_temp.uid('ca'),'Orden ficticia');
INSERT INTO negocio.orden_version(organizacion_id,sucursal_id,id,orden_id,revision,generador_empresa_id,generador_ficha_id,criterio_generador) VALUES
(pg_temp.uid('oa'),pg_temp.uid('sa'),pg_temp.uid('ov'),pg_temp.uid('o'),1,pg_temp.uid('ea'),pg_temp.uid('fa'),'fosa_propia_rentada');
INSERT INTO negocio.captura(organizacion_id,sucursal_id,id,asignacion_equipo_id,dispositivo_id,instalacion_app_id,orden_version_id,ocurrida_en) VALUES
(pg_temp.uid('oa'),pg_temp.uid('sa'),pg_temp.uid('cap'),pg_temp.uid('as1'),pg_temp.uid('d'),pg_temp.uid('i'),pg_temp.uid('ov'),'2026-01-02Z');
INSERT INTO negocio.contenido_aceptado(organizacion_id,sucursal_id,id,captura_id,formato,version_contrato,contenido) VALUES
(pg_temp.uid('oa'),pg_temp.uid('sa'),pg_temp.uid('cont'),pg_temp.uid('cap'),'text/plain','prueba',convert_to('Aceptación ficticia: succión','UTF8'));
INSERT INTO negocio.archivo VALUES(pg_temp.uid('oa'),pg_temp.uid('sa'),pg_temp.uid('file'),sha256('bytes ficticios'::bytea),15,'application/octet-stream');
INSERT INTO negocio.firma_generador VALUES(pg_temp.uid('oa'),pg_temp.uid('sa'),pg_temp.uid('firma'),pg_temp.uid('cont'),pg_temp.uid('file'),'Firmante ficticio','2026-01-02Z');
INSERT INTO negocio.descarga(organizacion_id,sucursal_id,id,cliente_id) VALUES(pg_temp.uid('oa'),pg_temp.uid('sa'),pg_temp.uid('des'),pg_temp.uid('ca'));
INSERT INTO negocio.manifiesto VALUES(pg_temp.uid('oa'),pg_temp.uid('sa'),pg_temp.uid('m'),pg_temp.uid('ca'),pg_temp.uid('des'));
INSERT INTO negocio.version_manifiesto(organizacion_id,sucursal_id,id,manifiesto_id,revision) VALUES(pg_temp.uid('oa'),pg_temp.uid('sa'),pg_temp.uid('vm'),pg_temp.uid('m'),1);
INSERT INTO negocio.participante_manifiesto(organizacion_id,sucursal_id,version_id,rol,empresa_id,ficha_empresa_id,origen_domicilio) VALUES
(pg_temp.uid('oa'),pg_temp.uid('sa'),pg_temp.uid('vm'),'generador',pg_temp.uid('ea'),pg_temp.uid('fa'),'fiscal');
INSERT INTO negocio.detalle_manifiesto(organizacion_id,sucursal_id,version_id,renglon,descripcion_residuo,cantidad,unidad_cantidad,naturaleza_cantidad,metodo_cuantificacion) VALUES
(pg_temp.uid('oa'),pg_temp.uid('sa'),pg_temp.uid('vm'),1,'Residuo ficticio',10,'m3','estimada','Ejemplo sin factor real');

SELECT pg_temp.afirmar((SELECT array_agg(valor_m3 ORDER BY valor_m3)=ARRAY[10,15,18]::numeric[] FROM catalogo.capacidad_vehiculo),'capacidades iniciales');
SELECT pg_temp.rechaza($q$INSERT INTO negocio.ficha_vehiculo(organizacion_id,sucursal_id,vehiculo_id,revision,capacidad_id) VALUES(pg_temp.uid('oa'),pg_temp.uid('sa'),pg_temp.uid('va'),2,99)$q$,'23503','capacidad inexistente');
SELECT pg_temp.rechaza($q$INSERT INTO negocio.orden_servicio(organizacion_id,sucursal_id,cliente_id) VALUES(pg_temp.uid('oa'),pg_temp.uid('sa'),pg_temp.uid('cb'))$q$,'23503','FK impide otra organización');
SELECT pg_temp.rechaza($q$INSERT INTO negocio.orden_servicio(organizacion_id,sucursal_id,cliente_id) VALUES(pg_temp.uid('oa'),pg_temp.uid('sa'),pg_temp.uid('ca2'))$q$,'23503','FK impide otra sucursal');
SELECT pg_temp.rechaza($q$UPDATE negocio.ficha_empresa SET calle='Sobrescrito' WHERE id=pg_temp.uid('fa')$q$,'23514','domicilio histórico inmutable');
SELECT pg_temp.rechaza($q$UPDATE negocio.contenido_aceptado SET contenido='alterado' WHERE id=pg_temp.uid('cont')$q$,'23514','contenido firmado inmutable');
SELECT pg_temp.rechaza($q$DELETE FROM negocio.archivo WHERE id=pg_temp.uid('file')$q$,'23514','no borrar evidencia');
SELECT pg_temp.rechaza($q$DELETE FROM negocio.organizacion WHERE id=pg_temp.uid('oa')$q$,'23001','no cascada destructiva');
SELECT pg_temp.rechaza($q$INSERT INTO negocio.orden_version(organizacion_id,sucursal_id,orden_id,revision) VALUES(pg_temp.uid('oa'),pg_temp.uid('sa'),pg_temp.uid('o'),1)$q$,'23505','revisión duplicada');
SELECT pg_temp.rechaza($q$INSERT INTO negocio.posicion_captura(organizacion_id,sucursal_id,captura_id,latitud,longitud,lectura_en) VALUES(pg_temp.uid('oa'),pg_temp.uid('sa'),pg_temp.uid('cap'),91,0,now())$q$,'23514','latitud inválida');
SELECT pg_temp.rechaza($q$INSERT INTO negocio.manifiesto(organizacion_id,sucursal_id,cliente_id,descarga_id) VALUES(pg_temp.uid('oa'),pg_temp.uid('sa'),pg_temp.uid('ca'),pg_temp.uid('des'))$q$,'23505','un manifiesto por descarga');
SELECT pg_temp.rechaza($q$INSERT INTO negocio.detalle_manifiesto(organizacion_id,sucursal_id,version_id,renglon,cantidad,unidad_cantidad,naturaleza_cantidad,metodo_cuantificacion) VALUES(pg_temp.uid('oa'),pg_temp.uid('sa'),pg_temp.uid('vm'),2,-1,'m3','medida','prueba')$q$,'23514','cantidad negativa');
UPDATE seguridad.asignacion_equipo SET hasta='2026-01-03Z' WHERE id=pg_temp.uid('as1');
INSERT INTO seguridad.asignacion_equipo(organizacion_id,sucursal_id,id,dispositivo_id,acceso_id,persona_id,revision,desde) VALUES
(pg_temp.uid('oa'),pg_temp.uid('sa'),pg_temp.uid('as2'),pg_temp.uid('d'),pg_temp.uid('a'),pg_temp.uid('p2'),2,'2026-01-03Z');
SELECT pg_temp.afirmar((SELECT a.persona_id=pg_temp.uid('p1') FROM negocio.captura c JOIN seguridad.asignacion_equipo a ON (a.organizacion_id,a.sucursal_id,a.id)=(c.organizacion_id,c.sucursal_id,c.asignacion_equipo_id) WHERE c.id=pg_temp.uid('cap')),'Gmail reciclado mantiene persona original');
SELECT pg_temp.rechaza($q$UPDATE seguridad.asignacion_equipo SET persona_id=pg_temp.uid('p2') WHERE id=pg_temp.uid('as1')$q$,'23514','no reatribuir asignación');
INSERT INTO negocio.ficha_empresa(organizacion_id,empresa_id,revision,nombre_fiscal,calle) VALUES(pg_temp.uid('oa'),pg_temp.uid('ea'),2,'Empresa nueva','Calle nueva');
SELECT pg_temp.afirmar((SELECT f.calle='Calle original' FROM negocio.participante_manifiesto p JOIN negocio.ficha_empresa f ON (f.organizacion_id,f.id)=(p.organizacion_id,p.ficha_empresa_id) WHERE p.version_id=pg_temp.uid('vm')),'nuevo catálogo no cambia documento');
SELECT pg_temp.afirmar((SELECT sha256=sha256(contenido) FROM negocio.contenido_aceptado WHERE id=pg_temp.uid('cont')),'hash calculado sobre bytes aceptados');
COMMIT;

BEGIN;
SELECT pg_temp.rechaza($q$INSERT INTO negocio.detalle_manifiesto(organizacion_id,sucursal_id,version_id,renglon) VALUES(pg_temp.uid('oa'),pg_temp.uid('sa'),pg_temp.uid('vm'),3)$q$,'23514','no agregar renglones a versión confirmada');
SET LOCAL ROLE manifiestos_app;
SELECT pg_temp.afirmar((SELECT count(*)=0 FROM negocio.cliente),'RLS sin contexto niega lectura');
SELECT set_config('app.organizacion_id',pg_temp.uid('oa')::text,true),set_config('app.acceso_id',pg_temp.uid('a')::text,true);
SELECT pg_temp.afirmar((SELECT count(*)=1 FROM negocio.cliente),'RLS filtra otra sucursal y organización');
SELECT pg_temp.afirmar((SELECT count(*)=1 FROM negocio.organizacion),'RLS permite organización autorizada');
SELECT pg_temp.rechaza($q$INSERT INTO negocio.orden_servicio(organizacion_id,sucursal_id,cliente_id) VALUES(pg_temp.uid('oa'),pg_temp.uid('sa2'),pg_temp.uid('ca2'))$q$,'42501','RLS niega escritura otra sucursal');
SELECT pg_temp.rechaza($q$UPDATE negocio.ficha_empresa SET calle='Cambio' WHERE id=pg_temp.uid('fa')$q$,'42501','rol normal no administra catálogos');
SELECT pg_temp.afirmar(sync.resultado_previo(pg_temp.uid('oa'),pg_temp.uid('sa'),pg_temp.uid('i'),pg_temp.uid('cmd'),'solicitud') IS NULL,'primera clave disponible');
INSERT INTO sync.comando(organizacion_id,sucursal_id,instalacion_app_id,id,solicitud,resultado) VALUES(pg_temp.uid('oa'),pg_temp.uid('sa'),pg_temp.uid('i'),pg_temp.uid('cmd'),'solicitud','{"recibido":true}');
SELECT pg_temp.afirmar(sync.resultado_previo(pg_temp.uid('oa'),pg_temp.uid('sa'),pg_temp.uid('i'),pg_temp.uid('cmd'),'solicitud')='{"recibido":true}'::jsonb,'reintento devuelve resultado persistido');
SELECT pg_temp.rechaza($q$SELECT sync.resultado_previo(pg_temp.uid('oa'),pg_temp.uid('sa'),pg_temp.uid('i'),pg_temp.uid('cmd'),'diferente')$q$,'23514','misma clave rechaza bytes distintos');
SELECT pg_temp.rechaza($q$INSERT INTO sync.comando(organizacion_id,sucursal_id,instalacion_app_id,id,solicitud,resultado) VALUES(pg_temp.uid('oa'),pg_temp.uid('sa'),pg_temp.uid('i'),pg_temp.uid('cmd'),'solicitud','{}')$q$,'23505','PK deduplica comandos');
RESET ROLE;
UPDATE seguridad.acceso_autorizado SET activo=false WHERE id=pg_temp.uid('a');
SET LOCAL ROLE manifiestos_app;
SELECT pg_temp.afirmar((SELECT count(*)=0 FROM negocio.captura),'baja de acceso bloquea lectura');
RESET ROLE;
UPDATE seguridad.acceso_autorizado SET activo=true WHERE id=pg_temp.uid('a');
COMMIT;
SELECT count(*) AS comprobaciones_correctas FROM comprobacion;
\echo 'Todas las comprobaciones terminaron sin error.'

