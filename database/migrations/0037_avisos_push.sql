-- 13-nov-2026 (PG-37): avisos push (Firebase Cloud Messaging) al teléfono del operador cuando Operaciones cambia el trabajo de HOY.
--   * seguridad.token_push: el token FCM de cada instalación de la app (uno por instalación; solo lo escribe quien la usa, con sesión móvil vigente).
--   * seguridad.registrar_token_push: lo guarda (si otro teléfono/instalación tenía el mismo token, se lo quita).
--   * seguridad.destinatarios_aviso_ruta: tokens de las instalaciones habilitadas cuyo equipo está hoy asignado al operador de la unidad de la ruta.
--   * seguridad.olvidar_token_push: FCM avisa que un token ya no existe; se retira para no insistir.
-- El token no es un secreto de acceso, pero identifica una instalación: no sale por ninguna API ni se registra en bitácoras.
CREATE TABLE seguridad.token_push (
    organizacion_id uuid NOT NULL, instalacion_id uuid NOT NULL,
    token text NOT NULL CHECK (length(token) BETWEEN 20 AND 4096),
    actualizado_en timestamptz NOT NULL DEFAULT clock_timestamp(),
    PRIMARY KEY (organizacion_id, instalacion_id),
    FOREIGN KEY (organizacion_id, instalacion_id) REFERENCES seguridad.instalacion_app ON DELETE RESTRICT
);
CREATE UNIQUE INDEX token_push_token ON seguridad.token_push (organizacion_id, token);

CREATE FUNCTION seguridad.registrar_token_push(o uuid, a uuid, i uuid, t text) RETURNS boolean
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
BEGIN
    IF t IS NULL OR length(btrim(t)) NOT BETWEEN 20 AND 4096 THEN RAISE EXCEPTION 'Token de aviso inválido' USING ERRCODE='22023'; END IF;
    IF NOT EXISTS (SELECT 1 FROM seguridad.contexto_operador(o,a,i)) THEN RETURN false; END IF;
    DELETE FROM seguridad.token_push WHERE organizacion_id=o AND token=btrim(t) AND instalacion_id<>i;
    INSERT INTO seguridad.token_push(organizacion_id, instalacion_id, token) VALUES (o, i, btrim(t))
    ON CONFLICT (organizacion_id, instalacion_id) DO UPDATE SET token=excluded.token, actualizado_en=clock_timestamp();
    RETURN true;
END $$;

CREATE FUNCTION seguridad.olvidar_token_push(o uuid, t text) RETURNS void
LANGUAGE sql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
    DELETE FROM seguridad.token_push WHERE organizacion_id=o AND token=t
$$;

CREATE FUNCTION seguridad.destinatarios_aviso_ruta(o uuid, ruta uuid) RETURNS TABLE(token text)
LANGUAGE sql STABLE SECURITY DEFINER SET search_path=pg_catalog AS $$
    SELECT DISTINCT tp.token
    FROM (SELECT r.sucursal_id, r.vehiculo_id FROM negocio.ruta_version r WHERE r.organizacion_id=o AND r.ruta_id=ruta ORDER BY r.revision DESC LIMIT 1) rv
    JOIN LATERAL (SELECT au.persona_id FROM negocio.asignacion_unidad au
        WHERE (au.organizacion_id, au.sucursal_id, au.vehiculo_id)=(o, rv.sucursal_id, rv.vehiculo_id) ORDER BY au.revision DESC LIMIT 1) op ON op.persona_id IS NOT NULL
    JOIN seguridad.asignacion_equipo ae ON ae.organizacion_id=o AND ae.persona_id=op.persona_id AND ae.desde<=now() AND (ae.hasta IS NULL OR ae.hasta>now())
        AND ae.revision=(SELECT max(x.revision) FROM seguridad.asignacion_equipo x WHERE x.organizacion_id=o AND x.dispositivo_id=ae.dispositivo_id)
    JOIN seguridad.instalacion_app ins ON ins.organizacion_id=o AND ins.dispositivo_id=ae.dispositivo_id AND ins.habilitada
    JOIN seguridad.token_push tp ON tp.organizacion_id=o AND tp.instalacion_id=ins.id
$$;

REVOKE ALL ON TABLE seguridad.token_push FROM PUBLIC;
REVOKE ALL ON FUNCTION seguridad.registrar_token_push(uuid,uuid,uuid,text) FROM PUBLIC;
REVOKE ALL ON FUNCTION seguridad.olvidar_token_push(uuid,text) FROM PUBLIC;
REVOKE ALL ON FUNCTION seguridad.destinatarios_aviso_ruta(uuid,uuid) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION seguridad.registrar_token_push(uuid,uuid,uuid,text) TO manifiestos_app;
GRANT EXECUTE ON FUNCTION seguridad.olvidar_token_push(uuid,text) TO manifiestos_app;
GRANT EXECUTE ON FUNCTION seguridad.destinatarios_aviso_ruta(uuid,uuid) TO manifiestos_app;
