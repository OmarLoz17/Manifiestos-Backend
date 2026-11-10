-- Recepción de capturas del operador (A6a). Solo registra HECHOS recibidos; NO genera manifiestos, folios
-- ni incidencias de panel (PG-03/06/23). El servidor guarda el sobre exacto y el contenido aceptado tal como llegan;
-- no define texto legal (PG-05). Las órdenes de A5 son derivadas (jornada+parada+cliente+vuelta): se conservan
-- aquí sin tocar captura.orden_version_id, que queda NULL.
-- El autor es la persona/identidad/asignación del sobre, que debe coincidir con la sesión vigente: nunca se reatribuye.

-- Contexto de trabajo de cada captura (sobre propuesto, versión 1).
CREATE TABLE negocio.captura_trabajo (
    organizacion_id uuid NOT NULL, sucursal_id uuid NOT NULL, captura_id uuid NOT NULL,
    tipo text NOT NULL CHECK (tipo IN ('SERVICIO_OPERADOR','FIRMA_CLIENTE','RECEPCION_PLANTA','PROBLEMA')),
    persona_id uuid NOT NULL, identidad_id uuid NOT NULL,
    orden_derivada_id uuid NOT NULL, jornada_id uuid NOT NULL,
    asignacion_trabajo text NOT NULL CHECK (btrim(asignacion_trabajo) <> ''), revision_trabajo text NOT NULL CHECK (btrim(revision_trabajo) <> ''),
    version_datos text NOT NULL CHECK (btrim(version_datos) <> ''), requiere_firma_cliente boolean NOT NULL,
    datos_orden bytea NOT NULL CHECK (octet_length(datos_orden) > 0), -- snapshot exacto recibido
    contenido_aceptado_id uuid, nombre_firmante text CHECK (nombre_firmante IS NULL OR btrim(nombre_firmante) <> ''),
    planta_id text, planta_nombre text,
    problema_motivo text, problema_comentario text,
    PRIMARY KEY (organizacion_id, sucursal_id, captura_id),
    FOREIGN KEY (organizacion_id, sucursal_id, captura_id) REFERENCES negocio.captura ON DELETE RESTRICT,
    FOREIGN KEY (organizacion_id, sucursal_id, contenido_aceptado_id) REFERENCES negocio.contenido_aceptado ON DELETE RESTRICT,
    FOREIGN KEY (organizacion_id, persona_id) REFERENCES seguridad.persona ON DELETE RESTRICT,
    CHECK ((tipo = 'PROBLEMA') = (problema_motivo IS NOT NULL)),
    CHECK ((tipo = 'RECEPCION_PLANTA') = (planta_id IS NOT NULL))
);
CREATE INDEX captura_trabajo_orden ON negocio.captura_trabajo (organizacion_id, sucursal_id, orden_derivada_id);
CREATE INDEX captura_trabajo_persona ON negocio.captura_trabajo (organizacion_id, persona_id);

-- Evidencias esperadas por captura. El archivo llega después (subida verificada); archivo_id NULL = pendiente.
CREATE TABLE negocio.evidencia_captura (
    organizacion_id uuid NOT NULL, sucursal_id uuid NOT NULL, id uuid NOT NULL, captura_id uuid NOT NULL,
    rol text NOT NULL CHECK (rol IN ('FOTO','FIRMA_OPERADOR','FIRMA_CLIENTE','FIRMA_RECEPCION')),
    sha256 bytea NOT NULL CHECK (octet_length(sha256) = 32), tamano_bytes bigint NOT NULL CHECK (tamano_bytes > 0),
    latitud numeric(10,7) CHECK (latitud BETWEEN -90 AND 90), longitud numeric(10,7) CHECK (longitud BETWEEN -180 AND 180),
    precision_metros numeric(12,3) CHECK (precision_metros >= 0), lectura_en timestamptz,
    archivo_id uuid, recibida_en timestamptz,
    PRIMARY KEY (organizacion_id, sucursal_id, id),
    FOREIGN KEY (organizacion_id, sucursal_id, captura_id) REFERENCES negocio.captura ON DELETE RESTRICT,
    FOREIGN KEY (organizacion_id, sucursal_id, archivo_id) REFERENCES negocio.archivo ON DELETE RESTRICT,
    CHECK ((rol = 'FOTO') = (latitud IS NOT NULL AND longitud IS NOT NULL AND precision_metros IS NOT NULL AND lectura_en IS NOT NULL)),
    CHECK ((archivo_id IS NULL) = (recibida_en IS NULL))
);
CREATE INDEX evidencia_captura_captura ON negocio.evidencia_captura (organizacion_id, sucursal_id, captura_id);

