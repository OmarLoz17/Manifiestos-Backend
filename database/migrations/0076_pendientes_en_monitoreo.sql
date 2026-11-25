-- Pendientes de ayer en el panel (decisión 0045) y corrección de la carga de fosas. Aditivo.
--   * La carga por tirar de fosas y el armado asignado por Operaciones usan la unidad en que se cuenta un pendiente (la del operador hoy),
--     no la de la ruta de ayer. Lo del mismo día no cambia.
--   * seguridad.pendientes_ayer_panel: lo pendiente de ayer de todos los operadores, lo hecho hoy y lo marcado «no se hará».

DO $migracion$
DECLARE d text; n text;
BEGIN
    d := pg_get_functiondef('negocio.carga_fosas_interna(uuid,uuid,uuid)'::regprocedure);
    n := replace(d, 'rv ON rv.vehiculo_id = p_vehiculo', 'rv ON true');
    IF n = d THEN RAISE EXCEPTION 'No se encontró la unidad en carga_fosas_interna'; END IF;
    d := n;
    n := replace(d, 'WHERE ct.organizacion_id = o AND ct.sucursal_id = s AND ct.tipo IN',
        'WHERE CASE WHEN (k.ocurrida_en AT TIME ZONE ''America/Monterrey'')::date > j.fecha
                    THEN coalesce(negocio.unidad_para_pendiente(ct.organizacion_id, ct.sucursal_id, ct.persona_id, ct.jornada_id, ct.orden_derivada_id, k.ocurrida_en), rv.vehiculo_id)
                    ELSE rv.vehiculo_id END = p_vehiculo
          AND ct.organizacion_id = o AND ct.sucursal_id = s AND ct.tipo IN');
    IF n = d THEN RAISE EXCEPTION 'No se encontró el filtro en carga_fosas_interna'; END IF;
    EXECUTE n;

    d := pg_get_functiondef('negocio.armar_orden_asignada(uuid,uuid,uuid,uuid)'::regprocedure);
    n := replace(d, 'IF veh IS NULL OR NOT negocio.orden_con_evidencias_completas',
        'veh := coalesce((SELECT negocio.unidad_para_pendiente(o, s, t.persona_id, p_jornada, p_orden, serv_en) FROM negocio.captura_trabajo_efectiva t
                         WHERE (t.organizacion_id, t.sucursal_id, t.orden_derivada_id) = (o, s, p_orden) AND t.tipo = ''SERVICIO_OPERADOR'' LIMIT 1), veh);
    IF veh IS NULL OR NOT negocio.orden_con_evidencias_completas');
    IF n = d THEN RAISE EXCEPTION 'No se encontró la unidad en armar_orden_asignada'; END IF;
    EXECUTE n;
END
$migracion$;

