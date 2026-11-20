-- 20-nov-2026: pestaña «Por corregir» de Incidencias (lámina aprobada del lienzo). Un servicio de sanitarios (o fosa propia) que el operador registró en una parada, pero cuya
-- FOTO se tomó junto a OTRA parada de la misma ruta, aparece para que Operaciones lo revise: moverlo a la parada correcta, dejarlo donde está o elegir otra parada.
--   * `negocio.correccion_captura`: historial inmutable de cada decisión (parada de origen y de destino, quién y cuándo).
--   * `seguridad.por_corregir_web`: pendientes (la foto está a >200 m de la parada registrada y a ≤250 m de otra, o a >600 m de todas) y resueltos, con foto, mapa y sugerencia.
--   * `seguridad.corregir_captura_web`: «mover» solo si el servicio todavía NO está en un manifiesto ni en una tirada. Es el único cambio permitido sobre una succión:
--     se hace dentro de la función (propietario) con el cierre de historia apagado un instante y queda registrado en `correccion_captura` con la parada original.
--   Fosa fija queda fuera de esta primera versión (su parada sale de la orden derivada, no de una succión).
CREATE TABLE negocio.correccion_captura (
    organizacion_id uuid NOT NULL, sucursal_id uuid NOT NULL, captura_id uuid NOT NULL,
    accion text NOT NULL CHECK (accion IN ('mover', 'dejar')),
    parada_origen_id uuid NOT NULL, parada_destino_id uuid NOT NULL,
    hecha_en timestamptz NOT NULL DEFAULT clock_timestamp(),
    por_acceso_id uuid DEFAULT seguridad.contexto_acceso(),
    PRIMARY KEY (organizacion_id, sucursal_id, captura_id),
    FOREIGN KEY (organizacion_id, sucursal_id, captura_id) REFERENCES negocio.captura ON DELETE RESTRICT,
    FOREIGN KEY (organizacion_id, por_acceso_id) REFERENCES seguridad.acceso_autorizado ON DELETE RESTRICT
);
CREATE INDEX correccion_captura_acceso ON negocio.correccion_captura (organizacion_id, por_acceso_id);
CREATE TRIGGER conservar_historia BEFORE UPDATE OR DELETE ON negocio.correccion_captura FOR EACH ROW EXECUTE FUNCTION infra.inmutable();
ALTER TABLE negocio.correccion_captura ENABLE ROW LEVEL SECURITY;
CREATE POLICY contexto ON negocio.correccion_captura TO manifiestos_app USING (seguridad.puede(organizacion_id, sucursal_id, 'datos.leer'));
GRANT SELECT ON negocio.correccion_captura TO manifiestos_app;

-- Metros entre dos puntos (haversine).
CREATE FUNCTION negocio.distancia_m(lat1 numeric, lon1 numeric, lat2 numeric, lon2 numeric) RETURNS double precision
LANGUAGE sql IMMUTABLE AS $$
    SELECT 2 * 6371000 * asin(sqrt(least(1, power(sin(radians((lat2 - lat1)::double precision) / 2), 2)
        + cos(radians(lat1::double precision)) * cos(radians(lat2::double precision)) * power(sin(radians((lon2 - lon1)::double precision) / 2), 2))))
$$;

