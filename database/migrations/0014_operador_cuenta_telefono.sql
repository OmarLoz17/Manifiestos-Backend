-- Cuenta de Google del operador: el correo con el que entra a la app del teléfono.
-- Se guarda en la ficha del operador (con historial) y mantiene su entrada en la lista de autorizados
-- con el rol 'telefono_operador' (0006), que NO puede entrar al panel web. Una identidad Google
-- autorizada no sustituye a la persona: cada ficha dice qué persona usó la cuenta en cada momento.
-- La cuenta se desactiva cuando ningún operador la tiene en su ficha vigente. Quién puede administrar
-- esta lista sigue pendiente (PG-20): provisionalmente basta datos.escribir en la sucursal.
-- Permisos de datos para la app móvil: ninguno todavía; se definen con el inicio de sesión móvil.
ALTER TABLE negocio.operador_ficha
    ADD COLUMN correo text CHECK (correo IS NULL OR (correo = lower(btrim(correo)) AND correo ~ '^[^[:space:]@]+@[^[:space:]@]+\.[^[:space:]@]+$'));
CREATE INDEX operador_ficha_correo ON negocio.operador_ficha (organizacion_id, correo) WHERE correo IS NOT NULL;

DROP FUNCTION negocio.alta_operador(uuid, uuid, text, text);
DROP FUNCTION negocio.corregir_operador(uuid, uuid, uuid, integer, text, text);

-- ¿Algún operador tiene hoy esta cuenta en su ficha vigente? (excluye a una persona y sucursal)
CREATE FUNCTION negocio.correo_operador_en_uso(o uuid, p_correo text, p_excluir_persona uuid, p_excluir_sucursal uuid)
RETURNS boolean
LANGUAGE sql STABLE SECURITY DEFINER SET search_path = pg_catalog AS $$
    SELECT EXISTS (
        SELECT 1 FROM negocio.operador_ficha f
        WHERE f.organizacion_id = o AND f.correo = p_correo
          AND f.revision = (SELECT max(x.revision) FROM negocio.operador_ficha x
                            WHERE (x.organizacion_id, x.sucursal_id, x.persona_id) = (f.organizacion_id, f.sucursal_id, f.persona_id))
          AND (p_excluir_persona IS NULL OR (f.persona_id, f.sucursal_id) IS DISTINCT FROM (p_excluir_persona, p_excluir_sucursal)))
$$;

-- Autoriza la cuenta (alta o reactivación) con el rol del teléfono. Nunca toca una cuenta que se use
-- para otra cosa (por ejemplo, de Operaciones): eso se rechaza.
CREATE FUNCTION negocio.autorizar_cuenta_operador(o uuid, p_correo text)
RETURNS void
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path = pg_catalog AS $$
DECLARE a uuid; r text;
BEGIN
    SELECT x.id, x.rol_codigo INTO a, r FROM seguridad.acceso_autorizado x
    WHERE x.organizacion_id = o AND lower(btrim(x.correo_permitido)) = p_correo FOR UPDATE;
    IF a IS NULL THEN
        INSERT INTO seguridad.acceso_autorizado (organizacion_id, correo_permitido, activo, rol_codigo)
        VALUES (o, p_correo, true, 'telefono_operador');
    ELSIF r = 'telefono_operador' THEN
        UPDATE seguridad.acceso_autorizado SET activo = true WHERE organizacion_id = o AND id = a AND NOT activo;
    ELSE
        RAISE EXCEPTION 'correo_reservado' USING ERRCODE = '23514';
    END IF;
END $$;

-- Retira la autorización de una cuenta de teléfono que ya nadie usa.
CREATE FUNCTION negocio.retirar_cuenta_operador(o uuid, p_correo text, p_persona uuid, p_sucursal uuid)
RETURNS void
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path = pg_catalog AS $$
BEGIN
    IF p_correo IS NULL OR negocio.correo_operador_en_uso(o, p_correo, p_persona, p_sucursal) THEN RETURN; END IF;
    UPDATE seguridad.acceso_autorizado SET activo = false
    WHERE organizacion_id = o AND lower(btrim(correo_permitido)) = p_correo AND rol_codigo = 'telefono_operador' AND activo;
END $$;

CREATE FUNCTION negocio.alta_operador(o uuid, s uuid, p_nombre text, p_cargo text, p_correo text)
RETURNS uuid
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path = pg_catalog AS $$
DECLARE p uuid; c text := lower(infra.texto(p_correo));
BEGIN
    IF NOT coalesce(seguridad.puede(o, s, 'datos.escribir'), false) THEN
        RAISE EXCEPTION 'Sin permiso de escritura en la sucursal' USING ERRCODE = '42501';
    END IF;
    IF infra.texto(p_nombre) IS NULL THEN
        RAISE EXCEPTION 'El operador necesita un nombre' USING ERRCODE = '22023';
    END IF;
    IF c IS NOT NULL THEN
        IF c !~ '^[^[:space:]@]+@[^[:space:]@]+\.[^[:space:]@]+$' THEN
            RAISE EXCEPTION 'correo_invalido' USING ERRCODE = '22023';
        END IF;
        PERFORM pg_advisory_xact_lock(hashtextextended('cuenta:' || o::text || ':' || c, 0));
        IF negocio.correo_operador_en_uso(o, c, NULL, NULL) THEN
            RAISE EXCEPTION 'correo_en_uso' USING ERRCODE = '23514';
        END IF;
        PERFORM negocio.autorizar_cuenta_operador(o, c);
    END IF;
    INSERT INTO seguridad.persona (organizacion_id, nombre_completo) VALUES (o, btrim(p_nombre)) RETURNING id INTO p;
    INSERT INTO negocio.operador_ficha (organizacion_id, sucursal_id, persona_id, revision, nombre_completo, cargo, correo)
    VALUES (o, s, p, 1, btrim(p_nombre), infra.texto(p_cargo), c);
    RETURN p;
