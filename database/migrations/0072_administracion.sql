-- Pantalla de Administración (decisión 0042, PG-11/PG-20/PG-34): cuentas con tipo, permisos por pantalla, deshabilitar en lugar de eliminar,
-- confirmación de identidad y equipos reservados al administrador. Solo el rol «administrador» (0034) ejecuta estas funciones. Sin GRANT de tablas:
-- el rol de la aplicación solo llama funciones. Nada se borra: las cuentas y los equipos se deshabilitan y todo cambio queda en el historial.

-- Nombre visible de la cuenta (las cuentas de operador lo toman de su ficha).
ALTER TABLE seguridad.acceso_autorizado ADD COLUMN nombre text CHECK (nombre IS NULL OR btrim(nombre) <> '');

-- Historial insert-only de lo que hace la administración (quién, cuándo, qué).
CREATE TABLE seguridad.administracion_historial (
    organizacion_id uuid NOT NULL REFERENCES negocio.organizacion ON DELETE RESTRICT,
    id uuid NOT NULL DEFAULT gen_random_uuid(),
    tipo text NOT NULL CHECK (tipo IN ('cuenta', 'equipo', 'sitio')),
    objeto_id uuid NOT NULL,
    accion text NOT NULL CHECK (btrim(accion) <> ''),
    detalle jsonb NOT NULL DEFAULT '{}'::jsonb,
    hecho_por uuid,
    hecho_en timestamptz NOT NULL DEFAULT clock_timestamp(),
    PRIMARY KEY (organizacion_id, id),
    FOREIGN KEY (organizacion_id, hecho_por) REFERENCES seguridad.acceso_autorizado ON DELETE RESTRICT
);
CREATE INDEX administracion_historial_objeto ON seguridad.administracion_historial (organizacion_id, tipo, objeto_id, hecho_en);
CREATE TRIGGER conservar_historia BEFORE UPDATE OR DELETE ON seguridad.administracion_historial FOR EACH ROW EXECUTE FUNCTION infra.inmutable();
ALTER TABLE seguridad.administracion_historial ENABLE ROW LEVEL SECURITY;

-- Permiso por usuario, sucursal y pantalla: sin acceso, solo ver o ver y editar. Dashboard y Reportes no se editan.
-- Si una cuenta no tiene filas aquí para una sucursal, vale lo de antes (permiso_sucursal: datos.leer y datos.escribir para todas las pantallas).
CREATE TABLE seguridad.permiso_pantalla (
    organizacion_id uuid NOT NULL, sucursal_id uuid NOT NULL, acceso_id uuid NOT NULL,
    pantalla text NOT NULL CHECK (pantalla IN ('dashboard','monitoreo','rutas','clientes','sitios','unidades','operadores','manifiestos','incidencias','reportes')),
    nivel text NOT NULL CHECK (nivel IN ('sin', 'ver', 'editar')),
    PRIMARY KEY (organizacion_id, sucursal_id, acceso_id, pantalla),
    CHECK (pantalla NOT IN ('dashboard', 'reportes') OR nivel <> 'editar'),
    FOREIGN KEY (organizacion_id, sucursal_id) REFERENCES negocio.sucursal ON DELETE RESTRICT,
    FOREIGN KEY (organizacion_id, acceso_id) REFERENCES seguridad.acceso_autorizado ON DELETE RESTRICT
);
ALTER TABLE seguridad.permiso_pantalla ENABLE ROW LEVEL SECURITY;

-- Confirmaciones de identidad de la sesión web (el administrador vuelve a demostrar con Google que es él). Insert-only.
CREATE TABLE seguridad.sesion_web_confirmacion (
    organizacion_id uuid NOT NULL, sesion_id uuid NOT NULL,
    confirmada_en timestamptz NOT NULL DEFAULT clock_timestamp(),
    PRIMARY KEY (organizacion_id, sesion_id, confirmada_en),
    FOREIGN KEY (organizacion_id, sesion_id) REFERENCES seguridad.sesion_web ON DELETE RESTRICT
);
CREATE TRIGGER conservar_historia BEFORE UPDATE OR DELETE ON seguridad.sesion_web_confirmacion FOR EACH ROW EXECUTE FUNCTION infra.inmutable();
ALTER TABLE seguridad.sesion_web_confirmacion ENABLE ROW LEVEL SECURITY;

