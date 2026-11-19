-- Base de sanitarios (11-nov-2026, decisión docs/decisiones/0023 y diseño docs/diseno/sanitarios-y-tirada-2026-11-11.md, paso B1).
-- Qué contiene: capacidad de 1 m3 para unidades de sanitarios, constante de baños, parada sin clientes SOLO en
-- rutas de sanitarios, baja de paradas, un sitio por ruta, firma precargada del generador (PROVISIONAL), validación
-- clasificación/capacidad de las unidades y carga actual de la unidad (derivada).
-- Qué NO contiene: captura de succiones, tirada, manifiestos de sanitarios ni funciones de la app (paso B2, migración 0028+).
-- No siembra datos de negocio: ni plantas ni firmas. Las plantas se dan de alta como sitios que no son punto de servicio.

-- a) Capacidad de las unidades de sanitarios: 1 m3 (confirmado 11-nov-2026). ON CONFLICT: el catálogo es inmutable
-- para UPDATE/DELETE, pero el alta es repetible.
INSERT INTO catalogo.capacidad_vehiculo (id, valor_m3) VALUES (4, 1.000) ON CONFLICT (id) DO NOTHING;

-- b) Constante única de baños: 16 L estimados por baño (estimación, no medición). Todo volumen visible se expresa en m3.
CREATE OR REPLACE FUNCTION negocio.litros_por_bano() RETURNS integer LANGUAGE sql IMMUTABLE AS $$ SELECT 16 $$;

-- Servicio de una ruta = clasificación de la unidad de su última revisión ('sanitarios' | 'fosas' | NULL sin clasificar).
CREATE FUNCTION negocio.servicio_ruta(o uuid, s uuid, p_ruta uuid) RETURNS text
LANGUAGE sql STABLE SECURITY DEFINER SET search_path = pg_catalog AS $$
    SELECT f.clasificacion
    FROM negocio.ruta_version v
    LEFT JOIN LATERAL (SELECT x.clasificacion FROM negocio.ficha_vehiculo x
        WHERE (x.organizacion_id, x.sucursal_id, x.vehiculo_id) = (v.organizacion_id, v.sucursal_id, v.vehiculo_id)
        ORDER BY x.revision DESC LIMIT 1) f ON true
    WHERE (v.organizacion_id, v.sucursal_id, v.ruta_id) = (o, s, p_ruta)
    ORDER BY v.revision DESC LIMIT 1
$$;
REVOKE ALL ON FUNCTION negocio.servicio_ruta(uuid, uuid, uuid) FROM PUBLIC;

-- c) Parada sin clientes SOLO en rutas de sanitarios (se arma sitio por sitio y los clientes se agregan uno por uno).
-- En fosas se conserva la protección: toda revisión de parada exige al menos un cliente. Se mantiene "toda parada tiene
-- revisión" y "los clientes se insertan en la transacción que crea la revisión".
CREATE OR REPLACE FUNCTION infra.exigir_contenido() RETURNS trigger LANGUAGE plpgsql
SECURITY DEFINER SET search_path = pg_catalog AS $$
DECLARE ruta uuid;
BEGIN
    IF TG_TABLE_NAME = 'ruta' THEN
        IF NOT EXISTS (SELECT 1 FROM negocio.ruta_version v
            WHERE (v.organizacion_id, v.sucursal_id, v.ruta_id) = (NEW.organizacion_id, NEW.sucursal_id, NEW.id)) THEN
            RAISE EXCEPTION 'Ruta sin datos: falta su primera revisión' USING ERRCODE = '23514';
        END IF;
    ELSIF TG_TABLE_NAME = 'parada' THEN
        IF NOT EXISTS (SELECT 1 FROM negocio.parada_version v
            WHERE (v.organizacion_id, v.sucursal_id, v.parada_id) = (NEW.organizacion_id, NEW.sucursal_id, NEW.id)) THEN
            RAISE EXCEPTION 'Parada sin datos: falta su primera revisión' USING ERRCODE = '23514';
        END IF;
    ELSIF TG_TABLE_NAME = 'parada_version' THEN
        IF NOT EXISTS (SELECT 1 FROM negocio.parada_cliente c
            WHERE (c.organizacion_id, c.sucursal_id, c.version_id) = (NEW.organizacion_id, NEW.sucursal_id, NEW.id)) THEN
            SELECT p.ruta_id INTO ruta FROM negocio.parada p
            WHERE (p.organizacion_id, p.sucursal_id, p.id) = (NEW.organizacion_id, NEW.sucursal_id, NEW.parada_id);
            IF negocio.servicio_ruta(NEW.organizacion_id, NEW.sucursal_id, ruta) IS DISTINCT FROM 'sanitarios' THEN
                RAISE EXCEPTION 'Parada sin clientes' USING ERRCODE = '23514';
            END IF;
        END IF;
    END IF;
    RETURN NULL;
