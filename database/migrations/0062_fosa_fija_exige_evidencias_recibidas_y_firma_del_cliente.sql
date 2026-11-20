-- 20-nov-2026 (revisión de código H03; decisión del usuario del 20-nov): FOSA FIJA exige la firma del CLIENTE en cada orden, porque el cliente autorizado
-- es el generador del manifiesto. Fosa propia y sanitarios no la piden: el generador es nuestra empresa con su firma precargada. Esto sustituye la opción B de
-- la migración 0053 («sin firma del cliente»).
--   * El manifiesto de una orden de fosa fija se arma cuando TODAS las evidencias requeridas YA se recibieron (archivo guardado), no al llegar el sobre:
--     foto y firma del transportista (servicio), firma del cliente (si el cliente es autorizado) y firma de quien recibe en la planta.
--   * Al recibirse cada archivo se vuelve a intentar el armado (sync.registrar_archivo), así el orden de llegada no importa.
CREATE FUNCTION negocio.orden_con_evidencias_completas(o uuid, s uuid, p_orden uuid, p_autorizado boolean) RETURNS boolean
LANGUAGE sql STABLE SECURITY DEFINER SET search_path = pg_catalog AS $$
    WITH requeridas(tipo, rol) AS (
        VALUES ('SERVICIO_OPERADOR','FOTO'), ('SERVICIO_OPERADOR','FIRMA_OPERADOR'), ('RECEPCION_PLANTA','FIRMA_RECEPCION')
        UNION ALL SELECT 'FIRMA_CLIENTE','FIRMA_CLIENTE' WHERE p_autorizado
    )
    SELECT coalesce(bool_and(EXISTS (
        SELECT 1 FROM negocio.captura_trabajo t
        JOIN negocio.evidencia_captura e ON (e.organizacion_id,e.sucursal_id,e.captura_id)=(t.organizacion_id,t.sucursal_id,t.captura_id)
        WHERE (t.organizacion_id,t.sucursal_id,t.orden_derivada_id)=(o,s,p_orden) AND t.tipo=q.tipo AND e.rol=q.rol AND e.archivo_id IS NOT NULL
          AND NOT EXISTS (SELECT 1 FROM negocio.captura_por_revisar x WHERE (x.organizacion_id,x.sucursal_id,x.captura_id)=(t.organizacion_id,t.sucursal_id,t.captura_id)))), false)
    FROM requeridas q
$$;
REVOKE ALL ON FUNCTION negocio.orden_con_evidencias_completas(uuid, uuid, uuid, boolean) FROM PUBLIC;

CREATE OR REPLACE FUNCTION negocio.armar_orden_fosa_fija(o uuid, s uuid, p_persona uuid, p_jornada uuid, p_orden uuid, p_instante timestamptz)
RETURNS void LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE r record; serv record; m uuid;
BEGIN
    IF EXISTS (SELECT 1 FROM negocio.orden_manifiesto om WHERE (om.organizacion_id,om.sucursal_id,om.orden_derivada_id)=(o,s,p_orden)) THEN RETURN; END IF;
    -- La unidad y la persona son las de la captura de servicio (la succión), no las de la última captura que llegó (H04).
    SELECT t.persona_id, k.ocurrida_en INTO serv
    FROM negocio.captura_trabajo t JOIN negocio.captura k ON (k.organizacion_id,k.sucursal_id,k.id)=(t.organizacion_id,t.sucursal_id,t.captura_id)
    WHERE (t.organizacion_id,t.sucursal_id,t.orden_derivada_id)=(o,s,p_orden) AND t.tipo='SERVICIO_OPERADOR'
      AND NOT EXISTS (SELECT 1 FROM negocio.captura_por_revisar x WHERE (x.organizacion_id,x.sucursal_id,x.captura_id)=(t.organizacion_id,t.sucursal_id,t.captura_id))
    ORDER BY k.ocurrida_en, t.captura_id LIMIT 1;
    IF NOT FOUND THEN RETURN; END IF;
    SELECT * INTO r FROM negocio.resolver_orden_derivada(o,s,serv.persona_id,p_jornada,p_orden,serv.ocurrida_en);
    IF NOT FOUND THEN RETURN; END IF;
    IF NOT negocio.orden_con_evidencias_completas(o,s,p_orden,r.autorizado) THEN RETURN; END IF;
    SELECT g.manifiesto INTO m FROM negocio.armar_manifiesto_fosa_fija(o,s,r.cliente_id,r.vehiculo_id,true) g;
    INSERT INTO negocio.orden_manifiesto(organizacion_id,sucursal_id,orden_derivada_id,jornada_id,parada_id,cliente_id,vuelta,vehiculo_id,manifiesto_id)
    VALUES(o,s,p_orden,p_jornada,r.parada_id,r.cliente_id,r.vuelta,r.vehiculo_id,m);