-- Falla (42501) si quien llama no es administrador activo; devuelve true para poder usarla dentro de un WHERE.
CREATE FUNCTION seguridad.exigir_administrador(o uuid) RETURNS boolean
LANGUAGE plpgsql STABLE SECURITY DEFINER SET search_path = pg_catalog AS $$
BEGIN
    IF NOT seguridad.es_administrador(o, seguridad.contexto_acceso()) THEN
        RAISE EXCEPTION 'Solo el administrador puede hacer esto' USING ERRCODE = '42501';
    END IF;
    RETURN true;
END $$;

-- ¿Esta sesión web confirmó su identidad en los últimos p_minutos?
CREATE FUNCTION seguridad.sesion_confirmada(o uuid, s uuid, p_minutos integer DEFAULT 5) RETURNS boolean
LANGUAGE sql STABLE SECURITY DEFINER SET search_path = pg_catalog AS $$
    SELECT EXISTS (SELECT 1 FROM seguridad.sesion_web_confirmacion c WHERE c.organizacion_id = o AND c.sesion_id = s
                   AND c.confirmada_en > clock_timestamp() - make_interval(mins => p_minutos))
$$;

-- Registra la confirmación SOLO si la identidad de Google (emisor y sujeto) es la que está vinculada a la cuenta de la sesión.
CREATE FUNCTION seguridad.confirmar_identidad_web(o uuid, a uuid, s uuid, p_emisor text, p_sujeto text) RETURNS boolean
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path = pg_catalog AS $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM seguridad.acceso_autorizado x JOIN seguridad.identidad_externa i ON i.organizacion_id = x.organizacion_id AND i.id = x.identidad_id
                   WHERE x.organizacion_id = o AND x.id = a AND x.activo AND i.emisor = p_emisor AND i.sujeto = p_sujeto)
       OR NOT EXISTS (SELECT 1 FROM seguridad.sesion_web w WHERE w.organizacion_id = o AND w.id = s AND w.acceso_id = a)
       OR EXISTS (SELECT 1 FROM seguridad.sesion_web_cerrada c WHERE c.organizacion_id = o AND c.sesion_id = s) THEN
        RETURN false;
    END IF;
    INSERT INTO seguridad.sesion_web_confirmacion (organizacion_id, sesion_id) VALUES (o, s);
    RETURN true;
END $$;

-- Permisos de una cuenta como lista [{sucursalId, pantalla, nivel}], con lo anterior (datos.leer / datos.escribir) como respaldo.
CREATE FUNCTION seguridad.permisos_de_cuenta(o uuid, a uuid) RETURNS jsonb
LANGUAGE sql STABLE SECURITY DEFINER SET search_path = pg_catalog AS $$
    SELECT coalesce(jsonb_agg(jsonb_build_object('sucursalId', s.sucursal_id, 'pantalla', p.pantalla,
        'nivel', coalesce(pp.nivel, CASE WHEN s.explicito THEN 'sin'
                                         WHEN s.escribe AND p.pantalla NOT IN ('dashboard', 'reportes') THEN 'editar' ELSE 'ver' END))
        ORDER BY s.sucursal_id, p.ord), '[]'::jsonb)
    FROM (SELECT ps.sucursal_id, bool_or(ps.permiso = 'datos.escribir') AS escribe,
                 EXISTS (SELECT 1 FROM seguridad.permiso_pantalla q WHERE q.organizacion_id = o AND q.acceso_id = a AND q.sucursal_id = ps.sucursal_id) AS explicito
          FROM seguridad.permiso_sucursal ps WHERE ps.organizacion_id = o AND ps.acceso_id = a GROUP BY ps.sucursal_id) s
    CROSS JOIN (VALUES ('dashboard', 1), ('monitoreo', 2), ('rutas', 3), ('clientes', 4), ('sitios', 5), ('unidades', 6),
                       ('operadores', 7), ('manifiestos', 8), ('incidencias', 9), ('reportes', 10)) AS p(pantalla, ord)
    LEFT JOIN seguridad.permiso_pantalla pp ON pp.organizacion_id = o AND pp.acceso_id = a AND pp.sucursal_id = s.sucursal_id AND pp.pantalla = p.pantalla
