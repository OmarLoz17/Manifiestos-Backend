-- Una persona con cuenta del panel (operaciones o administrador) puede ser TAMBIÉN operador en el teléfono con la misma cuenta de Google
-- (pedido del 25-nov-2026, subgerente que cubre a operadores). En el panel entra con su tipo; en la app, como operador.

ALTER TABLE seguridad.acceso_autorizado ADD COLUMN tambien_operador boolean NOT NULL DEFAULT false;

-- Las funciones del teléfono admiten, además de la cuenta de operador, la del panel marcada. Se parchea la definición vigente de cada una.
DO $migracion$
DECLARE x record; d text; n text;
BEGIN
    FOR x IN SELECT * FROM (VALUES
        ('seguridad.contexto_operador', 'x\.rol_codigo\s*=\s*''telefono_operador''', '(x.rol_codigo=''telefono_operador'' OR x.tambien_operador)'),
        ('seguridad.iniciar_sesion_movil', 'x\.rol_codigo IS DISTINCT FROM ''telefono_operador''', '(x.rol_codigo IS DISTINCT FROM ''telefono_operador'' AND NOT x.tambien_operador)'),
        ('seguridad.solicitar_equipo', 'x\.rol_codigo IS DISTINCT FROM ''telefono_operador''', '(x.rol_codigo IS DISTINCT FROM ''telefono_operador'' AND NOT x.tambien_operador)'),
        ('seguridad.aprobar_solicitud_equipo', 'a\.rol_codigo IS DISTINCT FROM ''telefono_operador''', '(a.rol_codigo IS DISTINCT FROM ''telefono_operador'' AND NOT (SELECT z.tambien_operador FROM seguridad.acceso_autorizado z WHERE z.organizacion_id = o AND z.id = a.id))'),
        ('seguridad.solicitudes_equipo_pendientes', 'a\.rol_codigo\s*=\s*''telefono_operador''', '(a.rol_codigo = ''telefono_operador'' OR a.tambien_operador)'),
        ('negocio.autorizar_cuenta_operador', 'ELSIF r = ''telefono_operador'' THEN',
            'ELSIF (SELECT z.tambien_operador FROM seguridad.acceso_autorizado z WHERE z.organizacion_id = o AND z.id = a) THEN NULL;
    ELSIF r = ''telefono_operador'' THEN'),
        ('seguridad.admin_pendientes_ayer', 'z\.rol_codigo\s*=\s*''telefono_operador''', '(z.rol_codigo = ''telefono_operador'' OR z.tambien_operador)'),
        ('seguridad.admin_guardar_pendientes_ayer', 'z\.rol_codigo\s*=\s*''telefono_operador''', '(z.rol_codigo = ''telefono_operador'' OR z.tambien_operador)')
    ) AS v(nombre, patron, nuevo)
    LOOP
        SELECT pg_get_functiondef(p.oid) INTO STRICT d FROM pg_proc p JOIN pg_namespace s ON s.oid = p.pronamespace
        WHERE s.nspname || '.' || p.proname = x.nombre;
        n := regexp_replace(d, x.patron, x.nuevo, 'g');
        IF n = d THEN RAISE EXCEPTION 'No se encontró la comprobación de operador en %', x.nombre; END IF;
        EXECUTE n;
    END LOOP;
END
$migracion$;

-- Marca o desmarca (solo el administrador; solo cuentas del panel). Queda en el historial de Administración.
CREATE FUNCTION seguridad.admin_tambien_operador(o uuid, p_acceso uuid, p_valor boolean) RETURNS void
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path = pg_catalog AS $$
DECLARE x seguridad.acceso_autorizado%ROWTYPE;
BEGIN
    PERFORM seguridad.exigir_administrador(o);
    SELECT * INTO x FROM seguridad.acceso_autorizado z WHERE z.organizacion_id = o AND z.id = p_acceso FOR UPDATE;
    IF x.id IS NULL THEN RAISE EXCEPTION 'cuenta_no_encontrada' USING ERRCODE = 'P0002'; END IF;
    IF x.rol_codigo NOT IN ('operaciones', 'administrador') THEN RAISE EXCEPTION 'cuenta_de_operador' USING ERRCODE = 'P0422'; END IF;
    IF x.tambien_operador = p_valor THEN RETURN; END IF;
    UPDATE seguridad.acceso_autorizado SET tambien_operador = p_valor WHERE organizacion_id = o AND id = p_acceso;
    INSERT INTO seguridad.administracion_historial (organizacion_id, tipo, objeto_id, accion, detalle, hecho_por)
    VALUES (o, 'cuenta', p_acceso, 'cambio', jsonb_build_object('tambienOperador', p_valor), seguridad.contexto_acceso());
END $$;
REVOKE ALL ON FUNCTION seguridad.admin_tambien_operador(uuid, uuid, boolean) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION seguridad.admin_tambien_operador(uuid, uuid, boolean) TO manifiestos_app;

CREATE FUNCTION seguridad.admin_cuentas_que_operan(o uuid) RETURNS TABLE (acceso_id uuid)
LANGUAGE plpgsql STABLE SECURITY DEFINER SET search_path = pg_catalog AS $$
BEGIN
    PERFORM seguridad.exigir_administrador(o);
    RETURN QUERY SELECT z.id FROM seguridad.acceso_autorizado z WHERE z.organizacion_id = o AND z.tambien_operador;
END $$;
REVOKE ALL ON FUNCTION seguridad.admin_cuentas_que_operan(uuid) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION seguridad.admin_cuentas_que_operan(uuid) TO manifiestos_app;
