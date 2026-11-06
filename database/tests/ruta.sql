\set ON_ERROR_STOP on
-- Rutas recurrentes y paradas (0008). Datos ficticios.
CREATE FUNCTION pg_temp.uid(t text) RETURNS uuid LANGUAGE sql IMMUTABLE AS $$ SELECT md5('ruta'||t)::uuid $$;
CREATE TEMP TABLE comprobacion_ruta(nombre text);
GRANT INSERT, SELECT ON comprobacion_ruta TO manifiestos_app;
CREATE FUNCTION pg_temp.afirmar(ok boolean,n text) RETURNS void LANGUAGE plpgsql AS $$
BEGIN IF ok IS DISTINCT FROM true THEN RAISE EXCEPTION 'FALLO: %',n; END IF; INSERT INTO comprobacion_ruta VALUES(n); END $$;
CREATE FUNCTION pg_temp.rechaza(q text,estado text,n text) RETURNS void LANGUAGE plpgsql AS $$
DECLARE obtenido text;
BEGIN
    BEGIN EXECUTE q; EXCEPTION WHEN OTHERS THEN GET STACKED DIAGNOSTICS obtenido=RETURNED_SQLSTATE; END;
    PERFORM pg_temp.afirmar(obtenido=estado,n||' ['||coalesce(obtenido,'sin error')||']');
END $$;
-- Ejecuta un bloque completo y su COMMIT diferido dentro de un subbloque (para restricciones al confirmar).
CREATE FUNCTION pg_temp.rechaza_al_confirmar(q text,estado text,n text) RETURNS void LANGUAGE plpgsql AS $$
DECLARE obtenido text;
BEGIN
    BEGIN EXECUTE q; SET CONSTRAINTS ALL IMMEDIATE; EXCEPTION WHEN OTHERS THEN GET STACKED DIAGNOSTICS obtenido=RETURNED_SQLSTATE; END;
    SET CONSTRAINTS ALL DEFERRED;
    PERFORM pg_temp.afirmar(obtenido=estado,n||' ['||coalesce(obtenido,'sin error')||']');
END $$;
CREATE FUNCTION pg_temp.contexto(acceso text) RETURNS void LANGUAGE sql AS $$
    SELECT set_config('app.organizacion_id',pg_temp.uid('o')::text,true), set_config('app.acceso_id',pg_temp.uid(acceso)::text,true)
$$;

BEGIN;
INSERT INTO negocio.organizacion VALUES (pg_temp.uid('o'),'Grupo Ficticio Rutas'),(pg_temp.uid('o2'),'Otro Grupo Ficticio');
INSERT INTO negocio.empresa VALUES (pg_temp.uid('o'),pg_temp.uid('e'),'EMP-R'),(pg_temp.uid('o'),pg_temp.uid('ec'),'CLI-R');
INSERT INTO negocio.sucursal(organizacion_id,id,empresa_id,codigo,nombre) VALUES
 (pg_temp.uid('o'),pg_temp.uid('s'),pg_temp.uid('e'),'R1','Sucursal ficticia R1'),
 (pg_temp.uid('o'),pg_temp.uid('s2'),pg_temp.uid('e'),'R2','Sucursal ficticia R2');
INSERT INTO negocio.empresa_sucursal VALUES (pg_temp.uid('o'),pg_temp.uid('s'),pg_temp.uid('ec')),(pg_temp.uid('o'),pg_temp.uid('s2'),pg_temp.uid('ec'));
INSERT INTO negocio.cliente VALUES (pg_temp.uid('o'),pg_temp.uid('s'),pg_temp.uid('c'),pg_temp.uid('ec'),'C1'),
 (pg_temp.uid('o'),pg_temp.uid('s2'),pg_temp.uid('c2'),pg_temp.uid('ec'),'C2');
