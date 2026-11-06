-- Alta y corrección de clientes desde el panel (pantalla Clientes, 2026-11-06).
-- - Cliente autorizado = su empresa tiene autorización de medio ambiente como generador
--   (autoridad 'Medio ambiente', finalidad 'Generador'; confirmado por el usuario). Cliente general = sin ella.
-- - Corregir datos crea una ficha nueva (revisión); nada se sobrescribe.
-- - Cambiar el número de autorización registra una autorización nueva; la vigente es la última registrada.
--   Quitar una autorización no está definido todavía y se rechaza.
-- Las altas pasan por funciones SECURITY DEFINER porque una empresa recién creada aún no es
-- visible por RLS hasta ligarla a la sucursal; las funciones exigen datos.escribir en esa sucursal.

ALTER TABLE negocio.autorizacion_empresa
    ADD COLUMN registrada_en timestamptz NOT NULL DEFAULT clock_timestamp(),
    ADD COLUMN registrada_por_acceso_id uuid DEFAULT seguridad.contexto_acceso();
ALTER TABLE negocio.autorizacion_empresa
    ADD FOREIGN KEY (organizacion_id, registrada_por_acceso_id) REFERENCES seguridad.acceso_autorizado ON DELETE RESTRICT;
CREATE INDEX autorizacion_empresa_registrada_por ON negocio.autorizacion_empresa (organizacion_id, registrada_por_acceso_id);
CREATE INDEX autorizacion_empresa_vigente ON negocio.autorizacion_empresa (organizacion_id, empresa_id, autoridad, finalidad, registrada_en);
ALTER TABLE negocio.ficha_empresa
    ADD COLUMN registrada_por_acceso_id uuid DEFAULT seguridad.contexto_acceso();
ALTER TABLE negocio.ficha_empresa
    ADD FOREIGN KEY (organizacion_id, registrada_por_acceso_id) REFERENCES seguridad.acceso_autorizado ON DELETE RESTRICT;
CREATE INDEX ficha_empresa_registrada_por ON negocio.ficha_empresa (organizacion_id, registrada_por_acceso_id);

-- Texto recortado; vacío = NULL.
CREATE FUNCTION infra.texto(t text) RETURNS text LANGUAGE sql IMMUTABLE AS $$ SELECT nullif(btrim(t), '') $$;

CREATE FUNCTION negocio.alta_cliente(
    o uuid, s uuid,
    p_nombre_fiscal text, p_nombre_comercial text, p_rfc text,
    p_calle text, p_numero_exterior text, p_colonia text, p_codigo_postal text, p_municipio text, p_estado text,
    p_telefono text, p_correo text, p_autorizacion text)
RETURNS uuid
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path = pg_catalog AS $$
DECLARE e uuid; c uuid;
BEGIN
    IF NOT coalesce(seguridad.puede(o, s, 'datos.escribir'), false) THEN
        RAISE EXCEPTION 'Sin permiso de escritura en la sucursal' USING ERRCODE = '42501';
    END IF;
    IF infra.texto(p_nombre_fiscal) IS NULL AND infra.texto(p_nombre_comercial) IS NULL THEN
        RAISE EXCEPTION 'El cliente necesita un nombre' USING ERRCODE = '22023';
    END IF;
    INSERT INTO negocio.empresa (organizacion_id, codigo)
    VALUES (o, 'CLI-' || upper(substr(replace(gen_random_uuid()::text, '-', ''), 1, 12)))
    RETURNING id INTO e;
    INSERT INTO negocio.ficha_empresa (organizacion_id, empresa_id, revision, nombre_fiscal, nombre_comercial, rfc,
        calle, numero_exterior, colonia, codigo_postal, municipio, estado, telefono, correo)
    VALUES (o, e, 1, infra.texto(p_nombre_fiscal), infra.texto(p_nombre_comercial), infra.texto(p_rfc),
        infra.texto(p_calle), infra.texto(p_numero_exterior), infra.texto(p_colonia), infra.texto(p_codigo_postal),
        infra.texto(p_municipio), infra.texto(p_estado), infra.texto(p_telefono), infra.texto(p_correo));
    INSERT INTO negocio.empresa_sucursal VALUES (o, s, e);
    INSERT INTO negocio.cliente (organizacion_id, sucursal_id, empresa_id) VALUES (o, s, e) RETURNING id INTO c;
    IF infra.texto(p_autorizacion) IS NOT NULL THEN
        INSERT INTO negocio.autorizacion_empresa (organizacion_id, empresa_id, autoridad, finalidad, numero)
        VALUES (o, e, 'Medio ambiente', 'Generador', infra.texto(p_autorizacion));
    END IF;
    RETURN c;
END $$;

-- Devuelve la revisión vigente de la ficha tras el cambio. p_revision = la que se editó;
-- si ya no es la última, SQLSTATE 40001 (el API responde 409).
CREATE FUNCTION negocio.corregir_cliente(
    o uuid, s uuid, p_cliente uuid, p_revision integer,
    p_nombre_fiscal text, p_nombre_comercial text, p_rfc text,
    p_calle text, p_numero_exterior text, p_colonia text, p_codigo_postal text, p_municipio text, p_estado text,
    p_telefono text, p_correo text, p_autorizacion text)