CREATE FUNCTION seguridad.por_corregir_web(o uuid, a uuid, p_dias integer DEFAULT 14) RETURNS jsonb
LANGUAGE sql STABLE SECURITY DEFINER SET search_path = pg_catalog AS $$
    WITH base AS (
        SELECT sp.organizacion_id, sp.sucursal_id, sp.captura_id, sp.jornada_id, sp.parada_id, sp.vehiculo_id, sp.persona_id, sp.fecha_captura,
               j.ruta_id, j.fecha, e.archivo_id, e.latitud AS flat, e.longitud AS flon
        FROM negocio.succion_parada sp
        JOIN negocio.jornada j ON (j.organizacion_id, j.sucursal_id, j.id) = (sp.organizacion_id, sp.sucursal_id, sp.jornada_id)
        JOIN LATERAL (SELECT x.archivo_id, x.latitud, x.longitud FROM negocio.evidencia_captura x
                      WHERE (x.organizacion_id, x.sucursal_id, x.captura_id) = (sp.organizacion_id, sp.sucursal_id, sp.captura_id) AND x.rol = 'FOTO' AND x.latitud IS NOT NULL
                      ORDER BY x.recibida_en NULLS LAST LIMIT 1) e ON true
        WHERE sp.organizacion_id = o AND seguridad.puede(sp.organizacion_id, sp.sucursal_id, 'datos.leer')
          AND sp.fecha_captura > now() - make_interval(days => p_dias)
    ), paradas AS (
        SELECT b.captura_id, pa.id AS parada_id, pa.posicion::text AS etiqueta, s.nombre AS sitio, s.latitud AS lat, s.longitud AS lon
        FROM base b
        JOIN negocio.parada pa ON (pa.organizacion_id, pa.sucursal_id, pa.ruta_id) = (b.organizacion_id, b.sucursal_id, b.ruta_id)
             AND NOT EXISTS (SELECT 1 FROM negocio.parada_baja pb WHERE (pb.organizacion_id, pb.sucursal_id, pb.parada_id) = (pa.organizacion_id, pa.sucursal_id, pa.id))
        JOIN LATERAL (SELECT x.sitio_id FROM negocio.parada_version x WHERE (x.organizacion_id, x.sucursal_id, x.parada_id) = (pa.organizacion_id, pa.sucursal_id, pa.id)
                      ORDER BY x.revision DESC LIMIT 1) pv ON true
        JOIN LATERAL (SELECT f.nombre, f.latitud, f.longitud FROM negocio.ficha_sitio f WHERE (f.organizacion_id, f.sucursal_id, f.sitio_id) = (pa.organizacion_id, pa.sucursal_id, pv.sitio_id)
                      ORDER BY f.revision DESC LIMIT 1) s ON true
        UNION ALL
        SELECT b.captura_id, pe.id, 'Extra', s.nombre, s.latitud, s.longitud
        FROM base b
        JOIN negocio.parada_extraordinaria pe ON (pe.organizacion_id, pe.sucursal_id, pe.ruta_id) = (b.organizacion_id, b.sucursal_id, b.ruta_id) AND pe.fecha = b.fecha
        JOIN LATERAL (SELECT f.nombre, f.latitud, f.longitud FROM negocio.ficha_sitio f WHERE (f.organizacion_id, f.sucursal_id, f.sitio_id) = (pe.organizacion_id, pe.sucursal_id, pe.sitio_id)
                      ORDER BY f.revision DESC LIMIT 1) s ON true
    ), medidas AS (
        SELECT p.captura_id, p.parada_id, p.etiqueta, p.sitio, p.lat, p.lon,
               CASE WHEN p.lat IS NULL THEN NULL ELSE round(negocio.distancia_m(b.flat, b.flon, p.lat, p.lon))::integer END AS metros
        FROM paradas p JOIN base b USING (captura_id)
    ), analisis AS (
        SELECT b.*,
               (SELECT m.metros FROM medidas m WHERE m.captura_id = b.captura_id AND m.parada_id = b.parada_id) AS m_registrada,
               (SELECT row_to_json(x) FROM (SELECT m.parada_id, m.metros FROM medidas m WHERE m.captura_id = b.captura_id AND m.parada_id <> b.parada_id AND m.metros IS NOT NULL
                                            ORDER BY m.metros LIMIT 1) x) AS cercana,
               EXISTS (SELECT 1 FROM negocio.manifiesto_parada mp WHERE (mp.organizacion_id, mp.sucursal_id, mp.captura_id) = (b.organizacion_id, b.sucursal_id, b.captura_id)) AS en_manifiesto,
               (SELECT row_to_json(c) FROM (SELECT cc.accion, cc.parada_origen_id, cc.parada_destino_id, cc.hecha_en FROM negocio.correccion_captura cc
                                            WHERE (cc.organizacion_id, cc.sucursal_id, cc.captura_id) = (b.organizacion_id, b.sucursal_id, b.captura_id)) c) AS correccion
        FROM base b
    ), marcadas AS (
        SELECT n.*, (n.cercana ->> 'metros')::integer AS m_cercana, (n.cercana ->> 'parada_id')::uuid AS id_cercana FROM analisis n
        WHERE n.correccion IS NOT NULL
           OR (n.m_registrada > 200 AND ((n.cercana ->> 'metros')::integer <= 250 AND (n.cercana ->> 'metros')::integer < n.m_registrada))
           OR n.m_registrada > 600
    )
    SELECT coalesce(jsonb_agg(jsonb_build_object(
        'capturaId', m.captura_id, 'ocurridaEn', m.fecha_captura, 'estado', CASE WHEN m.correccion IS NULL THEN 'pendiente' ELSE 'resuelta' END,
        'operador', (SELECT p.nombre_completo FROM seguridad.persona p WHERE (p.organizacion_id, p.id) = (m.organizacion_id, m.persona_id)),
        'unidad', (SELECT f.numero_economico FROM negocio.ficha_vehiculo f WHERE (f.organizacion_id, f.sucursal_id, f.vehiculo_id) = (m.organizacion_id, m.sucursal_id, m.vehiculo_id) ORDER BY f.revision DESC LIMIT 1),
        'ruta', (SELECT v.nombre FROM negocio.ruta_version v WHERE (v.organizacion_id, v.sucursal_id, v.ruta_id) = (m.organizacion_id, m.sucursal_id, m.ruta_id) ORDER BY v.revision DESC LIMIT 1),
        'registradaId', CASE WHEN m.correccion IS NULL THEN m.parada_id ELSE (m.correccion ->> 'parada_origen_id')::uuid END,
        'sugeridaId', CASE WHEN m.correccion IS NULL AND m.m_cercana <= 250 AND m.m_cercana < m.m_registrada THEN m.id_cercana END,
        'resolucion', m.correccion,
        'enManifiesto', m.en_manifiesto,
        'foto', jsonb_build_object('archivoId', m.archivo_id, 'latitud', m.flat, 'longitud', m.flon),
        'paradas', (SELECT coalesce(jsonb_agg(jsonb_build_object('paradaId', x.parada_id, 'etiqueta', x.etiqueta, 'sitio', x.sitio, 'latitud', x.lat, 'longitud', x.lon, 'metros', x.metros)
                                                ORDER BY x.etiqueta), '[]'::jsonb) FROM medidas x WHERE x.captura_id = m.captura_id)
    ) ORDER BY m.fecha_captura DESC), '[]'::jsonb)
    FROM marcadas m