$$;

-- Todas las cuentas con acceso al sistema (panel y teléfonos), con su tipo, último acceso y, para operadores, equipo y unidad.
CREATE FUNCTION seguridad.admin_cuentas(o uuid)
RETURNS TABLE (acceso_id uuid, correo text, nombre text, rol_codigo text, activo boolean, primer_acceso boolean, ultimo_acceso timestamptz,
    persona_id uuid, equipo text, unidad text, permisos jsonb)
LANGUAGE sql STABLE SECURITY DEFINER SET search_path = pg_catalog AS $$
    SELECT x.id, lower(btrim(x.correo_permitido)), coalesce(f.nombre, x.nombre, lower(btrim(x.correo_permitido))), x.rol_codigo, x.activo, x.identidad_id IS NULL,
        (SELECT max(i.ocurrido_en) FROM seguridad.inicio_sesion i WHERE i.organizacion_id = o AND i.acceso_id = x.id AND i.resultado = 'aceptado'),
        f.persona_id,
        (SELECT upper(right(replace(ins.id::text, '-', ''), 4)) FROM seguridad.asignacion_equipo ae
            JOIN seguridad.instalacion_app ins ON ins.organizacion_id = ae.organizacion_id AND ins.dispositivo_id = ae.dispositivo_id
            WHERE ae.organizacion_id = o AND ae.acceso_id = x.id AND ae.hasta IS NULL
              AND ae.revision = (SELECT max(z.revision) FROM seguridad.asignacion_equipo z WHERE z.organizacion_id = o AND z.dispositivo_id = ae.dispositivo_id)
            ORDER BY ae.desde DESC LIMIT 1),
        (SELECT v.numero_economico FROM negocio.vehiculo v
            WHERE v.organizacion_id = o AND f.persona_id IS NOT NULL
              AND (SELECT au.persona_id FROM negocio.asignacion_unidad au WHERE (au.organizacion_id, au.sucursal_id, au.vehiculo_id) = (v.organizacion_id, v.sucursal_id, v.id)
                   ORDER BY au.revision DESC LIMIT 1) = f.persona_id
            ORDER BY v.numero_economico LIMIT 1),
        CASE WHEN x.rol_codigo = 'telefono_operador' THEN '[]'::jsonb ELSE seguridad.permisos_de_cuenta(o, x.id) END
    FROM seguridad.acceso_autorizado x
    LEFT JOIN LATERAL seguridad.ficha_vigente_de_correo(o, lower(btrim(x.correo_permitido))) f ON x.rol_codigo = 'telefono_operador'
    WHERE x.organizacion_id = o AND x.rol_codigo IS NOT NULL AND seguridad.exigir_administrador(o)
    ORDER BY CASE x.rol_codigo WHEN 'administrador' THEN 0 WHEN 'operaciones' THEN 1 ELSE 2 END, lower(coalesce(f.nombre, x.nombre, x.correo_permitido))
$$;

-- Alta o edición de una cuenta DEL PANEL (Operaciones o Administrador). Las cuentas de teléfono se manejan en Operadores.
-- p_permisos: [{"sucursalId": "...", "pantallas": {"rutas": "editar", "dashboard": "ver", ...}}]. Un administrador siempre tiene todo.
CREATE FUNCTION seguridad.admin_guardar_cuenta(o uuid, p_acceso uuid, p_correo text, p_nombre text, p_rol text, p_permisos jsonb)
RETURNS uuid
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path = pg_catalog AS $$
DECLARE
    c text := lower(btrim(coalesce(p_correo, ''))); n text := btrim(coalesce(p_nombre, '')); a uuid := p_acceso; ant seguridad.acceso_autorizado%ROWTYPE;
    s record; q record; p text[] := ARRAY['dashboard','monitoreo','rutas','clientes','sitios','unidades','operadores','manifiestos','incidencias','reportes'];
    pant text; nivel text; alguna boolean := false; escribe boolean; lee boolean; admins integer;
