-- Rutas recurrentes y paradas (acuerdos con el usuario del 2026-11-05):
-- - Una ruta es un plan fijo que corre ciertos días de la semana (p. ej. L·M·V o M·J·S).
-- - La ruta se asocia a una unidad y la unidad a un operador.
-- - Una parada es un punto de servicio (donde se succiona). Una PTAR NO es parada: ahí se descarga.
-- - Una parada atiende a uno o varios clientes, con los baños planeados de cada uno
--   (los que se atienden ese día, no los que tiene el cliente).
-- - El orden de las paradas es solo visual para operaciones; la app del operador no lo usa.
-- - Operaciones puede corregir el plan: cada cambio es una revisión nueva, nada se sobrescribe.
-- Sigue abierto (PG-10): estados, cancelaciones, servicio del día y corrección de realizados.

-- La ruta "por fecha" de 0007 se sustituye. Autorizado por el usuario porque está vacía;
-- si tuviera datos, la migración se detiene en lugar de borrarlos.
DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM negocio.asignacion_ruta) OR EXISTS (SELECT 1 FROM negocio.ruta) THEN
        RAISE EXCEPTION 'Hay rutas registradas en el modelo de 0007; la 0008 no elimina datos' USING ERRCODE = '55000';
    END IF;
END $$;
DROP TABLE negocio.asignacion_ruta;
DROP TABLE negocio.ruta;

-- Sitios donde se presta servicio. Las PTAR (sitios de descarga) no se registran aquí,
-- así que no pueden elegirse como parada.
CREATE TABLE negocio.punto_servicio (
    organizacion_id uuid NOT NULL, sucursal_id uuid NOT NULL, sitio_id uuid NOT NULL,
    registrado_en timestamptz NOT NULL DEFAULT clock_timestamp(),
    registrado_por_acceso_id uuid DEFAULT seguridad.contexto_acceso(),
    PRIMARY KEY (organizacion_id, sucursal_id, sitio_id),
    FOREIGN KEY (organizacion_id, sucursal_id, sitio_id) REFERENCES negocio.sitio ON DELETE RESTRICT,
    FOREIGN KEY (organizacion_id, registrado_por_acceso_id) REFERENCES seguridad.acceso_autorizado ON DELETE RESTRICT
);

-- Operador de cada unidad, con historial. persona_id NULL = unidad sin operador.
CREATE TABLE negocio.asignacion_unidad (
    organizacion_id uuid NOT NULL, sucursal_id uuid NOT NULL,
    id uuid NOT NULL DEFAULT gen_random_uuid(),
    vehiculo_id uuid NOT NULL,
    revision integer NOT NULL CHECK (revision > 0),
    persona_id uuid,
    registrada_en timestamptz NOT NULL DEFAULT clock_timestamp(),
    registrada_por_acceso_id uuid DEFAULT seguridad.contexto_acceso(),
    PRIMARY KEY (organizacion_id, sucursal_id, id),
    UNIQUE (organizacion_id, sucursal_id, vehiculo_id, revision),
    FOREIGN KEY (organizacion_id, sucursal_id, vehiculo_id) REFERENCES negocio.vehiculo ON DELETE RESTRICT,
    FOREIGN KEY (organizacion_id, persona_id) REFERENCES seguridad.persona ON DELETE RESTRICT,
    FOREIGN KEY (organizacion_id, registrada_por_acceso_id) REFERENCES seguridad.acceso_autorizado ON DELETE RESTRICT
);

-- Identidad estable de la ruta; sus datos viven en revisiones.
CREATE TABLE negocio.ruta (
    organizacion_id uuid NOT NULL, sucursal_id uuid NOT NULL,
    id uuid NOT NULL DEFAULT gen_random_uuid(),
    creada_en timestamptz NOT NULL DEFAULT clock_timestamp(),
    creada_por_acceso_id uuid DEFAULT seguridad.contexto_acceso(),
    PRIMARY KEY (organizacion_id, sucursal_id, id),
    FOREIGN KEY (organizacion_id, sucursal_id) REFERENCES negocio.sucursal ON DELETE RESTRICT,
    FOREIGN KEY (organizacion_id, creada_por_acceso_id) REFERENCES seguridad.acceso_autorizado ON DELETE RESTRICT
);

