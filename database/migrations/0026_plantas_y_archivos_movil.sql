-- Canal móvil (A6c): catálogo de plantas (PTAR) para el paso 3, recepción de archivos de evidencia y aviso de
-- incidencia para el panel. SOLO fosa fija por ahora; sanitarios y fosas propias llevan otra lógica (PG-24).

-- Plantas de descarga: el modelo registra las PTAR como sitios que NO son punto de servicio (0008). Lista PROVISIONAL
-- hasta que el catálogo de destinatarios/plantas se defina (PG-08). Solo de la sucursal del operador, solo lectura.
CREATE FUNCTION seguridad.plantas_operador(o uuid, a uuid, i uuid) RETURNS jsonb
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
                                WHERE (ps.organizacion_id,ps.sucursal_id,ps.sitio_id)=(s.organizacion_id,s.sucursal_id,s.id))) p
        WHERE p.nombre IS NOT NULL), '[]'::jsonb);
END $$;
REVOKE ALL ON FUNCTION seguridad.plantas_operador(uuid,uuid,uuid) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION seguridad.plantas_operador(uuid,uuid,uuid) TO manifiestos_app;

-- Registra un archivo YA verificado (tamaño y SHA-256 comprobados por la API contra lo declarado en el sobre) y guardado
-- primero en el VPS. Idempotente. Encola la subida a Drive (no se ejecuta aquí; solo backend, nunca la app).
-- Si es la foto de un PROBLEMA, además crea el aviso de Incidencias del panel (sin estatus ni efecto sobre la orden).
CREATE FUNCTION sync.registrar_archivo(o uuid, a uuid, i uuid, p_evidencia uuid, p_mime text, p_clave text) RETURNS jsonb
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE c record; e negocio.evidencia_captura%ROWTYPE; k negocio.captura%ROWTYPE; t negocio.captura_trabajo%ROWTYPE;
    r record; ruta uuid; motivo text;
BEGIN
    SELECT * INTO c FROM seguridad.contexto_operador(o,a,i);
    IF NOT FOUND THEN RAISE EXCEPTION 'Contexto no autorizado' USING ERRCODE='42501'; END IF;
    IF coalesce(btrim(p_mime),'')='' OR coalesce(btrim(p_clave),'')='' THEN RAISE EXCEPTION 'Archivo incompleto' USING ERRCODE='22023'; END IF;
    SELECT x.* INTO e FROM negocio.evidencia_captura x
    JOIN negocio.captura kk ON (kk.organizacion_id,kk.sucursal_id,kk.id)=(x.organizacion_id,x.sucursal_id,x.captura_id)
    WHERE x.organizacion_id=o AND x.sucursal_id=c.sucursal_id AND x.id=p_evidencia AND kk.instalacion_app_id=i
    FOR UPDATE OF x;
    IF NOT FOUND THEN RETURN NULL; END IF;
    IF e.archivo_id IS NOT NULL THEN RETURN jsonb_build_object('estado','recibida','id',e.id); END IF;

    INSERT INTO negocio.archivo(organizacion_id,sucursal_id,id,sha256,tamano_bytes,mime) VALUES(o,c.sucursal_id,e.id,e.sha256,e.tamano_bytes,p_mime);
    INSERT INTO negocio.ubicacion_archivo(organizacion_id,sucursal_id,archivo_id,proveedor,clave_externa,verificada_en)
    VALUES(o,c.sucursal_id,e.id,'vps',p_clave,clock_timestamp());
    UPDATE negocio.evidencia_captura SET archivo_id=e.id, recibida_en=clock_timestamp()
    WHERE organizacion_id=o AND sucursal_id=c.sucursal_id AND id=e.id;
    INSERT INTO sync.tarea(organizacion_id,sucursal_id,clave,tipo,datos)
    VALUES(o,c.sucursal_id,'drive:'||e.id::text,'subir_drive',jsonb_build_object('archivoId',e.id)) ON CONFLICT DO NOTHING;

    SELECT * INTO t FROM negocio.captura_trabajo WHERE (organizacion_id,sucursal_id,captura_id)=(o,c.sucursal_id,e.captura_id);
    IF t.tipo='PROBLEMA' AND e.rol='FOTO' THEN
        SELECT * INTO k FROM negocio.captura WHERE (organizacion_id,sucursal_id,id)=(o,c.sucursal_id,e.captura_id);
        SELECT * INTO r FROM negocio.resolver_orden_derivada(o,c.sucursal_id,t.persona_id,t.jornada_id,t.orden_derivada_id);
        SELECT j.ruta_id INTO ruta FROM negocio.jornada j WHERE (j.organizacion_id,j.sucursal_id,j.id)=(o,c.sucursal_id,t.jornada_id);
        motivo:=CASE WHEN t.problema_motivo IN ('sin_acceso','cliente_ausente','unidad_danada','servicio_parcial','otro') THEN t.problema_motivo ELSE 'otro' END;
        INSERT INTO negocio.incidencia(organizacion_id,sucursal_id,servicio,motivo,descripcion,ocurrida_en,ruta_id,parada_id,cliente_id,vehiculo_id,persona_id,
            evidencia_archivo_id,latitud,longitud,precision_m)
        VALUES(o,c.sucursal_id,'fosa',motivo,t.problema_comentario,k.ocurrida_en,ruta,r.parada_id,r.cliente_id,r.vehiculo_id,t.persona_id,
            e.id,round(e.latitud,6),round(e.longitud,6),round(e.precision_metros,1));
    END IF;
    RETURN jsonb_build_object('estado','recibida','id',e.id);
END $$;
REVOKE ALL ON FUNCTION sync.registrar_archivo(uuid,uuid,uuid,uuid,text,text) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION sync.registrar_archivo(uuid,uuid,uuid,uuid,text,text) TO manifiestos_app;

-- Datos que la API necesita para verificar una subida sin exponer nada ajeno: esperado (tamaño, SHA-256) y estado.
CREATE FUNCTION sync.evidencia_esperada(o uuid, a uuid, i uuid, p_evidencia uuid) RETURNS TABLE(tamano_bytes bigint, sha256 bytea, rol text, recibida boolean)
LANGUAGE plpgsql STABLE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE c record;
BEGIN
    SELECT * INTO c FROM seguridad.contexto_operador(o,a,i);
    IF NOT FOUND THEN RAISE EXCEPTION 'Contexto no autorizado' USING ERRCODE='42501'; END IF;
    RETURN QUERY SELECT x.tamano_bytes, x.sha256, x.rol, x.archivo_id IS NOT NULL
    FROM negocio.evidencia_captura x JOIN negocio.captura kk ON (kk.organizacion_id,kk.sucursal_id,kk.id)=(x.organizacion_id,x.sucursal_id,x.captura_id)
    WHERE x.organizacion_id=o AND x.sucursal_id=c.sucursal_id AND x.id=p_evidencia AND kk.instalacion_app_id=i;
END $$;
REVOKE ALL ON FUNCTION sync.evidencia_esperada(uuid,uuid,uuid,uuid) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION sync.evidencia_esperada(uuid,uuid,uuid,uuid) TO manifiestos_app;
