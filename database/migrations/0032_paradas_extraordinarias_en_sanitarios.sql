-- 12-nov-2026: las rutas de SANITARIOS admiten paradas extraordinarias (solo de ese día, como en fosas).
--   * La captura de baños, la tirada y el manifiesto referencian la parada: ahora puede ser una parada del plan (negocio.parada)
--     o una parada extraordinaria (negocio.parada_extraordinaria, misma ruta y fecha de la jornada). Las llaves foráneas a
--     negocio.parada de succion_parada, manifiesto_parada e incidencia se sustituyen por una comprobación al insertar que acepta
--     cualquiera de las dos. (Corrige además el reporte de problema sobre una parada extraordinaria de fosas.)
--   * resolver_parada_derivada y clientes_parada_en resuelven también la parada extraordinaria.
--   Solo ALTER de restricciones + CREATE OR REPLACE: no se editan las migraciones anteriores.

ALTER TABLE negocio.succion_parada    DROP CONSTRAINT succion_parada_organizacion_id_sucursal_id_parada_id_fkey;
ALTER TABLE negocio.manifiesto_parada DROP CONSTRAINT manifiesto_parada_organizacion_id_sucursal_id_parada_id_fkey;
ALTER TABLE negocio.incidencia        DROP CONSTRAINT incidencia_organizacion_id_sucursal_id_parada_id_fkey;

CREATE FUNCTION infra.parada_o_extraordinaria() RETURNS trigger
LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog AS $$
BEGIN
    IF NEW.parada_id IS NOT NULL
       AND NOT EXISTS (SELECT 1 FROM negocio.parada p WHERE (p.organizacion_id, p.sucursal_id, p.id) = (NEW.organizacion_id, NEW.sucursal_id, NEW.parada_id))
       AND NOT EXISTS (SELECT 1 FROM negocio.parada_extraordinaria e WHERE (e.organizacion_id, e.sucursal_id, e.id) = (NEW.organizacion_id, NEW.sucursal_id, NEW.parada_id))
    THEN
        RAISE EXCEPTION 'La parada % no existe (ni en el plan ni como extraordinaria).', NEW.parada_id USING ERRCODE = '23503';
    END IF;
    RETURN NEW;
END $$;
REVOKE ALL ON FUNCTION infra.parada_o_extraordinaria() FROM PUBLIC;

CREATE TRIGGER parada_existe BEFORE INSERT ON negocio.succion_parada    FOR EACH ROW EXECUTE FUNCTION infra.parada_o_extraordinaria();
CREATE TRIGGER parada_existe BEFORE INSERT ON negocio.manifiesto_parada FOR EACH ROW EXECUTE FUNCTION infra.parada_o_extraordinaria();
CREATE TRIGGER parada_existe BEFORE INSERT ON negocio.incidencia        FOR EACH ROW EXECUTE FUNCTION infra.parada_o_extraordinaria();

-- Parada de la jornada (del plan o extraordinaria de la fecha de la jornada) de una unidad de sanitarios operada por la persona.
CREATE OR REPLACE FUNCTION negocio.resolver_parada_derivada(o uuid, s uuid, p_persona uuid, p_jornada uuid, p_parada uuid, p_fecha timestamptz)
RETURNS TABLE (parada_id uuid, sitio_id uuid, ruta_id uuid, vehiculo_id uuid)
LANGUAGE sql STABLE SECURITY DEFINER SET search_path = pg_catalog AS $$
    SELECT c.parada_id, c.sitio_id, j.ruta_id, rv.vehiculo_id
    FROM negocio.jornada j
    JOIN LATERAL negocio.ruta_version_en(o, s, j.ruta_id, j.fecha) rv ON true
    JOIN LATERAL (SELECT x.clasificacion FROM negocio.ficha_vehiculo x
        WHERE (x.organizacion_id, x.sucursal_id, x.vehiculo_id) = (o, s, rv.vehiculo_id)
        ORDER BY x.revision DESC LIMIT 1) f ON f.clasificacion = 'sanitarios'
    JOIN LATERAL (
        SELECT pa.id AS parada_id, pv.sitio_id
        FROM negocio.parada pa
        JOIN LATERAL (SELECT x.sitio_id FROM negocio.parada_version x
            WHERE (x.organizacion_id, x.sucursal_id, x.parada_id) = (pa.organizacion_id, pa.sucursal_id, pa.id)
            ORDER BY x.revision DESC LIMIT 1) pv ON true
        WHERE (pa.organizacion_id, pa.sucursal_id, pa.ruta_id) = (j.organizacion_id, j.sucursal_id, j.ruta_id) AND pa.id = p_parada
        UNION ALL
        SELECT pe.id, pe.sitio_id
        FROM negocio.parada_extraordinaria pe
        WHERE (pe.organizacion_id, pe.sucursal_id, pe.ruta_id) = (j.organizacion_id, j.sucursal_id, j.ruta_id) AND pe.fecha = j.fecha AND pe.id = p_parada
    ) c ON true
    WHERE j.organizacion_id = o AND j.sucursal_id = s AND j.id = p_jornada
      AND negocio.persona_opera_unidad(o, s, rv.vehiculo_id, p_persona, p_fecha)
$$;

-- Clientes de la parada y sus baños planeados: revisión vigente a la fecha (plan) o los de la parada extraordinaria.
CREATE OR REPLACE FUNCTION negocio.clientes_parada_en(o uuid, s uuid, p_parada uuid, p_fecha timestamptz) RETURNS jsonb
LANGUAGE sql STABLE SECURITY DEFINER SET search_path = pg_catalog AS $$
    WITH pv AS (
        SELECT x.id FROM negocio.parada_version x
        WHERE (x.organizacion_id, x.sucursal_id, x.parada_id) = (o, s, p_parada)
        ORDER BY (x.registrada_en <= p_fecha + interval '2 minutes') DESC,
                 CASE WHEN x.registrada_en <= p_fecha + interval '2 minutes' THEN x.revision END DESC NULLS LAST, x.revision
        LIMIT 1), base AS (
        SELECT pc.cliente_id, coalesce(pc.banos_planeados, 0) AS banos
        FROM pv JOIN negocio.parada_cliente pc ON (pc.organizacion_id, pc.sucursal_id, pc.version_id) = (o, s, pv.id)
        UNION ALL
        SELECT pc.cliente_id, coalesce(pc.banos_planeados, 0)
        FROM negocio.parada_extraordinaria_cliente pc
        WHERE (pc.organizacion_id, pc.sucursal_id, pc.parada_extraordinaria_id) = (o, s, p_parada)
    ), k AS (
        SELECT b.cliente_id, b.banos,
               coalesce(nullif(btrim(fe.nombre_comercial), ''), nullif(btrim(fe.nombre_fiscal), ''), nullif(btrim(cl.codigo_comercial), ''), 'Cliente sin nombre') AS nombre
        FROM base b
        JOIN negocio.cliente cl ON (cl.organizacion_id, cl.sucursal_id, cl.id) = (o, s, b.cliente_id)
        LEFT JOIN LATERAL (SELECT x.nombre_fiscal, x.nombre_comercial FROM negocio.ficha_empresa x
            WHERE (x.organizacion_id, x.empresa_id) = (cl.organizacion_id, cl.empresa_id) ORDER BY x.revision DESC LIMIT 1) fe ON true)
    SELECT coalesce(jsonb_agg(jsonb_build_object('clienteId', k.cliente_id, 'nombre', k.nombre, 'banos', k.banos) ORDER BY k.nombre, k.cliente_id), '[]'::jsonb) FROM k
$$;