-- dias: máscara de bits, lunes = 1, martes = 2, miércoles = 4 … domingo = 64 (ISO, lunes primero).
-- Una sola representación por combinación de días; al menos un día.
CREATE TABLE negocio.ruta_version (
    organizacion_id uuid NOT NULL, sucursal_id uuid NOT NULL,
    id uuid NOT NULL DEFAULT gen_random_uuid(),
    ruta_id uuid NOT NULL,
    revision integer NOT NULL CHECK (revision > 0),
    nombre text NOT NULL CHECK (btrim(nombre) <> '' AND length(nombre) <= 120),
    dias smallint NOT NULL CHECK (dias BETWEEN 1 AND 127),
    vehiculo_id uuid NOT NULL,
    registrada_en timestamptz NOT NULL DEFAULT clock_timestamp(),
    registrada_por_acceso_id uuid DEFAULT seguridad.contexto_acceso(),
    PRIMARY KEY (organizacion_id, sucursal_id, id),
    UNIQUE (organizacion_id, sucursal_id, ruta_id, revision),
    FOREIGN KEY (organizacion_id, sucursal_id, ruta_id) REFERENCES negocio.ruta ON DELETE RESTRICT,
    FOREIGN KEY (organizacion_id, sucursal_id, vehiculo_id) REFERENCES negocio.vehiculo ON DELETE RESTRICT,
    FOREIGN KEY (organizacion_id, registrada_por_acceso_id) REFERENCES seguridad.acceso_autorizado ON DELETE RESTRICT
);

-- Identidad estable de la parada. posicion = orden visual (lo único modificable);
-- única por ruta, comprobada al confirmar para permitir intercambios en una transacción.
CREATE TABLE negocio.parada (
    organizacion_id uuid NOT NULL, sucursal_id uuid NOT NULL,
    id uuid NOT NULL DEFAULT gen_random_uuid(),
    ruta_id uuid NOT NULL,
    posicion integer NOT NULL CHECK (posicion > 0),
    creada_en timestamptz NOT NULL DEFAULT clock_timestamp(),
    creada_por_acceso_id uuid DEFAULT seguridad.contexto_acceso(),
    PRIMARY KEY (organizacion_id, sucursal_id, id),
    CONSTRAINT parada_posicion_unica UNIQUE (organizacion_id, sucursal_id, ruta_id, posicion) DEFERRABLE INITIALLY DEFERRED,
    FOREIGN KEY (organizacion_id, sucursal_id, ruta_id) REFERENCES negocio.ruta ON DELETE RESTRICT,
    FOREIGN KEY (organizacion_id, creada_por_acceso_id) REFERENCES seguridad.acceso_autorizado ON DELETE RESTRICT
);

CREATE TABLE negocio.parada_version (
    organizacion_id uuid NOT NULL, sucursal_id uuid NOT NULL,
    id uuid NOT NULL DEFAULT gen_random_uuid(),
    parada_id uuid NOT NULL,
    revision integer NOT NULL CHECK (revision > 0),
    sitio_id uuid NOT NULL,
    registrada_en timestamptz NOT NULL DEFAULT clock_timestamp(),
    registrada_por_acceso_id uuid DEFAULT seguridad.contexto_acceso(),
    transaccion_creacion xid8 NOT NULL DEFAULT pg_current_xact_id(),
    PRIMARY KEY (organizacion_id, sucursal_id, id),
    UNIQUE (organizacion_id, sucursal_id, parada_id, revision),
    FOREIGN KEY (organizacion_id, sucursal_id, parada_id) REFERENCES negocio.parada ON DELETE RESTRICT,
    FOREIGN KEY (organizacion_id, sucursal_id, sitio_id) REFERENCES negocio.punto_servicio ON DELETE RESTRICT,
    FOREIGN KEY (organizacion_id, registrada_por_acceso_id) REFERENCES seguridad.acceso_autorizado ON DELETE RESTRICT
);