END $$;

-- d) Baja de paradas. Una parada nunca se borra; darla de baja inserta un registro (insert-only, con quién y cuándo).
-- Los listados de paradas excluyen las que tienen baja. Las reglas de cuándo se permite viven en el servidor.
CREATE TABLE negocio.parada_baja (
    organizacion_id uuid NOT NULL, sucursal_id uuid NOT NULL,
    parada_id uuid NOT NULL,
    dada_de_baja_en timestamptz NOT NULL DEFAULT clock_timestamp(),
    acceso_id uuid DEFAULT seguridad.contexto_acceso(),
    motivo text CHECK (motivo IS NULL OR btrim(motivo) <> ''),
    PRIMARY KEY (organizacion_id, sucursal_id, parada_id),
    FOREIGN KEY (organizacion_id, sucursal_id, parada_id) REFERENCES negocio.parada ON DELETE RESTRICT,
    FOREIGN KEY (organizacion_id, acceso_id) REFERENCES seguridad.acceso_autorizado ON DELETE RESTRICT
);
CREATE INDEX parada_baja_acceso ON negocio.parada_baja (organizacion_id, acceso_id);
CREATE TRIGGER conservar_historia BEFORE UPDATE OR DELETE ON negocio.parada_baja
FOR EACH ROW EXECUTE FUNCTION infra.inmutable();
ALTER TABLE negocio.parada_baja ENABLE ROW LEVEL SECURITY;
CREATE POLICY contexto ON negocio.parada_baja TO manifiestos_app
USING (seguridad.puede(organizacion_id, sucursal_id, 'datos.leer'))
WITH CHECK (seguridad.puede(organizacion_id, sucursal_id, 'datos.escribir'));
GRANT SELECT, INSERT ON negocio.parada_baja TO manifiestos_app;

-- e) Un sitio tiene a lo más una parada vigente (sin baja) por ruta. Se comprueba al confirmar y solo sobre la última
-- revisión de cada parada; los datos anteriores a esta migración no se tocan.
CREATE FUNCTION infra.sitio_unico_por_ruta() RETURNS trigger LANGUAGE plpgsql
SECURITY DEFINER SET search_path = pg_catalog AS $$
DECLARE ruta uuid;
BEGIN
    SELECT p.ruta_id INTO ruta FROM negocio.parada p
    WHERE (p.organizacion_id, p.sucursal_id, p.id) = (NEW.organizacion_id, NEW.sucursal_id, NEW.parada_id);
    IF EXISTS (SELECT 1 FROM negocio.parada_version y
               WHERE (y.organizacion_id, y.sucursal_id, y.parada_id) = (NEW.organizacion_id, NEW.sucursal_id, NEW.parada_id)
                 AND y.revision > NEW.revision)
       OR EXISTS (SELECT 1 FROM negocio.parada_baja b
                  WHERE (b.organizacion_id, b.sucursal_id, b.parada_id) = (NEW.organizacion_id, NEW.sucursal_id, NEW.parada_id)) THEN
        RETURN NULL; -- revisión histórica o parada dada de baja
    END IF;
    IF EXISTS (
        SELECT 1 FROM negocio.parada p2
        JOIN LATERAL (SELECT y.sitio_id FROM negocio.parada_version y
            WHERE (y.organizacion_id, y.sucursal_id, y.parada_id) = (p2.organizacion_id, p2.sucursal_id, p2.id)
            ORDER BY y.revision DESC LIMIT 1) v2 ON true
        WHERE (p2.organizacion_id, p2.sucursal_id, p2.ruta_id) = (NEW.organizacion_id, NEW.sucursal_id, ruta)
          AND p2.id <> NEW.parada_id AND v2.sitio_id = NEW.sitio_id
          AND NOT EXISTS (SELECT 1 FROM negocio.parada_baja b2
                          WHERE (b2.organizacion_id, b2.sucursal_id, b2.parada_id) = (p2.organizacion_id, p2.sucursal_id, p2.id))) THEN
        RAISE EXCEPTION 'El sitio ya tiene una parada vigente en esta ruta' USING ERRCODE = '23505';
    END IF;
    RETURN NULL;