$$;

-- Decisión sobre un servicio: devuelve 'ok', 'sin_permiso', 'no_encontrada', 'ya_resuelto', 'ya_en_manifiesto', 'destino_invalido' o 'destino_ocupado'.
CREATE FUNCTION seguridad.corregir_captura_web(o uuid, a uuid, p_captura uuid, p_accion text, p_parada uuid) RETURNS text
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path = pg_catalog AS $$
DECLARE sp record; destino uuid; sitio uuid; rv record;
BEGIN
    IF p_accion NOT IN ('mover', 'dejar') THEN RETURN 'destino_invalido'; END IF;
    SELECT s.* INTO sp FROM negocio.succion_parada s WHERE s.organizacion_id = o AND s.captura_id = p_captura;
    IF NOT FOUND THEN RETURN 'no_encontrada'; END IF;
    IF NOT seguridad.puede(sp.organizacion_id, sp.sucursal_id, 'datos.escribir') THEN RETURN 'sin_permiso'; END IF;
    PERFORM pg_advisory_xact_lock(hashtextextended('correccion:' || p_captura::text, 0));
    IF EXISTS (SELECT 1 FROM negocio.correccion_captura c WHERE (c.organizacion_id, c.sucursal_id, c.captura_id) = (sp.organizacion_id, sp.sucursal_id, p_captura)) THEN RETURN 'ya_resuelto'; END IF;
    IF p_accion = 'dejar' THEN
        INSERT INTO negocio.correccion_captura (organizacion_id, sucursal_id, captura_id, accion, parada_origen_id, parada_destino_id, por_acceso_id)
        VALUES (sp.organizacion_id, sp.sucursal_id, p_captura, 'dejar', sp.parada_id, sp.parada_id, a);
        RETURN 'ok';
    END IF;
    destino := p_parada;
    IF destino IS NULL OR destino = sp.parada_id OR NOT negocio.parada_en_jornada(sp.organizacion_id, sp.sucursal_id, sp.jornada_id, destino) THEN RETURN 'destino_invalido'; END IF;
    IF EXISTS (SELECT 1 FROM negocio.manifiesto_parada mp WHERE (mp.organizacion_id, mp.sucursal_id, mp.captura_id) = (sp.organizacion_id, sp.sucursal_id, p_captura))
       OR EXISTS (SELECT 1 FROM negocio.tirada_parada tp WHERE (tp.organizacion_id, tp.sucursal_id, tp.captura_id) = (sp.organizacion_id, sp.sucursal_id, p_captura)) THEN
        RETURN 'ya_en_manifiesto';
    END IF;
    SELECT coalesce((SELECT x.sitio_id FROM negocio.parada_version x WHERE (x.organizacion_id, x.sucursal_id, x.parada_id) = (sp.organizacion_id, sp.sucursal_id, destino) ORDER BY x.revision DESC LIMIT 1),
                    (SELECT pe.sitio_id FROM negocio.parada_extraordinaria pe WHERE (pe.organizacion_id, pe.sucursal_id, pe.id) = (sp.organizacion_id, sp.sucursal_id, destino))) INTO sitio;
    IF sitio IS NULL THEN RETURN 'destino_invalido'; END IF;
    BEGIN
        -- Único cambio permitido sobre una succión ya recibida: se queda constancia de la parada original en correccion_captura.
        ALTER TABLE negocio.succion_parada DISABLE TRIGGER conservar_historia;
        UPDATE negocio.succion_parada SET parada_id = destino, sitio_id = sitio WHERE organizacion_id = sp.organizacion_id AND sucursal_id = sp.sucursal_id AND captura_id = p_captura;
        ALTER TABLE negocio.succion_parada ENABLE TRIGGER conservar_historia;
    EXCEPTION WHEN unique_violation THEN
        ALTER TABLE negocio.succion_parada ENABLE TRIGGER conservar_historia;
        RETURN 'destino_ocupado';
    END;
    INSERT INTO negocio.correccion_captura (organizacion_id, sucursal_id, captura_id, accion, parada_origen_id, parada_destino_id, por_acceso_id)
    VALUES (sp.organizacion_id, sp.sucursal_id, p_captura, 'mover', sp.parada_id, destino, a);
    RETURN 'ok';
END $$;

REVOKE ALL ON FUNCTION seguridad.por_corregir_web(uuid, uuid, integer) FROM PUBLIC;
REVOKE ALL ON FUNCTION seguridad.corregir_captura_web(uuid, uuid, uuid, text, uuid) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION seguridad.por_corregir_web(uuid, uuid, integer) TO manifiestos_app;
GRANT EXECUTE ON FUNCTION seguridad.corregir_captura_web(uuid, uuid, uuid, text, uuid) TO manifiestos_app;
GRANT EXECUTE ON FUNCTION negocio.distancia_m(numeric, numeric, numeric, numeric) TO manifiestos_app;
