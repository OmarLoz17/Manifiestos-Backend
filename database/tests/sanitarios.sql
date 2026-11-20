\set ON_ERROR_STOP on
-- Captura de baños y tirada (0028), pasos B3-B6. Datos ficticios. Se insertan capturas como propietario (el disparador de
-- captura_trabajo hace el trabajo); la recepción HTTP completa se prueba en SanitariosMovilTests.
CREATE FUNCTION pg_temp.uid(t text) RETURNS uuid LANGUAGE sql IMMUTABLE AS $$ SELECT md5('sanitarios-tirada'||t)::uuid $$;
CREATE TEMP TABLE comprobacion_tirada(nombre text);
GRANT INSERT, SELECT ON comprobacion_tirada TO manifiestos_app;
CREATE FUNCTION pg_temp.afirmar(ok boolean,n text) RETURNS void LANGUAGE plpgsql AS $$
BEGIN IF ok IS DISTINCT FROM true THEN RAISE EXCEPTION 'FALLO: %',n; END IF; INSERT INTO comprobacion_tirada VALUES(n); END $$;
CREATE FUNCTION pg_temp.rechaza(q text,estado text,n text) RETURNS void LANGUAGE plpgsql AS $$
DECLARE obtenido text;
BEGIN
    BEGIN EXECUTE q; EXCEPTION WHEN OTHERS THEN GET STACKED DIAGNOSTICS obtenido=RETURNED_SQLSTATE; END;
    PERFORM pg_temp.afirmar(obtenido=estado,n||' ['||coalesce(obtenido,'sin error')||']');
END $$;
-- Succión: captura + foto con ubicación + trabajo SERVICIO_SANITARIOS (el disparador la procesa).
CREATE FUNCTION pg_temp.succion(cap text, jornada text, parada text, banos int, fecha timestamptz, persona text DEFAULT 'p1') RETURNS void LANGUAGE plpgsql AS $$
BEGIN
    INSERT INTO negocio.captura(organizacion_id,sucursal_id,id,asignacion_equipo_id,dispositivo_id,instalacion_app_id,ocurrida_en)
    VALUES(pg_temp.uid('o'),pg_temp.uid('s'),pg_temp.uid(cap),pg_temp.uid('as1'),pg_temp.uid('d'),pg_temp.uid('i'),fecha);
    INSERT INTO negocio.evidencia_captura(organizacion_id,sucursal_id,id,captura_id,rol,sha256,tamano_bytes,latitud,longitud,precision_metros,lectura_en)
    VALUES(pg_temp.uid('o'),pg_temp.uid('s'),pg_temp.uid(cap||'f'),pg_temp.uid(cap),'FOTO',sha256(cap::bytea),10,25.6702,-100.3091,8,fecha);
    INSERT INTO negocio.captura_trabajo(organizacion_id,sucursal_id,captura_id,tipo,persona_id,identidad_id,orden_derivada_id,jornada_id,
        asignacion_trabajo,revision_trabajo,version_datos,requiere_firma_cliente,datos_orden,banos,banos_asignados)
    VALUES(pg_temp.uid('o'),pg_temp.uid('s'),pg_temp.uid(cap),'SERVICIO_SANITARIOS',pg_temp.uid(persona),pg_temp.uid('x'),pg_temp.uid(parada),pg_temp.uid(jornada),
        'a','1','1',false,convert_to('{}','UTF8'),banos,banos);
END $$;
-- Tirada: captura + 2 firmas + trabajo TIRADA_SANITARIOS con capturas incluidas.
CREATE FUNCTION pg_temp.tirada(cap text, tirada text, unidad text, incluidas text[], fecha timestamptz, planta text DEFAULT 'ptar') RETURNS void LANGUAGE plpgsql AS $$
BEGIN
    INSERT INTO negocio.captura(organizacion_id,sucursal_id,id,asignacion_equipo_id,dispositivo_id,instalacion_app_id,ocurrida_en)
    VALUES(pg_temp.uid('o'),pg_temp.uid('s'),pg_temp.uid(cap),pg_temp.uid('as1'),pg_temp.uid('d'),pg_temp.uid('i'),fecha);
    INSERT INTO negocio.evidencia_captura(organizacion_id,sucursal_id,id,captura_id,rol,sha256,tamano_bytes) VALUES
        (pg_temp.uid('o'),pg_temp.uid('s'),pg_temp.uid(cap||'a'),pg_temp.uid(cap),'FIRMA_OPERADOR',sha256(('a'||cap)::bytea),10),
        (pg_temp.uid('o'),pg_temp.uid('s'),pg_temp.uid(cap||'b'),pg_temp.uid(cap),'FIRMA_RECEPCION',sha256(('b'||cap)::bytea),10);
    INSERT INTO negocio.captura_trabajo(organizacion_id,sucursal_id,captura_id,tipo,persona_id,identidad_id,orden_derivada_id,jornada_id,
        asignacion_trabajo,revision_trabajo,version_datos,requiere_firma_cliente,datos_orden,nombre_firmante,planta_id,planta_nombre,capturas_incluidas)
    VALUES(pg_temp.uid('o'),pg_temp.uid('s'),pg_temp.uid(cap),'TIRADA_SANITARIOS',pg_temp.uid('p1'),pg_temp.uid('x'),pg_temp.uid(tirada),pg_temp.uid('j2'),
        'a','1','1',false,convert_to('{"unidadId":"'||pg_temp.uid(unidad)||'"}','UTF8'),'Quien Recibe',pg_temp.uid(planta)::text,'nombre del teléfono',
        ARRAY(SELECT pg_temp.uid(x) FROM unnest(incluidas) x));
END $$;
CREATE FUNCTION pg_temp.carga(v text) RETURNS TABLE (banos integer, m3 numeric, capacidad_m3 numeric, porcentaje numeric, ultima_tirada_en timestamptz, desglose jsonb) AS $$ SELECT * FROM negocio.carga_unidad_interna(pg_temp.uid('o'),pg_temp.uid('s'),pg_temp.uid(v)) $$ LANGUAGE sql;
CREATE FUNCTION pg_temp.motivo(cap text) RETURNS text LANGUAGE sql AS $$ SELECT motivo FROM negocio.captura_por_revisar WHERE captura_id=pg_temp.uid(cap) $$;

BEGIN;
INSERT INTO negocio.organizacion VALUES (pg_temp.uid('o'),'Grupo Ficticio Tirada'),(pg_temp.uid('o2'),'Otro Grupo Ficticio');
INSERT INTO negocio.empresa VALUES (pg_temp.uid('o'),pg_temp.uid('e'),'EMP-T'),(pg_temp.uid('o'),pg_temp.uid('ec'),'CLI-T'),(pg_temp.uid('o2'),pg_temp.uid('e2'),'EMP-2');
INSERT INTO negocio.sucursal(organizacion_id,id,empresa_id,codigo,nombre) VALUES
 (pg_temp.uid('o'),pg_temp.uid('s'),pg_temp.uid('e'),'T1','Sucursal ficticia T1'),
 (pg_temp.uid('o'),pg_temp.uid('s2'),pg_temp.uid('e'),'T2','Sucursal ficticia T2'),
 (pg_temp.uid('o2'),pg_temp.uid('s3'),pg_temp.uid('e2'),'T3','Sucursal ficticia T3');
INSERT INTO negocio.empresa_sucursal VALUES (pg_temp.uid('o'),pg_temp.uid('s'),pg_temp.uid('ec')),(pg_temp.uid('o'),pg_temp.uid('s'),pg_temp.uid('e'));
INSERT INTO negocio.ficha_empresa(organizacion_id,empresa_id,revision,nombre_fiscal,nombre_comercial) VALUES
 (pg_temp.uid('o'),pg_temp.uid('e'),1,'Empresa Ficticia Tirada','Empresa Tirada'),(pg_temp.uid('o'),pg_temp.uid('ec'),1,'Cliente General Ficticio','Cliente General');
INSERT INTO negocio.autorizacion_empresa(organizacion_id,empresa_id,autoridad,finalidad,numero) VALUES
 (pg_temp.uid('o'),pg_temp.uid('e'),'Medio ambiente','Transportista','AUT-T-1'),(pg_temp.uid('o'),pg_temp.uid('e'),'Medio ambiente','Generador','AUT-G-1');
INSERT INTO negocio.cliente VALUES (pg_temp.uid('o'),pg_temp.uid('s'),pg_temp.uid('c'),pg_temp.uid('ec'),'CG-1');
INSERT INTO negocio.vehiculo VALUES (pg_temp.uid('o'),pg_temp.uid('s'),pg_temp.uid('vsan'),'SAN-T1'),(pg_temp.uid('o'),pg_temp.uid('s'),pg_temp.uid('vsan2'),'SAN-T2'),
 (pg_temp.uid('o'),pg_temp.uid('s'),pg_temp.uid('vfos'),'FOS-T1');
