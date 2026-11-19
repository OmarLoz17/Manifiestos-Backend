-- Cambios del 12-nov-2026 (backend):
--   * El manifiesto de SANITARIOS lleva como cantidad total la CAPACIDAD de la unidad de la tirada en m3 (siempre se tira llena),
--     con método 'capacidad_unidad_llena' (naturaleza 'estimada', igual que fosa fija). La estimación por baños (x 16 L) se
--     conserva solo en el detalle por parada (manifiesto_parada.banos) y en la barra de carga de la unidad.
--   * Carga por tirar de la unidad en FOSAS: órdenes (vueltas) con captura SERVICIO_OPERADOR y sin RECEPCION_PLANTA. Cada orden llena
--     la unidad, así que m3 = órdenes x capacidad (estimación por regla, no medición) y el porcentaje = órdenes x 100.
--   Solo CREATE OR REPLACE / objetos nuevos: no se tocan las migraciones anteriores.

CREATE OR REPLACE FUNCTION negocio.armar_manifiesto_sanitarios(o uuid, s uuid, p_tirada uuid) RETURNS uuid
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path = pg_catalog AS $$
DECLARE
    ti negocio.tirada%ROWTYPE; m uuid; v uuid; folio text; total bigint; litros numeric; n_esp integer; capacidad numeric;
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
    SELECT sum(sp.banos_succionados), sum(sp.banos_succionados::numeric * sp.litros_por_bano) INTO total, litros
    FROM negocio.tirada_esperada e JOIN negocio.succion_parada sp
      ON (sp.organizacion_id, sp.sucursal_id, sp.captura_id) = (e.organizacion_id, e.sucursal_id, e.captura_id)
    WHERE (e.organizacion_id, e.sucursal_id, e.tirada_id) = (o, s, p_tirada);
    IF coalesce(total, 0) = 0 THEN RAISE EXCEPTION 'Tirada sin baños' USING ERRCODE = 'P0A01'; END IF;
    -- La tirada siempre sale con la unidad llena: la cantidad del manifiesto es la CAPACIDAD de la unidad (m3), no baños x 16 L.
    -- Los baños x 16 L solo quedan en el detalle por parada y en la barra de carga (estimaciones, no mediciones).
    SELECT cap.valor_m3 INTO capacidad
    FROM negocio.ficha_vehiculo f JOIN catalogo.capacidad_vehiculo cap ON cap.id = f.capacidad_id
    WHERE (f.organizacion_id, f.sucursal_id, f.vehiculo_id) = (o, s, ti.vehiculo_id) ORDER BY f.revision DESC LIMIT 1;
    IF capacidad IS NULL OR capacidad <= 0 THEN RAISE EXCEPTION 'Falta la capacidad de la unidad' USING ERRCODE = 'P0A03'; END IF;

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
    VALUES (o, s, v, 1, 'Agua residual de sanitarios portátiles', 'sanitario', total, capacidad, 'm3', 'estimada', 'capacidad_unidad_llena', '1');

    INSERT INTO negocio.manifiesto_parada (organizacion_id, sucursal_id, version_id, renglon, parada_id, captura_id, sitio_id, ficha_sitio_id,
        fecha, banos, latitud, longitud, precision_m, clientes, evidencia_foto_id)
    SELECT o, s, v, row_number() OVER (ORDER BY sp.fecha_captura, sp.recibida_en, sp.captura_id), sp.parada_id, sp.captura_id, sp.sitio_id, fs.id,
           sp.fecha_captura, sp.banos_succionados, ev.latitud, ev.longitud, ev.precision_metros,
           negocio.clientes_parada_en(o, s, sp.parada_id, sp.fecha_captura), ev.id
    FROM negocio.tirada_esperada e
    JOIN negocio.succion_parada sp ON (sp.organizacion_id, sp.sucursal_id, sp.captura_id) = (e.organizacion_id, e.sucursal_id, e.captura_id)
    LEFT JOIN LATERAL (SELECT x.id FROM negocio.ficha_sitio x WHERE (x.organizacion_id, x.sucursal_id, x.sitio_id) = (sp.organizacion_id, sp.sucursal_id, sp.sitio_id)
        ORDER BY x.revision DESC LIMIT 1) fs ON true
    LEFT JOIN LATERAL (SELECT x.id, x.latitud, x.longitud, x.precision_metros FROM negocio.evidencia_captura x
        WHERE (x.organizacion_id, x.sucursal_id, x.captura_id) = (sp.organizacion_id, sp.sucursal_id, sp.captura_id) AND x.rol = 'FOTO'
        ORDER BY x.id LIMIT 1) ev ON true
    WHERE (e.organizacion_id, e.sucursal_id, e.tirada_id) = (o, s, p_tirada);
    INSERT INTO negocio.recepcion_manifiesto (organizacion_id, sucursal_id, version_id, planta_id, planta_nombre, nombre_recibe, captura_id, firma_generador_id)
    VALUES (o, s, v, ti.planta_id, ti.planta_nombre, ti.nombre_recibe, ti.captura_id, firma);
    -- Asignación definitiva: de aquí en adelante estas succiones ya no cuentan en la carga de la unidad.
    INSERT INTO negocio.tirada_parada (organizacion_id, sucursal_id, tirada_id, captura_id, renglon)
    SELECT o, s, p_tirada, mp.captura_id, mp.renglon FROM negocio.manifiesto_parada mp WHERE (mp.organizacion_id, mp.sucursal_id, mp.version_id) = (o, s, v);
    INSERT INTO negocio.tirada_manifiesto (organizacion_id, sucursal_id, tirada_id, manifiesto_id) VALUES (o, s, p_tirada, m);
    RETURN m;