CREATE FUNCTION seguridad.pendientes_ayer_panel(o uuid, p_hoy date)
RETURNS TABLE (sucursal_id uuid, ruta_id uuid, ruta text, operador text, unidad text, parada_id uuid, sitio text, clientes text, servicio text,
               banos integer, banos_hechos boolean, ordenes integer, ordenes_hechas integer, no_se_hara boolean)
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path = pg_catalog AS $$
#variable_conflict use_column
DECLARE ayer date := p_hoy - 1; corte timestamptz := (p_hoy::timestamp AT TIME ZONE 'America/Monterrey');
BEGIN
    -- Si nadie abrió la app ayer, la jornada de las rutas que salían ayer puede no existir (misma regla que pendientes_de_ayer).
    WITH nuevas AS (
        INSERT INTO negocio.jornada (organizacion_id, sucursal_id, id, ruta_id, fecha)
        SELECT ru.organizacion_id, ru.sucursal_id, gen_random_uuid(), ru.id, ayer
        FROM negocio.ruta ru
        JOIN LATERAL (SELECT x.dias FROM negocio.ruta_version x
            WHERE (x.organizacion_id, x.sucursal_id, x.ruta_id) = (ru.organizacion_id, ru.sucursal_id, ru.id) AND x.registrada_en < corte
            ORDER BY x.revision DESC LIMIT 1) rv ON true
        WHERE ru.organizacion_id = o AND coalesce(seguridad.puede(o, ru.sucursal_id, 'datos.leer'), false)
          AND (rv.dias & (1 << (extract(isodow FROM ayer)::int - 1))) <> 0
          AND EXISTS (SELECT 1 FROM negocio.parada p WHERE (p.organizacion_id, p.sucursal_id, p.ruta_id) = (ru.organizacion_id, ru.sucursal_id, ru.id) AND p.creada_en < corte
                      AND NOT EXISTS (SELECT 1 FROM negocio.parada_baja pb WHERE (pb.organizacion_id, pb.sucursal_id, pb.parada_id) = (p.organizacion_id, p.sucursal_id, p.id) AND pb.dada_de_baja_en < corte))
        ON CONFLICT (organizacion_id, sucursal_id, ruta_id, fecha) DO NOTHING
        RETURNING organizacion_id, sucursal_id, id)
    INSERT INTO negocio.estado_jornada (organizacion_id, sucursal_id, jornada_id, revision, estado)
    SELECT organizacion_id, sucursal_id, id, 1, 'programada' FROM nuevas;

    RETURN QUERY
    WITH jornadas AS (
        SELECT j.sucursal_id, j.id AS jornada_id, j.ruta_id, rv.nombre, coalesce(cu.persona_id, negocio.persona_de_unidad_en(o, j.sucursal_id, coalesce(cu.vehiculo_id, rv.vehiculo_id), corte)) AS persona_id
        FROM negocio.jornada j
        JOIN LATERAL (SELECT x.estado FROM negocio.estado_jornada x WHERE (x.organizacion_id, x.sucursal_id, x.jornada_id) = (o, j.sucursal_id, j.id)
            ORDER BY x.revision DESC LIMIT 1) ej ON ej.estado IN ('programada', 'en_curso', 'cancelada')
        JOIN LATERAL (SELECT x.nombre, x.vehiculo_id FROM negocio.ruta_version x
            WHERE (x.organizacion_id, x.sucursal_id, x.ruta_id) = (o, j.sucursal_id, j.ruta_id) AND x.registrada_en < corte
            ORDER BY x.revision DESC LIMIT 1) rv ON true
        LEFT JOIN LATERAL negocio.cambio_unidad_en(o, j.sucursal_id, j.ruta_id, j.fecha) cu ON true
        WHERE j.organizacion_id = o AND j.fecha = ayer AND coalesce(seguridad.puede(o, j.sucursal_id, 'datos.leer'), false)
    ), operadores AS (
        -- Solo quien tiene la función encendida (su cuenta se toma de la última entrega de equipo a esa persona).
        SELECT x.* FROM jornadas x
        WHERE x.persona_id IS NOT NULL AND seguridad.pendientes_ayer_activo(o,
            (SELECT ae.acceso_id FROM seguridad.asignacion_equipo ae WHERE ae.organizacion_id = o AND ae.persona_id = x.persona_id ORDER BY ae.desde DESC, ae.revision DESC LIMIT 1))
    ), planeado AS (
        SELECT jo.sucursal_id, jo.jornada_id, jo.ruta_id, jo.nombre, jo.persona_id, pa.id AS parada_id, pa.posicion, pv.sitio_id, pc.cliente_id,
               EXISTS (SELECT 1 FROM negocio.cliente cl JOIN negocio.autorizacion_empresa au ON (au.organizacion_id, au.empresa_id) = (cl.organizacion_id, cl.empresa_id)
                       WHERE (cl.organizacion_id, cl.sucursal_id, cl.id) = (o, jo.sucursal_id, pc.cliente_id) AND au.autoridad = 'Medio ambiente' AND au.finalidad = 'Generador') AS autorizado,
               coalesce((SELECT aj.banos FROM negocio.ajuste_banos_dia aj WHERE (aj.organizacion_id, aj.sucursal_id, aj.parada_id, aj.cliente_id, aj.fecha) = (o, jo.sucursal_id, pa.id, pc.cliente_id, ayer)
                         ORDER BY aj.revision DESC LIMIT 1), pc.banos_planeados, 0) AS banos,
               coalesce((SELECT af.fosas FROM negocio.ajuste_fosas_dia af WHERE (af.organizacion_id, af.sucursal_id, af.parada_id, af.cliente_id, af.fecha) = (o, jo.sucursal_id, pa.id, pc.cliente_id, ayer)
                         ORDER BY af.revision DESC LIMIT 1), pc.fosas_planeadas, 0) AS fosas
        FROM operadores jo
        JOIN negocio.parada pa ON (pa.organizacion_id, pa.sucursal_id, pa.ruta_id) = (o, jo.sucursal_id, jo.ruta_id)
            AND NOT EXISTS (SELECT 1 FROM negocio.parada_baja pb WHERE (pb.organizacion_id, pb.sucursal_id, pb.parada_id) = (o, jo.sucursal_id, pa.id) AND pb.dada_de_baja_en < corte)
        JOIN LATERAL (SELECT x.id, x.sitio_id FROM negocio.parada_version x
            WHERE (x.organizacion_id, x.sucursal_id, x.parada_id) = (o, jo.sucursal_id, pa.id) AND x.registrada_en < corte
            ORDER BY x.revision DESC LIMIT 1) pv ON true
        JOIN negocio.parada_cliente pc ON (pc.organizacion_id, pc.sucursal_id, pc.version_id) = (o, jo.sucursal_id, pv.id)
    ), fosas AS (
        -- Órdenes de fosa (solo clientes con convenio), una por una, con cuándo se hizo cada una.
        SELECT p.sucursal_id, p.jornada_id, p.parada_id, p.cliente_id,
               (SELECT min(k.ocurrida_en) FROM negocio.captura_trabajo_efectiva t JOIN negocio.captura k ON (k.organizacion_id, k.sucursal_id, k.id) = (t.organizacion_id, t.sucursal_id, t.captura_id)
                WHERE (t.organizacion_id, t.sucursal_id, t.jornada_id) = (o, p.sucursal_id, p.jornada_id) AND t.tipo = 'SERVICIO_OPERADOR'
                  AND t.orden_derivada_id = md5(p.jornada_id::text || ':' || p.parada_id::text || ':' || p.cliente_id::text || ':' || g)::uuid) AS hecha_en
        FROM planeado p CROSS JOIN LATERAL generate_series(1, CASE WHEN p.autorizado THEN p.fosas ELSE 0 END) g
    ), de_fosas AS (
        SELECT f.sucursal_id, f.jornada_id, f.parada_id,
               count(*) FILTER (WHERE f.hecha_en IS NULL OR f.hecha_en >= corte)::integer AS ordenes,
               count(*) FILTER (WHERE f.hecha_en >= corte)::integer AS hechas
        FROM fosas f GROUP BY f.sucursal_id, f.jornada_id, f.parada_id
    ), de_banos AS (
        -- Sanitarios: clientes generales (baños y fosas propias, que viajan como capturas de la parada).
        SELECT p.sucursal_id, p.jornada_id, p.parada_id, sum(p.banos)::integer AS banos, sum(p.fosas)::integer AS fosas,
               c.banos_en, coalesce(c.fosas_antes, 0) AS fosas_antes, coalesce(c.fosas_hoy, 0) AS fosas_hoy
        FROM planeado p
        LEFT JOIN LATERAL (
            SELECT min(k.ocurrida_en) FILTER (WHERE NOT coalesce(t.fosa, false)) AS banos_en,
                   count(*) FILTER (WHERE t.fosa AND k.ocurrida_en < corte)::integer AS fosas_antes,
                   count(*) FILTER (WHERE t.fosa AND k.ocurrida_en >= corte)::integer AS fosas_hoy
            FROM negocio.captura_trabajo_efectiva t JOIN negocio.captura k ON (k.organizacion_id, k.sucursal_id, k.id) = (t.organizacion_id, t.sucursal_id, t.captura_id)
            WHERE (t.organizacion_id, t.sucursal_id, t.jornada_id, t.orden_derivada_id) = (o, p.sucursal_id, p.jornada_id, p.parada_id) AND t.tipo = 'SERVICIO_SANITARIOS'
        ) c ON true
        WHERE NOT p.autorizado AND (p.banos > 0 OR p.fosas > 0)
        GROUP BY p.sucursal_id, p.jornada_id, p.parada_id, c.banos_en, c.fosas_antes, c.fosas_hoy
    ), items AS (
        SELECT f.sucursal_id, f.jornada_id, f.parada_id, 'fosas'::text AS servicio, 0 AS banos, false AS banos_hechos, f.ordenes, f.hechas AS ordenes_hechas, true AS convenio
        FROM de_fosas f WHERE f.ordenes > 0
        UNION ALL
        SELECT b.sucursal_id, b.jornada_id, b.parada_id, 'sanitarios',
               CASE WHEN b.banos_en IS NOT NULL AND b.banos_en < corte THEN 0 ELSE b.banos END,
               coalesce(b.banos_en >= corte, false), greatest(b.fosas - b.fosas_antes, 0), b.fosas_hoy, false
        FROM de_banos b
        WHERE (b.banos > 0 AND (b.banos_en IS NULL OR b.banos_en >= corte)) OR b.fosas > b.fosas_antes
    )
    SELECT p.sucursal_id, p.ruta_id, p.nombre,
           coalesce((SELECT x.nombre_completo FROM seguridad.persona x WHERE (x.organizacion_id, x.id) = (o, p.persona_id)), ''),
           (SELECT coalesce(fv.numero_economico, v.numero_economico) FROM negocio.vehiculo v
            LEFT JOIN LATERAL (SELECT x.numero_economico FROM negocio.ficha_vehiculo x WHERE (x.organizacion_id, x.sucursal_id, x.vehiculo_id) = (o, p.sucursal_id, v.id) ORDER BY x.revision DESC LIMIT 1) fv ON true
            WHERE (v.organizacion_id, v.sucursal_id, v.id) = (o, p.sucursal_id, negocio.unidad_de_persona_en(o, p.sucursal_id, p.persona_id, clock_timestamp()))),
           p.parada_id,
           (SELECT coalesce(nullif(btrim(fs.nombre), ''), s.referencia) FROM negocio.sitio s
            LEFT JOIN LATERAL (SELECT x.nombre FROM negocio.ficha_sitio x WHERE (x.organizacion_id, x.sucursal_id, x.sitio_id) = (o, p.sucursal_id, s.id) ORDER BY x.revision DESC LIMIT 1) fs ON true
            WHERE (s.organizacion_id, s.sucursal_id, s.id) = (o, p.sucursal_id, p.sitio_id)),
           string_agg(coalesce(nullif(btrim(fe.nombre_comercial), ''), nullif(btrim(fe.nombre_fiscal), ''), nullif(btrim(cl.codigo_comercial), ''), 'Cliente sin nombre'), ', ' ORDER BY cl.codigo_comercial),
           i.servicio, i.banos, i.banos_hechos, i.ordenes, i.ordenes_hechas,
           coalesce((SELECT d.activo FROM negocio.pendiente_descartado d WHERE (d.organizacion_id, d.sucursal_id, d.jornada_id, d.parada_id) = (o, p.sucursal_id, p.jornada_id, p.parada_id)
                     ORDER BY d.revision DESC LIMIT 1), false)
    FROM items i
    JOIN planeado p ON (p.sucursal_id, p.jornada_id, p.parada_id) = (i.sucursal_id, i.jornada_id, i.parada_id) AND p.autorizado = i.convenio
    JOIN negocio.cliente cl ON (cl.organizacion_id, cl.sucursal_id, cl.id) = (o, p.sucursal_id, p.cliente_id)
    LEFT JOIN LATERAL (SELECT x.nombre_fiscal, x.nombre_comercial FROM negocio.ficha_empresa x WHERE (x.organizacion_id, x.empresa_id) = (o, cl.empresa_id) ORDER BY x.revision DESC LIMIT 1) fe ON true
    GROUP BY p.sucursal_id, p.ruta_id, p.nombre, p.persona_id, p.jornada_id, p.parada_id, p.posicion, p.sitio_id, i.servicio, i.banos, i.banos_hechos, i.ordenes, i.ordenes_hechas
    ORDER BY p.nombre, p.posicion, i.servicio;
END $$;
REVOKE ALL ON FUNCTION seguridad.pendientes_ayer_panel(uuid, date) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION seguridad.pendientes_ayer_panel(uuid, date) TO manifiestos_app;