INSERT INTO negocio.ficha_vehiculo(organizacion_id,sucursal_id,id,vehiculo_id,revision,capacidad_id,clasificacion,numero_economico) VALUES
 (pg_temp.uid('o'),pg_temp.uid('s'),pg_temp.uid('fsan'),pg_temp.uid('vsan'),1,4,'sanitarios','SAN-T1'),
 (pg_temp.uid('o'),pg_temp.uid('s'),pg_temp.uid('fsan2'),pg_temp.uid('vsan2'),1,4,'sanitarios','SAN-T2'),
 (pg_temp.uid('o'),pg_temp.uid('s'),pg_temp.uid('ffos'),pg_temp.uid('vfos'),1,3,'fosas','FOS-T1');
INSERT INTO negocio.sitio VALUES (pg_temp.uid('o'),pg_temp.uid('s'),pg_temp.uid('a'),'Sitio A'),(pg_temp.uid('o'),pg_temp.uid('s'),pg_temp.uid('b'),'Sitio B'),
 (pg_temp.uid('o'),pg_temp.uid('s'),pg_temp.uid('c3'),'Sitio C'),(pg_temp.uid('o'),pg_temp.uid('s'),pg_temp.uid('ptar'),'PTAR T');
INSERT INTO negocio.ficha_sitio(organizacion_id,sucursal_id,sitio_id,revision,nombre) VALUES (pg_temp.uid('o'),pg_temp.uid('s'),pg_temp.uid('ptar'),1,'PTAR NORTE');
INSERT INTO negocio.punto_servicio(organizacion_id,sucursal_id,sitio_id) VALUES
 (pg_temp.uid('o'),pg_temp.uid('s'),pg_temp.uid('a')),(pg_temp.uid('o'),pg_temp.uid('s'),pg_temp.uid('b')),(pg_temp.uid('o'),pg_temp.uid('s'),pg_temp.uid('c3'));
INSERT INTO seguridad.persona VALUES (pg_temp.uid('o'),pg_temp.uid('p1'),'Operador ficticio uno'),(pg_temp.uid('o'),pg_temp.uid('p2'),'Operador ficticio dos');
INSERT INTO negocio.asignacion_unidad(organizacion_id,sucursal_id,vehiculo_id,revision,persona_id) VALUES
 (pg_temp.uid('o'),pg_temp.uid('s'),pg_temp.uid('vsan'),1,pg_temp.uid('p1')),(pg_temp.uid('o'),pg_temp.uid('s'),pg_temp.uid('vsan2'),1,pg_temp.uid('p1'));
INSERT INTO seguridad.dispositivo VALUES (pg_temp.uid('o'),pg_temp.uid('d'),'TEST-T');
INSERT INTO seguridad.instalacion_app VALUES (pg_temp.uid('o'),pg_temp.uid('i'),pg_temp.uid('d'),true);
INSERT INTO seguridad.acceso_autorizado(organizacion_id,id,correo_permitido,activo,rol_codigo) VALUES
 (pg_temp.uid('o'),pg_temp.uid('a'),'equipo-t@example.invalid',true,'telefono_operador'),
 (pg_temp.uid('o'),pg_temp.uid('lee'),'lee-t@example.invalid',true,'operaciones'),
 (pg_temp.uid('o'),pg_temp.uid('otra'),'otra-t@example.invalid',true,'operaciones');
INSERT INTO seguridad.permiso_sucursal VALUES (pg_temp.uid('o'),pg_temp.uid('s'),pg_temp.uid('lee'),'datos.leer'),(pg_temp.uid('o'),pg_temp.uid('s2'),pg_temp.uid('otra'),'datos.leer');
INSERT INTO seguridad.asignacion_equipo(organizacion_id,sucursal_id,id,dispositivo_id,acceso_id,persona_id,revision,desde)
VALUES (pg_temp.uid('o'),pg_temp.uid('s'),pg_temp.uid('as1'),pg_temp.uid('d'),pg_temp.uid('a'),pg_temp.uid('p1'),1,now()-interval '30 days');
INSERT INTO negocio.firma_generador_precargada(organizacion_id,sucursal_id,empresa_id,revision,nombre_firmante,cargo)
VALUES (pg_temp.uid('o'),pg_temp.uid('s'),pg_temp.uid('e'),1,'Gerente (PROVISIONAL)','Gerente');
-- Rutas: dos de la MISMA unidad (LMV y MJS) y una de fosas. Jornadas: j1 ayer (r1), j2 hoy (r2).
INSERT INTO negocio.ruta(organizacion_id,sucursal_id,id) VALUES (pg_temp.uid('o'),pg_temp.uid('s'),pg_temp.uid('r1')),(pg_temp.uid('o'),pg_temp.uid('s'),pg_temp.uid('r2')),
 (pg_temp.uid('o'),pg_temp.uid('s'),pg_temp.uid('rf')),(pg_temp.uid('o'),pg_temp.uid('s'),pg_temp.uid('r3'));
INSERT INTO negocio.ruta_version(organizacion_id,sucursal_id,ruta_id,revision,nombre,dias,vehiculo_id) VALUES
 (pg_temp.uid('o'),pg_temp.uid('s'),pg_temp.uid('r1'),1,'SAN-T1-LMV',21,pg_temp.uid('vsan')),
 (pg_temp.uid('o'),pg_temp.uid('s'),pg_temp.uid('r2'),1,'SAN-T1-MJS',42,pg_temp.uid('vsan')),
 (pg_temp.uid('o'),pg_temp.uid('s'),pg_temp.uid('rf'),1,'FOS-T1-LMV',21,pg_temp.uid('vfos')),
 (pg_temp.uid('o'),pg_temp.uid('s'),pg_temp.uid('r3'),1,'SAN-T2-D',64,pg_temp.uid('vsan2'));
INSERT INTO negocio.parada(organizacion_id,sucursal_id,id,ruta_id,posicion) VALUES
 (pg_temp.uid('o'),pg_temp.uid('s'),pg_temp.uid('pa'),pg_temp.uid('r1'),1),(pg_temp.uid('o'),pg_temp.uid('s'),pg_temp.uid('pb'),pg_temp.uid('r1'),2),
 (pg_temp.uid('o'),pg_temp.uid('s'),pg_temp.uid('pc'),pg_temp.uid('r2'),1),(pg_temp.uid('o'),pg_temp.uid('s'),pg_temp.uid('pd'),pg_temp.uid('r3'),1);
INSERT INTO negocio.parada_version(organizacion_id,sucursal_id,id,parada_id,revision,sitio_id) VALUES
 (pg_temp.uid('o'),pg_temp.uid('s'),pg_temp.uid('pva'),pg_temp.uid('pa'),1,pg_temp.uid('a')),(pg_temp.uid('o'),pg_temp.uid('s'),pg_temp.uid('pvb'),pg_temp.uid('pb'),1,pg_temp.uid('b')),
 (pg_temp.uid('o'),pg_temp.uid('s'),pg_temp.uid('pvc'),pg_temp.uid('pc'),1,pg_temp.uid('c3')),(pg_temp.uid('o'),pg_temp.uid('s'),pg_temp.uid('pvd'),pg_temp.uid('pd'),1,pg_temp.uid('b'));
INSERT INTO negocio.parada_cliente(organizacion_id,sucursal_id,version_id,cliente_id,banos_planeados) VALUES
 (pg_temp.uid('o'),pg_temp.uid('s'),pg_temp.uid('pva'),pg_temp.uid('c'),12),(pg_temp.uid('o'),pg_temp.uid('s'),pg_temp.uid('pvb'),pg_temp.uid('c'),20),
 (pg_temp.uid('o'),pg_temp.uid('s'),pg_temp.uid('pvc'),pg_temp.uid('c'),8);
INSERT INTO negocio.jornada(organizacion_id,sucursal_id,id,ruta_id,fecha) VALUES
 (pg_temp.uid('o'),pg_temp.uid('s'),pg_temp.uid('j1'),pg_temp.uid('r1'),current_date-1),(pg_temp.uid('o'),pg_temp.uid('s'),pg_temp.uid('j2'),pg_temp.uid('r2'),current_date),
 (pg_temp.uid('o'),pg_temp.uid('s'),pg_temp.uid('j3'),pg_temp.uid('r3'),current_date);
COMMIT;

