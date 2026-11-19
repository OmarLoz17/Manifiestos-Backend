-- 13-nov-2026: volumen estimado por parada en el manifiesto de SANITARIOS.
--   * negocio.manifiesto_parada.volumen_m3 (NULL en manifiestos anteriores): capacidad de la unidad x baños de la parada / baños de la tirada,
--     con variación de +/-3 % entre paradas con los mismos baños (estimación por regla; reemplaza mostrar baños x 16 L).
--   Solo ALTER ADD COLUMN y CREATE OR REPLACE: no se tocan migraciones anteriores.
ALTER TABLE negocio.manifiesto_parada ADD COLUMN volumen_m3 numeric(12,3) CHECK (volumen_m3 IS NULL OR volumen_m3 >= 0);

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

    -- Volumen estimado por parada (m3): la unidad sale llena, así que se reparte su capacidad según los baños de cada parada
    -- (capacidad x baños / total de baños; ya no x 16 L). Paradas con los MISMOS baños no repiten el mismo número: se emparejan y una sube
    -- 3 % y la otra baja 3 % (el par suma igual); una sobrante queda sin variación. Es estimación por regla, no medición.
    INSERT INTO negocio.manifiesto_parada (organizacion_id, sucursal_id, version_id, renglon, parada_id, captura_id, sitio_id, ficha_sitio_id,
        fecha, banos, latitud, longitud, precision_m, clientes, evidencia_foto_id, volumen_m3)
    WITH base AS (
        SELECT sp.*, row_number() OVER (ORDER BY sp.fecha_captura, sp.recibida_en, sp.captura_id) AS renglon
        FROM negocio.tirada_esperada e
        JOIN negocio.succion_parada sp ON (sp.organizacion_id, sp.sucursal_id, sp.captura_id) = (e.organizacion_id, e.sucursal_id, e.captura_id)
        WHERE (e.organizacion_id, e.sucursal_id, e.tirada_id) = (o, s, p_tirada)
    ), grupos AS (
        SELECT b.*, row_number() OVER (PARTITION BY b.banos_succionados ORDER BY b.renglon) AS pos,
               count(*) OVER (PARTITION BY b.banos_succionados) AS n
        FROM base b
    )
    SELECT o, s, v, g.renglon, g.parada_id, g.captura_id, g.sitio_id, fs.id,
           g.fecha_captura, g.banos_succionados, ev.latitud, ev.longitud, ev.precision_metros,
           negocio.clientes_parada_en(o, s, g.parada_id, g.fecha_captura), ev.id,
           round(capacidad * g.banos_succionados / total
                 * CASE WHEN g.n > 1 AND g.pos = g.n AND g.n % 2 = 1 THEN 1 WHEN g.n > 1 AND g.pos % 2 = 1 THEN 1.03 WHEN g.n > 1 THEN 0.97 ELSE 1 END, 3)
    FROM grupos g
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
REVOKE ALL ON FUNCTION negocio.armar_manifiesto_sanitarios(uuid, uuid, uuid) FROM PUBLIC;
