-- 20-nov-2026 (revisión de código, hallazgos H01, H02, H04, H13). Aditivo: reemplaza funciones; no toca migraciones anteriores.
--   H02: UNA sola resolución temporal del plan, unidad y persona al INSTANTE de la captura.
--        * El cambio del día (cambio_unidad_dia) tiene precedencia: si existe a ese instante, su persona es la ÚNICA que opera (ya no se acepta al
--          titular fijo con un OR); si el cambio no trae persona, opera quien tenía la unidad a ese instante.
--        * El plan fijo y la asignación son los vigentes a ese instante; una revisión registrada DESPUÉS de la captura no cuenta (solo si ninguna
--          existía todavía se toma la más antigua, que es el comportamiento de siempre para datos previos al instante).
--   H04: el manifiesto de fosa fija toma la unidad y la persona de la captura de SERVICIO (la succión), no de la última captura que llegó; las
--        capturas en `captura_por_revisar` no cuentan para armar.
--   H01: «Terminar parada» solo se acepta si la persona de la sesión es quien opera la unidad de esa jornada, la parada pertenece a la ruta de la
--        jornada y el número de órdenes no necesarias no supera las órdenes de esa parada. Lo que no cumple se ignora sin revelar nada.
--   H13: el cierre de una parada serializa por jornada y parada antes de calcular `revision + 1`.

-- Asignación vigente de una unidad a un instante (revisión más reciente ya registrada; si ninguna existía todavía, la más antigua).
CREATE FUNCTION negocio.persona_de_unidad_en(o uuid, s uuid, p_vehiculo uuid, p_instante timestamptz) RETURNS uuid
LANGUAGE sql STABLE SECURITY DEFINER SET search_path = pg_catalog AS $$
    SELECT a.persona_id FROM negocio.asignacion_unidad a
    WHERE (a.organizacion_id, a.sucursal_id, a.vehiculo_id) = (o, s, p_vehiculo)
    ORDER BY (a.registrada_en <= p_instante + interval '2 minutes') DESC,
             CASE WHEN a.registrada_en <= p_instante + interval '2 minutes' THEN a.revision END DESC NULLS LAST,
             a.revision
    LIMIT 1
$$;
REVOKE ALL ON FUNCTION negocio.persona_de_unidad_en(uuid, uuid, uuid, timestamptz) FROM PUBLIC;

-- Plan fijo vigente a un instante (con instante) o al cierre del día (sin él); el cambio del día manda sobre la unidad.
CREATE OR REPLACE FUNCTION negocio.ruta_version_en(o uuid, s uuid, p_ruta uuid, p_fecha date, p_instante timestamptz DEFAULT NULL) RETURNS TABLE (vehiculo_id uuid, revision integer)
LANGUAGE sql STABLE SECURITY DEFINER SET search_path = pg_catalog AS $$
    SELECT coalesce(cu.vehiculo_id, v.vehiculo_id), v.revision FROM negocio.ruta_version v
    LEFT JOIN LATERAL negocio.cambio_unidad_en(o, s, p_ruta, p_fecha, p_instante) cu ON true
    WHERE (v.organizacion_id, v.sucursal_id, v.ruta_id) = (o, s, p_ruta)
    ORDER BY CASE WHEN p_instante IS NOT NULL THEN v.registrada_en <= p_instante + interval '2 minutes'
                  ELSE v.registrada_en < ((p_fecha + 1)::timestamp AT TIME ZONE 'UTC') END DESC,
             CASE WHEN (CASE WHEN p_instante IS NOT NULL THEN v.registrada_en <= p_instante + interval '2 minutes'
                             ELSE v.registrada_en < ((p_fecha + 1)::timestamp AT TIME ZONE 'UTC') END) THEN v.revision END DESC NULLS LAST,
             v.revision
    LIMIT 1
$$;

