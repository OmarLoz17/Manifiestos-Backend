-- 20-nov-2026 (revisión de código H08, decisiones del usuario): lo que el servidor recibe pero no logra ubicar (`captura_por_revisar`) aparece en «Por corregir» como «No se pudo ubicar».
-- Operaciones puede (1) marcarlo como corregido sin hacer nada o (2) asignarlo a una orden (fosa fija) o a una parada (servicio de sanitarios); el registro conserva el autor y la hora
-- originales y la decisión queda en `resolucion_captura_revisada` (inmutable). Otros motivos (firma distinta, tirada) solo admiten «corregido».
CREATE TABLE negocio.resolucion_captura_revisada (
    organizacion_id uuid NOT NULL, sucursal_id uuid NOT NULL, captura_id uuid NOT NULL,
    accion text NOT NULL CHECK (accion IN ('corregido', 'asignado')),
    orden_destino_id uuid, parada_destino_id uuid,
    hecha_en timestamptz NOT NULL DEFAULT clock_timestamp(),
    por_acceso_id uuid DEFAULT seguridad.contexto_acceso(),
    PRIMARY KEY (organizacion_id, sucursal_id, captura_id),
    FOREIGN KEY (organizacion_id, sucursal_id, captura_id) REFERENCES negocio.captura ON DELETE RESTRICT,
    FOREIGN KEY (organizacion_id, por_acceso_id) REFERENCES seguridad.acceso_autorizado ON DELETE RESTRICT
);
CREATE INDEX resolucion_captura_revisada_acceso ON negocio.resolucion_captura_revisada (organizacion_id, por_acceso_id);
CREATE TRIGGER conservar_historia BEFORE UPDATE OR DELETE ON negocio.resolucion_captura_revisada FOR EACH ROW EXECUTE FUNCTION infra.inmutable();
ALTER TABLE negocio.resolucion_captura_revisada ENABLE ROW LEVEL SECURITY;
CREATE POLICY contexto ON negocio.resolucion_captura_revisada TO manifiestos_app USING (seguridad.puede(organizacion_id, sucursal_id, 'datos.leer'));
GRANT SELECT ON negocio.resolucion_captura_revisada TO manifiestos_app;

-- Una captura «por revisar» ya asignada a una orden se trata como aplicable; la corregida sin asignar sigue sin aplicarse.
CREATE FUNCTION negocio.captura_aplicable(o uuid, s uuid, p_captura uuid) RETURNS boolean
LANGUAGE sql STABLE SECURITY DEFINER SET search_path = pg_catalog AS $$
    SELECT NOT EXISTS (SELECT 1 FROM negocio.captura_por_revisar x WHERE (x.organizacion_id, x.sucursal_id, x.captura_id) = (o, s, p_captura))
        OR EXISTS (SELECT 1 FROM negocio.resolucion_captura_revisada r WHERE (r.organizacion_id, r.sucursal_id, r.captura_id) = (o, s, p_captura) AND r.accion = 'asignado')
$$;
REVOKE ALL ON FUNCTION negocio.captura_aplicable(uuid, uuid, uuid) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION negocio.captura_aplicable(uuid, uuid, uuid) TO manifiestos_app;

-- La captura de trabajo con su orden EFECTIVA (la asignada por Operaciones, si la hay).
CREATE VIEW negocio.captura_trabajo_efectiva WITH (security_invoker = true) AS
SELECT ct.organizacion_id, ct.sucursal_id, ct.captura_id, ct.tipo, ct.persona_id, ct.identidad_id,
       coalesce(r.orden_destino_id, ct.orden_derivada_id) AS orden_derivada_id,
       ct.jornada_id, ct.asignacion_trabajo, ct.revision_trabajo, ct.version_datos, ct.requiere_firma_cliente, ct.datos_orden, ct.contenido_aceptado_id,
       ct.nombre_firmante, ct.planta_id, ct.planta_nombre, ct.problema_motivo, ct.problema_comentario, ct.banos, ct.banos_asignados, ct.capturas_incluidas, ct.fosa
