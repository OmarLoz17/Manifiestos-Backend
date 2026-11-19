\set ON_ERROR_STOP on
-- Base de sanitarios (0027), paso B1. Datos ficticios.
-- Cubre: capacidad de 1 m3 y validación clasificación/capacidad, parada sin clientes solo en sanitarios (la protección
-- de fosas se conserva), un sitio por ruta, baja de paradas, carga derivada de la unidad y firma precargada provisional.
CREATE FUNCTION pg_temp.uid(t text) RETURNS uuid LANGUAGE sql IMMUTABLE AS $$ SELECT md5('sanitarios-base'||t)::uuid $$;
CREATE TEMP TABLE comprobacion_sanitarios(nombre text);
GRANT INSERT, SELECT ON comprobacion_sanitarios TO manifiestos_app;
CREATE FUNCTION pg_temp.afirmar(ok boolean,n text) RETURNS void LANGUAGE plpgsql AS $$
BEGIN IF ok IS DISTINCT FROM true THEN RAISE EXCEPTION 'FALLO: %',n; END IF; INSERT INTO comprobacion_sanitarios VALUES(n); END $$;
CREATE FUNCTION pg_temp.rechaza(q text,estado text,n text) RETURNS void LANGUAGE plpgsql AS $$
DECLARE obtenido text;
BEGIN
    BEGIN EXECUTE q; EXCEPTION WHEN OTHERS THEN GET STACKED DIAGNOSTICS obtenido=RETURNED_SQLSTATE; END;
    PERFORM pg_temp.afirmar(obtenido=estado,n||' ['||coalesce(obtenido,'sin error')||']');
END $$;
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
INSERT INTO negocio.organizacion VALUES (pg_temp.uid('o'),'Grupo Ficticio Sanitarios'),(pg_temp.uid('o2'),'Otro Grupo Ficticio');
INSERT INTO negocio.empresa VALUES (pg_temp.uid('o'),pg_temp.uid('e'),'EMP-S'),(pg_temp.uid('o'),pg_temp.uid('ec'),'CLI-S');
INSERT INTO negocio.sucursal(organizacion_id,id,empresa_id,codigo,nombre) VALUES
 (pg_temp.uid('o'),pg_temp.uid('s'),pg_temp.uid('e'),'S1','Sucursal ficticia S1'),
 (pg_temp.uid('o'),pg_temp.uid('s2'),pg_temp.uid('e'),'S2','Sucursal ficticia S2');
INSERT INTO negocio.empresa_sucursal VALUES (pg_temp.uid('o'),pg_temp.uid('s'),pg_temp.uid('ec')),(pg_temp.uid('o'),pg_temp.uid('s2'),pg_temp.uid('ec'));
INSERT INTO negocio.cliente VALUES (pg_temp.uid('o'),pg_temp.uid('s'),pg_temp.uid('c'),pg_temp.uid('ec'),'C1');
INSERT INTO negocio.vehiculo VALUES
 (pg_temp.uid('o'),pg_temp.uid('s'),pg_temp.uid('vsan'),'SAN-01'),
 (pg_temp.uid('o'),pg_temp.uid('s'),pg_temp.uid('vfos'),'FOS-01'),
 (pg_temp.uid('o'),pg_temp.uid('s'),pg_temp.uid('vnc'),'NC-01');
INSERT INTO negocio.sitio VALUES (pg_temp.uid('o'),pg_temp.uid('s'),pg_temp.uid('a'),'Sitio A'),(pg_temp.uid('o'),pg_temp.uid('s'),pg_temp.uid('b'),'Sitio B');
INSERT INTO negocio.punto_servicio(organizacion_id,sucursal_id,sitio_id) VALUES
 (pg_temp.uid('o'),pg_temp.uid('s'),pg_temp.uid('a')),(pg_temp.uid('o'),pg_temp.uid('s'),pg_temp.uid('b'));
INSERT INTO seguridad.acceso_autorizado(organizacion_id,id,correo_permitido,activo,rol_codigo) VALUES
 (pg_temp.uid('o'),pg_temp.uid('esc'),'escribe-san@example.invalid',true,'operaciones'),
 (pg_temp.uid('o'),pg_temp.uid('lee'),'lee-san@example.invalid',true,'operaciones'),
 (pg_temp.uid('o'),pg_temp.uid('otra'),'otra-san@example.invalid',true,'operaciones');
