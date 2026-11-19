-- 13-nov-2026 (corrección tras la auditoría de Codex, R2-03/04/05/07/09/11/13; decisiones del usuario del 13-nov):
--   * El cambio de unidad/operador de un día (cambio_unidad_dia) vale desde que se hizo: una captura se resuelve con el cambio que ya existía a
--     su instante (cambio_unidad_en con instante). Lo ya succionado se queda en la unidad que tenía; lo que succione la otra unidad se carga a esa.
--     ruta_version_en, resolver_orden_derivada (instante opcional), resolver_parada_derivada, tras_captura_trabajo, persona_opera_unidad y
--     carga_fosas_interna lo usan. La fecha de la captura se busca en el día local y en el UTC (PG-18 sigue abierto).
--   * cambio_unidad_en ya no es SECURITY DEFINER: se ejecuta bajo la política de quien la llama (las funciones del servidor, como dueño).
--   * trabajo_operador: el SERVICIO del trabajo es el de la unidad del plan de la ruta (no cambia al elegir una unidad del otro servicio); la unidad
--     y su capacidad son las del día. Cada parada trae su 'sitioId' para que la app junte las paradas del mismo sitio entre rutas.
--   * El volumen por parada del manifiesto de sanitarios suma EXACTAMENTE la capacidad (el residuo se reparte en milésimas).
--   Se reemplazan funciones con DROP + CREATE (cambia su firma) y CREATE OR REPLACE; no se tocan migraciones anteriores.

DROP FUNCTION negocio.cambio_unidad_en(uuid, uuid, uuid, date);
CREATE FUNCTION negocio.cambio_unidad_en(o uuid, s uuid, p_ruta uuid, p_fecha date, p_instante timestamptz DEFAULT NULL) RETURNS TABLE (vehiculo_id uuid, persona_id uuid)
LANGUAGE sql STABLE SET search_path = pg_catalog AS $$
    SELECT c.vehiculo_id, c.persona_id FROM negocio.cambio_unidad_dia c
    WHERE (c.organizacion_id, c.sucursal_id, c.ruta_id, c.fecha) = (o, s, p_ruta, p_fecha)
      -- Con instante (una captura): solo vale el cambio que ya existía entonces (2 min de tolerancia por el reloj del teléfono).
      AND (p_instante IS NULL OR c.creado_en <= p_instante + interval '2 minutes')
    ORDER BY c.revision DESC LIMIT 1
$$;
REVOKE ALL ON FUNCTION negocio.cambio_unidad_en(uuid, uuid, uuid, date, timestamptz) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION negocio.cambio_unidad_en(uuid, uuid, uuid, date, timestamptz) TO manifiestos_app;

DROP FUNCTION negocio.ruta_version_en(uuid, uuid, uuid, date);
CREATE FUNCTION negocio.ruta_version_en(o uuid, s uuid, p_ruta uuid, p_fecha date, p_instante timestamptz DEFAULT NULL) RETURNS TABLE (vehiculo_id uuid, revision integer)
LANGUAGE sql STABLE SECURITY DEFINER SET search_path = pg_catalog AS $$
    SELECT coalesce(cu.vehiculo_id, v.vehiculo_id), v.revision FROM negocio.ruta_version v
    LEFT JOIN LATERAL negocio.cambio_unidad_en(o, s, p_ruta, p_fecha, p_instante) cu ON true
    WHERE (v.organizacion_id, v.sucursal_id, v.ruta_id) = (o, s, p_ruta)
    ORDER BY (v.registrada_en < ((p_fecha + 1)::timestamp AT TIME ZONE 'UTC')) DESC,
             CASE WHEN v.registrada_en < ((p_fecha + 1)::timestamp AT TIME ZONE 'UTC') THEN v.revision END DESC NULLS LAST,
             v.revision
    LIMIT 1
$$;
REVOKE ALL ON FUNCTION negocio.ruta_version_en(uuid, uuid, uuid, date, timestamptz) FROM PUBLIC;

