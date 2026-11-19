-- Captura de baños y TIRADA de sanitarios (11-nov-2026, paso B3-B6; decisión docs/decisiones/0023 y diseño
-- docs/diseno/sanitarios-y-tirada-2026-11-11.md; la sección 2 de ese diseño, las correcciones de la crítica, prevalece).
-- Qué hace:
--   * SERVICIO_SANITARIOS: una captura por parada con foto+ubicación y baños succionados (sin tope) -> negocio.succion_parada.
--   * TIRADA_SANITARIOS: la unidad descarga en una planta; cierra el periodo y arma UN manifiesto de sanitarios con folio
--     NNN/AAAA del mismo contador que fosa fija. La carga actual de la unidad (barra) = succiones sin tirar; se reinicia sola.
--   * El operador NUNCA ve folio ni manifiesto. Drive solo desde backend. Plantilla 'sanitarios-1' y firma del generador PROVISIONALES.
-- Reglas de robustez (críticas 1-10): errores esperados de la rama de sanitarios NUNCA revierten la recepción de la captura;
-- se guardan en captura_por_revisar. Orden de locks fijo: sync (instalación+captura) -> unidad (advisory) -> contador de folio
-- (siempre el ÚLTIMO lock, igual que fosa fija). Los campos de litros son internos y PROVISIONALES; todo volumen visible es m3.

-- ---------------------------------------------------------------------------------------------------------------------
-- a) captura_trabajo: tipos nuevos, planta también en la tirada, y datos propios de sanitarios.
--    (orden_derivada_id y jornada_id son uuid NOT NULL sin FK: no se relajan; para sanitarios orden_derivada_id = paradaId o tiradaId.)
-- ---------------------------------------------------------------------------------------------------------------------
DO $$
DECLARE r record;
BEGIN
    FOR r IN SELECT conname FROM pg_constraint
             WHERE conrelid = 'negocio.captura_trabajo'::regclass AND contype = 'c' AND pg_get_constraintdef(oid) LIKE '%RECEPCION_PLANTA%' LOOP
        EXECUTE format('ALTER TABLE negocio.captura_trabajo DROP CONSTRAINT %I', r.conname);
    END LOOP;
END $$;
ALTER TABLE negocio.captura_trabajo
    ADD COLUMN banos integer CHECK (banos >= 0),
    ADD COLUMN banos_asignados integer CHECK (banos_asignados >= 0),
    ADD COLUMN capturas_incluidas uuid[],
    ADD CONSTRAINT captura_trabajo_tipo_check CHECK (tipo IN
        ('SERVICIO_OPERADOR','FIRMA_CLIENTE','RECEPCION_PLANTA','PROBLEMA','SERVICIO_SANITARIOS','TIRADA_SANITARIOS')),
    ADD CONSTRAINT captura_trabajo_planta_check CHECK ((tipo IN ('RECEPCION_PLANTA','TIRADA_SANITARIOS')) = (planta_id IS NOT NULL)),
    ADD CONSTRAINT captura_trabajo_banos_segun_tipo CHECK ((tipo = 'SERVICIO_SANITARIOS') = (banos IS NOT NULL AND banos_asignados IS NOT NULL)),
    ADD CONSTRAINT captura_trabajo_incluidas_check CHECK ((tipo = 'TIRADA_SANITARIOS') = (capturas_incluidas IS NOT NULL));

-- Motivos de revisión nuevos (el hecho se guarda siempre; la revisión decide).
DO $$
DECLARE r record;
BEGIN
    FOR r IN SELECT conname FROM pg_constraint
             WHERE conrelid = 'negocio.captura_por_revisar'::regclass AND contype = 'c' AND pg_get_constraintdef(oid) LIKE '%orden_no_resuelta%' LOOP
        EXECUTE format('ALTER TABLE negocio.captura_por_revisar DROP CONSTRAINT %I', r.conname);
    END LOOP;
END $$;
ALTER TABLE negocio.captura_por_revisar ADD CONSTRAINT captura_por_revisar_motivo_check CHECK (motivo IN
    ('orden_no_resuelta','regla_firma_distinta','parada_no_resuelta','tirada_no_resuelta','tirada_sin_carga',
     'tirada_capturas_ya_incluidas','tirada_incompleta'));

-- ---------------------------------------------------------------------------------------------------------------------
-- b) Tablas. Todo insert-only (infra.inmutable); lo "pendiente" se calcula, nunca se actualiza.
-- ---------------------------------------------------------------------------------------------------------------------
-- Baños succionados en una parada. litros_por_bano guarda la constante vigente al registrar (estimación PROVISIONAL, no medición).
CREATE TABLE negocio.succion_parada (
    organizacion_id uuid NOT NULL, sucursal_id uuid NOT NULL, captura_id uuid NOT NULL,
    jornada_id uuid NOT NULL, parada_id uuid NOT NULL, vehiculo_id uuid NOT NULL, persona_id uuid NOT NULL, sitio_id uuid NOT NULL,
    banos_succionados integer NOT NULL CHECK (banos_succionados >= 0),
    banos_asignados integer NOT NULL CHECK (banos_asignados >= 0),
    litros_por_bano smallint NOT NULL CHECK (litros_por_bano > 0),
    fecha_captura timestamptz NOT NULL, recibida_en timestamptz NOT NULL,
    PRIMARY KEY (organizacion_id, sucursal_id, captura_id),
    FOREIGN KEY (organizacion_id, sucursal_id, captura_id) REFERENCES negocio.captura_trabajo ON DELETE RESTRICT,
    FOREIGN KEY (organizacion_id, sucursal_id, vehiculo_id) REFERENCES negocio.vehiculo ON DELETE RESTRICT,
    FOREIGN KEY (organizacion_id, persona_id) REFERENCES seguridad.persona ON DELETE RESTRICT,
    FOREIGN KEY (organizacion_id, sucursal_id, parada_id) REFERENCES negocio.parada ON DELETE RESTRICT,
    FOREIGN KEY (organizacion_id, sucursal_id, sitio_id) REFERENCES negocio.sitio ON DELETE RESTRICT
);
CREATE INDEX succion_parada_unidad ON negocio.succion_parada (organizacion_id, sucursal_id, vehiculo_id, fecha_captura);
CREATE INDEX succion_parada_parada ON negocio.succion_parada (organizacion_id, sucursal_id, parada_id);
CREATE INDEX succion_parada_persona ON negocio.succion_parada (organizacion_id, persona_id);
CREATE INDEX succion_parada_sitio ON negocio.succion_parada (organizacion_id, sucursal_id, sitio_id);

-- Tirada = la UNIDAD descarga en una planta (no pertenece a una ruta ni a una jornada). planta_nombre sale de la base, no del teléfono.
CREATE TABLE negocio.tirada (
    organizacion_id uuid NOT NULL, sucursal_id uuid NOT NULL, id uuid NOT NULL,
    captura_id uuid NOT NULL, vehiculo_id uuid NOT NULL, persona_id uuid NOT NULL,
    planta_id uuid NOT NULL, planta_nombre text NOT NULL CHECK (btrim(planta_nombre) <> ''),
    nombre_recibe text NOT NULL CHECK (btrim(nombre_recibe) <> ''),
    fecha_captura timestamptz NOT NULL, recibida_en timestamptz NOT NULL,
    PRIMARY KEY (organizacion_id, sucursal_id, id),
    UNIQUE (organizacion_id, sucursal_id, captura_id),
    FOREIGN KEY (organizacion_id, sucursal_id, captura_id) REFERENCES negocio.captura_trabajo ON DELETE RESTRICT,
    FOREIGN KEY (organizacion_id, sucursal_id, vehiculo_id) REFERENCES negocio.vehiculo ON DELETE RESTRICT,
    FOREIGN KEY (organizacion_id, persona_id) REFERENCES seguridad.persona ON DELETE RESTRICT,
    FOREIGN KEY (organizacion_id, sucursal_id, planta_id) REFERENCES negocio.sitio ON DELETE RESTRICT
);
CREATE INDEX tirada_unidad ON negocio.tirada (organizacion_id, sucursal_id, vehiculo_id, fecha_captura);
CREATE INDEX tirada_persona ON negocio.tirada (organizacion_id, persona_id);
CREATE INDEX tirada_planta ON negocio.tirada (organizacion_id, sucursal_id, planta_id);

-- Capturas que la app DECLARA incluidas (puede llegar antes que las succiones: la tirada queda "esperando").
CREATE TABLE negocio.tirada_esperada (
    organizacion_id uuid NOT NULL, sucursal_id uuid NOT NULL, tirada_id uuid NOT NULL, captura_id uuid NOT NULL,
    PRIMARY KEY (organizacion_id, sucursal_id, tirada_id, captura_id),
    FOREIGN KEY (organizacion_id, sucursal_id, tirada_id) REFERENCES negocio.tirada ON DELETE RESTRICT
);
-- Asignación DEFINITIVA al armar el manifiesto. UNIQUE(captura): una succión entra en una sola tirada.
CREATE TABLE negocio.tirada_parada (
    organizacion_id uuid NOT NULL, sucursal_id uuid NOT NULL, tirada_id uuid NOT NULL, captura_id uuid NOT NULL,
    renglon integer NOT NULL CHECK (renglon > 0),
    PRIMARY KEY (organizacion_id, sucursal_id, tirada_id, captura_id),
    UNIQUE (organizacion_id, sucursal_id, captura_id),
    FOREIGN KEY (organizacion_id, sucursal_id, tirada_id) REFERENCES negocio.tirada ON DELETE RESTRICT,
    FOREIGN KEY (organizacion_id, sucursal_id, captura_id) REFERENCES negocio.succion_parada ON DELETE RESTRICT
);
-- manifiesto_id se declara sin FK compuesta aún: negocio.manifiesto se amplía más abajo (misma clave primaria).
CREATE TABLE negocio.tirada_manifiesto (
    organizacion_id uuid NOT NULL, sucursal_id uuid NOT NULL, tirada_id uuid NOT NULL, manifiesto_id uuid NOT NULL,
    PRIMARY KEY (organizacion_id, sucursal_id, tirada_id),
    UNIQUE (organizacion_id, sucursal_id, manifiesto_id),
    FOREIGN KEY (organizacion_id, sucursal_id, tirada_id) REFERENCES negocio.tirada ON DELETE RESTRICT,
    FOREIGN KEY (organizacion_id, sucursal_id, manifiesto_id) REFERENCES negocio.manifiesto ON DELETE RESTRICT
);

