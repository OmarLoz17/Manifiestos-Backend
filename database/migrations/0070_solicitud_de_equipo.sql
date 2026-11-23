-- Alta de equipos sin pasos técnicos (decisión 0040, PG-22). El operador pulsa «Solicitar acceso» en su teléfono,
-- Operaciones aprueba o rechaza desde el panel y el servidor crea la instalación y la asignación versionada.
-- Solo pueden solicitar correos que YA están en la lista de autorizados (activos, rol telefono_operador y con ficha
-- vigente de operador). Una solicitud no habilita nada por sí sola ni sustituye a la persona: la persona es la de la
-- ficha vigente de ese correo. Reasignar un equipo cierra su entrega anterior y abre una revisión nueva; las capturas
-- históricas conservan su persona/asignación. Sin GRANT de tablas: acceso mediante funciones acotadas (como 0021).
ALTER TABLE seguridad.inicio_sesion DROP CONSTRAINT inicio_sesion_resultado_check;
ALTER TABLE seguridad.inicio_sesion ADD CHECK (resultado IN
    ('aceptado','no_autorizado','inactivo','sin_acceso_web','sin_alcance','sin_acceso_movil','sin_equipo','desafio_invalido',
     'solicitud_equipo','solicitud_equipo_rechazada','solicitud_equipo_aprobada'));

-- Un equipo sin vincular no tiene fila en instalacion_app, así que su desafío vive aparte (misma vigencia y un solo uso).
CREATE TABLE seguridad.desafio_solicitud_equipo (
    organizacion_id uuid NOT NULL REFERENCES negocio.organizacion ON DELETE RESTRICT,
    id uuid NOT NULL DEFAULT gen_random_uuid(), instalacion_id uuid NOT NULL,
    nonce_sha256 bytea NOT NULL CHECK (octet_length(nonce_sha256) = 32),
    vence_en timestamptz NOT NULL DEFAULT (clock_timestamp() + interval '2 minutes'), consumido_en timestamptz,
    PRIMARY KEY (organizacion_id, id)
);
CREATE TABLE seguridad.solicitud_equipo (
    organizacion_id uuid NOT NULL, id uuid NOT NULL DEFAULT gen_random_uuid(),
    instalacion_id uuid NOT NULL, acceso_id uuid NOT NULL,
    estado text NOT NULL DEFAULT 'pendiente' CHECK (estado IN ('pendiente', 'aprobada', 'rechazada')),
    solicitada_en timestamptz NOT NULL DEFAULT clock_timestamp(),
    vence_en timestamptz NOT NULL DEFAULT (clock_timestamp() + interval '14 days'),
    resuelta_en timestamptz, resuelta_por uuid, asignacion_id uuid,
    PRIMARY KEY (organizacion_id, id),
    FOREIGN KEY (organizacion_id, acceso_id) REFERENCES seguridad.acceso_autorizado ON DELETE RESTRICT,
    FOREIGN KEY (organizacion_id, resuelta_por) REFERENCES seguridad.acceso_autorizado ON DELETE RESTRICT,
    CHECK ((estado = 'pendiente') = (resuelta_en IS NULL))
);
-- Una pendiente por cuenta y equipo: repetir la solicitud no la duplica.
CREATE UNIQUE INDEX solicitud_equipo_pendiente ON seguridad.solicitud_equipo (organizacion_id, acceso_id, instalacion_id) WHERE estado = 'pendiente';
CREATE INDEX solicitud_equipo_acceso ON seguridad.solicitud_equipo (organizacion_id, acceso_id);
ALTER TABLE seguridad.desafio_solicitud_equipo ENABLE ROW LEVEL SECURITY;
ALTER TABLE seguridad.solicitud_equipo ENABLE ROW LEVEL SECURITY;

-- Ficha vigente del operador que tiene hoy este correo (interna; el correo es único entre fichas vigentes).
CREATE FUNCTION seguridad.ficha_vigente_de_correo(o uuid, c text)
RETURNS TABLE (sucursal_id uuid, persona_id uuid, nombre text)
LANGUAGE sql STABLE SECURITY DEFINER SET search_path = pg_catalog AS $$
    SELECT f.sucursal_id, f.persona_id, f.nombre_completo
    FROM negocio.operador_ficha f
    WHERE f.organizacion_id = o AND f.correo = c
      AND f.revision = (SELECT max(x.revision) FROM negocio.operador_ficha x
                        WHERE (x.organizacion_id, x.sucursal_id, x.persona_id) = (f.organizacion_id, f.sucursal_id, f.persona_id))
    LIMIT 1
