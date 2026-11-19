-- 0029: sitios independientes de los clientes generales (12-nov-2026).
-- Los clientes GENERALES ya no se ligan a sitios: los sitios normales se dan de alta solos (pantalla Clientes > Sitios) y
-- cualquier general puede ir en cualquier parada. Los sitios de los clientes AUTORIZADOS siguen siendo suyos (cliente_sitio).

-- a) Baja de sitios normales. Un sitio nunca se borra; darlo de baja inserta un registro (insert-only). Las reglas de cuándo se
-- permite (no estar en una ruta vigente) viven en el servidor; los listados excluyen los sitios con baja.
CREATE TABLE negocio.sitio_baja (
    organizacion_id uuid NOT NULL, sucursal_id uuid NOT NULL,
    sitio_id uuid NOT NULL,
    dada_de_baja_en timestamptz NOT NULL DEFAULT clock_timestamp(),
    acceso_id uuid DEFAULT seguridad.contexto_acceso(),
    PRIMARY KEY (organizacion_id, sucursal_id, sitio_id),
    FOREIGN KEY (organizacion_id, sucursal_id, sitio_id) REFERENCES negocio.sitio ON DELETE RESTRICT,
    FOREIGN KEY (organizacion_id, acceso_id) REFERENCES seguridad.acceso_autorizado ON DELETE RESTRICT
);
CREATE INDEX sitio_baja_acceso ON negocio.sitio_baja (organizacion_id, acceso_id);
CREATE TRIGGER conservar_historia BEFORE UPDATE OR DELETE ON negocio.sitio_baja
FOR EACH ROW EXECUTE FUNCTION infra.inmutable();
ALTER TABLE negocio.sitio_baja ENABLE ROW LEVEL SECURITY;
CREATE POLICY contexto ON negocio.sitio_baja TO manifiestos_app
USING (seguridad.puede(organizacion_id, sucursal_id, 'datos.leer'))
WITH CHECK (seguridad.puede(organizacion_id, sucursal_id, 'datos.escribir'));
GRANT SELECT, INSERT ON negocio.sitio_baja TO manifiestos_app;

-- b) Una parada es el sitio: se crea sin clientes y los clientes se agregan después, también en fosas. Se conserva
-- "toda parada tiene revisión" y "los clientes se insertan en la transacción que crea la revisión".
CREATE OR REPLACE FUNCTION infra.exigir_contenido() RETURNS trigger LANGUAGE plpgsql
SECURITY DEFINER SET search_path = pg_catalog AS $$
BEGIN
    IF TG_TABLE_NAME = 'ruta' THEN
        IF NOT EXISTS (SELECT 1 FROM negocio.ruta_version v
            WHERE (v.organizacion_id, v.sucursal_id, v.ruta_id) = (NEW.organizacion_id, NEW.sucursal_id, NEW.id)) THEN
            RAISE EXCEPTION 'Ruta sin datos: falta su primera revisión' USING ERRCODE = '23514';
        END IF;
    ELSIF TG_TABLE_NAME = 'parada' THEN
        IF NOT EXISTS (SELECT 1 FROM negocio.parada_version v
            WHERE (v.organizacion_id, v.sucursal_id, v.parada_id) = (NEW.organizacion_id, NEW.sucursal_id, NEW.id)) THEN
            RAISE EXCEPTION 'Parada sin datos: falta su primera revisión' USING ERRCODE = '23514';
        END IF;
    END IF;
    RETURN NULL;
END $$;