BEGIN
    PERFORM seguridad.exigir_administrador(o);
    IF c !~ '^[^[:space:]@]+@[^[:space:]@]+\.[^[:space:]@]+$' OR length(c) > 200 THEN RAISE EXCEPTION 'correo_invalido' USING ERRCODE = 'P0422'; END IF;
    IF n = '' OR length(n) > 200 THEN RAISE EXCEPTION 'nombre_invalido' USING ERRCODE = 'P0422'; END IF;
    IF p_rol NOT IN ('operaciones', 'administrador') THEN RAISE EXCEPTION 'rol_invalido' USING ERRCODE = 'P0422'; END IF;
    PERFORM pg_advisory_xact_lock(hashtextextended('cuenta:' || o::text || ':' || c, 0));
    PERFORM pg_advisory_xact_lock(hashtextextended('administradores:' || o::text, 0));

    IF a IS NULL THEN
        IF EXISTS (SELECT 1 FROM seguridad.acceso_autorizado x WHERE x.organizacion_id = o AND lower(btrim(x.correo_permitido)) = c) THEN
            RAISE EXCEPTION 'correo_en_uso' USING ERRCODE = 'P0422';
        END IF;
        INSERT INTO seguridad.acceso_autorizado (organizacion_id, correo_permitido, activo, rol_codigo, nombre) VALUES (o, c, true, p_rol, n) RETURNING id INTO a;
    ELSE
        SELECT * INTO ant FROM seguridad.acceso_autorizado x WHERE x.organizacion_id = o AND x.id = a FOR UPDATE;
        IF ant.id IS NULL THEN RAISE EXCEPTION 'cuenta_no_encontrada' USING ERRCODE = 'P0002'; END IF;
        IF ant.rol_codigo NOT IN ('operaciones', 'administrador') THEN RAISE EXCEPTION 'cuenta_de_operador' USING ERRCODE = 'P0422'; END IF;
        IF c <> lower(btrim(ant.correo_permitido)) THEN
            IF ant.identidad_id IS NOT NULL THEN RAISE EXCEPTION 'correo_vinculado' USING ERRCODE = 'P0422'; END IF;
            IF EXISTS (SELECT 1 FROM seguridad.acceso_autorizado x WHERE x.organizacion_id = o AND x.id <> a AND lower(btrim(x.correo_permitido)) = c) THEN
                RAISE EXCEPTION 'correo_en_uso' USING ERRCODE = 'P0422';
            END IF;
        END IF;
        IF ant.rol_codigo = 'administrador' AND p_rol <> 'administrador' THEN
            IF a = seguridad.contexto_acceso() THEN RAISE EXCEPTION 'no_a_ti_mismo' USING ERRCODE = 'P0422'; END IF;
            SELECT count(*) INTO admins FROM seguridad.acceso_autorizado x WHERE x.organizacion_id = o AND x.rol_codigo = 'administrador' AND x.activo AND x.id <> a;
            IF ant.activo AND admins < 2 THEN RAISE EXCEPTION 'minimo_administradores' USING ERRCODE = 'P0422'; END IF;
        END IF;
        UPDATE seguridad.acceso_autorizado SET correo_permitido = c, nombre = n, rol_codigo = p_rol WHERE organizacion_id = o AND id = a;
    END IF;

    DELETE FROM seguridad.permiso_pantalla WHERE organizacion_id = o AND acceso_id = a;
    DELETE FROM seguridad.permiso_sucursal WHERE organizacion_id = o AND acceso_id = a;
    IF p_rol = 'administrador' THEN
        -- Todo en todas las sucursales de la organización (dashboard y reportes, solo ver).
        FOR s IN SELECT su.id FROM negocio.sucursal su WHERE su.organizacion_id = o LOOP
            FOREACH pant IN ARRAY p LOOP
                INSERT INTO seguridad.permiso_pantalla VALUES (o, s.id, a, pant, CASE WHEN pant IN ('dashboard', 'reportes') THEN 'ver' ELSE 'editar' END);
            END LOOP;
            INSERT INTO seguridad.permiso_sucursal VALUES (o, s.id, a, 'datos.leer'), (o, s.id, a, 'datos.escribir');
        END LOOP;
    ELSE
        IF jsonb_typeof(p_permisos) IS DISTINCT FROM 'array' THEN RAISE EXCEPTION 'permisos_invalidos' USING ERRCODE = 'P0422'; END IF;
        FOR q IN SELECT (e ->> 'sucursalId')::uuid AS sucursal, e -> 'pantallas' AS pantallas FROM jsonb_array_elements(p_permisos) e LOOP
            IF NOT EXISTS (SELECT 1 FROM negocio.sucursal su WHERE su.organizacion_id = o AND su.id = q.sucursal) THEN RAISE EXCEPTION 'sucursal_invalida' USING ERRCODE = 'P0422'; END IF;
            IF jsonb_typeof(q.pantallas) IS DISTINCT FROM 'object' THEN RAISE EXCEPTION 'permisos_invalidos' USING ERRCODE = 'P0422'; END IF;
            IF EXISTS (SELECT 1 FROM jsonb_object_keys(q.pantallas) k WHERE k <> ALL (p)) THEN RAISE EXCEPTION 'pantalla_invalida' USING ERRCODE = 'P0422'; END IF;
            escribe := false; lee := false;
            FOREACH pant IN ARRAY p LOOP
                nivel := coalesce(q.pantallas ->> pant, 'sin');
                IF nivel NOT IN ('sin', 'ver', 'editar') OR (nivel = 'editar' AND pant IN ('dashboard', 'reportes')) THEN RAISE EXCEPTION 'nivel_invalido' USING ERRCODE = 'P0422'; END IF;
                INSERT INTO seguridad.permiso_pantalla VALUES (o, q.sucursal, a, pant, nivel);
                IF nivel <> 'sin' THEN lee := true; END IF;
                IF nivel = 'editar' THEN escribe := true; END IF;
            END LOOP;
            IF lee THEN
                alguna := true;
                INSERT INTO seguridad.permiso_sucursal VALUES (o, q.sucursal, a, 'datos.leer');
                IF escribe THEN INSERT INTO seguridad.permiso_sucursal VALUES (o, q.sucursal, a, 'datos.escribir'); END IF;
            ELSE
                -- Una sucursal sin ninguna pantalla no es acceso: no queda ninguna fila suya.
                DELETE FROM seguridad.permiso_pantalla WHERE organizacion_id = o AND acceso_id = a AND sucursal_id = q.sucursal;
            END IF;
        END LOOP;
        IF NOT alguna THEN RAISE EXCEPTION 'sin_acceso' USING ERRCODE = 'P0422'; END IF;
    END IF;
    INSERT INTO seguridad.administracion_historial (organizacion_id, tipo, objeto_id, accion, detalle, hecho_por)
    VALUES (o, 'cuenta', a, CASE WHEN p_acceso IS NULL THEN 'alta' ELSE 'cambio' END,
            jsonb_build_object('correo', c, 'rol', p_rol, 'permisos', seguridad.permisos_de_cuenta(o, a)), seguridad.contexto_acceso());
    RETURN a;
