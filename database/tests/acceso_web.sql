\set ON_ERROR_STOP on
-- Acceso web (0006). Datos ficticios; dominio example.invalid. Sin tokens reales:
-- se prueba la parte de base de datos, que recibe una identidad ya validada.
CREATE FUNCTION pg_temp.uid(t text) RETURNS uuid LANGUAGE sql IMMUTABLE AS $$ SELECT md5(t)::uuid $$;
CREATE TEMP TABLE comprobacion_acceso(nombre text);
GRANT INSERT, SELECT ON comprobacion_acceso TO manifiestos_app;
CREATE FUNCTION pg_temp.afirmar(ok boolean,n text) RETURNS void LANGUAGE plpgsql AS $$
BEGIN IF ok IS DISTINCT FROM true THEN RAISE EXCEPTION 'FALLO: %',n; END IF; INSERT INTO comprobacion_acceso VALUES(n); END $$;
CREATE FUNCTION pg_temp.rechaza(q text,estado text,n text) RETURNS void LANGUAGE plpgsql AS $$
DECLARE obtenido text;
BEGIN
    BEGIN EXECUTE q; EXCEPTION WHEN OTHERS THEN GET STACKED DIAGNOSTICS obtenido=RETURNED_SQLSTATE; END;
    PERFORM pg_temp.afirmar(obtenido=estado,n||' ['||coalesce(obtenido,'sin error')||']');
END $$;
CREATE FUNCTION pg_temp.login(org text,sujeto text,correo text) RETURNS TABLE(r text,a uuid) LANGUAGE sql AS $$
    SELECT resultado_login, acceso FROM seguridad.iniciar_sesion_web(pg_temp.uid(org),'https://accounts.google.com',sujeto,correo)
$$;

-- Preparación (confirmada para que la usen también las pruebas concurrentes).
BEGIN;
INSERT INTO negocio.organizacion VALUES (pg_temp.uid('ow'),'Grupo Ficticio Web'),(pg_temp.uid('ow2'),'Otro Grupo Ficticio');
INSERT INTO negocio.empresa VALUES (pg_temp.uid('ow'),pg_temp.uid('ew'),'EMP-W');
INSERT INTO negocio.sucursal(organizacion_id,id,empresa_id,codigo,nombre) VALUES (pg_temp.uid('ow'),pg_temp.uid('sw'),pg_temp.uid('ew'),'W','Sucursal ficticia W');
INSERT INTO seguridad.acceso_autorizado(organizacion_id,id,correo_permitido,activo,rol_codigo) VALUES
 (pg_temp.uid('ow'),pg_temp.uid('a_ok'),'ops@example.invalid',true,'operaciones'),
 (pg_temp.uid('ow'),pg_temp.uid('a_inact'),'inactivo@example.invalid',false,'operaciones'),
 (pg_temp.uid('ow'),pg_temp.uid('a_tel'),'telefono@example.invalid',true,'telefono_operador'),
 (pg_temp.uid('ow'),pg_temp.uid('a_sinalc'),'sinalcance@example.invalid',true,'operaciones'),
 (pg_temp.uid('ow'),pg_temp.uid('a_sinrol'),'sinrol@example.invalid',true,NULL),
 (pg_temp.uid('ow'),pg_temp.uid('a_baja'),'baja@example.invalid',true,'operaciones'),
 (pg_temp.uid('ow'),pg_temp.uid('a_conc'),'concurrente@example.invalid',true,'operaciones'),
 (pg_temp.uid('ow'),pg_temp.uid('a_carrera'),'carrera@example.invalid',true,'operaciones');
INSERT INTO seguridad.permiso_sucursal
SELECT pg_temp.uid('ow'),pg_temp.uid('sw'),pg_temp.uid(a),'datos.leer'
FROM unnest(ARRAY['a_ok','a_inact','a_tel','a_sinrol','a_baja','a_conc','a_carrera']) a;
COMMIT;

