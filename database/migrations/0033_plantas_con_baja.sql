-- 12-nov-2026: pantalla «Sitios y plantas». Una planta tratadora (sitio sin punto de servicio) puede darse de baja (sitio_baja) cuando
-- no tiene descargas; la lista de plantas que descarga al teléfono ya no debe incluir las dadas de baja. Solo CREATE OR REPLACE.
CREATE OR REPLACE FUNCTION seguridad.plantas_operador(o uuid, a uuid, i uuid) RETURNS jsonb
LANGUAGE plpgsql STABLE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE c record;
BEGIN
    SELECT * INTO c FROM seguridad.contexto_operador(o,a,i);
    IF NOT FOUND THEN RAISE EXCEPTION 'Contexto no autorizado' USING ERRCODE='42501'; END IF;
    RETURN coalesce((
        SELECT jsonb_agg(jsonb_build_object('id', p.id, 'nombre', p.nombre) ORDER BY p.nombre, p.id)
        FROM (SELECT s.id, coalesce(nullif(btrim(fs.nombre),''), s.referencia) AS nombre
              FROM negocio.sitio s
              LEFT JOIN LATERAL (SELECT x.nombre FROM negocio.ficha_sitio x
                  WHERE (x.organizacion_id,x.sucursal_id,x.sitio_id)=(s.organizacion_id,s.sucursal_id,s.id)
                  ORDER BY x.revision DESC LIMIT 1) fs ON true
              WHERE s.organizacion_id=o AND s.sucursal_id=c.sucursal_id
                AND NOT EXISTS (SELECT 1 FROM negocio.punto_servicio ps
                                WHERE (ps.organizacion_id,ps.sucursal_id,ps.sitio_id)=(s.organizacion_id,s.sucursal_id,s.id))
                AND NOT EXISTS (SELECT 1 FROM negocio.sitio_baja sb
                                WHERE (sb.organizacion_id,sb.sucursal_id,sb.sitio_id)=(s.organizacion_id,s.sucursal_id,s.id))) p
        WHERE p.nombre IS NOT NULL), '[]'::jsonb);
END $$;