-- 1-4. Carga de la unidad: baños*16/1000 en m3, sin tope, compartida entre rutas y con días anteriores.
BEGIN;
SELECT pg_temp.succion('c1','j1','pa',10,now()-interval '1 day');
SELECT pg_temp.succion('c2','j2','pc',40,now()-interval '3 hours');
SELECT pg_temp.afirmar((SELECT banos=50 AND m3=0.800 AND capacidad_m3=1.000 AND porcentaje=80.0 AND jsonb_array_length(desglose)=2 FROM pg_temp.carga('vsan')),
  '50 baños = 0.800 m3 = 80 %, con succiones de dos días y de dos rutas de la misma unidad');
SELECT pg_temp.afirmar((SELECT count(*)=2 FROM negocio.succion_parada WHERE vehiculo_id=pg_temp.uid('vsan')),'ambas rutas alimentan la misma unidad');
-- (0055) Menos baños de los asignados -> una incidencia automática «servicio_parcial» por captura; completos o de más no generan aviso.
SELECT pg_temp.afirmar((SELECT count(*) FROM negocio.incidencia i WHERE i.motivo='servicio_parcial' AND i.descripcion LIKE 'Se atendieron % de % baños.')
    = (SELECT count(*) FROM negocio.succion_parada s WHERE s.banos_succionados<s.banos_asignados),'una incidencia automática por cada captura con baños incompletos');
SELECT pg_temp.afirmar(NOT EXISTS (SELECT 1 FROM negocio.incidencia i JOIN negocio.succion_parada s ON s.parada_id=i.parada_id AND s.fecha_captura=i.ocurrida_en
    WHERE i.motivo='servicio_parcial' AND i.descripcion LIKE 'Se atendieron % de % baños.' AND (i.banos_atendidos IS DISTINCT FROM s.banos_succionados OR i.banos_asignados IS DISTINCT FROM s.banos_asignados)),'la incidencia automática lleva asignados y atendidos');
SELECT pg_temp.succion('c3','j1','pb',16,now()-interval '2 hours');
SELECT pg_temp.afirmar((SELECT banos=66 AND m3=1.056 AND porcentaje=105.6 FROM pg_temp.carga('vsan')),'sin tope: 66 baños = 1.056 m3 = 105.6 %');
SELECT pg_temp.afirmar((SELECT banos=0 AND m3=0 FROM pg_temp.carga('vsan2')),'otra unidad no ve esa carga');
-- Parada ajena a la jornada o persona que no opera la unidad: se guarda y queda por revisar, sin succión ni excepción.
SELECT pg_temp.succion('x1','j1','pc',5,now());
SELECT pg_temp.afirmar(pg_temp.motivo('x1')='parada_no_resuelta' AND NOT EXISTS (SELECT 1 FROM negocio.succion_parada WHERE captura_id=pg_temp.uid('x1')),'parada de otra ruta -> parada_no_resuelta');
SELECT pg_temp.succion('x2','j1','pa',5,now(),'p2');
SELECT pg_temp.afirmar(pg_temp.motivo('x2')='parada_no_resuelta','persona que no opera la unidad -> parada_no_resuelta');
SELECT pg_temp.afirmar((SELECT banos=66 FROM pg_temp.carga('vsan')),'lo revisable no cuenta en la carga');
COMMIT;

-- 5-7. Tirada: un manifiesto, folio compartido con fosa fija, carga a 0, succiones posteriores vuelven a sumar.
BEGIN;
SELECT negocio.armar_manifiesto_fosa_fija(pg_temp.uid('o'),pg_temp.uid('s'),pg_temp.uid('c'),pg_temp.uid('vfos'),true);
SELECT pg_temp.tirada('t1','tir1','vsan',ARRAY['c1','c2','c3'],now()-interval '1 hour');
SELECT pg_temp.afirmar((SELECT count(*)=1 FROM negocio.tirada_manifiesto WHERE tirada_id=pg_temp.uid('tir1')),'la tirada arma UN manifiesto');
SELECT pg_temp.afirmar((SELECT m.tipo='sanitarios' AND m.cliente_id IS NULL FROM negocio.manifiesto m JOIN negocio.tirada_manifiesto tm ON tm.manifiesto_id=m.id),'manifiesto de sanitarios sin cliente');
SELECT pg_temp.afirmar((SELECT v.numero_manifiesto=('002/'||extract(year FROM now() AT TIME ZONE 'UTC')::int) AND v.version_plantilla='sanitarios-1'
   FROM negocio.version_manifiesto v JOIN negocio.tirada_manifiesto tm ON tm.manifiesto_id=v.manifiesto_id),'folio consecutivo compartido: fosa 001, sanitarios 002');
SELECT pg_temp.afirmar((SELECT d.cantidad=1 AND d.unidad_cantidad='m3' AND d.naturaleza_cantidad='estimada' AND d.metodo_cuantificacion='capacidad_unidad_llena' AND d.numero_contenedores=66
   FROM negocio.detalle_manifiesto d JOIN negocio.version_manifiesto v ON v.id=d.version_id JOIN negocio.tirada_manifiesto tm ON tm.manifiesto_id=v.manifiesto_id),'renglón resumen: cantidad = capacidad de la unidad (1 m3) estimada; los baños siguen en numero_contenedores');
SELECT pg_temp.afirmar((SELECT array_agg(mp.captura_id ORDER BY mp.renglon)=ARRAY[pg_temp.uid('c1'),pg_temp.uid('c2'),pg_temp.uid('c3')]
   AND bool_and(mp.latitud=25.6702 AND mp.longitud=-100.3091 AND mp.evidencia_foto_id IS NOT NULL) FROM negocio.manifiesto_parada mp),'paradas ordenadas por fecha, con la coordenada de su foto');
SELECT pg_temp.afirmar((SELECT mp.clientes->0->>'nombre'='Cliente General' FROM negocio.manifiesto_parada mp WHERE mp.captura_id=pg_temp.uid('c1')),'instantánea de clientes de la parada');
SELECT pg_temp.afirmar((SELECT r.planta_nombre='PTAR NORTE' AND r.nombre_recibe='Quien Recibe' AND r.firma_generador_id IS NOT NULL FROM negocio.recepcion_manifiesto r),
  'recepción con planta tomada de la base (no del teléfono), quien recibe y firma precargada provisional');
SELECT pg_temp.afirmar((SELECT p.rol IS NOT NULL FROM negocio.participante_manifiesto p JOIN negocio.version_manifiesto v ON v.id=p.version_id JOIN negocio.tirada_manifiesto tm ON tm.manifiesto_id=v.manifiesto_id
   WHERE p.rol='generador' AND p.empresa_id=pg_temp.uid('e') AND p.origen_domicilio='fiscal'),'generador = nuestra empresa con domicilio fiscal');
SELECT pg_temp.afirmar((SELECT banos=0 AND m3=0 AND porcentaje=0 AND ultima_tirada_en IS NOT NULL FROM pg_temp.carga('vsan')),'la carga vuelve a 0 al armarse el manifiesto');
SELECT pg_temp.succion('c4','j2','pc',20,now());
SELECT pg_temp.afirmar((SELECT banos=20 AND m3=0.320 AND porcentaje=32.0 FROM pg_temp.carga('vsan')),'succión posterior vuelve a sumar desde 0');
-- 8. Una captura entra en una sola tirada.
SELECT pg_temp.rechaza($q$INSERT INTO negocio.tirada_parada(organizacion_id,sucursal_id,tirada_id,captura_id,renglon) VALUES (pg_temp.uid('o'),pg_temp.uid('s'),pg_temp.uid('tir1'),pg_temp.uid('c1'),9)$q$,'23505','tirada_parada impide incluir dos veces');
-- 9. Doble tirada con capturas ya incluidas / reenvío con otro id de captura y mismo tiradaId: por revisar, sin otro manifiesto ni folio.
SELECT pg_temp.tirada('t2','tir2','vsan',ARRAY['c1','c2'],now());
SELECT pg_temp.afirmar(pg_temp.motivo('t2')='tirada_capturas_ya_incluidas' AND (SELECT count(*)=1 FROM negocio.tirada_manifiesto),'doble tirada -> tirada_capturas_ya_incluidas, un solo manifiesto');
SELECT pg_temp.tirada('t2b','tir1','vsan',ARRAY['c1','c2','c3'],now());
SELECT pg_temp.afirmar(pg_temp.motivo('t2b')='tirada_capturas_ya_incluidas' AND (SELECT count(*)=1 FROM negocio.tirada_manifiesto),'mismo tiradaId con otra captura no duplica');
SELECT pg_temp.afirmar((SELECT ultimo=2 FROM negocio.contador_folio WHERE organizacion_id=pg_temp.uid('o')),'los intentos fallidos no consumen folio');
-- 10. Tirada sin carga; unidad distinta; planta que no es planta.
SELECT pg_temp.tirada('t3','tir3','vsan',ARRAY[]::text[],now());
SELECT pg_temp.afirmar(pg_temp.motivo('t3')='tirada_sin_carga','tirada sin capturas incluidas -> tirada_sin_carga');
SELECT pg_temp.tirada('t4','tir4','vsan2',ARRAY['c4'],now());
SELECT pg_temp.afirmar(pg_temp.motivo('t4')='tirada_no_resuelta' AND (SELECT count(*)=1 FROM negocio.tirada_manifiesto),'capturas de otra unidad -> tirada_no_resuelta');
SELECT pg_temp.tirada('t5','tir5','vsan',ARRAY['c4'],now(),'a');
SELECT pg_temp.afirmar(pg_temp.motivo('t5')='tirada_no_resuelta','un punto de servicio no es planta -> tirada_no_resuelta');
-- Problema por parada de sanitarios: se acepta, no se marca ni arma nada.
INSERT INTO negocio.captura(organizacion_id,sucursal_id,id,asignacion_equipo_id,dispositivo_id,instalacion_app_id,ocurrida_en)
VALUES(pg_temp.uid('o'),pg_temp.uid('s'),pg_temp.uid('pr1'),pg_temp.uid('as1'),pg_temp.uid('d'),pg_temp.uid('i'),now());
INSERT INTO negocio.captura_trabajo(organizacion_id,sucursal_id,captura_id,tipo,persona_id,identidad_id,orden_derivada_id,jornada_id,asignacion_trabajo,revision_trabajo,version_datos,requiere_firma_cliente,datos_orden,problema_motivo)
VALUES(pg_temp.uid('o'),pg_temp.uid('s'),pg_temp.uid('pr1'),'PROBLEMA',pg_temp.uid('p1'),pg_temp.uid('x'),pg_temp.uid('pa'),pg_temp.uid('j1'),'a','1','1',false,convert_to('{}','UTF8'),'sin_acceso');
SELECT pg_temp.afirmar(pg_temp.motivo('pr1') IS NULL,'problema de parada de sanitarios no se marca como orden no resuelta');
COMMIT;

