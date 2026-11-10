-- Fosa fija (A6): una ORDEN = un MANIFIESTO = una vuelta completa (usuario, 10-nov-2026). El servidor arma el
-- manifiesto cuando llegan los pasos de la orden: (1) foto y firma del operador, (2) firma y nombre del cliente
-- (solo si es cliente autorizado/generador), (3) firma y nombre en la planta. El operador NUNCA ve manifiestos.
-- - Se arma lo que ya definía 0015 (generador, transportista, capacidad estimada) y se asigna el FOLIO `NNN/AAAA`
--   consecutivo sin huecos al completarse la orden (usuario, 10-nov-2026). NO registra destinatario/descarga
--   (catálogo de plantas y relación PG-08 pendientes; la planta capturada queda en captura_trabajo) y no se envía por correo.
-- - Una captura NUNCA se rechaza por no resolver su orden (el plan pudo corregirse después de descargarlo):
--   se guarda el hecho, se marca para revisión y no se arma manifiesto. La revisión es de A7.
-- - Solo fosa fija. Sanitarios y fosas propias rentadas siguen con otra lógica (PG-24).

-- Versión interna sin permisos de panel: solo la invoca el disparador de recepción. Sin GRANT para la aplicación.
CREATE FUNCTION negocio.armar_manifiesto_fosa_fija(o uuid, s uuid, p_cliente uuid, p_vehiculo uuid, p_con_folio boolean DEFAULT false)
RETURNS TABLE (manifiesto uuid, generador_es_cliente boolean)
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path = pg_catalog AS $$
DECLARE
    folio text; emp_cli uuid; emp_nuestra uuid; aut_cli uuid; aut_gen uuid; aut_tra uuid; emp_gen uuid;
    ficha_veh uuid; capacidad numeric; ficha_gen uuid; ficha_tra uuid; m uuid; v uuid;
BEGIN
    SELECT c.empresa_id INTO emp_cli FROM negocio.cliente c WHERE c.organizacion_id = o AND c.sucursal_id = s AND c.id = p_cliente;
    IF emp_cli IS NULL THEN RAISE EXCEPTION 'Cliente no encontrado' USING ERRCODE = 'P0002'; END IF;
    SELECT su.empresa_id INTO emp_nuestra FROM negocio.sucursal su WHERE su.organizacion_id = o AND su.id = s;
    SELECT f.id, cap.valor_m3 INTO ficha_veh, capacidad
    FROM negocio.ficha_vehiculo f JOIN catalogo.capacidad_vehiculo cap ON cap.id = f.capacidad_id
    WHERE f.organizacion_id = o AND f.sucursal_id = s AND f.vehiculo_id = p_vehiculo
    ORDER BY f.revision DESC LIMIT 1;
    IF ficha_veh IS NULL THEN RAISE EXCEPTION 'Unidad sin ficha ni capacidad' USING ERRCODE = 'P0002'; END IF;

    SELECT a.id INTO aut_cli FROM negocio.autorizacion_empresa a
    WHERE a.organizacion_id = o AND a.empresa_id = emp_cli AND a.autoridad = 'Medio ambiente' AND a.finalidad = 'Generador'
    ORDER BY a.registrada_en DESC, a.id DESC LIMIT 1;
    IF aut_cli IS NOT NULL THEN
        emp_gen := emp_cli; aut_gen := aut_cli;
    ELSE
        emp_gen := emp_nuestra;
        SELECT a.id INTO aut_gen FROM negocio.autorizacion_empresa a
        WHERE a.organizacion_id = o AND a.empresa_id = emp_nuestra AND a.autoridad = 'Medio ambiente' AND a.finalidad = 'Generador'
        ORDER BY a.registrada_en DESC, a.id DESC LIMIT 1;
    END IF;
    SELECT a.id INTO aut_tra FROM negocio.autorizacion_empresa a
    WHERE a.organizacion_id = o AND a.empresa_id = emp_nuestra AND a.autoridad = 'Medio ambiente' AND a.finalidad = 'Transportista'
    ORDER BY a.registrada_en DESC, a.id DESC LIMIT 1;
    SELECT x.id INTO ficha_gen FROM negocio.ficha_empresa x WHERE x.organizacion_id = o AND x.empresa_id = emp_gen ORDER BY x.revision DESC LIMIT 1;
    SELECT x.id INTO ficha_tra FROM negocio.ficha_empresa x WHERE x.organizacion_id = o AND x.empresa_id = emp_nuestra ORDER BY x.revision DESC LIMIT 1;
    IF ficha_gen IS NULL OR ficha_tra IS NULL THEN
        RAISE EXCEPTION 'Falta la ficha de datos de la empresa del generador o del transportista' USING ERRCODE = '22023';
    END IF;

    -- Nuestra empresa participa en la sucursal como generador o transportista.
    INSERT INTO negocio.empresa_sucursal VALUES (o, s, emp_nuestra) ON CONFLICT DO NOTHING;
    INSERT INTO negocio.manifiesto (organizacion_id, sucursal_id, cliente_id) VALUES (o, s, p_cliente) RETURNING id INTO m;
    IF p_con_folio THEN folio := negocio.siguiente_folio(o, s, extract(year FROM now() AT TIME ZONE 'UTC')::integer); END IF;
    INSERT INTO negocio.version_manifiesto (organizacion_id, sucursal_id, manifiesto_id, revision, numero_manifiesto, ficha_vehiculo_id, version_plantilla)
    VALUES (o, s, m, 1, folio, ficha_veh, 'fosa-fija-1') RETURNING id INTO v;
    INSERT INTO negocio.participante_manifiesto (organizacion_id, sucursal_id, version_id, rol, empresa_id, ficha_empresa_id, autorizacion_id, origen_domicilio)
    VALUES (o, s, v, 'generador', emp_gen, ficha_gen, aut_gen, 'fiscal'),
           (o, s, v, 'transportista', emp_nuestra, ficha_tra, aut_tra, 'fiscal');
    INSERT INTO negocio.detalle_manifiesto (organizacion_id, sucursal_id, version_id, renglon, descripcion_residuo, tipo_contenedor,
        capacidad_contenedor, unidad_capacidad, numero_contenedores, cantidad, unidad_cantidad, naturaleza_cantidad, metodo_cuantificacion, version_metodo)
    VALUES (o, s, v, 1, 'Agua residual de fosa fija', 'Fosa del cliente',
        capacidad, 'm3', 1, capacidad, 'm3', 'estimada', 'capacidad_unidad_llena', '1');
    RETURN QUERY SELECT m, aut_cli IS NOT NULL;