$$;

CREATE FUNCTION seguridad.crear_desafio_solicitud_equipo(o uuid, i uuid, n bytea) RETURNS uuid
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path = pg_catalog AS $$
DECLARE d uuid;
BEGIN
    -- Desafíos anónimos: los vencidos se retiran para que no se acumulen.
    DELETE FROM seguridad.desafio_solicitud_equipo WHERE organizacion_id = o AND vence_en < clock_timestamp() - interval '1 day';
    INSERT INTO seguridad.desafio_solicitud_equipo (organizacion_id, instalacion_id, nonce_sha256) VALUES (o, i, n) RETURNING id INTO d;
    RETURN d;
END $$;

-- Resultado: 'solicitada' (nueva o ya pendiente), 'vinculado' (el equipo ya sirve para esta cuenta) o el motivo del rechazo.
CREATE FUNCTION seguridad.solicitar_equipo(o uuid, p_emisor text, p_sujeto text, p_correo text, i uuid, d uuid, n bytea)
RETURNS text
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path = pg_catalog AS $$
DECLARE v_correo text := lower(btrim(p_correo)); v_identidad uuid; v_acceso uuid; v_motivo text; v_hab boolean; v_ins boolean;
    v_desafio seguridad.desafio_solicitud_equipo%ROWTYPE; v_resultado text;
BEGIN
    IF o IS NULL OR coalesce(btrim(p_emisor), '') = '' OR coalesce(btrim(p_sujeto), '') = '' OR coalesce(v_correo, '') = '' THEN
        RAISE EXCEPTION 'Identidad incompleta' USING ERRCODE = '22023';
    END IF;
    SELECT * INTO v_desafio FROM seguridad.desafio_solicitud_equipo x WHERE x.organizacion_id = o AND x.id = d FOR UPDATE;
    IF v_desafio.id IS NULL OR v_desafio.instalacion_id <> i OR v_desafio.vence_en <= clock_timestamp()
       OR v_desafio.consumido_en IS NOT NULL OR v_desafio.nonce_sha256 IS DISTINCT FROM n THEN
        RETURN 'desafio_invalido';
    END IF;
    UPDATE seguridad.desafio_solicitud_equipo SET consumido_en = clock_timestamp() WHERE organizacion_id = o AND id = d;
    PERFORM pg_advisory_xact_lock(hashtextextended('login' || chr(31) || o::text || chr(31) || p_emisor || chr(31) || p_sujeto, 0));
    SELECT id INTO v_identidad FROM seguridad.identidad_externa x WHERE x.organizacion_id = o AND x.emisor = p_emisor AND x.sujeto = p_sujeto;
    IF v_identidad IS NOT NULL THEN
        SELECT id INTO v_acceso FROM seguridad.acceso_autorizado x WHERE x.organizacion_id = o AND x.identidad_id = v_identidad FOR UPDATE;
    END IF;
    IF v_acceso IS NULL THEN
        SELECT id INTO v_acceso FROM seguridad.acceso_autorizado x
        WHERE x.organizacion_id = o AND lower(btrim(x.correo_permitido)) = v_correo AND x.identidad_id IS NULL FOR UPDATE;
    END IF;
    SELECT CASE WHEN x.id IS NULL THEN 'no_autorizado' WHEN NOT x.activo THEN 'inactivo'
        WHEN x.rol_codigo IS DISTINCT FROM 'telefono_operador' THEN 'sin_acceso_movil'
        WHEN NOT EXISTS (SELECT 1 FROM seguridad.ficha_vigente_de_correo(o, lower(btrim(x.correo_permitido)))) THEN 'sin_equipo' END
    INTO v_motivo
    FROM (SELECT 1) uno LEFT JOIN seguridad.acceso_autorizado x ON x.organizacion_id = o AND x.id = v_acceso;
    IF v_motivo IS NULL THEN
        SELECT true, ins.habilitada INTO v_ins, v_hab FROM seguridad.instalacion_app ins WHERE ins.organizacion_id = o AND ins.id = i;
        IF EXISTS (SELECT 1 FROM seguridad.contexto_operador(o, v_acceso, i)) THEN
            v_resultado := 'vinculado';
        ELSIF coalesce(v_ins, false) AND NOT v_hab THEN
            v_motivo := 'sin_equipo';   -- un equipo deshabilitado por Administración no se reactiva por solicitud
        ELSIF EXISTS (SELECT 1 FROM seguridad.solicitud_equipo s WHERE s.organizacion_id = o AND s.acceso_id = v_acceso
                      AND s.instalacion_id = i AND s.estado = 'pendiente') THEN
            UPDATE seguridad.solicitud_equipo SET vence_en = clock_timestamp() + interval '14 days'
            WHERE organizacion_id = o AND acceso_id = v_acceso AND instalacion_id = i AND estado = 'pendiente';
            v_resultado := 'solicitada';
        ELSIF (SELECT count(*) FROM seguridad.solicitud_equipo s WHERE s.organizacion_id = o AND s.acceso_id = v_acceso
               AND s.estado = 'pendiente' AND s.vence_en > clock_timestamp()) >= 5 THEN
            v_motivo := 'sin_equipo';   -- tope de pendientes por cuenta
        ELSE
            INSERT INTO seguridad.solicitud_equipo (organizacion_id, instalacion_id, acceso_id) VALUES (o, i, v_acceso);
            v_resultado := 'solicitada';
        END IF;
    END IF;
    INSERT INTO seguridad.inicio_sesion (organizacion_id, canal, resultado, acceso_id, identidad_id)
    VALUES (o, 'movil', coalesce(v_motivo, 'solicitud_equipo'), v_acceso, v_identidad);
    RETURN coalesce(v_motivo, v_resultado);