END $$;
REVOKE ALL ON FUNCTION infra.sitio_unico_por_ruta() FROM PUBLIC;
CREATE CONSTRAINT TRIGGER sitio_unico_por_ruta AFTER INSERT ON negocio.parada_version
DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION infra.sitio_unico_por_ruta();

-- f) Firma del generador PRECARGADA (PROVISIONAL). Es configuración de la empresa: nunca la firma del operador ni la
-- firma_generador de la captura (0002). archivo_id NULL = marcador provisional (el manifiesto lo mostrará como una raya);
-- la firma real la entregará el usuario fuera del repositorio. Con revisiones; no se actualiza ni se borra.
CREATE TABLE negocio.firma_generador_precargada (
    organizacion_id uuid NOT NULL, sucursal_id uuid NOT NULL,
    id uuid NOT NULL DEFAULT gen_random_uuid(),
    empresa_id uuid NOT NULL,
    revision integer NOT NULL CHECK (revision > 0),
    nombre_firmante text NOT NULL CHECK (btrim(nombre_firmante) <> ''),
    cargo text CHECK (cargo IS NULL OR btrim(cargo) <> ''),
    archivo_id uuid,
    es_provisional boolean NOT NULL DEFAULT true,
    vigente_desde timestamptz NOT NULL DEFAULT clock_timestamp(),
    registrada_por_acceso_id uuid DEFAULT seguridad.contexto_acceso(),
    PRIMARY KEY (organizacion_id, sucursal_id, id),
    UNIQUE (organizacion_id, sucursal_id, empresa_id, revision),
    CHECK (es_provisional OR archivo_id IS NOT NULL),
    FOREIGN KEY (organizacion_id, sucursal_id) REFERENCES negocio.sucursal ON DELETE RESTRICT,
    FOREIGN KEY (organizacion_id, empresa_id) REFERENCES negocio.empresa ON DELETE RESTRICT,
    FOREIGN KEY (organizacion_id, sucursal_id, archivo_id) REFERENCES negocio.archivo ON DELETE RESTRICT,
    FOREIGN KEY (organizacion_id, registrada_por_acceso_id) REFERENCES seguridad.acceso_autorizado ON DELETE RESTRICT
);
CREATE INDEX firma_generador_precargada_empresa ON negocio.firma_generador_precargada (organizacion_id, empresa_id);
CREATE INDEX firma_generador_precargada_archivo ON negocio.firma_generador_precargada (organizacion_id, sucursal_id, archivo_id);
CREATE INDEX firma_generador_precargada_registrada_por ON negocio.firma_generador_precargada (organizacion_id, registrada_por_acceso_id);
CREATE TRIGGER conservar_historia BEFORE UPDATE OR DELETE ON negocio.firma_generador_precargada
FOR EACH ROW EXECUTE FUNCTION infra.inmutable();
ALTER TABLE negocio.firma_generador_precargada ENABLE ROW LEVEL SECURITY;
CREATE POLICY contexto ON negocio.firma_generador_precargada TO manifiestos_app
USING (seguridad.puede(organizacion_id, sucursal_id, 'datos.leer'));
GRANT SELECT ON negocio.firma_generador_precargada TO manifiestos_app;