-- 11. Tirada que llega ANTES que su succión: queda esperando (visible) y se arma al llegar. Succión tardía con fecha anterior a una tirada armada.
BEGIN;
SELECT pg_temp.tirada('t6','tir6','vsan',ARRAY['c4','c5'],now());
SELECT pg_temp.afirmar((SELECT estado='esperando' AND faltantes=1 FROM negocio.estado_tirada WHERE tirada_id=pg_temp.uid('tir6')),'tirada con una captura que no llega: esperando, faltantes=1');
SELECT pg_temp.afirmar((SELECT banos=20 FROM pg_temp.carga('vsan')),'esperando no reinicia la barra');
SELECT pg_temp.succion('c5','j2','pc',10,now()-interval '30 minutes');
SELECT pg_temp.afirmar((SELECT estado='armada' AND faltantes=0 FROM negocio.estado_tirada WHERE tirada_id=pg_temp.uid('tir6')),'al llegar la succión la tirada se arma');
SELECT pg_temp.afirmar((SELECT banos=0 FROM pg_temp.carga('vsan')),'la barra reinicia al armarse');
SELECT pg_temp.afirmar((SELECT v.numero_manifiesto=('003/'||extract(year FROM now() AT TIME ZONE 'UTC')::int) FROM negocio.version_manifiesto v JOIN negocio.tirada_manifiesto tm ON tm.manifiesto_id=v.manifiesto_id WHERE tm.tirada_id=pg_temp.uid('tir6')),'folio consecutivo sin huecos');
-- Succión tardía: fecha anterior a una tirada ya armada, llega después: cuenta en la carga y NO en el manifiesto ya armado.
SELECT pg_temp.succion('c6','j2','pc',7,now()-interval '5 hours');
SELECT pg_temp.afirmar((SELECT banos=7 FROM pg_temp.carga('vsan')) AND (SELECT count(*)=0 FROM negocio.tirada_parada WHERE captura_id=pg_temp.uid('c6')),'succión tardía queda en la carga actual y fuera de la tirada anterior');
-- Tirada que espera algo que no llega: se marca incompleta (nunca se borra).
SELECT pg_temp.tirada('t7','tir7','vsan',ARRAY['c6','nunca'],now());
SELECT pg_temp.afirmar((SELECT estado='esperando' FROM negocio.estado_tirada WHERE tirada_id=pg_temp.uid('tir7')),'tirada con captura inexistente espera');
SELECT pg_temp.afirmar(negocio.marcar_tiradas_incompletas(pg_temp.uid('o'),pg_temp.uid('s'),interval '0 seconds')=1,'una tirada que espera demasiado se marca incompleta');
SELECT pg_temp.afirmar(pg_temp.motivo('t7')='tirada_incompleta' AND (SELECT estado='por_revisar' FROM negocio.estado_tirada WHERE tirada_id=pg_temp.uid('tir7')),'tirada incompleta -> por revisar sin borrar');
-- Captura declarada que se resuelve a OTRA unidad (j3 es de vsan2): al llegar, la tirada de vsan pasa a revisión, no espera para siempre.
SELECT pg_temp.succion('c9','j2','pc',3,now()-interval '20 minutes');
SELECT pg_temp.tirada('t8','tir8','vsan',ARRAY['c9','c7'],now());
SELECT pg_temp.afirmar((SELECT estado='esperando' AND faltantes=1 FROM negocio.estado_tirada WHERE tirada_id=pg_temp.uid('tir8')),'tirada que espera c7');
SELECT pg_temp.succion('c7','j3','pd',4,now()-interval '10 minutes');
SELECT pg_temp.afirmar(pg_temp.motivo('t8')='tirada_no_resuelta' AND (SELECT estado='por_revisar' FROM negocio.estado_tirada WHERE tirada_id=pg_temp.uid('tir8')),
  'captura declarada que llegó con otra unidad -> la tirada pasa a revisión (no espera para siempre)');
COMMIT;

-- 12. Rollback no consume folio.
DO $$
DECLARE antes integer; despues integer;
BEGIN
    SELECT ultimo INTO antes FROM negocio.contador_folio WHERE organizacion_id=pg_temp.uid('o');
    BEGIN
        PERFORM negocio.siguiente_folio(pg_temp.uid('o'),pg_temp.uid('s'),extract(year FROM now() AT TIME ZONE 'UTC')::int);
        RAISE EXCEPTION 'revertir' USING ERRCODE='P0001';
    EXCEPTION WHEN SQLSTATE 'P0001' THEN NULL; END;
    SELECT ultimo INTO despues FROM negocio.contador_folio WHERE organizacion_id=pg_temp.uid('o');
    PERFORM pg_temp.afirmar(antes=despues,'revertir una tirada no consume número de folio');
END $$;

-- 13. Inmutabilidad y reglas del manifiesto sin cliente.
BEGIN;
SELECT pg_temp.rechaza($q$UPDATE negocio.succion_parada SET banos_succionados=1$q$,'23514','succion_parada inmutable');
SELECT pg_temp.rechaza($q$DELETE FROM negocio.tirada$q$,'23514','tirada inmutable');
SELECT pg_temp.rechaza($q$UPDATE negocio.manifiesto_parada SET banos=1$q$,'23514','manifiesto_parada inmutable');
SELECT pg_temp.rechaza($q$DELETE FROM negocio.tirada_parada$q$,'23514','tirada_parada inmutable');
SELECT pg_temp.rechaza($q$INSERT INTO negocio.manifiesto(organizacion_id,sucursal_id,tipo) VALUES (pg_temp.uid('o'),pg_temp.uid('s'),'fosa_fija')$q$,'23514','sin cliente solo en sanitarios');
SELECT pg_temp.rechaza($q$INSERT INTO negocio.manifiesto(organizacion_id,sucursal_id,cliente_id,tipo) VALUES (pg_temp.uid('o'),pg_temp.uid('s'),pg_temp.uid('c'),'sanitarios')$q$,'23514','sanitarios no lleva cliente');
SELECT pg_temp.rechaza($q$INSERT INTO negocio.manifiesto_parada(organizacion_id,sucursal_id,version_id,renglon,parada_id,captura_id,sitio_id,fecha,banos) VALUES (pg_temp.uid('o'),pg_temp.uid('s'),(SELECT id FROM negocio.version_manifiesto LIMIT 1),99,pg_temp.uid('pa'),pg_temp.uid('c1'),pg_temp.uid('a'),now(),1)$q$,'23514','los hijos solo entran con su versión');
COMMIT;