CREATE OR REPLACE FUNCTION negocio.persona_opera_unidad(o uuid, s uuid, p_vehiculo uuid, p_persona uuid, p_fecha timestamptz) RETURNS boolean
LANGUAGE sql STABLE SECURITY DEFINER SET search_path = pg_catalog AS $$
    SELECT EXISTS (SELECT 1 FROM (SELECT DISTINCT ON (c.ruta_id, c.fecha) c.vehiculo_id, c.persona_id FROM negocio.cambio_unidad_dia c
                      WHERE (c.organizacion_id, c.sucursal_id) = (o, s)
                        -- El día de la captura puede ser el local (America/Monterrey, provisional PG-18) o el UTC.
                        AND c.fecha BETWEEN (p_fecha AT TIME ZONE 'America/Monterrey')::date AND (p_fecha AT TIME ZONE 'UTC')::date
                        AND c.creado_en <= p_fecha + interval '2 minutes'
                      ORDER BY c.ruta_id, c.fecha, c.revision DESC) u
                  WHERE u.vehiculo_id = p_vehiculo AND u.persona_id = p_persona)
        OR coalesce((
        SELECT a.persona_id = p_persona FROM negocio.asignacion_unidad a
        WHERE (a.organizacion_id, a.sucursal_id, a.vehiculo_id) = (o, s, p_vehiculo)
        ORDER BY (a.registrada_en <= p_fecha + interval '2 minutes') DESC,
                 CASE WHEN a.registrada_en <= p_fecha + interval '2 minutes' THEN a.revision END DESC NULLS LAST,
                 a.revision
        LIMIT 1), false)