END $$;

CREATE FUNCTION negocio.corregir_operador(o uuid, s uuid, p_persona uuid, p_revision integer, p_nombre text, p_cargo text, p_correo text)
RETURNS integer
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path = pg_catalog AS $$
DECLARE f negocio.operador_ficha%ROWTYPE; original text; c text := lower(infra.texto(p_correo));
BEGIN
    IF NOT coalesce(seguridad.puede(o, s, 'datos.escribir'), false) THEN
        RAISE EXCEPTION 'Sin permiso de escritura en la sucursal' USING ERRCODE = '42501';
    END IF;
    IF infra.texto(p_nombre) IS NULL THEN
        RAISE EXCEPTION 'El operador necesita un nombre' USING ERRCODE = '22023';
    END IF;
    IF c IS NOT NULL AND c !~ '^[^[:space:]@]+@[^[:space:]@]+\.[^[:space:]@]+$' THEN
        RAISE EXCEPTION 'correo_invalido' USING ERRCODE = '22023';
    END IF;
    PERFORM pg_advisory_xact_lock(hashtextextended('operador:' || o::text || ':' || p_persona::text, 0));
    SELECT pe.nombre_completo INTO original FROM seguridad.persona pe WHERE pe.organizacion_id = o AND pe.id = p_persona;
    -- Solo se corrige a quien ya es conocido en esta sucursal (con ficha o como operador de una unidad).
    IF original IS NULL OR NOT (
        EXISTS (SELECT 1 FROM negocio.operador_ficha x WHERE x.organizacion_id = o AND x.sucursal_id = s AND x.persona_id = p_persona)
        OR EXISTS (SELECT 1 FROM negocio.asignacion_unidad a WHERE a.organizacion_id = o AND a.sucursal_id = s AND a.persona_id = p_persona)) THEN
        RAISE EXCEPTION 'Operador no encontrado' USING ERRCODE = 'P0002';
    END IF;
    SELECT * INTO f FROM negocio.operador_ficha x
    WHERE x.organizacion_id = o AND x.sucursal_id = s AND x.persona_id = p_persona ORDER BY x.revision DESC LIMIT 1;
    IF coalesce(f.revision, 0) <> p_revision THEN
        RAISE EXCEPTION 'La ficha del operador cambió' USING ERRCODE = '40001';
    END IF;
    IF btrim(p_nombre) = coalesce(f.nombre_completo, original) AND infra.texto(p_cargo) IS NOT DISTINCT FROM f.cargo
       AND c IS NOT DISTINCT FROM f.correo THEN
        RETURN coalesce(f.revision, 0);
    END IF;
    IF c IS NOT NULL AND c IS DISTINCT FROM f.correo THEN
        PERFORM pg_advisory_xact_lock(hashtextextended('cuenta:' || o::text || ':' || c, 0));
        IF negocio.correo_operador_en_uso(o, c, p_persona, s) THEN
            RAISE EXCEPTION 'correo_en_uso' USING ERRCODE = '23514';
        END IF;
        PERFORM negocio.autorizar_cuenta_operador(o, c);
    END IF;
    INSERT INTO negocio.operador_ficha (organizacion_id, sucursal_id, persona_id, revision, nombre_completo, cargo, correo)
    VALUES (o, s, p_persona, coalesce(f.revision, 0) + 1, btrim(p_nombre), infra.texto(p_cargo), c);
    -- La cuenta anterior se retira solo si ya nadie la tiene en su ficha vigente.
    IF f.correo IS NOT NULL AND f.correo IS DISTINCT FROM c THEN
        PERFORM pg_advisory_xact_lock(hashtextextended('cuenta:' || o::text || ':' || f.correo, 0));
        PERFORM negocio.retirar_cuenta_operador(o, f.correo, p_persona, s);
    END IF;
    RETURN coalesce(f.revision, 0) + 1;
END $$;

REVOKE ALL ON FUNCTION negocio.correo_operador_en_uso(uuid,text,uuid,uuid) FROM PUBLIC;
REVOKE ALL ON FUNCTION negocio.autorizar_cuenta_operador(uuid,text) FROM PUBLIC;
REVOKE ALL ON FUNCTION negocio.retirar_cuenta_operador(uuid,text,uuid,uuid) FROM PUBLIC;
REVOKE ALL ON FUNCTION negocio.alta_operador(uuid,uuid,text,text,text) FROM PUBLIC;
REVOKE ALL ON FUNCTION negocio.corregir_operador(uuid,uuid,uuid,integer,text,text,text) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION negocio.alta_operador(uuid,uuid,text,text,text) TO manifiestos_app;
GRANT EXECUTE ON FUNCTION negocio.corregir_operador(uuid,uuid,uuid,integer,text,text,text) TO manifiestos_app;
