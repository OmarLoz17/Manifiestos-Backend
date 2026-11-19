-- 13-nov-2026: cambio de UNIDAD y/o de OPERADOR de una ruta SOLO PARA UN DÍA (se captura en Órdenes y se toma como extraordinario).
--   * negocio.cambio_unidad_dia: historial insert-only por (ruta, fecha); vale la última revisión. vehiculo_id = unidad del día (cualquier
--     unidad, de baños o fosas); persona_id = operador del día (NULL = el operador asignado a esa unidad).
--   * negocio.cambio_unidad_en: lectura del cambio vigente. Lo usan trabajo_operador (qué jornadas ve cada operador), la validación de
--     capturas (ruta_version_en, persona_opera_unidad, resolver_orden_derivada) y los avisos push. El plan fijo y las asignaciones no se tocan.
--   * La validación de capturas es permisiva con el operador original (una captura legítima no cae a revisión por un cambio del mismo día).
CREATE TABLE negocio.cambio_unidad_dia (
    organizacion_id uuid NOT NULL, sucursal_id uuid NOT NULL,
    ruta_id uuid NOT NULL, fecha date NOT NULL, revision integer NOT NULL CHECK (revision > 0),
    vehiculo_id uuid NOT NULL, persona_id uuid,
    creado_en timestamptz NOT NULL DEFAULT clock_timestamp(),
    creado_por_acceso_id uuid DEFAULT seguridad.contexto_acceso(),
    PRIMARY KEY (organizacion_id, sucursal_id, ruta_id, fecha, revision),
    FOREIGN KEY (organizacion_id, sucursal_id, ruta_id) REFERENCES negocio.ruta ON DELETE RESTRICT,
    FOREIGN KEY (organizacion_id, sucursal_id, vehiculo_id) REFERENCES negocio.vehiculo ON DELETE RESTRICT,
    FOREIGN KEY (organizacion_id, persona_id) REFERENCES seguridad.persona ON DELETE RESTRICT,
    FOREIGN KEY (organizacion_id, creado_por_acceso_id) REFERENCES seguridad.acceso_autorizado ON DELETE RESTRICT
);
CREATE INDEX cambio_unidad_dia_vehiculo ON negocio.cambio_unidad_dia (organizacion_id, sucursal_id, vehiculo_id);
CREATE INDEX cambio_unidad_dia_persona ON negocio.cambio_unidad_dia (organizacion_id, persona_id);
CREATE INDEX cambio_unidad_dia_acceso ON negocio.cambio_unidad_dia (organizacion_id, creado_por_acceso_id);
CREATE INDEX cambio_unidad_dia_fecha ON negocio.cambio_unidad_dia (organizacion_id, sucursal_id, fecha);
CREATE TRIGGER conservar_historia BEFORE UPDATE OR DELETE ON negocio.cambio_unidad_dia FOR EACH ROW EXECUTE FUNCTION infra.inmutable();
ALTER TABLE negocio.cambio_unidad_dia ENABLE ROW LEVEL SECURITY;
CREATE POLICY contexto ON negocio.cambio_unidad_dia TO manifiestos_app USING (seguridad.puede(organizacion_id, sucursal_id, 'datos.leer')) WITH CHECK (seguridad.puede(organizacion_id, sucursal_id, 'datos.escribir'));
GRANT SELECT, INSERT ON negocio.cambio_unidad_dia TO manifiestos_app;

CREATE FUNCTION negocio.cambio_unidad_en(o uuid, s uuid, p_ruta uuid, p_fecha date) RETURNS TABLE (vehiculo_id uuid, persona_id uuid)
LANGUAGE sql STABLE SECURITY DEFINER SET search_path = pg_catalog AS $$
    SELECT c.vehiculo_id, c.persona_id FROM negocio.cambio_unidad_dia c
    WHERE (c.organizacion_id, c.sucursal_id, c.ruta_id, c.fecha) = (o, s, p_ruta, p_fecha)
    ORDER BY c.revision DESC LIMIT 1
