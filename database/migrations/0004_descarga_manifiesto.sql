-- Aclaración 2026-11-05: cada descarga en PTAR lleva manifiesto.
-- No decide cuántas succiones alimentan una descarga ni acredita convenio = SMA.
CREATE TABLE negocio.descarga (
    organizacion_id uuid NOT NULL, sucursal_id uuid NOT NULL,
    id uuid NOT NULL DEFAULT gen_random_uuid(), cliente_id uuid NOT NULL,
    ficha_vehiculo_id uuid, ficha_sitio_ptar_id uuid,
    ocurrida_en timestamptz,
    PRIMARY KEY (organizacion_id,sucursal_id,id),
    UNIQUE (organizacion_id,sucursal_id,cliente_id,id),
    FOREIGN KEY (organizacion_id,sucursal_id,cliente_id) REFERENCES negocio.cliente ON DELETE RESTRICT,
    FOREIGN KEY (organizacion_id,sucursal_id,ficha_vehiculo_id) REFERENCES negocio.ficha_vehiculo ON DELETE RESTRICT,
    FOREIGN KEY (organizacion_id,sucursal_id,ficha_sitio_ptar_id) REFERENCES negocio.ficha_sitio ON DELETE RESTRICT
);
CREATE TABLE negocio.manifiesto (
    organizacion_id uuid NOT NULL, sucursal_id uuid NOT NULL,
    id uuid NOT NULL DEFAULT gen_random_uuid(), cliente_id uuid NOT NULL, descarga_id uuid,
    PRIMARY KEY (organizacion_id,sucursal_id,id),
    UNIQUE (organizacion_id,sucursal_id,descarga_id),
    FOREIGN KEY (organizacion_id,sucursal_id,cliente_id) REFERENCES negocio.cliente ON DELETE RESTRICT,
    FOREIGN KEY (organizacion_id,sucursal_id,cliente_id,descarga_id) REFERENCES negocio.descarga(organizacion_id,sucursal_id,cliente_id,id) ON DELETE RESTRICT
);
CREATE TABLE negocio.version_manifiesto (
    organizacion_id uuid NOT NULL, sucursal_id uuid NOT NULL,
    id uuid NOT NULL DEFAULT gen_random_uuid(), manifiesto_id uuid NOT NULL,
    revision integer NOT NULL CHECK (revision>0),
    numero_manifiesto text, -- Texto documental; no se asignan folios ni se asume su ámbito UNIQUE.
    ficha_vehiculo_id uuid, version_plantilla text,
    instrucciones_seguridad text, observaciones_recepcion text,
    creada_en timestamptz NOT NULL DEFAULT clock_timestamp(),
    transaccion_creacion xid8 NOT NULL DEFAULT pg_current_xact_id(),
    PRIMARY KEY (organizacion_id,sucursal_id,id),
    UNIQUE (organizacion_id,sucursal_id,manifiesto_id,revision),
    FOREIGN KEY (organizacion_id,sucursal_id,manifiesto_id) REFERENCES negocio.manifiesto ON DELETE RESTRICT,
    FOREIGN KEY (organizacion_id,sucursal_id,ficha_vehiculo_id) REFERENCES negocio.ficha_vehiculo ON DELETE RESTRICT
);
CREATE TABLE negocio.participante_manifiesto (
    organizacion_id uuid NOT NULL, sucursal_id uuid NOT NULL,
    version_id uuid NOT NULL,
    rol text NOT NULL CHECK (rol IN ('generador','transportista','destinatario')),
    empresa_id uuid NOT NULL, ficha_empresa_id uuid NOT NULL, autorizacion_id uuid,
    origen_domicilio text NOT NULL CHECK (origen_domicilio IN ('fiscal','sitio_documental')),
    ficha_sitio_id uuid,
    giro_documental text,
    PRIMARY KEY (organizacion_id,sucursal_id,version_id,rol),
    FOREIGN KEY (organizacion_id,sucursal_id,version_id) REFERENCES negocio.version_manifiesto ON DELETE RESTRICT,
    FOREIGN KEY (organizacion_id,sucursal_id,empresa_id) REFERENCES negocio.empresa_sucursal ON DELETE RESTRICT,
    FOREIGN KEY (organizacion_id,empresa_id,ficha_empresa_id) REFERENCES negocio.ficha_empresa(organizacion_id,empresa_id,id) ON DELETE RESTRICT,
    FOREIGN KEY (organizacion_id,empresa_id,autorizacion_id) REFERENCES negocio.autorizacion_empresa(organizacion_id,empresa_id,id) ON DELETE RESTRICT,
    FOREIGN KEY (organizacion_id,sucursal_id,ficha_sitio_id) REFERENCES negocio.ficha_sitio ON DELETE RESTRICT,
    CHECK ((origen_domicilio='sitio_documental') = (ficha_sitio_id IS NOT NULL))
);
CREATE TABLE negocio.detalle_manifiesto (
    organizacion_id uuid NOT NULL, sucursal_id uuid NOT NULL, version_id uuid NOT NULL,
    renglon integer NOT NULL CHECK (renglon>0),
    descripcion_residuo text, tipo_contenedor text,
    capacidad_contenedor numeric(18,6), unidad_capacidad text,
    numero_contenedores integer CHECK (numero_contenedores>=0),
    cantidad numeric(18,6), unidad_cantidad text,
    naturaleza_cantidad text CHECK (naturaleza_cantidad IN ('estimada','medida')),
    metodo_cuantificacion text, version_metodo text,
    PRIMARY KEY (organizacion_id,sucursal_id,version_id,renglon),
    FOREIGN KEY (organizacion_id,sucursal_id,version_id) REFERENCES negocio.version_manifiesto ON DELETE RESTRICT,
    CHECK (capacidad_contenedor IS NULL OR (capacidad_contenedor>=0 AND capacidad_contenedor<>'NaN'::numeric)),
    CHECK ((capacidad_contenedor IS NULL) = (unidad_capacidad IS NULL)),
    CHECK (cantidad IS NULL OR (cantidad>=0 AND cantidad<>'NaN'::numeric)),
    CHECK ((cantidad IS NULL) = (unidad_cantidad IS NULL)),
    CHECK (cantidad IS NULL OR (naturaleza_cantidad IS NOT NULL AND metodo_cuantificacion IS NOT NULL))
);
-- Adjuntos posteriores no modifican la versión ni fabrican firmas físicas.
CREATE TABLE negocio.documento_manifiesto (
    organizacion_id uuid NOT NULL, sucursal_id uuid NOT NULL,
    id uuid NOT NULL DEFAULT gen_random_uuid(), version_id uuid NOT NULL, archivo_id uuid NOT NULL,
    tipo text NOT NULL CHECK (btrim(tipo)<>''), registrado_en timestamptz NOT NULL DEFAULT clock_timestamp(),
    PRIMARY KEY (organizacion_id,sucursal_id,id),
    FOREIGN KEY (organizacion_id,sucursal_id,version_id) REFERENCES negocio.version_manifiesto ON DELETE RESTRICT,
    FOREIGN KEY (organizacion_id,sucursal_id,archivo_id) REFERENCES negocio.archivo ON DELETE RESTRICT
);
CREATE FUNCTION infra.version_transaccion() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN NEW.transaccion_creacion := pg_current_xact_id(); RETURN NEW; END $$;
CREATE TRIGGER version_transaccion BEFORE INSERT ON negocio.version_manifiesto
FOR EACH ROW EXECUTE FUNCTION infra.version_transaccion();
CREATE FUNCTION infra.agregado_inmutable() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE tx xid8;
BEGIN
    SELECT transaccion_creacion INTO tx FROM negocio.version_manifiesto
    WHERE organizacion_id=NEW.organizacion_id AND sucursal_id=NEW.sucursal_id AND id=NEW.version_id;
    IF tx IS DISTINCT FROM pg_current_xact_id() THEN
        RAISE EXCEPTION 'Los participantes/renglones deben insertarse en la transacción que crea la versión' USING ERRCODE='23514';
    END IF;
    RETURN NEW;