-- Manifiesto sin cliente (sanitarios): el generador es nuestra empresa. Ninguna fila actual tiene cliente_id NULL.
ALTER TABLE negocio.manifiesto ALTER COLUMN cliente_id DROP NOT NULL;
ALTER TABLE negocio.manifiesto
    ADD COLUMN tipo text NOT NULL DEFAULT 'fosa_fija' CHECK (tipo IN ('fosa_fija','sanitarios')),
    ADD CONSTRAINT manifiesto_tipo_cliente CHECK ((tipo = 'fosa_fija') = (cliente_id IS NOT NULL));

-- Una fila por parada incluida en el manifiesto, ordenada cronológicamente. La coordenada es la de la FOTO de esa parada
-- (nunca se inventa). La foto se identifica por la evidencia (su archivo llega después y comparte el mismo id).
CREATE TABLE negocio.manifiesto_parada (
    organizacion_id uuid NOT NULL, sucursal_id uuid NOT NULL, version_id uuid NOT NULL, renglon integer NOT NULL CHECK (renglon > 0),
    parada_id uuid NOT NULL, captura_id uuid NOT NULL, sitio_id uuid NOT NULL, ficha_sitio_id uuid,
    fecha timestamptz NOT NULL, banos integer NOT NULL CHECK (banos >= 0),
    latitud numeric(10,7), longitud numeric(10,7), precision_m numeric(12,3),
    clientes jsonb NOT NULL DEFAULT '[]'::jsonb,   -- instantánea [{clienteId,nombre,banos}] al armar; la tabla es insert-only
    evidencia_foto_id uuid,
    PRIMARY KEY (organizacion_id, sucursal_id, version_id, renglon),
    FOREIGN KEY (organizacion_id, sucursal_id, version_id) REFERENCES negocio.version_manifiesto ON DELETE RESTRICT,
    FOREIGN KEY (organizacion_id, sucursal_id, parada_id) REFERENCES negocio.parada ON DELETE RESTRICT,
    FOREIGN KEY (organizacion_id, sucursal_id, captura_id) REFERENCES negocio.succion_parada ON DELETE RESTRICT,
    FOREIGN KEY (organizacion_id, sucursal_id, sitio_id) REFERENCES negocio.sitio ON DELETE RESTRICT,
    FOREIGN KEY (organizacion_id, sucursal_id, ficha_sitio_id) REFERENCES negocio.ficha_sitio ON DELETE RESTRICT,
    FOREIGN KEY (organizacion_id, sucursal_id, evidencia_foto_id) REFERENCES negocio.evidencia_captura ON DELETE RESTRICT
);
CREATE INDEX manifiesto_parada_parada ON negocio.manifiesto_parada (organizacion_id, sucursal_id, parada_id);
CREATE INDEX manifiesto_parada_captura ON negocio.manifiesto_parada (organizacion_id, sucursal_id, captura_id);
CREATE INDEX manifiesto_parada_sitio ON negocio.manifiesto_parada (organizacion_id, sucursal_id, sitio_id);
CREATE INDEX manifiesto_parada_ficha_sitio ON negocio.manifiesto_parada (organizacion_id, sucursal_id, ficha_sitio_id);
CREATE INDEX manifiesto_parada_evidencia ON negocio.manifiesto_parada (organizacion_id, sucursal_id, evidencia_foto_id);

-- Recepción en planta: destinatario documental (planta + nombre de quien recibe). No es un participante formal hasta cerrar PG-08.
-- Las firmas del operador y de quien recibe son las evidencias de la captura de la tirada (captura_id).
-- firma_generador_id = firma PRECARGADA provisional vigente (NULL si no hay ninguna configurada).
CREATE TABLE negocio.recepcion_manifiesto (
    organizacion_id uuid NOT NULL, sucursal_id uuid NOT NULL, version_id uuid NOT NULL,
    planta_id uuid NOT NULL, planta_nombre text NOT NULL, nombre_recibe text NOT NULL,
    captura_id uuid NOT NULL, firma_generador_id uuid,
    PRIMARY KEY (organizacion_id, sucursal_id, version_id),
    FOREIGN KEY (organizacion_id, sucursal_id, version_id) REFERENCES negocio.version_manifiesto ON DELETE RESTRICT,
    FOREIGN KEY (organizacion_id, sucursal_id, planta_id) REFERENCES negocio.sitio ON DELETE RESTRICT,
    FOREIGN KEY (organizacion_id, sucursal_id, captura_id) REFERENCES negocio.captura ON DELETE RESTRICT,
    FOREIGN KEY (organizacion_id, sucursal_id, firma_generador_id) REFERENCES negocio.firma_generador_precargada ON DELETE RESTRICT
);
CREATE INDEX recepcion_manifiesto_planta ON negocio.recepcion_manifiesto (organizacion_id, sucursal_id, planta_id);
CREATE INDEX recepcion_manifiesto_captura ON negocio.recepcion_manifiesto (organizacion_id, sucursal_id, captura_id);
CREATE INDEX recepcion_manifiesto_firma ON negocio.recepcion_manifiesto (organizacion_id, sucursal_id, firma_generador_id);

DO $$
DECLARE t text;
BEGIN
    FOREACH t IN ARRAY ARRAY['succion_parada','tirada','tirada_esperada','tirada_parada','tirada_manifiesto','manifiesto_parada','recepcion_manifiesto'] LOOP
        EXECUTE format('CREATE TRIGGER conservar_historia BEFORE UPDATE OR DELETE ON negocio.%I FOR EACH ROW EXECUTE FUNCTION infra.inmutable()', t);
        EXECUTE format('ALTER TABLE negocio.%I ENABLE ROW LEVEL SECURITY', t);
        EXECUTE format('CREATE POLICY contexto ON negocio.%I TO manifiestos_app USING (seguridad.puede(organizacion_id, sucursal_id, ''datos.leer''))', t);
        EXECUTE format('GRANT SELECT ON negocio.%I TO manifiestos_app', t);
    END LOOP;
    -- Los hijos del manifiesto solo se insertan en la transacción que crea su versión.
    FOREACH t IN ARRAY ARRAY['manifiesto_parada','recepcion_manifiesto'] LOOP
        EXECUTE format('CREATE TRIGGER agregado_inmutable BEFORE INSERT ON negocio.%I FOR EACH ROW EXECUTE FUNCTION infra.agregado_inmutable()', t);
    END LOOP;
END $$;

-- ---------------------------------------------------------------------------------------------------------------------
-- c) Funciones auxiliares (solo las invocan funciones definer; sin GRANT para la aplicación).
-- ---------------------------------------------------------------------------------------------------------------------
-- Candado de unidad: serializa succiones y tiradas de la misma unidad. Siempre ANTES del contador de folio.
CREATE FUNCTION negocio.bloquear_unidad(o uuid, p_vehiculo uuid) RETURNS void LANGUAGE sql VOLATILE SET search_path = pg_catalog AS $$
    SELECT pg_advisory_xact_lock(hashtextextended('unidad:' || o::text || ':' || p_vehiculo::text, 0))
$$;

REVOKE ALL ON FUNCTION negocio.bloquear_unidad(uuid, uuid) FROM PUBLIC;

-- ¿La persona era la operadora de la unidad a la fecha de la captura? Se usa la asignación vigente a esa fecha (con 2 minutos
-- de tolerancia por el reloj del teléfono). Si todas las asignaciones se registraron DESPUÉS de la captura se toma la primera
-- registrada (la fecha de registro no es la de efecto). Una captura legítima antigua no cae a revisión por un cambio posterior.
CREATE FUNCTION negocio.persona_opera_unidad(o uuid, s uuid, p_vehiculo uuid, p_persona uuid, p_fecha timestamptz) RETURNS boolean
LANGUAGE sql STABLE SECURITY DEFINER SET search_path = pg_catalog AS $$
    SELECT coalesce((
        SELECT a.persona_id = p_persona FROM negocio.asignacion_unidad a
        WHERE (a.organizacion_id, a.sucursal_id, a.vehiculo_id) = (o, s, p_vehiculo)
        ORDER BY (a.registrada_en <= p_fecha + interval '2 minutes') DESC,
                 CASE WHEN a.registrada_en <= p_fecha + interval '2 minutes' THEN a.revision END DESC NULLS LAST,
                 a.revision
        LIMIT 1), false)
$$;
REVOKE ALL ON FUNCTION negocio.persona_opera_unidad(uuid, uuid, uuid, uuid, timestamptz) FROM PUBLIC;

-- Revisión de la ruta vigente en la fecha de la jornada (no la de hoy): la unidad no cambia retroactivamente. Sin revisión
-- registrada hasta esa fecha se toma la primera.
CREATE FUNCTION negocio.ruta_version_en(o uuid, s uuid, p_ruta uuid, p_fecha date) RETURNS TABLE (vehiculo_id uuid, revision integer)
LANGUAGE sql STABLE SECURITY DEFINER SET search_path = pg_catalog AS $$
    SELECT v.vehiculo_id, v.revision FROM negocio.ruta_version v
    WHERE (v.organizacion_id, v.sucursal_id, v.ruta_id) = (o, s, p_ruta)
    ORDER BY (v.registrada_en < ((p_fecha + 1)::timestamp AT TIME ZONE 'UTC')) DESC,
             CASE WHEN v.registrada_en < ((p_fecha + 1)::timestamp AT TIME ZONE 'UTC') THEN v.revision END DESC NULLS LAST,
             v.revision
    LIMIT 1
$$;
REVOKE ALL ON FUNCTION negocio.ruta_version_en(uuid, uuid, uuid, date) FROM PUBLIC;

