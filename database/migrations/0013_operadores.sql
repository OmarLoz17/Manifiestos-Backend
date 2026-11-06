-- Pantalla Operadores: nombre y cargo por sucursal, con revisiones.
-- seguridad.persona es de la organización, inmutable y solo visible si tiene equipo o unidad asignados.
-- negocio.operador_ficha conserva el nombre y el cargo vigentes de la persona en una sucursal;
-- persona.nombre_completo queda como el original. No guarda correos ni cuentas Google: la cuenta
-- del teléfono y su entrega a cada persona siguen pendientes (PG-20 y PG-22) y una identidad Google
-- autorizada no sustituye a la persona.
CREATE TABLE negocio.operador_ficha (
    organizacion_id uuid NOT NULL, sucursal_id uuid NOT NULL, persona_id uuid NOT NULL,
    revision integer NOT NULL CHECK (revision > 0),
    nombre_completo text NOT NULL CHECK (btrim(nombre_completo) <> ''),
    cargo text CHECK (cargo IS NULL OR btrim(cargo) <> ''),
    registrada_en timestamptz NOT NULL DEFAULT clock_timestamp(),
    registrada_por_acceso_id uuid DEFAULT seguridad.contexto_acceso(),
    PRIMARY KEY (organizacion_id, sucursal_id, persona_id, revision),
    FOREIGN KEY (organizacion_id, sucursal_id) REFERENCES negocio.sucursal ON DELETE RESTRICT,
    FOREIGN KEY (organizacion_id, persona_id) REFERENCES seguridad.persona ON DELETE RESTRICT,
    FOREIGN KEY (organizacion_id, registrada_por_acceso_id) REFERENCES seguridad.acceso_autorizado ON DELETE RESTRICT
);
CREATE INDEX operador_ficha_persona ON negocio.operador_ficha (organizacion_id, persona_id);
CREATE INDEX operador_ficha_registrada_por ON negocio.operador_ficha (organizacion_id, registrada_por_acceso_id);
CREATE TRIGGER conservar_historia BEFORE UPDATE OR DELETE ON negocio.operador_ficha
FOR EACH ROW EXECUTE FUNCTION infra.inmutable();
ALTER TABLE negocio.operador_ficha ENABLE ROW LEVEL SECURITY;
CREATE POLICY contexto ON negocio.operador_ficha TO manifiestos_app
USING (seguridad.puede(organizacion_id, sucursal_id, 'datos.leer'))
WITH CHECK (seguridad.puede(organizacion_id, sucursal_id, 'datos.escribir'));
GRANT SELECT ON negocio.operador_ficha TO manifiestos_app;

-- Persona visible: con equipo asignado (0003), como operador de una unidad visible (0008) o con ficha visible.
CREATE OR REPLACE FUNCTION seguridad.persona_visible(o uuid, p uuid) RETURNS boolean
LANGUAGE sql STABLE SECURITY DEFINER SET search_path = pg_catalog AS $$
    SELECT EXISTS (SELECT 1 FROM seguridad.asignacion_equipo a WHERE a.organizacion_id=o AND a.persona_id=p
        AND seguridad.puede(o,a.sucursal_id,'datos.leer'))
    OR EXISTS (SELECT 1 FROM negocio.asignacion_unidad a WHERE a.organizacion_id=o AND a.persona_id=p
        AND seguridad.puede(o,a.sucursal_id,'datos.leer'))
    OR EXISTS (SELECT 1 FROM negocio.operador_ficha f WHERE f.organizacion_id=o AND f.persona_id=p
        AND seguridad.puede(o,f.sucursal_id,'datos.leer'))
$$;

-- Alta: persona nueva y su primera ficha (revisión 1) en la sucursal.
CREATE FUNCTION negocio.alta_operador(o uuid, s uuid, p_nombre text, p_cargo text)
RETURNS uuid
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path = pg_catalog AS $$
DECLARE p uuid;
BEGIN
    IF NOT coalesce(seguridad.puede(o, s, 'datos.escribir'), false) THEN
        RAISE EXCEPTION 'Sin permiso de escritura en la sucursal' USING ERRCODE = '42501';
    END IF;
    IF infra.texto(p_nombre) IS NULL THEN
        RAISE EXCEPTION 'El operador necesita un nombre' USING ERRCODE = '22023';
    END IF;
    INSERT INTO seguridad.persona (organizacion_id, nombre_completo) VALUES (o, btrim(p_nombre)) RETURNING id INTO p;
    INSERT INTO negocio.operador_ficha (organizacion_id, sucursal_id, persona_id, revision, nombre_completo, cargo)
    VALUES (o, s, p, 1, btrim(p_nombre), infra.texto(p_cargo));
    RETURN p;
END $$;

-- Corrección: ficha nueva con revisión + 1. p_revision = la que se editó (0 = la persona no tiene
-- ficha en esta sucursal); si ya no es la última, SQLSTATE 40001 (el API responde 409).
-- Sin cambios no se crea una revisión. Devuelve la revisión vigente.
CREATE FUNCTION negocio.corregir_operador(o uuid, s uuid, p_persona uuid, p_revision integer, p_nombre text, p_cargo text)
RETURNS integer
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path = pg_catalog AS $$
DECLARE f negocio.operador_ficha%ROWTYPE; original text;
BEGIN
    IF NOT coalesce(seguridad.puede(o, s, 'datos.escribir'), false) THEN
        RAISE EXCEPTION 'Sin permiso de escritura en la sucursal' USING ERRCODE = '42501';
    END IF;
    IF infra.texto(p_nombre) IS NULL THEN
        RAISE EXCEPTION 'El operador necesita un nombre' USING ERRCODE = '22023';
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
    IF btrim(p_nombre) = coalesce(f.nombre_completo, original) AND infra.texto(p_cargo) IS NOT DISTINCT FROM f.cargo THEN
        RETURN coalesce(f.revision, 0);
    END IF;
    INSERT INTO negocio.operador_ficha (organizacion_id, sucursal_id, persona_id, revision, nombre_completo, cargo)
    VALUES (o, s, p_persona, coalesce(f.revision, 0) + 1, btrim(p_nombre), infra.texto(p_cargo));
    RETURN coalesce(f.revision, 0) + 1;
END $$;

REVOKE ALL ON FUNCTION negocio.alta_operador(uuid,uuid,text,text) FROM PUBLIC;
REVOKE ALL ON FUNCTION negocio.corregir_operador(uuid,uuid,uuid,integer,text,text) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION negocio.alta_operador(uuid,uuid,text,text) TO manifiestos_app;
GRANT EXECUTE ON FUNCTION negocio.corregir_operador(uuid,uuid,uuid,integer,text,text) TO manifiestos_app;