END $$;

-- Deshabilita o reactiva una cuenta (panel o teléfono). Nunca se elimina. Nadie se deshabilita a sí mismo y siempre quedan 2 administradores activos.
CREATE FUNCTION seguridad.admin_estado_cuenta(o uuid, p_acceso uuid, p_activo boolean) RETURNS void
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path = pg_catalog AS $$
DECLARE x seguridad.acceso_autorizado%ROWTYPE; admins integer;
BEGIN
    PERFORM seguridad.exigir_administrador(o);
    PERFORM pg_advisory_xact_lock(hashtextextended('administradores:' || o::text, 0));
    SELECT * INTO x FROM seguridad.acceso_autorizado z WHERE z.organizacion_id = o AND z.id = p_acceso FOR UPDATE;
    IF x.id IS NULL OR x.rol_codigo IS NULL THEN RAISE EXCEPTION 'cuenta_no_encontrada' USING ERRCODE = 'P0002'; END IF;
    IF x.activo = p_activo THEN RETURN; END IF;
    IF NOT p_activo THEN
        IF p_acceso = seguridad.contexto_acceso() THEN RAISE EXCEPTION 'no_a_ti_mismo' USING ERRCODE = 'P0422'; END IF;
        IF x.rol_codigo = 'administrador' THEN
            SELECT count(*) INTO admins FROM seguridad.acceso_autorizado z WHERE z.organizacion_id = o AND z.rol_codigo = 'administrador' AND z.activo AND z.id <> p_acceso;
            IF admins < 2 THEN RAISE EXCEPTION 'minimo_administradores' USING ERRCODE = 'P0422'; END IF;
        END IF;
    END IF;
    UPDATE seguridad.acceso_autorizado SET activo = p_activo WHERE organizacion_id = o AND id = p_acceso;
    INSERT INTO seguridad.administracion_historial (organizacion_id, tipo, objeto_id, accion, detalle, hecho_por)
    VALUES (o, 'cuenta', p_acceso, CASE WHEN p_activo THEN 'reactivar' ELSE 'deshabilitar' END,
            jsonb_build_object('correo', lower(btrim(x.correo_permitido)), 'rol', x.rol_codigo), seguridad.contexto_acceso());
