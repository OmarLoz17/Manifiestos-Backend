-- Acceso al panel web con Google y lista local de autorizados (RF-16).
-- Roles PROVISIONALES: la matriz de permisos sigue abierta (PG-11, PG-20).
-- Solo se distingue si un rol puede entrar al panel web; la cuenta del
-- teléfono de un operador no entra. No se guardan tokens de Google ni de sesión.
-- El backend valida el ID token (firma, emisor, audiencia, vigencia y
-- email_verified) ANTES de llamar a estas funciones; aquí solo llega el
-- emisor/sujeto/correo de un token ya validado, nunca un correo escrito por el cliente.

CREATE TABLE catalogo.rol_acceso (
    codigo text PRIMARY KEY CHECK (codigo ~ '^[a-z_]+$'),
    nombre text NOT NULL CHECK (btrim(nombre) <> ''),
    acceso_web boolean NOT NULL
);
INSERT INTO catalogo.rol_acceso VALUES
    ('operaciones', 'Operaciones (provisional, PG-11)', true),
    ('telefono_operador', 'Teléfono de operador (provisional, PG-11)', false);
CREATE TRIGGER conservar_historia BEFORE UPDATE OR DELETE ON catalogo.rol_acceso
FOR EACH ROW EXECUTE FUNCTION infra.inmutable();
GRANT SELECT ON catalogo.rol_acceso TO manifiestos_app;

-- Un rol por entrada autorizada (simplificación provisional del UML, que lo
-- ubicaba en PermisoSucursal). NULL = sin rol: no entra a ningún canal.
ALTER TABLE seguridad.acceso_autorizado
    ADD COLUMN rol_codigo text REFERENCES catalogo.rol_acceso ON DELETE RESTRICT;
CREATE INDEX acceso_rol ON seguridad.acceso_autorizado (rol_codigo);

-- Registro simple de intentos con identidad Google ya validada. Sin tokens.
CREATE TABLE seguridad.inicio_sesion (
    organizacion_id uuid NOT NULL REFERENCES negocio.organizacion ON DELETE RESTRICT,
    id uuid NOT NULL DEFAULT gen_random_uuid(),
    canal text NOT NULL CHECK (canal IN ('web')),
    resultado text NOT NULL CHECK (resultado IN
        ('aceptado', 'no_autorizado', 'inactivo', 'sin_acceso_web', 'sin_alcance')),
    acceso_id uuid,
    identidad_id uuid,
    ocurrido_en timestamptz NOT NULL DEFAULT clock_timestamp(),
    PRIMARY KEY (organizacion_id, id),
    FOREIGN KEY (organizacion_id, acceso_id) REFERENCES seguridad.acceso_autorizado ON DELETE RESTRICT,
    FOREIGN KEY (organizacion_id, identidad_id) REFERENCES seguridad.identidad_externa ON DELETE RESTRICT,
    CHECK (resultado <> 'aceptado' OR (acceso_id IS NOT NULL AND identidad_id IS NOT NULL))
);
CREATE INDEX inicio_sesion_fecha ON seguridad.inicio_sesion (organizacion_id, ocurrido_en);
CREATE INDEX inicio_sesion_acceso ON seguridad.inicio_sesion (organizacion_id, acceso_id);
CREATE INDEX inicio_sesion_identidad ON seguridad.inicio_sesion (organizacion_id, identidad_id);
CREATE TRIGGER conservar_historia BEFORE UPDATE OR DELETE ON seguridad.inicio_sesion
FOR EACH ROW EXECUTE FUNCTION infra.inmutable();
-- Sin políticas ni GRANT: el rol de aplicación no lo lee; solo escriben las funciones.
ALTER TABLE seguridad.inicio_sesion ENABLE ROW LEVEL SECURITY;

-- Única definición de "puede usar el panel web". NULL = permitido.
CREATE FUNCTION seguridad.motivo_rechazo_web(o uuid, a uuid) RETURNS text
LANGUAGE sql STABLE SECURITY DEFINER SET search_path = pg_catalog AS $$
    SELECT CASE
        WHEN x.id IS NULL THEN 'no_autorizado'
        WHEN NOT x.activo THEN 'inactivo'
        WHEN r.codigo IS NULL OR NOT r.acceso_web THEN 'sin_acceso_web'
        WHEN NOT EXISTS (SELECT 1 FROM seguridad.permiso_sucursal p
                         WHERE p.organizacion_id = o AND p.acceso_id = a AND p.permiso = 'datos.leer')
            THEN 'sin_alcance'
    END
    FROM (SELECT 1) AS uno
    LEFT JOIN seguridad.acceso_autorizado x ON x.organizacion_id = o AND x.id = a
    LEFT JOIN catalogo.rol_acceso r ON r.codigo = x.rol_codigo
$$;