-- Parada de una jornada de SANITARIOS operada por la persona. Una parada dada de baja DESPUÉS de descargarse el plan sigue
-- resolviendo (es un hecho ocurrido). La unidad sale de la revisión de la ruta en la fecha de la jornada enviada.
CREATE FUNCTION negocio.resolver_parada_derivada(o uuid, s uuid, p_persona uuid, p_jornada uuid, p_parada uuid, p_fecha timestamptz)
RETURNS TABLE (parada_id uuid, sitio_id uuid, ruta_id uuid, vehiculo_id uuid)
LANGUAGE sql STABLE SECURITY DEFINER SET search_path = pg_catalog AS $$
    SELECT pa.id, pv.sitio_id, j.ruta_id, rv.vehiculo_id
    FROM negocio.jornada j
    JOIN negocio.parada pa ON (pa.organizacion_id, pa.sucursal_id, pa.ruta_id) = (j.organizacion_id, j.sucursal_id, j.ruta_id) AND pa.id = p_parada
    JOIN LATERAL negocio.ruta_version_en(o, s, j.ruta_id, j.fecha) rv ON true
    JOIN LATERAL (SELECT x.sitio_id FROM negocio.parada_version x
        WHERE (x.organizacion_id, x.sucursal_id, x.parada_id) = (pa.organizacion_id, pa.sucursal_id, pa.id)
        ORDER BY x.revision DESC LIMIT 1) pv ON true
    JOIN LATERAL (SELECT x.clasificacion FROM negocio.ficha_vehiculo x
        WHERE (x.organizacion_id, x.sucursal_id, x.vehiculo_id) = (o, s, rv.vehiculo_id)
        ORDER BY x.revision DESC LIMIT 1) f ON f.clasificacion = 'sanitarios'
    WHERE j.organizacion_id = o AND j.sucursal_id = s AND j.id = p_jornada
      AND negocio.persona_opera_unidad(o, s, rv.vehiculo_id, p_persona, p_fecha)
$$;
REVOKE ALL ON FUNCTION negocio.resolver_parada_derivada(uuid, uuid, uuid, uuid, uuid, timestamptz) FROM PUBLIC;

-- Clientes de la parada (y sus baños planeados) en la revisión vigente a la fecha indicada.
CREATE FUNCTION negocio.clientes_parada_en(o uuid, s uuid, p_parada uuid, p_fecha timestamptz) RETURNS jsonb
LANGUAGE sql STABLE SECURITY DEFINER SET search_path = pg_catalog AS $$
    WITH pv AS (
        SELECT x.id FROM negocio.parada_version x
        WHERE (x.organizacion_id, x.sucursal_id, x.parada_id) = (o, s, p_parada)
        ORDER BY (x.registrada_en <= p_fecha + interval '2 minutes') DESC,
                 CASE WHEN x.registrada_en <= p_fecha + interval '2 minutes' THEN x.revision END DESC NULLS LAST, x.revision
        LIMIT 1), k AS (
        SELECT pc.cliente_id, coalesce(pc.banos_planeados, 0) AS banos,
               coalesce(nullif(btrim(fe.nombre_comercial), ''), nullif(btrim(fe.nombre_fiscal), ''), nullif(btrim(cl.codigo_comercial), ''), 'Cliente sin nombre') AS nombre
        FROM pv JOIN negocio.parada_cliente pc ON (pc.organizacion_id, pc.sucursal_id, pc.version_id) = (o, s, pv.id)
        JOIN negocio.cliente cl ON (cl.organizacion_id, cl.sucursal_id, cl.id) = (o, s, pc.cliente_id)
        LEFT JOIN LATERAL (SELECT x.nombre_fiscal, x.nombre_comercial FROM negocio.ficha_empresa x
            WHERE (x.organizacion_id, x.empresa_id) = (cl.organizacion_id, cl.empresa_id) ORDER BY x.revision DESC LIMIT 1) fe ON true)
    SELECT coalesce(jsonb_agg(jsonb_build_object('clienteId', k.cliente_id, 'nombre', k.nombre, 'banos', k.banos) ORDER BY k.nombre, k.cliente_id), '[]'::jsonb) FROM k
$$;
REVOKE ALL ON FUNCTION negocio.clientes_parada_en(uuid, uuid, uuid, timestamptz) FROM PUBLIC;

-- Nombre de una planta de la sucursal (sitio que no es punto de servicio; criterio de seguridad.plantas_operador). NULL si no es planta.
CREATE FUNCTION negocio.nombre_planta(o uuid, s uuid, p_planta uuid) RETURNS text
LANGUAGE sql STABLE SECURITY DEFINER SET search_path = pg_catalog AS $$
    SELECT coalesce(nullif(btrim(fs.nombre), ''), si.referencia)
    FROM negocio.sitio si
    LEFT JOIN LATERAL (SELECT x.nombre FROM negocio.ficha_sitio x
        WHERE (x.organizacion_id, x.sucursal_id, x.sitio_id) = (si.organizacion_id, si.sucursal_id, si.id) ORDER BY x.revision DESC LIMIT 1) fs ON true
    WHERE (si.organizacion_id, si.sucursal_id, si.id) = (o, s, p_planta)
      AND NOT EXISTS (SELECT 1 FROM negocio.punto_servicio ps WHERE (ps.organizacion_id, ps.sucursal_id, ps.sitio_id) = (si.organizacion_id, si.sucursal_id, si.id))
$$;
REVOKE ALL ON FUNCTION negocio.nombre_planta(uuid, uuid, uuid) FROM PUBLIC;

-- ---------------------------------------------------------------------------------------------------------------------
-- d) Carga actual de la UNIDAD (reemplaza la función temporal de 0027). Derivada: succiones de la unidad que NO están en
--    ninguna tirada armada. m3 = baños * litros_por_bano / 1000 (estimación). Porcentaje SIN tope. Dos rutas de la misma unidad
--    ven lo mismo y puede venir de días anteriores. Se reinicia sola al armarse el manifiesto de una tirada.
-- ---------------------------------------------------------------------------------------------------------------------
CREATE OR REPLACE FUNCTION negocio.carga_unidad_interna(o uuid, s uuid, p_vehiculo uuid)
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
    ), pend AS (
        SELECT sp.banos_succionados AS banos, sp.litros_por_bano, (sp.fecha_captura AT TIME ZONE 'UTC')::date AS dia
        FROM negocio.succion_parada sp
        WHERE (sp.organizacion_id, sp.sucursal_id, sp.vehiculo_id) = (o, s, p_vehiculo)
          AND NOT EXISTS (SELECT 1 FROM negocio.tirada_parada tp
                          WHERE (tp.organizacion_id, tp.sucursal_id, tp.captura_id) = (sp.organizacion_id, sp.sucursal_id, sp.captura_id))
    ), dias AS (
        SELECT dia, sum(banos) AS banos FROM pend GROUP BY dia
    ), c AS (
        SELECT coalesce((SELECT sum(banos) FROM pend), 0)::integer AS banos,
               coalesce((SELECT sum(banos::numeric * litros_por_bano) FROM pend), 0) AS litros,
               (SELECT max(t.recibida_en) FROM negocio.tirada t
                WHERE (t.organizacion_id, t.sucursal_id, t.vehiculo_id) = (o, s, p_vehiculo)
                  AND EXISTS (SELECT 1 FROM negocio.tirada_manifiesto tm WHERE (tm.organizacion_id, tm.sucursal_id, tm.tirada_id) = (t.organizacion_id, t.sucursal_id, t.id))) AS ultima,
               coalesce((SELECT jsonb_agg(jsonb_build_object('fecha', d.dia, 'banos', d.banos::integer) ORDER BY d.dia) FROM dias d), '[]'::jsonb) AS desglose
    )
    SELECT c.banos,
           round(c.litros / 1000.0, 3),
           v.valor_m3,
           CASE WHEN v.valor_m3 > 0 THEN round(c.litros / 1000.0 / v.valor_m3 * 100, 1) END,
           c.ultima, c.desglose
    FROM v CROSS JOIN c
$$;

-- ---------------------------------------------------------------------------------------------------------------------
-- e) Armado del manifiesto de la tirada. Devuelve el manifiesto, o NULL si la tirada sigue ESPERANDO capturas.
--    Errores esperados (los convierte en captura_por_revisar quien llama): P0A01 sin carga, P0A02 capturas ya incluidas en otra
--    tirada, P0A03 no resuelta (configuración o unidad distinta). El folio se asigna AL FINAL de las lecturas y escrituras previas.
-- ---------------------------------------------------------------------------------------------------------------------
CREATE FUNCTION negocio.armar_manifiesto_sanitarios(o uuid, s uuid, p_tirada uuid) RETURNS uuid
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path = pg_catalog AS $$
DECLARE
    ti negocio.tirada%ROWTYPE; m uuid; v uuid; folio text; total bigint; litros numeric; n_esp integer;
    emp uuid; ficha_emp uuid; ficha_veh uuid; aut_gen uuid; aut_tra uuid; firma uuid;
