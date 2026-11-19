\set ON_ERROR_STOP on
-- Comprobaciones de coherencia de datos_ejemplo.sql (se ejecuta sobre una base recién sembrada).
-- Falla con una excepción si alguna regla de negocio confirmada no se cumple en los datos de ejemplo.
DO $$
DECLARE o uuid := 'e1000000-0000-4000-8000-000000000001'; e uuid := 'e1000000-0000-4000-8000-000000000002'; n bigint;
BEGIN
    -- Volumen mínimo en todos los módulos.
    IF (SELECT count(*) FROM negocio.cliente WHERE organizacion_id = o) < 20 THEN RAISE EXCEPTION 'Pocos clientes'; END IF;
    IF (SELECT count(*) FROM negocio.vehiculo WHERE organizacion_id = o) < 6 THEN RAISE EXCEPTION 'Pocas unidades'; END IF;
    IF (SELECT count(*) FROM negocio.operador_ficha WHERE organizacion_id = o) < 6 THEN RAISE EXCEPTION 'Pocos operadores'; END IF;
    IF (SELECT count(*) FROM negocio.ruta WHERE organizacion_id = o) < 6 THEN RAISE EXCEPTION 'Pocas rutas'; END IF;
    IF (SELECT count(*) FROM negocio.manifiesto WHERE organizacion_id = o) < 10 THEN RAISE EXCEPTION 'Pocos manifiestos'; END IF;
    IF (SELECT count(*) FROM negocio.incidencia WHERE organizacion_id = o) < 6 THEN RAISE EXCEPTION 'Pocas incidencias'; END IF;

    -- Generador: cliente autorizado → el cliente; cliente general → nuestra empresa.
    SELECT count(*) INTO n
    FROM negocio.manifiesto m
    JOIN negocio.cliente c ON (c.organizacion_id, c.sucursal_id, c.id) = (m.organizacion_id, m.sucursal_id, m.cliente_id)
    JOIN negocio.version_manifiesto v ON (v.organizacion_id, v.sucursal_id, v.manifiesto_id) = (m.organizacion_id, m.sucursal_id, m.id)
    JOIN negocio.participante_manifiesto p ON (p.organizacion_id, p.sucursal_id, p.version_id, p.rol) = (v.organizacion_id, v.sucursal_id, v.id, 'generador')
    WHERE m.organizacion_id = o AND p.empresa_id <> CASE
        WHEN EXISTS (SELECT 1 FROM negocio.autorizacion_empresa a WHERE a.empresa_id = c.empresa_id AND a.finalidad = 'Generador') THEN c.empresa_id ELSE e END;
    IF n > 0 THEN RAISE EXCEPTION '% manifiestos con generador incorrecto', n; END IF;
    IF NOT EXISTS (SELECT 1 FROM negocio.participante_manifiesto WHERE organizacion_id = o AND rol = 'generador' AND empresa_id = e) THEN
        RAISE EXCEPTION 'Falta al menos un manifiesto de cliente general (generador = nuestra empresa)';
    END IF;

    -- Unidad llena (SOLO fosa fija): la cantidad de cada renglón es la capacidad de la unidad, estimada por regla.
    -- Los manifiestos de sanitarios (tirada) usan otra regla: baños × 16 L, sin firma de cliente y con nuestra empresa como generador.
    SELECT count(*) INTO n
    FROM negocio.detalle_manifiesto d
    JOIN negocio.version_manifiesto v ON (v.organizacion_id, v.sucursal_id, v.id) = (d.organizacion_id, d.sucursal_id, d.version_id)
    JOIN negocio.ficha_vehiculo f ON (f.organizacion_id, f.sucursal_id, f.id) = (v.organizacion_id, v.sucursal_id, v.ficha_vehiculo_id)
    JOIN catalogo.capacidad_vehiculo cap ON cap.id = f.capacidad_id
    WHERE d.organizacion_id = o AND v.version_plantilla = 'fosa-fija-1' AND (d.cantidad <> cap.valor_m3 OR d.naturaleza_cantidad <> 'estimada' OR d.metodo_cuantificacion <> 'capacidad_unidad_llena');
    IF n > 0 THEN RAISE EXCEPTION '% renglones no siguen la regla de unidad llena', n; END IF;

    -- Sanitarios (11-nov-2026): unidades de 1 m³, rutas por unidad + terciado, solo clientes generales y baños; fosas sin baños.
    IF EXISTS (SELECT 1 FROM negocio.ficha_vehiculo f JOIN catalogo.capacidad_vehiculo cap ON cap.id = f.capacidad_id
               WHERE f.organizacion_id = o AND f.clasificacion = 'sanitarios' AND cap.valor_m3 <> 1) THEN
        RAISE EXCEPTION 'Una unidad de sanitarios no es de 1 m3';
    END IF;
    IF EXISTS (SELECT 1 FROM negocio.ficha_vehiculo f JOIN catalogo.capacidad_vehiculo cap ON cap.id = f.capacidad_id
               WHERE f.organizacion_id = o AND f.clasificacion = 'fosas' AND cap.valor_m3 NOT IN (10, 15, 18)) THEN
        RAISE EXCEPTION 'Una unidad de fosas no es de 10, 15 o 18 m3';
    END IF;
    IF NOT EXISTS (SELECT 1 FROM negocio.ficha_vehiculo f WHERE f.organizacion_id = o AND f.clasificacion = 'sanitarios' AND f.capacidad_id = 4) THEN
        RAISE EXCEPTION 'Faltan unidades de sanitarios de 1 m3';
    END IF;
    SELECT count(*) INTO n
    FROM negocio.ruta r
    JOIN LATERAL (SELECT x.nombre, x.dias, x.vehiculo_id FROM negocio.ruta_version x WHERE x.ruta_id = r.id ORDER BY x.revision DESC LIMIT 1) v ON true
    JOIN negocio.vehiculo veh ON veh.id = v.vehiculo_id
    WHERE r.organizacion_id = o AND v.nombre <> veh.numero_economico || '-' || CASE v.dias WHEN 21 THEN 'LMV' WHEN 42 THEN 'MJS' ELSE '?' END;
    IF n > 0 THEN RAISE EXCEPTION '% rutas sin el nombre unidad + terciado', n; END IF;
    SELECT count(*) INTO n
    FROM negocio.parada pa
    JOIN LATERAL (SELECT x.id FROM negocio.parada_version x WHERE x.parada_id = pa.id ORDER BY x.revision DESC LIMIT 1) pv ON true
    JOIN negocio.parada_cliente pc ON pc.version_id = pv.id
    JOIN negocio.cliente c ON (c.organizacion_id, c.sucursal_id, c.id) = (pc.organizacion_id, pc.sucursal_id, pc.cliente_id)
    JOIN LATERAL (SELECT x.vehiculo_id FROM negocio.ruta_version x WHERE x.ruta_id = pa.ruta_id ORDER BY x.revision DESC LIMIT 1) rv ON true
    JOIN LATERAL (SELECT x.clasificacion FROM negocio.ficha_vehiculo x WHERE x.vehiculo_id = rv.vehiculo_id ORDER BY x.revision DESC LIMIT 1) f ON true
    WHERE pa.organizacion_id = o AND (
        (f.clasificacion = 'sanitarios' AND (coalesce(pc.fosas_planeadas, 0) > 0 OR coalesce(pc.banos_planeados, 0) = 0 OR EXISTS (
            SELECT 1 FROM negocio.autorizacion_empresa a WHERE a.empresa_id = c.empresa_id AND a.autoridad = 'Medio ambiente' AND a.finalidad = 'Generador')))
        OR (f.clasificacion = 'fosas' AND coalesce(pc.banos_planeados, 0) > 0));
    IF n > 0 THEN RAISE EXCEPTION '% clientes de parada que mezclan sanitarios y fosas o usan un cliente autorizado en sanitarios', n; END IF;
    IF (SELECT count(*) FROM negocio.sitio s WHERE s.organizacion_id = o AND s.referencia IN ('PTAR NORTE', 'PTAR DULCES NOMBRES')
        AND NOT EXISTS (SELECT 1 FROM negocio.punto_servicio ps WHERE ps.sitio_id = s.id)) <> 2 THEN
        RAISE EXCEPTION 'Faltan las plantas PTAR NORTE y PTAR DULCES NOMBRES (sitios que no son punto de servicio)';
    END IF;
    IF NOT EXISTS (SELECT 1 FROM negocio.firma_generador_precargada WHERE organizacion_id = o AND es_provisional AND archivo_id IS NULL) THEN
        RAISE EXCEPTION 'Falta la firma precargada del generador (marcador provisional)';
    END IF;

    -- Manifiestos de sanitarios (tirada): sin cliente, generador = nuestra empresa con domicilio fiscal, sin firma de cliente,
    -- cantidad = capacidad de la unidad en m3 (estimada, siempre se tira llena), folio del mismo contador que fosa fija y firma del generador provisional.
    IF NOT EXISTS (SELECT 1 FROM negocio.manifiesto WHERE organizacion_id = o AND tipo = 'sanitarios') THEN RAISE EXCEPTION 'Falta un manifiesto de sanitarios'; END IF;
    SELECT count(*) INTO n
    FROM negocio.manifiesto m
    JOIN negocio.version_manifiesto v ON (v.organizacion_id, v.sucursal_id, v.manifiesto_id) = (m.organizacion_id, m.sucursal_id, m.id)
    JOIN negocio.participante_manifiesto g ON (g.organizacion_id, g.sucursal_id, g.version_id, g.rol) = (v.organizacion_id, v.sucursal_id, v.id, 'generador')
    JOIN negocio.detalle_manifiesto d ON (d.organizacion_id, d.sucursal_id, d.version_id) = (v.organizacion_id, v.sucursal_id, v.id)
    WHERE m.organizacion_id = o AND m.tipo = 'sanitarios' AND (
        m.cliente_id IS NOT NULL OR g.empresa_id <> e OR g.origen_domicilio <> 'fiscal'
        OR d.metodo_cuantificacion <> 'capacidad_unidad_llena' OR d.naturaleza_cantidad <> 'estimada' OR d.unidad_cantidad <> 'm3'
        OR d.cantidad <> (SELECT cap.valor_m3 FROM negocio.ficha_vehiculo fv JOIN catalogo.capacidad_vehiculo cap ON cap.id = fv.capacidad_id WHERE fv.id = v.ficha_vehiculo_id)
        OR NOT EXISTS (SELECT 1 FROM negocio.recepcion_manifiesto r WHERE r.version_id = v.id AND r.firma_generador_id IS NOT NULL)
        OR EXISTS (SELECT 1 FROM negocio.participante_manifiesto x WHERE x.version_id = v.id AND x.rol = 'destinatario')
        OR v.numero_manifiesto !~ '^[0-9]{3}/[0-9]{4}$');
    IF n > 0 THEN RAISE EXCEPTION '% manifiestos de sanitarios no siguen la regla de capacidad de la unidad, generador propio y recepción', n; END IF;
    IF (SELECT ultimo FROM negocio.contador_folio WHERE organizacion_id = o AND anio = 2026) < 14 THEN RAISE EXCEPTION 'El folio de sanitarios no sale del contador compartido'; END IF;
    -- Barra de carga: la unidad ARM-410 tiene 50 baños sin tirar = 0.800 m3 de 1 m3 (80 %); lo ya tirado no cuenta.
    IF NOT EXISTS (SELECT 1 FROM negocio.vehiculo v, LATERAL negocio.carga_unidad_interna(o, v.sucursal_id, v.id) c
                   WHERE v.organizacion_id = o AND v.numero_economico = 'ARM-410' AND c.banos = 50 AND c.m3 = 0.800 AND c.porcentaje = 80.0) THEN
        RAISE EXCEPTION 'La carga actual de ARM-410 no es 50 baños / 0.800 m3 / 80 %%';
    END IF;
    IF EXISTS (SELECT 1 FROM negocio.captura_por_revisar WHERE organizacion_id = o) THEN RAISE EXCEPTION 'Hay capturas de ejemplo por revisar'; END IF;

    -- Cuentas de teléfono: cada operador con correo tiene su entrada activa con un rol sin panel.
    SELECT count(*) INTO n FROM negocio.operador_ficha f
    WHERE f.organizacion_id = o AND f.correo IS NOT NULL AND NOT EXISTS (
        SELECT 1 FROM seguridad.acceso_autorizado a WHERE a.organizacion_id = o AND a.correo_permitido = f.correo AND a.activo AND a.rol_codigo = 'telefono_operador');
    IF n > 0 THEN RAISE EXCEPTION '% operadores sin cuenta de teléfono activa', n; END IF;

    -- Cada incidencia nace abierta y su historial no tiene huecos.
    SELECT count(*) INTO n FROM negocio.incidencia i WHERE i.organizacion_id = o AND NOT EXISTS (
        SELECT 1 FROM negocio.seguimiento_incidencia s WHERE s.incidencia_id = i.id AND s.revision = 1 AND s.estatus = 'abierta');
    IF n > 0 THEN RAISE EXCEPTION '% incidencias sin seguimiento inicial', n; END IF;

    -- Los tres tipos de servicio aparecen en Órdenes (baños, fosa fija y fosa propia).
    IF NOT EXISTS (SELECT 1 FROM negocio.parada_cliente WHERE organizacion_id = o AND banos_planeados > 0) THEN RAISE EXCEPTION 'Sin sanitarios'; END IF;
    IF NOT EXISTS (SELECT 1 FROM negocio.parada_cliente pc JOIN negocio.parada_version pv ON pv.id = pc.version_id
        JOIN negocio.cliente_sitio cs ON (cs.cliente_id, cs.sitio_id) = (pc.cliente_id, pv.sitio_id) AND cs.fosa_propia
        WHERE pc.organizacion_id = o AND pc.fosas_planeadas > 0) THEN RAISE EXCEPTION 'Sin fosas propias'; END IF;
    IF NOT EXISTS (SELECT 1 FROM negocio.parada_cliente pc JOIN negocio.parada_version pv ON pv.id = pc.version_id
        JOIN negocio.cliente_sitio cs ON (cs.cliente_id, cs.sitio_id) = (pc.cliente_id, pv.sitio_id) AND NOT cs.fosa_propia
        WHERE pc.organizacion_id = o AND pc.fosas_planeadas > 0) THEN RAISE EXCEPTION 'Sin fosas fijas'; END IF;

    -- Nada con aspecto de dato real de contacto: correos solo en dominios reservados.
    IF EXISTS (SELECT 1 FROM negocio.ficha_empresa WHERE organizacion_id = o AND correo IS NOT NULL AND correo NOT LIKE '%@example.com') THEN
        RAISE EXCEPTION 'Correo de empresa fuera de example.com';
    END IF;
    -- La única cuenta real posible es la de Operaciones que autoriza New-Ejemplo.ps1 -CorreoAcceso.
    IF EXISTS (SELECT 1 FROM seguridad.acceso_autorizado WHERE organizacion_id = o AND rol_codigo = 'telefono_operador' AND correo_permitido NOT LIKE '%@example.com') THEN
        RAISE EXCEPTION 'Cuenta de teléfono fuera de example.com';
    END IF;
    RAISE NOTICE 'Datos de ejemplo coherentes.';
END $$;
