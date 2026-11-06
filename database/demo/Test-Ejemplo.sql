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

    -- Unidad llena: la cantidad de cada renglón es la capacidad de la unidad, estimada por regla.
    SELECT count(*) INTO n
    FROM negocio.detalle_manifiesto d
    JOIN negocio.version_manifiesto v ON (v.organizacion_id, v.sucursal_id, v.id) = (d.organizacion_id, d.sucursal_id, d.version_id)
    JOIN negocio.ficha_vehiculo f ON (f.organizacion_id, f.sucursal_id, f.id) = (v.organizacion_id, v.sucursal_id, v.ficha_vehiculo_id)
    JOIN catalogo.capacidad_vehiculo cap ON cap.id = f.capacidad_id
    WHERE d.organizacion_id = o AND (d.cantidad <> cap.valor_m3 OR d.naturaleza_cantidad <> 'estimada' OR d.metodo_cuantificacion <> 'capacidad_unidad_llena');
    IF n > 0 THEN RAISE EXCEPTION '% renglones no siguen la regla de unidad llena', n; END IF;

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