FROM negocio.captura_trabajo ct
LEFT JOIN negocio.resolucion_captura_revisada r ON (r.organizacion_id, r.sucursal_id, r.captura_id) = (ct.organizacion_id, ct.sucursal_id, ct.captura_id) AND r.accion = 'asignado' AND r.orden_destino_id IS NOT NULL;
GRANT SELECT ON negocio.captura_trabajo_efectiva TO manifiestos_app;

-- Evidencias completas y armado (migración 0062) con la orden efectiva y las capturas asignadas como aplicables.
CREATE OR REPLACE FUNCTION negocio.orden_con_evidencias_completas(o uuid, s uuid, p_orden uuid, p_autorizado boolean) RETURNS boolean
LANGUAGE sql STABLE SECURITY DEFINER SET search_path = pg_catalog AS $$
    WITH requeridas(tipo, rol) AS (
        VALUES ('SERVICIO_OPERADOR','FOTO'), ('SERVICIO_OPERADOR','FIRMA_OPERADOR'), ('RECEPCION_PLANTA','FIRMA_RECEPCION')
        UNION ALL SELECT 'FIRMA_CLIENTE','FIRMA_CLIENTE' WHERE p_autorizado
    )
    SELECT coalesce(bool_and(EXISTS (
        SELECT 1 FROM negocio.captura_trabajo_efectiva t
        JOIN negocio.evidencia_captura e ON (e.organizacion_id,e.sucursal_id,e.captura_id)=(t.organizacion_id,t.sucursal_id,t.captura_id)
        WHERE (t.organizacion_id,t.sucursal_id,t.orden_derivada_id)=(o,s,p_orden) AND t.tipo=q.tipo AND e.rol=q.rol AND e.archivo_id IS NOT NULL
          AND negocio.captura_aplicable(t.organizacion_id, t.sucursal_id, t.captura_id))), false)
    FROM requeridas q
$$;

-- Órdenes de fosa fija de una jornada con su parada y cliente (ids derivados, igual que resolver_orden_derivada): sirven para asignar un registro a una orden concreta.
CREATE FUNCTION negocio.ordenes_de_jornada(o uuid, s uuid, p_jornada uuid)
RETURNS TABLE (orden_id uuid, parada_id uuid, etiqueta text, cliente_id uuid, cliente text, vuelta integer)
LANGUAGE sql STABLE SECURITY DEFINER SET search_path = pg_catalog AS $$
    SELECT md5(j.id::text || ':' || q.parada_id::text || ':' || q.cliente_id::text || ':' || g::text)::uuid, q.parada_id, q.etiqueta, q.cliente_id, q.nombre, g
    FROM negocio.jornada j
    CROSS JOIN LATERAL (
        SELECT pa.id AS parada_id, pa.posicion::text AS etiqueta, pc.cliente_id,
               coalesce((SELECT af.fosas FROM negocio.ajuste_fosas_dia af WHERE (af.organizacion_id, af.sucursal_id, af.parada_id, af.cliente_id, af.fecha) = (o, s, pa.id, pc.cliente_id, j.fecha)
                         ORDER BY af.revision DESC LIMIT 1), pc.fosas_planeadas, 0) AS fosas
        FROM negocio.parada pa
        JOIN LATERAL (SELECT x.id FROM negocio.parada_version x WHERE (x.organizacion_id, x.sucursal_id, x.parada_id) = (pa.organizacion_id, pa.sucursal_id, pa.id) ORDER BY x.revision DESC LIMIT 1) pv ON true
        JOIN negocio.parada_cliente pc ON (pc.organizacion_id, pc.sucursal_id, pc.version_id) = (o, s, pv.id)
        WHERE (pa.organizacion_id, pa.sucursal_id, pa.ruta_id) = (j.organizacion_id, j.sucursal_id, j.ruta_id)
          AND NOT EXISTS (SELECT 1 FROM negocio.parada_baja pb WHERE (pb.organizacion_id, pb.sucursal_id, pb.parada_id) = (pa.organizacion_id, pa.sucursal_id, pa.id))
        UNION ALL
        SELECT pe.id, 'Extra', pc.cliente_id, coalesce(pc.fosas_planeadas, 0)
        FROM negocio.parada_extraordinaria pe
        JOIN negocio.parada_extraordinaria_cliente pc ON (pc.organizacion_id, pc.sucursal_id, pc.parada_extraordinaria_id) = (o, s, pe.id)
        WHERE (pe.organizacion_id, pe.sucursal_id, pe.ruta_id) = (j.organizacion_id, j.sucursal_id, j.ruta_id) AND pe.fecha = j.fecha
    ) base
    CROSS JOIN LATERAL (
        SELECT base.parada_id, base.etiqueta, base.cliente_id,
               coalesce(nullif(btrim(fe.nombre_comercial), ''), nullif(btrim(fe.nombre_fiscal), ''), nullif(btrim(cl.codigo_comercial), ''), 'Cliente sin nombre') AS nombre
        FROM negocio.cliente cl
        LEFT JOIN LATERAL (SELECT x.nombre_fiscal, x.nombre_comercial FROM negocio.ficha_empresa x WHERE (x.organizacion_id, x.empresa_id) = (cl.organizacion_id, cl.empresa_id) ORDER BY x.revision DESC LIMIT 1) fe ON true
        WHERE (cl.organizacion_id, cl.sucursal_id, cl.id) = (o, s, base.cliente_id)
    ) q
    CROSS JOIN LATERAL generate_series(1, base.fosas) g
    WHERE (j.organizacion_id, j.sucursal_id, j.id) = (o, s, p_jornada)