DO $$
DECLARE t text;
BEGIN
    FOREACH t IN ARRAY ARRAY['captura_trabajo','evidencia_captura'] LOOP
        EXECUTE format('ALTER TABLE negocio.%I ENABLE ROW LEVEL SECURITY', t);
        EXECUTE format('CREATE POLICY contexto ON negocio.%I TO manifiestos_app USING (seguridad.puede(organizacion_id, sucursal_id, ''datos.leer''))', t);
        EXECUTE format('GRANT SELECT ON negocio.%I TO manifiestos_app', t);
    END LOOP;
    EXECUTE 'CREATE TRIGGER conservar_historia BEFORE UPDATE OR DELETE ON negocio.captura_trabajo FOR EACH ROW EXECUTE FUNCTION infra.inmutable()';
END $$;

CREATE FUNCTION sync.recibir_captura(o uuid, a uuid, i uuid, p_solicitud bytea) RETURNS jsonb
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE c record; s jsonb; ctx jsonb; trab jsonb; previo sync.comando%ROWTYPE; cid uuid; disp uuid; tp text;
    ms bigint; cont uuid; ev jsonb; res jsonb; ubic jsonb; hay_foto boolean:=false; recibida timestamptz;
BEGIN
    SELECT * INTO c FROM seguridad.contexto_operador(o,a,i);
    IF NOT FOUND THEN RAISE EXCEPTION 'Contexto no autorizado' USING ERRCODE='42501'; END IF;
    BEGIN s:=convert_from(p_solicitud,'UTF8')::jsonb;
    EXCEPTION WHEN OTHERS THEN RAISE EXCEPTION 'Sobre inválido' USING ERRCODE='22023'; END;
    IF coalesce(jsonb_typeof(s),'')<>'object' OR s->>'version' IS DISTINCT FROM '1' THEN RAISE EXCEPTION 'Versión de sobre no compatible' USING ERRCODE='22023'; END IF;
    cid:=(s->>'id')::uuid;
    PERFORM pg_advisory_xact_lock(hashtextextended(o::text||i::text||cid::text,0));
    SELECT * INTO previo FROM sync.comando WHERE organizacion_id=o AND sucursal_id=c.sucursal_id AND instalacion_app_id=i AND id=cid;
    IF FOUND THEN
        IF previo.solicitud IS DISTINCT FROM p_solicitud THEN
            RAISE EXCEPTION 'La clave ya identifica otro contenido' USING ERRCODE='P0409';
        END IF;
        RETURN previo.resultado;
    END IF;

    -- El autor del sobre debe ser la persona, identidad y asignación de la sesión vigente (sin reatribuir).
    ctx:=s->'contexto';
    IF coalesce(jsonb_typeof(ctx),'')<>'object' OR ctx->>'instalacionId' IS DISTINCT FROM i::text
       OR ctx->>'personaId' IS DISTINCT FROM c.persona_id::text OR ctx->>'identidadId' IS DISTINCT FROM c.identidad_id::text
       OR ctx->>'asignacionEquipoId' IS DISTINCT FROM c.asignacion_id::text
       OR ctx->>'revisionEquipo' IS DISTINCT FROM c.revision_equipo::text THEN
        RAISE EXCEPTION 'El sobre no corresponde al contexto autorizado' USING ERRCODE='42501';
    END IF;
    tp:=s->>'tipo';
    IF tp IS NULL OR tp NOT IN ('SERVICIO_OPERADOR','FIRMA_CLIENTE','RECEPCION_PLANTA','PROBLEMA') THEN RAISE EXCEPTION 'Tipo no válido' USING ERRCODE='22023'; END IF;
    trab:=s->'trabajo';
    IF coalesce(jsonb_typeof(trab),'')<>'object' OR coalesce(btrim(trab->>'datosOrdenJson'),'')='' THEN RAISE EXCEPTION 'Falta el trabajo' USING ERRCODE='22023'; END IF;
    ms:=(s->>'fechaEpochMs')::bigint;
    IF ms IS NULL OR ms<=0 OR coalesce(jsonb_typeof(s->'evidencias'),'')<>'array' OR jsonb_array_length(s->'evidencias')=0 THEN RAISE EXCEPTION 'Sobre incompleto' USING ERRCODE='22023'; END IF;
    IF (tp='PROBLEMA')<>coalesce(jsonb_typeof(s->'problema')='object',false)
       OR (tp='PROBLEMA')=coalesce(jsonb_typeof(s->'aceptado')='object',false)
       OR (tp='RECEPCION_PLANTA')<>coalesce(jsonb_typeof(s->'planta')='object',false) THEN
        RAISE EXCEPTION 'Contenido inconsistente con el tipo' USING ERRCODE='22023';
    END IF;

    SELECT ae.dispositivo_id INTO disp FROM seguridad.asignacion_equipo ae
    WHERE ae.organizacion_id=o AND ae.sucursal_id=c.sucursal_id AND ae.id=c.asignacion_id;
    INSERT INTO negocio.captura(organizacion_id,sucursal_id,id,asignacion_equipo_id,dispositivo_id,instalacion_app_id,ocurrida_en,resultado_declarado,incidencia_declarada)
    VALUES(o,c.sucursal_id,cid,c.asignacion_id,disp,i,to_timestamp(ms/1000.0),tp,s->'problema'->>'motivo')
    RETURNING recibida_en INTO recibida;

    IF coalesce(jsonb_typeof(s->'aceptado')='object',false) THEN
        -- Bytes exactos del texto aceptado; su redacción y alcance legal siguen pendientes (PG-05).
        INSERT INTO negocio.contenido_aceptado(organizacion_id,sucursal_id,captura_id,formato,version_contrato,contenido)
        VALUES(o,c.sucursal_id,cid,'text/plain; charset=utf-8',s->'aceptado'->>'version',convert_to(s->'aceptado'->>'textoExacto','UTF8'))
        RETURNING id INTO cont;
    END IF;

    INSERT INTO negocio.captura_trabajo(organizacion_id,sucursal_id,captura_id,tipo,persona_id,identidad_id,orden_derivada_id,jornada_id,
        asignacion_trabajo,revision_trabajo,version_datos,requiere_firma_cliente,datos_orden,contenido_aceptado_id,nombre_firmante,
        planta_id,planta_nombre,problema_motivo,problema_comentario)
    VALUES(o,c.sucursal_id,cid,tp,c.persona_id,c.identidad_id,(trab->>'ordenId')::uuid,(trab->>'jornadaId')::uuid,
        trab->>'asignacionTrabajoId',trab->>'revisionTrabajo',trab->>'versionDatos',(trab->>'requiereFirmaCliente')::boolean,
        convert_to(trab->>'datosOrdenJson','UTF8'),cont,nullif(btrim(s->>'nombreFirmante'),''),
        s->'planta'->>'id',s->'planta'->>'nombre',s->'problema'->>'motivo',nullif(s->'problema'->>'comentario',''));

    FOR ev IN SELECT value FROM jsonb_array_elements(s->'evidencias') LOOP
        ubic:=ev->'ubicacion';
        IF (ev->>'rol'='FOTO')<>coalesce(jsonb_typeof(ubic)='object',false) THEN RAISE EXCEPTION 'Cada foto requiere ubicación' USING ERRCODE='22023'; END IF;
        INSERT INTO negocio.evidencia_captura(organizacion_id,sucursal_id,id,captura_id,rol,sha256,tamano_bytes,latitud,longitud,precision_metros,lectura_en)
        VALUES(o,c.sucursal_id,(ev->>'id')::uuid,cid,ev->>'rol',decode(ev->>'sha256','hex'),(ev->>'tamano')::bigint,
            (ubic->>'latitud')::numeric,(ubic->>'longitud')::numeric,(ubic->>'precisionMetros')::numeric,
            CASE WHEN coalesce(jsonb_typeof(ubic),'')<>'object' THEN NULL ELSE to_timestamp((ubic->>'fechaEpochMs')::bigint/1000.0) END);
        IF ev->>'rol'='FOTO' AND NOT hay_foto THEN
            hay_foto:=true;
            INSERT INTO negocio.posicion_captura(organizacion_id,sucursal_id,captura_id,latitud,longitud,precision_metros,antiguedad_segundos,lectura_en)
            VALUES(o,c.sucursal_id,cid,(ubic->>'latitud')::numeric,(ubic->>'longitud')::numeric,(ubic->>'precisionMetros')::numeric,
                greatest(0,(ms-(ubic->>'fechaEpochMs')::bigint)/1000.0),to_timestamp((ubic->>'fechaEpochMs')::bigint/1000.0));
        END IF;
    END LOOP;

    res:=jsonb_build_object('estado','recibida','capturaId',cid,'recibidaEn',recibida,
        'evidencias',(SELECT jsonb_agg(jsonb_build_object('id',e.id,'estado','pendiente') ORDER BY e.id)
                      FROM negocio.evidencia_captura e WHERE e.organizacion_id=o AND e.sucursal_id=c.sucursal_id AND e.captura_id=cid));
    INSERT INTO sync.comando(organizacion_id,sucursal_id,instalacion_app_id,id,solicitud,resultado) VALUES(o,c.sucursal_id,i,cid,p_solicitud,res);
    RETURN res;
