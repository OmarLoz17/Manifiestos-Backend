-- 13-nov-2026 (PG-37): una parada extraordinaria de un sitio que YA está en la ruta de ese día se FUSIONA con la parada existente (del plan o
-- una extraordinaria anterior): no aparece una parada repetida para el operador; lo extraordinario es la orden o el servicio.
--   * parada_extraordinaria.fusionada_en = id de la parada donde se muestra (sin llave foránea: puede ser del plan o extraordinaria).
--   * trabajo_operador: las fusionadas salen dentro de esa parada, con `extraordinaria: true` en su cliente; el id de cada orden sigue
--     derivándose de la extraordinaria de origen, así que las capturas se resuelven igual que antes. El resto de la función no cambia.
ALTER TABLE negocio.parada_extraordinaria ADD COLUMN fusionada_en uuid;
CREATE INDEX parada_extraordinaria_fusionada ON negocio.parada_extraordinaria (organizacion_id, sucursal_id, fusionada_en) WHERE fusionada_en IS NOT NULL;

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