END $$;
CREATE TRIGGER agregado_inmutable BEFORE INSERT ON negocio.participante_manifiesto
FOR EACH ROW EXECUTE FUNCTION infra.agregado_inmutable();
CREATE TRIGGER agregado_inmutable BEFORE INSERT ON negocio.detalle_manifiesto
FOR EACH ROW EXECUTE FUNCTION infra.agregado_inmutable();
CREATE FUNCTION infra.vincular_descarga() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF TG_OP='UPDATE' AND OLD.descarga_id IS NULL AND NEW.descarga_id IS NOT NULL
       AND (to_jsonb(NEW)-'descarga_id')=(to_jsonb(OLD)-'descarga_id') THEN RETURN NEW; END IF;
    RAISE EXCEPTION 'Manifiesto estable: solo puede vincularse a su descarga una vez' USING ERRCODE='23514';
END $$;
CREATE TRIGGER vincular_descarga BEFORE UPDATE OR DELETE ON negocio.manifiesto
FOR EACH ROW EXECUTE FUNCTION infra.vincular_descarga();
DO $$
DECLARE t text;
BEGIN
    FOREACH t IN ARRAY ARRAY['descarga','version_manifiesto','participante_manifiesto','detalle_manifiesto','documento_manifiesto'] LOOP
        EXECUTE format('CREATE TRIGGER conservar_historia BEFORE UPDATE OR DELETE ON negocio.%I FOR EACH ROW EXECUTE FUNCTION infra.inmutable()',t);
    END LOOP;
    FOREACH t IN ARRAY ARRAY['descarga','manifiesto','version_manifiesto','participante_manifiesto','detalle_manifiesto','documento_manifiesto'] LOOP
        EXECUTE format('ALTER TABLE negocio.%I ENABLE ROW LEVEL SECURITY',t);
        EXECUTE format('CREATE POLICY contexto ON negocio.%I TO manifiestos_app USING (seguridad.puede(organizacion_id,sucursal_id,''datos.leer'')) WITH CHECK (seguridad.puede(organizacion_id,sucursal_id,''datos.escribir''))',t);
        EXECUTE format('GRANT SELECT, INSERT ON negocio.%I TO manifiestos_app',t);
    END LOOP;