-- Clientes de una revisión de parada. banos_planeados = piezas a atender ese día (no volumen).
-- NULL reservado para servicios sin baños (p. ej. fosas). Los litros se estiman fuera de la base.
CREATE TABLE negocio.parada_cliente (
    organizacion_id uuid NOT NULL, sucursal_id uuid NOT NULL,
    version_id uuid NOT NULL,
    cliente_id uuid NOT NULL,
    banos_planeados integer CHECK (banos_planeados > 0),
    PRIMARY KEY (organizacion_id, sucursal_id, version_id, cliente_id),
    FOREIGN KEY (organizacion_id, sucursal_id, version_id) REFERENCES negocio.parada_version ON DELETE RESTRICT,
    FOREIGN KEY (organizacion_id, sucursal_id, cliente_id) REFERENCES negocio.cliente ON DELETE RESTRICT
);

CREATE INDEX punto_servicio_registrado_por ON negocio.punto_servicio (organizacion_id, registrado_por_acceso_id);
CREATE INDEX asignacion_unidad_persona ON negocio.asignacion_unidad (organizacion_id, persona_id);
CREATE INDEX asignacion_unidad_registrada_por ON negocio.asignacion_unidad (organizacion_id, registrada_por_acceso_id);
CREATE INDEX ruta_creada_por ON negocio.ruta (organizacion_id, creada_por_acceso_id);
CREATE INDEX ruta_version_vehiculo ON negocio.ruta_version (organizacion_id, sucursal_id, vehiculo_id);
CREATE INDEX ruta_version_registrada_por ON negocio.ruta_version (organizacion_id, registrada_por_acceso_id);
CREATE INDEX parada_creada_por ON negocio.parada (organizacion_id, creada_por_acceso_id);
CREATE INDEX parada_version_sitio ON negocio.parada_version (organizacion_id, sucursal_id, sitio_id);
CREATE INDEX parada_version_registrada_por ON negocio.parada_version (organizacion_id, registrada_por_acceso_id);
CREATE INDEX parada_cliente_cliente ON negocio.parada_cliente (organizacion_id, sucursal_id, cliente_id);

-- La parada solo admite cambiar su posición; nunca se borra.
CREATE FUNCTION infra.solo_posicion_parada() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF TG_OP = 'UPDATE' AND (to_jsonb(NEW) - 'posicion') = (to_jsonb(OLD) - 'posicion') THEN
        RETURN NEW;
    END IF;
    RAISE EXCEPTION 'La parada solo admite cambiar su posición' USING ERRCODE = '23514';
END $$;
CREATE TRIGGER conservar_parada BEFORE UPDATE OR DELETE ON negocio.parada
FOR EACH ROW EXECUTE FUNCTION infra.solo_posicion_parada();

-- Los clientes se insertan en la misma transacción que crea la revisión de la parada.
CREATE TRIGGER version_transaccion BEFORE INSERT ON negocio.parada_version
FOR EACH ROW EXECUTE FUNCTION infra.version_transaccion();
CREATE FUNCTION infra.cliente_en_version_nueva() RETURNS trigger LANGUAGE plpgsql
SECURITY DEFINER SET search_path = pg_catalog AS $$
DECLARE tx xid8;
BEGIN
    SELECT v.transaccion_creacion INTO tx FROM negocio.parada_version v
    WHERE v.organizacion_id = NEW.organizacion_id AND v.sucursal_id = NEW.sucursal_id AND v.id = NEW.version_id;
    IF tx IS DISTINCT FROM pg_current_xact_id() THEN
        RAISE EXCEPTION 'Los clientes deben insertarse en la transacción que crea la revisión de la parada' USING ERRCODE = '23514';
    END IF;
    RETURN NEW;
END $$;
CREATE TRIGGER agregado_inmutable BEFORE INSERT ON negocio.parada_cliente
FOR EACH ROW EXECUTE FUNCTION infra.cliente_en_version_nueva();