END $$;

-- Deshabilita o reactiva un teléfono (todas sus instalaciones). El operador deja de poder entrar desde ese equipo.
CREATE FUNCTION seguridad.admin_estado_equipo(o uuid, p_dispositivo uuid, p_habilitado boolean) RETURNS void
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path = pg_catalog AS $$
DECLARE n integer;
BEGIN
    PERFORM seguridad.exigir_administrador(o);
    UPDATE seguridad.instalacion_app SET habilitada = p_habilitado WHERE organizacion_id = o AND dispositivo_id = p_dispositivo AND habilitada IS DISTINCT FROM p_habilitado;
    GET DIAGNOSTICS n = ROW_COUNT;
    IF n = 0 AND NOT EXISTS (SELECT 1 FROM seguridad.instalacion_app WHERE organizacion_id = o AND dispositivo_id = p_dispositivo) THEN
        RAISE EXCEPTION 'equipo_no_encontrado' USING ERRCODE = 'P0002';
    END IF;
    IF n > 0 THEN
        INSERT INTO seguridad.administracion_historial (organizacion_id, tipo, objeto_id, accion, detalle, hecho_por)
        VALUES (o, 'equipo', p_dispositivo, CASE WHEN p_habilitado THEN 'reactivar' ELSE 'deshabilitar' END, '{}'::jsonb, seguridad.contexto_acceso());
    END IF;
END $$;

-- Lo deshabilitado (cuentas y equipos) con quién y cuándo, según el historial.
CREATE FUNCTION seguridad.admin_deshabilitados(o uuid)
RETURNS TABLE (tipo text, objeto_id uuid, nombre text, detalle text, desde timestamptz, por text)
LANGUAGE sql STABLE SECURITY DEFINER SET search_path = pg_catalog AS $$
    SELECT 'usuario', x.id, coalesce(f.nombre, x.nombre, lower(btrim(x.correo_permitido))), lower(btrim(x.correo_permitido)), h.hecho_en,
           coalesce(pa.nombre, pa.correo_permitido)
    FROM seguridad.acceso_autorizado x
    LEFT JOIN LATERAL seguridad.ficha_vigente_de_correo(o, lower(btrim(x.correo_permitido))) f ON x.rol_codigo = 'telefono_operador'
    LEFT JOIN LATERAL (SELECT z.hecho_en, z.hecho_por FROM seguridad.administracion_historial z
                       WHERE z.organizacion_id = o AND z.tipo = 'cuenta' AND z.objeto_id = x.id AND z.accion = 'deshabilitar' ORDER BY z.hecho_en DESC LIMIT 1) h ON true
    LEFT JOIN seguridad.acceso_autorizado pa ON pa.organizacion_id = o AND pa.id = h.hecho_por
    WHERE x.organizacion_id = o AND x.rol_codigo IS NOT NULL AND NOT x.activo AND seguridad.exigir_administrador(o)
    UNION ALL
    SELECT 'equipo', d.id, 'Equipo …' || (SELECT upper(right(replace(min(ins.id::text), '-', ''), 4)) FROM seguridad.instalacion_app ins WHERE ins.organizacion_id = o AND ins.dispositivo_id = d.id),
           (SELECT 'Último operador: ' || p.nombre_completo FROM seguridad.asignacion_equipo ae JOIN seguridad.persona p ON p.organizacion_id = ae.organizacion_id AND p.id = ae.persona_id
            WHERE ae.organizacion_id = o AND ae.dispositivo_id = d.id ORDER BY ae.revision DESC LIMIT 1),
           h.hecho_en, coalesce(pa.nombre, pa.correo_permitido)
    FROM seguridad.dispositivo d
    LEFT JOIN LATERAL (SELECT z.hecho_en, z.hecho_por FROM seguridad.administracion_historial z
                       WHERE z.organizacion_id = o AND z.tipo = 'equipo' AND z.objeto_id = d.id AND z.accion = 'deshabilitar' ORDER BY z.hecho_en DESC LIMIT 1) h ON true
    LEFT JOIN seguridad.acceso_autorizado pa ON pa.organizacion_id = o AND pa.id = h.hecho_por
    WHERE d.organizacion_id = o AND seguridad.exigir_administrador(o)
      AND EXISTS (SELECT 1 FROM seguridad.instalacion_app ins WHERE ins.organizacion_id = o AND ins.dispositivo_id = d.id)
      AND NOT EXISTS (SELECT 1 FROM seguridad.instalacion_app ins WHERE ins.organizacion_id = o AND ins.dispositivo_id = d.id AND ins.habilitada)