BEGIN
    SELECT * INTO ti FROM negocio.tirada WHERE (organizacion_id, sucursal_id, id) = (o, s, p_tirada);
    IF NOT FOUND THEN RAISE EXCEPTION 'Tirada no encontrada' USING ERRCODE = 'P0A03'; END IF;
    PERFORM negocio.bloquear_unidad(o, ti.vehiculo_id);
    SELECT tm.manifiesto_id INTO m FROM negocio.tirada_manifiesto tm WHERE (tm.organizacion_id, tm.sucursal_id, tm.tirada_id) = (o, s, p_tirada);
    IF FOUND THEN RETURN m; END IF;   -- idempotente: ya está armado

    SELECT count(*) INTO n_esp FROM negocio.tirada_esperada e WHERE (e.organizacion_id, e.sucursal_id, e.tirada_id) = (o, s, p_tirada);
    IF n_esp = 0 THEN RAISE EXCEPTION 'Tirada sin carga' USING ERRCODE = 'P0A01'; END IF;
    IF EXISTS (SELECT 1 FROM negocio.tirada_esperada e JOIN negocio.tirada_parada tp
               ON (tp.organizacion_id, tp.sucursal_id, tp.captura_id) = (e.organizacion_id, e.sucursal_id, e.captura_id)
               WHERE (e.organizacion_id, e.sucursal_id, e.tirada_id) = (o, s, p_tirada)) THEN
        RAISE EXCEPTION 'Capturas ya incluidas en otra tirada' USING ERRCODE = 'P0A02';
    END IF;
    IF EXISTS (SELECT 1 FROM negocio.tirada_esperada e JOIN negocio.succion_parada sp
               ON (sp.organizacion_id, sp.sucursal_id, sp.captura_id) = (e.organizacion_id, e.sucursal_id, e.captura_id)
               WHERE (e.organizacion_id, e.sucursal_id, e.tirada_id) = (o, s, p_tirada) AND sp.vehiculo_id <> ti.vehiculo_id) THEN
        RAISE EXCEPTION 'La tirada declara capturas de otra unidad' USING ERRCODE = 'P0A03';
    END IF;
    -- Faltan succiones por llegar: la tirada sigue esperando (se reintenta al llegar cada una).
    IF EXISTS (SELECT 1 FROM negocio.tirada_esperada e
               WHERE (e.organizacion_id, e.sucursal_id, e.tirada_id) = (o, s, p_tirada)
                 AND NOT EXISTS (SELECT 1 FROM negocio.succion_parada sp
                                 WHERE (sp.organizacion_id, sp.sucursal_id, sp.captura_id) = (e.organizacion_id, e.sucursal_id, e.captura_id))) THEN
        RETURN NULL;
    END IF;
    SELECT sum(sp.banos_succionados), sum(sp.banos_succionados::numeric * sp.litros_por_bano) INTO total, litros
    FROM negocio.tirada_esperada e JOIN negocio.succion_parada sp
      ON (sp.organizacion_id, sp.sucursal_id, sp.captura_id) = (e.organizacion_id, e.sucursal_id, e.captura_id)
    WHERE (e.organizacion_id, e.sucursal_id, e.tirada_id) = (o, s, p_tirada);
    IF coalesce(total, 0) = 0 THEN RAISE EXCEPTION 'Tirada sin baños' USING ERRCODE = 'P0A01'; END IF;

    -- Generador = transportista = nuestra empresa (domicilio fiscal); el destinatario documental es la planta (recepcion_manifiesto).
    SELECT su.empresa_id INTO emp FROM negocio.sucursal su WHERE (su.organizacion_id, su.id) = (o, s);
    SELECT f.id INTO ficha_veh FROM negocio.ficha_vehiculo f
    WHERE (f.organizacion_id, f.sucursal_id, f.vehiculo_id) = (o, s, ti.vehiculo_id) ORDER BY f.revision DESC LIMIT 1;
    SELECT x.id INTO ficha_emp FROM negocio.ficha_empresa x WHERE (x.organizacion_id, x.empresa_id) = (o, emp) ORDER BY x.revision DESC LIMIT 1;
    IF emp IS NULL OR ficha_veh IS NULL OR ficha_emp IS NULL THEN
        RAISE EXCEPTION 'Falta la ficha de la unidad o de la empresa' USING ERRCODE = 'P0A03';
    END IF;
    SELECT a.id INTO aut_gen FROM negocio.autorizacion_empresa a
    WHERE (a.organizacion_id, a.empresa_id) = (o, emp) AND a.autoridad = 'Medio ambiente' AND a.finalidad = 'Generador'
    ORDER BY a.registrada_en DESC, a.id DESC LIMIT 1;
    SELECT a.id INTO aut_tra FROM negocio.autorizacion_empresa a
    WHERE (a.organizacion_id, a.empresa_id) = (o, emp) AND a.autoridad = 'Medio ambiente' AND a.finalidad = 'Transportista'
    ORDER BY a.registrada_en DESC, a.id DESC LIMIT 1;
    SELECT g.id INTO firma FROM negocio.firma_generador_precargada g
    WHERE (g.organizacion_id, g.sucursal_id, g.empresa_id) = (o, s, emp) ORDER BY g.revision DESC LIMIT 1;

    INSERT INTO negocio.empresa_sucursal VALUES (o, s, emp) ON CONFLICT DO NOTHING;
    INSERT INTO negocio.manifiesto (organizacion_id, sucursal_id, tipo) VALUES (o, s, 'sanitarios') RETURNING id INTO m;
    -- Contador de folio compartido con fosa fija: SIEMPRE el último lock de la transacción.
    folio := negocio.siguiente_folio(o, s, extract(year FROM now() AT TIME ZONE 'UTC')::integer);
    INSERT INTO negocio.version_manifiesto (organizacion_id, sucursal_id, manifiesto_id, revision, numero_manifiesto, ficha_vehiculo_id, version_plantilla)
    VALUES (o, s, m, 1, folio, ficha_veh, 'sanitarios-1') RETURNING id INTO v;   -- plantilla PROVISIONAL
    INSERT INTO negocio.participante_manifiesto (organizacion_id, sucursal_id, version_id, rol, empresa_id, ficha_empresa_id, autorizacion_id, origen_domicilio)
    VALUES (o, s, v, 'generador', emp, ficha_emp, aut_gen, 'fiscal'),
           (o, s, v, 'transportista', emp, ficha_emp, aut_tra, 'fiscal');
    -- Un renglón resumen. tipo_contenedor y numero_contenedores (= baños) son PROVISIONALES: no están confirmados como campos del manifiesto.
    INSERT INTO negocio.detalle_manifiesto (organizacion_id, sucursal_id, version_id, renglon, descripcion_residuo, tipo_contenedor,
        numero_contenedores, cantidad, unidad_cantidad, naturaleza_cantidad, metodo_cuantificacion, version_metodo)
    VALUES (o, s, v, 1, 'Agua residual de sanitarios portátiles', 'sanitario', total, round(litros / 1000.0, 6), 'm3', 'estimada', 'banos_x_16_l', '1');

    INSERT INTO negocio.manifiesto_parada (organizacion_id, sucursal_id, version_id, renglon, parada_id, captura_id, sitio_id, ficha_sitio_id,
        fecha, banos, latitud, longitud, precision_m, clientes, evidencia_foto_id)
    SELECT o, s, v, row_number() OVER (ORDER BY sp.fecha_captura, sp.recibida_en, sp.captura_id), sp.parada_id, sp.captura_id, sp.sitio_id, fs.id,
           sp.fecha_captura, sp.banos_succionados, ev.latitud, ev.longitud, ev.precision_metros,
           negocio.clientes_parada_en(o, s, sp.parada_id, sp.fecha_captura), ev.id
    FROM negocio.tirada_esperada e
    JOIN negocio.succion_parada sp ON (sp.organizacion_id, sp.sucursal_id, sp.captura_id) = (e.organizacion_id, e.sucursal_id, e.captura_id)
    LEFT JOIN LATERAL (SELECT x.id FROM negocio.ficha_sitio x WHERE (x.organizacion_id, x.sucursal_id, x.sitio_id) = (sp.organizacion_id, sp.sucursal_id, sp.sitio_id)
        ORDER BY x.revision DESC LIMIT 1) fs ON true
    LEFT JOIN LATERAL (SELECT x.id, x.latitud, x.longitud, x.precision_metros FROM negocio.evidencia_captura x
        WHERE (x.organizacion_id, x.sucursal_id, x.captura_id) = (sp.organizacion_id, sp.sucursal_id, sp.captura_id) AND x.rol = 'FOTO'
        ORDER BY x.id LIMIT 1) ev ON true
    WHERE (e.organizacion_id, e.sucursal_id, e.tirada_id) = (o, s, p_tirada);
    INSERT INTO negocio.recepcion_manifiesto (organizacion_id, sucursal_id, version_id, planta_id, planta_nombre, nombre_recibe, captura_id, firma_generador_id)
    VALUES (o, s, v, ti.planta_id, ti.planta_nombre, ti.nombre_recibe, ti.captura_id, firma);
    -- Asignación definitiva: de aquí en adelante estas succiones ya no cuentan en la carga de la unidad.
    INSERT INTO negocio.tirada_parada (organizacion_id, sucursal_id, tirada_id, captura_id, renglon)
    SELECT o, s, p_tirada, mp.captura_id, mp.renglon FROM negocio.manifiesto_parada mp WHERE (mp.organizacion_id, mp.sucursal_id, mp.version_id) = (o, s, v);
    INSERT INTO negocio.tirada_manifiesto (organizacion_id, sucursal_id, tirada_id, manifiesto_id) VALUES (o, s, p_tirada, m);
    RETURN m;
END $$;
REVOKE ALL ON FUNCTION negocio.armar_manifiesto_sanitarios(uuid, uuid, uuid) FROM PUBLIC;

-- Convierte el error esperado de una tirada en su motivo de revisión (nunca una excepción que revierta la recepción).
-- Errores transitorios (bloqueos, cancelaciones, recursos) se relanzan: el reintento del teléfono los resuelve.
CREATE FUNCTION negocio.marcar_tirada_por_revisar(o uuid, s uuid, p_captura uuid, p_sqlstate text) RETURNS void
LANGUAGE sql VOLATILE SET search_path = pg_catalog AS $$
    INSERT INTO negocio.captura_por_revisar (organizacion_id, sucursal_id, captura_id, motivo)
    VALUES (o, s, p_captura, CASE p_sqlstate WHEN 'P0A01' THEN 'tirada_sin_carga' WHEN 'P0A02' THEN 'tirada_capturas_ya_incluidas'
                                              WHEN '23505' THEN 'tirada_capturas_ya_incluidas' ELSE 'tirada_no_resuelta' END)
    ON CONFLICT DO NOTHING
$$;
REVOKE ALL ON FUNCTION negocio.marcar_tirada_por_revisar(uuid, uuid, uuid, text) FROM PUBLIC;