INSERT INTO seguridad.permiso_sucursal VALUES
 (pg_temp.uid('o'),pg_temp.uid('s'),pg_temp.uid('esc'),'datos.leer'),
 (pg_temp.uid('o'),pg_temp.uid('s'),pg_temp.uid('esc'),'datos.escribir'),
 (pg_temp.uid('o'),pg_temp.uid('s'),pg_temp.uid('lee'),'datos.leer'),
 (pg_temp.uid('o'),pg_temp.uid('s2'),pg_temp.uid('otra'),'datos.leer'),
 (pg_temp.uid('o'),pg_temp.uid('s2'),pg_temp.uid('otra'),'datos.escribir');

-- Capacidad de 1 m3 y validación clasificación/capacidad de las fichas nuevas.
SELECT pg_temp.afirmar((SELECT valor_m3 = 1.000 FROM catalogo.capacidad_vehiculo WHERE id = 4),'catálogo: capacidad 4 = 1 m3');
INSERT INTO catalogo.capacidad_vehiculo (id, valor_m3) VALUES (4, 1.000) ON CONFLICT (id) DO NOTHING;
SELECT pg_temp.afirmar((SELECT count(*) = 1 FROM catalogo.capacidad_vehiculo WHERE id = 4),'el alta de la capacidad 4 es repetible sin duplicar');
SELECT pg_temp.afirmar(negocio.litros_por_bano() = 16,'constante de 16 litros por baño');
INSERT INTO negocio.ficha_vehiculo(organizacion_id,sucursal_id,id,vehiculo_id,revision,capacidad_id,clasificacion,numero_economico) VALUES
 (pg_temp.uid('o'),pg_temp.uid('s'),pg_temp.uid('fsan'),pg_temp.uid('vsan'),1,4,'sanitarios','SAN-01'),
 (pg_temp.uid('o'),pg_temp.uid('s'),pg_temp.uid('ffos'),pg_temp.uid('vfos'),1,3,'fosas','FOS-01'),
 (pg_temp.uid('o'),pg_temp.uid('s'),pg_temp.uid('fnc'),pg_temp.uid('vnc'),1,2,NULL,'NC-01');
SELECT pg_temp.rechaza($q$INSERT INTO negocio.ficha_vehiculo(organizacion_id,sucursal_id,vehiculo_id,revision,capacidad_id,clasificacion) VALUES (pg_temp.uid('o'),pg_temp.uid('s'),pg_temp.uid('vsan'),2,1,'sanitarios')$q$,'23514','sanitarios no admite capacidades de fosas');
SELECT pg_temp.rechaza($q$INSERT INTO negocio.ficha_vehiculo(organizacion_id,sucursal_id,vehiculo_id,revision,capacidad_id,clasificacion) VALUES (pg_temp.uid('o'),pg_temp.uid('s'),pg_temp.uid('vfos'),2,4,'fosas')$q$,'23514','fosas no admite 1 m3');
SELECT pg_temp.rechaza($q$INSERT INTO negocio.ficha_vehiculo(organizacion_id,sucursal_id,vehiculo_id,revision,capacidad_id,clasificacion) VALUES (pg_temp.uid('o'),pg_temp.uid('s'),pg_temp.uid('vnc'),2,4,NULL)$q$,'23514','1 m3 sin clasificar no se admite');
SELECT pg_temp.rechaza($q$INSERT INTO negocio.ficha_vehiculo(organizacion_id,sucursal_id,vehiculo_id,revision,capacidad_id,clasificacion) VALUES (pg_temp.uid('o'),pg_temp.uid('s'),pg_temp.uid('vfos'),2,1,'otra')$q$,'23514','clasificación fuera del catálogo');