END $$;
REVOKE ALL ON FUNCTION negocio.armar_orden_fosa_fija(uuid, uuid, uuid, uuid, uuid, timestamptz) FROM PUBLIC;

CREATE OR REPLACE FUNCTION sync.registrar_archivo(o uuid, a uuid, i uuid, p_evidencia uuid, p_mime text, p_clave text) RETURNS jsonb
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE c record; e negocio.evidencia_captura%ROWTYPE; k negocio.captura%ROWTYPE; t negocio.captura_trabajo%ROWTYPE;
    r record; ps record; ruta uuid; motivo text;
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
        motivo:=CASE WHEN t.problema_motivo IN ('sin_acceso','cliente_ausente','unidad_danada','servicio_parcial','otro') THEN t.problema_motivo ELSE 'otro' END;
        SELECT * INTO ps FROM negocio.resolver_parada_derivada(o,c.sucursal_id,t.persona_id,t.jornada_id,t.orden_derivada_id,k.ocurrida_en);
        IF FOUND THEN
            -- Parada de sanitarios: sin cliente; los baños asignados salen del plan vigente a la fecha de la captura.
            INSERT INTO negocio.incidencia(organizacion_id,sucursal_id,servicio,motivo,descripcion,ocurrida_en,ruta_id,parada_id,cliente_id,vehiculo_id,persona_id,
                banos_asignados,evidencia_archivo_id,latitud,longitud,precision_m)
            VALUES(o,c.sucursal_id,'sanitario',motivo,t.problema_comentario,k.ocurrida_en,ps.ruta_id,ps.parada_id,NULL,ps.vehiculo_id,t.persona_id,
                (SELECT coalesce(sum((x->>'banos')::integer),0) FROM jsonb_array_elements(negocio.clientes_parada_en(o,c.sucursal_id,ps.parada_id,k.ocurrida_en)) x),
                e.id,round(e.latitud,6),round(e.longitud,6),round(e.precision_metros,1));
        ELSE
            SELECT * INTO r FROM negocio.resolver_orden_derivada(o,c.sucursal_id,t.persona_id,t.jornada_id,t.orden_derivada_id,k.ocurrida_en);
            SELECT j.ruta_id INTO ruta FROM negocio.jornada j WHERE (j.organizacion_id,j.sucursal_id,j.id)=(o,c.sucursal_id,t.jornada_id);
            INSERT INTO negocio.incidencia(organizacion_id,sucursal_id,servicio,motivo,descripcion,ocurrida_en,ruta_id,parada_id,cliente_id,vehiculo_id,persona_id,
                evidencia_archivo_id,latitud,longitud,precision_m)
            VALUES(o,c.sucursal_id,'fosa',motivo,t.problema_comentario,k.ocurrida_en,ruta,r.parada_id,r.cliente_id,r.vehiculo_id,t.persona_id,
                e.id,round(e.latitud,6),round(e.longitud,6),round(e.precision_metros,1));
        END IF;
    END IF;
    -- H03: al llegar un archivo de una captura de fosa fija se vuelve a intentar armar la orden (solo arma si TODAS las evidencias requeridas ya están recibidas).
    IF t.tipo IN ('SERVICIO_OPERADOR','FIRMA_CLIENTE','RECEPCION_PLANTA') AND t.orden_derivada_id IS NOT NULL THEN
        SELECT * INTO k FROM negocio.captura WHERE (organizacion_id,sucursal_id,id)=(o,c.sucursal_id,e.captura_id);
        PERFORM pg_advisory_xact_lock(hashtextextended('orden:'||o::text||':'||t.orden_derivada_id::text,0));
        PERFORM negocio.armar_orden_fosa_fija(o,c.sucursal_id,t.persona_id,t.jornada_id,t.orden_derivada_id,k.ocurrida_en);
    END IF;
    RETURN jsonb_build_object('estado','recibida','id',e.id);
END $$;
