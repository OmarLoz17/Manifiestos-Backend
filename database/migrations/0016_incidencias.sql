-- Incidencias: el operador no pudo hacer una orden (fosas) o la hizo parcial (sanitarios).
-- Llegan desde la app (pantalla «No pude hacer la orden»); el panel les da seguimiento.
-- Estatus del mockup del usuario: abierta → en_proceso → cerrada. Cada cambio es un seguimiento nuevo
-- (historial inmutable); qué pasa con la orden tras la incidencia sigue pendiente (PG-06).
-- La evidencia es la foto de la app (archivo privado; su almacenamiento en Drive aún no existe) y la
-- ubicación se toma al disparar la foto.
CREATE SEQUENCE negocio.incidencia_numero;
GRANT USAGE ON SEQUENCE negocio.incidencia_numero TO manifiestos_app;

CREATE TABLE negocio.incidencia (
    organizacion_id uuid NOT NULL, sucursal_id uuid NOT NULL,
    id uuid NOT NULL DEFAULT gen_random_uuid(),
    numero bigint NOT NULL DEFAULT nextval('negocio.incidencia_numero') UNIQUE,
    servicio text NOT NULL CHECK (servicio IN ('fosa', 'sanitario')),
    motivo text NOT NULL CHECK (motivo IN ('sin_acceso', 'cliente_ausente', 'unidad_danada', 'servicio_parcial', 'otro')),
    descripcion text CHECK (descripcion IS NULL OR btrim(descripcion) <> ''),
    ocurrida_en timestamptz NOT NULL,
    ruta_id uuid, parada_id uuid, cliente_id uuid, vehiculo_id uuid, persona_id uuid,
    banos_asignados integer CHECK (banos_asignados >= 0),
    banos_atendidos integer CHECK (banos_atendidos >= 0),
    evidencia_archivo_id uuid,
    latitud numeric(9,6) CHECK (latitud BETWEEN -90 AND 90),
    longitud numeric(9,6) CHECK (longitud BETWEEN -180 AND 180),
    precision_m numeric(8,1) CHECK (precision_m >= 0),
    registrada_en timestamptz NOT NULL DEFAULT clock_timestamp(),
    registrada_por_acceso_id uuid DEFAULT seguridad.contexto_acceso(),
    PRIMARY KEY (organizacion_id, sucursal_id, id),
    FOREIGN KEY (organizacion_id, sucursal_id, ruta_id) REFERENCES negocio.ruta ON DELETE RESTRICT,
    FOREIGN KEY (organizacion_id, sucursal_id, parada_id) REFERENCES negocio.parada ON DELETE RESTRICT,
    FOREIGN KEY (organizacion_id, sucursal_id, cliente_id) REFERENCES negocio.cliente ON DELETE RESTRICT,
    FOREIGN KEY (organizacion_id, sucursal_id, vehiculo_id) REFERENCES negocio.vehiculo ON DELETE RESTRICT,
    FOREIGN KEY (organizacion_id, persona_id) REFERENCES seguridad.persona ON DELETE RESTRICT,
    FOREIGN KEY (organizacion_id, sucursal_id, evidencia_archivo_id) REFERENCES negocio.archivo ON DELETE RESTRICT,
    FOREIGN KEY (organizacion_id, registrada_por_acceso_id) REFERENCES seguridad.acceso_autorizado ON DELETE RESTRICT,
    CHECK ((latitud IS NULL) = (longitud IS NULL)),
    CHECK (banos_atendidos IS NULL OR banos_asignados IS NULL OR banos_atendidos <= banos_asignados),
    CHECK (servicio = 'sanitario' OR (banos_asignados IS NULL AND banos_atendidos IS NULL))
);
CREATE INDEX incidencia_fecha ON negocio.incidencia (organizacion_id, sucursal_id, ocurrida_en);
CREATE INDEX incidencia_ruta ON negocio.incidencia (organizacion_id, sucursal_id, ruta_id);
CREATE INDEX incidencia_parada ON negocio.incidencia (organizacion_id, sucursal_id, parada_id);
CREATE INDEX incidencia_cliente ON negocio.incidencia (organizacion_id, sucursal_id, cliente_id);
CREATE INDEX incidencia_vehiculo ON negocio.incidencia (organizacion_id, sucursal_id, vehiculo_id);
CREATE INDEX incidencia_persona ON negocio.incidencia (organizacion_id, persona_id);
CREATE INDEX incidencia_archivo ON negocio.incidencia (organizacion_id, sucursal_id, evidencia_archivo_id);
CREATE INDEX incidencia_registrada_por ON negocio.incidencia (organizacion_id, registrada_por_acceso_id);