-- Rutas: sanitarios, fosas y sin clasificar.
INSERT INTO negocio.ruta(organizacion_id,sucursal_id,id) VALUES
 (pg_temp.uid('o'),pg_temp.uid('s'),pg_temp.uid('rsan')),(pg_temp.uid('o'),pg_temp.uid('s'),pg_temp.uid('rsan2')),
 (pg_temp.uid('o'),pg_temp.uid('s'),pg_temp.uid('rfos')),(pg_temp.uid('o'),pg_temp.uid('s'),pg_temp.uid('rnc'));
INSERT INTO negocio.ruta_version(organizacion_id,sucursal_id,ruta_id,revision,nombre,dias,vehiculo_id) VALUES
 (pg_temp.uid('o'),pg_temp.uid('s'),pg_temp.uid('rsan'),1,'SAN-01-LMV',21,pg_temp.uid('vsan')),
 (pg_temp.uid('o'),pg_temp.uid('s'),pg_temp.uid('rsan2'),1,'SAN-01-MJS',42,pg_temp.uid('vsan')),
 (pg_temp.uid('o'),pg_temp.uid('s'),pg_temp.uid('rfos'),1,'FOS-01-LMV',21,pg_temp.uid('vfos')),
 (pg_temp.uid('o'),pg_temp.uid('s'),pg_temp.uid('rnc'),1,'NC-01-LMV',21,pg_temp.uid('vnc'));
SELECT pg_temp.afirmar(negocio.servicio_ruta(pg_temp.uid('o'),pg_temp.uid('s'),pg_temp.uid('rsan')) = 'sanitarios'
  AND negocio.servicio_ruta(pg_temp.uid('o'),pg_temp.uid('s'),pg_temp.uid('rfos')) = 'fosas'
  AND negocio.servicio_ruta(pg_temp.uid('o'),pg_temp.uid('s'),pg_temp.uid('rnc')) IS NULL,'el servicio de la ruta sale de la clasificación de su unidad');
COMMIT;

-- Parada sin clientes: permitida en sanitarios y (0029) también en fosas y rutas sin clasificar.
BEGIN;
SET LOCAL ROLE manifiestos_app;
SELECT pg_temp.contexto('esc');
INSERT INTO negocio.parada(organizacion_id,sucursal_id,id,ruta_id,posicion) VALUES (pg_temp.uid('o'),pg_temp.uid('s'),pg_temp.uid('psan1'),pg_temp.uid('rsan'),1);
INSERT INTO negocio.parada_version(organizacion_id,sucursal_id,id,parada_id,revision,sitio_id) VALUES (pg_temp.uid('o'),pg_temp.uid('s'),pg_temp.uid('pvsan1'),pg_temp.uid('psan1'),1,pg_temp.uid('a'));
COMMIT;
BEGIN;
SET LOCAL ROLE manifiestos_app;
SELECT pg_temp.contexto('esc');
SELECT pg_temp.afirmar((SELECT count(*)=1 FROM negocio.parada WHERE id=pg_temp.uid('psan1')),'parada de sanitarios sin clientes confirmada');
SELECT pg_temp.afirmar((SELECT count(*)=0 FROM negocio.parada_cliente WHERE version_id=pg_temp.uid('pvsan1')),'la parada de sanitarios no tiene clientes todavía');
-- Los clientes se agregan creando una revisión nueva con el conjunto completo.
INSERT INTO negocio.parada_version(organizacion_id,sucursal_id,id,parada_id,revision,sitio_id) VALUES (pg_temp.uid('o'),pg_temp.uid('s'),pg_temp.uid('pvsan1b'),pg_temp.uid('psan1'),2,pg_temp.uid('a'));
INSERT INTO negocio.parada_cliente(organizacion_id,sucursal_id,version_id,cliente_id,banos_planeados) VALUES (pg_temp.uid('o'),pg_temp.uid('s'),pg_temp.uid('pvsan1b'),pg_temp.uid('c'),12);
SELECT pg_temp.rechaza_al_confirmar($q$INSERT INTO negocio.parada(organizacion_id,sucursal_id,ruta_id,posicion) VALUES (pg_temp.uid('o'),pg_temp.uid('s'),pg_temp.uid('rsan'),9)$q$,'23514','una parada sigue necesitando su primera revisión');
SELECT pg_temp.rechaza($q$INSERT INTO negocio.parada_cliente VALUES (pg_temp.uid('o'),pg_temp.uid('s'),pg_temp.uid('pvsan1'),pg_temp.uid('c'),5)$q$,'23514','los clientes solo se agregan en la transacción de la revisión');
COMMIT;