-- ¿Opera esta persona esta unidad a este instante? El cambio del día manda; sin cambio, la asignación vigente.
CREATE OR REPLACE FUNCTION negocio.persona_opera_unidad(o uuid, s uuid, p_vehiculo uuid, p_persona uuid, p_fecha timestamptz) RETURNS boolean
LANGUAGE sql STABLE SECURITY DEFINER SET search_path = pg_catalog AS $$
    WITH cambios AS (
        SELECT DISTINCT ON (c.ruta_id, c.fecha) c.vehiculo_id, c.persona_id FROM negocio.cambio_unidad_dia c
        WHERE (c.organizacion_id, c.sucursal_id) = (o, s)
          -- El día de la captura puede ser el local (America/Monterrey, provisional PG-18) o el UTC.
          AND c.fecha BETWEEN (p_fecha AT TIME ZONE 'America/Monterrey')::date AND (p_fecha AT TIME ZONE 'UTC')::date
          AND c.creado_en <= p_fecha + interval '2 minutes'
        ORDER BY c.ruta_id, c.fecha, c.revision DESC
    )
    SELECT CASE WHEN EXISTS (SELECT 1 FROM cambios WHERE vehiculo_id = p_vehiculo)
                THEN EXISTS (SELECT 1 FROM cambios WHERE vehiculo_id = p_vehiculo
                             AND coalesce(persona_id, negocio.persona_de_unidad_en(o, s, p_vehiculo, p_fecha)) = p_persona)
                ELSE coalesce(negocio.persona_de_unidad_en(o, s, p_vehiculo, p_fecha) = p_persona, false) END
$$;

