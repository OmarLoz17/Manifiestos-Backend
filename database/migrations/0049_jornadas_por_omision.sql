-- Las rutas fijas ya están programadas por omisión (decisión del 9-nov-2026) y desde el 18-nov-2026 no existe el estado «borrador».
-- Pero trabajo_operador solo ve rutas con fila en negocio.jornada: un día sin ningún cambio de Operaciones dejaba al teléfono sin ruta.
-- Esta función crea, al pedir el trabajo de un día, la jornada (y su revisión 1 «programada») de las rutas de la sucursal del operador
-- que salen ese día de la semana y tienen al menos una parada vigente. Es idempotente y no toca jornadas existentes (p. ej. canceladas).
CREATE OR REPLACE FUNCTION seguridad.asegurar_jornadas_del_dia(o uuid, a uuid, i uuid, f date) RETURNS void
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE c record;
BEGIN
    IF f IS NULL OR f < ((now() AT TIME ZONE 'UTC')::date - 1) OR f > ((now() AT TIME ZONE 'UTC')::date + 1) THEN RETURN; END IF;
    SELECT * INTO c FROM seguridad.contexto_operador(o,a,i);
    IF NOT FOUND THEN RETURN; END IF;
    WITH nuevas AS (
        INSERT INTO negocio.jornada (organizacion_id, sucursal_id, id, ruta_id, fecha)
        SELECT ru.organizacion_id, ru.sucursal_id, gen_random_uuid(), ru.id, f
        FROM negocio.ruta ru
        JOIN LATERAL (SELECT x.dias FROM negocio.ruta_version x
            WHERE (x.organizacion_id,x.sucursal_id,x.ruta_id)=(ru.organizacion_id,ru.sucursal_id,ru.id)
            ORDER BY x.revision DESC LIMIT 1) rv ON true
        WHERE ru.organizacion_id = o AND ru.sucursal_id = c.sucursal_id
          AND (rv.dias & (1 << (extract(isodow FROM f)::int - 1))) <> 0
          AND EXISTS (SELECT 1 FROM negocio.parada p WHERE (p.organizacion_id,p.sucursal_id,p.ruta_id)=(ru.organizacion_id,ru.sucursal_id,ru.id)
                      AND NOT EXISTS (SELECT 1 FROM negocio.parada_baja pb WHERE (pb.organizacion_id,pb.sucursal_id,pb.parada_id)=(p.organizacion_id,p.sucursal_id,p.id)))
        ON CONFLICT (organizacion_id, sucursal_id, ruta_id, fecha) DO NOTHING
        RETURNING organizacion_id, sucursal_id, id)
    INSERT INTO negocio.estado_jornada (organizacion_id, sucursal_id, jornada_id, revision, estado)
    SELECT organizacion_id, sucursal_id, id, 1, 'programada' FROM nuevas;
END $$;
GRANT EXECUTE ON FUNCTION seguridad.asegurar_jornadas_del_dia(uuid, uuid, uuid, date) TO manifiestos_app;