$$;

REVOKE ALL ON FUNCTION seguridad.exigir_administrador(uuid) FROM PUBLIC;
REVOKE ALL ON FUNCTION seguridad.sesion_confirmada(uuid, uuid, integer) FROM PUBLIC;
REVOKE ALL ON FUNCTION seguridad.confirmar_identidad_web(uuid, uuid, uuid, text, text) FROM PUBLIC;
REVOKE ALL ON FUNCTION seguridad.permisos_de_cuenta(uuid, uuid) FROM PUBLIC;
REVOKE ALL ON FUNCTION seguridad.admin_cuentas(uuid) FROM PUBLIC;
REVOKE ALL ON FUNCTION seguridad.admin_guardar_cuenta(uuid, uuid, text, text, text, jsonb) FROM PUBLIC;
REVOKE ALL ON FUNCTION seguridad.admin_estado_cuenta(uuid, uuid, boolean) FROM PUBLIC;
REVOKE ALL ON FUNCTION seguridad.admin_estado_equipo(uuid, uuid, boolean) FROM PUBLIC;
REVOKE ALL ON FUNCTION seguridad.admin_deshabilitados(uuid) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION seguridad.exigir_administrador(uuid) TO manifiestos_app;
GRANT EXECUTE ON FUNCTION seguridad.sesion_confirmada(uuid, uuid, integer) TO manifiestos_app;
GRANT EXECUTE ON FUNCTION seguridad.confirmar_identidad_web(uuid, uuid, uuid, text, text) TO manifiestos_app;
GRANT EXECUTE ON FUNCTION seguridad.admin_cuentas(uuid) TO manifiestos_app;
GRANT EXECUTE ON FUNCTION seguridad.admin_guardar_cuenta(uuid, uuid, text, text, text, jsonb) TO manifiestos_app;
GRANT EXECUTE ON FUNCTION seguridad.admin_estado_cuenta(uuid, uuid, boolean) TO manifiestos_app;
GRANT EXECUTE ON FUNCTION seguridad.admin_estado_equipo(uuid, uuid, boolean) TO manifiestos_app;
GRANT EXECUTE ON FUNCTION seguridad.admin_deshabilitados(uuid) TO manifiestos_app;
-- permisos_de_cuenta es interna (la llaman las otras funciones del propietario).

-- Equipos: solo el administrador ve solicitudes y entregas, aprueba, rechaza o reasigna (decisión 0042; antes 0040 lo hacía Operaciones).
CREATE OR REPLACE FUNCTION seguridad.solicitudes_equipo_pendientes(o uuid)
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
      AND seguridad.exigir_administrador(o)
    ORDER BY s.solicitada_en, s.id
$$;

CREATE OR REPLACE FUNCTION seguridad.equipos_entregados(o uuid)
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
      AND seguridad.exigir_administrador(o)
    ORDER BY ae.desde DESC, ae.dispositivo_id
$$;

CREATE OR REPLACE FUNCTION seguridad.aprobar_solicitud_equipo(o uuid, p_id uuid, p_reasignar boolean)
RETURNS text
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path = pg_catalog AS $$
DECLARE s seguridad.solicitud_equipo%ROWTYPE; a seguridad.acceso_autorizado%ROWTYPE; f record; d uuid; hab boolean;
    anterior seguridad.asignacion_equipo%ROWTYPE; t timestamptz; nueva uuid;