-- 14. Aislamiento: otra sucursal / lector sin permiso no ve nada; la aplicación no escribe estas tablas.
BEGIN;
SET LOCAL ROLE manifiestos_app;
SELECT set_config('app.organizacion_id',pg_temp.uid('o')::text,true), set_config('app.acceso_id',pg_temp.uid('lee')::text,true);
SELECT pg_temp.afirmar((SELECT count(*)>=5 FROM negocio.succion_parada) AND (SELECT count(*)>=3 FROM negocio.tirada) AND (SELECT count(*)>=3 FROM negocio.estado_tirada),'datos.leer ve succiones, tiradas y su estado');
SELECT pg_temp.rechaza($q$INSERT INTO negocio.tirada_parada(organizacion_id,sucursal_id,tirada_id,captura_id,renglon) VALUES (pg_temp.uid('o'),pg_temp.uid('s'),pg_temp.uid('tir1'),pg_temp.uid('c2'),5)$q$,'42501','la aplicación no escribe tirada_parada');
SELECT pg_temp.rechaza($q$SELECT negocio.bloquear_unidad(pg_temp.uid('o'),pg_temp.uid('vsan'))$q$,'42501','el candado de unidad no se concede a la aplicación');
SELECT pg_temp.rechaza($q$SELECT negocio.armar_manifiesto_sanitarios(pg_temp.uid('o'),pg_temp.uid('s'),pg_temp.uid('tir1'))$q$,'42501','armar no se concede a la aplicación');
SELECT set_config('app.acceso_id',pg_temp.uid('otra')::text,true);
SELECT pg_temp.afirmar((SELECT count(*)=0 FROM negocio.succion_parada) AND (SELECT count(*)=0 FROM negocio.manifiesto_parada) AND (SELECT count(*)=0 FROM negocio.recepcion_manifiesto)
   AND (SELECT count(*)=0 FROM negocio.estado_tirada),'otra sucursal no ve tablas nuevas');
COMMIT;

-- 15. (0030) Una parada sin clientes se permite en la ruta pero NO llega al operador; con clientes si. Ni sanitarios ni fosas.
BEGIN;
INSERT INTO negocio.estado_jornada(organizacion_id,sucursal_id,jornada_id,revision,estado) VALUES (pg_temp.uid('o'),pg_temp.uid('s'),pg_temp.uid('j2'),1,'programada');
INSERT INTO negocio.operador_ficha(organizacion_id,sucursal_id,persona_id,revision,nombre_completo,correo) VALUES (pg_temp.uid('o'),pg_temp.uid('s'),pg_temp.uid('p1'),1,'Operador ficticio uno','equipo-t@example.invalid');
INSERT INTO negocio.parada(organizacion_id,sucursal_id,id,ruta_id,posicion) VALUES (pg_temp.uid('o'),pg_temp.uid('s'),pg_temp.uid('pvacia'),pg_temp.uid('r2'),2);
INSERT INTO negocio.parada_version(organizacion_id,sucursal_id,id,parada_id,revision,sitio_id) VALUES (pg_temp.uid('o'),pg_temp.uid('s'),pg_temp.uid('pvvacia'),pg_temp.uid('pvacia'),1,pg_temp.uid('a'));
SELECT pg_temp.afirmar((SELECT count(*)=2 FROM negocio.parada WHERE ruta_id=pg_temp.uid('r2')),'la ruta de hoy tiene dos paradas, una sin clientes');
SELECT pg_temp.afirmar((SELECT jsonb_array_length(j->'jornadas'->0->'paradas')=1 AND (j->'jornadas'->0->'paradas'->0->>'paradaId')::uuid=pg_temp.uid('pc')
        FROM (SELECT seguridad.trabajo_operador(pg_temp.uid('o'),pg_temp.uid('a'),pg_temp.uid('i'),current_date) AS j) x),
   'trabajo_operador omite la parada sin clientes y entrega la que si tiene');
SELECT pg_temp.afirmar((SELECT jsonb_array_length(j->'jornadas'->0->'paradas'->0->'clientes')=1 FROM (SELECT seguridad.trabajo_operador(pg_temp.uid('o'),pg_temp.uid('a'),pg_temp.uid('i'),current_date) AS j) x),
   'la parada entregada conserva sus clientes');
INSERT INTO negocio.parada_version(organizacion_id,sucursal_id,id,parada_id,revision,sitio_id) VALUES (pg_temp.uid('o'),pg_temp.uid('s'),pg_temp.uid('pvvacia2'),pg_temp.uid('pvacia'),2,pg_temp.uid('a'));
INSERT INTO negocio.parada_cliente(organizacion_id,sucursal_id,version_id,cliente_id,banos_planeados) VALUES (pg_temp.uid('o'),pg_temp.uid('s'),pg_temp.uid('pvvacia2'),pg_temp.uid('c'),5);
SELECT pg_temp.afirmar((SELECT jsonb_array_length(j->'jornadas'->0->'paradas')=2 FROM (SELECT seguridad.trabajo_operador(pg_temp.uid('o'),pg_temp.uid('a'),pg_temp.uid('i'),current_date) AS j) x),
   'con cliente la parada llega al operador');
INSERT INTO negocio.parada_version(organizacion_id,sucursal_id,id,parada_id,revision,sitio_id) VALUES (pg_temp.uid('o'),pg_temp.uid('s'),pg_temp.uid('pvvacia3'),pg_temp.uid('pvacia'),3,pg_temp.uid('a'));
SELECT pg_temp.afirmar((SELECT jsonb_array_length(j->'jornadas'->0->'paradas')=1 FROM (SELECT seguridad.trabajo_operador(pg_temp.uid('o'),pg_temp.uid('a'),pg_temp.uid('i'),current_date) AS j) x),
   'una revision sin clientes la vuelve a ocultar');
COMMIT;

-- 16. (0030) La tirada lleva la FECHA DE LA TIRADA (cuando firma quien recibe): el folio usa su anio y no el del momento en que el
--     servidor la arma. Una tirada = una unidad: capturas de dos unidades no se mezclan (cubierto arriba con t4/t8).
BEGIN;
INSERT INTO negocio.captura(organizacion_id,sucursal_id,id,asignacion_equipo_id,dispositivo_id,instalacion_app_id,ocurrida_en)
VALUES(pg_temp.uid('o'),pg_temp.uid('s'),pg_temp.uid('c10'),pg_temp.uid('as1'),pg_temp.uid('d'),pg_temp.uid('i'),now()-interval '2 days');
INSERT INTO negocio.evidencia_captura(organizacion_id,sucursal_id,id,captura_id,rol,sha256,tamano_bytes,latitud,longitud,precision_metros,lectura_en)
VALUES(pg_temp.uid('o'),pg_temp.uid('s'),pg_temp.uid('c10f'),pg_temp.uid('c10'),'FOTO',sha256('c10'::bytea),10,25.6702,-100.3091,8,now()-interval '2 days');
INSERT INTO negocio.captura_trabajo(organizacion_id,sucursal_id,captura_id,tipo,persona_id,identidad_id,orden_derivada_id,jornada_id,
    asignacion_trabajo,revision_trabajo,version_datos,requiere_firma_cliente,datos_orden,banos,banos_asignados)
VALUES(pg_temp.uid('o'),pg_temp.uid('s'),pg_temp.uid('c10'),'SERVICIO_SANITARIOS',pg_temp.uid('p1'),pg_temp.uid('x'),pg_temp.uid('pc'),pg_temp.uid('j2'),'a','1','1',false,convert_to('{}','UTF8'),25,25);
SELECT pg_temp.tirada('t10','tir10','vsan',ARRAY['c10'],(date_trunc('year', now() AT TIME ZONE 'UTC') AT TIME ZONE 'UTC') - interval '1 hour');
SELECT pg_temp.afirmar((SELECT v.numero_manifiesto=('001/'||(extract(year FROM now() AT TIME ZONE 'UTC')::int-1))
    FROM negocio.version_manifiesto v JOIN negocio.tirada_manifiesto tm ON tm.manifiesto_id=v.manifiesto_id WHERE tm.tirada_id=pg_temp.uid('tir10')),
  'el folio lleva el anio de la tirada (001/AAAA del anio en que se tiro), no el del servidor');
SELECT pg_temp.afirmar((SELECT d.cantidad=1 AND d.numero_contenedores=25 FROM negocio.detalle_manifiesto d JOIN negocio.version_manifiesto v ON v.id=d.version_id
    JOIN negocio.tirada_manifiesto tm ON tm.manifiesto_id=v.manifiesto_id WHERE tm.tirada_id=pg_temp.uid('tir10')),'la cantidad es la capacidad de la unidad (1 m3), no 25 x 16 L; los 25 baños quedan en numero_contenedores');
