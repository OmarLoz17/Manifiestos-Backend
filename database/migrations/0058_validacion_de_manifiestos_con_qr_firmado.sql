-- QR de validación del manifiesto (decisión 19-nov-2026, aprobada con mockup). El QR impreso lleva el id de un registro de validación y la firma digital
-- (ECDSA P-256 con la llave privada del servidor, que nunca sale de él). La página pública recalcula nada: lee el registro inmutable, comprueba la firma con la
-- llave pública y enseña los datos emitidos para compararlos con el papel («Verificar lo impreso»).
--   * negocio.codigo_validacion_manifiesto: copia congelada (JSON canónico) de lo que se imprime + su huella SHA-256 + la firma. Solo inserta; una huella nueva = otro registro.
--   * negocio.escaneo_validacion: bitácora de consultas públicas (quién/cuándo no se identifica; solo una huella del origen). Alimenta un futuro aviso a Operaciones.
--   * seguridad.registrar_codigo_validacion: lo llama el panel con sesión (datos.leer: imprimir es leer). Idempotente por (manifiesto, huella).
--   * seguridad.consultar_codigo_validacion / registrar_escaneo_validacion: las usa la página pública SIN sesión; devuelven solo lo mínimo.
CREATE TABLE negocio.codigo_validacion_manifiesto (
    id uuid NOT NULL DEFAULT gen_random_uuid(),
    organizacion_id uuid NOT NULL, sucursal_id uuid NOT NULL, manifiesto_id uuid NOT NULL,
    revision integer NOT NULL CHECK (revision > 0),
    contenido text NOT NULL,
    huella bytea NOT NULL CHECK (length(huella) = 32),
    firma bytea NOT NULL CHECK (length(firma) BETWEEN 8 AND 200),
    llave_id text NOT NULL,
    creada_en timestamptz NOT NULL DEFAULT clock_timestamp(),
    PRIMARY KEY (id),
    UNIQUE (organizacion_id, sucursal_id, manifiesto_id, huella),
    FOREIGN KEY (organizacion_id, sucursal_id, manifiesto_id) REFERENCES negocio.manifiesto ON DELETE RESTRICT
);
CREATE TRIGGER conservar_historia BEFORE UPDATE OR DELETE ON negocio.codigo_validacion_manifiesto FOR EACH ROW EXECUTE FUNCTION infra.inmutable();
ALTER TABLE negocio.codigo_validacion_manifiesto ENABLE ROW LEVEL SECURITY;
CREATE POLICY contexto ON negocio.codigo_validacion_manifiesto TO manifiestos_app USING (seguridad.puede(organizacion_id, sucursal_id, 'datos.leer'));
GRANT SELECT ON negocio.codigo_validacion_manifiesto TO manifiestos_app;

CREATE TABLE negocio.escaneo_validacion (
    id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    codigo_id uuid NOT NULL REFERENCES negocio.codigo_validacion_manifiesto ON DELETE RESTRICT,
    resultado text NOT NULL CHECK (resultado IN ('autentico', 'no_autentico', 'reemplazado', 'verificacion_coincide', 'verificacion_no_coincide')),
    origen text, -- huella corta del origen (no la dirección), solo para detectar muchos escaneos desde lugares distintos
    en timestamptz NOT NULL DEFAULT clock_timestamp()
);
CREATE INDEX escaneo_validacion_codigo ON negocio.escaneo_validacion (codigo_id, en);
CREATE TRIGGER conservar_historia BEFORE UPDATE OR DELETE ON negocio.escaneo_validacion FOR EACH ROW EXECUTE FUNCTION infra.inmutable();
ALTER TABLE negocio.escaneo_validacion ENABLE ROW LEVEL SECURITY;

-- Panel (con sesión): congela y registra lo que se imprime. Devuelve el id del registro (existente si la huella ya estaba).
CREATE FUNCTION seguridad.registrar_codigo_validacion(p_manifiesto uuid, p_revision integer, p_contenido text, p_huella bytea, p_firma bytea, p_llave text)
RETURNS uuid LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog AS $$
DECLARE m record; r uuid;
BEGIN
    SELECT organizacion_id, sucursal_id INTO m FROM negocio.manifiesto WHERE id = p_manifiesto;
    IF NOT FOUND OR NOT seguridad.puede(m.organizacion_id, m.sucursal_id, 'datos.leer') THEN
        RAISE EXCEPTION 'Contexto no autorizado' USING ERRCODE = '42501';
    END IF;
    SELECT id INTO r FROM negocio.codigo_validacion_manifiesto
        WHERE (organizacion_id, sucursal_id, manifiesto_id, huella) = (m.organizacion_id, m.sucursal_id, p_manifiesto, p_huella);
    IF r IS NULL THEN
        INSERT INTO negocio.codigo_validacion_manifiesto (organizacion_id, sucursal_id, manifiesto_id, revision, contenido, huella, firma, llave_id)
        VALUES (m.organizacion_id, m.sucursal_id, p_manifiesto, p_revision, p_contenido, p_huella, p_firma, p_llave) RETURNING id INTO r;
    END IF;
    RETURN r;
END $$;
REVOKE ALL ON FUNCTION seguridad.registrar_codigo_validacion(uuid, integer, text, bytea, bytea, text) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION seguridad.registrar_codigo_validacion(uuid, integer, text, bytea, bytea, text) TO manifiestos_app;

-- Página pública (sin sesión): solo lo necesario para comprobar la firma y enseñar los datos emitidos. «revision_actual» detecta un manifiesto reemplazado.
CREATE FUNCTION seguridad.consultar_codigo_validacion(p_id uuid)
RETURNS TABLE (contenido text, huella bytea, firma bytea, llave_id text, creada_en timestamptz, revision integer, revision_actual integer)
LANGUAGE sql STABLE SECURITY DEFINER SET search_path = pg_catalog AS $$
    SELECT c.contenido, c.huella, c.firma, c.llave_id, c.creada_en, c.revision,
           (SELECT max(v.revision) FROM negocio.version_manifiesto v
             WHERE (v.organizacion_id, v.sucursal_id, v.manifiesto_id) = (c.organizacion_id, c.sucursal_id, c.manifiesto_id))
    FROM negocio.codigo_validacion_manifiesto c WHERE c.id = p_id
$$;
REVOKE ALL ON FUNCTION seguridad.consultar_codigo_validacion(uuid) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION seguridad.consultar_codigo_validacion(uuid) TO manifiestos_app;

CREATE FUNCTION seguridad.registrar_escaneo_validacion(p_id uuid, p_resultado text, p_origen text) RETURNS void
LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog AS $$
BEGIN
    IF EXISTS (SELECT 1 FROM negocio.codigo_validacion_manifiesto WHERE id = p_id) THEN
        INSERT INTO negocio.escaneo_validacion (codigo_id, resultado, origen) VALUES (p_id, p_resultado, left(p_origen, 32));
    END IF;
END $$;
REVOKE ALL ON FUNCTION seguridad.registrar_escaneo_validacion(uuid, text, text) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION seguridad.registrar_escaneo_validacion(uuid, text, text) TO manifiestos_app;