BEGIN;
SET LOCAL ROLE manifiestos_app;
SELECT pg_temp.contexto('esc');
INSERT INTO negocio.parada(organizacion_id,sucursal_id,id,ruta_id,posicion) VALUES (pg_temp.uid('o'),pg_temp.uid('s'),pg_temp.uid('pfos1'),pg_temp.uid('rfos'),1);
INSERT INTO negocio.parada_version(organizacion_id,sucursal_id,id,parada_id,revision,sitio_id) VALUES (pg_temp.uid('o'),pg_temp.uid('s'),pg_temp.uid('pvfos1'),pg_temp.uid('pfos1'),1,pg_temp.uid('a'));
INSERT INTO negocio.parada(organizacion_id,sucursal_id,id,ruta_id,posicion) VALUES (pg_temp.uid('o'),pg_temp.uid('s'),pg_temp.uid('pnc1'),pg_temp.uid('rnc'),1);
INSERT INTO negocio.parada_version(organizacion_id,sucursal_id,id,parada_id,revision,sitio_id) VALUES (pg_temp.uid('o'),pg_temp.uid('s'),pg_temp.uid('pvnc1'),pg_temp.uid('pnc1'),1,pg_temp.uid('a'));
COMMIT;
BEGIN;
SET LOCAL ROLE manifiestos_app;
SELECT pg_temp.contexto('esc');
SELECT pg_temp.afirmar((SELECT count(*)=2 FROM negocio.parada WHERE id IN (pg_temp.uid('pfos1'),pg_temp.uid('pnc1'))),'0029: una parada sin clientes también es válida en fosas y en rutas sin clasificar');
COMMIT;

-- Un sitio, a lo más una parada vigente por ruta; la baja la libera.
BEGIN;
SET LOCAL ROLE manifiestos_app;
SELECT pg_temp.contexto('esc');
SELECT pg_temp.rechaza_al_confirmar($q$
    INSERT INTO negocio.parada(organizacion_id,sucursal_id,id,ruta_id,posicion) VALUES (pg_temp.uid('o'),pg_temp.uid('s'),pg_temp.uid('psan1dup'),pg_temp.uid('rsan'),2);
    INSERT INTO negocio.parada_version(organizacion_id,sucursal_id,parada_id,revision,sitio_id) VALUES (pg_temp.uid('o'),pg_temp.uid('s'),pg_temp.uid('psan1dup'),1,pg_temp.uid('a'))$q$,'23505','el mismo sitio dos veces en una ruta se rechaza');