-- Reintenta las tiradas de la unidad que esperan capturas (se llama al llegar cada succión). Cada intento es una subtransacción.
-- Además de las tiradas de la unidad, reintenta las de OTRA unidad que declaran la captura recién llegada (p_captura): si la
-- succión se resolvió a una unidad distinta de la declarada, armar_manifiesto_sanitarios las manda a revisión (P0A03) en vez
-- de dejarlas esperando para siempre.
CREATE FUNCTION negocio.reintentar_tiradas(o uuid, s uuid, p_vehiculo uuid, p_captura uuid DEFAULT NULL) RETURNS void
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path = pg_catalog AS $$
DECLARE t record; estado text;
BEGIN
    FOR t IN SELECT ti.id, ti.captura_id FROM negocio.tirada ti
             WHERE (ti.organizacion_id, ti.sucursal_id) = (o, s)
               AND (ti.vehiculo_id = p_vehiculo
                    OR (p_captura IS NOT NULL AND EXISTS (SELECT 1 FROM negocio.tirada_esperada e
                         WHERE (e.organizacion_id, e.sucursal_id, e.tirada_id, e.captura_id) = (o, s, ti.id, p_captura))))
               AND NOT EXISTS (SELECT 1 FROM negocio.tirada_manifiesto tm WHERE (tm.organizacion_id, tm.sucursal_id, tm.tirada_id) = (ti.organizacion_id, ti.sucursal_id, ti.id))
             ORDER BY ti.fecha_captura, ti.id LOOP
        BEGIN
            PERFORM negocio.armar_manifiesto_sanitarios(o, s, t.id);
        EXCEPTION WHEN OTHERS THEN
            GET STACKED DIAGNOSTICS estado = RETURNED_SQLSTATE;
            IF estado LIKE '40%' OR estado LIKE '53%' OR estado LIKE '57%' THEN RAISE; END IF;
            PERFORM negocio.marcar_tirada_por_revisar(o, s, t.captura_id, estado);
        END;
    END LOOP;
END $$;
REVOKE ALL ON FUNCTION negocio.reintentar_tiradas(uuid, uuid, uuid, uuid) FROM PUBLIC;

-- Procesa una captura de sanitarios ya guardada (con sus evidencias). Llamada por el disparador de captura_trabajo.
CREATE FUNCTION negocio.procesar_captura_sanitarios(o uuid, s uuid, p_captura uuid) RETURNS void
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path = pg_catalog AS $$
DECLARE t negocio.captura_trabajo%ROWTYPE; k negocio.captura%ROWTYPE; r record; datos jsonb; veh uuid; planta uuid; nombre text; estado text;
BEGIN
    SELECT * INTO t FROM negocio.captura_trabajo WHERE (organizacion_id, sucursal_id, captura_id) = (o, s, p_captura);
    SELECT * INTO k FROM negocio.captura WHERE (organizacion_id, sucursal_id, id) = (o, s, p_captura);

    IF t.tipo = 'SERVICIO_SANITARIOS' THEN
        SELECT * INTO r FROM negocio.resolver_parada_derivada(o, s, t.persona_id, t.jornada_id, t.orden_derivada_id, k.ocurrida_en);
        IF NOT FOUND THEN
            INSERT INTO negocio.captura_por_revisar (organizacion_id, sucursal_id, captura_id, motivo) VALUES (o, s, p_captura, 'parada_no_resuelta');
            RETURN;
        END IF;
        PERFORM negocio.bloquear_unidad(o, r.vehiculo_id);
        INSERT INTO negocio.succion_parada (organizacion_id, sucursal_id, captura_id, jornada_id, parada_id, vehiculo_id, persona_id, sitio_id,
            banos_succionados, banos_asignados, litros_por_bano, fecha_captura, recibida_en)
        VALUES (o, s, p_captura, t.jornada_id, r.parada_id, r.vehiculo_id, t.persona_id, r.sitio_id,
            t.banos, t.banos_asignados, negocio.litros_por_bano(), k.ocurrida_en, k.recibida_en);
        PERFORM negocio.reintentar_tiradas(o, s, r.vehiculo_id, p_captura);   -- succión tardía: puede completar una tirada que esperaba
        RETURN;
    END IF;

    -- TIRADA_SANITARIOS. La unidad viene en el trabajo; debe ser operada por la persona a la fecha de la captura.
    BEGIN
        datos := convert_from(t.datos_orden, 'UTF8')::jsonb;
        veh := (datos->>'unidadId')::uuid;
        planta := t.planta_id::uuid;
    EXCEPTION WHEN OTHERS THEN
        PERFORM negocio.marcar_tirada_por_revisar(o, s, p_captura, 'P0A03'); RETURN;
    END;
    nombre := negocio.nombre_planta(o, s, planta);
    IF veh IS NULL OR nombre IS NULL OR NOT EXISTS (SELECT 1 FROM negocio.vehiculo x WHERE (x.organizacion_id, x.sucursal_id, x.id) = (o, s, veh))
       OR NOT negocio.persona_opera_unidad(o, s, veh, t.persona_id, k.ocurrida_en) THEN
        PERFORM negocio.marcar_tirada_por_revisar(o, s, p_captura, 'P0A03'); RETURN;
    END IF;
    IF coalesce(cardinality(t.capturas_incluidas), 0) = 0 THEN
        PERFORM negocio.marcar_tirada_por_revisar(o, s, p_captura, 'P0A01'); RETURN;
    END IF;
    PERFORM negocio.bloquear_unidad(o, veh);
    BEGIN   -- subtransacción: si falla, no queda tirada a medias ni folio consumido
        IF EXISTS (SELECT 1 FROM negocio.tirada x WHERE (x.organizacion_id, x.sucursal_id, x.id) = (o, s, t.orden_derivada_id)) THEN
            RAISE EXCEPTION 'La tirada ya existe' USING ERRCODE = 'P0A02';
        END IF;
        INSERT INTO negocio.tirada (organizacion_id, sucursal_id, id, captura_id, vehiculo_id, persona_id, planta_id, planta_nombre, nombre_recibe, fecha_captura, recibida_en)
        VALUES (o, s, t.orden_derivada_id, p_captura, veh, t.persona_id, planta, nombre, t.nombre_firmante, k.ocurrida_en, k.recibida_en);
        INSERT INTO negocio.tirada_esperada (organizacion_id, sucursal_id, tirada_id, captura_id)
        SELECT DISTINCT o, s, t.orden_derivada_id, x FROM unnest(t.capturas_incluidas) x;
        PERFORM negocio.armar_manifiesto_sanitarios(o, s, t.orden_derivada_id);
    EXCEPTION WHEN OTHERS THEN
        GET STACKED DIAGNOSTICS estado = RETURNED_SQLSTATE;
        IF estado LIKE '40%' OR estado LIKE '53%' OR estado LIKE '57%' THEN RAISE; END IF;
        PERFORM negocio.marcar_tirada_por_revisar(o, s, p_captura, estado);
    END;
END $$;
REVOKE ALL ON FUNCTION negocio.procesar_captura_sanitarios(uuid, uuid, uuid) FROM PUBLIC;

-- Tiradas que llevan demasiado tiempo esperando una captura que no llega (borrada, rechazada o jamás resuelta): se marcan para
-- revisión, nunca se borran. Sin planificador todavía: la invoca el propietario o una futura acción del panel.
CREATE FUNCTION negocio.marcar_tiradas_incompletas(o uuid, s uuid, p_limite interval DEFAULT interval '3 days') RETURNS integer
LANGUAGE sql VOLATILE SECURITY DEFINER SET search_path = pg_catalog AS $$
    WITH n AS (
        INSERT INTO negocio.captura_por_revisar (organizacion_id, sucursal_id, captura_id, motivo)
        SELECT t.organizacion_id, t.sucursal_id, t.captura_id, 'tirada_incompleta' FROM negocio.tirada t
        WHERE (t.organizacion_id, t.sucursal_id) = (o, s) AND t.recibida_en < clock_timestamp() - p_limite
          AND NOT EXISTS (SELECT 1 FROM negocio.tirada_manifiesto tm WHERE (tm.organizacion_id, tm.sucursal_id, tm.tirada_id) = (t.organizacion_id, t.sucursal_id, t.id))
          AND EXISTS (SELECT 1 FROM negocio.tirada_esperada e WHERE (e.organizacion_id, e.sucursal_id, e.tirada_id) = (t.organizacion_id, t.sucursal_id, t.id)
                      AND NOT EXISTS (SELECT 1 FROM negocio.succion_parada sp WHERE (sp.organizacion_id, sp.sucursal_id, sp.captura_id) = (e.organizacion_id, e.sucursal_id, e.captura_id)))
        ON CONFLICT DO NOTHING RETURNING 1)
    SELECT count(*)::integer FROM n
$$;
REVOKE ALL ON FUNCTION negocio.marcar_tiradas_incompletas(uuid, uuid, interval) FROM PUBLIC;

-- Estado visible de cada tirada: armada, esperando capturas (con cuántas faltan) o por revisar.
CREATE VIEW negocio.estado_tirada WITH (security_invoker = true) AS
SELECT t.organizacion_id, t.sucursal_id, t.id AS tirada_id, t.vehiculo_id, t.captura_id, t.fecha_captura, t.recibida_en, tm.manifiesto_id,
       (SELECT count(*) FROM negocio.tirada_esperada e WHERE (e.organizacion_id, e.sucursal_id, e.tirada_id) = (t.organizacion_id, t.sucursal_id, t.id)
          AND NOT EXISTS (SELECT 1 FROM negocio.succion_parada sp WHERE (sp.organizacion_id, sp.sucursal_id, sp.captura_id) = (e.organizacion_id, e.sucursal_id, e.captura_id)))::integer AS faltantes,
       CASE WHEN tm.manifiesto_id IS NOT NULL THEN 'armada'
            WHEN EXISTS (SELECT 1 FROM negocio.captura_por_revisar cr WHERE (cr.organizacion_id, cr.sucursal_id, cr.captura_id) = (t.organizacion_id, t.sucursal_id, t.captura_id)) THEN 'por_revisar'
            ELSE 'esperando' END AS estado
FROM negocio.tirada t
LEFT JOIN negocio.tirada_manifiesto tm ON (tm.organizacion_id, tm.sucursal_id, tm.tirada_id) = (t.organizacion_id, t.sucursal_id, t.id);
GRANT SELECT ON negocio.estado_tirada TO manifiestos_app;

