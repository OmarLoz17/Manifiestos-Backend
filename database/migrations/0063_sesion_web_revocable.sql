-- 20-nov-2026 (revisión de código, H28): la sesión web tiene un identificador que el servidor puede revocar.
-- Antes, cerrar sesión solo borraba la cookie de ese navegador: una copia de la cookie seguía sirviendo mientras la cuenta estuviera activa.
-- Ahora cada inicio de sesión abre una `sesion_web`; «Cerrar sesión» la marca como cerrada y la revalidación de cada petición exige que siga abierta.
-- Las cookies emitidas antes de este cambio no traen identificador de sesión: se rechazan y la persona vuelve a entrar una vez.
CREATE TABLE seguridad.sesion_web (
    organizacion_id uuid NOT NULL REFERENCES negocio.organizacion ON DELETE RESTRICT,
    id uuid NOT NULL DEFAULT gen_random_uuid(),
    acceso_id uuid NOT NULL,
    abierta_en timestamptz NOT NULL DEFAULT clock_timestamp(),
    PRIMARY KEY (organizacion_id, id),
    FOREIGN KEY (organizacion_id, acceso_id) REFERENCES seguridad.acceso_autorizado ON DELETE RESTRICT
);
CREATE INDEX sesion_web_acceso ON seguridad.sesion_web (organizacion_id, acceso_id);
CREATE TABLE seguridad.sesion_web_cerrada (
    organizacion_id uuid NOT NULL,
    sesion_id uuid NOT NULL,
    cerrada_en timestamptz NOT NULL DEFAULT clock_timestamp(),
    PRIMARY KEY (organizacion_id, sesion_id),
    FOREIGN KEY (organizacion_id, sesion_id) REFERENCES seguridad.sesion_web ON DELETE RESTRICT
);
CREATE TRIGGER conservar_historia BEFORE UPDATE OR DELETE ON seguridad.sesion_web FOR EACH ROW EXECUTE FUNCTION infra.inmutable();
CREATE TRIGGER conservar_historia BEFORE UPDATE OR DELETE ON seguridad.sesion_web_cerrada FOR EACH ROW EXECUTE FUNCTION infra.inmutable();
-- Sin políticas ni GRANT: el rol de aplicación no las lee; solo escriben y consultan las funciones.
ALTER TABLE seguridad.sesion_web ENABLE ROW LEVEL SECURITY;
ALTER TABLE seguridad.sesion_web_cerrada ENABLE ROW LEVEL SECURITY;

CREATE FUNCTION seguridad.abrir_sesion_web(o uuid, a uuid) RETURNS uuid
LANGUAGE sql VOLATILE SECURITY DEFINER SET search_path = pg_catalog AS $$
    INSERT INTO seguridad.sesion_web (organizacion_id, acceso_id) VALUES (o, a) RETURNING id
$$;
-- Solo cierra una sesión de ese acceso (quien la presenta); repetir el cierre no falla.
CREATE FUNCTION seguridad.cerrar_sesion_web(o uuid, s uuid, a uuid) RETURNS void
LANGUAGE sql VOLATILE SECURITY DEFINER SET search_path = pg_catalog AS $$
    INSERT INTO seguridad.sesion_web_cerrada (organizacion_id, sesion_id)
    SELECT w.organizacion_id, w.id FROM seguridad.sesion_web w WHERE (w.organizacion_id, w.id, w.acceso_id) = (o, s, a)
    ON CONFLICT DO NOTHING
$$;
CREATE FUNCTION seguridad.sesion_web_vigente(o uuid, s uuid, a uuid) RETURNS boolean
LANGUAGE sql STABLE SECURITY DEFINER SET search_path = pg_catalog AS $$
    SELECT EXISTS (SELECT 1 FROM seguridad.sesion_web w WHERE (w.organizacion_id, w.id, w.acceso_id) = (o, s, a)
                   AND NOT EXISTS (SELECT 1 FROM seguridad.sesion_web_cerrada c WHERE (c.organizacion_id, c.sesion_id) = (w.organizacion_id, w.id)))
$$;
REVOKE ALL ON FUNCTION seguridad.abrir_sesion_web(uuid, uuid) FROM PUBLIC;
REVOKE ALL ON FUNCTION seguridad.cerrar_sesion_web(uuid, uuid, uuid) FROM PUBLIC;
REVOKE ALL ON FUNCTION seguridad.sesion_web_vigente(uuid, uuid, uuid) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION seguridad.abrir_sesion_web(uuid, uuid) TO manifiestos_app;
GRANT EXECUTE ON FUNCTION seguridad.cerrar_sesion_web(uuid, uuid, uuid) TO manifiestos_app;
GRANT EXECUTE ON FUNCTION seguridad.sesion_web_vigente(uuid, uuid, uuid) TO manifiestos_app;