BEGIN
    PERFORM seguridad.exigir_administrador(o);
    SELECT * INTO s FROM seguridad.solicitud_equipo x WHERE x.organizacion_id = o AND x.id = p_id FOR UPDATE;
    IF s.id IS NULL OR s.estado <> 'pendiente' OR s.vence_en <= now() THEN RETURN 'no_encontrada'; END IF;
    SELECT * INTO a FROM seguridad.acceso_autorizado x WHERE x.organizacion_id = o AND x.id = s.acceso_id;
    IF NOT a.activo OR a.rol_codigo IS DISTINCT FROM 'telefono_operador' THEN RETURN 'no_encontrada'; END IF;
    SELECT * INTO f FROM seguridad.ficha_vigente_de_correo(o, lower(btrim(a.correo_permitido)));
    IF f.persona_id IS NULL THEN RETURN 'no_encontrada'; END IF;
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

CREATE OR REPLACE FUNCTION seguridad.rechazar_solicitud_equipo(o uuid, p_id uuid) RETURNS text
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path = pg_catalog AS $$
DECLARE s seguridad.solicitud_equipo%ROWTYPE; a seguridad.acceso_autorizado%ROWTYPE; f record;
BEGIN
    PERFORM seguridad.exigir_administrador(o);
    SELECT * INTO s FROM seguridad.solicitud_equipo x WHERE x.organizacion_id = o AND x.id = p_id FOR UPDATE;
    IF s.id IS NULL OR s.estado <> 'pendiente' THEN RETURN 'no_encontrada'; END IF;
    SELECT * INTO a FROM seguridad.acceso_autorizado x WHERE x.organizacion_id = o AND x.id = s.acceso_id;
    SELECT * INTO f FROM seguridad.ficha_vigente_de_correo(o, lower(btrim(a.correo_permitido)));
    IF f.sucursal_id IS NULL THEN RETURN 'no_encontrada'; END IF;
    UPDATE seguridad.solicitud_equipo SET estado = 'rechazada', resuelta_en = clock_timestamp(), resuelta_por = seguridad.contexto_acceso()
    WHERE organizacion_id = o AND id = p_id;
    INSERT INTO seguridad.inicio_sesion (organizacion_id, canal, resultado, acceso_id) VALUES (o, 'movil', 'solicitud_equipo_rechazada', s.acceso_id);
    RETURN 'rechazada';
END $$;

-- Permiso efectivo de quien llama por pantalla (el más alto entre sus sucursales). Lo usa el panel para ocultar y el servidor para negar.
CREATE FUNCTION seguridad.mis_permisos(o uuid) RETURNS TABLE (pantalla text, nivel text)
LANGUAGE sql STABLE SECURITY DEFINER SET search_path = pg_catalog AS $$
    SELECT e ->> 'pantalla',
           CASE max(CASE e ->> 'nivel' WHEN 'editar' THEN 2 WHEN 'ver' THEN 1 ELSE 0 END) WHEN 2 THEN 'editar' WHEN 1 THEN 'ver' ELSE 'sin' END
    FROM jsonb_array_elements(seguridad.permisos_de_cuenta(o, seguridad.contexto_acceso())) e
    WHERE o = seguridad.contexto_org() AND EXISTS (SELECT 1 FROM seguridad.acceso_autorizado a WHERE a.organizacion_id = o AND a.id = seguridad.contexto_acceso() AND a.activo)
    GROUP BY e ->> 'pantalla'
    UNION ALL
    -- Marca interna: la cuenta ya tiene permisos por pantalla (si no, valen solo datos.leer / datos.escribir y los responde cada endpoint).
    SELECT '_explicito', 'editar'
    WHERE o = seguridad.contexto_org() AND EXISTS (SELECT 1 FROM seguridad.permiso_pantalla q WHERE q.organizacion_id = o AND q.acceso_id = seguridad.contexto_acceso())
$$;
REVOKE ALL ON FUNCTION seguridad.mis_permisos(uuid) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION seguridad.mis_permisos(uuid) TO manifiestos_app;