END $$;
REVOKE ALL ON FUNCTION negocio.armar_manifiesto_fosa_fija(uuid, uuid, uuid, uuid, boolean) FROM PUBLIC;

-- La función pública conserva su comportamiento y permisos; ahora delega en la interna.
CREATE OR REPLACE FUNCTION negocio.generar_manifiesto_fosa_fija(o uuid, s uuid, p_cliente uuid, p_vehiculo uuid)
RETURNS TABLE (manifiesto uuid, generador_es_cliente boolean)
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path = pg_catalog AS $$
DECLARE
    emp_cli uuid; emp_nuestra uuid; aut_cli uuid; aut_gen uuid; aut_tra uuid; emp_gen uuid;
    ficha_veh uuid; capacidad numeric; ficha_gen uuid; ficha_tra uuid; m uuid; v uuid;
BEGIN
    IF NOT coalesce(seguridad.puede(o, s, 'datos.escribir'), false) THEN
        RAISE EXCEPTION 'Sin permiso de escritura en la sucursal' USING ERRCODE = '42501';
    END IF;
    RETURN QUERY SELECT * FROM negocio.armar_manifiesto_fosa_fija(o, s, p_cliente, p_vehiculo);
END $$;

-- Folio `NNN/AAAA`, consecutivo SIN HUECOS (usuario, 10-nov-2026): se asigna al armar el manifiesto, dentro de la misma
-- transacción, con la fila del contador bloqueada; si la transacción se revierte, el número no se consume.
-- Alcance PROVISIONAL: por organización + sucursal + año (UTC); pendiente confirmar alcance y zona horaria (PG-18).
CREATE TABLE negocio.contador_folio (
    organizacion_id uuid NOT NULL, sucursal_id uuid NOT NULL,
    anio integer NOT NULL CHECK (anio BETWEEN 2000 AND 9999), ultimo integer NOT NULL DEFAULT 0 CHECK (ultimo >= 0),
    PRIMARY KEY (organizacion_id, sucursal_id, anio),
    FOREIGN KEY (organizacion_id, sucursal_id) REFERENCES negocio.sucursal ON DELETE RESTRICT
);
ALTER TABLE negocio.contador_folio ENABLE ROW LEVEL SECURITY;  -- sin política ni GRANT: solo la función definer lo toca
CREATE FUNCTION negocio.siguiente_folio(o uuid, s uuid, p_anio integer) RETURNS text
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE n integer;
BEGIN
    INSERT INTO negocio.contador_folio(organizacion_id, sucursal_id, anio) VALUES (o, s, p_anio) ON CONFLICT DO NOTHING;
    UPDATE negocio.contador_folio SET ultimo = ultimo + 1 WHERE (organizacion_id, sucursal_id, anio) = (o, s, p_anio) RETURNING ultimo INTO n;
    RETURN lpad(n::text, 3, '0') || '/' || p_anio::text;