-- ---------------------------------------------------------------------------------------------------------------------
-- f) Disparador de captura_trabajo: la rama de sanitarios va ANTES de resolver_orden_derivada (que devolvería "orden no resuelta").
--    La rama de fosa fija queda exactamente igual.
-- ---------------------------------------------------------------------------------------------------------------------
CREATE OR REPLACE FUNCTION negocio.tras_captura_trabajo() RETURNS trigger
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE r record; serv boolean; cli boolean; planta boolean; m uuid;
BEGIN
    IF NEW.tipo IN ('SERVICIO_SANITARIOS','TIRADA_SANITARIOS') THEN
        PERFORM negocio.procesar_captura_sanitarios(NEW.organizacion_id, NEW.sucursal_id, NEW.captura_id);
        RETURN NEW;
    END IF;
    -- Un problema por parada de una jornada de sanitarios se acepta con su parada: no arma ni cierra nada.
    IF NEW.tipo = 'PROBLEMA' AND EXISTS (SELECT 1 FROM negocio.resolver_parada_derivada(NEW.organizacion_id, NEW.sucursal_id, NEW.persona_id, NEW.jornada_id, NEW.orden_derivada_id,
            (SELECT k.ocurrida_en FROM negocio.captura k WHERE (k.organizacion_id, k.sucursal_id, k.id) = (NEW.organizacion_id, NEW.sucursal_id, NEW.captura_id)))) THEN
        RETURN NEW;
    END IF;
    PERFORM pg_advisory_xact_lock(hashtextextended('orden:'||NEW.organizacion_id::text||':'||NEW.orden_derivada_id::text,0));
    SELECT * INTO r FROM negocio.resolver_orden_derivada(NEW.organizacion_id,NEW.sucursal_id,NEW.persona_id,NEW.jornada_id,NEW.orden_derivada_id);
    IF NOT FOUND THEN
        INSERT INTO negocio.captura_por_revisar(organizacion_id,sucursal_id,captura_id,motivo)
        VALUES(NEW.organizacion_id,NEW.sucursal_id,NEW.captura_id,'orden_no_resuelta');
        RETURN NEW;
    END IF;
    IF NEW.requiere_firma_cliente IS DISTINCT FROM r.autorizado THEN
        INSERT INTO negocio.captura_por_revisar(organizacion_id,sucursal_id,captura_id,motivo)
        VALUES(NEW.organizacion_id,NEW.sucursal_id,NEW.captura_id,'regla_firma_distinta');
        RETURN NEW;
    END IF;
    IF NEW.tipo='PROBLEMA' OR EXISTS (SELECT 1 FROM negocio.orden_manifiesto om
        WHERE (om.organizacion_id,om.sucursal_id,om.orden_derivada_id)=(NEW.organizacion_id,NEW.sucursal_id,NEW.orden_derivada_id)) THEN
        RETURN NEW;
    END IF;
    SELECT coalesce(bool_or(t.tipo='SERVICIO_OPERADOR'),false), coalesce(bool_or(t.tipo='FIRMA_CLIENTE'),false), coalesce(bool_or(t.tipo='RECEPCION_PLANTA'),false)
    INTO serv, cli, planta
    FROM negocio.captura_trabajo t
    WHERE (t.organizacion_id,t.sucursal_id,t.orden_derivada_id)=(NEW.organizacion_id,NEW.sucursal_id,NEW.orden_derivada_id);
    IF serv AND planta AND (cli OR NOT r.autorizado) THEN
        SELECT g.manifiesto INTO m FROM negocio.armar_manifiesto_fosa_fija(NEW.organizacion_id,NEW.sucursal_id,r.cliente_id,r.vehiculo_id,true) g;
        INSERT INTO negocio.orden_manifiesto(organizacion_id,sucursal_id,orden_derivada_id,jornada_id,parada_id,cliente_id,vuelta,vehiculo_id,manifiesto_id)
        VALUES(NEW.organizacion_id,NEW.sucursal_id,NEW.orden_derivada_id,NEW.jornada_id,r.parada_id,r.cliente_id,r.vuelta,r.vehiculo_id,m);
    END IF;
    RETURN NEW;
END $$;

-- ---------------------------------------------------------------------------------------------------------------------
-- g) Recepción de capturas: tipos nuevos y validación de forma propia de sanitarios (rechazo 422 = SQLSTATE P0422).
--    Cambios respecto a 0024: (1) las evidencias se guardan ANTES de captura_trabajo para que el disparador ya las vea;
--    (2) tipos nuevos; (3) campos propios de sanitarios. El sobre y su comparación idempotente (bytes exactos) no cambian.
-- ---------------------------------------------------------------------------------------------------------------------
CREATE OR REPLACE FUNCTION sync.recibir_captura(o uuid, a uuid, i uuid, p_solicitud bytea) RETURNS jsonb
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE c record; s jsonb; ctx jsonb; trab jsonb; previo sync.comando%ROWTYPE; cid uuid; disp uuid; tp text;
    ms bigint; cont uuid; ev jsonb; res jsonb; ubic jsonb; hay_foto boolean:=false; recibida timestamptz;
    san jsonb; v_banos integer; v_asig integer; v_incl uuid[]; v_unidad_txt text;
