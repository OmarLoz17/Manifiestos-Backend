-- «Terminar parada» recuerda CUÁNTAS órdenes no se necesitaron (decisión 19-nov-2026): si después Operaciones agrega una extraordinaria a esa parada, las órdenes
-- «No se necesitó» se quedan así y solo lo nuevo queda pendiente (el operador hace 1 orden, no 2). El panel marca como «No se necesitó» las primeras
-- órdenes sin hacer, tantas como `no_necesarias`; las demás (las agregadas después) siguen pendientes.
ALTER TABLE negocio.parada_terminada_dia ADD COLUMN no_necesarias integer NOT NULL DEFAULT 0 CHECK (no_necesarias >= 0);

CREATE OR REPLACE FUNCTION seguridad.registrar_paradas_terminadas(o uuid, a uuid, i uuid, p jsonb) RETURNS integer
LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog AS $$
DECLARE c record; x jsonb; n integer := 0; j uuid; pa uuid; t boolean; k integer; ult boolean; ultk integer; rev integer;
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
        IF NOT EXISTS (SELECT 1 FROM negocio.jornada jo WHERE (jo.organizacion_id, jo.sucursal_id, jo.id) = (o, c.sucursal_id, j)) THEN CONTINUE; END IF;
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
