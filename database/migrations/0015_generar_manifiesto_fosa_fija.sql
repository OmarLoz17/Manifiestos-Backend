-- Generación del manifiesto de una orden de fosa fija (reglas confirmadas el 6-nov-2026).
-- - Generador: cliente AUTORIZADO (autorización de medio ambiente como generador) → el cliente, con su
--   autorización; cliente GENERAL → nuestra empresa (la empresa de la sucursal) con su domicilio fiscal.
--   Con cliente general no se pide firma del cliente: la función lo indica en generador_es_cliente.
-- - Transportista: siempre nuestra empresa, con su autorización de transportista si existe.
-- - Cantidad: la capacidad de la unidad llena, estimada por regla (no medida).
-- - No asigna folio (PG-03, PG-23) ni destinatario: el destinatario se registra al descargar en la planta.
-- Se llamará al recibir de la app la orden atendida; por ahora la usan las pruebas y la demo.
CREATE FUNCTION negocio.generar_manifiesto_fosa_fija(o uuid, s uuid, p_cliente uuid, p_vehiculo uuid)
RETURNS TABLE (manifiesto uuid, generador_es_cliente boolean)
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path = pg_catalog AS $$
DECLARE
    emp_cli uuid; emp_nuestra uuid; aut_cli uuid; aut_gen uuid; aut_tra uuid; emp_gen uuid;
    ficha_veh uuid; capacidad numeric; ficha_gen uuid; ficha_tra uuid; m uuid; v uuid;
BEGIN
    IF NOT coalesce(seguridad.puede(o, s, 'datos.escribir'), false) THEN
        RAISE EXCEPTION 'Sin permiso de escritura en la sucursal' USING ERRCODE = '42501';
    END IF;
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
    INSERT INTO negocio.version_manifiesto (organizacion_id, sucursal_id, manifiesto_id, revision, ficha_vehiculo_id, version_plantilla)
    VALUES (o, s, m, 1, ficha_veh, 'fosa-fija-1') RETURNING id INTO v;
    INSERT INTO negocio.participante_manifiesto (organizacion_id, sucursal_id, version_id, rol, empresa_id, ficha_empresa_id, autorizacion_id, origen_domicilio)
    VALUES (o, s, v, 'generador', emp_gen, ficha_gen, aut_gen, 'fiscal'),
           (o, s, v, 'transportista', emp_nuestra, ficha_tra, aut_tra, 'fiscal');
    INSERT INTO negocio.detalle_manifiesto (organizacion_id, sucursal_id, version_id, renglon, descripcion_residuo, tipo_contenedor,
        capacidad_contenedor, unidad_capacidad, numero_contenedores, cantidad, unidad_cantidad, naturaleza_cantidad, metodo_cuantificacion, version_metodo)
    VALUES (o, s, v, 1, 'Agua residual de fosa fija', 'Fosa del cliente',
        capacidad, 'm3', 1, capacidad, 'm3', 'estimada', 'capacidad_unidad_llena', '1');
    RETURN QUERY SELECT m, aut_cli IS NOT NULL;
END $$;

REVOKE ALL ON FUNCTION negocio.generar_manifiesto_fosa_fija(uuid, uuid, uuid, uuid) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION negocio.generar_manifiesto_fosa_fija(uuid, uuid, uuid, uuid) TO manifiestos_app;