-- Reconoce por emisor+sujeto; en el primer acceso vincula el sujeto a la entrada
-- con ese correo verificado, una sola vez. El lock por identidad y el FOR UPDATE
-- de la entrada serializan peticiones simultáneas; las UNIQUE existentes son respaldo.
-- Solo vincula si el acceso se concede: un intento rechazado no deja efectos salvo el registro.
CREATE FUNCTION seguridad.iniciar_sesion_web(o uuid, p_emisor text, p_sujeto text, p_correo_verificado text)
RETURNS TABLE (resultado_login text, acceso uuid)
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path = pg_catalog AS $$
DECLARE
    v_correo text := lower(btrim(p_correo_verificado));
    v_identidad uuid;
    v_acceso uuid;
    v_motivo text;
BEGIN
    IF o IS NULL OR coalesce(btrim(p_emisor), '') = '' OR coalesce(btrim(p_sujeto), '') = ''
       OR coalesce(v_correo, '') = '' THEN
        RAISE EXCEPTION 'Datos de identidad incompletos' USING ERRCODE = '22023';
    END IF;
    PERFORM pg_advisory_xact_lock(hashtextextended('login' || chr(31) || o::text || chr(31) || p_emisor || chr(31) || p_sujeto, 0));

    SELECT i.id INTO v_identidad FROM seguridad.identidad_externa i
    WHERE i.organizacion_id = o AND i.emisor = p_emisor AND i.sujeto = p_sujeto;
    IF v_identidad IS NOT NULL THEN
        SELECT x.id INTO v_acceso FROM seguridad.acceso_autorizado x
        WHERE x.organizacion_id = o AND x.identidad_id = v_identidad;
    END IF;

    IF v_acceso IS NOT NULL THEN
        v_motivo := seguridad.motivo_rechazo_web(o, v_acceso);
        UPDATE seguridad.identidad_externa SET correo_verificado = v_correo
        WHERE organizacion_id = o AND id = v_identidad AND correo_verificado IS DISTINCT FROM v_correo;
    ELSE
        -- Primer acceso. Una entrada ya vinculada a otro sujeto nunca se reatribuye.
        SELECT x.id INTO v_acceso FROM seguridad.acceso_autorizado x
        WHERE x.organizacion_id = o AND lower(btrim(x.correo_permitido)) = v_correo AND x.identidad_id IS NULL
        FOR UPDATE;
        IF v_acceso IS NULL THEN
            v_motivo := 'no_autorizado';
        ELSE
            v_motivo := seguridad.motivo_rechazo_web(o, v_acceso);
            IF v_motivo IS NULL THEN
                IF v_identidad IS NULL THEN
                    INSERT INTO seguridad.identidad_externa (organizacion_id, emisor, sujeto, correo_verificado)
                    VALUES (o, p_emisor, p_sujeto, v_correo) RETURNING id INTO v_identidad;
                END IF;
                UPDATE seguridad.acceso_autorizado SET identidad_id = v_identidad
                WHERE organizacion_id = o AND id = v_acceso;
            END IF;
        END IF;
    END IF;

    INSERT INTO seguridad.inicio_sesion (organizacion_id, canal, resultado, acceso_id, identidad_id)
    VALUES (o, 'web', coalesce(v_motivo, 'aceptado'), v_acceso, v_identidad);
    RETURN QUERY SELECT coalesce(v_motivo, 'aceptado'), CASE WHEN v_motivo IS NULL THEN v_acceso END;
END $$;

-- Revalidación en cada petición protegida y datos de /api/auth/me.
-- Sin filas = la sesión ya no es válida (baja, rol sin web o sin alcance).
CREATE FUNCTION seguridad.sesion_web(o uuid, a uuid)
RETURNS TABLE (correo text, rol_codigo text, rol_nombre text)
LANGUAGE sql STABLE SECURITY DEFINER SET search_path = pg_catalog AS $$
    SELECT i.correo_verificado, r.codigo, r.nombre
    FROM seguridad.acceso_autorizado x
    JOIN seguridad.identidad_externa i ON i.organizacion_id = x.organizacion_id AND i.id = x.identidad_id
    JOIN catalogo.rol_acceso r ON r.codigo = x.rol_codigo
    WHERE x.organizacion_id = o AND x.id = a AND seguridad.motivo_rechazo_web(o, a) IS NULL
$$;

REVOKE ALL ON FUNCTION seguridad.motivo_rechazo_web(uuid, uuid) FROM PUBLIC;
REVOKE ALL ON FUNCTION seguridad.iniciar_sesion_web(uuid, text, text, text) FROM PUBLIC;
REVOKE ALL ON FUNCTION seguridad.sesion_web(uuid, uuid) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION seguridad.iniciar_sesion_web(uuid, text, text, text) TO manifiestos_app;
GRANT EXECUTE ON FUNCTION seguridad.sesion_web(uuid, uuid) TO manifiestos_app;