INSERT INTO negocio.vehiculo VALUES (pg_temp.uid('o'),pg_temp.uid('s'),pg_temp.uid('v'),'U-01'),(pg_temp.uid('o'),pg_temp.uid('s2'),pg_temp.uid('v2'),'U-02');
INSERT INTO negocio.sitio VALUES (pg_temp.uid('o'),pg_temp.uid('s'),pg_temp.uid('obra'),'Obra'),(pg_temp.uid('o'),pg_temp.uid('s'),pg_temp.uid('ptar'),'PTAR');
INSERT INTO negocio.punto_servicio(organizacion_id,sucursal_id,sitio_id) VALUES (pg_temp.uid('o'),pg_temp.uid('s'),pg_temp.uid('obra'));
INSERT INTO seguridad.persona VALUES (pg_temp.uid('o'),pg_temp.uid('p'),'Operador ficticio'),(pg_temp.uid('o2'),pg_temp.uid('px'),'Persona de otro grupo');
INSERT INTO seguridad.acceso_autorizado(organizacion_id,id,correo_permitido,activo,rol_codigo) VALUES
 (pg_temp.uid('o'),pg_temp.uid('esc'),'escribe@example.invalid',true,'operaciones'),
 (pg_temp.uid('o'),pg_temp.uid('lee'),'lee@example.invalid',true,'operaciones');
INSERT INTO seguridad.permiso_sucursal VALUES
 (pg_temp.uid('o'),pg_temp.uid('s'),pg_temp.uid('esc'),'datos.leer'),
 (pg_temp.uid('o'),pg_temp.uid('s'),pg_temp.uid('esc'),'datos.escribir'),
 (pg_temp.uid('o'),pg_temp.uid('s'),pg_temp.uid('lee'),'datos.leer');
COMMIT;

-- Escritura con contexto de operaciones.
BEGIN;
SET LOCAL ROLE manifiestos_app;
SELECT pg_temp.contexto('esc');
SELECT pg_temp.afirmar((SELECT count(*)=0 FROM seguridad.persona),'persona sin unidad sigue oculta');
INSERT INTO negocio.asignacion_unidad(organizacion_id,sucursal_id,vehiculo_id,revision,persona_id) VALUES (pg_temp.uid('o'),pg_temp.uid('s'),pg_temp.uid('v'),1,pg_temp.uid('p'));
SELECT pg_temp.afirmar((SELECT nombre_completo='Operador ficticio' FROM seguridad.persona),'operador de una unidad visible se vuelve visible');
INSERT INTO negocio.ruta(organizacion_id,sucursal_id,id) VALUES (pg_temp.uid('o'),pg_temp.uid('s'),pg_temp.uid('r'));
INSERT INTO negocio.ruta_version(organizacion_id,sucursal_id,ruta_id,revision,nombre,dias,vehiculo_id) VALUES
 (pg_temp.uid('o'),pg_temp.uid('s'),pg_temp.uid('r'),1,'Ruta 01 – Norte',1|4|16,pg_temp.uid('v'));
INSERT INTO negocio.parada(organizacion_id,sucursal_id,id,ruta_id,posicion) VALUES
 (pg_temp.uid('o'),pg_temp.uid('s'),pg_temp.uid('pa1'),pg_temp.uid('r'),1),(pg_temp.uid('o'),pg_temp.uid('s'),pg_temp.uid('pa2'),pg_temp.uid('r'),2);
INSERT INTO negocio.parada_version(organizacion_id,sucursal_id,id,parada_id,revision,sitio_id) VALUES
 (pg_temp.uid('o'),pg_temp.uid('s'),pg_temp.uid('pv1'),pg_temp.uid('pa1'),1,pg_temp.uid('obra')),
 (pg_temp.uid('o'),pg_temp.uid('s'),pg_temp.uid('pv2'),pg_temp.uid('pa2'),1,pg_temp.uid('obra'));
INSERT INTO negocio.parada_cliente VALUES
 (pg_temp.uid('o'),pg_temp.uid('s'),pg_temp.uid('pv1'),pg_temp.uid('c'),15),
 (pg_temp.uid('o'),pg_temp.uid('s'),pg_temp.uid('pv2'),pg_temp.uid('c'),10);
