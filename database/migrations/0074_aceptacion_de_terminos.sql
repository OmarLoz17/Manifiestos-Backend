-- Aceptación de términos y aviso de privacidad (decisión 0044): quién aceptó qué versión, cuándo y desde dónde. Insert-only.
-- La versión es la fecha de vigencia del documento y la huella el SHA-256 de su texto. Sin aceptar la versión vigente no se entra al
-- panel ni a la app (lo exige la API); aquí solo queda la evidencia y las funciones para consultarla.
CREATE TABLE seguridad.aceptacion_documento (
    organizacion_id uuid NOT NULL REFERENCES negocio.organizacion ON DELETE RESTRICT,
    id uuid NOT NULL DEFAULT gen_random_uuid(),
    acceso_id uuid NOT NULL,
    documento text NOT NULL CHECK (documento IN ('terminos', 'privacidad')),
    version text NOT NULL CHECK (btrim(version) <> ''),
    huella text NOT NULL CHECK (huella ~ '^[0-9a-f]{64}$'),
    canal text NOT NULL CHECK (canal IN ('web', 'movil')),
    aceptada_en timestamptz NOT NULL DEFAULT clock_timestamp(),
    PRIMARY KEY (organizacion_id, id),
    UNIQUE (organizacion_id, acceso_id, documento, version),
    FOREIGN KEY (organizacion_id, acceso_id) REFERENCES seguridad.acceso_autorizado ON DELETE RESTRICT
);
CREATE TRIGGER conservar_historia BEFORE UPDATE OR DELETE ON seguridad.aceptacion_documento FOR EACH ROW EXECUTE FUNCTION infra.inmutable();
ALTER TABLE seguridad.aceptacion_documento ENABLE ROW LEVEL SECURITY;

-- Lo que una cuenta ya aceptó (todas las versiones).
CREATE FUNCTION seguridad.aceptaciones_de(o uuid, a uuid) RETURNS TABLE (documento text, version text, huella text, aceptada_en timestamptz)
LANGUAGE sql STABLE SECURITY DEFINER SET search_path = pg_catalog AS $$
    SELECT x.documento, x.version, x.huella, x.aceptada_en FROM seguridad.aceptacion_documento x
    WHERE x.organizacion_id = o AND x.acceso_id = a
$$;

-- Registra la aceptación de una cuenta activa. Idempotente: repetirla no cambia la fecha original. Devuelve false si la cuenta no existe o está inactiva.
CREATE FUNCTION seguridad.aceptar_documento(o uuid, a uuid, p_documento text, p_version text, p_huella text, p_canal text) RETURNS boolean
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path = pg_catalog AS $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM seguridad.acceso_autorizado x WHERE x.organizacion_id = o AND x.id = a AND x.activo) THEN RETURN false; END IF;
    INSERT INTO seguridad.aceptacion_documento (organizacion_id, acceso_id, documento, version, huella, canal)
    VALUES (o, a, p_documento, p_version, p_huella, p_canal) ON CONFLICT DO NOTHING;
    RETURN true;
END $$;

-- Cuentas de Administración con la última aceptación de los términos (versión y fecha) para auditar quién aceptó.
DROP FUNCTION seguridad.admin_cuentas(uuid);
CREATE FUNCTION seguridad.admin_cuentas(o uuid)
RETURNS TABLE (acceso_id uuid, correo text, nombre text, rol_codigo text, activo boolean, primer_acceso boolean, ultimo_acceso timestamptz,
    persona_id uuid, equipo text, unidad text, permisos jsonb,
    terminos_version text, terminos_en timestamptz)
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
        CASE WHEN x.rol_codigo = 'telefono_operador' THEN '[]'::jsonb ELSE seguridad.permisos_de_cuenta(o, x.id) END,
        (SELECT ad.version FROM seguridad.aceptacion_documento ad WHERE ad.organizacion_id = o AND ad.acceso_id = x.id AND ad.documento = 'terminos' ORDER BY ad.aceptada_en DESC LIMIT 1),
        (SELECT ad.aceptada_en FROM seguridad.aceptacion_documento ad WHERE ad.organizacion_id = o AND ad.acceso_id = x.id AND ad.documento = 'terminos' ORDER BY ad.aceptada_en DESC LIMIT 1)
    FROM seguridad.acceso_autorizado x
    LEFT JOIN LATERAL seguridad.ficha_vigente_de_correo(o, lower(btrim(x.correo_permitido))) f ON x.rol_codigo = 'telefono_operador'
    WHERE x.organizacion_id = o AND x.rol_codigo IS NOT NULL AND seguridad.exigir_administrador(o)
    ORDER BY CASE x.rol_codigo WHEN 'administrador' THEN 0 WHEN 'operaciones' THEN 1 ELSE 2 END, lower(coalesce(f.nombre, x.nombre, x.correo_permitido))
$$;

REVOKE ALL ON FUNCTION seguridad.aceptaciones_de(uuid, uuid), seguridad.aceptar_documento(uuid, uuid, text, text, text, text), seguridad.admin_cuentas(uuid) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION seguridad.aceptaciones_de(uuid, uuid), seguridad.aceptar_documento(uuid, uuid, text, text, text, text), seguridad.admin_cuentas(uuid) TO manifiestos_app;