$$;
CREATE OR REPLACE FUNCTION negocio.resolver_parada_derivada(o uuid, s uuid, p_persona uuid, p_jornada uuid, p_parada uuid, p_fecha timestamptz)
RETURNS TABLE (parada_id uuid, sitio_id uuid, ruta_id uuid, vehiculo_id uuid)
LANGUAGE sql STABLE SECURITY DEFINER SET search_path = pg_catalog AS $$
    SELECT c.parada_id, c.sitio_id, j.ruta_id, rv.vehiculo_id
    FROM negocio.jornada j
    JOIN LATERAL negocio.ruta_version_en(o, s, j.ruta_id, j.fecha, p_fecha) rv ON true
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
DROP FUNCTION negocio.resolver_orden_derivada(uuid, uuid, uuid, uuid, uuid);
CREATE FUNCTION negocio.resolver_orden_derivada(o uuid, s uuid, p_persona uuid, p_jornada uuid, p_orden uuid, p_instante timestamptz DEFAULT NULL)
RETURNS TABLE(parada_id uuid, cliente_id uuid, vuelta integer, vehiculo_id uuid, autorizado boolean)
LANGUAGE sql STABLE SECURITY DEFINER SET search_path=pg_catalog AS $$
    WITH jo AS (
        SELECT j.id AS jornada_id, j.ruta_id, j.fecha, rv.vehiculo_id
        FROM negocio.jornada j
        JOIN LATERAL (SELECT coalesce(cu.vehiculo_id, x.vehiculo_id) AS vehiculo_id FROM negocio.ruta_version x
            LEFT JOIN LATERAL negocio.cambio_unidad_en(o,s,j.ruta_id,j.fecha,p_instante) cu ON true
            WHERE (x.organizacion_id,x.sucursal_id,x.ruta_id)=(j.organizacion_id,j.sucursal_id,j.ruta_id)
            ORDER BY x.revision DESC LIMIT 1) rv ON true
        WHERE j.organizacion_id=o AND j.sucursal_id=s AND j.id=p_jornada
          AND (EXISTS (SELECT 1 FROM negocio.asignacion_unidad a
                       WHERE (a.organizacion_id,a.sucursal_id,a.vehiculo_id)=(o,s,rv.vehiculo_id) AND a.persona_id=p_persona)
               OR EXISTS (SELECT 1 FROM negocio.cambio_unidad_en(o,s,j.ruta_id,j.fecha,p_instante) c2 WHERE c2.persona_id=p_persona))
    ), cand AS (
        SELECT jo.jornada_id, pa.id AS parada_id, pc.cliente_id,
               coalesce((SELECT af.fosas FROM negocio.ajuste_fosas_dia af WHERE (af.organizacion_id,af.sucursal_id,af.parada_id,af.cliente_id,af.fecha)=(o,s,pa.id,pc.cliente_id,jo.fecha)
                         ORDER BY af.revision DESC LIMIT 1), pc.fosas_planeadas, 0) AS fosas, jo.vehiculo_id
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
REVOKE ALL ON FUNCTION negocio.resolver_orden_derivada(uuid, uuid, uuid, uuid, uuid, timestamptz) FROM PUBLIC;

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
    SELECT * INTO r FROM negocio.resolver_orden_derivada(NEW.organizacion_id,NEW.sucursal_id,NEW.persona_id,NEW.jornada_id,NEW.orden_derivada_id,
        (SELECT k.ocurrida_en FROM negocio.captura k WHERE (k.organizacion_id,k.sucursal_id,k.id)=(NEW.organizacion_id,NEW.sucursal_id,NEW.captura_id)));
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
               cg.banos AS carga_banos, cg.m3 AS carga_m3, cg.porcentaje AS carga_pct
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
               fs.latitud, fs.longitud, pv.sitio_id
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
               fs.latitud, fs.longitud, pe.sitio_id
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
                    'paradaId', p.parada_id, 'sitioId', p.sitio_id, 'tipo', p.tipo, 'sitio', p.sitio, 'direccion', p.direccion,
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
CREATE OR REPLACE FUNCTION negocio.carga_fosas_interna(o uuid, s uuid, p_vehiculo uuid)
RETURNS TABLE (ordenes integer, m3 numeric, capacidad_m3 numeric, porcentaje numeric, ultima_descarga_en timestamptz, desglose jsonb)
LANGUAGE sql STABLE SECURITY DEFINER SET search_path = pg_catalog AS $$
    WITH v AS (
        SELECT cap.valor_m3
        FROM negocio.vehiculo veh
        LEFT JOIN LATERAL (SELECT x.capacidad_id FROM negocio.ficha_vehiculo x
            WHERE (x.organizacion_id, x.sucursal_id, x.vehiculo_id) = (veh.organizacion_id, veh.sucursal_id, veh.id)
            ORDER BY x.revision DESC LIMIT 1) f ON true
        LEFT JOIN catalogo.capacidad_vehiculo cap ON cap.id = f.capacidad_id
        WHERE (veh.organizacion_id, veh.sucursal_id, veh.id) = (o, s, p_vehiculo)
    ), de_unidad AS (
        -- Capturas de órdenes de fosas (no de sanitarios) cuya jornada es de una ruta de esta unidad.
        SELECT ct.captura_id, ct.tipo, ct.orden_derivada_id, j.fecha, k.ocurrida_en
        FROM negocio.captura_trabajo ct
        JOIN negocio.captura k ON (k.organizacion_id, k.sucursal_id, k.id) = (ct.organizacion_id, ct.sucursal_id, ct.captura_id)
        JOIN negocio.jornada j ON (j.organizacion_id, j.sucursal_id, j.id) = (ct.organizacion_id, ct.sucursal_id, ct.jornada_id)
        -- La unidad es la que tenía la ruta cuando se capturó la orden: lo ya succionado se queda en esa unidad aunque la ruta cambie después.
        JOIN LATERAL (SELECT coalesce(cu.vehiculo_id, x.vehiculo_id) AS vehiculo_id FROM negocio.ruta_version x
            LEFT JOIN LATERAL negocio.cambio_unidad_en(j.organizacion_id, j.sucursal_id, j.ruta_id, j.fecha, k.ocurrida_en) cu ON true
            WHERE (x.organizacion_id, x.sucursal_id, x.ruta_id) = (j.organizacion_id, j.sucursal_id, j.ruta_id)
            ORDER BY x.revision DESC LIMIT 1) rv ON rv.vehiculo_id = p_vehiculo
        WHERE ct.organizacion_id = o AND ct.sucursal_id = s AND ct.tipo IN ('SERVICIO_OPERADOR', 'RECEPCION_PLANTA')
          AND NOT EXISTS (SELECT 1 FROM negocio.captura_por_revisar r
                          WHERE (r.organizacion_id, r.sucursal_id, r.captura_id) = (ct.organizacion_id, ct.sucursal_id, ct.captura_id))
    ), pend AS (
        SELECT d.orden_derivada_id, min(d.fecha) AS fecha
        FROM de_unidad d
        WHERE d.tipo = 'SERVICIO_OPERADOR'
          AND NOT EXISTS (SELECT 1 FROM de_unidad r WHERE r.tipo = 'RECEPCION_PLANTA' AND r.orden_derivada_id = d.orden_derivada_id)
        GROUP BY d.orden_derivada_id
    ), dias AS (
        SELECT fecha, count(*)::integer AS ordenes FROM pend GROUP BY fecha
    ), c AS (
        SELECT (SELECT count(*) FROM pend)::integer AS ordenes,
               (SELECT max(d.ocurrida_en) FROM de_unidad d WHERE d.tipo = 'RECEPCION_PLANTA') AS ultima,
               coalesce((SELECT jsonb_agg(jsonb_build_object('fecha', d.fecha, 'ordenes', d.ordenes) ORDER BY d.fecha) FROM dias d), '[]'::jsonb) AS desglose
    )
    SELECT c.ordenes, round(c.ordenes * v.valor_m3, 3), v.valor_m3, (c.ordenes * 100)::numeric, c.ultima, c.desglose
    FROM v CROSS JOIN c
$$;
CREATE OR REPLACE FUNCTION negocio.armar_manifiesto_sanitarios(o uuid, s uuid, p_tirada uuid) RETURNS uuid
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path = pg_catalog AS $$
DECLARE
    ti negocio.tirada%ROWTYPE; m uuid; v uuid; folio text; total bigint; litros numeric; n_esp integer; capacidad numeric;
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
    -- La tirada siempre sale con la unidad llena: la cantidad del manifiesto es la CAPACIDAD de la unidad (m3), no baños x 16 L.
    -- Los baños x 16 L solo quedan en el detalle por parada y en la barra de carga (estimaciones, no mediciones).
    SELECT cap.valor_m3 INTO capacidad
    FROM negocio.ficha_vehiculo f JOIN catalogo.capacidad_vehiculo cap ON cap.id = f.capacidad_id
    WHERE (f.organizacion_id, f.sucursal_id, f.vehiculo_id) = (o, s, ti.vehiculo_id) ORDER BY f.revision DESC LIMIT 1;
    IF capacidad IS NULL OR capacidad <= 0 THEN RAISE EXCEPTION 'Falta la capacidad de la unidad' USING ERRCODE = 'P0A03'; END IF;

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
    folio := negocio.siguiente_folio(o, s, extract(year FROM ti.fecha_captura AT TIME ZONE 'UTC')::integer);   -- año de la tirada (cuando firma quien recibe)
    INSERT INTO negocio.version_manifiesto (organizacion_id, sucursal_id, manifiesto_id, revision, numero_manifiesto, ficha_vehiculo_id, version_plantilla)
    VALUES (o, s, m, 1, folio, ficha_veh, 'sanitarios-1') RETURNING id INTO v;   -- plantilla PROVISIONAL
    INSERT INTO negocio.participante_manifiesto (organizacion_id, sucursal_id, version_id, rol, empresa_id, ficha_empresa_id, autorizacion_id, origen_domicilio)
    VALUES (o, s, v, 'generador', emp, ficha_emp, aut_gen, 'fiscal'),
           (o, s, v, 'transportista', emp, ficha_emp, aut_tra, 'fiscal');
    -- Un renglón resumen: cantidad = capacidad de la unidad. tipo_contenedor y numero_contenedores (= baños) son PROVISIONALES: no están confirmados como campos del manifiesto.
    INSERT INTO negocio.detalle_manifiesto (organizacion_id, sucursal_id, version_id, renglon, descripcion_residuo, tipo_contenedor,
        numero_contenedores, cantidad, unidad_cantidad, naturaleza_cantidad, metodo_cuantificacion, version_metodo)
    VALUES (o, s, v, 1, 'Agua residual de sanitarios portátiles', 'sanitario', total, capacidad, 'm3', 'estimada', 'capacidad_unidad_llena', '1');

    -- Volumen estimado por parada (m3): la unidad sale llena, así que se reparte su capacidad según los baños de cada parada
    -- (capacidad x baños / total de baños; ya no x 16 L). Paradas con los MISMOS baños no repiten el mismo número: se emparejan y una sube
    -- 3 % y la otra baja 3 % (el par suma igual); una sobrante queda sin variación. Es estimación por regla, no medición.
    INSERT INTO negocio.manifiesto_parada (organizacion_id, sucursal_id, version_id, renglon, parada_id, captura_id, sitio_id, ficha_sitio_id,
        fecha, banos, latitud, longitud, precision_m, clientes, evidencia_foto_id, volumen_m3)
    WITH base AS (
        SELECT sp.*, row_number() OVER (ORDER BY sp.fecha_captura, sp.recibida_en, sp.captura_id) AS renglon
        FROM negocio.tirada_esperada e
        JOIN negocio.succion_parada sp ON (sp.organizacion_id, sp.sucursal_id, sp.captura_id) = (e.organizacion_id, e.sucursal_id, e.captura_id)
        WHERE (e.organizacion_id, e.sucursal_id, e.tirada_id) = (o, s, p_tirada)
    ), grupos AS (
        SELECT b.*, row_number() OVER (PARTITION BY b.banos_succionados ORDER BY b.renglon) AS pos,
               count(*) OVER (PARTITION BY b.banos_succionados) AS n
        FROM base b
    ), crudo AS (
        SELECT g.*, capacidad * g.banos_succionados / total
                    * CASE WHEN g.n > 1 AND g.pos = g.n AND g.n % 2 = 1 THEN 1 WHEN g.n > 1 AND g.pos % 2 = 1 THEN 1.03 WHEN g.n > 1 THEN 0.97 ELSE 1 END AS raw
        FROM grupos g
    ), pisos AS (
        SELECT c.*, floor(c.raw * 1000) AS piso, c.raw * 1000 - floor(c.raw * 1000) AS fraccion FROM crudo c
    ), resto AS (
        -- Cada parada baja a la milésima; lo que falta para llegar a la capacidad se reparte de a una milésima, empezando por la fracción mayor.
        SELECT p.*, (round(capacidad * 1000) - sum(p.piso) OVER ())::integer AS faltan, row_number() OVER (ORDER BY p.fraccion DESC, p.renglon) AS por_fraccion
        FROM pisos p
    )
    SELECT o, s, v, g.renglon, g.parada_id, g.captura_id, g.sitio_id, fs.id,
           g.fecha_captura, g.banos_succionados, ev.latitud, ev.longitud, ev.precision_metros,
           negocio.clientes_parada_en(o, s, g.parada_id, g.fecha_captura), ev.id,
           (g.piso + CASE WHEN g.por_fraccion <= g.faltan THEN 1 ELSE 0 END) / 1000.0
    FROM resto g
    LEFT JOIN LATERAL (SELECT x.id FROM negocio.ficha_sitio x WHERE (x.organizacion_id, x.sucursal_id, x.sitio_id) = (g.organizacion_id, g.sucursal_id, g.sitio_id)
        ORDER BY x.revision DESC LIMIT 1) fs ON true
    LEFT JOIN LATERAL (SELECT x.id, x.latitud, x.longitud, x.precision_metros FROM negocio.evidencia_captura x
        WHERE (x.organizacion_id, x.sucursal_id, x.captura_id) = (g.organizacion_id, g.sucursal_id, g.captura_id) AND x.rol = 'FOTO'
        ORDER BY x.id LIMIT 1) ev ON true;
    INSERT INTO negocio.recepcion_manifiesto (organizacion_id, sucursal_id, version_id, planta_id, planta_nombre, nombre_recibe, captura_id, firma_generador_id)
    VALUES (o, s, v, ti.planta_id, ti.planta_nombre, ti.nombre_recibe, ti.captura_id, firma);
    -- Asignación definitiva: de aquí en adelante estas succiones ya no cuentan en la carga de la unidad.
    INSERT INTO negocio.tirada_parada (organizacion_id, sucursal_id, tirada_id, captura_id, renglon)
    SELECT o, s, p_tirada, mp.captura_id, mp.renglon FROM negocio.manifiesto_parada mp WHERE (mp.organizacion_id, mp.sucursal_id, mp.version_id) = (o, s, v);
    INSERT INTO negocio.tirada_manifiesto (organizacion_id, sucursal_id, tirada_id, manifiesto_id) VALUES (o, s, p_tirada, m);
    RETURN m;
END $$;