END $$;
GRANT UPDATE (descarga_id) ON negocio.manifiesto TO manifiestos_app;
CREATE INDEX descarga_vehiculo ON negocio.descarga(organizacion_id,sucursal_id,ficha_vehiculo_id);
CREATE INDEX descarga_ptar ON negocio.descarga(organizacion_id,sucursal_id,ficha_sitio_ptar_id);
CREATE INDEX manifiesto_cliente ON negocio.manifiesto(organizacion_id,sucursal_id,cliente_id,descarga_id);
CREATE INDEX version_vehiculo ON negocio.version_manifiesto(organizacion_id,sucursal_id,ficha_vehiculo_id);
CREATE INDEX participante_empresa ON negocio.participante_manifiesto(organizacion_id,sucursal_id,empresa_id);
CREATE INDEX participante_ficha ON negocio.participante_manifiesto(organizacion_id,empresa_id,ficha_empresa_id);
CREATE INDEX participante_autorizacion ON negocio.participante_manifiesto(organizacion_id,empresa_id,autorizacion_id);
CREATE INDEX participante_sitio ON negocio.participante_manifiesto(organizacion_id,sucursal_id,ficha_sitio_id);
CREATE INDEX documento_version ON negocio.documento_manifiesto(organizacion_id,sucursal_id,version_id);
CREATE INDEX documento_archivo ON negocio.documento_manifiesto(organizacion_id,sucursal_id,archivo_id);
REVOKE ALL ON ALL FUNCTIONS IN SCHEMA infra FROM PUBLIC;