SELECT pg_temp.afirmar((SELECT cap.valor_m3=1 FROM negocio.version_manifiesto v JOIN negocio.tirada_manifiesto tm ON tm.manifiesto_id=v.manifiesto_id
    JOIN negocio.ficha_vehiculo f ON f.id=v.ficha_vehiculo_id JOIN catalogo.capacidad_vehiculo cap ON cap.id=f.capacidad_id WHERE tm.tirada_id=pg_temp.uid('tir10')),
  'el manifiesto queda ligado a la unidad (y su capacidad) de la tirada');
COMMIT;

-- 17. (0029) sitio_baja: insert-only y con RLS por permiso y sucursal.
BEGIN;
INSERT INTO negocio.sitio VALUES (pg_temp.uid('o'),pg_temp.uid('s'),pg_temp.uid('sbaja'),'Sitio con baja');
INSERT INTO negocio.sitio VALUES (pg_temp.uid('o'),pg_temp.uid('s'),pg_temp.uid('sbaja2'),'Sitio con baja 2');
INSERT INTO seguridad.acceso_autorizado(organizacion_id,id,correo_permitido,activo,rol_codigo) VALUES (pg_temp.uid('o'),pg_temp.uid('escribe'),'escribe-t@example.invalid',true,'operaciones');
INSERT INTO seguridad.permiso_sucursal VALUES (pg_temp.uid('o'),pg_temp.uid('s'),pg_temp.uid('escribe'),'datos.leer'),(pg_temp.uid('o'),pg_temp.uid('s'),pg_temp.uid('escribe'),'datos.escribir');
INSERT INTO negocio.sitio_baja(organizacion_id,sucursal_id,sitio_id) VALUES (pg_temp.uid('o'),pg_temp.uid('s'),pg_temp.uid('sbaja'));
SELECT pg_temp.rechaza($q$UPDATE negocio.sitio_baja SET dada_de_baja_en=now()$q$,'23514','sitio_baja no se actualiza');
SELECT pg_temp.rechaza($q$DELETE FROM negocio.sitio_baja$q$,'23514','sitio_baja no se borra');
SELECT pg_temp.rechaza($q$INSERT INTO negocio.sitio_baja(organizacion_id,sucursal_id,sitio_id) VALUES (pg_temp.uid('o'),pg_temp.uid('s'),pg_temp.uid('sbaja'))$q$,'23505','un sitio tiene una sola baja');
COMMIT;
BEGIN;
SET LOCAL ROLE manifiestos_app;
SELECT set_config('app.organizacion_id',pg_temp.uid('o')::text,true), set_config('app.acceso_id',pg_temp.uid('lee')::text,true);
SELECT pg_temp.afirmar((SELECT count(*)=1 FROM negocio.sitio_baja),'quien solo lee ve la baja');
SELECT pg_temp.rechaza($q$INSERT INTO negocio.sitio_baja(organizacion_id,sucursal_id,sitio_id) VALUES (pg_temp.uid('o'),pg_temp.uid('s'),pg_temp.uid('sbaja2'))$q$,'42501','sin datos.escribir no se da de baja');
SELECT set_config('app.acceso_id',pg_temp.uid('otra')::text,true);
SELECT pg_temp.afirmar((SELECT count(*)=0 FROM negocio.sitio_baja),'otra sucursal no ve la baja');
SELECT pg_temp.rechaza($q$INSERT INTO negocio.sitio_baja(organizacion_id,sucursal_id,sitio_id) VALUES (pg_temp.uid('o'),pg_temp.uid('s'),pg_temp.uid('sbaja2'))$q$,'42501','otra sucursal no da de baja sitios ajenos');
SELECT set_config('app.acceso_id',pg_temp.uid('escribe')::text,true);
SELECT pg_temp.afirmar((SELECT count(*)=1 FROM negocio.sitio_baja),'quien escribe tambien la ve');
INSERT INTO negocio.sitio_baja(organizacion_id,sucursal_id,sitio_id) VALUES (pg_temp.uid('o'),pg_temp.uid('s'),pg_temp.uid('sbaja2'));
SELECT pg_temp.afirmar((SELECT count(*)=2 FROM negocio.sitio_baja),'con datos.escribir se registra la baja');
COMMIT;

-- 18b. (0032) Parada EXTRAORDINARIA en sanitarios: la succión se resuelve, suma a la carga y
-- sus clientes salen en clientes_parada_en. Sitio y fecha: hoy, ruta r3 (unidad vsan2).
BEGIN;
INSERT INTO negocio.parada_extraordinaria(organizacion_id,sucursal_id,id,ruta_id,fecha,sitio_id)
VALUES (pg_temp.uid('o'),pg_temp.uid('s'),pg_temp.uid('pxe'),pg_temp.uid('r3'),current_date,pg_temp.uid('c3'));
INSERT INTO negocio.parada_extraordinaria_cliente(organizacion_id,sucursal_id,parada_extraordinaria_id,cliente_id,banos_planeados)
VALUES (pg_temp.uid('o'),pg_temp.uid('s'),pg_temp.uid('pxe'),pg_temp.uid('c'),7);
CREATE TEMP TABLE antes_xe AS SELECT banos FROM pg_temp.carga('vsan2');
SELECT pg_temp.succion('xe1','j3','pxe',7,now()-interval '30 minutes');
SELECT pg_temp.afirmar((SELECT count(*)=1 FROM negocio.succion_parada WHERE captura_id=pg_temp.uid('xe1') AND parada_id=pg_temp.uid('pxe') AND vehiculo_id=pg_temp.uid('vsan2')),
  'la succión de una parada extraordinaria se resuelve');
SELECT pg_temp.afirmar((SELECT c.banos=a.banos+7 FROM pg_temp.carga('vsan2') c, antes_xe a),'la parada extraordinaria suma a la carga de la unidad');
SELECT pg_temp.afirmar((SELECT clientes_parada.cl->0->>'banos'='7' FROM (SELECT negocio.clientes_parada_en(pg_temp.uid('o'),pg_temp.uid('s'),pg_temp.uid('pxe'),now()) cl) clientes_parada),
  'clientes_parada_en devuelve los clientes de la extraordinaria');
SELECT pg_temp.rechaza($q$INSERT INTO negocio.incidencia(organizacion_id,sucursal_id,servicio,motivo,descripcion,ocurrida_en,ruta_id,parada_id,vehiculo_id,persona_id)
    VALUES (pg_temp.uid('o'),pg_temp.uid('s'),'sanitario','otro','x',now(),pg_temp.uid('r3'),pg_temp.uid('no-existe'),pg_temp.uid('vsan2'),pg_temp.uid('p1'))$q$,'23503','una parada inexistente sigue rechazándose');
COMMIT;

-- 18. (0031) Carga por tirar de fosas: unidad vacía = 0 órdenes con la capacidad de su ficha (18 m3); la función web exige datos.leer en la sucursal.
-- Las órdenes succionadas/descargadas se prueban de punta a punta en CapturasMovilTests (HTTP).
BEGIN;
SELECT pg_temp.afirmar((SELECT ordenes=0 AND m3=0 AND capacidad_m3=18.000 AND porcentaje=0 AND ultima_descarga_en IS NULL AND desglose='[]'::jsonb
    FROM negocio.carga_fosas_interna(pg_temp.uid('o'),pg_temp.uid('s'),pg_temp.uid('vfos'))),'unidad de fosas vacía: 0 órdenes, capacidad de la ficha');
SELECT pg_temp.afirmar((SELECT count(*)=0 FROM negocio.carga_fosas_interna(pg_temp.uid('o'),pg_temp.uid('s'),pg_temp.uid('inexistente'))),'unidad inexistente: sin filas');
SET LOCAL ROLE manifiestos_app;
SELECT set_config('app.organizacion_id',pg_temp.uid('o')::text,true), set_config('app.acceso_id',pg_temp.uid('lee')::text,true);
SELECT pg_temp.afirmar((SELECT count(*)=1 FROM seguridad.carga_fosas_web(pg_temp.uid('o'),pg_temp.uid('lee'),pg_temp.uid('s'),pg_temp.uid('vfos'))),'quien lee la sucursal ve la carga de fosas');
SELECT pg_temp.afirmar((SELECT count(*)=0 FROM seguridad.carga_fosas_web(pg_temp.uid('o'),pg_temp.uid('otra'),pg_temp.uid('s'),pg_temp.uid('vfos'))),'el acceso informado debe ser el del contexto');
SELECT set_config('app.acceso_id',pg_temp.uid('otra')::text,true);
SELECT pg_temp.afirmar((SELECT count(*)=0 FROM seguridad.carga_fosas_web(pg_temp.uid('o'),pg_temp.uid('otra'),pg_temp.uid('s'),pg_temp.uid('vfos'))),'sin datos.leer en la sucursal no hay carga de fosas');
SELECT pg_temp.rechaza($q$SELECT * FROM negocio.carga_fosas_interna(pg_temp.uid('o'),pg_temp.uid('s'),pg_temp.uid('vfos'))$q$,'42501','la función interna no se concede a la aplicación');
COMMIT;