SELECT pg_temp.afirmar((SELECT creada_por_acceso_id=pg_temp.uid('esc') FROM negocio.ruta),'autor tomado del contexto del servidor');
-- Intercambio de posiciones en la misma transacción (unicidad diferida).
UPDATE negocio.parada SET posicion = CASE id WHEN pg_temp.uid('pa1') THEN 2 ELSE 1 END WHERE ruta_id = pg_temp.uid('r');
COMMIT;

BEGIN;
SET LOCAL ROLE manifiestos_app;
SELECT pg_temp.contexto('esc');
SELECT pg_temp.afirmar((SELECT posicion FROM negocio.parada WHERE id=pg_temp.uid('pa2'))=1,'reordenar intercambia posiciones');
SELECT pg_temp.rechaza($q$INSERT INTO negocio.parada_version(organizacion_id,sucursal_id,parada_id,revision,sitio_id) VALUES (pg_temp.uid('o'),pg_temp.uid('s'),pg_temp.uid('pa1'),2,pg_temp.uid('ptar'))$q$,'23503','una PTAR no puede ser parada');
-- Revisión nueva de una parada (corrección de baños) con sus clientes en la misma transacción.
INSERT INTO negocio.parada_version(organizacion_id,sucursal_id,id,parada_id,revision,sitio_id) VALUES (pg_temp.uid('o'),pg_temp.uid('s'),pg_temp.uid('pv2b'),pg_temp.uid('pa2'),2,pg_temp.uid('obra'));
INSERT INTO negocio.parada_cliente VALUES (pg_temp.uid('o'),pg_temp.uid('s'),pg_temp.uid('pv2b'),pg_temp.uid('c'),20);
SELECT pg_temp.rechaza($q$INSERT INTO negocio.parada_cliente VALUES (pg_temp.uid('o'),pg_temp.uid('s'),pg_temp.uid('pv2b'),pg_temp.uid('c'),5)$q$,'23505','cliente repetido en la misma revisión');
SELECT pg_temp.rechaza($q$INSERT INTO negocio.parada_cliente VALUES (pg_temp.uid('o'),pg_temp.uid('s'),pg_temp.uid('pv1'),gen_random_uuid(),5)$q$,'23514','no se agregan clientes a una revisión ya confirmada');
SELECT pg_temp.rechaza($q$INSERT INTO negocio.ruta_version(organizacion_id,sucursal_id,ruta_id,revision,nombre,dias,vehiculo_id) VALUES (pg_temp.uid('o'),pg_temp.uid('s'),pg_temp.uid('r'),2,'X',0,pg_temp.uid('v'))$q$,'23514','sin días no es ruta');
SELECT pg_temp.rechaza($q$INSERT INTO negocio.ruta_version(organizacion_id,sucursal_id,ruta_id,revision,nombre,dias,vehiculo_id) VALUES (pg_temp.uid('o'),pg_temp.uid('s'),pg_temp.uid('r'),2,'X',128,pg_temp.uid('v'))$q$,'23514','días fuera de la semana');
SELECT pg_temp.rechaza($q$INSERT INTO negocio.ruta_version(organizacion_id,sucursal_id,ruta_id,revision,nombre,dias,vehiculo_id) VALUES (pg_temp.uid('o'),pg_temp.uid('s'),pg_temp.uid('r'),1,'X',1,pg_temp.uid('v'))$q$,'23505','revisión de ruta duplicada');
SELECT pg_temp.rechaza($q$INSERT INTO negocio.ruta_version(organizacion_id,sucursal_id,ruta_id,revision,nombre,dias,vehiculo_id) VALUES (pg_temp.uid('o'),pg_temp.uid('s'),pg_temp.uid('r'),2,'X',1,pg_temp.uid('v2'))$q$,'23503','unidad de otra sucursal');
SELECT pg_temp.rechaza($q$INSERT INTO negocio.asignacion_unidad(organizacion_id,sucursal_id,vehiculo_id,revision,persona_id) VALUES (pg_temp.uid('o'),pg_temp.uid('s'),pg_temp.uid('v'),2,pg_temp.uid('px'))$q$,'23503','operador de otra organización');
SELECT pg_temp.rechaza($q$INSERT INTO negocio.parada_cliente VALUES (pg_temp.uid('o'),pg_temp.uid('s'),pg_temp.uid('pv2b'),pg_temp.uid('c2'),5)$q$,'23503','cliente de otra sucursal');
SELECT pg_temp.rechaza($q$UPDATE negocio.parada SET ruta_id=gen_random_uuid() WHERE id=pg_temp.uid('pa1')$q$,'42501','la aplicación solo puede cambiar la posición');
SELECT pg_temp.rechaza($q$UPDATE negocio.ruta_version SET nombre='Cambio'$q$,'42501','la aplicación no reescribe revisiones');
SELECT pg_temp.rechaza($q$INSERT INTO negocio.ruta(organizacion_id,sucursal_id) VALUES (pg_temp.uid('o'),pg_temp.uid('s2'))$q$,'42501','RLS niega crear en otra sucursal');
COMMIT;