-- Validación clasificación/capacidad de la ficha de unidad (servidor es la fuente de verdad):
-- 'sanitarios' <=> capacidad id 4 (1 m3); 'fosas' => capacidades 1, 2 o 3 (10/15/18 m3); sin clasificar no admite 1 m3.
-- Solo fichas nuevas: la historia ya guardada no se reescribe.
CREATE FUNCTION infra.validar_ficha_vehiculo() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF NEW.clasificacion = 'sanitarios' AND NEW.capacidad_id <> 4 THEN
        RAISE EXCEPTION 'Una unidad de sanitarios es de 1 m3' USING ERRCODE = '23514';
    ELSIF NEW.clasificacion = 'fosas' AND NEW.capacidad_id NOT IN (1, 2, 3) THEN
        RAISE EXCEPTION 'Una unidad de fosas es de 10, 15 o 18 m3' USING ERRCODE = '23514';
    ELSIF NEW.capacidad_id = 4 AND NEW.clasificacion IS DISTINCT FROM 'sanitarios' THEN
        RAISE EXCEPTION 'La capacidad de 1 m3 es de unidades de sanitarios' USING ERRCODE = '23514';
    END IF;
    RETURN NEW;
END $$;
REVOKE ALL ON FUNCTION infra.validar_ficha_vehiculo() FROM PUBLIC;
CREATE TRIGGER validar_clasificacion BEFORE INSERT ON negocio.ficha_vehiculo
FOR EACH ROW EXECUTE FUNCTION infra.validar_ficha_vehiculo();

-- Carga actual de la UNIDAD (no de la ruta ni de la jornada). Derivada, no guardada: dos rutas de la misma unidad ven
-- lo mismo. m3 = baños * litros_por_bano / 1000 (estimación, nunca medición); porcentaje sin tope (puede pasar de 100).
-- TEMPORAL: mientras no exista la captura de succiones (paso B2, migración 0028+) devuelve 0 baños de forma tolerante.
-- La ampliación de B2 reemplazará esta función con CREATE OR REPLACE en una migración nueva; no se edita esta.
CREATE FUNCTION negocio.carga_unidad_interna(o uuid, s uuid, p_vehiculo uuid)
RETURNS TABLE (banos integer, m3 numeric, capacidad_m3 numeric, porcentaje numeric, ultima_tirada_en timestamptz, desglose jsonb)
LANGUAGE sql STABLE SECURITY DEFINER SET search_path = pg_catalog AS $$
    WITH v AS (
        SELECT cap.valor_m3
        FROM negocio.vehiculo veh
        LEFT JOIN LATERAL (SELECT x.capacidad_id FROM negocio.ficha_vehiculo x
            WHERE (x.organizacion_id, x.sucursal_id, x.vehiculo_id) = (veh.organizacion_id, veh.sucursal_id, veh.id)
            ORDER BY x.revision DESC LIMIT 1) f ON true
        LEFT JOIN catalogo.capacidad_vehiculo cap ON cap.id = f.capacidad_id
        WHERE (veh.organizacion_id, veh.sucursal_id, veh.id) = (o, s, p_vehiculo)
    ), c AS (
        SELECT 0::integer AS banos, NULL::timestamptz AS ultima, '[]'::jsonb AS desglose
    )
    SELECT c.banos,
           round(c.banos * negocio.litros_por_bano() / 1000.0, 3),
           v.valor_m3,
           CASE WHEN v.valor_m3 > 0 THEN round(c.banos * negocio.litros_por_bano() / 1000.0 / v.valor_m3 * 100, 1) END,
           c.ultima, c.desglose
    FROM v CROSS JOIN c
$$;
REVOKE ALL ON FUNCTION negocio.carga_unidad_interna(uuid, uuid, uuid) FROM PUBLIC;