END $$;
REVOKE ALL ON FUNCTION negocio.armar_manifiesto_sanitarios(uuid, uuid, uuid) FROM PUBLIC;


-- ---------------------------------------------------------------------------------------------------------------------
-- Carga por tirar de una unidad de FOSAS. Derivada, no guardada. Una orden derivada (jornada+parada+cliente+vuelta) está por tirar
-- si la unidad la succionó (captura SERVICIO_OPERADOR resuelta, no enviada a revisión) y todavía no llega su RECEPCION_PLANTA.
-- La unidad de la orden es la de la ruta de su jornada (última versión de la ruta, igual que resolver_orden_derivada).
-- Puede venir de días anteriores. fecha del desglose = fecha de la jornada.
-- ---------------------------------------------------------------------------------------------------------------------
CREATE FUNCTION negocio.carga_fosas_interna(o uuid, s uuid, p_vehiculo uuid)
RETURNS TABLE (ordenes integer, m3 numeric, capacidad_m3 numeric, porcentaje numeric, ultima_descarga_en timestamptz, desglose jsonb)
LANGUAGE sql STABLE SECURITY DEFINER SET search_path = pg_catalog AS $$
    WITH v AS (
        SELECT cap.valor_m3
        FROM negocio.vehiculo veh
        LEFT JOIN LATERAL (SELECT x.capacidad_id FROM negocio.ficha_vehiculo x
            WHERE (x.organizacion_id, x.sucursal_id, x.vehiculo_id) = (veh.organizacion_id, veh.sucursal_id, veh.id)
            ORDER BY x.revision DESC LIMIT 1) f ON true
        LEFT JOIN catalogo.capacidad_vehiculo cap ON cap.id = f.capacidad_id
        WHERE (veh.organizacion_id, veh.sucursal_id, veh.id) = (o, s, p_vehiculo)
    ), de_unidad AS (
        -- Capturas de órdenes de fosas (no de sanitarios) cuya jornada es de una ruta de esta unidad.
        SELECT ct.captura_id, ct.tipo, ct.orden_derivada_id, j.fecha, k.ocurrida_en
        FROM negocio.captura_trabajo ct
        JOIN negocio.captura k ON (k.organizacion_id, k.sucursal_id, k.id) = (ct.organizacion_id, ct.sucursal_id, ct.captura_id)
        JOIN negocio.jornada j ON (j.organizacion_id, j.sucursal_id, j.id) = (ct.organizacion_id, ct.sucursal_id, ct.jornada_id)
        JOIN LATERAL (SELECT x.vehiculo_id FROM negocio.ruta_version x
            WHERE (x.organizacion_id, x.sucursal_id, x.ruta_id) = (j.organizacion_id, j.sucursal_id, j.ruta_id)
            ORDER BY x.revision DESC LIMIT 1) rv ON rv.vehiculo_id = p_vehiculo
        WHERE ct.organizacion_id = o AND ct.sucursal_id = s AND ct.tipo IN ('SERVICIO_OPERADOR', 'RECEPCION_PLANTA')
          AND NOT EXISTS (SELECT 1 FROM negocio.captura_por_revisar r
                          WHERE (r.organizacion_id, r.sucursal_id, r.captura_id) = (ct.organizacion_id, ct.sucursal_id, ct.captura_id))
    ), pend AS (
        SELECT d.orden_derivada_id, min(d.fecha) AS fecha
        FROM de_unidad d
        WHERE d.tipo = 'SERVICIO_OPERADOR'
          AND NOT EXISTS (SELECT 1 FROM de_unidad r WHERE r.tipo = 'RECEPCION_PLANTA' AND r.orden_derivada_id = d.orden_derivada_id)
        GROUP BY d.orden_derivada_id
    ), dias AS (
        SELECT fecha, count(*)::integer AS ordenes FROM pend GROUP BY fecha
    ), c AS (
        SELECT (SELECT count(*) FROM pend)::integer AS ordenes,
               (SELECT max(d.ocurrida_en) FROM de_unidad d WHERE d.tipo = 'RECEPCION_PLANTA') AS ultima,
               coalesce((SELECT jsonb_agg(jsonb_build_object('fecha', d.fecha, 'ordenes', d.ordenes) ORDER BY d.fecha) FROM dias d), '[]'::jsonb) AS desglose
    )
    SELECT c.ordenes, round(c.ordenes * v.valor_m3, 3), v.valor_m3, (c.ordenes * 100)::numeric, c.ultima, c.desglose
    FROM v CROSS JOIN c
$$;
REVOKE ALL ON FUNCTION negocio.carga_fosas_interna(uuid, uuid, uuid) FROM PUBLIC;

-- Para el panel: exige datos.leer en la sucursal y que el acceso informado sea el del contexto.
CREATE FUNCTION seguridad.carga_fosas_web(o uuid, a uuid, s uuid, p_vehiculo uuid)
RETURNS TABLE (ordenes integer, m3 numeric, capacidad_m3 numeric, porcentaje numeric, ultima_descarga_en timestamptz, desglose jsonb)
LANGUAGE sql STABLE SECURITY DEFINER SET search_path = pg_catalog AS $$
    SELECT c.* FROM negocio.carga_fosas_interna(o, s, p_vehiculo) c
    WHERE a = seguridad.contexto_acceso() AND coalesce(seguridad.puede(o, s, 'datos.leer'), false)
$$;
REVOKE ALL ON FUNCTION seguridad.carga_fosas_web(uuid, uuid, uuid, uuid) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION seguridad.carga_fosas_web(uuid, uuid, uuid, uuid) TO manifiestos_app;