$$;
REVOKE ALL ON FUNCTION negocio.cambio_unidad_en(uuid, uuid, uuid, date) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION negocio.cambio_unidad_en(uuid, uuid, uuid, date) TO manifiestos_app;

CREATE OR REPLACE FUNCTION negocio.persona_opera_unidad(o uuid, s uuid, p_vehiculo uuid, p_persona uuid, p_fecha timestamptz) RETURNS boolean
LANGUAGE sql STABLE SECURITY DEFINER SET search_path = pg_catalog AS $$
    SELECT EXISTS (SELECT 1 FROM (SELECT DISTINCT ON (c.ruta_id) c.vehiculo_id, c.persona_id FROM negocio.cambio_unidad_dia c
                      WHERE (c.organizacion_id, c.sucursal_id, c.fecha) = (o, s, (p_fecha AT TIME ZONE 'UTC')::date) ORDER BY c.ruta_id, c.revision DESC) u
                  WHERE u.vehiculo_id = p_vehiculo AND u.persona_id = p_persona)
        OR coalesce((
        SELECT a.persona_id = p_persona FROM negocio.asignacion_unidad a
        WHERE (a.organizacion_id, a.sucursal_id, a.vehiculo_id) = (o, s, p_vehiculo)
        ORDER BY (a.registrada_en <= p_fecha + interval '2 minutes') DESC,
                 CASE WHEN a.registrada_en <= p_fecha + interval '2 minutes' THEN a.revision END DESC NULLS LAST,
                 a.revision
        LIMIT 1), false)
$$;

CREATE OR REPLACE FUNCTION negocio.ruta_version_en(o uuid, s uuid, p_ruta uuid, p_fecha date) RETURNS TABLE (vehiculo_id uuid, revision integer)
LANGUAGE sql STABLE SECURITY DEFINER SET search_path = pg_catalog AS $$
    SELECT coalesce(cu.vehiculo_id, v.vehiculo_id), v.revision FROM negocio.ruta_version v
    LEFT JOIN LATERAL negocio.cambio_unidad_en(o, s, p_ruta, p_fecha) cu ON true
    WHERE (v.organizacion_id, v.sucursal_id, v.ruta_id) = (o, s, p_ruta)
    ORDER BY (v.registrada_en < ((p_fecha + 1)::timestamp AT TIME ZONE 'UTC')) DESC,
             CASE WHEN v.registrada_en < ((p_fecha + 1)::timestamp AT TIME ZONE 'UTC') THEN v.revision END DESC NULLS LAST,
             v.revision
    LIMIT 1
$$;