END $$;

-- Estado actual (con archivos ya recibidos) de una captura de ESTA instalación; NULL si no existe o es ajena.
CREATE FUNCTION sync.consultar_captura(o uuid, a uuid, i uuid, p_captura uuid) RETURNS jsonb
LANGUAGE plpgsql STABLE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE c record; r jsonb;
BEGIN
    SELECT * INTO c FROM seguridad.contexto_operador(o,a,i);
    IF NOT FOUND THEN RAISE EXCEPTION 'Contexto no autorizado' USING ERRCODE='42501'; END IF;
    SELECT jsonb_build_object('estado','recibida','capturaId',k.id,'recibidaEn',k.recibida_en,
        'evidencias',(SELECT jsonb_agg(jsonb_build_object('id',e.id,'estado',CASE WHEN e.archivo_id IS NULL THEN 'pendiente' ELSE 'recibida' END) ORDER BY e.id)
                      FROM negocio.evidencia_captura e WHERE (e.organizacion_id,e.sucursal_id,e.captura_id)=(k.organizacion_id,k.sucursal_id,k.id)))
    INTO r
    FROM negocio.captura k
    WHERE k.organizacion_id=o AND k.sucursal_id=c.sucursal_id AND k.id=p_captura AND k.instalacion_app_id=i;
    RETURN r;
END $$;

REVOKE ALL ON FUNCTION sync.recibir_captura(uuid,uuid,uuid,bytea) FROM PUBLIC;
REVOKE ALL ON FUNCTION sync.consultar_captura(uuid,uuid,uuid,uuid) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION sync.recibir_captura(uuid,uuid,uuid,bytea) TO manifiestos_app;
GRANT EXECUTE ON FUNCTION sync.consultar_captura(uuid,uuid,uuid,uuid) TO manifiestos_app;