-- El mismo sitio en otra ruta sí es posible a nivel de datos (la regla de reasignación vive en el servidor).
INSERT INTO negocio.parada(organizacion_id,sucursal_id,id,ruta_id,posicion) VALUES (pg_temp.uid('o'),pg_temp.uid('s'),pg_temp.uid('psan2a'),pg_temp.uid('rsan2'),1);
INSERT INTO negocio.parada_version(organizacion_id,sucursal_id,parada_id,revision,sitio_id) VALUES (pg_temp.uid('o'),pg_temp.uid('s'),pg_temp.uid('psan2a'),1,pg_temp.uid('a'));
COMMIT;
BEGIN;
SET LOCAL ROLE manifiestos_app;
SELECT pg_temp.contexto('esc');
SELECT pg_temp.afirmar((SELECT count(*)=1 FROM negocio.parada WHERE ruta_id=pg_temp.uid('rsan2')),'el sitio puede estar en otra ruta');
-- Dar de baja la parada y volver a agregar el sitio en la misma ruta.
INSERT INTO negocio.parada_baja(organizacion_id,sucursal_id,parada_id,motivo) VALUES (pg_temp.uid('o'),pg_temp.uid('s'),pg_temp.uid('psan1'),'prueba');
SELECT pg_temp.afirmar((SELECT acceso_id=pg_temp.uid('esc') AND dada_de_baja_en IS NOT NULL FROM negocio.parada_baja WHERE parada_id=pg_temp.uid('psan1')),'la baja guarda quién y cuándo');
INSERT INTO negocio.parada(organizacion_id,sucursal_id,id,ruta_id,posicion) VALUES (pg_temp.uid('o'),pg_temp.uid('s'),pg_temp.uid('psan1n'),pg_temp.uid('rsan'),3);
INSERT INTO negocio.parada_version(organizacion_id,sucursal_id,parada_id,revision,sitio_id) VALUES (pg_temp.uid('o'),pg_temp.uid('s'),pg_temp.uid('psan1n'),1,pg_temp.uid('a'));
COMMIT;
BEGIN;
SET LOCAL ROLE manifiestos_app;
SELECT pg_temp.contexto('esc');
SELECT pg_temp.afirmar((SELECT count(*)=2 FROM negocio.parada WHERE ruta_id=pg_temp.uid('rsan')),'tras la baja, el sitio vuelve a agregarse en la misma ruta');
SELECT pg_temp.rechaza($q$INSERT INTO negocio.parada_baja(organizacion_id,sucursal_id,parada_id) VALUES (pg_temp.uid('o'),pg_temp.uid('s'),pg_temp.uid('psan1'))$q$,'23505','una parada se da de baja una sola vez');
SELECT pg_temp.rechaza($q$UPDATE negocio.parada_baja SET motivo='cambio'$q$,'42501','la aplicación no reescribe la baja');
SELECT pg_temp.rechaza($q$DELETE FROM negocio.parada_baja$q$,'42501','la aplicación no retira la baja');
SELECT pg_temp.rechaza($q$INSERT INTO negocio.parada_baja(organizacion_id,sucursal_id,parada_id,motivo) VALUES (pg_temp.uid('o'),pg_temp.uid('s'),pg_temp.uid('psan2a'),'   ')$q$,'23514','el motivo no puede ir en blanco');
COMMIT;

-- Aislamiento: solo lectura no da de baja; otra sucursal no ve ni da de baja.
BEGIN;
SET LOCAL ROLE manifiestos_app;
SELECT pg_temp.contexto('lee');
SELECT pg_temp.afirmar((SELECT count(*)=1 FROM negocio.parada_baja),'solo lectura ve la baja');
SELECT pg_temp.rechaza($q$INSERT INTO negocio.parada_baja(organizacion_id,sucursal_id,parada_id) VALUES (pg_temp.uid('o'),pg_temp.uid('s'),pg_temp.uid('psan2a'))$q$,'42501','sin datos.escribir no da de baja');
SELECT pg_temp.contexto('otra');
SELECT pg_temp.afirmar((SELECT count(*)=0 FROM negocio.parada_baja),'otra sucursal no ve bajas ajenas');
SELECT pg_temp.rechaza($q$INSERT INTO negocio.parada_baja(organizacion_id,sucursal_id,parada_id) VALUES (pg_temp.uid('o'),pg_temp.uid('s'),pg_temp.uid('psan2a'))$q$,'42501','otra sucursal no da de baja paradas ajenas');
COMMIT;

BEGIN;
SELECT pg_temp.rechaza($q$UPDATE negocio.parada_baja SET motivo='cambio'$q$,'23514','la baja es inmutable incluso para el dueño');
SELECT pg_temp.rechaza($q$DELETE FROM negocio.parada_baja$q$,'23514','la baja no se borra ni por el dueño');
COMMIT;

-- Carga actual de la unidad: derivada, en m3, sin tope. Aún sin succiones (B2): 0 de forma tolerante.
BEGIN;
SET LOCAL ROLE manifiestos_app;
SELECT pg_temp.contexto('lee');
SELECT pg_temp.afirmar((SELECT banos=0 AND m3=0 AND capacidad_m3=1.000 AND porcentaje=0 AND ultima_tirada_en IS NULL AND desglose='[]'::jsonb
    FROM seguridad.carga_unidad_web(pg_temp.uid('o'),pg_temp.uid('lee'),pg_temp.uid('s'),pg_temp.uid('vsan'))),'carga inicial de una unidad de sanitarios: 0 de 1 m3');