-- Restricciones comprobadas al confirmar.
BEGIN;
SET LOCAL ROLE manifiestos_app;
SELECT pg_temp.contexto('esc');
SELECT pg_temp.rechaza_al_confirmar($q$INSERT INTO negocio.ruta(organizacion_id,sucursal_id) VALUES (pg_temp.uid('o'),pg_temp.uid('s'))$q$,'23514','ruta sin revisión');
SELECT pg_temp.rechaza_al_confirmar($q$INSERT INTO negocio.parada(organizacion_id,sucursal_id,ruta_id,posicion) VALUES (pg_temp.uid('o'),pg_temp.uid('s'),pg_temp.uid('r'),3)$q$,'23514','parada sin revisión');
SELECT pg_temp.rechaza_al_confirmar($q$INSERT INTO negocio.parada_version(organizacion_id,sucursal_id,parada_id,revision,sitio_id) VALUES (pg_temp.uid('o'),pg_temp.uid('s'),pg_temp.uid('pa1'),2,pg_temp.uid('obra'))$q$,'23514','revisión de parada sin clientes');
SELECT pg_temp.rechaza_al_confirmar($q$UPDATE negocio.parada SET posicion=1 WHERE id=pg_temp.uid('pa1')$q$,'23505','dos paradas en la misma posición');
COMMIT;

-- Solo lectura.
BEGIN;
SET LOCAL ROLE manifiestos_app;
SELECT pg_temp.contexto('lee');
SELECT pg_temp.afirmar((SELECT count(*)=1 FROM negocio.ruta_version),'solo lectura ve la ruta');
SELECT pg_temp.afirmar((SELECT count(*)=3 FROM negocio.parada_cliente),'solo lectura ve las paradas (con la revisión de corrección)');
SELECT pg_temp.rechaza($q$INSERT INTO negocio.asignacion_unidad(organizacion_id,sucursal_id,vehiculo_id,revision,persona_id) VALUES (pg_temp.uid('o'),pg_temp.uid('s'),pg_temp.uid('v'),2,pg_temp.uid('p'))$q$,'42501','sin datos.escribir no cambia operador');
SELECT pg_temp.rechaza($q$UPDATE negocio.parada SET posicion=9 WHERE id=pg_temp.uid('pa1')$q$,'42501','sin datos.escribir no reordena');
SELECT set_config('app.acceso_id','',true);
SELECT pg_temp.afirmar((SELECT count(*)=0 FROM negocio.parada),'sin contexto no ve paradas');
RESET ROLE;
SELECT pg_temp.rechaza($q$DELETE FROM negocio.parada WHERE id=pg_temp.uid('pa1')$q$,'23514','parada no se borra ni por el dueño');
SELECT pg_temp.rechaza($q$UPDATE negocio.parada_cliente SET banos_planeados=99$q$,'23514','clientes de parada inmutables');
COMMIT;
SELECT count(*) AS comprobaciones_ruta FROM comprobacion_ruta;
\echo 'Comprobaciones de rutas y paradas terminadas sin error.'