-- 19. (0033) Una planta dada de baja deja de ofrecerse al teléfono (seguridad.plantas_operador).
BEGIN;
SELECT pg_temp.afirmar((SELECT seguridad.plantas_operador(pg_temp.uid('o'),pg_temp.uid('a'),pg_temp.uid('i')) @> jsonb_build_array(jsonb_build_object('id',pg_temp.uid('ptar'),'nombre','PTAR NORTE'))),'la planta vigente se ofrece al teléfono');
INSERT INTO negocio.sitio_baja(organizacion_id,sucursal_id,sitio_id) VALUES (pg_temp.uid('o'),pg_temp.uid('s'),pg_temp.uid('ptar'));
SELECT pg_temp.afirmar((SELECT NOT (seguridad.plantas_operador(pg_temp.uid('o'),pg_temp.uid('a'),pg_temp.uid('i')) @> jsonb_build_array(jsonb_build_object('id',pg_temp.uid('ptar'),'nombre','PTAR NORTE')))),'una planta dada de baja ya no se ofrece');
ROLLBACK;

-- 20. (0056) «Terminar parada» llega al servidor: registra solo cambios de estado, reabrir es otra revisión y una jornada ajena se ignora.
BEGIN;
INSERT INTO negocio.parada_version(organizacion_id,sucursal_id,id,parada_id,revision,sitio_id) VALUES (pg_temp.uid('o'),pg_temp.uid('s'),pg_temp.uid('pvc2'),pg_temp.uid('pc'),2,pg_temp.uid('c3'));
INSERT INTO negocio.parada_cliente(organizacion_id,sucursal_id,version_id,cliente_id,banos_planeados,fosas_planeadas) VALUES (pg_temp.uid('o'),pg_temp.uid('s'),pg_temp.uid('pvc2'),pg_temp.uid('c'),8,3);
CREATE FUNCTION pg_temp.terminar(j uuid, t boolean) RETURNS integer LANGUAGE sql AS $$
    SELECT seguridad.registrar_paradas_terminadas(pg_temp.uid('o'),pg_temp.uid('a'),pg_temp.uid('i'),
        jsonb_build_object('paradas',jsonb_build_array(jsonb_build_object('jornadaId',j,'paradaId',pg_temp.uid('pc'),'terminada',t)))) $$;
SELECT pg_temp.afirmar(pg_temp.terminar(pg_temp.uid('j2'),true)=1,'terminar una parada registra una revisión');
SELECT pg_temp.afirmar(pg_temp.terminar(pg_temp.uid('j2'),true)=0,'repetir el mismo estado no registra nada (idempotente)');
SELECT pg_temp.afirmar(pg_temp.terminar(pg_temp.uid('j2'),false)=1,'reabrir es otra revisión');
SELECT pg_temp.afirmar((SELECT count(*)=2 AND max(revision)=2 FROM negocio.parada_terminada_dia WHERE parada_id=pg_temp.uid('pc')),'el historial conserva las dos revisiones');
SELECT pg_temp.afirmar(pg_temp.terminar(gen_random_uuid(),true)=0,'una jornada que no es de la sucursal se ignora');
-- (0057) Se recuerda cuántas órdenes no se necesitaron: un cambio de ese número es otra revisión (si se agrega una orden y se vuelve a terminar).
CREATE FUNCTION pg_temp.terminar_k(j uuid, t boolean, k integer) RETURNS integer LANGUAGE sql AS $$
    SELECT seguridad.registrar_paradas_terminadas(pg_temp.uid('o'),pg_temp.uid('a'),pg_temp.uid('i'),
        jsonb_build_object('paradas',jsonb_build_array(jsonb_build_object('jornadaId',j,'paradaId',pg_temp.uid('pc'),'terminada',t,'noNecesarias',k)))) $$;
SELECT pg_temp.afirmar(pg_temp.terminar_k(pg_temp.uid('j2'),true,1)=1,'terminar con 1 orden no necesaria registra una revisión');
SELECT pg_temp.afirmar(pg_temp.terminar_k(pg_temp.uid('j2'),true,1)=0,'repetir lo mismo no registra nada');
SELECT pg_temp.afirmar(pg_temp.terminar_k(pg_temp.uid('j2'),true,2)=1,'terminar otra vez con 2 no necesarias es otra revisión');
SELECT pg_temp.afirmar((SELECT no_necesarias=2 FROM negocio.parada_terminada_dia WHERE parada_id=pg_temp.uid('pc') ORDER BY revision DESC LIMIT 1),'la última revisión guarda el número de órdenes no necesarias');
-- (0067, H17) Las identidades de las órdenes no necesarias: se guardan, se validan contra las órdenes de ESA parada y repetirlas es duplicado.
CREATE FUNCTION pg_temp.terminar_ids(ids uuid[]) RETURNS text LANGUAGE sql AS $$
    SELECT r->>'resultado' FROM jsonb_array_elements(seguridad.registrar_paradas_terminadas_detalle(pg_temp.uid('o'),pg_temp.uid('a'),pg_temp.uid('i'),
        jsonb_build_object('paradas',jsonb_build_array(jsonb_build_object('jornadaId',pg_temp.uid('j2'),'paradaId',pg_temp.uid('pc'),'terminada',true,'ordenesNoNecesarias',to_jsonb(ids)))))) r $$;
SELECT pg_temp.afirmar(pg_temp.terminar_ids(ARRAY[md5(pg_temp.uid('j2')::text||':'||pg_temp.uid('pc')::text||':'||pg_temp.uid('c')::text||':2')::uuid])='aceptada','terminar con la identidad de una orden de la parada se acepta');
SELECT pg_temp.afirmar((SELECT ordenes_no_necesarias=ARRAY[md5(pg_temp.uid('j2')::text||':'||pg_temp.uid('pc')::text||':'||pg_temp.uid('c')::text||':2')::uuid] AND no_necesarias=1 FROM negocio.parada_terminada_dia WHERE parada_id=pg_temp.uid('pc') ORDER BY revision DESC LIMIT 1),'se guardan las identidades y el conteo es su número');
SELECT pg_temp.afirmar(pg_temp.terminar_ids(ARRAY[md5(pg_temp.uid('j2')::text||':'||pg_temp.uid('pc')::text||':'||pg_temp.uid('c')::text||':2')::uuid])='duplicada','repetir las mismas identidades es duplicado');
SELECT pg_temp.afirmar(pg_temp.terminar_ids(ARRAY[gen_random_uuid()])='rechazada','una identidad que no es de la parada se rechaza');
-- (0060, H01) El alcance del cierre: parada de otra ruta, conteo mayor que las órdenes y persona que ya no opera la unidad se ignoran.
SELECT pg_temp.afirmar(seguridad.registrar_paradas_terminadas(pg_temp.uid('o'),pg_temp.uid('a'),pg_temp.uid('i'),
    jsonb_build_object('paradas',jsonb_build_array(jsonb_build_object('jornadaId',pg_temp.uid('j2'),'paradaId',pg_temp.uid('pa'),'terminada',true))))=0,'una parada de otra ruta no se cierra con esta jornada');
SELECT pg_temp.afirmar(pg_temp.terminar_k(pg_temp.uid('j2'),true,4)=0,'un conteo de no necesarias mayor que las órdenes de la parada se ignora');
INSERT INTO negocio.cambio_unidad_dia(organizacion_id,sucursal_id,ruta_id,fecha,revision,vehiculo_id,persona_id)
VALUES (pg_temp.uid('o'),pg_temp.uid('s'),pg_temp.uid('r2'),current_date,1,pg_temp.uid('vsan'),pg_temp.uid('p2'));
SELECT pg_temp.afirmar(pg_temp.terminar_k(pg_temp.uid('j2'),false,0)=0,'con el cambio del día a otra persona, el titular fijo ya no cierra la parada (H02)');
SELECT pg_temp.afirmar(NOT negocio.persona_opera_unidad(pg_temp.uid('o'),pg_temp.uid('s'),pg_temp.uid('vsan'),pg_temp.uid('p1'),clock_timestamp()),'el titular fijo no opera la unidad si el cambio del día la dio a otra persona');
SELECT pg_temp.afirmar(negocio.persona_opera_unidad(pg_temp.uid('o'),pg_temp.uid('s'),pg_temp.uid('vsan'),pg_temp.uid('p2'),clock_timestamp()),'la persona del cambio del día opera la unidad');
-- (0065, H15) El resultado es POR PARADA: una de otra ruta se rechaza y una ya registrada es duplicada.
SELECT pg_temp.afirmar((SELECT r->>'resultado' FROM jsonb_array_elements(seguridad.registrar_paradas_terminadas_detalle(pg_temp.uid('o'),pg_temp.uid('a'),pg_temp.uid('i'),
    jsonb_build_object('paradas',jsonb_build_array(jsonb_build_object('jornadaId',pg_temp.uid('j2'),'paradaId',pg_temp.uid('pa'),'terminada',true))))) r)='rechazada','la parada de otra ruta se reporta rechazada');