END $$;

-- Solicitudes pendientes visibles para el usuario del panel (lectura en la sucursal del operador).
CREATE FUNCTION seguridad.solicitudes_equipo_pendientes(o uuid)
RETURNS TABLE (id uuid, correo text, solicitada_en timestamptz, vence_en timestamptz, sucursal_id uuid, persona_id uuid,
    nombre text, codigo_equipo text, persona_actual text)
LANGUAGE sql STABLE SECURITY DEFINER SET search_path = pg_catalog AS $$
    SELECT s.id, lower(btrim(a.correo_permitido)), s.solicitada_en, s.vence_en, f.sucursal_id, f.persona_id, f.nombre,
        upper(right(replace(s.instalacion_id::text, '-', ''), 4)), actual.nombre_completo
    FROM seguridad.solicitud_equipo s
    JOIN seguridad.acceso_autorizado a ON a.organizacion_id = s.organizacion_id AND a.id = s.acceso_id
    JOIN LATERAL seguridad.ficha_vigente_de_correo(o, lower(btrim(a.correo_permitido))) f ON true
    LEFT JOIN seguridad.instalacion_app ins ON ins.organizacion_id = s.organizacion_id AND ins.id = s.instalacion_id
    LEFT JOIN LATERAL (SELECT p.nombre_completo FROM seguridad.asignacion_equipo ae
        JOIN seguridad.persona p ON p.organizacion_id = ae.organizacion_id AND p.id = ae.persona_id
        WHERE ae.organizacion_id = o AND ae.dispositivo_id = ins.dispositivo_id AND ae.desde <= now() AND (ae.hasta IS NULL OR ae.hasta > now())
        ORDER BY ae.revision DESC LIMIT 1) actual ON true
    WHERE s.organizacion_id = o AND s.estado = 'pendiente' AND s.vence_en > now() AND a.activo AND a.rol_codigo = 'telefono_operador'
      AND coalesce(seguridad.puede(o, f.sucursal_id, 'datos.leer'), false)
    ORDER BY s.solicitada_en, s.id
$$;

-- Equipos con entrega vigente en las sucursales visibles.
CREATE FUNCTION seguridad.equipos_entregados(o uuid)
RETURNS TABLE (dispositivo_id uuid, sucursal_id uuid, persona_id uuid, nombre text, correo text, desde timestamptz,
    revision integer, habilitado boolean, codigo_equipo text)
LANGUAGE sql STABLE SECURITY DEFINER SET search_path = pg_catalog AS $$
    SELECT ae.dispositivo_id, ae.sucursal_id, ae.persona_id, p.nombre_completo, lower(btrim(a.correo_permitido)), ae.desde, ae.revision,
        bool_or(ins.habilitada) OVER (PARTITION BY ae.dispositivo_id), upper(right(replace(ins.id::text, '-', ''), 4))
    FROM seguridad.asignacion_equipo ae
    JOIN seguridad.persona p ON p.organizacion_id = ae.organizacion_id AND p.id = ae.persona_id
    JOIN seguridad.acceso_autorizado a ON a.organizacion_id = ae.organizacion_id AND a.id = ae.acceso_id
    JOIN seguridad.instalacion_app ins ON ins.organizacion_id = ae.organizacion_id AND ins.dispositivo_id = ae.dispositivo_id
    WHERE ae.organizacion_id = o AND ae.desde <= now() AND (ae.hasta IS NULL OR ae.hasta > now())
      AND ae.revision = (SELECT max(x.revision) FROM seguridad.asignacion_equipo x WHERE x.organizacion_id = o AND x.dispositivo_id = ae.dispositivo_id)
      AND coalesce(seguridad.puede(o, ae.sucursal_id, 'datos.leer'), false)
    ORDER BY ae.desde DESC, ae.dispositivo_id