SELECT pg_temp.afirmar((SELECT capacidad_m3=18.000 FROM seguridad.carga_unidad_web(pg_temp.uid('o'),pg_temp.uid('lee'),pg_temp.uid('s'),pg_temp.uid('vfos'))),'la carga trae la capacidad de la unidad');
SELECT pg_temp.afirmar((SELECT count(*)=0 FROM seguridad.carga_unidad_web(pg_temp.uid('o'),pg_temp.uid('lee'),pg_temp.uid('s'),gen_random_uuid())),'unidad inexistente no devuelve carga');
SELECT pg_temp.afirmar((SELECT count(*)=0 FROM seguridad.carga_unidad_web(pg_temp.uid('o'),pg_temp.uid('esc'),pg_temp.uid('s'),pg_temp.uid('vsan'))),'el acceso informado debe ser el de la sesión');
SELECT pg_temp.contexto('otra');
SELECT pg_temp.afirmar((SELECT count(*)=0 FROM seguridad.carga_unidad_web(pg_temp.uid('o'),pg_temp.uid('otra'),pg_temp.uid('s'),pg_temp.uid('vsan'))),'sin datos.leer en la sucursal no hay carga');
SELECT pg_temp.rechaza($q$SELECT * FROM negocio.carga_unidad_interna(pg_temp.uid('o'),pg_temp.uid('s'),pg_temp.uid('vsan'))$q$,'42501','la carga interna no se concede a la aplicación');
COMMIT;

-- Firma del generador precargada: marcador provisional sin archivo, con revisiones y sin reescritura.
BEGIN;
INSERT INTO negocio.firma_generador_precargada(organizacion_id,sucursal_id,empresa_id,revision,nombre_firmante,cargo)
VALUES (pg_temp.uid('o'),pg_temp.uid('s'),pg_temp.uid('e'),1,'Gerente (PROVISIONAL)','Gerente');
SELECT pg_temp.afirmar((SELECT es_provisional AND archivo_id IS NULL FROM negocio.firma_generador_precargada),'la firma precargada nace como marcador provisional sin archivo');
SELECT pg_temp.rechaza($q$INSERT INTO negocio.firma_generador_precargada(organizacion_id,sucursal_id,empresa_id,revision,nombre_firmante,es_provisional) VALUES (pg_temp.uid('o'),pg_temp.uid('s'),pg_temp.uid('e'),2,'Firma real',false)$q$,'23514','una firma no provisional exige archivo');
SELECT pg_temp.rechaza($q$INSERT INTO negocio.firma_generador_precargada(organizacion_id,sucursal_id,empresa_id,revision,nombre_firmante) VALUES (pg_temp.uid('o'),pg_temp.uid('s'),pg_temp.uid('e'),1,'Repetida')$q$,'23505','revisión de firma duplicada');
SELECT pg_temp.rechaza($q$UPDATE negocio.firma_generador_precargada SET nombre_firmante='Otro'$q$,'23514','la firma precargada no se reescribe');
SELECT pg_temp.rechaza($q$DELETE FROM negocio.firma_generador_precargada$q$,'23514','la firma precargada no se borra');
COMMIT;
BEGIN;
SET LOCAL ROLE manifiestos_app;
SELECT pg_temp.contexto('lee');
SELECT pg_temp.afirmar((SELECT count(*)=1 FROM negocio.firma_generador_precargada),'datos.leer ve la firma precargada');
SELECT pg_temp.rechaza($q$INSERT INTO negocio.firma_generador_precargada(organizacion_id,sucursal_id,empresa_id,revision,nombre_firmante) VALUES (pg_temp.uid('o'),pg_temp.uid('s'),pg_temp.uid('e'),2,'La app no la crea')$q$,'42501','la aplicación no crea firmas precargadas');
SELECT pg_temp.contexto('otra');
SELECT pg_temp.afirmar((SELECT count(*)=0 FROM negocio.firma_generador_precargada),'otra sucursal no ve la firma precargada');
COMMIT;
SELECT count(*) AS comprobaciones_sanitarios FROM comprobacion_sanitarios;
\echo 'Comprobaciones de la base de sanitarios terminadas sin error.'