END $$;
REVOKE ALL ON FUNCTION negocio.siguiente_folio(uuid, uuid, integer) FROM PUBLIC;

CREATE TABLE negocio.orden_manifiesto (
    organizacion_id uuid NOT NULL, sucursal_id uuid NOT NULL, orden_derivada_id uuid NOT NULL,
    jornada_id uuid NOT NULL, parada_id uuid NOT NULL, cliente_id uuid NOT NULL,
    vuelta integer NOT NULL CHECK (vuelta > 0), vehiculo_id uuid NOT NULL,
    manifiesto_id uuid NOT NULL, generada_en timestamptz NOT NULL DEFAULT clock_timestamp(),
    PRIMARY KEY (organizacion_id, sucursal_id, orden_derivada_id),
    UNIQUE (organizacion_id, sucursal_id, manifiesto_id),
    FOREIGN KEY (organizacion_id, sucursal_id, manifiesto_id) REFERENCES negocio.manifiesto ON DELETE RESTRICT
);
-- Capturas recibidas que no se pudieron ligar a una orden vigente del operador; esperan revisión (A7).
CREATE TABLE negocio.captura_por_revisar (
    organizacion_id uuid NOT NULL, sucursal_id uuid NOT NULL, captura_id uuid NOT NULL,
    motivo text NOT NULL CHECK (motivo IN ('orden_no_resuelta','regla_firma_distinta')),
    registrada_en timestamptz NOT NULL DEFAULT clock_timestamp(),
    PRIMARY KEY (organizacion_id, sucursal_id, captura_id),
    FOREIGN KEY (organizacion_id, sucursal_id, captura_id) REFERENCES negocio.captura ON DELETE RESTRICT
);
DO $$
DECLARE t text;
BEGIN
    FOREACH t IN ARRAY ARRAY['orden_manifiesto','captura_por_revisar'] LOOP
        EXECUTE format('ALTER TABLE negocio.%I ENABLE ROW LEVEL SECURITY', t);
        EXECUTE format('CREATE POLICY contexto ON negocio.%I TO manifiestos_app USING (seguridad.puede(organizacion_id, sucursal_id, ''datos.leer''))', t);
        EXECUTE format('GRANT SELECT ON negocio.%I TO manifiestos_app', t);
        EXECUTE format('CREATE TRIGGER conservar_historia BEFORE UPDATE OR DELETE ON negocio.%I FOR EACH ROW EXECUTE FUNCTION infra.inmutable()', t);
    END LOOP;
END $$;

-- Ubica una orden derivada (jornada+parada+cliente+vuelta, igual que seguridad.trabajo_operador) dentro del trabajo de
-- una jornada cuya unidad operó la persona. Acepta cualquier estado de la jornada: es un hecho ya ocurrido.
CREATE FUNCTION negocio.resolver_orden_derivada(o uuid, s uuid, p_persona uuid, p_jornada uuid, p_orden uuid)
RETURNS TABLE(parada_id uuid, cliente_id uuid, vuelta integer, vehiculo_id uuid, autorizado boolean)
LANGUAGE sql STABLE SECURITY DEFINER SET search_path=pg_catalog AS $$
    WITH jo AS (
        SELECT j.id AS jornada_id, j.ruta_id, j.fecha, rv.vehiculo_id
        FROM negocio.jornada j
        JOIN LATERAL (SELECT x.vehiculo_id FROM negocio.ruta_version x
            WHERE (x.organizacion_id,x.sucursal_id,x.ruta_id)=(j.organizacion_id,j.sucursal_id,j.ruta_id)
            ORDER BY x.revision DESC LIMIT 1) rv ON true
        WHERE j.organizacion_id=o AND j.sucursal_id=s AND j.id=p_jornada
          AND EXISTS (SELECT 1 FROM negocio.asignacion_unidad a
                      WHERE (a.organizacion_id,a.sucursal_id,a.vehiculo_id)=(o,s,rv.vehiculo_id) AND a.persona_id=p_persona)
    ), cand AS (
        SELECT jo.jornada_id, pa.id AS parada_id, pc.cliente_id, coalesce(pc.fosas_planeadas,0) AS fosas, jo.vehiculo_id
        FROM jo JOIN negocio.parada pa ON (pa.organizacion_id,pa.sucursal_id,pa.ruta_id)=(o,s,jo.ruta_id)
        JOIN LATERAL (SELECT x.id FROM negocio.parada_version x
            WHERE (x.organizacion_id,x.sucursal_id,x.parada_id)=(pa.organizacion_id,pa.sucursal_id,pa.id)
            ORDER BY x.revision DESC LIMIT 1) pv ON true
        JOIN negocio.parada_cliente pc ON (pc.organizacion_id,pc.sucursal_id,pc.version_id)=(o,s,pv.id)
        UNION ALL
        SELECT jo.jornada_id, pe.id, pc.cliente_id, coalesce(pc.fosas_planeadas,0), jo.vehiculo_id
        FROM jo JOIN negocio.parada_extraordinaria pe ON (pe.organizacion_id,pe.sucursal_id,pe.ruta_id)=(o,s,jo.ruta_id) AND pe.fecha=jo.fecha
        JOIN negocio.parada_extraordinaria_cliente pc ON (pc.organizacion_id,pc.sucursal_id,pc.parada_extraordinaria_id)=(o,s,pe.id)
    )
    SELECT cand.parada_id, cand.cliente_id, n, cand.vehiculo_id,
           EXISTS (SELECT 1 FROM negocio.cliente cl JOIN negocio.autorizacion_empresa au
                   ON (au.organizacion_id,au.empresa_id)=(cl.organizacion_id,cl.empresa_id)
                   WHERE (cl.organizacion_id,cl.sucursal_id,cl.id)=(o,s,cand.cliente_id)
                     AND au.autoridad='Medio ambiente' AND au.finalidad='Generador')
    FROM cand CROSS JOIN LATERAL generate_series(1,cand.fosas) AS n
    WHERE md5(cand.jornada_id::text||':'||cand.parada_id::text||':'||cand.cliente_id::text||':'||n)::uuid = p_orden
    LIMIT 1