-- Carga de la unidad para el panel: exige datos.leer en la sucursal y que el acceso informado sea el del contexto.
CREATE FUNCTION seguridad.carga_unidad_web(o uuid, a uuid, s uuid, p_vehiculo uuid)
RETURNS TABLE (banos integer, m3 numeric, capacidad_m3 numeric, porcentaje numeric, ultima_tirada_en timestamptz, desglose jsonb)
LANGUAGE sql STABLE SECURITY DEFINER SET search_path = pg_catalog AS $$
    SELECT c.* FROM negocio.carga_unidad_interna(o, s, p_vehiculo) c
    WHERE a = seguridad.contexto_acceso() AND coalesce(seguridad.puede(o, s, 'datos.leer'), false)
$$;
REVOKE ALL ON FUNCTION seguridad.carga_unidad_web(uuid, uuid, uuid, uuid) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION seguridad.carga_unidad_web(uuid, uuid, uuid, uuid) TO manifiestos_app;

-- Las paradas dadas de baja dejan de verse en el trabajo del operador y en la resolución de órdenes derivadas.
-- Se redefinen las funciones de 0023 y 0025 solo con la condición adicional NOT EXISTS (parada_baja).

CREATE OR REPLACE FUNCTION seguridad.trabajo_operador(o uuid, a uuid, i uuid, f date) RETURNS jsonb
LANGUAGE plpgsql STABLE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE c record; r jsonb;
BEGIN
    IF f IS NULL OR f < ((now() AT TIME ZONE 'UTC')::date - 1) OR f > ((now() AT TIME ZONE 'UTC')::date + 1) THEN
        RAISE EXCEPTION 'Fecha fuera del rango permitido' USING ERRCODE='22023';
    END IF;
    SELECT * INTO c FROM seguridad.contexto_operador(o,a,i);
    IF NOT FOUND THEN RETURN NULL; END IF;

    WITH clientes AS (
        -- Paradas del plan: clientes de la última revisión de cada parada.
        SELECT pa.id AS parada_id, 'plan'::text AS tipo, pc.cliente_id,
               coalesce(nullif(btrim(fe.nombre_comercial),''), nullif(btrim(fe.nombre_fiscal),''), nullif(btrim(cl.codigo_comercial),''), 'Cliente sin nombre') AS nombre,
               EXISTS (SELECT 1 FROM negocio.autorizacion_empresa au WHERE (au.organizacion_id,au.empresa_id)=(cl.organizacion_id,cl.empresa_id)
                       AND au.autoridad='Medio ambiente' AND au.finalidad='Generador') AS autorizado,
               coalesce(pc.banos_planeados,0) AS banos, coalesce(pc.fosas_planeadas,0) AS fosas
        FROM negocio.parada pa
        JOIN LATERAL (SELECT x.id FROM negocio.parada_version x
            WHERE (x.organizacion_id,x.sucursal_id,x.parada_id)=(pa.organizacion_id,pa.sucursal_id,pa.id)
            ORDER BY x.revision DESC LIMIT 1) pv ON true
        JOIN negocio.parada_cliente pc ON (pc.organizacion_id,pc.sucursal_id,pc.version_id)=(pa.organizacion_id,pa.sucursal_id,pv.id)
        JOIN negocio.cliente cl ON (cl.organizacion_id,cl.sucursal_id,cl.id)=(pa.organizacion_id,pa.sucursal_id,pc.cliente_id)
        LEFT JOIN LATERAL (SELECT x.nombre_fiscal,x.nombre_comercial FROM negocio.ficha_empresa x
            WHERE (x.organizacion_id,x.empresa_id)=(cl.organizacion_id,cl.empresa_id) ORDER BY x.revision DESC LIMIT 1) fe ON true
        WHERE pa.organizacion_id=o AND pa.sucursal_id=c.sucursal_id AND NOT EXISTS (SELECT 1 FROM negocio.parada_baja pb WHERE (pb.organizacion_id,pb.sucursal_id,pb.parada_id)=(pa.organizacion_id,pa.sucursal_id,pa.id))
        UNION ALL
        -- Paradas extraordinarias de esa fecha.
        SELECT pe.id, 'extraordinaria', pc.cliente_id,
               coalesce(nullif(btrim(fe.nombre_comercial),''), nullif(btrim(fe.nombre_fiscal),''), nullif(btrim(cl.codigo_comercial),''), 'Cliente sin nombre'),
               EXISTS (SELECT 1 FROM negocio.autorizacion_empresa au WHERE (au.organizacion_id,au.empresa_id)=(cl.organizacion_id,cl.empresa_id)
                       AND au.autoridad='Medio ambiente' AND au.finalidad='Generador'),
               coalesce(pc.banos_planeados,0), coalesce(pc.fosas_planeadas,0)
        FROM negocio.parada_extraordinaria pe
        JOIN negocio.parada_extraordinaria_cliente pc ON (pc.organizacion_id,pc.sucursal_id,pc.parada_extraordinaria_id)=(pe.organizacion_id,pe.sucursal_id,pe.id)
        JOIN negocio.cliente cl ON (cl.organizacion_id,cl.sucursal_id,cl.id)=(pe.organizacion_id,pe.sucursal_id,pc.cliente_id)
        LEFT JOIN LATERAL (SELECT x.nombre_fiscal,x.nombre_comercial FROM negocio.ficha_empresa x
            WHERE (x.organizacion_id,x.empresa_id)=(cl.organizacion_id,cl.empresa_id) ORDER BY x.revision DESC LIMIT 1) fe ON true
        WHERE pe.organizacion_id=o AND pe.sucursal_id=c.sucursal_id AND pe.fecha=f
    ),
    jornadas AS (
        SELECT jo.id AS jornada_id, ru.id AS ruta_id, rv.nombre, ej.estado, ej.revision,
               veh.id AS vehiculo_id, coalesce(fv.numero_economico,veh.numero_economico) AS economico, cap.valor_m3
        FROM negocio.jornada jo
        JOIN LATERAL (SELECT x.estado,x.revision FROM negocio.estado_jornada x
            WHERE (x.organizacion_id,x.sucursal_id,x.jornada_id)=(jo.organizacion_id,jo.sucursal_id,jo.id)
            ORDER BY x.revision DESC LIMIT 1) ej ON ej.estado IN ('programada','en_curso')
        JOIN negocio.ruta ru ON (ru.organizacion_id,ru.sucursal_id,ru.id)=(jo.organizacion_id,jo.sucursal_id,jo.ruta_id)
        JOIN LATERAL (SELECT x.nombre,x.vehiculo_id FROM negocio.ruta_version x
            WHERE (x.organizacion_id,x.sucursal_id,x.ruta_id)=(ru.organizacion_id,ru.sucursal_id,ru.id)
            ORDER BY x.revision DESC LIMIT 1) rv ON true
        JOIN negocio.vehiculo veh ON (veh.organizacion_id,veh.sucursal_id,veh.id)=(ru.organizacion_id,ru.sucursal_id,rv.vehiculo_id)
        LEFT JOIN LATERAL (SELECT x.* FROM negocio.ficha_vehiculo x
            WHERE (x.organizacion_id,x.sucursal_id,x.vehiculo_id)=(veh.organizacion_id,veh.sucursal_id,veh.id)
            ORDER BY x.revision DESC LIMIT 1) fv ON true
        LEFT JOIN catalogo.capacidad_vehiculo cap ON cap.id=fv.capacidad_id
        JOIN LATERAL (SELECT x.persona_id FROM negocio.asignacion_unidad x
            WHERE (x.organizacion_id,x.sucursal_id,x.vehiculo_id)=(veh.organizacion_id,veh.sucursal_id,veh.id)
            ORDER BY x.revision DESC LIMIT 1) op ON op.persona_id=c.persona_id
        WHERE jo.organizacion_id=o AND jo.sucursal_id=c.sucursal_id AND jo.fecha=f
    ),
    paradas AS (
        SELECT jo.jornada_id, pa.id AS parada_id, 'plan'::text AS tipo,
               coalesce(nullif(btrim(fs.nombre),''), s.referencia) AS sitio,
               nullif(concat_ws(', ', nullif(btrim(concat_ws(' ', fs.calle, fs.numero_exterior)),''), fs.colonia, fs.municipio),'') AS direccion,
               fs.latitud, fs.longitud
        FROM jornadas jo
        JOIN negocio.parada pa ON (pa.organizacion_id,pa.sucursal_id,pa.ruta_id)=(o,c.sucursal_id,jo.ruta_id) AND NOT EXISTS (SELECT 1 FROM negocio.parada_baja pb WHERE (pb.organizacion_id,pb.sucursal_id,pb.parada_id)=(pa.organizacion_id,pa.sucursal_id,pa.id))
        JOIN LATERAL (SELECT x.sitio_id FROM negocio.parada_version x
            WHERE (x.organizacion_id,x.sucursal_id,x.parada_id)=(pa.organizacion_id,pa.sucursal_id,pa.id)
            ORDER BY x.revision DESC LIMIT 1) pv ON true
        JOIN negocio.sitio s ON (s.organizacion_id,s.sucursal_id,s.id)=(pa.organizacion_id,pa.sucursal_id,pv.sitio_id)
        LEFT JOIN LATERAL (SELECT x.* FROM negocio.ficha_sitio x
            WHERE (x.organizacion_id,x.sucursal_id,x.sitio_id)=(s.organizacion_id,s.sucursal_id,s.id)
            ORDER BY x.revision DESC LIMIT 1) fs ON true
        UNION ALL
        SELECT jo.jornada_id, pe.id, 'extraordinaria',
               coalesce(nullif(btrim(fs.nombre),''), s.referencia),
               nullif(concat_ws(', ', nullif(btrim(concat_ws(' ', fs.calle, fs.numero_exterior)),''), fs.colonia, fs.municipio),''),
               fs.latitud, fs.longitud
        FROM jornadas jo
        JOIN negocio.parada_extraordinaria pe ON (pe.organizacion_id,pe.sucursal_id,pe.ruta_id)=(o,c.sucursal_id,jo.ruta_id) AND pe.fecha=f
        JOIN negocio.sitio s ON (s.organizacion_id,s.sucursal_id,s.id)=(pe.organizacion_id,pe.sucursal_id,pe.sitio_id)
        LEFT JOIN LATERAL (SELECT x.* FROM negocio.ficha_sitio x
            WHERE (x.organizacion_id,x.sucursal_id,x.sitio_id)=(s.organizacion_id,s.sucursal_id,s.id)
            ORDER BY x.revision DESC LIMIT 1) fs ON true
    )
    SELECT jsonb_build_object('fecha', f, 'jornadas', coalesce(jsonb_agg(j.dato ORDER BY j.nombre, j.jornada_id), '[]'::jsonb))
    INTO r
    FROM (
        SELECT jo.jornada_id, jo.nombre, jsonb_build_object(
            'jornadaId', jo.jornada_id, 'rutaId', jo.ruta_id, 'rutaNombre', jo.nombre,
            'estado', jo.estado, 'revision', jo.revision,
            'unidad', jsonb_build_object('id', jo.vehiculo_id, 'numeroEconomico', jo.economico, 'capacidadM3', jo.valor_m3),
            'paradas', coalesce((
                SELECT jsonb_agg(jsonb_build_object(
                    'paradaId', p.parada_id, 'tipo', p.tipo, 'sitio', p.sitio, 'direccion', p.direccion,
                    'latitud', p.latitud, 'longitud', p.longitud,
                    'clientes', coalesce((
                        SELECT jsonb_agg(jsonb_build_object(
                            'clienteId', k.cliente_id, 'nombre', k.nombre, 'autorizado', k.autorizado,
                            -- Cliente general: fosa nuestra y generador nuestra empresa (sin firma del cliente).
                            'fosaPropia', NOT k.autorizado, 'banos', k.banos, 'fosas', k.fosas,
                            'ordenes', coalesce((
                                SELECT jsonb_agg(jsonb_build_object(
                                    'ordenId', md5(jo.jornada_id::text||':'||p.parada_id::text||':'||k.cliente_id::text||':'||n)::uuid,
                                    'vuelta', n) ORDER BY n)
                                FROM generate_series(1, k.fosas) n), '[]'::jsonb)
                        ) ORDER BY k.nombre, k.cliente_id)
                        FROM clientes k WHERE k.parada_id=p.parada_id AND k.tipo=p.tipo), '[]'::jsonb)
                ) ORDER BY p.tipo, p.sitio, p.parada_id)
                FROM paradas p WHERE p.jornada_id=jo.jornada_id), '[]'::jsonb)
        ) AS dato
        FROM jornadas jo
    ) j;
    RETURN r;