$$;
REVOKE ALL ON FUNCTION negocio.ordenes_de_jornada(uuid, uuid, uuid) FROM PUBLIC;

-- Arma el manifiesto de una orden ASIGNADA por Operaciones (no vuelve a exigir que la persona operara la unidad: esa es justo la comprobación que falló).
CREATE FUNCTION negocio.armar_orden_asignada(o uuid, s uuid, p_jornada uuid, p_orden uuid) RETURNS void
LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog AS $$
DECLARE d record; jo record; veh uuid; aut boolean; serv_en timestamptz; m uuid;
BEGIN
    IF EXISTS (SELECT 1 FROM negocio.orden_manifiesto om WHERE (om.organizacion_id, om.sucursal_id, om.orden_derivada_id) = (o, s, p_orden)) THEN RETURN; END IF;
    SELECT * INTO d FROM negocio.ordenes_de_jornada(o, s, p_jornada) x WHERE x.orden_id = p_orden;
    IF NOT FOUND THEN RETURN; END IF;
    SELECT j.ruta_id, j.fecha INTO jo FROM negocio.jornada j WHERE (j.organizacion_id, j.sucursal_id, j.id) = (o, s, p_jornada);
    SELECT min(k.ocurrida_en) INTO serv_en FROM negocio.captura_trabajo_efectiva t JOIN negocio.captura k ON (k.organizacion_id, k.sucursal_id, k.id) = (t.organizacion_id, t.sucursal_id, t.captura_id)
        WHERE (t.organizacion_id, t.sucursal_id, t.orden_derivada_id) = (o, s, p_orden) AND t.tipo = 'SERVICIO_OPERADOR' AND negocio.captura_aplicable(t.organizacion_id, t.sucursal_id, t.captura_id);
    IF serv_en IS NULL THEN RETURN; END IF;
    SELECT rv.vehiculo_id INTO veh FROM negocio.ruta_version_en(o, s, jo.ruta_id, jo.fecha, serv_en) rv;
    SELECT EXISTS (SELECT 1 FROM negocio.cliente cl JOIN negocio.autorizacion_empresa au ON (au.organizacion_id, au.empresa_id) = (cl.organizacion_id, cl.empresa_id)
                   WHERE (cl.organizacion_id, cl.sucursal_id, cl.id) = (o, s, d.cliente_id) AND au.autoridad = 'Medio ambiente' AND au.finalidad = 'Generador') INTO aut;
    IF veh IS NULL OR NOT negocio.orden_con_evidencias_completas(o, s, p_orden, aut) THEN RETURN; END IF;
    SELECT g.manifiesto INTO m FROM negocio.armar_manifiesto_fosa_fija(o, s, d.cliente_id, veh, true) g;
    INSERT INTO negocio.orden_manifiesto (organizacion_id, sucursal_id, orden_derivada_id, jornada_id, parada_id, cliente_id, vuelta, vehiculo_id, manifiesto_id)
    VALUES (o, s, p_orden, p_jornada, d.parada_id, d.cliente_id, d.vuelta, veh, m);