CREATE TABLE negocio.seguimiento_incidencia (
    organizacion_id uuid NOT NULL, sucursal_id uuid NOT NULL, incidencia_id uuid NOT NULL,
    revision integer NOT NULL CHECK (revision > 0),
    estatus text NOT NULL CHECK (estatus IN ('abierta', 'en_proceso', 'cerrada')),
    comentario text CHECK (comentario IS NULL OR btrim(comentario) <> ''),
    registrado_en timestamptz NOT NULL DEFAULT clock_timestamp(),
    registrado_por_acceso_id uuid DEFAULT seguridad.contexto_acceso(),
    PRIMARY KEY (organizacion_id, sucursal_id, incidencia_id, revision),
    FOREIGN KEY (organizacion_id, sucursal_id, incidencia_id) REFERENCES negocio.incidencia ON DELETE RESTRICT,
    FOREIGN KEY (organizacion_id, registrado_por_acceso_id) REFERENCES seguridad.acceso_autorizado ON DELETE RESTRICT
);
CREATE INDEX seguimiento_incidencia_registrado_por ON negocio.seguimiento_incidencia (organizacion_id, registrado_por_acceso_id);

DO $$
DECLARE t text;
BEGIN
    FOREACH t IN ARRAY ARRAY['incidencia', 'seguimiento_incidencia'] LOOP
        EXECUTE format('CREATE TRIGGER conservar_historia BEFORE UPDATE OR DELETE ON negocio.%I FOR EACH ROW EXECUTE FUNCTION infra.inmutable()', t);
        EXECUTE format('ALTER TABLE negocio.%I ENABLE ROW LEVEL SECURITY', t);
        EXECUTE format('CREATE POLICY contexto ON negocio.%I TO manifiestos_app USING (seguridad.puede(organizacion_id, sucursal_id, ''datos.leer'')) WITH CHECK (seguridad.puede(organizacion_id, sucursal_id, ''datos.escribir''))', t);
        EXECUTE format('GRANT SELECT, INSERT ON negocio.%I TO manifiestos_app', t);
    END LOOP;
END $$;

-- Toda incidencia nace abierta, en la misma transacción.
CREATE FUNCTION infra.incidencia_abierta() RETURNS trigger LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog AS $$
BEGIN
    INSERT INTO negocio.seguimiento_incidencia (organizacion_id, sucursal_id, incidencia_id, revision, estatus, registrado_por_acceso_id)
    VALUES (NEW.organizacion_id, NEW.sucursal_id, NEW.id, 1, 'abierta', NEW.registrada_por_acceso_id);
    RETURN NEW;
END $$;
CREATE TRIGGER nace_abierta AFTER INSERT ON negocio.incidencia FOR EACH ROW EXECUTE FUNCTION infra.incidencia_abierta();
REVOKE ALL ON FUNCTION infra.incidencia_abierta() FROM PUBLIC;

-- Quien reportó una incidencia visible también es visible (nombre del operador en el panel).
CREATE OR REPLACE FUNCTION seguridad.persona_visible(o uuid, p uuid) RETURNS boolean
LANGUAGE sql STABLE SECURITY DEFINER SET search_path = pg_catalog AS $$
    SELECT EXISTS (SELECT 1 FROM seguridad.asignacion_equipo a WHERE a.organizacion_id=o AND a.persona_id=p
        AND seguridad.puede(o,a.sucursal_id,'datos.leer'))
    OR EXISTS (SELECT 1 FROM negocio.asignacion_unidad a WHERE a.organizacion_id=o AND a.persona_id=p
        AND seguridad.puede(o,a.sucursal_id,'datos.leer'))
    OR EXISTS (SELECT 1 FROM negocio.operador_ficha f WHERE f.organizacion_id=o AND f.persona_id=p
        AND seguridad.puede(o,f.sucursal_id,'datos.leer'))
    OR EXISTS (SELECT 1 FROM negocio.incidencia i WHERE i.organizacion_id=o AND i.persona_id=p
        AND seguridad.puede(o,i.sucursal_id,'datos.leer'))
$$;