-- Orden de fosa fija derivada: la unidad, el plan y la persona se resuelven con la misma regla temporal (ruta_version_en + persona_opera_unidad).
CREATE OR REPLACE FUNCTION negocio.resolver_orden_derivada(o uuid, s uuid, p_persona uuid, p_jornada uuid, p_orden uuid, p_instante timestamptz DEFAULT NULL)
RETURNS TABLE(parada_id uuid, cliente_id uuid, vuelta integer, vehiculo_id uuid, autorizado boolean)
LANGUAGE sql STABLE SECURITY DEFINER SET search_path=pg_catalog AS $$
    WITH jo AS (
        SELECT j.id AS jornada_id, j.ruta_id, j.fecha, rv.vehiculo_id
        FROM negocio.jornada j
        JOIN LATERAL negocio.ruta_version_en(o, s, j.ruta_id, j.fecha, p_instante) rv ON true
        WHERE j.organizacion_id=o AND j.sucursal_id=s AND j.id=p_jornada
          AND negocio.persona_opera_unidad(o, s, rv.vehiculo_id, p_persona, coalesce(p_instante, clock_timestamp()))
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

-- H04: se arma con la unidad y la persona de la captura de SERVICIO (la succión); las capturas por revisar no cuentan.
CREATE OR REPLACE FUNCTION negocio.armar_orden_fosa_fija(o uuid, s uuid, p_persona uuid, p_jornada uuid, p_orden uuid, p_instante timestamptz)
RETURNS void LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE r record; serv record; planta boolean; m uuid;
BEGIN
    IF EXISTS (SELECT 1 FROM negocio.orden_manifiesto om WHERE (om.organizacion_id,om.sucursal_id,om.orden_derivada_id)=(o,s,p_orden)) THEN RETURN; END IF;
    SELECT t.persona_id, k.ocurrida_en INTO serv
    FROM negocio.captura_trabajo t JOIN negocio.captura k ON (k.organizacion_id,k.sucursal_id,k.id)=(t.organizacion_id,t.sucursal_id,t.captura_id)
    WHERE (t.organizacion_id,t.sucursal_id,t.orden_derivada_id)=(o,s,p_orden) AND t.tipo='SERVICIO_OPERADOR'
      AND NOT EXISTS (SELECT 1 FROM negocio.captura_por_revisar x WHERE (x.organizacion_id,x.sucursal_id,x.captura_id)=(t.organizacion_id,t.sucursal_id,t.captura_id))
    ORDER BY k.ocurrida_en, t.captura_id LIMIT 1;
    IF NOT FOUND THEN RETURN; END IF;
    SELECT coalesce(bool_or(t.tipo='RECEPCION_PLANTA'),false) INTO planta
    FROM negocio.captura_trabajo t
    WHERE (t.organizacion_id,t.sucursal_id,t.orden_derivada_id)=(o,s,p_orden)
      AND NOT EXISTS (SELECT 1 FROM negocio.captura_por_revisar x WHERE (x.organizacion_id,x.sucursal_id,x.captura_id)=(t.organizacion_id,t.sucursal_id,t.captura_id));
    IF NOT planta THEN RETURN; END IF;
    SELECT * INTO r FROM negocio.resolver_orden_derivada(o,s,serv.persona_id,p_jornada,p_orden,serv.ocurrida_en);
    IF NOT FOUND THEN RETURN; END IF;
    SELECT g.manifiesto INTO m FROM negocio.armar_manifiesto_fosa_fija(o,s,r.cliente_id,r.vehiculo_id,true) g;
    INSERT INTO negocio.orden_manifiesto(organizacion_id,sucursal_id,orden_derivada_id,jornada_id,parada_id,cliente_id,vuelta,vehiculo_id,manifiesto_id)
    VALUES(o,s,p_orden,p_jornada,r.parada_id,r.cliente_id,r.vuelta,r.vehiculo_id,m);
END $$;
REVOKE ALL ON FUNCTION negocio.armar_orden_fosa_fija(uuid, uuid, uuid, uuid, uuid, timestamptz) FROM PUBLIC;

-- Órdenes de una parada en un día (plan con ajustes del día, más extraordinarias): tope del conteo de «no necesarias».
CREATE FUNCTION negocio.ordenes_de_parada_dia(o uuid, s uuid, p_jornada uuid, p_parada uuid) RETURNS integer
LANGUAGE sql STABLE SECURITY DEFINER SET search_path = pg_catalog AS $$
    SELECT coalesce(sum(n), 0)::integer FROM (
        SELECT coalesce((SELECT af.fosas FROM negocio.ajuste_fosas_dia af WHERE (af.organizacion_id,af.sucursal_id,af.parada_id,af.cliente_id,af.fecha)=(o,s,pa.id,pc.cliente_id,j.fecha)
                         ORDER BY af.revision DESC LIMIT 1), pc.fosas_planeadas, 0) AS n
        FROM negocio.jornada j
        JOIN negocio.parada pa ON (pa.organizacion_id,pa.sucursal_id,pa.ruta_id)=(j.organizacion_id,j.sucursal_id,j.ruta_id) AND pa.id=p_parada
        JOIN LATERAL (SELECT x.id FROM negocio.parada_version x WHERE (x.organizacion_id,x.sucursal_id,x.parada_id)=(pa.organizacion_id,pa.sucursal_id,pa.id) ORDER BY x.revision DESC LIMIT 1) pv ON true
        JOIN negocio.parada_cliente pc ON (pc.organizacion_id,pc.sucursal_id,pc.version_id)=(o,s,pv.id)
        WHERE (j.organizacion_id,j.sucursal_id,j.id)=(o,s,p_jornada)
        UNION ALL
        SELECT coalesce(pc.fosas_planeadas, 0)
        FROM negocio.jornada j
        JOIN negocio.parada_extraordinaria pe ON (pe.organizacion_id,pe.sucursal_id,pe.ruta_id)=(j.organizacion_id,j.sucursal_id,j.ruta_id) AND pe.fecha=j.fecha AND (pe.id=p_parada OR pe.fusionada_en=p_parada)
        JOIN negocio.parada_extraordinaria_cliente pc ON (pc.organizacion_id,pc.sucursal_id,pc.parada_extraordinaria_id)=(o,s,pe.id)
        WHERE (j.organizacion_id,j.sucursal_id,j.id)=(o,s,p_jornada)
    ) q
$$;
REVOKE ALL ON FUNCTION negocio.ordenes_de_parada_dia(uuid, uuid, uuid, uuid) FROM PUBLIC;

-- ¿Pertenece la parada a la ruta de la jornada (del plan o extraordinaria de ese día)?
CREATE FUNCTION negocio.parada_en_jornada(o uuid, s uuid, p_jornada uuid, p_parada uuid) RETURNS boolean
LANGUAGE sql STABLE SECURITY DEFINER SET search_path = pg_catalog AS $$
    SELECT EXISTS (SELECT 1 FROM negocio.jornada j
        WHERE (j.organizacion_id,j.sucursal_id,j.id)=(o,s,p_jornada)
          AND (EXISTS (SELECT 1 FROM negocio.parada pa WHERE (pa.organizacion_id,pa.sucursal_id,pa.ruta_id)=(j.organizacion_id,j.sucursal_id,j.ruta_id) AND pa.id=p_parada)
               OR EXISTS (SELECT 1 FROM negocio.parada_extraordinaria pe WHERE (pe.organizacion_id,pe.sucursal_id,pe.ruta_id)=(j.organizacion_id,j.sucursal_id,j.ruta_id) AND pe.fecha=j.fecha
                          AND (pe.id=p_parada OR pe.fusionada_en=p_parada))))
$$;
REVOKE ALL ON FUNCTION negocio.parada_en_jornada(uuid, uuid, uuid, uuid) FROM PUBLIC;

CREATE OR REPLACE FUNCTION seguridad.registrar_paradas_terminadas(o uuid, a uuid, i uuid, p jsonb) RETURNS integer
LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog AS $$
DECLARE c record; x jsonb; n integer := 0; j uuid; pa uuid; t boolean; k integer; ult boolean; ultk integer; rev integer; jo record; veh uuid; inst timestamptz;
BEGIN
    SELECT * INTO c FROM seguridad.contexto_operador(o, a, i);
    IF NOT FOUND THEN RAISE EXCEPTION 'Contexto no autorizado' USING ERRCODE = '42501'; END IF;
    IF coalesce(jsonb_typeof(p -> 'paradas'), '') <> 'array' OR jsonb_array_length(p -> 'paradas') > 200 THEN
        RAISE EXCEPTION 'Envío no válido' USING ERRCODE = '22023';
    END IF;
    FOR x IN SELECT e FROM jsonb_array_elements(p -> 'paradas') e LOOP
        j := (x ->> 'jornadaId')::uuid; pa := (x ->> 'paradaId')::uuid;
        IF coalesce(jsonb_typeof(x -> 'terminada'), '') <> 'boolean' THEN RAISE EXCEPTION 'Envío no válido' USING ERRCODE = '22023'; END IF;
        t := (x ->> 'terminada')::boolean;
        k := CASE WHEN t AND coalesce(x ->> 'noNecesarias', '') ~ '^[0-9]{1,4}$' THEN (x ->> 'noNecesarias')::integer ELSE 0 END;
        -- Solo jornadas de la sucursal de la sesión; cualquier otra se ignora sin revelar nada.
        SELECT jj.ruta_id, jj.fecha INTO jo FROM negocio.jornada jj WHERE (jj.organizacion_id, jj.sucursal_id, jj.id) = (o, c.sucursal_id, j);
        IF NOT FOUND THEN CONTINUE; END IF;
        -- H01: la parada es de la ruta de esa jornada, la persona de la sesión opera su unidad ese día y el conteo cabe en las órdenes de la parada.
        IF NOT negocio.parada_en_jornada(o, c.sucursal_id, j, pa) THEN CONTINUE; END IF;
        inst := least(clock_timestamp(), ((jo.fecha + 1)::timestamp AT TIME ZONE 'America/Monterrey'));
        SELECT rv.vehiculo_id INTO veh FROM negocio.ruta_version_en(o, c.sucursal_id, jo.ruta_id, jo.fecha, inst) rv;
        IF veh IS NULL OR NOT negocio.persona_opera_unidad(o, c.sucursal_id, veh, c.persona_id, inst) THEN CONTINUE; END IF;
        IF k > negocio.ordenes_de_parada_dia(o, c.sucursal_id, j, pa) THEN CONTINUE; END IF;
        -- H13: serializa los cierres de la misma parada antes de leer la última revisión.
        PERFORM pg_advisory_xact_lock(hashtextextended('parada_terminada:' || c.sucursal_id::text || ':' || j::text || ':' || pa::text, 0));
        ult := NULL; ultk := NULL; rev := NULL;
        SELECT d.terminada, d.no_necesarias, d.revision INTO ult, ultk, rev FROM negocio.parada_terminada_dia d
            WHERE (d.organizacion_id, d.sucursal_id, d.jornada_id, d.parada_id) = (o, c.sucursal_id, j, pa) ORDER BY d.revision DESC LIMIT 1;
        IF coalesce(ult, false) = t AND coalesce(ultk, 0) = k THEN CONTINUE; END IF;
        INSERT INTO negocio.parada_terminada_dia (organizacion_id, sucursal_id, jornada_id, parada_id, revision, terminada, no_necesarias, persona_id)
        VALUES (o, c.sucursal_id, j, pa, coalesce(rev, 0) + 1, t, k, c.persona_id);
        n := n + 1;
    END LOOP;
    RETURN n;
END $$;
