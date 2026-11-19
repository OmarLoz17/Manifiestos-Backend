-- Tirada mezclada de fosa propia y baños (decisión 0032, etapa 1: servidor). Una fosa propia (cliente general) es una parada «como un baño muy grande»:
-- se captura con foto con ubicación y la marca `sanitarios.fosa` (sin cantidad). Se tira con la lógica de sanitarios: un solo folio por tirada, desglose por parada
-- con coordenadas y litros, descripción «Excreta humana». Los baños pesan 16 L y el resto de la capacidad se reparte entre las fosas (con la variación de siempre).
-- Aditivo: una app anterior sigue enviando solo baños. El cliente con convenio (fosa fija) NO entra aquí.
ALTER TABLE negocio.captura_trabajo ADD COLUMN fosa boolean NOT NULL DEFAULT false;
ALTER TABLE negocio.succion_parada ADD COLUMN es_fosa boolean NOT NULL DEFAULT false;
ALTER TABLE negocio.manifiesto_parada ADD COLUMN es_fosa boolean NOT NULL DEFAULT false;

CREATE OR REPLACE FUNCTION sync.recibir_captura(o uuid, a uuid, i uuid, p_solicitud bytea) RETURNS jsonb
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE c record; s jsonb; ctx jsonb; trab jsonb; previo sync.comando%ROWTYPE; cid uuid; disp uuid; tp text;
    ms bigint; cont uuid; ev jsonb; res jsonb; ubic jsonb; hay_foto boolean:=false; recibida timestamptz;
    san jsonb; v_fosa boolean := false; v_banos integer; v_asig integer; v_incl uuid[]; v_unidad_txt text;
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
    IF tp IS NULL OR tp NOT IN ('SERVICIO_OPERADOR','FIRMA_CLIENTE','RECEPCION_PLANTA','PROBLEMA','SERVICIO_SANITARIOS','TIRADA_SANITARIOS') THEN
        RAISE EXCEPTION 'Tipo no válido' USING ERRCODE='22023';
    END IF;
    trab:=s->'trabajo';
    IF coalesce(jsonb_typeof(trab),'')<>'object' OR coalesce(btrim(trab->>'datosOrdenJson'),'')='' THEN RAISE EXCEPTION 'Falta el trabajo' USING ERRCODE='22023'; END IF;
    ms:=(s->>'fechaEpochMs')::bigint;
    IF ms IS NULL OR ms<=0 OR coalesce(jsonb_typeof(s->'evidencias'),'')<>'array' OR jsonb_array_length(s->'evidencias')=0 THEN RAISE EXCEPTION 'Sobre incompleto' USING ERRCODE='22023'; END IF;
    IF (tp='PROBLEMA')<>coalesce(jsonb_typeof(s->'problema')='object',false)
       OR (tp IN ('PROBLEMA','SERVICIO_SANITARIOS'))=coalesce(jsonb_typeof(s->'aceptado')='object',false)
       OR (tp IN ('RECEPCION_PLANTA','TIRADA_SANITARIOS'))<>coalesce(jsonb_typeof(s->'planta')='object',false) THEN
        RAISE EXCEPTION 'Contenido inconsistente con el tipo' USING ERRCODE='22023';
    END IF;

    -- Reglas de forma de sanitarios (422). Lo que se pueda resolver después (parada, unidad, planta) NO se rechaza aquí: queda para revisión.
    san:=s->'sanitarios';
    IF tp='SERVICIO_SANITARIOS' THEN
        ubic:=s->'evidencias'->0->'ubicacion';
        IF jsonb_array_length(s->'evidencias')<>1 OR s->'evidencias'->0->>'rol' IS DISTINCT FROM 'FOTO' OR coalesce(jsonb_typeof(ubic),'')<>'object'
           OR ubic->>'latitud' IS NULL OR ubic->>'longitud' IS NULL OR ubic->>'precisionMetros' IS NULL OR ubic->>'fechaEpochMs' IS NULL THEN
            RAISE EXCEPTION 'La captura de baños requiere exactamente una foto con ubicación' USING ERRCODE='P0422';
        END IF;
        IF coalesce(jsonb_typeof(san),'')<>'object' OR coalesce(jsonb_typeof(san->'banos'),'')<>'number' OR coalesce(jsonb_typeof(san->'banosAsignados'),'')<>'number'
           OR NOT coalesce(san->>'banos' ~ '^[0-9]{1,9}$', false) OR NOT coalesce(san->>'banosAsignados' ~ '^[0-9]{1,9}$', false) THEN
            RAISE EXCEPTION 'Los baños deben ser enteros no negativos' USING ERRCODE='P0422';
        END IF;
        v_banos:=(san->>'banos')::integer; v_asig:=(san->>'banosAsignados')::integer;
        -- Decisión 0032: una fosa propia es una parada «como un baño muy grande»: la marca «fosa» sustituye a la cantidad (que va en 0).
        IF san->'fosa' IS NOT NULL AND jsonb_typeof(san->'fosa')<>'null' THEN
            IF jsonb_typeof(san->'fosa')<>'boolean' THEN RAISE EXCEPTION 'La marca de fosa debe ser verdadero o falso' USING ERRCODE='P0422'; END IF;
            v_fosa:=(san->>'fosa')::boolean;
            IF v_fosa AND v_banos<>0 THEN RAISE EXCEPTION 'Una fosa propia no lleva cantidad de baños' USING ERRCODE='P0422'; END IF;
        END IF;
    ELSIF tp='TIRADA_SANITARIOS' THEN
        IF jsonb_array_length(s->'evidencias')<>2
           OR (SELECT count(*) FROM jsonb_array_elements(s->'evidencias') x WHERE x->>'rol'='FIRMA_OPERADOR' AND coalesce(jsonb_typeof(x->'ubicacion'),'null')<>'object')<>1
           OR (SELECT count(*) FROM jsonb_array_elements(s->'evidencias') x WHERE x->>'rol'='FIRMA_RECEPCION' AND coalesce(jsonb_typeof(x->'ubicacion'),'null')<>'object')<>1 THEN
            RAISE EXCEPTION 'La tirada requiere la firma del operador y la de quien recibe' USING ERRCODE='P0422';
        END IF;
        IF char_length(coalesce(btrim(s->>'nombreFirmante'),'')) NOT BETWEEN 1 AND 160 THEN
            RAISE EXCEPTION 'Falta el nombre de quien recibe' USING ERRCODE='P0422';
        END IF;
        IF coalesce(s->'planta'->>'id','') !~ '^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$' THEN
            RAISE EXCEPTION 'Planta no válida' USING ERRCODE='P0422';
        END IF;
        IF coalesce(jsonb_typeof(san),'')<>'object' OR coalesce(jsonb_typeof(san->'capturasIncluidas'),'')<>'array'
           OR EXISTS (SELECT 1 FROM jsonb_array_elements(san->'capturasIncluidas') x
                      WHERE coalesce(jsonb_typeof(x),'')<>'string' OR (x#>>'{}') !~ '^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$') THEN
            RAISE EXCEPTION 'La tirada debe declarar las capturas incluidas' USING ERRCODE='P0422';
        END IF;
        v_incl:=ARRAY(SELECT (x#>>'{}')::uuid FROM jsonb_array_elements(san->'capturasIncluidas') x);
        BEGIN
            v_unidad_txt:=coalesce(((trab->>'datosOrdenJson')::jsonb)->>'unidadId','');
        EXCEPTION WHEN OTHERS THEN
            v_unidad_txt:='';   -- datosOrdenJson que no es JSON: se rechaza igual que una unidad ausente (422, no 500)
        END;
        IF v_unidad_txt !~ '^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$' THEN
            RAISE EXCEPTION 'La tirada debe indicar la unidad' USING ERRCODE='P0422';
        END IF;
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

    -- Evidencias ANTES del trabajo: el disparador de captura_trabajo (manifiesto de sanitarios) necesita ya la coordenada de la foto.
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

    INSERT INTO negocio.captura_trabajo(organizacion_id,sucursal_id,captura_id,tipo,persona_id,identidad_id,orden_derivada_id,jornada_id,
        asignacion_trabajo,revision_trabajo,version_datos,requiere_firma_cliente,datos_orden,contenido_aceptado_id,nombre_firmante,
        planta_id,planta_nombre,problema_motivo,problema_comentario,banos,banos_asignados,capturas_incluidas,fosa)
    VALUES(o,c.sucursal_id,cid,tp,c.persona_id,c.identidad_id,(trab->>'ordenId')::uuid,(trab->>'jornadaId')::uuid,
        trab->>'asignacionTrabajoId',trab->>'revisionTrabajo',trab->>'versionDatos',(trab->>'requiereFirmaCliente')::boolean,
        convert_to(trab->>'datosOrdenJson','UTF8'),cont,nullif(btrim(s->>'nombreFirmante'),''),
        s->'planta'->>'id',s->'planta'->>'nombre',s->'problema'->>'motivo',nullif(s->'problema'->>'comentario',''),
        v_banos,v_asig,v_incl,v_fosa);

    res:=jsonb_build_object('estado','recibida','capturaId',cid,'recibidaEn',recibida,
        'evidencias',(SELECT jsonb_agg(jsonb_build_object('id',e.id,'estado','pendiente') ORDER BY e.id)
                      FROM negocio.evidencia_captura e WHERE e.organizacion_id=o AND e.sucursal_id=c.sucursal_id AND e.captura_id=cid));
    INSERT INTO sync.comando(organizacion_id,sucursal_id,instalacion_app_id,id,solicitud,resultado) VALUES(o,c.sucursal_id,i,cid,p_solicitud,res);
    RETURN res;
END $$;

CREATE OR REPLACE FUNCTION negocio.procesar_captura_sanitarios(o uuid, s uuid, p_captura uuid) RETURNS void
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path = pg_catalog AS $$
DECLARE t negocio.captura_trabajo%ROWTYPE; k negocio.captura%ROWTYPE; r record; datos jsonb; veh uuid; planta uuid; nombre text; estado text;
BEGIN
    SELECT * INTO t FROM negocio.captura_trabajo WHERE (organizacion_id, sucursal_id, captura_id) = (o, s, p_captura);
    SELECT * INTO k FROM negocio.captura WHERE (organizacion_id, sucursal_id, id) = (o, s, p_captura);

    IF t.tipo = 'SERVICIO_SANITARIOS' THEN
        SELECT * INTO r FROM negocio.resolver_parada_derivada(o, s, t.persona_id, t.jornada_id, t.orden_derivada_id, k.ocurrida_en);
        IF NOT FOUND THEN
            INSERT INTO negocio.captura_por_revisar (organizacion_id, sucursal_id, captura_id, motivo) VALUES (o, s, p_captura, 'parada_no_resuelta');
            RETURN;
        END IF;
        PERFORM negocio.bloquear_unidad(o, r.vehiculo_id);
        INSERT INTO negocio.succion_parada (organizacion_id, sucursal_id, captura_id, jornada_id, parada_id, vehiculo_id, persona_id, sitio_id,
            banos_succionados, banos_asignados, litros_por_bano, fecha_captura, recibida_en, es_fosa)
        VALUES (o, s, p_captura, t.jornada_id, r.parada_id, r.vehiculo_id, t.persona_id, r.sitio_id,
            t.banos, t.banos_asignados, negocio.litros_por_bano(), k.ocurrida_en, k.recibida_en, coalesce(t.fosa, false));
        PERFORM negocio.reintentar_tiradas(o, s, r.vehiculo_id, p_captura);   -- succión tardía: puede completar una tirada que esperaba
        RETURN;
    END IF;

    -- TIRADA_SANITARIOS. La unidad viene en el trabajo; debe ser operada por la persona a la fecha de la captura.
    BEGIN
        datos := convert_from(t.datos_orden, 'UTF8')::jsonb;
        veh := (datos->>'unidadId')::uuid;
        planta := t.planta_id::uuid;
    EXCEPTION WHEN OTHERS THEN
        PERFORM negocio.marcar_tirada_por_revisar(o, s, p_captura, 'P0A03'); RETURN;
    END;
    nombre := negocio.nombre_planta(o, s, planta);
    IF veh IS NULL OR nombre IS NULL OR NOT EXISTS (SELECT 1 FROM negocio.vehiculo x WHERE (x.organizacion_id, x.sucursal_id, x.id) = (o, s, veh))
       OR NOT negocio.persona_opera_unidad(o, s, veh, t.persona_id, k.ocurrida_en) THEN
        PERFORM negocio.marcar_tirada_por_revisar(o, s, p_captura, 'P0A03'); RETURN;
    END IF;
    IF coalesce(cardinality(t.capturas_incluidas), 0) = 0 THEN
        PERFORM negocio.marcar_tirada_por_revisar(o, s, p_captura, 'P0A01'); RETURN;
    END IF;
    PERFORM negocio.bloquear_unidad(o, veh);
    BEGIN   -- subtransacción: si falla, no queda tirada a medias ni folio consumido
        IF EXISTS (SELECT 1 FROM negocio.tirada x WHERE (x.organizacion_id, x.sucursal_id, x.id) = (o, s, t.orden_derivada_id)) THEN
            RAISE EXCEPTION 'La tirada ya existe' USING ERRCODE = 'P0A02';
        END IF;
        INSERT INTO negocio.tirada (organizacion_id, sucursal_id, id, captura_id, vehiculo_id, persona_id, planta_id, planta_nombre, nombre_recibe, fecha_captura, recibida_en)
        VALUES (o, s, t.orden_derivada_id, p_captura, veh, t.persona_id, planta, nombre, t.nombre_firmante, k.ocurrida_en, k.recibida_en);
        INSERT INTO negocio.tirada_esperada (organizacion_id, sucursal_id, tirada_id, captura_id)
        SELECT DISTINCT o, s, t.orden_derivada_id, x FROM unnest(t.capturas_incluidas) x;
        PERFORM negocio.armar_manifiesto_sanitarios(o, s, t.orden_derivada_id);
    EXCEPTION WHEN OTHERS THEN
        GET STACKED DIAGNOSTICS estado = RETURNED_SQLSTATE;
        IF estado LIKE '40%' OR estado LIKE '53%' OR estado LIKE '57%' THEN RAISE; END IF;
        PERFORM negocio.marcar_tirada_por_revisar(o, s, p_captura, estado);
    END;
END $$;

CREATE OR REPLACE FUNCTION negocio.armar_manifiesto_sanitarios(o uuid, s uuid, p_tirada uuid) RETURNS uuid
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path = pg_catalog AS $$
DECLARE
    ti negocio.tirada%ROWTYPE; m uuid; v uuid; folio text; total bigint; litros numeric; n_esp integer; capacidad numeric; n_fosas integer; peso_fosa numeric; total_peso numeric;
    emp uuid; ficha_emp uuid; ficha_veh uuid; aut_gen uuid; aut_tra uuid; firma uuid;
BEGIN
    SELECT * INTO ti FROM negocio.tirada WHERE (organizacion_id, sucursal_id, id) = (o, s, p_tirada);
    IF NOT FOUND THEN RAISE EXCEPTION 'Tirada no encontrada' USING ERRCODE = 'P0A03'; END IF;
    PERFORM negocio.bloquear_unidad(o, ti.vehiculo_id);
    SELECT tm.manifiesto_id INTO m FROM negocio.tirada_manifiesto tm WHERE (tm.organizacion_id, tm.sucursal_id, tm.tirada_id) = (o, s, p_tirada);
    IF FOUND THEN RETURN m; END IF;   -- idempotente: ya está armado

    SELECT count(*) INTO n_esp FROM negocio.tirada_esperada e WHERE (e.organizacion_id, e.sucursal_id, e.tirada_id) = (o, s, p_tirada);
    IF n_esp = 0 THEN RAISE EXCEPTION 'Tirada sin carga' USING ERRCODE = 'P0A01'; END IF;
    IF EXISTS (SELECT 1 FROM negocio.tirada_esperada e JOIN negocio.tirada_parada tp
               ON (tp.organizacion_id, tp.sucursal_id, tp.captura_id) = (e.organizacion_id, e.sucursal_id, e.captura_id)
               WHERE (e.organizacion_id, e.sucursal_id, e.tirada_id) = (o, s, p_tirada)) THEN
        RAISE EXCEPTION 'Capturas ya incluidas en otra tirada' USING ERRCODE = 'P0A02';
    END IF;
    IF EXISTS (SELECT 1 FROM negocio.tirada_esperada e JOIN negocio.succion_parada sp
               ON (sp.organizacion_id, sp.sucursal_id, sp.captura_id) = (e.organizacion_id, e.sucursal_id, e.captura_id)
               WHERE (e.organizacion_id, e.sucursal_id, e.tirada_id) = (o, s, p_tirada) AND sp.vehiculo_id <> ti.vehiculo_id) THEN
        RAISE EXCEPTION 'La tirada declara capturas de otra unidad' USING ERRCODE = 'P0A03';
    END IF;
    -- Faltan succiones por llegar: la tirada sigue esperando (se reintenta al llegar cada una).
    IF EXISTS (SELECT 1 FROM negocio.tirada_esperada e
               WHERE (e.organizacion_id, e.sucursal_id, e.tirada_id) = (o, s, p_tirada)
                 AND NOT EXISTS (SELECT 1 FROM negocio.succion_parada sp
                                 WHERE (sp.organizacion_id, sp.sucursal_id, sp.captura_id) = (e.organizacion_id, e.sucursal_id, e.captura_id))) THEN
        RETURN NULL;
    END IF;
    SELECT sum(sp.banos_succionados) FILTER (WHERE NOT sp.es_fosa), count(*) FILTER (WHERE sp.es_fosa), sum(sp.banos_succionados::numeric * sp.litros_por_bano) INTO total, n_fosas, litros
    FROM negocio.tirada_esperada e JOIN negocio.succion_parada sp
      ON (sp.organizacion_id, sp.sucursal_id, sp.captura_id) = (e.organizacion_id, e.sucursal_id, e.captura_id)
    WHERE (e.organizacion_id, e.sucursal_id, e.tirada_id) = (o, s, p_tirada);
    total := coalesce(total, 0); n_fosas := coalesce(n_fosas, 0);
    IF total = 0 AND n_fosas = 0 THEN RAISE EXCEPTION 'Tirada sin servicios' USING ERRCODE = 'P0A01'; END IF;
    -- La tirada siempre sale con la unidad llena: la cantidad del manifiesto es la CAPACIDAD de la unidad (m3), no baños x 16 L.
    -- Los baños x 16 L solo quedan en el detalle por parada y en la barra de carga (estimaciones, no mediciones).
    SELECT cap.valor_m3 INTO capacidad
    FROM negocio.ficha_vehiculo f JOIN catalogo.capacidad_vehiculo cap ON cap.id = f.capacidad_id
    WHERE (f.organizacion_id, f.sucursal_id, f.vehiculo_id) = (o, s, ti.vehiculo_id) ORDER BY f.revision DESC LIMIT 1;
    IF capacidad IS NULL OR capacidad <= 0 THEN RAISE EXCEPTION 'Falta la capacidad de la unidad' USING ERRCODE = 'P0A03'; END IF;
    -- Decisión 0032: cada baño pesa 16 L y el resto de la capacidad se reparte entre las fosas propias (peso en «baños equivalentes»).
    total_peso := total;
    peso_fosa := 0;
    IF n_fosas > 0 THEN
        total_peso := capacidad * 1000 / negocio.litros_por_bano();
        peso_fosa := (total_peso - total) / n_fosas;
        IF peso_fosa <= 0 THEN RAISE EXCEPTION 'Los baños rebasan la capacidad de la unidad' USING ERRCODE = 'P0A03'; END IF;
    END IF;

    -- Generador = transportista = nuestra empresa (domicilio fiscal); el destinatario documental es la planta (recepcion_manifiesto).
    SELECT su.empresa_id INTO emp FROM negocio.sucursal su WHERE (su.organizacion_id, su.id) = (o, s);
    SELECT f.id INTO ficha_veh FROM negocio.ficha_vehiculo f
    WHERE (f.organizacion_id, f.sucursal_id, f.vehiculo_id) = (o, s, ti.vehiculo_id) ORDER BY f.revision DESC LIMIT 1;
    SELECT x.id INTO ficha_emp FROM negocio.ficha_empresa x WHERE (x.organizacion_id, x.empresa_id) = (o, emp) ORDER BY x.revision DESC LIMIT 1;
    IF emp IS NULL OR ficha_veh IS NULL OR ficha_emp IS NULL THEN
        RAISE EXCEPTION 'Falta la ficha de la unidad o de la empresa' USING ERRCODE = 'P0A03';
    END IF;
    SELECT a.id INTO aut_gen FROM negocio.autorizacion_empresa a
    WHERE (a.organizacion_id, a.empresa_id) = (o, emp) AND a.autoridad = 'Medio ambiente' AND a.finalidad = 'Generador'
    ORDER BY a.registrada_en DESC, a.id DESC LIMIT 1;
    SELECT a.id INTO aut_tra FROM negocio.autorizacion_empresa a
    WHERE (a.organizacion_id, a.empresa_id) = (o, emp) AND a.autoridad = 'Medio ambiente' AND a.finalidad = 'Transportista'
    ORDER BY a.registrada_en DESC, a.id DESC LIMIT 1;
    SELECT g.id INTO firma FROM negocio.firma_generador_precargada g
    WHERE (g.organizacion_id, g.sucursal_id, g.empresa_id) = (o, s, emp) ORDER BY g.revision DESC LIMIT 1;

    INSERT INTO negocio.empresa_sucursal VALUES (o, s, emp) ON CONFLICT DO NOTHING;
    INSERT INTO negocio.manifiesto (organizacion_id, sucursal_id, tipo) VALUES (o, s, 'sanitarios') RETURNING id INTO m;
    -- Contador de folio compartido con fosa fija: SIEMPRE el último lock de la transacción.
    folio := negocio.siguiente_folio(o, s, extract(year FROM ti.fecha_captura AT TIME ZONE 'UTC')::integer);   -- año de la tirada (cuando firma quien recibe)
    INSERT INTO negocio.version_manifiesto (organizacion_id, sucursal_id, manifiesto_id, revision, numero_manifiesto, ficha_vehiculo_id, version_plantilla)
    VALUES (o, s, m, 1, folio, ficha_veh, 'sanitarios-1') RETURNING id INTO v;   -- plantilla PROVISIONAL
    INSERT INTO negocio.participante_manifiesto (organizacion_id, sucursal_id, version_id, rol, empresa_id, ficha_empresa_id, autorizacion_id, origen_domicilio)
    VALUES (o, s, v, 'generador', emp, ficha_emp, aut_gen, 'fiscal'),
           (o, s, v, 'transportista', emp, ficha_emp, aut_tra, 'fiscal');
    -- Un renglón resumen: cantidad = capacidad de la unidad. tipo_contenedor y numero_contenedores (= baños) son PROVISIONALES: no están confirmados como campos del manifiesto.
    INSERT INTO negocio.detalle_manifiesto (organizacion_id, sucursal_id, version_id, renglon, descripcion_residuo, tipo_contenedor,
        numero_contenedores, cantidad, unidad_cantidad, naturaleza_cantidad, metodo_cuantificacion, version_metodo)
    VALUES (o, s, v, 1, 'Excreta humana', CASE WHEN n_fosas = 0 THEN 'sanitario' WHEN total = 0 THEN 'fosa' ELSE 'sanitario y fosa' END, (total + n_fosas)::integer, capacidad, 'm3', 'estimada', 'capacidad_unidad_llena', '1');

    -- Volumen estimado por parada (m3): la unidad sale llena, así que se reparte su capacidad según los baños de cada parada
    -- (capacidad x baños / total de baños; ya no x 16 L). Paradas con los MISMOS baños no repiten el mismo número: se emparejan y una sube
    -- 3 % y la otra baja 3 % (el par suma igual); una sobrante queda sin variación. Es estimación por regla, no medición.
    INSERT INTO negocio.manifiesto_parada (organizacion_id, sucursal_id, version_id, renglon, parada_id, captura_id, sitio_id, ficha_sitio_id,
        fecha, banos, latitud, longitud, precision_m, clientes, evidencia_foto_id, volumen_m3, es_fosa)
    WITH base AS (
        SELECT sp.*, CASE WHEN sp.es_fosa THEN peso_fosa ELSE sp.banos_succionados END AS peso, row_number() OVER (ORDER BY sp.fecha_captura, sp.recibida_en, sp.captura_id) AS renglon
        FROM negocio.tirada_esperada e
        JOIN negocio.succion_parada sp ON (sp.organizacion_id, sp.sucursal_id, sp.captura_id) = (e.organizacion_id, e.sucursal_id, e.captura_id)
        WHERE (e.organizacion_id, e.sucursal_id, e.tirada_id) = (o, s, p_tirada)
    ), grupos AS (
        SELECT b.*, row_number() OVER (PARTITION BY b.es_fosa, b.peso ORDER BY b.renglon) AS pos,
               count(*) OVER (PARTITION BY b.es_fosa, b.peso) AS n
        FROM base b
    ), crudo AS (
        SELECT g.*, capacidad * g.peso / total_peso
                    * CASE WHEN g.n > 1 AND g.pos = g.n AND g.n % 2 = 1 THEN 1 WHEN g.n > 1 AND g.pos % 2 = 1 THEN 1.03 WHEN g.n > 1 THEN 0.97 ELSE 1 END AS raw
        FROM grupos g
    ), pisos AS (
        SELECT c.*, floor(c.raw * 1000) AS piso, c.raw * 1000 - floor(c.raw * 1000) AS fraccion FROM crudo c
    ), resto AS (
        -- Cada parada baja a la milésima; lo que falta para llegar a la capacidad se reparte de a una milésima, empezando por la fracción mayor.
        SELECT p.*, (round(capacidad * 1000) - sum(p.piso) OVER ())::integer AS faltan, row_number() OVER (ORDER BY p.fraccion DESC, p.renglon) AS por_fraccion
        FROM pisos p
    )
    SELECT o, s, v, g.renglon, g.parada_id, g.captura_id, g.sitio_id, fs.id,
           g.fecha_captura, g.banos_succionados, ev.latitud, ev.longitud, ev.precision_metros,
           negocio.clientes_parada_en(o, s, g.parada_id, g.fecha_captura), ev.id,
           (g.piso + CASE WHEN g.por_fraccion <= g.faltan THEN 1 ELSE 0 END) / 1000.0, g.es_fosa
    FROM resto g
    LEFT JOIN LATERAL (SELECT x.id FROM negocio.ficha_sitio x WHERE (x.organizacion_id, x.sucursal_id, x.sitio_id) = (g.organizacion_id, g.sucursal_id, g.sitio_id)
        ORDER BY x.revision DESC LIMIT 1) fs ON true
    LEFT JOIN LATERAL (SELECT x.id, x.latitud, x.longitud, x.precision_metros FROM negocio.evidencia_captura x
        WHERE (x.organizacion_id, x.sucursal_id, x.captura_id) = (g.organizacion_id, g.sucursal_id, g.captura_id) AND x.rol = 'FOTO'
        ORDER BY x.id LIMIT 1) ev ON true;
    INSERT INTO negocio.recepcion_manifiesto (organizacion_id, sucursal_id, version_id, planta_id, planta_nombre, nombre_recibe, captura_id, firma_generador_id)
    VALUES (o, s, v, ti.planta_id, ti.planta_nombre, ti.nombre_recibe, ti.captura_id, firma);
    -- Asignación definitiva: de aquí en adelante estas succiones ya no cuentan en la carga de la unidad.
    INSERT INTO negocio.tirada_parada (organizacion_id, sucursal_id, tirada_id, captura_id, renglon)
    SELECT o, s, p_tirada, mp.captura_id, mp.renglon FROM negocio.manifiesto_parada mp WHERE (mp.organizacion_id, mp.sucursal_id, mp.version_id) = (o, s, v);
    INSERT INTO negocio.tirada_manifiesto (organizacion_id, sucursal_id, tirada_id, manifiesto_id) VALUES (o, s, p_tirada, m);
    RETURN m;
END $$;
