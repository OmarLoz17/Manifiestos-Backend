-- «Terminar parada» llega al panel (decisión 19-nov-2026). El operador puede cerrar una parada con menos órdenes de las planeadas (el plan es un máximo);
-- hasta hoy eso vivía solo en el teléfono y el panel veía la parada incompleta («En atención»). Ahora el teléfono envía el estado y el panel lo usa:
-- con la parada terminada, lo que no se hizo se muestra «No se necesitó» y la parada puede quedar «Atendida» con un «1 de 2».
--   * negocio.parada_terminada_dia: historial insert-only por (jornada, parada); vale la última revisión y reabrir es otra revisión con terminada=false.
--   * seguridad.registrar_paradas_terminadas(...): lo llama el teléfono (idempotente: solo registra cambios de estado). No toca nada capturado.
CREATE TABLE negocio.parada_terminada_dia (
    organizacion_id uuid NOT NULL, sucursal_id uuid NOT NULL, jornada_id uuid NOT NULL, parada_id uuid NOT NULL,
    revision integer NOT NULL CHECK (revision > 0), terminada boolean NOT NULL, persona_id uuid NOT NULL,
    registrada_en timestamptz NOT NULL DEFAULT clock_timestamp(),
    PRIMARY KEY (organizacion_id, sucursal_id, jornada_id, parada_id, revision),
    FOREIGN KEY (organizacion_id, sucursal_id, jornada_id) REFERENCES negocio.jornada ON DELETE RESTRICT,
    FOREIGN KEY (organizacion_id, persona_id) REFERENCES seguridad.persona ON DELETE RESTRICT
);
CREATE INDEX parada_terminada_dia_persona ON negocio.parada_terminada_dia (organizacion_id, persona_id);
CREATE TRIGGER conservar_historia BEFORE UPDATE OR DELETE ON negocio.parada_terminada_dia FOR EACH ROW EXECUTE FUNCTION infra.inmutable();
ALTER TABLE negocio.parada_terminada_dia ENABLE ROW LEVEL SECURITY;
CREATE POLICY contexto ON negocio.parada_terminada_dia TO manifiestos_app USING (seguridad.puede(organizacion_id, sucursal_id, 'datos.leer'));
GRANT SELECT ON negocio.parada_terminada_dia TO manifiestos_app;

CREATE FUNCTION seguridad.registrar_paradas_terminadas(o uuid, a uuid, i uuid, p jsonb) RETURNS integer
LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog AS $$
DECLARE c record; x jsonb; n integer := 0; j uuid; pa uuid; t boolean; ult boolean; rev integer;
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
        -- Solo jornadas de la sucursal de la sesión; cualquier otra se ignora sin revelar nada.
        IF NOT EXISTS (SELECT 1 FROM negocio.jornada jo WHERE (jo.organizacion_id, jo.sucursal_id, jo.id) = (o, c.sucursal_id, j)) THEN CONTINUE; END IF;
        ult := NULL; rev := NULL;
        SELECT d.terminada, d.revision INTO ult, rev FROM negocio.parada_terminada_dia d
            WHERE (d.organizacion_id, d.sucursal_id, d.jornada_id, d.parada_id) = (o, c.sucursal_id, j, pa) ORDER BY d.revision DESC LIMIT 1;
        IF coalesce(ult, false) = t THEN CONTINUE; END IF;
        INSERT INTO negocio.parada_terminada_dia (organizacion_id, sucursal_id, jornada_id, parada_id, revision, terminada, persona_id)
        VALUES (o, c.sucursal_id, j, pa, coalesce(rev, 0) + 1, t, c.persona_id);
        n := n + 1;
    END LOOP;
    RETURN n;
END $$;
REVOKE ALL ON FUNCTION seguridad.registrar_paradas_terminadas(uuid, uuid, uuid, jsonb) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION seguridad.registrar_paradas_terminadas(uuid, uuid, uuid, jsonb) TO manifiestos_app;