END $$;
REVOKE ALL ON FUNCTION negocio.armar_orden_asignada(uuid, uuid, uuid, uuid) FROM PUBLIC;

CREATE FUNCTION seguridad.sin_ubicar_web(o uuid, a uuid, p_dias integer DEFAULT 30) RETURNS jsonb
LANGUAGE sql STABLE SECURITY DEFINER SET search_path = pg_catalog AS $$
    WITH base AS (
        SELECT r.organizacion_id, r.sucursal_id, r.captura_id, r.motivo, ct.tipo, ct.persona_id, ct.jornada_id, ct.orden_derivada_id, k.ocurrida_en, j.ruta_id, j.fecha,
               (SELECT e.archivo_id FROM negocio.evidencia_captura e WHERE (e.organizacion_id, e.sucursal_id, e.captura_id) = (r.organizacion_id, r.sucursal_id, r.captura_id) AND e.archivo_id IS NOT NULL
                ORDER BY (e.rol = 'FOTO') DESC, e.recibida_en LIMIT 1) AS archivo,
               (SELECT row_to_json(x) FROM (SELECT rs.accion, rs.orden_destino_id, rs.parada_destino_id, rs.hecha_en FROM negocio.resolucion_captura_revisada rs
                                            WHERE (rs.organizacion_id, rs.sucursal_id, rs.captura_id) = (r.organizacion_id, r.sucursal_id, r.captura_id)) x) AS resolucion
        FROM negocio.captura_por_revisar r
        JOIN negocio.captura_trabajo ct ON (ct.organizacion_id, ct.sucursal_id, ct.captura_id) = (r.organizacion_id, r.sucursal_id, r.captura_id)
        JOIN negocio.captura k ON (k.organizacion_id, k.sucursal_id, k.id) = (r.organizacion_id, r.sucursal_id, r.captura_id)
        LEFT JOIN negocio.jornada j ON (j.organizacion_id, j.sucursal_id, j.id) = (ct.organizacion_id, ct.sucursal_id, ct.jornada_id)
        WHERE r.organizacion_id = o AND seguridad.puede(r.organizacion_id, r.sucursal_id, 'datos.leer') AND r.registrada_en > now() - make_interval(days => p_dias)
    )
    SELECT coalesce(jsonb_agg(jsonb_build_object(
        'capturaId', b.captura_id, 'ocurridaEn', b.ocurrida_en, 'tipoCaptura', b.tipo, 'motivo', b.motivo,
        'estado', CASE WHEN b.resolucion IS NULL THEN 'pendiente' ELSE 'resuelta' END, 'resolucion', b.resolucion,
        'operador', (SELECT p.nombre_completo FROM seguridad.persona p WHERE (p.organizacion_id, p.id) = (b.organizacion_id, b.persona_id)),
        'ruta', (SELECT v.nombre FROM negocio.ruta_version v WHERE (v.organizacion_id, v.sucursal_id, v.ruta_id) = (b.organizacion_id, b.sucursal_id, b.ruta_id) ORDER BY v.revision DESC LIMIT 1),
        'unidad', (SELECT f.numero_economico FROM negocio.ruta_version_en(b.organizacion_id, b.sucursal_id, b.ruta_id, b.fecha, b.ocurrida_en) rv
                   JOIN LATERAL (SELECT x.numero_economico FROM negocio.ficha_vehiculo x WHERE (x.organizacion_id, x.sucursal_id, x.vehiculo_id) = (b.organizacion_id, b.sucursal_id, rv.vehiculo_id) ORDER BY x.revision DESC LIMIT 1) f ON true),
        'archivoId', b.archivo,
        'puedeAsignar', b.ruta_id IS NOT NULL AND b.motivo IN ('orden_no_resuelta', 'parada_no_resuelta'),
        'ordenes', CASE WHEN b.motivo = 'orden_no_resuelta' AND b.ruta_id IS NOT NULL
                        THEN (SELECT coalesce(jsonb_agg(jsonb_build_object('ordenId', x.orden_id, 'paradaId', x.parada_id, 'etiqueta', x.etiqueta, 'cliente', x.cliente, 'vuelta', x.vuelta) ORDER BY x.etiqueta, x.cliente, x.vuelta), '[]'::jsonb)
                              FROM negocio.ordenes_de_jornada(b.organizacion_id, b.sucursal_id, b.jornada_id) x) ELSE '[]'::jsonb END,
        'paradas', CASE WHEN b.motivo = 'parada_no_resuelta' AND b.ruta_id IS NOT NULL
                        THEN (SELECT coalesce(jsonb_agg(jsonb_build_object('paradaId', x.id, 'etiqueta', x.etq, 'sitio', x.sitio) ORDER BY x.etq), '[]'::jsonb) FROM (
                                SELECT pa.id, pa.posicion::text AS etq, (SELECT f.nombre FROM negocio.ficha_sitio f WHERE (f.organizacion_id, f.sucursal_id, f.sitio_id) = (pa.organizacion_id, pa.sucursal_id,
                                         (SELECT pv.sitio_id FROM negocio.parada_version pv WHERE (pv.organizacion_id, pv.sucursal_id, pv.parada_id) = (pa.organizacion_id, pa.sucursal_id, pa.id) ORDER BY pv.revision DESC LIMIT 1))
                                       ORDER BY f.revision DESC LIMIT 1) AS sitio
                                FROM negocio.parada pa WHERE (pa.organizacion_id, pa.sucursal_id, pa.ruta_id) = (b.organizacion_id, b.sucursal_id, b.ruta_id)
                                  AND NOT EXISTS (SELECT 1 FROM negocio.parada_baja pb WHERE (pb.organizacion_id, pb.sucursal_id, pb.parada_id) = (pa.organizacion_id, pa.sucursal_id, pa.id))) x)
                        ELSE '[]'::jsonb END
    ) ORDER BY b.ocurrida_en DESC), '[]'::jsonb)
    FROM base b
