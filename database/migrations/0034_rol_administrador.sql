-- 12-nov-2026: rol «administrador» (provisional, PG-11): es el único que puede ELIMINAR sitios y plantas (los demás roles ven y editan).
-- La matriz completa de permisos sigue abierta (PG-11, PG-20); aquí solo se añade el rol y una comprobación para el servidor.
INSERT INTO catalogo.rol_acceso (codigo, nombre, acceso_web)
VALUES ('administrador', 'Administrador (provisional, PG-11)', true)
ON CONFLICT (codigo) DO NOTHING;

-- ¿El acceso informado (el del contexto actual) es administrador activo de la organización?
CREATE OR REPLACE FUNCTION seguridad.es_administrador(o uuid, a uuid) RETURNS boolean
LANGUAGE sql STABLE SECURITY DEFINER SET search_path = pg_catalog AS $$
    SELECT coalesce((SELECT x.activo AND x.rol_codigo = 'administrador'
                     FROM seguridad.acceso_autorizado x
                     WHERE x.organizacion_id = o AND x.id = a AND a = seguridad.contexto_acceso()), false)
$$;
REVOKE ALL ON FUNCTION seguridad.es_administrador(uuid, uuid) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION seguridad.es_administrador(uuid, uuid) TO manifiestos_app;