END $$;

CREATE OR REPLACE FUNCTION negocio.resolver_orden_derivada(o uuid, s uuid, p_persona uuid, p_jornada uuid, p_orden uuid)
RETURNS TABLE(parada_id uuid, cliente_id uuid, vuelta integer, vehiculo_id uuid, autorizado boolean)
LANGUAGE sql STABLE SECURITY DEFINER SET search_path=pg_catalog AS $$
    WITH jo AS (
        SELECT j.id AS jornada_id, j.ruta_id, j.fecha, rv.vehiculo_id
        FROM negocio.jornada j
        JOIN LATERAL (SELECT x.vehiculo_id FROM negocio.ruta_version x
            WHERE (x.organizacion_id,x.sucursal_id,x.ruta_id)=(j.organizacion_id,j.sucursal_id,j.ruta_id)
            ORDER BY x.revision DESC LIMIT 1) rv ON true
        WHERE j.organizacion_id=o AND j.sucursal_id=s AND j.id=p_jornada
          AND EXISTS (SELECT 1 FROM negocio.asignacion_unidad a
                      WHERE (a.organizacion_id,a.sucursal_id,a.vehiculo_id)=(o,s,rv.vehiculo_id) AND a.persona_id=p_persona)
    ), cand AS (
        SELECT jo.jornada_id, pa.id AS parada_id, pc.cliente_id, coalesce(pc.fosas_planeadas,0) AS fosas, jo.vehiculo_id
        FROM jo JOIN negocio.parada pa ON (pa.organizacion_id,pa.sucursal_id,pa.ruta_id)=(o,s,jo.ruta_id) AND NOT EXISTS (SELECT 1 FROM negocio.parada_baja pb WHERE (pb.organizacion_id,pb.sucursal_id,pb.parada_id)=(pa.organizacion_id,pa.sucursal_id,pa.id))
        JOIN LATERAL (SELECT x.id FROM negocio.parada_version x
            WHERE (x.organizacion_id,x.sucursal_id,x.parada_id)=(pa.organizacion_id,pa.sucursal_id,pa.id)
            ORDER BY x.revision DESC LIMIT 1) pv ON true
        JOIN negocio.parada_cliente pc ON (pc.organizacion_id,pc.sucursal_id,pc.version_id)=(o,s,pv.id)
        UNION ALL
        SELECT jo.jornada_id, pe.id, pc.cliente_id, coalesce(pc.fosas_planeadas,0), jo.vehiculo_id
        FROM jo JOIN negocio.parada_extraordinaria pe ON (pe.organizacion_id,pe.sucursal_id,pe.ruta_id)=(o,s,jo.ruta_id) AND pe.fecha=jo.fecha
        JOIN negocio.parada_extraordinaria_cliente pc ON (pc.organizacion_id,pc.sucursal_id,pc.parada_extraordinaria_id)=(o,s,pe.id)
    )
    SELECT cand.parada_id, cand.cliente_id, n, cand.vehiculo_id,
           EXISTS (SELECT 1 FROM negocio.cliente cl JOIN negocio.autorizacion_empresa au
                   ON (au.organizacion_id,au.empresa_id)=(cl.organizacion_id,cl.empresa_id)
                   WHERE (cl.organizacion_id,cl.sucursal_id,cl.id)=(o,s,cand.cliente_id)
                     AND au.autoridad='Medio ambiente' AND au.finalidad='Generador')
    FROM cand CROSS JOIN LATERAL generate_series(1,cand.fosas) AS n
    WHERE md5(cand.jornada_id::text||':'||cand.parada_id::text||':'||cand.cliente_id::text||':'||n)::uuid = p_orden
    LIMIT 1
$$;