SET ROLE manifiestos_app;
SELECT pg_temp.afirmar((SELECT r='aceptado' AND a=pg_temp.uid('a_ok') FROM pg_temp.login('ow','s-ok',' OPS@Example.invalid ')),'primer vínculo por correo verificado');
SELECT pg_temp.afirmar((SELECT r='aceptado' AND a=pg_temp.uid('a_ok') FROM pg_temp.login('ow','s-ok','ops@example.invalid')),'reconoce identidad ya vinculada');
SELECT pg_temp.afirmar((SELECT r='no_autorizado' AND a IS NULL FROM pg_temp.login('ow','s-nadie','nadie@example.invalid')),'cuenta fuera de la lista');
SELECT pg_temp.afirmar((SELECT r='inactivo' AND a IS NULL FROM pg_temp.login('ow','s-inact','inactivo@example.invalid')),'entrada inactiva');
SELECT pg_temp.afirmar((SELECT r='sin_acceso_web' AND a IS NULL FROM pg_temp.login('ow','s-tel','telefono@example.invalid')),'teléfono de operador no entra a la web');
SELECT pg_temp.afirmar((SELECT r='sin_acceso_web' AND a IS NULL FROM pg_temp.login('ow','s-sinrol','sinrol@example.invalid')),'entrada sin rol');
SELECT pg_temp.afirmar((SELECT r='sin_alcance' AND a IS NULL FROM pg_temp.login('ow','s-sinalc','sinalcance@example.invalid')),'entrada sin sucursal');
SELECT pg_temp.afirmar((SELECT r='no_autorizado' AND a IS NULL FROM pg_temp.login('ow','s-otro','ops@example.invalid')),'correo ya vinculado a otro sujeto');
SELECT pg_temp.afirmar((SELECT r='no_autorizado' FROM pg_temp.login('ow2','s-ok2','ops@example.invalid')),'lista de otra organización no aplica');
SELECT pg_temp.afirmar((SELECT count(*)=1 AND bool_and(rol_codigo='operaciones' AND correo='ops@example.invalid') FROM seguridad.sesion_web(pg_temp.uid('ow'),pg_temp.uid('a_ok'))),'sesión vigente devuelve correo y rol');
SELECT pg_temp.afirmar((SELECT count(*)=0 FROM seguridad.sesion_web(pg_temp.uid('ow'),pg_temp.uid('a_tel'))),'sesión de teléfono no vale para web');
SELECT pg_temp.afirmar((SELECT r='aceptado' FROM pg_temp.login('ow','s-baja','baja@example.invalid')),'acceso antes de la baja');
SELECT pg_temp.rechaza($q$SELECT count(*) FROM seguridad.inicio_sesion$q$,'42501','aplicación no lee el registro de inicios');
SELECT pg_temp.rechaza($q$SELECT seguridad.motivo_rechazo_web(pg_temp.uid('ow'),pg_temp.uid('a_ok'))$q$,'42501','función interna no expuesta');
SELECT pg_temp.rechaza($q$UPDATE seguridad.acceso_autorizado SET activo=true WHERE id=pg_temp.uid('a_inact')$q$,'42501','aplicación no se autohabilita');
SELECT pg_temp.rechaza($q$SELECT * FROM pg_temp.login('ow','','ops@example.invalid')$q$,'22023','sujeto vacío');
RESET ROLE;

-- Baja administrativa con sesión abierta: la siguiente revalidación la rechaza.
UPDATE seguridad.acceso_autorizado SET activo=false WHERE id=pg_temp.uid('a_baja');
SET ROLE manifiestos_app;
SELECT pg_temp.afirmar((SELECT count(*)=0 FROM seguridad.sesion_web(pg_temp.uid('ow'),pg_temp.uid('a_baja'))),'baja invalida la sesión en la siguiente petición');
SELECT pg_temp.afirmar((SELECT r='inactivo' FROM pg_temp.login('ow','s-baja','baja@example.invalid')),'baja impide volver a entrar');
RESET ROLE;

SELECT pg_temp.afirmar((SELECT count(*)=1 FROM seguridad.identidad_externa WHERE sujeto='s-ok'),'sin identidades duplicadas');
SELECT pg_temp.afirmar((SELECT identidad_id IS NOT NULL FROM seguridad.acceso_autorizado WHERE id=pg_temp.uid('a_ok')),'entrada quedó vinculada');
SELECT pg_temp.afirmar((SELECT i.sujeto='s-ok' FROM seguridad.acceso_autorizado x JOIN seguridad.identidad_externa i ON (i.organizacion_id,i.id)=(x.organizacion_id,x.identidad_id) WHERE x.id=pg_temp.uid('a_ok')),'otro sujeto no reatribuyó la entrada');
SELECT pg_temp.afirmar((SELECT count(*)=0 FROM seguridad.identidad_externa WHERE sujeto IN ('s-nadie','s-inact','s-tel','s-sinrol','s-sinalc','s-otro','s-ok2')),'intentos rechazados no crean identidad');
SELECT pg_temp.afirmar((SELECT identidad_id IS NULL FROM seguridad.acceso_autorizado WHERE id=pg_temp.uid('a_tel')),'teléfono rechazado en web no se vincula');
SELECT pg_temp.afirmar((SELECT count(*)=2 FROM seguridad.inicio_sesion WHERE acceso_id=pg_temp.uid('a_ok') AND resultado='aceptado'),'registro de inicios aceptados');
SELECT pg_temp.afirmar((SELECT count(*)=1 FROM seguridad.inicio_sesion WHERE organizacion_id=pg_temp.uid('ow') AND resultado='sin_acceso_web' AND acceso_id=pg_temp.uid('a_tel')),'registro del rechazo del teléfono');
SELECT pg_temp.rechaza($q$UPDATE seguridad.inicio_sesion SET resultado='aceptado' WHERE resultado='inactivo'$q$,'23514','registro de inicios inmutable');
SELECT pg_temp.rechaza($q$UPDATE catalogo.rol_acceso SET acceso_web=true WHERE codigo='telefono_operador'$q$,'23514','catálogo de roles inmutable');
SELECT pg_temp.afirmar((SELECT count(*)=0 FROM information_schema.columns WHERE table_schema='seguridad' AND table_name='inicio_sesion' AND column_name ~ 'token'),'registro sin columnas de token');

SELECT count(*) AS comprobaciones_acceso_web FROM comprobacion_acceso;
\echo 'Acceso web: todas las comprobaciones terminaron sin error.'