CREATE OR REPLACE FUNCTION negocio.resolver_orden_derivada(o uuid, s uuid, p_persona uuid, p_jornada uuid, p_orden uuid)
RETURNS TABLE(parada_id uuid, cliente_id uuid, vuelta integer, vehiculo_id uuid, autorizado boolean)
LANGUAGE sql STABLE SECURITY DEFINER SET search_path=pg_catalog AS $$
    WITH jo AS (
        SELECT j.id AS jornada_id, j.ruta_id, j.fecha, rv.vehiculo_id
        FROM negocio.jornada j
        JOIN LATERAL (SELECT coalesce(cu.vehiculo_id, x.vehiculo_id) AS vehiculo_id FROM negocio.ruta_version x
            LEFT JOIN LATERAL negocio.cambio_unidad_en(o,s,j.ruta_id,j.fecha) cu ON true
            WHERE (x.organizacion_id,x.sucursal_id,x.ruta_id)=(j.organizacion_id,j.sucursal_id,j.ruta_id)
            ORDER BY x.revision DESC LIMIT 1) rv ON true
        WHERE j.organizacion_id=o AND j.sucursal_id=s AND j.id=p_jornada
          AND (EXISTS (SELECT 1 FROM negocio.asignacion_unidad a
                       WHERE (a.organizacion_id,a.sucursal_id,a.vehiculo_id)=(o,s,rv.vehiculo_id) AND a.persona_id=p_persona)
               OR EXISTS (SELECT 1 FROM negocio.cambio_unidad_en(o,s,j.ruta_id,j.fecha) c2 WHERE c2.persona_id=p_persona))
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

CREATE OR REPLACE FUNCTION seguridad.destinatarios_aviso_ruta(o uuid, ruta uuid) RETURNS TABLE(token text)
LANGUAGE sql STABLE SECURITY DEFINER SET search_path=pg_catalog AS $$
    SELECT DISTINCT tp.token
    FROM (SELECT r.sucursal_id, coalesce(cu.vehiculo_id, r.vehiculo_id) AS vehiculo_id, cu.persona_id AS persona_cambio FROM negocio.ruta_version r
          LEFT JOIN LATERAL negocio.cambio_unidad_en(o, r.sucursal_id, ruta, (now() AT TIME ZONE 'America/Monterrey')::date) cu ON true
          WHERE r.organizacion_id=o AND r.ruta_id=ruta ORDER BY r.revision DESC LIMIT 1) rv
    JOIN LATERAL (SELECT coalesce(rv.persona_cambio, (SELECT au.persona_id FROM negocio.asignacion_unidad au
        WHERE (au.organizacion_id, au.sucursal_id, au.vehiculo_id)=(o, rv.sucursal_id, rv.vehiculo_id) ORDER BY au.revision DESC LIMIT 1)) AS persona_id) op ON op.persona_id IS NOT NULL
    JOIN seguridad.asignacion_equipo ae ON ae.organizacion_id=o AND ae.persona_id=op.persona_id AND ae.desde<=now() AND (ae.hasta IS NULL OR ae.hasta>now())
        AND ae.revision=(SELECT max(x.revision) FROM seguridad.asignacion_equipo x WHERE x.organizacion_id=o AND x.dispositivo_id=ae.dispositivo_id)
    JOIN seguridad.instalacion_app ins ON ins.organizacion_id=o AND ins.dispositivo_id=ae.dispositivo_id AND ins.habilitada
    JOIN seguridad.token_push tp ON tp.organizacion_id=o AND tp.instalacion_id=ins.id
$$;

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
        SELECT pa.id AS parada_id, 'plan'::text AS tipo, pa.id AS orden_parada_id, false AS extra, pc.cliente_id,
               coalesce(nullif(btrim(fe.nombre_comercial),''), nullif(btrim(fe.nombre_fiscal),''), nullif(btrim(cl.codigo_comercial),''), 'Cliente sin nombre') AS nombre,
               EXISTS (SELECT 1 FROM negocio.autorizacion_empresa au WHERE (au.organizacion_id,au.empresa_id)=(cl.organizacion_id,cl.empresa_id)
                       AND au.autoridad='Medio ambiente' AND au.finalidad='Generador') AS autorizado,
               coalesce((SELECT aj.banos FROM negocio.ajuste_banos_dia aj WHERE (aj.organizacion_id,aj.sucursal_id,aj.parada_id,aj.cliente_id,aj.fecha)=(pa.organizacion_id,pa.sucursal_id,pa.id,pc.cliente_id,f)
                        ORDER BY aj.revision DESC LIMIT 1), pc.banos_planeados, 0) AS banos, coalesce(pc.fosas_planeadas,0) AS fosas
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
        SELECT coalesce(pe.fusionada_en, pe.id),
               CASE WHEN pe.fusionada_en IS NOT NULL AND EXISTS (SELECT 1 FROM negocio.parada pp WHERE (pp.organizacion_id,pp.sucursal_id,pp.id)=(pe.organizacion_id,pe.sucursal_id,pe.fusionada_en)) THEN 'plan' ELSE 'extraordinaria' END,
               pe.id, pe.fusionada_en IS NOT NULL, pc.cliente_id,
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
        JOIN LATERAL (SELECT x.nombre, coalesce(cu.vehiculo_id, x.vehiculo_id) AS vehiculo_id FROM negocio.ruta_version x
            LEFT JOIN LATERAL negocio.cambio_unidad_en(o,ru.sucursal_id,ru.id,f) cu ON true
            WHERE (x.organizacion_id,x.sucursal_id,x.ruta_id)=(ru.organizacion_id,ru.sucursal_id,ru.id)
            ORDER BY x.revision DESC LIMIT 1) rv ON true
        JOIN negocio.vehiculo veh ON (veh.organizacion_id,veh.sucursal_id,veh.id)=(ru.organizacion_id,ru.sucursal_id,rv.vehiculo_id)
        LEFT JOIN LATERAL (SELECT x.* FROM negocio.ficha_vehiculo x
            WHERE (x.organizacion_id,x.sucursal_id,x.vehiculo_id)=(veh.organizacion_id,veh.sucursal_id,veh.id)
            ORDER BY x.revision DESC LIMIT 1) fv ON true
        LEFT JOIN catalogo.capacidad_vehiculo cap ON cap.id=fv.capacidad_id
        LEFT JOIN LATERAL (SELECT g.* FROM negocio.carga_unidad_interna(o,c.sucursal_id,veh.id) g WHERE fv.clasificacion='sanitarios') cg ON true
        JOIN LATERAL (SELECT coalesce(cu.persona_id, (SELECT x.persona_id FROM negocio.asignacion_unidad x
            WHERE (x.organizacion_id,x.sucursal_id,x.vehiculo_id)=(veh.organizacion_id,veh.sucursal_id,veh.id)
            ORDER BY x.revision DESC LIMIT 1)) AS persona_id
            FROM (SELECT 1) z LEFT JOIN LATERAL negocio.cambio_unidad_en(o,ru.sucursal_id,ru.id,f) cu ON true) op ON op.persona_id=c.persona_id
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
        JOIN negocio.parada_extraordinaria pe ON (pe.organizacion_id,pe.sucursal_id,pe.ruta_id)=(o,c.sucursal_id,jo.ruta_id) AND pe.fecha=f AND pe.fusionada_en IS NULL
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
                            'clienteId', k.cliente_id, 'nombre', k.nombre, 'autorizado', k.autorizado, 'extraordinaria', k.extra,
                            -- Cliente general: fosa nuestra y generador nuestra empresa (sin firma del cliente).
                            'fosaPropia', NOT k.autorizado, 'banos', k.banos, 'fosas', CASE WHEN jo.servicio='sanitarios' THEN 0 ELSE k.fosas END,
                            'ordenes', coalesce((
                                SELECT jsonb_agg(jsonb_build_object(
                                    'ordenId', md5(jo.jornada_id::text||':'||k.orden_parada_id::text||':'||k.cliente_id::text||':'||n)::uuid,
                                    'vuelta', n) ORDER BY n)
                                FROM generate_series(1, CASE WHEN jo.servicio='sanitarios' THEN 0 ELSE k.fosas END) n), '[]'::jsonb)
                        ) ORDER BY k.extra, k.nombre, k.cliente_id)
                        FROM clientes k WHERE k.parada_id=p.parada_id AND k.tipo=p.tipo), '[]'::jsonb)
                ) ORDER BY p.tipo, p.sitio, p.parada_id)
                FROM paradas p WHERE p.jornada_id=jo.jornada_id
                  AND EXISTS (SELECT 1 FROM clientes k WHERE k.parada_id=p.parada_id AND k.tipo=p.tipo)), '[]'::jsonb)
        ) AS dato
        FROM jornadas jo
    ) j;
    RETURN r;
END $$;
