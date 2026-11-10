-- Sesión prolongada renovable. No reactiva sesiones ya caducadas/revocadas.
ALTER TABLE seguridad.sesion_movil ALTER COLUMN vence_en
    SET DEFAULT (clock_timestamp()+interval '30 days');

CREATE FUNCTION seguridad.renovar_sesion_movil(o uuid,s uuid) RETURNS text
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE v seguridad.sesion_movil%ROWTYPE;
BEGIN
    -- Comparte el bloqueo de fila con logout: renovar nunca borra una revocación.
    SELECT * INTO v FROM seguridad.sesion_movil WHERE organizacion_id=o AND id=s FOR UPDATE;
    IF NOT FOUND OR v.revocada_en IS NOT NULL THEN RETURN 'revocada'; END IF;
    IF NOT EXISTS(SELECT 1 FROM seguridad.contexto_operador(o,v.acceso_id,v.instalacion_id) c
        WHERE c.asignacion_id=v.asignacion_id AND c.sucursal_id=v.sucursal_id AND c.identidad_id IS NOT NULL)
        THEN RETURN 'revocada'; END IF;
    IF v.vence_en<=clock_timestamp() THEN RETURN 'caducada'; END IF;
    UPDATE seguridad.sesion_movil SET vence_en=clock_timestamp()+interval '30 days'
        WHERE organizacion_id=o AND id=s;
    RETURN 'renovada';
END $$;
REVOKE ALL ON FUNCTION seguridad.renovar_sesion_movil(uuid,uuid) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION seguridad.renovar_sesion_movil(uuid,uuid) TO manifiestos_app;
