-- Fosa fija (decisión 18-nov-2026, opción B): por fosa solo se pide la foto; las firmas se obtienen al descargar y la firma del cliente
-- (generador autorizado) deja de ser requisito. El manifiesto de la orden se arma cuando llegan el servicio del operador (foto y su firma)
-- y la recepción en la planta, también para clientes autorizados. Si la firma del cliente llega, se conserva como captura.
CREATE OR REPLACE FUNCTION negocio.armar_orden_fosa_fija(o uuid, s uuid, p_persona uuid, p_jornada uuid, p_orden uuid, p_instante timestamptz)
RETURNS void LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE r record; serv boolean; planta boolean; m uuid;
BEGIN
    IF EXISTS (SELECT 1 FROM negocio.orden_manifiesto om WHERE (om.organizacion_id,om.sucursal_id,om.orden_derivada_id)=(o,s,p_orden)) THEN RETURN; END IF;
    SELECT * INTO r FROM negocio.resolver_orden_derivada(o,s,p_persona,p_jornada,p_orden,p_instante);
    IF NOT FOUND THEN RETURN; END IF;
    SELECT coalesce(bool_or(t.tipo='SERVICIO_OPERADOR'),false), coalesce(bool_or(t.tipo='RECEPCION_PLANTA'),false)
    INTO serv, planta
    FROM negocio.captura_trabajo t WHERE (t.organizacion_id,t.sucursal_id,t.orden_derivada_id)=(o,s,p_orden);
    IF serv AND planta THEN
        SELECT g.manifiesto INTO m FROM negocio.armar_manifiesto_fosa_fija(o,s,r.cliente_id,r.vehiculo_id,true) g;
        INSERT INTO negocio.orden_manifiesto(organizacion_id,sucursal_id,orden_derivada_id,jornada_id,parada_id,cliente_id,vuelta,vehiculo_id,manifiesto_id)
        VALUES(o,s,p_orden,p_jornada,r.parada_id,r.cliente_id,r.vuelta,r.vehiculo_id,m);
    END IF;
END $$;
REVOKE ALL ON FUNCTION negocio.armar_orden_fosa_fija(uuid, uuid, uuid, uuid, uuid, timestamptz) FROM PUBLIC;

CREATE OR REPLACE FUNCTION negocio.tras_captura_trabajo() RETURNS trigger
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE r record; instante timestamptz;
BEGIN
    IF NEW.tipo IN ('SERVICIO_SANITARIOS','TIRADA_SANITARIOS') THEN
        PERFORM negocio.procesar_captura_sanitarios(NEW.organizacion_id, NEW.sucursal_id, NEW.captura_id);
        RETURN NEW;
    END IF;
    SELECT k.ocurrida_en INTO instante FROM negocio.captura k WHERE (k.organizacion_id,k.sucursal_id,k.id)=(NEW.organizacion_id,NEW.sucursal_id,NEW.captura_id);
    -- Un problema por parada de una jornada de sanitarios se acepta con su parada: no arma ni cierra nada.
    IF NEW.tipo = 'PROBLEMA' AND EXISTS (SELECT 1 FROM negocio.resolver_parada_derivada(NEW.organizacion_id, NEW.sucursal_id, NEW.persona_id, NEW.jornada_id, NEW.orden_derivada_id, instante)) THEN
        RETURN NEW;
    END IF;
    PERFORM pg_advisory_xact_lock(hashtextextended('orden:'||NEW.organizacion_id::text||':'||NEW.orden_derivada_id::text,0));
    SELECT * INTO r FROM negocio.resolver_orden_derivada(NEW.organizacion_id,NEW.sucursal_id,NEW.persona_id,NEW.jornada_id,NEW.orden_derivada_id,instante);
    IF NOT FOUND THEN
        INSERT INTO negocio.captura_por_revisar(organizacion_id,sucursal_id,captura_id,motivo) VALUES(NEW.organizacion_id,NEW.sucursal_id,NEW.captura_id,'orden_no_resuelta');
        RETURN NEW;
    END IF;
    IF NEW.requiere_firma_cliente IS DISTINCT FROM r.autorizado THEN
        INSERT INTO negocio.captura_por_revisar(organizacion_id,sucursal_id,captura_id,motivo) VALUES(NEW.organizacion_id,NEW.sucursal_id,NEW.captura_id,'regla_firma_distinta');
        RETURN NEW;
    END IF;
    IF NEW.tipo <> 'PROBLEMA' THEN
        PERFORM negocio.armar_orden_fosa_fija(NEW.organizacion_id, NEW.sucursal_id, NEW.persona_id, NEW.jornada_id, NEW.orden_derivada_id, instante);
    END IF;
    RETURN NEW;
END $$;
REVOKE ALL ON FUNCTION negocio.tras_captura_trabajo() FROM PUBLIC;

-- Órdenes que ya tenían servicio y recepción pero se quedaron sin manifiesto por exigir la firma del cliente: se arman ahora, en orden de llegada.
DO $$
DECLARE x record; inst timestamptz;
BEGIN
    FOR x IN
        SELECT DISTINCT ON (t.organizacion_id, t.sucursal_id, t.orden_derivada_id) t.organizacion_id, t.sucursal_id, t.persona_id, t.jornada_id, t.orden_derivada_id, t.captura_id
        FROM negocio.captura_trabajo t
        WHERE t.tipo IN ('SERVICIO_OPERADOR','RECEPCION_PLANTA')
          AND NOT EXISTS (SELECT 1 FROM negocio.orden_manifiesto om WHERE (om.organizacion_id,om.sucursal_id,om.orden_derivada_id)=(t.organizacion_id,t.sucursal_id,t.orden_derivada_id))
          AND NOT EXISTS (SELECT 1 FROM negocio.captura_por_revisar cr WHERE (cr.organizacion_id,cr.sucursal_id,cr.captura_id)=(t.organizacion_id,t.sucursal_id,t.captura_id))
        ORDER BY t.organizacion_id, t.sucursal_id, t.orden_derivada_id, t.captura_id
    LOOP
        SELECT k.ocurrida_en INTO inst FROM negocio.captura k WHERE (k.organizacion_id,k.sucursal_id,k.id)=(x.organizacion_id,x.sucursal_id,x.captura_id);
        PERFORM negocio.armar_orden_fosa_fija(x.organizacion_id, x.sucursal_id, x.persona_id, x.jornada_id, x.orden_derivada_id, inst);
    END LOOP;
END $$;
