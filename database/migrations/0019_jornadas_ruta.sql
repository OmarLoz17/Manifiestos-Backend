-- Jornadas: la ruta fija (días de la semana) se ejecuta en una fecha concreta (9-nov-2026).
-- Operaciones la PUBLICA desde el panel (borrador → programada) y solo entonces la ve el operador en la app.
-- Estados: borrador, programada, en_curso, completada, cancelada. El panel solo pone borrador, programada y
-- cancelada; en_curso y completada las pondrá la app. Una jornada sin fila equivale a borrador.
-- Cada cambio es una revisión nueva (historial inmutable) con quién lo hizo.
-- Pendiente de confirmar con el gerente: si publica la víspera o automático, retiro de una jornada ya publicada
-- (hoy el panel permite retirarla mientras siga programada), visitas extraordinarias (PG-10).
CREATE TABLE negocio.jornada (
    organizacion_id uuid NOT NULL, sucursal_id uuid NOT NULL,
    id uuid NOT NULL DEFAULT gen_random_uuid(),
    ruta_id uuid NOT NULL,
    fecha date NOT NULL,
    PRIMARY KEY (organizacion_id, sucursal_id, id),
    UNIQUE (organizacion_id, sucursal_id, ruta_id, fecha),
    FOREIGN KEY (organizacion_id, sucursal_id, ruta_id) REFERENCES negocio.ruta ON DELETE RESTRICT
);

CREATE TABLE negocio.estado_jornada (
    organizacion_id uuid NOT NULL, sucursal_id uuid NOT NULL, jornada_id uuid NOT NULL,
    revision integer NOT NULL CHECK (revision > 0),
    estado text NOT NULL CHECK (estado IN ('borrador', 'programada', 'en_curso', 'completada', 'cancelada')),
    comentario text CHECK (comentario IS NULL OR btrim(comentario) <> ''),
    registrado_en timestamptz NOT NULL DEFAULT clock_timestamp(),
    registrado_por_acceso_id uuid DEFAULT seguridad.contexto_acceso(),
    PRIMARY KEY (organizacion_id, sucursal_id, jornada_id, revision),
    FOREIGN KEY (organizacion_id, sucursal_id, jornada_id) REFERENCES negocio.jornada ON DELETE RESTRICT,
    FOREIGN KEY (organizacion_id, registrado_por_acceso_id) REFERENCES seguridad.acceso_autorizado ON DELETE RESTRICT
);
CREATE INDEX estado_jornada_registrado_por ON negocio.estado_jornada (organizacion_id, registrado_por_acceso_id);

DO $$
DECLARE t text;
BEGIN
    FOREACH t IN ARRAY ARRAY['jornada', 'estado_jornada'] LOOP
        EXECUTE format('CREATE TRIGGER conservar_historia BEFORE UPDATE OR DELETE ON negocio.%I FOR EACH ROW EXECUTE FUNCTION infra.inmutable()', t);
        EXECUTE format('ALTER TABLE negocio.%I ENABLE ROW LEVEL SECURITY', t);
        EXECUTE format('CREATE POLICY contexto ON negocio.%I TO manifiestos_app USING (seguridad.puede(organizacion_id, sucursal_id, ''datos.leer'')) WITH CHECK (seguridad.puede(organizacion_id, sucursal_id, ''datos.escribir''))', t);
        EXECUTE format('GRANT SELECT, INSERT ON negocio.%I TO manifiestos_app', t);
    END LOOP;
END $$;
