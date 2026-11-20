-- 20-nov-2026 (revisión de código, H17): «Terminar parada» manda las IDENTIDADES de las órdenes que no se necesitaron, no solo cuántas.
-- Antes el panel marcaba como «No se necesitó» las primeras órdenes sin hacer, tantas como el conteo; si cambiaba el orden de la tabla o se combinaba con otra
-- extraordinaria, podía marcar otra. Ahora, si llegan las identidades (fosa fija), el panel marca exactamente esas; sin ellas (sanitarios, teléfonos anteriores) se usa el conteo como antes.
ALTER TABLE negocio.parada_terminada_dia ADD COLUMN ordenes_no_necesarias uuid[] NOT NULL DEFAULT '{}';

-- Ids de las órdenes derivadas de una parada en un día (md5(jornada:parada:cliente:vuelta), igual que resolver_orden_derivada): las del plan y las de sus extraordinarias.
CREATE FUNCTION negocio.ordenes_derivadas_de_parada_dia(o uuid, s uuid, p_jornada uuid, p_parada uuid) RETURNS uuid[]
LANGUAGE sql STABLE SECURITY DEFINER SET search_path = pg_catalog AS $$
    SELECT coalesce(array_agg(q.id), '{}') FROM (
        SELECT md5(j.id::text || ':' || pa.id::text || ':' || pc.cliente_id::text || ':' || g::text)::uuid AS id
        FROM negocio.jornada j
        JOIN negocio.parada pa ON (pa.organizacion_id, pa.sucursal_id, pa.ruta_id) = (j.organizacion_id, j.sucursal_id, j.ruta_id) AND pa.id = p_parada
        JOIN LATERAL (SELECT x.id FROM negocio.parada_version x WHERE (x.organizacion_id, x.sucursal_id, x.parada_id) = (pa.organizacion_id, pa.sucursal_id, pa.id) ORDER BY x.revision DESC LIMIT 1) pv ON true
        JOIN negocio.parada_cliente pc ON (pc.organizacion_id, pc.sucursal_id, pc.version_id) = (o, s, pv.id)
        CROSS JOIN LATERAL generate_series(1, coalesce((SELECT af.fosas FROM negocio.ajuste_fosas_dia af WHERE (af.organizacion_id, af.sucursal_id, af.parada_id, af.cliente_id, af.fecha) = (o, s, pa.id, pc.cliente_id, j.fecha)
                                                         ORDER BY af.revision DESC LIMIT 1), pc.fosas_planeadas, 0)) g
        WHERE (j.organizacion_id, j.sucursal_id, j.id) = (o, s, p_jornada)
        UNION ALL
        SELECT md5(j.id::text || ':' || pe.id::text || ':' || pc.cliente_id::text || ':' || g::text)::uuid
        FROM negocio.jornada j
        JOIN negocio.parada_extraordinaria pe ON (pe.organizacion_id, pe.sucursal_id, pe.ruta_id) = (j.organizacion_id, j.sucursal_id, j.ruta_id) AND pe.fecha = j.fecha AND (pe.id = p_parada OR pe.fusionada_en = p_parada)
        JOIN negocio.parada_extraordinaria_cliente pc ON (pc.organizacion_id, pc.sucursal_id, pc.parada_extraordinaria_id) = (o, s, pe.id)
        CROSS JOIN LATERAL generate_series(1, coalesce(pc.fosas_planeadas, 0)) g
        WHERE (j.organizacion_id, j.sucursal_id, j.id) = (o, s, p_jornada)
    ) q
$$;
REVOKE ALL ON FUNCTION negocio.ordenes_derivadas_de_parada_dia(uuid, uuid, uuid, uuid) FROM PUBLIC;

CREATE OR REPLACE FUNCTION seguridad.registrar_paradas_terminadas_detalle(o uuid, a uuid, i uuid, p jsonb) RETURNS jsonb
LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog AS $$
DECLARE c record; x jsonb; res jsonb := '[]'::jsonb; j uuid; pa uuid; t boolean; k integer; ult boolean; ultk integer; ultids uuid[]; rev integer; jo record; veh uuid; inst timestamptz; estado text;
        ids uuid[]; validas uuid[]; v text;
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
        -- Identidades de las órdenes no necesarias (opcional, máx. 100); si llegan, el conteo es su número.
        ids := '{}';
        IF t AND coalesce(jsonb_typeof(x -> 'ordenesNoNecesarias'), '') = 'array' THEN
            IF jsonb_array_length(x -> 'ordenesNoNecesarias') > 100 THEN RAISE EXCEPTION 'Envío no válido' USING ERRCODE = '22023'; END IF;
            FOR v IN SELECT jsonb_array_elements_text(x -> 'ordenesNoNecesarias') LOOP ids := array_append(ids, v::uuid); END LOOP;
            SELECT coalesce(array_agg(DISTINCT u ORDER BY u), '{}') INTO ids FROM unnest(ids) u;
        END IF;
        k := CASE WHEN cardinality(ids) > 0 THEN cardinality(ids)
                  WHEN t AND coalesce(x ->> 'noNecesarias', '') ~ '^[0-9]{1,4}$' THEN (x ->> 'noNecesarias')::integer ELSE 0 END;
        estado := 'rechazada';
        SELECT jj.ruta_id, jj.fecha INTO jo FROM negocio.jornada jj WHERE (jj.organizacion_id, jj.sucursal_id, jj.id) = (o, c.sucursal_id, j);
        IF FOUND AND negocio.parada_en_jornada(o, c.sucursal_id, j, pa) THEN
            inst := least(clock_timestamp(), ((jo.fecha + 1)::timestamp AT TIME ZONE 'America/Monterrey'));
            SELECT rv.vehiculo_id INTO veh FROM negocio.ruta_version_en(o, c.sucursal_id, jo.ruta_id, jo.fecha, inst) rv;
            validas := negocio.ordenes_derivadas_de_parada_dia(o, c.sucursal_id, j, pa);
            IF veh IS NOT NULL AND negocio.persona_opera_unidad(o, c.sucursal_id, veh, c.persona_id, inst)
               AND k <= negocio.ordenes_de_parada_dia(o, c.sucursal_id, j, pa)
               AND ids <@ validas THEN   -- cada identidad enviada debe ser una orden de ESA parada
                PERFORM pg_advisory_xact_lock(hashtextextended('parada_terminada:' || c.sucursal_id::text || ':' || j::text || ':' || pa::text, 0));
                ult := NULL; ultk := NULL; ultids := NULL; rev := NULL;
                SELECT d.terminada, d.no_necesarias, d.ordenes_no_necesarias, d.revision INTO ult, ultk, ultids, rev FROM negocio.parada_terminada_dia d
                    WHERE (d.organizacion_id, d.sucursal_id, d.jornada_id, d.parada_id) = (o, c.sucursal_id, j, pa) ORDER BY d.revision DESC LIMIT 1;
                IF coalesce(ult, false) = t AND coalesce(ultk, 0) = k AND coalesce(ultids, '{}') = ids THEN
                    estado := 'duplicada';
                ELSE
                    INSERT INTO negocio.parada_terminada_dia (organizacion_id, sucursal_id, jornada_id, parada_id, revision, terminada, no_necesarias, ordenes_no_necesarias, persona_id)
                    VALUES (o, c.sucursal_id, j, pa, coalesce(rev, 0) + 1, t, k, ids, c.persona_id);
                    estado := 'aceptada';
                END IF;
            END IF;
        END IF;
        res := res || jsonb_build_array(jsonb_build_object('jornadaId', j, 'paradaId', pa, 'resultado', estado));
    END LOOP;
    RETURN res;
END $$;