$$;

-- Resultado: 'aprobada', 'confirmar_reasignacion' (el equipo ya es de otra persona y falta confirmarlo),
-- 'no_encontrada' (ya resuelta, vencida o sin operador vigente) o 'equipo_deshabilitado'.
CREATE FUNCTION seguridad.aprobar_solicitud_equipo(o uuid, p_id uuid, p_reasignar boolean)
RETURNS text
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path = pg_catalog AS $$
DECLARE s seguridad.solicitud_equipo%ROWTYPE; a seguridad.acceso_autorizado%ROWTYPE; f record; d uuid; hab boolean;
    anterior seguridad.asignacion_equipo%ROWTYPE; t timestamptz; nueva uuid;
BEGIN
    SELECT * INTO s FROM seguridad.solicitud_equipo x WHERE x.organizacion_id = o AND x.id = p_id FOR UPDATE;
    IF s.id IS NULL OR s.estado <> 'pendiente' OR s.vence_en <= now() THEN RETURN 'no_encontrada'; END IF;
    SELECT * INTO a FROM seguridad.acceso_autorizado x WHERE x.organizacion_id = o AND x.id = s.acceso_id;
    IF NOT a.activo OR a.rol_codigo IS DISTINCT FROM 'telefono_operador' THEN RETURN 'no_encontrada'; END IF;
    SELECT * INTO f FROM seguridad.ficha_vigente_de_correo(o, lower(btrim(a.correo_permitido)));
    IF f.persona_id IS NULL THEN RETURN 'no_encontrada'; END IF;
    IF NOT coalesce(seguridad.puede(o, f.sucursal_id, 'datos.escribir'), false) THEN
        RAISE EXCEPTION 'Sin permiso de escritura en la sucursal' USING ERRCODE = '42501';
    END IF;
    SELECT dispositivo_id, habilitada INTO d, hab FROM seguridad.instalacion_app WHERE organizacion_id = o AND id = s.instalacion_id FOR UPDATE;
    IF d IS NOT NULL AND NOT hab THEN RETURN 'equipo_deshabilitado'; END IF;
    IF d IS NULL THEN
        INSERT INTO seguridad.dispositivo (organizacion_id) VALUES (o) RETURNING id INTO d;
        INSERT INTO seguridad.instalacion_app (organizacion_id, id, dispositivo_id, habilitada) VALUES (o, s.instalacion_id, d, true);
    END IF;
    PERFORM pg_advisory_xact_lock(hashtextextended('registro-equipo:' || o::text || ':' || d::text, 0));
    SELECT * INTO anterior FROM seguridad.asignacion_equipo x WHERE x.organizacion_id = o AND x.dispositivo_id = d ORDER BY x.revision DESC LIMIT 1;
    IF anterior.id IS NULL THEN
        INSERT INTO seguridad.asignacion_equipo (organizacion_id, sucursal_id, dispositivo_id, acceso_id, persona_id, revision, desde)
        VALUES (o, f.sucursal_id, d, s.acceso_id, f.persona_id, 1, clock_timestamp()) RETURNING id INTO nueva;
    ELSIF anterior.acceso_id = s.acceso_id AND anterior.persona_id = f.persona_id AND anterior.sucursal_id = f.sucursal_id
          AND anterior.hasta IS NULL AND anterior.desde <= now() THEN
        nueva := anterior.id;   -- ya estaba entregado a esta persona y cuenta
    ELSE
        IF (anterior.hasta IS NULL OR anterior.hasta > now()) AND NOT coalesce(p_reasignar, false) THEN
            RETURN 'confirmar_reasignacion';
        END IF;
        t := greatest(clock_timestamp(), anterior.desde + interval '1 millisecond');
        IF anterior.hasta IS NULL THEN
            UPDATE seguridad.asignacion_equipo SET hasta = t WHERE organizacion_id = o AND sucursal_id = anterior.sucursal_id AND id = anterior.id;
        END IF;
        INSERT INTO seguridad.asignacion_equipo (organizacion_id, sucursal_id, dispositivo_id, acceso_id, persona_id, revision, desde)
        VALUES (o, f.sucursal_id, d, s.acceso_id, f.persona_id, anterior.revision + 1, t) RETURNING id INTO nueva;
    END IF;
    UPDATE seguridad.solicitud_equipo SET estado = 'aprobada', resuelta_en = clock_timestamp(), resuelta_por = seguridad.contexto_acceso(), asignacion_id = nueva
    WHERE organizacion_id = o AND id = p_id;
    -- Otras solicitudes pendientes del mismo equipo (de cuentas que ya no lo recibirán) quedan resueltas como rechazadas.
    UPDATE seguridad.solicitud_equipo SET estado = 'rechazada', resuelta_en = clock_timestamp(), resuelta_por = seguridad.contexto_acceso()
    WHERE organizacion_id = o AND instalacion_id = s.instalacion_id AND estado = 'pendiente' AND id <> p_id;
    INSERT INTO seguridad.inicio_sesion (organizacion_id, canal, resultado, acceso_id) VALUES (o, 'movil', 'solicitud_equipo_aprobada', s.acceso_id);
    RETURN 'aprobada';
