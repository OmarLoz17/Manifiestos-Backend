-- 20-nov-2026 (revisión de código, H15): «Terminar parada» devuelve el resultado de CADA parada (aceptada, duplicada o rechazada) para que el teléfono
-- sepa qué quedó confirmado y qué no; antes solo devolvía un total y un 200 con cero registros podía ser un rechazo o un estado idéntico.
-- Las reglas de alcance son las de la migración 0060; `registrar_paradas_terminadas` (entero) se conserva como el total de aceptadas.
CREATE FUNCTION seguridad.registrar_paradas_terminadas_detalle(o uuid, a uuid, i uuid, p jsonb) RETURNS jsonb
LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog AS $$
DECLARE c record; x jsonb; res jsonb := '[]'::jsonb; j uuid; pa uuid; t boolean; k integer; ult boolean; ultk integer; rev integer; jo record; veh uuid; inst timestamptz; estado text;
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
        estado := 'rechazada';
        SELECT jj.ruta_id, jj.fecha INTO jo FROM negocio.jornada jj WHERE (jj.organizacion_id, jj.sucursal_id, jj.id) = (o, c.sucursal_id, j);
        IF FOUND AND negocio.parada_en_jornada(o, c.sucursal_id, j, pa) THEN
            inst := least(clock_timestamp(), ((jo.fecha + 1)::timestamp AT TIME ZONE 'America/Monterrey'));
            SELECT rv.vehiculo_id INTO veh FROM negocio.ruta_version_en(o, c.sucursal_id, jo.ruta_id, jo.fecha, inst) rv;
            IF veh IS NOT NULL AND negocio.persona_opera_unidad(o, c.sucursal_id, veh, c.persona_id, inst)
               AND k <= negocio.ordenes_de_parada_dia(o, c.sucursal_id, j, pa) THEN
                PERFORM pg_advisory_xact_lock(hashtextextended('parada_terminada:' || c.sucursal_id::text || ':' || j::text || ':' || pa::text, 0));
                ult := NULL; ultk := NULL; rev := NULL;
                SELECT d.terminada, d.no_necesarias, d.revision INTO ult, ultk, rev FROM negocio.parada_terminada_dia d
                    WHERE (d.organizacion_id, d.sucursal_id, d.jornada_id, d.parada_id) = (o, c.sucursal_id, j, pa) ORDER BY d.revision DESC LIMIT 1;
                IF coalesce(ult, false) = t AND coalesce(ultk, 0) = k THEN
                    estado := 'duplicada';
                ELSE
                    INSERT INTO negocio.parada_terminada_dia (organizacion_id, sucursal_id, jornada_id, parada_id, revision, terminada, no_necesarias, persona_id)
                    VALUES (o, c.sucursal_id, j, pa, coalesce(rev, 0) + 1, t, k, c.persona_id);
                    estado := 'aceptada';
                END IF;
            END IF;
        END IF;
        res := res || jsonb_build_array(jsonb_build_object('jornadaId', j, 'paradaId', pa, 'resultado', estado));
    END LOOP;
    RETURN res;
END $$;
REVOKE ALL ON FUNCTION seguridad.registrar_paradas_terminadas_detalle(uuid, uuid, uuid, jsonb) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION seguridad.registrar_paradas_terminadas_detalle(uuid, uuid, uuid, jsonb) TO manifiestos_app;

CREATE OR REPLACE FUNCTION seguridad.registrar_paradas_terminadas(o uuid, a uuid, i uuid, p jsonb) RETURNS integer
LANGUAGE sql VOLATILE SECURITY DEFINER SET search_path = pg_catalog AS $$
    SELECT count(*)::integer FROM jsonb_array_elements(seguridad.registrar_paradas_terminadas_detalle(o, a, i, p)) e WHERE e ->> 'resultado' = 'aceptada'
$$;
