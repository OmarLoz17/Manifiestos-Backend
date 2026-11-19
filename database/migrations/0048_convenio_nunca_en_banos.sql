-- Tirada mezclada (decisión 0032, etapa 2b): un cliente con convenio (autorizado) NUNCA va en baños ni cuenta para que una ruta sea «mixta»; sus baños planeados
-- (datos antiguos) se ignoran en el trabajo del operador, como antes.
CREATE OR REPLACE FUNCTION negocio.demanda_servicio(p_servicio text, p_banos integer, p_fosas integer, p_autorizado boolean, p_mixta boolean) RETURNS boolean
LANGUAGE sql IMMUTABLE AS $$
    SELECT CASE WHEN p_servicio = 'sanitarios' THEN NOT p_autorizado AND (p_banos > 0 OR (p_mixta AND p_fosas > 0))
                ELSE p_fosas > 0 AND NOT (p_mixta AND NOT p_autorizado) END
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
                        ORDER BY aj.revision DESC LIMIT 1), pc.banos_planeados, 0) AS banos,
               coalesce((SELECT af.fosas FROM negocio.ajuste_fosas_dia af WHERE (af.organizacion_id,af.sucursal_id,af.parada_id,af.cliente_id,af.fecha)=(pa.organizacion_id,pa.sucursal_id,pa.id,pc.cliente_id,f)
                         ORDER BY af.revision DESC LIMIT 1), pc.fosas_planeadas, 0) AS fosas
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
               fv.clasificacion, CASE WHEN fvp.clasificacion='sanitarios' THEN 'sanitarios' ELSE 'fosas' END AS servicio,
               cg.banos AS carga_banos, cg.m3 AS carga_m3, cg.porcentaje AS carga_pct,
               cg2.banos AS carga2_banos, cg2.m3 AS carga2_m3, cg2.porcentaje AS carga2_pct,
               negocio.no_se_hara(o, ru.sucursal_id, ru.id, f, ru.id) AS no_hara
        FROM negocio.jornada jo
        JOIN LATERAL (SELECT x.estado,x.revision FROM negocio.estado_jornada x
            WHERE (x.organizacion_id,x.sucursal_id,x.jornada_id)=(jo.organizacion_id,jo.sucursal_id,jo.id)
            ORDER BY x.revision DESC LIMIT 1) ej ON ej.estado IN ('programada','en_curso')
        JOIN negocio.ruta ru ON (ru.organizacion_id,ru.sucursal_id,ru.id)=(jo.organizacion_id,jo.sucursal_id,jo.ruta_id)
        JOIN LATERAL (SELECT x.nombre, x.vehiculo_id AS plan_vehiculo_id, coalesce(cu.vehiculo_id, x.vehiculo_id) AS vehiculo_id FROM negocio.ruta_version x
            LEFT JOIN LATERAL negocio.cambio_unidad_en(o,ru.sucursal_id,ru.id,f) cu ON true
            WHERE (x.organizacion_id,x.sucursal_id,x.ruta_id)=(ru.organizacion_id,ru.sucursal_id,ru.id)
            ORDER BY x.revision DESC LIMIT 1) rv ON true
        JOIN negocio.vehiculo veh ON (veh.organizacion_id,veh.sucursal_id,veh.id)=(ru.organizacion_id,ru.sucursal_id,rv.vehiculo_id)
        LEFT JOIN LATERAL (SELECT x.* FROM negocio.ficha_vehiculo x
            WHERE (x.organizacion_id,x.sucursal_id,x.vehiculo_id)=(veh.organizacion_id,veh.sucursal_id,veh.id)
            ORDER BY x.revision DESC LIMIT 1) fv ON true
        -- El SERVICIO (qué trabajo es) lo da la unidad del plan de la ruta; la unidad del día solo pone la unidad y su capacidad.
        LEFT JOIN LATERAL (SELECT x.clasificacion FROM negocio.ficha_vehiculo x
            WHERE (x.organizacion_id,x.sucursal_id,x.vehiculo_id)=(ru.organizacion_id,ru.sucursal_id,rv.plan_vehiculo_id)
            ORDER BY x.revision DESC LIMIT 1) fvp ON true
        LEFT JOIN catalogo.capacidad_vehiculo cap ON cap.id=fv.capacidad_id
        LEFT JOIN LATERAL (SELECT g.* FROM negocio.carga_unidad_interna(o,c.sucursal_id,veh.id) g WHERE fvp.clasificacion='sanitarios') cg ON true
        LEFT JOIN LATERAL (SELECT g.* FROM negocio.carga_unidad_interna(o,c.sucursal_id,veh.id) g) cg2 ON true
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
               fs.latitud, fs.longitud, pv.sitio_id, negocio.no_se_hara(o, c.sucursal_id, jo.ruta_id, f, pa.id) AS no_hara
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
               fs.latitud, fs.longitud, pe.sitio_id, negocio.no_se_hara(o, c.sucursal_id, jo.ruta_id, f, pe.id)
        FROM jornadas jo
        JOIN negocio.parada_extraordinaria pe ON (pe.organizacion_id,pe.sucursal_id,pe.ruta_id)=(o,c.sucursal_id,jo.ruta_id) AND pe.fecha=f AND pe.fusionada_en IS NULL
        JOIN negocio.sitio s ON (s.organizacion_id,s.sucursal_id,s.id)=(pe.organizacion_id,pe.sucursal_id,pe.sitio_id)
        LEFT JOIN LATERAL (SELECT x.* FROM negocio.ficha_sitio x
            WHERE (x.organizacion_id,x.sucursal_id,x.sitio_id)=(s.organizacion_id,s.sucursal_id,s.id)
            ORDER BY x.revision DESC LIMIT 1) fs ON true
    )
    SELECT jsonb_build_object('fecha', f, 'jornadas', coalesce(jsonb_agg(j.dato ORDER BY j.nombre, j.jornada_id, j.ord), '[]'::jsonb))
    INTO r
    FROM (
        SELECT jo.jornada_id, jo.nombre, s.ord, jsonb_build_object(
            'jornadaId', jo.jornada_id, 'rutaId', jo.ruta_id, 'rutaNombre', jo.nombre,
            'estado', jo.estado, 'revision', jo.revision,
            'servicio', s.sv, 'noSeHara', jo.no_hara,
            'unidad', jsonb_build_object('id', jo.vehiculo_id, 'numeroEconomico', jo.economico, 'capacidadM3', jo.valor_m3, 'clasificacion', jo.clasificacion)
                || CASE WHEN s.sv='sanitarios' THEN jsonb_build_object('carga', jsonb_build_object('banos', coalesce(jo.carga_banos, jo.carga2_banos), 'm3', coalesce(jo.carga_m3, jo.carga2_m3), 'capacidadM3', jo.valor_m3, 'porcentaje', coalesce(jo.carga_pct, jo.carga2_pct))) ELSE '{}'::jsonb END,
            'paradas', coalesce((
                SELECT jsonb_agg(jsonb_build_object(
                    'paradaId', p.parada_id, 'sitioId', p.sitio_id, 'tipo', p.tipo, 'noSeHara', p.no_hara,
                    'fosa', (s.sv='sanitarios' AND m.mixta
                             AND EXISTS (SELECT 1 FROM clientes k WHERE k.parada_id=p.parada_id AND k.tipo=p.tipo AND k.fosas>0 AND NOT k.autorizado)
                             AND NOT EXISTS (SELECT 1 FROM clientes k WHERE k.parada_id=p.parada_id AND k.tipo=p.tipo AND k.banos>0)), 'sitio', p.sitio, 'direccion', p.direccion,
                    'latitud', p.latitud, 'longitud', p.longitud,
                    'banosAsignados', CASE WHEN s.sv='sanitarios' THEN (SELECT coalesce(sum(k.banos),0)::integer FROM clientes k WHERE k.parada_id=p.parada_id AND k.tipo=p.tipo) ELSE 0 END,
                    'clientes', coalesce((
                        SELECT jsonb_agg(jsonb_build_object(
                            'clienteId', k.cliente_id, 'nombre', k.nombre, 'autorizado', k.autorizado, 'extraordinaria', k.extra,
                            -- Cliente general: fosa nuestra y generador nuestra empresa (sin firma del cliente).
                            'fosaPropia', NOT k.autorizado, 'banos', k.banos, 'fosas', CASE WHEN s.sv='sanitarios' THEN 0 ELSE k.fosas END,
                            'ordenes', coalesce((
                                SELECT jsonb_agg(jsonb_build_object(
                                    'ordenId', md5(jo.jornada_id::text||':'||k.orden_parada_id::text||':'||k.cliente_id::text||':'||n)::uuid,
                                    'vuelta', n) ORDER BY n)
                                FROM generate_series(1, CASE WHEN s.sv='sanitarios' THEN 0 ELSE k.fosas END) n), '[]'::jsonb)
                        ) ORDER BY k.extra, k.nombre, k.cliente_id)
                        FROM clientes k WHERE k.parada_id=p.parada_id AND k.tipo=p.tipo), '[]'::jsonb)
                ) ORDER BY p.tipo, p.sitio, p.parada_id)
                FROM paradas p WHERE p.jornada_id=jo.jornada_id
                  AND EXISTS (SELECT 1 FROM clientes k WHERE k.parada_id=p.parada_id AND k.tipo=p.tipo)
                  AND (EXISTS (SELECT 1 FROM clientes k WHERE k.parada_id=p.parada_id AND k.tipo=p.tipo AND negocio.demanda_servicio(s.sv, k.banos, k.fosas, k.autorizado, m.mixta))
                       OR (s.ord=1 AND NOT EXISTS (SELECT 1 FROM clientes k WHERE k.parada_id=p.parada_id AND k.tipo=p.tipo AND negocio.demanda_servicio(CASE WHEN s.sv='sanitarios' THEN 'fosas' ELSE 'sanitarios' END, k.banos, k.fosas, k.autorizado, m.mixta))))), '[]'::jsonb)
        ) || CASE WHEN s.ord=2 THEN jsonb_build_object('claveRuta', jo.jornada_id::text||':'||s.sv) ELSE '{}'::jsonb END AS dato
        FROM jornadas jo
        -- «mixta»: la ruta del día tiene baños. Entonces las fosas de clientes GENERALES (fosa propia) se trabajan como paradas de sanitarios (decisión 0032).
        CROSS JOIN LATERAL (
            SELECT coalesce(bool_or(k2.banos > 0 AND NOT k2.autorizado), false) AS mixta
            FROM paradas p2 JOIN clientes k2 ON k2.parada_id=p2.parada_id AND k2.tipo=p2.tipo WHERE p2.jornada_id=jo.jornada_id
        ) m
        CROSS JOIN LATERAL (
            SELECT v.sv, v.ord FROM (VALUES (jo.servicio, 1), (CASE WHEN jo.servicio='sanitarios' THEN 'fosas' ELSE 'sanitarios' END, 2)) v(sv, ord)
            WHERE v.ord = 1 OR ((jo.clasificacion = v.sv OR v.sv = 'sanitarios') AND EXISTS (
                SELECT 1 FROM paradas p JOIN clientes k ON k.parada_id=p.parada_id AND k.tipo=p.tipo
                WHERE p.jornada_id=jo.jornada_id AND negocio.demanda_servicio(v.sv, k.banos, k.fosas, k.autorizado, m.mixta)))
        ) s
    ) j;
    RETURN r;
END $$;
