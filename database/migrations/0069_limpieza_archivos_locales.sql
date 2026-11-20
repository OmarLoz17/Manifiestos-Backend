-- Limpieza por retención de los archivos de evidencia guardados en el VPS (decisión: Drive es el archivo definitivo).
-- Un archivo del VPS solo es candidato cuando tiene copia en Drive ya verificada (tamaño y MD5 al subirla) desde hace al menos p_dias días.
-- La fila de ubicación 'vps' se conserva (los registros de ubicación son inmutables): el panel sirve el archivo desde Drive si el VPS ya no lo tiene.
-- Esta tabla registra que la copia del VPS ya se retiró, para no volver a proponerla.
CREATE TABLE sync.archivo_local_limpiado (
    organizacion_id uuid NOT NULL, sucursal_id uuid NOT NULL, archivo_id uuid NOT NULL,
    limpiado_en timestamptz NOT NULL DEFAULT clock_timestamp(),
    PRIMARY KEY (organizacion_id, sucursal_id, archivo_id),
    FOREIGN KEY (organizacion_id, sucursal_id, archivo_id) REFERENCES negocio.archivo ON DELETE RESTRICT
);
-- Sin políticas ni permisos directos: solo la leen y escriben las funciones de abajo (SECURITY DEFINER).
ALTER TABLE sync.archivo_local_limpiado ENABLE ROW LEVEL SECURITY;

CREATE FUNCTION sync.archivos_para_limpieza(p_dias integer, p_limite integer DEFAULT 200)
RETURNS TABLE (organizacion_id uuid, sucursal_id uuid, archivo_id uuid, clave_vps text, tamano_bytes bigint)
LANGUAGE plpgsql STABLE SECURITY DEFINER SET search_path = pg_catalog AS $$
BEGIN
    IF p_dias IS NULL OR p_dias < 7 THEN RAISE EXCEPTION 'La retención mínima es de 7 días' USING ERRCODE = '22023'; END IF;
    RETURN QUERY
    SELECT a.organizacion_id, a.sucursal_id, a.id, v.clave_externa, a.tamano_bytes
    FROM negocio.archivo a
    JOIN negocio.ubicacion_archivo v ON (v.organizacion_id, v.sucursal_id, v.archivo_id) = (a.organizacion_id, a.sucursal_id, a.id) AND v.proveedor = 'vps'
    JOIN negocio.ubicacion_archivo d ON (d.organizacion_id, d.sucursal_id, d.archivo_id) = (a.organizacion_id, a.sucursal_id, a.id) AND d.proveedor = 'drive'
        AND d.verificada_en IS NOT NULL AND d.verificada_en < clock_timestamp() - make_interval(days => p_dias)
    WHERE NOT EXISTS (SELECT 1 FROM sync.archivo_local_limpiado l WHERE (l.organizacion_id, l.sucursal_id, l.archivo_id) = (a.organizacion_id, a.sucursal_id, a.id))
    ORDER BY d.verificada_en
    LIMIT least(greatest(coalesce(p_limite, 200), 1), 1000);
END $$;

-- Marca la copia del VPS como retirada. Solo si el archivo tiene copia verificada en Drive; idempotente.
CREATE FUNCTION sync.registrar_archivo_local_limpiado(o uuid, s uuid, p_archivo uuid) RETURNS void
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path = pg_catalog AS $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM negocio.ubicacion_archivo d WHERE (d.organizacion_id, d.sucursal_id, d.archivo_id) = (o, s, p_archivo)
                   AND d.proveedor = 'drive' AND d.verificada_en IS NOT NULL) THEN
        RAISE EXCEPTION 'El archivo no tiene copia verificada en Drive' USING ERRCODE = '22023';
    END IF;
    INSERT INTO sync.archivo_local_limpiado (organizacion_id, sucursal_id, archivo_id) VALUES (o, s, p_archivo) ON CONFLICT DO NOTHING;
END $$;

REVOKE ALL ON FUNCTION sync.archivos_para_limpieza(integer, integer), sync.registrar_archivo_local_limpiado(uuid, uuid, uuid) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION sync.archivos_para_limpieza(integer, integer), sync.registrar_archivo_local_limpiado(uuid, uuid, uuid) TO manifiestos_app;
