-- Primer equipo de una cuenta de operador: se registra solo al solicitar acceso (decisión 0043). Reemplaza solicitar_equipo (0070).
CREATE OR REPLACE FUNCTION seguridad.solicitar_equipo(o uuid, p_emisor text, p_sujeto text, p_correo text, i uuid, d uuid, n bytea)
RETURNS text
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path = pg_catalog AS $$
DECLARE f record; v_dispositivo uuid; v_correo text := lower(btrim(p_correo)); v_identidad uuid; v_acceso uuid; v_motivo text; v_hab boolean; v_ins boolean;
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
        -- Primer equipo de la cuenta (decisión 0043): si Operaciones ya la dio de alta y nunca tuvo un equipo, el primero con el que entra
        -- queda registrado solo. Después, cualquier otro teléfono necesita la aprobación del administrador.
        PERFORM pg_advisory_xact_lock(hashtextextended('registro-equipo-acceso:' || o::text || ':' || v_acceso::text, 0));
        IF NOT EXISTS (SELECT 1 FROM seguridad.instalacion_app ins WHERE ins.organizacion_id = o AND ins.id = i)
           AND NOT EXISTS (SELECT 1 FROM seguridad.asignacion_equipo ae WHERE ae.organizacion_id = o AND ae.acceso_id = v_acceso) THEN
            SELECT * INTO f FROM seguridad.ficha_vigente_de_correo(o, v_correo);
            INSERT INTO seguridad.dispositivo (organizacion_id) VALUES (o) RETURNING id INTO v_dispositivo;
            INSERT INTO seguridad.instalacion_app (organizacion_id, id, dispositivo_id, habilitada) VALUES (o, i, v_dispositivo, true);
            INSERT INTO seguridad.asignacion_equipo (organizacion_id, sucursal_id, dispositivo_id, acceso_id, persona_id, revision, desde)
            VALUES (o, f.sucursal_id, v_dispositivo, v_acceso, f.persona_id, 1, clock_timestamp());
            INSERT INTO seguridad.administracion_historial (organizacion_id, tipo, objeto_id, accion, detalle)
            VALUES (o, 'equipo', v_dispositivo, 'registro_automatico', jsonb_build_object('correo', v_correo));
            v_resultado := 'vinculado';
        ELSIF EXISTS (SELECT 1 FROM seguridad.contexto_operador(o, v_acceso, i)) THEN
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
