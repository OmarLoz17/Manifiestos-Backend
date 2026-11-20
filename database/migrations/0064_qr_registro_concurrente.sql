-- 20-nov-2026 (revisión de código, H13): dos impresiones simultáneas del mismo manifiesto ya no compiten por el INSERT (la segunda fallaba por la restricción única).
-- El registro se resuelve en una sola sentencia con ON CONFLICT y las dos solicitudes reciben el MISMO registro persistido.
CREATE OR REPLACE FUNCTION seguridad.registrar_codigo_validacion(p_manifiesto uuid, p_revision integer, p_contenido text, p_huella bytea, p_firma bytea, p_llave text)
RETURNS uuid LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog AS $$
DECLARE m record; r uuid;
BEGIN
    SELECT organizacion_id, sucursal_id INTO m FROM negocio.manifiesto WHERE id = p_manifiesto;
    IF NOT FOUND OR NOT seguridad.puede(m.organizacion_id, m.sucursal_id, 'datos.leer') THEN
        RAISE EXCEPTION 'Contexto no autorizado' USING ERRCODE = '42501';
    END IF;
    INSERT INTO negocio.codigo_validacion_manifiesto (organizacion_id, sucursal_id, manifiesto_id, revision, contenido, huella, firma, llave_id)
    VALUES (m.organizacion_id, m.sucursal_id, p_manifiesto, p_revision, p_contenido, p_huella, p_firma, p_llave)
    ON CONFLICT (organizacion_id, sucursal_id, manifiesto_id, huella) DO NOTHING
    RETURNING id INTO r;
    IF r IS NULL THEN
        -- Otra transacción lo registró primero (o ya existía): se devuelve el registro persistido.
        SELECT id INTO r FROM negocio.codigo_validacion_manifiesto
            WHERE (organizacion_id, sucursal_id, manifiesto_id, huella) = (m.organizacion_id, m.sucursal_id, p_manifiesto, p_huella);
    END IF;
    RETURN r;
END $$;
