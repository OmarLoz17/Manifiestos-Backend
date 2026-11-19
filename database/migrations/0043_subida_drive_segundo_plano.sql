-- Subida a Drive en segundo plano (cola sync.tarea tipo 'subir_drive', ya encolada por sync.registrar_archivo).
-- Funciones para el proceso del servidor: reservar la siguiente subida, registrar la ubicación en Drive y reprogramar si falla.
-- La app y el panel nunca esperan a Drive: el archivo ya está recibido en el VPS (ubicación 'vps') y se archiva después.

CREATE FUNCTION sync.reservar_subida_drive(p_reserva_segundos integer DEFAULT 300)
RETURNS TABLE (tarea_id uuid, organizacion_id uuid, sucursal_id uuid, archivo_id uuid, clave_vps text, mime text, sha256 bytea,
               tamano_bytes bigint, ocurrida_en timestamptz, intentos integer)
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path = pg_catalog AS $$
DECLARE t sync.tarea%ROWTYPE;
BEGIN
    SELECT x.* INTO t FROM sync.tarea x
    WHERE x.tipo = 'subir_drive' AND x.completada_en IS NULL AND x.disponible_en <= clock_timestamp()
      AND (x.reserva_hasta IS NULL OR x.reserva_hasta < clock_timestamp())
    ORDER BY x.disponible_en FOR UPDATE SKIP LOCKED LIMIT 1;
    IF NOT FOUND THEN RETURN; END IF;
    UPDATE sync.tarea SET intentos = sync.tarea.intentos + 1, reserva_hasta = clock_timestamp() + make_interval(secs => p_reserva_segundos)
    WHERE (sync.tarea.organizacion_id, sync.tarea.sucursal_id, sync.tarea.id) = (t.organizacion_id, t.sucursal_id, t.id);
    RETURN QUERY
    SELECT t.id, t.organizacion_id, t.sucursal_id, a.id, u.clave_externa, a.mime, a.sha256, a.tamano_bytes,
           coalesce(k.ocurrida_en, clock_timestamp()), t.intentos + 1
    FROM negocio.archivo a
    JOIN negocio.ubicacion_archivo u ON (u.organizacion_id, u.sucursal_id, u.archivo_id) = (a.organizacion_id, a.sucursal_id, a.id) AND u.proveedor = 'vps'
    LEFT JOIN negocio.evidencia_captura e ON (e.organizacion_id, e.sucursal_id, e.archivo_id) = (a.organizacion_id, a.sucursal_id, a.id)
    LEFT JOIN negocio.captura k ON (k.organizacion_id, k.sucursal_id, k.id) = (e.organizacion_id, e.sucursal_id, e.captura_id)
    WHERE (a.organizacion_id, a.sucursal_id, a.id) = (t.organizacion_id, t.sucursal_id, (t.datos ->> 'archivoId')::uuid)
    LIMIT 1;
END $$;

-- Archivo verificado en Drive: se guarda su identificador y se cierra la tarea. Idempotente.
CREATE FUNCTION sync.completar_subida_drive(p_tarea uuid, o uuid, s uuid, p_archivo uuid, p_drive_id text) RETURNS void
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path = pg_catalog AS $$
BEGIN
    IF coalesce(btrim(p_drive_id), '') = '' THEN RAISE EXCEPTION 'Falta el identificador de Drive' USING ERRCODE = '22023'; END IF;
    INSERT INTO negocio.ubicacion_archivo (organizacion_id, sucursal_id, archivo_id, proveedor, clave_externa, verificada_en)
    VALUES (o, s, p_archivo, 'drive', p_drive_id, clock_timestamp()) ON CONFLICT DO NOTHING;
    UPDATE sync.tarea SET completada_en = coalesce(completada_en, clock_timestamp()), reserva_hasta = NULL
    WHERE (organizacion_id, sucursal_id, id) = (o, s, p_tarea);
END $$;

-- Fallo transitorio: se reintenta con espera creciente (1, 2, 4… hasta 60 minutos). Nunca descarta ni marca como archivada la evidencia.
CREATE FUNCTION sync.reprogramar_subida_drive(p_tarea uuid, o uuid, s uuid) RETURNS void
LANGUAGE sql VOLATILE SECURITY DEFINER SET search_path = pg_catalog AS $$
    UPDATE sync.tarea SET reserva_hasta = NULL,
        disponible_en = clock_timestamp() + make_interval(mins => least(60, (2 ^ least(intentos, 6))::integer / 2))
    WHERE (organizacion_id, sucursal_id, id) = (o, s, p_tarea) AND completada_en IS NULL;
$$;

REVOKE ALL ON FUNCTION sync.reservar_subida_drive(integer), sync.completar_subida_drive(uuid, uuid, uuid, uuid, text),
    sync.reprogramar_subida_drive(uuid, uuid, uuid) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION sync.reservar_subida_drive(integer), sync.completar_subida_drive(uuid, uuid, uuid, uuid, text),
    sync.reprogramar_subida_drive(uuid, uuid, uuid) TO manifiestos_app;