RETURNS integer
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path = pg_catalog AS $$
DECLARE e uuid; f negocio.ficha_empresa%ROWTYPE; aut text; nueva negocio.ficha_empresa%ROWTYPE;
BEGIN
    IF NOT coalesce(seguridad.puede(o, s, 'datos.escribir'), false) THEN
        RAISE EXCEPTION 'Sin permiso de escritura en la sucursal' USING ERRCODE = '42501';
    END IF;
    SELECT c.empresa_id INTO e FROM negocio.cliente c WHERE c.organizacion_id = o AND c.sucursal_id = s AND c.id = p_cliente;
    IF e IS NULL THEN
        RAISE EXCEPTION 'Cliente no encontrado' USING ERRCODE = 'P0002';
    END IF;
    IF infra.texto(p_nombre_fiscal) IS NULL AND infra.texto(p_nombre_comercial) IS NULL THEN
        RAISE EXCEPTION 'El cliente necesita un nombre' USING ERRCODE = '22023';
    END IF;
    PERFORM pg_advisory_xact_lock(hashtextextended('empresa:' || o::text || ':' || e::text, 0));
    SELECT * INTO f FROM negocio.ficha_empresa x WHERE x.organizacion_id = o AND x.empresa_id = e ORDER BY x.revision DESC LIMIT 1;
    IF coalesce(f.revision, 0) <> p_revision THEN
        RAISE EXCEPTION 'La ficha del cliente cambió' USING ERRCODE = '40001';
    END IF;

    nueva := f;
    nueva.nombre_fiscal := infra.texto(p_nombre_fiscal); nueva.nombre_comercial := infra.texto(p_nombre_comercial);
    nueva.rfc := infra.texto(p_rfc); nueva.calle := infra.texto(p_calle); nueva.numero_exterior := infra.texto(p_numero_exterior);
    nueva.colonia := infra.texto(p_colonia); nueva.codigo_postal := infra.texto(p_codigo_postal);
    nueva.municipio := infra.texto(p_municipio); nueva.estado := infra.texto(p_estado);
    nueva.telefono := infra.texto(p_telefono); nueva.correo := infra.texto(p_correo);
    -- Los campos que el panel no edita se conservan de la ficha anterior.
    IF (to_jsonb(nueva) - 'id' - 'creada_en' - 'registrada_por_acceso_id') <> (to_jsonb(f) - 'id' - 'creada_en' - 'registrada_por_acceso_id') THEN
        nueva.revision := f.revision + 1;
        INSERT INTO negocio.ficha_empresa (organizacion_id, empresa_id, revision, nombre_fiscal, nombre_comercial, rfc,
            pais, estado, municipio, codigo_postal, calle, numero_exterior, numero_interior, colonia,
            referencia_localizacion, telefono, correo)
        VALUES (o, e, nueva.revision, nueva.nombre_fiscal, nueva.nombre_comercial, nueva.rfc,
            nueva.pais, nueva.estado, nueva.municipio, nueva.codigo_postal, nueva.calle, nueva.numero_exterior,
            nueva.numero_interior, nueva.colonia, nueva.referencia_localizacion, nueva.telefono, nueva.correo);
    END IF;

    SELECT a.numero INTO aut FROM negocio.autorizacion_empresa a
    WHERE a.organizacion_id = o AND a.empresa_id = e AND a.autoridad = 'Medio ambiente' AND a.finalidad = 'Generador'
    ORDER BY a.registrada_en DESC, a.id DESC LIMIT 1;
    IF infra.texto(p_autorizacion) IS NULL AND aut IS NOT NULL THEN
        RAISE EXCEPTION 'Quitar la autorización de un cliente todavía no está definido' USING ERRCODE = '22023';
    ELSIF infra.texto(p_autorizacion) IS DISTINCT FROM aut THEN
        INSERT INTO negocio.autorizacion_empresa (organizacion_id, empresa_id, autoridad, finalidad, numero)
        VALUES (o, e, 'Medio ambiente', 'Generador', infra.texto(p_autorizacion));
    END IF;
    RETURN coalesce(nueva.revision, f.revision);
END $$;

REVOKE ALL ON FUNCTION infra.texto(text) FROM PUBLIC;
REVOKE ALL ON FUNCTION negocio.alta_cliente(uuid,uuid,text,text,text,text,text,text,text,text,text,text,text,text) FROM PUBLIC;
REVOKE ALL ON FUNCTION negocio.corregir_cliente(uuid,uuid,uuid,integer,text,text,text,text,text,text,text,text,text,text,text,text) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION negocio.alta_cliente(uuid,uuid,text,text,text,text,text,text,text,text,text,text,text,text) TO manifiestos_app;
GRANT EXECUTE ON FUNCTION negocio.corregir_cliente(uuid,uuid,uuid,integer,text,text,text,text,text,text,text,text,text,text,text,text) TO manifiestos_app;