$$;
REVOKE ALL ON FUNCTION negocio.resolver_orden_derivada(uuid,uuid,uuid,uuid,uuid) FROM PUBLIC;

-- Tras guardar cada captura: liga con su orden y, si ya llegaron todos los pasos, arma el manifiesto UNA sola vez.
CREATE FUNCTION negocio.tras_captura_trabajo() RETURNS trigger
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE r record; serv boolean; cli boolean; planta boolean; m uuid;
BEGIN
    PERFORM pg_advisory_xact_lock(hashtextextended('orden:'||NEW.organizacion_id::text||':'||NEW.orden_derivada_id::text,0));
    SELECT * INTO r FROM negocio.resolver_orden_derivada(NEW.organizacion_id,NEW.sucursal_id,NEW.persona_id,NEW.jornada_id,NEW.orden_derivada_id);
    IF NOT FOUND THEN
        INSERT INTO negocio.captura_por_revisar(organizacion_id,sucursal_id,captura_id,motivo)
        VALUES(NEW.organizacion_id,NEW.sucursal_id,NEW.captura_id,'orden_no_resuelta');
        RETURN NEW;
    END IF;
    IF NEW.requiere_firma_cliente IS DISTINCT FROM r.autorizado THEN
        INSERT INTO negocio.captura_por_revisar(organizacion_id,sucursal_id,captura_id,motivo)
        VALUES(NEW.organizacion_id,NEW.sucursal_id,NEW.captura_id,'regla_firma_distinta');
        RETURN NEW;
    END IF;
    IF NEW.tipo='PROBLEMA' OR EXISTS (SELECT 1 FROM negocio.orden_manifiesto om
        WHERE (om.organizacion_id,om.sucursal_id,om.orden_derivada_id)=(NEW.organizacion_id,NEW.sucursal_id,NEW.orden_derivada_id)) THEN
        RETURN NEW;
    END IF;
    SELECT coalesce(bool_or(t.tipo='SERVICIO_OPERADOR'),false), coalesce(bool_or(t.tipo='FIRMA_CLIENTE'),false), coalesce(bool_or(t.tipo='RECEPCION_PLANTA'),false)
    INTO serv, cli, planta
    FROM negocio.captura_trabajo t
    WHERE (t.organizacion_id,t.sucursal_id,t.orden_derivada_id)=(NEW.organizacion_id,NEW.sucursal_id,NEW.orden_derivada_id);
    IF serv AND planta AND (cli OR NOT r.autorizado) THEN
        SELECT g.manifiesto INTO m FROM negocio.armar_manifiesto_fosa_fija(NEW.organizacion_id,NEW.sucursal_id,r.cliente_id,r.vehiculo_id,true) g;
        INSERT INTO negocio.orden_manifiesto(organizacion_id,sucursal_id,orden_derivada_id,jornada_id,parada_id,cliente_id,vuelta,vehiculo_id,manifiesto_id)
        VALUES(NEW.organizacion_id,NEW.sucursal_id,NEW.orden_derivada_id,NEW.jornada_id,r.parada_id,r.cliente_id,r.vuelta,r.vehiculo_id,m);
    END IF;
    RETURN NEW;
END $$;
REVOKE ALL ON FUNCTION negocio.tras_captura_trabajo() FROM PUBLIC;
CREATE TRIGGER armar_manifiesto AFTER INSERT ON negocio.captura_trabajo FOR EACH ROW EXECUTE FUNCTION negocio.tras_captura_trabajo();