BEGIN
    SELECT * INTO c FROM seguridad.contexto_operador(o,a,i);
    IF NOT FOUND THEN RAISE EXCEPTION 'Contexto no autorizado' USING ERRCODE='42501'; END IF;
    BEGIN s:=convert_from(p_solicitud,'UTF8')::jsonb;
    EXCEPTION WHEN OTHERS THEN RAISE EXCEPTION 'Sobre inválido' USING ERRCODE='22023'; END;
    IF coalesce(jsonb_typeof(s),'')<>'object' OR s->>'version' IS DISTINCT FROM '1' THEN RAISE EXCEPTION 'Versión de sobre no compatible' USING ERRCODE='22023'; END IF;
    cid:=(s->>'id')::uuid;
    PERFORM pg_advisory_xact_lock(hashtextextended(o::text||i::text||cid::text,0));
    SELECT * INTO previo FROM sync.comando WHERE organizacion_id=o AND sucursal_id=c.sucursal_id AND instalacion_app_id=i AND id=cid;
    IF FOUND THEN
        IF previo.solicitud IS DISTINCT FROM p_solicitud THEN
            RAISE EXCEPTION 'La clave ya identifica otro contenido' USING ERRCODE='P0409';
        END IF;
        RETURN previo.resultado;
    END IF;

    -- El autor del sobre debe ser la persona, identidad y asignación de la sesión vigente (sin reatribuir).
    ctx:=s->'contexto';
    IF coalesce(jsonb_typeof(ctx),'')<>'object' OR ctx->>'instalacionId' IS DISTINCT FROM i::text
       OR ctx->>'personaId' IS DISTINCT FROM c.persona_id::text OR ctx->>'identidadId' IS DISTINCT FROM c.identidad_id::text
       OR ctx->>'asignacionEquipoId' IS DISTINCT FROM c.asignacion_id::text
       OR ctx->>'revisionEquipo' IS DISTINCT FROM c.revision_equipo::text THEN
        RAISE EXCEPTION 'El sobre no corresponde al contexto autorizado' USING ERRCODE='42501';
    END IF;
    tp:=s->>'tipo';
    IF tp IS NULL OR tp NOT IN ('SERVICIO_OPERADOR','FIRMA_CLIENTE','RECEPCION_PLANTA','PROBLEMA','SERVICIO_SANITARIOS','TIRADA_SANITARIOS') THEN
        RAISE EXCEPTION 'Tipo no válido' USING ERRCODE='22023';
    END IF;
    trab:=s->'trabajo';
    IF coalesce(jsonb_typeof(trab),'')<>'object' OR coalesce(btrim(trab->>'datosOrdenJson'),'')='' THEN RAISE EXCEPTION 'Falta el trabajo' USING ERRCODE='22023'; END IF;
    ms:=(s->>'fechaEpochMs')::bigint;
    IF ms IS NULL OR ms<=0 OR coalesce(jsonb_typeof(s->'evidencias'),'')<>'array' OR jsonb_array_length(s->'evidencias')=0 THEN RAISE EXCEPTION 'Sobre incompleto' USING ERRCODE='22023'; END IF;
    IF (tp='PROBLEMA')<>coalesce(jsonb_typeof(s->'problema')='object',false)
       OR (tp IN ('PROBLEMA','SERVICIO_SANITARIOS'))=coalesce(jsonb_typeof(s->'aceptado')='object',false)
       OR (tp IN ('RECEPCION_PLANTA','TIRADA_SANITARIOS'))<>coalesce(jsonb_typeof(s->'planta')='object',false) THEN
        RAISE EXCEPTION 'Contenido inconsistente con el tipo' USING ERRCODE='22023';
    END IF;

    -- Reglas de forma de sanitarios (422). Lo que se pueda resolver después (parada, unidad, planta) NO se rechaza aquí: queda para revisión.
    san:=s->'sanitarios';
    IF tp='SERVICIO_SANITARIOS' THEN
        ubic:=s->'evidencias'->0->'ubicacion';
        IF jsonb_array_length(s->'evidencias')<>1 OR s->'evidencias'->0->>'rol' IS DISTINCT FROM 'FOTO' OR coalesce(jsonb_typeof(ubic),'')<>'object'
           OR ubic->>'latitud' IS NULL OR ubic->>'longitud' IS NULL OR ubic->>'precisionMetros' IS NULL OR ubic->>'fechaEpochMs' IS NULL THEN
            RAISE EXCEPTION 'La captura de baños requiere exactamente una foto con ubicación' USING ERRCODE='P0422';
        END IF;
        IF coalesce(jsonb_typeof(san),'')<>'object' OR coalesce(jsonb_typeof(san->'banos'),'')<>'number' OR coalesce(jsonb_typeof(san->'banosAsignados'),'')<>'number'
           OR NOT coalesce(san->>'banos' ~ '^[0-9]{1,9}$', false) OR NOT coalesce(san->>'banosAsignados' ~ '^[0-9]{1,9}$', false) THEN
            RAISE EXCEPTION 'Los baños deben ser enteros no negativos' USING ERRCODE='P0422';
        END IF;
        v_banos:=(san->>'banos')::integer; v_asig:=(san->>'banosAsignados')::integer;
    ELSIF tp='TIRADA_SANITARIOS' THEN
        IF jsonb_array_length(s->'evidencias')<>2
           OR (SELECT count(*) FROM jsonb_array_elements(s->'evidencias') x WHERE x->>'rol'='FIRMA_OPERADOR' AND coalesce(jsonb_typeof(x->'ubicacion'),'null')<>'object')<>1
           OR (SELECT count(*) FROM jsonb_array_elements(s->'evidencias') x WHERE x->>'rol'='FIRMA_RECEPCION' AND coalesce(jsonb_typeof(x->'ubicacion'),'null')<>'object')<>1 THEN
            RAISE EXCEPTION 'La tirada requiere la firma del operador y la de quien recibe' USING ERRCODE='P0422';
        END IF;
        IF char_length(coalesce(btrim(s->>'nombreFirmante'),'')) NOT BETWEEN 1 AND 160 THEN
            RAISE EXCEPTION 'Falta el nombre de quien recibe' USING ERRCODE='P0422';
        END IF;
        IF coalesce(s->'planta'->>'id','') !~ '^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$' THEN
            RAISE EXCEPTION 'Planta no válida' USING ERRCODE='P0422';
        END IF;
        IF coalesce(jsonb_typeof(san),'')<>'object' OR coalesce(jsonb_typeof(san->'capturasIncluidas'),'')<>'array'
           OR EXISTS (SELECT 1 FROM jsonb_array_elements(san->'capturasIncluidas') x
                      WHERE coalesce(jsonb_typeof(x),'')<>'string' OR (x#>>'{}') !~ '^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$') THEN
            RAISE EXCEPTION 'La tirada debe declarar las capturas incluidas' USING ERRCODE='P0422';
        END IF;
        v_incl:=ARRAY(SELECT (x#>>'{}')::uuid FROM jsonb_array_elements(san->'capturasIncluidas') x);
        BEGIN
            v_unidad_txt:=coalesce(((trab->>'datosOrdenJson')::jsonb)->>'unidadId','');
        EXCEPTION WHEN OTHERS THEN
            v_unidad_txt:='';   -- datosOrdenJson que no es JSON: se rechaza igual que una unidad ausente (422, no 500)
        END;
        IF v_unidad_txt !~ '^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$' THEN
            RAISE EXCEPTION 'La tirada debe indicar la unidad' USING ERRCODE='P0422';
        END IF;
    END IF;

    SELECT ae.dispositivo_id INTO disp FROM seguridad.asignacion_equipo ae
    WHERE ae.organizacion_id=o AND ae.sucursal_id=c.sucursal_id AND ae.id=c.asignacion_id;
    INSERT INTO negocio.captura(organizacion_id,sucursal_id,id,asignacion_equipo_id,dispositivo_id,instalacion_app_id,ocurrida_en,resultado_declarado,incidencia_declarada)
    VALUES(o,c.sucursal_id,cid,c.asignacion_id,disp,i,to_timestamp(ms/1000.0),tp,s->'problema'->>'motivo')
    RETURNING recibida_en INTO recibida;

    IF coalesce(jsonb_typeof(s->'aceptado')='object',false) THEN
        -- Bytes exactos del texto aceptado; su redacción y alcance legal siguen pendientes (PG-05).
        INSERT INTO negocio.contenido_aceptado(organizacion_id,sucursal_id,captura_id,formato,version_contrato,contenido)
        VALUES(o,c.sucursal_id,cid,'text/plain; charset=utf-8',s->'aceptado'->>'version',convert_to(s->'aceptado'->>'textoExacto','UTF8'))
        RETURNING id INTO cont;
    END IF;

    -- Evidencias ANTES del trabajo: el disparador de captura_trabajo (manifiesto de sanitarios) necesita ya la coordenada de la foto.
    FOR ev IN SELECT value FROM jsonb_array_elements(s->'evidencias') LOOP
        ubic:=ev->'ubicacion';
        IF (ev->>'rol'='FOTO')<>coalesce(jsonb_typeof(ubic)='object',false) THEN RAISE EXCEPTION 'Cada foto requiere ubicación' USING ERRCODE='22023'; END IF;
        INSERT INTO negocio.evidencia_captura(organizacion_id,sucursal_id,id,captura_id,rol,sha256,tamano_bytes,latitud,longitud,precision_metros,lectura_en)
        VALUES(o,c.sucursal_id,(ev->>'id')::uuid,cid,ev->>'rol',decode(ev->>'sha256','hex'),(ev->>'tamano')::bigint,
            (ubic->>'latitud')::numeric,(ubic->>'longitud')::numeric,(ubic->>'precisionMetros')::numeric,
            CASE WHEN coalesce(jsonb_typeof(ubic),'')<>'object' THEN NULL ELSE to_timestamp((ubic->>'fechaEpochMs')::bigint/1000.0) END);
        IF ev->>'rol'='FOTO' AND NOT hay_foto THEN
            hay_foto:=true;
            INSERT INTO negocio.posicion_captura(organizacion_id,sucursal_id,captura_id,latitud,longitud,precision_metros,antiguedad_segundos,lectura_en)
            VALUES(o,c.sucursal_id,cid,(ubic->>'latitud')::numeric,(ubic->>'longitud')::numeric,(ubic->>'precisionMetros')::numeric,
                greatest(0,(ms-(ubic->>'fechaEpochMs')::bigint)/1000.0),to_timestamp((ubic->>'fechaEpochMs')::bigint/1000.0));
        END IF;
    END LOOP;

    INSERT INTO negocio.captura_trabajo(organizacion_id,sucursal_id,captura_id,tipo,persona_id,identidad_id,orden_derivada_id,jornada_id,
        asignacion_trabajo,revision_trabajo,version_datos,requiere_firma_cliente,datos_orden,contenido_aceptado_id,nombre_firmante,
        planta_id,planta_nombre,problema_motivo,problema_comentario,banos,banos_asignados,capturas_incluidas)
    VALUES(o,c.sucursal_id,cid,tp,c.persona_id,c.identidad_id,(trab->>'ordenId')::uuid,(trab->>'jornadaId')::uuid,
        trab->>'asignacionTrabajoId',trab->>'revisionTrabajo',trab->>'versionDatos',(trab->>'requiereFirmaCliente')::boolean,
        convert_to(trab->>'datosOrdenJson','UTF8'),cont,nullif(btrim(s->>'nombreFirmante'),''),
        s->'planta'->>'id',s->'planta'->>'nombre',s->'problema'->>'motivo',nullif(s->'problema'->>'comentario',''),
        v_banos,v_asig,v_incl);

    res:=jsonb_build_object('estado','recibida','capturaId',cid,'recibidaEn',recibida,
        'evidencias',(SELECT jsonb_agg(jsonb_build_object('id',e.id,'estado','pendiente') ORDER BY e.id)
                      FROM negocio.evidencia_captura e WHERE e.organizacion_id=o AND e.sucursal_id=c.sucursal_id AND e.captura_id=cid));
    INSERT INTO sync.comando(organizacion_id,sucursal_id,instalacion_app_id,id,solicitud,resultado) VALUES(o,c.sucursal_id,i,cid,p_solicitud,res);
    RETURN res;
END $$;

-- Estado de una captura de ESTA instalación. Para baños: si ya entró en una tirada; para tirada: si su manifiesto ya se armó
-- (solo booleano y los ids de capturas que faltan; NUNCA folio ni datos del manifiesto para el operador).
CREATE OR REPLACE FUNCTION sync.consultar_captura(o uuid, a uuid, i uuid, p_captura uuid) RETURNS jsonb
LANGUAGE plpgsql STABLE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE c record; r jsonb; t negocio.captura_trabajo%ROWTYPE; armado boolean; faltan jsonb;
BEGIN
    SELECT * INTO c FROM seguridad.contexto_operador(o,a,i);
    IF NOT FOUND THEN RAISE EXCEPTION 'Contexto no autorizado' USING ERRCODE='42501'; END IF;
    SELECT jsonb_build_object('estado','recibida','capturaId',k.id,'recibidaEn',k.recibida_en,
        'evidencias',(SELECT jsonb_agg(jsonb_build_object('id',e.id,'estado',CASE WHEN e.archivo_id IS NULL THEN 'pendiente' ELSE 'recibida' END) ORDER BY e.id)
                      FROM negocio.evidencia_captura e WHERE (e.organizacion_id,e.sucursal_id,e.captura_id)=(k.organizacion_id,k.sucursal_id,k.id)))
    INTO r
    FROM negocio.captura k
    WHERE k.organizacion_id=o AND k.sucursal_id=c.sucursal_id AND k.id=p_captura AND k.instalacion_app_id=i;
    IF r IS NULL THEN RETURN NULL; END IF;
    SELECT * INTO t FROM negocio.captura_trabajo WHERE (organizacion_id,sucursal_id,captura_id)=(o,c.sucursal_id,p_captura);
    IF t.tipo='SERVICIO_SANITARIOS' THEN
        r:=r || jsonb_build_object(
            'incluidaEnTirada', EXISTS (SELECT 1 FROM negocio.tirada_parada tp WHERE (tp.organizacion_id,tp.sucursal_id,tp.captura_id)=(o,c.sucursal_id,p_captura)),
            'enRevision', EXISTS (SELECT 1 FROM negocio.captura_por_revisar cr WHERE (cr.organizacion_id,cr.sucursal_id,cr.captura_id)=(o,c.sucursal_id,p_captura)));
    ELSIF t.tipo='TIRADA_SANITARIOS' THEN
        armado:=EXISTS (SELECT 1 FROM negocio.tirada ti JOIN negocio.tirada_manifiesto tm
                        ON (tm.organizacion_id,tm.sucursal_id,tm.tirada_id)=(ti.organizacion_id,ti.sucursal_id,ti.id)
                        WHERE (ti.organizacion_id,ti.sucursal_id,ti.captura_id)=(o,c.sucursal_id,p_captura));
        SELECT coalesce(jsonb_agg(e.captura_id ORDER BY e.captura_id),'[]'::jsonb) INTO faltan
        FROM negocio.tirada ti JOIN negocio.tirada_esperada e ON (e.organizacion_id,e.sucursal_id,e.tirada_id)=(ti.organizacion_id,ti.sucursal_id,ti.id)
        WHERE (ti.organizacion_id,ti.sucursal_id,ti.captura_id)=(o,c.sucursal_id,p_captura)
          AND NOT EXISTS (SELECT 1 FROM negocio.succion_parada sp WHERE (sp.organizacion_id,sp.sucursal_id,sp.captura_id)=(e.organizacion_id,e.sucursal_id,e.captura_id));
        r:=r || jsonb_build_object('manifiestoArmado',armado,'capturasFaltantes',CASE WHEN armado THEN '[]'::jsonb ELSE faltan END,
            'enRevision', EXISTS (SELECT 1 FROM negocio.captura_por_revisar cr WHERE (cr.organizacion_id,cr.sucursal_id,cr.captura_id)=(o,c.sucursal_id,p_captura)));
    END IF;
    RETURN r;
END $$;

-- ---------------------------------------------------------------------------------------------------------------------
-- h) Archivos: la foto de un PROBLEMA de sanitarios crea la incidencia con servicio 'sanitario' (parada y baños asignados, sin cliente).
-- ---------------------------------------------------------------------------------------------------------------------
CREATE OR REPLACE FUNCTION sync.registrar_archivo(o uuid, a uuid, i uuid, p_evidencia uuid, p_mime text, p_clave text) RETURNS jsonb
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE c record; e negocio.evidencia_captura%ROWTYPE; k negocio.captura%ROWTYPE; t negocio.captura_trabajo%ROWTYPE;
    r record; ps record; ruta uuid; motivo text;
BEGIN
    SELECT * INTO c FROM seguridad.contexto_operador(o,a,i);
    IF NOT FOUND THEN RAISE EXCEPTION 'Contexto no autorizado' USING ERRCODE='42501'; END IF;
    IF coalesce(btrim(p_mime),'')='' OR coalesce(btrim(p_clave),'')='' THEN RAISE EXCEPTION 'Archivo incompleto' USING ERRCODE='22023'; END IF;
    SELECT x.* INTO e FROM negocio.evidencia_captura x
    JOIN negocio.captura kk ON (kk.organizacion_id,kk.sucursal_id,kk.id)=(x.organizacion_id,x.sucursal_id,x.captura_id)
    WHERE x.organizacion_id=o AND x.sucursal_id=c.sucursal_id AND x.id=p_evidencia AND kk.instalacion_app_id=i
    FOR UPDATE OF x;
    IF NOT FOUND THEN RETURN NULL; END IF;
    IF e.archivo_id IS NOT NULL THEN RETURN jsonb_build_object('estado','recibida','id',e.id); END IF;

    INSERT INTO negocio.archivo(organizacion_id,sucursal_id,id,sha256,tamano_bytes,mime) VALUES(o,c.sucursal_id,e.id,e.sha256,e.tamano_bytes,p_mime);
    INSERT INTO negocio.ubicacion_archivo(organizacion_id,sucursal_id,archivo_id,proveedor,clave_externa,verificada_en)
    VALUES(o,c.sucursal_id,e.id,'vps',p_clave,clock_timestamp());
    UPDATE negocio.evidencia_captura SET archivo_id=e.id, recibida_en=clock_timestamp()
    WHERE organizacion_id=o AND sucursal_id=c.sucursal_id AND id=e.id;
    INSERT INTO sync.tarea(organizacion_id,sucursal_id,clave,tipo,datos)
    VALUES(o,c.sucursal_id,'drive:'||e.id::text,'subir_drive',jsonb_build_object('archivoId',e.id)) ON CONFLICT DO NOTHING;

    SELECT * INTO t FROM negocio.captura_trabajo WHERE (organizacion_id,sucursal_id,captura_id)=(o,c.sucursal_id,e.captura_id);
    IF t.tipo='PROBLEMA' AND e.rol='FOTO' THEN
        SELECT * INTO k FROM negocio.captura WHERE (organizacion_id,sucursal_id,id)=(o,c.sucursal_id,e.captura_id);
        motivo:=CASE WHEN t.problema_motivo IN ('sin_acceso','cliente_ausente','unidad_danada','servicio_parcial','otro') THEN t.problema_motivo ELSE 'otro' END;
        SELECT * INTO ps FROM negocio.resolver_parada_derivada(o,c.sucursal_id,t.persona_id,t.jornada_id,t.orden_derivada_id,k.ocurrida_en);
        IF FOUND THEN
            -- Parada de sanitarios: sin cliente; los baños asignados salen del plan vigente a la fecha de la captura.
            INSERT INTO negocio.incidencia(organizacion_id,sucursal_id,servicio,motivo,descripcion,ocurrida_en,ruta_id,parada_id,cliente_id,vehiculo_id,persona_id,
                banos_asignados,evidencia_archivo_id,latitud,longitud,precision_m)
            VALUES(o,c.sucursal_id,'sanitario',motivo,t.problema_comentario,k.ocurrida_en,ps.ruta_id,ps.parada_id,NULL,ps.vehiculo_id,t.persona_id,
                (SELECT coalesce(sum((x->>'banos')::integer),0) FROM jsonb_array_elements(negocio.clientes_parada_en(o,c.sucursal_id,ps.parada_id,k.ocurrida_en)) x),
                e.id,round(e.latitud,6),round(e.longitud,6),round(e.precision_metros,1));
        ELSE
            SELECT * INTO r FROM negocio.resolver_orden_derivada(o,c.sucursal_id,t.persona_id,t.jornada_id,t.orden_derivada_id);
            SELECT j.ruta_id INTO ruta FROM negocio.jornada j WHERE (j.organizacion_id,j.sucursal_id,j.id)=(o,c.sucursal_id,t.jornada_id);
            INSERT INTO negocio.incidencia(organizacion_id,sucursal_id,servicio,motivo,descripcion,ocurrida_en,ruta_id,parada_id,cliente_id,vehiculo_id,persona_id,
                evidencia_archivo_id,latitud,longitud,precision_m)
            VALUES(o,c.sucursal_id,'fosa',motivo,t.problema_comentario,k.ocurrida_en,ruta,r.parada_id,r.cliente_id,r.vehiculo_id,t.persona_id,
                e.id,round(e.latitud,6),round(e.longitud,6),round(e.precision_metros,1));
        END IF;
    END IF;
    RETURN jsonb_build_object('estado','recibida','id',e.id);
END $$;

-- ---------------------------------------------------------------------------------------------------------------------
-- i) Trabajo del operador (GET /api/movil/trabajo): campos ADITIVOS. jornada.servicio ('sanitarios'|'fosas'; unidad sin clasificar = fosas),
--    unidad.clasificacion y unidad.carga (solo sanitarios; informativa), parada.banosAsignados. En sanitarios no hay órdenes derivadas:
--    fosas = 0 y ordenes = [] (cumple ordenes.length == fosas). Lo demás queda igual que en 0027.
-- ---------------------------------------------------------------------------------------------------------------------
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
               veh.id AS vehiculo_id, coalesce(fv.numero_economico,veh.numero_economico) AS economico, cap.valor_m3,
               fv.clasificacion, CASE WHEN fv.clasificacion='sanitarios' THEN 'sanitarios' ELSE 'fosas' END AS servicio,
               cg.banos AS carga_banos, cg.m3 AS carga_m3, cg.porcentaje AS carga_pct
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
        LEFT JOIN LATERAL (SELECT g.* FROM negocio.carga_unidad_interna(o,c.sucursal_id,veh.id) g WHERE fv.clasificacion='sanitarios') cg ON true
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
            'servicio', jo.servicio,
            'unidad', jsonb_build_object('id', jo.vehiculo_id, 'numeroEconomico', jo.economico, 'capacidadM3', jo.valor_m3, 'clasificacion', jo.clasificacion)
                || CASE WHEN jo.servicio='sanitarios' THEN jsonb_build_object('carga', jsonb_build_object('banos', jo.carga_banos, 'm3', jo.carga_m3, 'capacidadM3', jo.valor_m3, 'porcentaje', jo.carga_pct)) ELSE '{}'::jsonb END,
            'paradas', coalesce((
                SELECT jsonb_agg(jsonb_build_object(
                    'paradaId', p.parada_id, 'tipo', p.tipo, 'sitio', p.sitio, 'direccion', p.direccion,
                    'latitud', p.latitud, 'longitud', p.longitud,
                    'banosAsignados', CASE WHEN jo.servicio='sanitarios' THEN (SELECT coalesce(sum(k.banos),0)::integer FROM clientes k WHERE k.parada_id=p.parada_id AND k.tipo=p.tipo) ELSE 0 END,
                    'clientes', coalesce((
                        SELECT jsonb_agg(jsonb_build_object(
                            'clienteId', k.cliente_id, 'nombre', k.nombre, 'autorizado', k.autorizado,
                            -- Cliente general: fosa nuestra y generador nuestra empresa (sin firma del cliente).
                            'fosaPropia', NOT k.autorizado, 'banos', k.banos, 'fosas', CASE WHEN jo.servicio='sanitarios' THEN 0 ELSE k.fosas END,
                            'ordenes', coalesce((
                                SELECT jsonb_agg(jsonb_build_object(
                                    'ordenId', md5(jo.jornada_id::text||':'||p.parada_id::text||':'||k.cliente_id::text||':'||n)::uuid,
                                    'vuelta', n) ORDER BY n)
                                FROM generate_series(1, CASE WHEN jo.servicio='sanitarios' THEN 0 ELSE k.fosas END) n), '[]'::jsonb)
                        ) ORDER BY k.nombre, k.cliente_id)
                        FROM clientes k WHERE k.parada_id=p.parada_id AND k.tipo=p.tipo), '[]'::jsonb)
                ) ORDER BY p.tipo, p.sitio, p.parada_id)
                FROM paradas p WHERE p.jornada_id=jo.jornada_id), '[]'::jsonb)
        ) AS dato
        FROM jornadas jo
    ) j;
    RETURN r;
END $$;