-- Integridad al confirmar: toda ruta tiene revisión, toda parada tiene revisión
-- y toda revisión de parada tiene al menos un cliente.
CREATE FUNCTION infra.exigir_contenido() RETURNS trigger LANGUAGE plpgsql
SECURITY DEFINER SET search_path = pg_catalog AS $$
BEGIN
    IF TG_TABLE_NAME = 'ruta' AND NOT EXISTS (SELECT 1 FROM negocio.ruta_version v
        WHERE (v.organizacion_id, v.sucursal_id, v.ruta_id) = (NEW.organizacion_id, NEW.sucursal_id, NEW.id)) THEN
        RAISE EXCEPTION 'Ruta sin datos: falta su primera revisión' USING ERRCODE = '23514';
    ELSIF TG_TABLE_NAME = 'parada' AND NOT EXISTS (SELECT 1 FROM negocio.parada_version v
        WHERE (v.organizacion_id, v.sucursal_id, v.parada_id) = (NEW.organizacion_id, NEW.sucursal_id, NEW.id)) THEN
        RAISE EXCEPTION 'Parada sin datos: falta su primera revisión' USING ERRCODE = '23514';
    ELSIF TG_TABLE_NAME = 'parada_version' AND NOT EXISTS (SELECT 1 FROM negocio.parada_cliente c
        WHERE (c.organizacion_id, c.sucursal_id, c.version_id) = (NEW.organizacion_id, NEW.sucursal_id, NEW.id)) THEN
        RAISE EXCEPTION 'Parada sin clientes' USING ERRCODE = '23514';
    END IF;
    RETURN NULL;
END $$;
CREATE CONSTRAINT TRIGGER exigir_contenido AFTER INSERT ON negocio.ruta
DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION infra.exigir_contenido();
CREATE CONSTRAINT TRIGGER exigir_contenido AFTER INSERT ON negocio.parada
DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION infra.exigir_contenido();
CREATE CONSTRAINT TRIGGER exigir_contenido AFTER INSERT ON negocio.parada_version
DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION infra.exigir_contenido();
REVOKE ALL ON FUNCTION infra.solo_posicion_parada(), infra.cliente_en_version_nueva(), infra.exigir_contenido() FROM PUBLIC;

DO $$
DECLARE t text;
BEGIN
    FOREACH t IN ARRAY ARRAY['punto_servicio','asignacion_unidad','ruta','ruta_version','parada_version','parada_cliente'] LOOP
        EXECUTE format('CREATE TRIGGER conservar_historia BEFORE UPDATE OR DELETE ON negocio.%I FOR EACH ROW EXECUTE FUNCTION infra.inmutable()',t);
    END LOOP;
    FOREACH t IN ARRAY ARRAY['punto_servicio','asignacion_unidad','ruta','ruta_version','parada','parada_version','parada_cliente'] LOOP
        EXECUTE format('ALTER TABLE negocio.%I ENABLE ROW LEVEL SECURITY',t);
        EXECUTE format('CREATE POLICY contexto ON negocio.%I TO manifiestos_app USING (seguridad.puede(organizacion_id,sucursal_id,''datos.leer'')) WITH CHECK (seguridad.puede(organizacion_id,sucursal_id,''datos.escribir''))',t);
        EXECUTE format('GRANT SELECT, INSERT ON negocio.%I TO manifiestos_app',t);
    END LOOP;
END $$;
GRANT UPDATE (posicion) ON negocio.parada TO manifiestos_app;
-- Alta de puntos de servicio desde el panel (sitio + su primera ficha).
GRANT INSERT ON negocio.sitio, negocio.ficha_sitio TO manifiestos_app;

-- Persona visible: con equipo asignado (0003) o como operador de una unidad visible.
CREATE OR REPLACE FUNCTION seguridad.persona_visible(o uuid, p uuid) RETURNS boolean
LANGUAGE sql STABLE SECURITY DEFINER SET search_path = pg_catalog AS $$
    SELECT EXISTS (SELECT 1 FROM seguridad.asignacion_equipo a WHERE a.organizacion_id=o AND a.persona_id=p
        AND seguridad.puede(o,a.sucursal_id,'datos.leer'))
    OR EXISTS (SELECT 1 FROM negocio.asignacion_unidad a WHERE a.organizacion_id=o AND a.persona_id=p
        AND seguridad.puede(o,a.sucursal_id,'datos.leer'))
$$;