$$;

-- Decisión: 'corregido' (no hacer nada) o 'asignar' (orden o parada). Devuelve 'ok', 'sin_permiso', 'no_encontrada', 'ya_resuelto', 'no_se_puede_asignar', 'destino_invalido' o 'destino_ocupado'.
CREATE FUNCTION seguridad.resolver_sin_ubicar_web(o uuid, a uuid, p_captura uuid, p_accion text, p_orden uuid, p_parada uuid) RETURNS text
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path = pg_catalog AS $$
DECLARE r record; ct record; k record; jo record; veh uuid; sitio uuid;
BEGIN
    IF p_accion NOT IN ('corregido', 'asignar') THEN RETURN 'destino_invalido'; END IF;
    SELECT * INTO r FROM negocio.captura_por_revisar x WHERE x.organizacion_id = o AND x.captura_id = p_captura;
    IF NOT FOUND THEN RETURN 'no_encontrada'; END IF;
    IF NOT seguridad.puede(r.organizacion_id, r.sucursal_id, 'datos.escribir') THEN RETURN 'sin_permiso'; END IF;
    PERFORM pg_advisory_xact_lock(hashtextextended('correccion:' || p_captura::text, 0));
    IF EXISTS (SELECT 1 FROM negocio.resolucion_captura_revisada x WHERE (x.organizacion_id, x.sucursal_id, x.captura_id) = (r.organizacion_id, r.sucursal_id, p_captura)) THEN RETURN 'ya_resuelto'; END IF;
    IF p_accion = 'corregido' THEN
        INSERT INTO negocio.resolucion_captura_revisada (organizacion_id, sucursal_id, captura_id, accion, por_acceso_id) VALUES (r.organizacion_id, r.sucursal_id, p_captura, 'corregido', a);
        RETURN 'ok';
    END IF;
    SELECT t.* INTO ct FROM negocio.captura_trabajo t WHERE (t.organizacion_id, t.sucursal_id, t.captura_id) = (r.organizacion_id, r.sucursal_id, p_captura);
    SELECT x.ocurrida_en, x.recibida_en INTO k FROM negocio.captura x WHERE (x.organizacion_id, x.sucursal_id, x.id) = (r.organizacion_id, r.sucursal_id, p_captura);
    SELECT j.ruta_id, j.fecha INTO jo FROM negocio.jornada j WHERE (j.organizacion_id, j.sucursal_id, j.id) = (r.organizacion_id, r.sucursal_id, ct.jornada_id);
    IF jo.ruta_id IS NULL OR r.motivo NOT IN ('orden_no_resuelta', 'parada_no_resuelta') THEN RETURN 'no_se_puede_asignar'; END IF;
    IF r.motivo = 'orden_no_resuelta' THEN
        IF p_orden IS NULL OR NOT EXISTS (SELECT 1 FROM negocio.ordenes_de_jornada(r.organizacion_id, r.sucursal_id, ct.jornada_id) x WHERE x.orden_id = p_orden) THEN RETURN 'destino_invalido'; END IF;
        INSERT INTO negocio.resolucion_captura_revisada (organizacion_id, sucursal_id, captura_id, accion, orden_destino_id, por_acceso_id) VALUES (r.organizacion_id, r.sucursal_id, p_captura, 'asignado', p_orden, a);
        PERFORM negocio.armar_orden_asignada(r.organizacion_id, r.sucursal_id, ct.jornada_id, p_orden);
        RETURN 'ok';
    END IF;
    -- parada_no_resuelta: servicio de sanitarios sin parada; se registra la succión en la parada elegida, con el autor y la hora originales.
    IF ct.tipo <> 'SERVICIO_SANITARIOS' OR p_parada IS NULL OR NOT negocio.parada_en_jornada(r.organizacion_id, r.sucursal_id, ct.jornada_id, p_parada) THEN RETURN 'destino_invalido'; END IF;
    SELECT rv.vehiculo_id INTO veh FROM negocio.ruta_version_en(r.organizacion_id, r.sucursal_id, jo.ruta_id, jo.fecha, k.ocurrida_en) rv;
    SELECT coalesce((SELECT x.sitio_id FROM negocio.parada_version x WHERE (x.organizacion_id, x.sucursal_id, x.parada_id) = (r.organizacion_id, r.sucursal_id, p_parada) ORDER BY x.revision DESC LIMIT 1),
                    (SELECT pe.sitio_id FROM negocio.parada_extraordinaria pe WHERE (pe.organizacion_id, pe.sucursal_id, pe.id) = (r.organizacion_id, r.sucursal_id, p_parada))) INTO sitio;
    IF veh IS NULL OR sitio IS NULL THEN RETURN 'destino_invalido'; END IF;
    BEGIN
        PERFORM negocio.bloquear_unidad(r.organizacion_id, veh);
        INSERT INTO negocio.succion_parada (organizacion_id, sucursal_id, captura_id, jornada_id, parada_id, vehiculo_id, persona_id, sitio_id,
            banos_succionados, banos_asignados, litros_por_bano, fecha_captura, recibida_en, es_fosa)
        VALUES (r.organizacion_id, r.sucursal_id, p_captura, ct.jornada_id, p_parada, veh, ct.persona_id, sitio,
            coalesce(ct.banos, 0), coalesce(ct.banos_asignados, 0), negocio.litros_por_bano(), k.ocurrida_en, k.recibida_en, coalesce(ct.fosa, false));
    EXCEPTION WHEN unique_violation THEN
        RETURN 'destino_ocupado';
    END;
    PERFORM negocio.reintentar_tiradas(r.organizacion_id, r.sucursal_id, veh, p_captura);
    INSERT INTO negocio.resolucion_captura_revisada (organizacion_id, sucursal_id, captura_id, accion, parada_destino_id, por_acceso_id) VALUES (r.organizacion_id, r.sucursal_id, p_captura, 'asignado', p_parada, a);
    RETURN 'ok';
END $$;

REVOKE ALL ON FUNCTION seguridad.sin_ubicar_web(uuid, uuid, integer) FROM PUBLIC;
REVOKE ALL ON FUNCTION seguridad.resolver_sin_ubicar_web(uuid, uuid, uuid, text, uuid, uuid) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION seguridad.sin_ubicar_web(uuid, uuid, integer) TO manifiestos_app;
GRANT EXECUTE ON FUNCTION seguridad.resolver_sin_ubicar_web(uuid, uuid, uuid, text, uuid, uuid) TO manifiestos_app;