SELECT pg_temp.rechaza($q$UPDATE negocio.parada_terminada_dia SET terminada=true$q$,'23514','parada_terminada_dia inmutable');
ROLLBACK;

-- 21. (0066) «Por corregir»: un servicio cuya foto está junto a OTRA parada de la ruta se sugiere mover; mover exige permiso de escritura y deja constancia.
BEGIN;
INSERT INTO negocio.ficha_sitio(organizacion_id,sucursal_id,sitio_id,revision,nombre,latitud,longitud) VALUES
 (pg_temp.uid('o'),pg_temp.uid('s'),pg_temp.uid('a'),1,'Sitio A',25.6702,-100.3000),   -- a ~913 m de la foto del helper
 (pg_temp.uid('o'),pg_temp.uid('s'),pg_temp.uid('b'),1,'Sitio B',25.6702,-100.3095);   -- b a ~40 m de la foto
SELECT pg_temp.succion('cx1','j1','pa',5,now());
INSERT INTO seguridad.permiso_sucursal VALUES (pg_temp.uid('o'),pg_temp.uid('s'),pg_temp.uid('lee'),'datos.escribir');
SET LOCAL ROLE manifiestos_app;
SELECT set_config('app.organizacion_id',pg_temp.uid('o')::text,true), set_config('app.acceso_id',pg_temp.uid('lee')::text,true);
CREATE TEMP TABLE pc AS SELECT e FROM jsonb_array_elements(seguridad.por_corregir_web(pg_temp.uid('o'),pg_temp.uid('lee'))) e WHERE e->>'capturaId'=pg_temp.uid('cx1')::text;
SELECT pg_temp.afirmar((SELECT e->>'estado'='pendiente' AND e->>'sugeridaId'=pg_temp.uid('pb')::text AND e->>'registradaId'=pg_temp.uid('pa')::text FROM pc),'servicio junto a otra parada: pendiente con sugerencia');
SELECT pg_temp.afirmar(seguridad.corregir_captura_web(pg_temp.uid('o'),pg_temp.uid('lee'),pg_temp.uid('cx1'),'mover',pg_temp.uid('pd'))='destino_invalido','una parada de otra ruta no es destino válido');
SELECT pg_temp.afirmar(seguridad.corregir_captura_web(pg_temp.uid('o'),pg_temp.uid('lee'),pg_temp.uid('cx1'),'mover',pg_temp.uid('pb'))='ok','mover a la parada correcta');
SELECT pg_temp.afirmar((SELECT parada_id=pg_temp.uid('pb') FROM negocio.succion_parada WHERE captura_id=pg_temp.uid('cx1')),'la succión queda en la parada correcta');
SELECT pg_temp.afirmar((SELECT parada_origen_id=pg_temp.uid('pa') AND parada_destino_id=pg_temp.uid('pb') AND accion='mover' FROM negocio.correccion_captura WHERE captura_id=pg_temp.uid('cx1')),'queda constancia de la parada original');
SELECT pg_temp.afirmar(seguridad.corregir_captura_web(pg_temp.uid('o'),pg_temp.uid('lee'),pg_temp.uid('cx1'),'mover',pg_temp.uid('pa'))='ya_resuelto','una decisión no se repite');
SELECT pg_temp.afirmar((SELECT count(*)=1 FROM jsonb_array_elements(seguridad.por_corregir_web(pg_temp.uid('o'),pg_temp.uid('lee'))) e WHERE e->>'capturaId'=pg_temp.uid('cx1')::text AND e->>'estado'='resuelta'),'queda en resueltas');
SELECT set_config('app.acceso_id',pg_temp.uid('otra')::text,true);
SELECT pg_temp.afirmar(seguridad.corregir_captura_web(pg_temp.uid('o'),pg_temp.uid('otra'),pg_temp.uid('cx1'),'dejar',NULL) IN ('sin_permiso','no_encontrada'),'sin permiso sobre la sucursal no corrige');
ROLLBACK;

-- 22. (0068, H08) «No se pudo ubicar»: un servicio con una parada que no es de la ruta queda por revisar; se asigna a una parada de la ruta o se marca como corregido.
BEGIN;
SELECT pg_temp.succion('cs1','j1','pd',7,now());   -- 'pd' es de otra ruta: parada_no_resuelta
SELECT pg_temp.succion('cs2','j1','pd',3,now());
SELECT pg_temp.afirmar((SELECT count(*)=2 FROM negocio.captura_por_revisar WHERE motivo='parada_no_resuelta' AND captura_id IN (pg_temp.uid('cs1'),pg_temp.uid('cs2'))),'las dos succiones quedaron por revisar');
INSERT INTO seguridad.permiso_sucursal VALUES (pg_temp.uid('o'),pg_temp.uid('s'),pg_temp.uid('lee'),'datos.escribir');
SET LOCAL ROLE manifiestos_app;
SELECT set_config('app.organizacion_id',pg_temp.uid('o')::text,true), set_config('app.acceso_id',pg_temp.uid('lee')::text,true);
CREATE TEMP TABLE su AS SELECT e FROM jsonb_array_elements(seguridad.sin_ubicar_web(pg_temp.uid('o'),pg_temp.uid('lee'))) e WHERE e->>'capturaId'=pg_temp.uid('cs1')::text;
SELECT pg_temp.afirmar((SELECT e->>'estado'='pendiente' AND (e->>'puedeAsignar')::boolean AND jsonb_array_length(e->'paradas')=2 FROM su),'aparece pendiente, asignable y con las paradas de su ruta');
SELECT pg_temp.afirmar(seguridad.resolver_sin_ubicar_web(pg_temp.uid('o'),pg_temp.uid('lee'),pg_temp.uid('cs1'),'asignar',NULL,pg_temp.uid('pd'))='destino_invalido','una parada de otra ruta no es destino');
SELECT pg_temp.afirmar(seguridad.resolver_sin_ubicar_web(pg_temp.uid('o'),pg_temp.uid('lee'),pg_temp.uid('cs1'),'asignar',NULL,pg_temp.uid('pb'))='ok','asignar a una parada de la ruta');
SELECT pg_temp.afirmar((SELECT parada_id=pg_temp.uid('pb') AND banos_succionados=7 FROM negocio.succion_parada WHERE captura_id=pg_temp.uid('cs1')),'la succión se registra en esa parada con sus baños originales');
SELECT pg_temp.afirmar(seguridad.resolver_sin_ubicar_web(pg_temp.uid('o'),pg_temp.uid('lee'),pg_temp.uid('cs1'),'corregido',NULL,NULL)='ya_resuelto','una decisión no se repite');
SELECT pg_temp.afirmar(seguridad.resolver_sin_ubicar_web(pg_temp.uid('o'),pg_temp.uid('lee'),pg_temp.uid('cs2'),'corregido',NULL,NULL)='ok','marcar como corregido');
SELECT pg_temp.afirmar((SELECT count(*)=0 FROM negocio.succion_parada WHERE captura_id=pg_temp.uid('cs2')),'corregido sin asignar no aplica nada');
SELECT pg_temp.afirmar((SELECT count(*)=2 FROM jsonb_array_elements(seguridad.sin_ubicar_web(pg_temp.uid('o'),pg_temp.uid('lee'))) e WHERE e->>'estado'='resuelta' AND e->>'capturaId' IN (pg_temp.uid('cs1')::text,pg_temp.uid('cs2')::text)),'las dos quedan en resueltas');
SELECT set_config('app.acceso_id',pg_temp.uid('otra')::text,true);
SELECT pg_temp.afirmar(seguridad.resolver_sin_ubicar_web(pg_temp.uid('o'),pg_temp.uid('otra'),pg_temp.uid('cs1'),'corregido',NULL,NULL) IN ('sin_permiso','no_encontrada','ya_resuelto'),'sin permiso no decide');
ROLLBACK;

SELECT count(*) AS comprobaciones_tirada FROM comprobacion_tirada;
\echo 'Comprobaciones de captura de baños y tirada terminadas sin error.'
