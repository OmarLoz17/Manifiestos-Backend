-- 20-nov-2026 (revisión de código, H20).
--   * El aviso que crea el sistema por baños incompletos se marca con una columna explícita (`automatica`) en vez de reconocerse por su texto: un reporte
--     manual «servicio_parcial» sin comentario (descripción NULL) ya no se confunde con él ni desaparece de Monitoreo. Las filas anteriores quedan en false
--     (el panel conserva además su regla por texto solo para ese historial).
--   * La incidencia de FOSA que nace de la foto de un problema resuelve la orden con el instante de la captura (no con el de la llegada del archivo).
ALTER TABLE negocio.incidencia ADD COLUMN automatica boolean NOT NULL DEFAULT false;

CREATE OR REPLACE FUNCTION negocio.incidencia_por_banos_incompletos() RETURNS trigger
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE ruta uuid;
BEGIN
    IF NEW.banos_succionados >= NEW.banos_asignados THEN RETURN NEW; END IF;
    SELECT j.ruta_id INTO ruta FROM negocio.jornada j WHERE (j.organizacion_id,j.sucursal_id,j.id)=(NEW.organizacion_id,NEW.sucursal_id,NEW.jornada_id);
    INSERT INTO negocio.incidencia(organizacion_id,sucursal_id,servicio,motivo,descripcion,ocurrida_en,ruta_id,parada_id,cliente_id,vehiculo_id,persona_id,
        banos_asignados,banos_atendidos,automatica)
    VALUES(NEW.organizacion_id,NEW.sucursal_id,'sanitario','servicio_parcial',
        'Se atendieron '||NEW.banos_succionados||' de '||NEW.banos_asignados||' baños.',
        NEW.fecha_captura,ruta,NEW.parada_id,NULL,NEW.vehiculo_id,NEW.persona_id,NEW.banos_asignados,NEW.banos_succionados,true);
    RETURN NEW;
END $$;

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
    RETURN jsonb_build_object('estado','recibida','id',e.id);
END $$;