END $$;

CREATE FUNCTION seguridad.rechazar_solicitud_equipo(o uuid, p_id uuid) RETURNS text
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path = pg_catalog AS $$
DECLARE s seguridad.solicitud_equipo%ROWTYPE; a seguridad.acceso_autorizado%ROWTYPE; f record;
BEGIN
    SELECT * INTO s FROM seguridad.solicitud_equipo x WHERE x.organizacion_id = o AND x.id = p_id FOR UPDATE;
    IF s.id IS NULL OR s.estado <> 'pendiente' THEN RETURN 'no_encontrada'; END IF;
    SELECT * INTO a FROM seguridad.acceso_autorizado x WHERE x.organizacion_id = o AND x.id = s.acceso_id;
    SELECT * INTO f FROM seguridad.ficha_vigente_de_correo(o, lower(btrim(a.correo_permitido)));
    -- Sin ficha vigente no hay sucursal a quien exigir permiso: solo se puede dejar vencer.
    IF f.sucursal_id IS NULL THEN RETURN 'no_encontrada'; END IF;
    IF NOT coalesce(seguridad.puede(o, f.sucursal_id, 'datos.escribir'), false) THEN
        RAISE EXCEPTION 'Sin permiso de escritura en la sucursal' USING ERRCODE = '42501';
    END IF;
    UPDATE seguridad.solicitud_equipo SET estado = 'rechazada', resuelta_en = clock_timestamp(), resuelta_por = seguridad.contexto_acceso()
    WHERE organizacion_id = o AND id = p_id;
    INSERT INTO seguridad.inicio_sesion (organizacion_id, canal, resultado, acceso_id) VALUES (o, 'movil', 'solicitud_equipo_rechazada', s.acceso_id);
    RETURN 'rechazada';
END $$;

REVOKE ALL ON FUNCTION seguridad.ficha_vigente_de_correo(uuid,text) FROM PUBLIC;
REVOKE ALL ON FUNCTION seguridad.crear_desafio_solicitud_equipo(uuid,uuid,bytea) FROM PUBLIC;
REVOKE ALL ON FUNCTION seguridad.solicitar_equipo(uuid,text,text,text,uuid,uuid,bytea) FROM PUBLIC;
REVOKE ALL ON FUNCTION seguridad.solicitudes_equipo_pendientes(uuid) FROM PUBLIC;
REVOKE ALL ON FUNCTION seguridad.equipos_entregados(uuid) FROM PUBLIC;
REVOKE ALL ON FUNCTION seguridad.aprobar_solicitud_equipo(uuid,uuid,boolean) FROM PUBLIC;
REVOKE ALL ON FUNCTION seguridad.rechazar_solicitud_equipo(uuid,uuid) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION seguridad.crear_desafio_solicitud_equipo(uuid,uuid,bytea) TO manifiestos_app;
GRANT EXECUTE ON FUNCTION seguridad.solicitar_equipo(uuid,text,text,text,uuid,uuid,bytea) TO manifiestos_app;
GRANT EXECUTE ON FUNCTION seguridad.solicitudes_equipo_pendientes(uuid) TO manifiestos_app;
GRANT EXECUTE ON FUNCTION seguridad.equipos_entregados(uuid) TO manifiestos_app;
GRANT EXECUTE ON FUNCTION seguridad.aprobar_solicitud_equipo(uuid,uuid,boolean) TO manifiestos_app;
GRANT EXECUTE ON FUNCTION seguridad.rechazar_solicitud_equipo(uuid,uuid) TO manifiestos_app;
