-- Pendientes de ayer (decisión 0045, PG-39). Aditivo: no cambia el trabajo del día ni la recepción de capturas.
--   * Ajuste por cuenta o general (encendido por omisión), con historial insert-only.
--   * seguridad.pendientes_de_ayer: lo planeado ayer para la persona de la sesión que no se completó (incluido lo cancelado o «no se hará»),
--     sin extraordinarias. Todavía no la usa ningún endpoint: la recepción de capturas de pendientes va en una migración posterior.

CREATE TABLE seguridad.pendientes_ayer_ajuste (
    organizacion_id uuid NOT NULL,
    acceso_id uuid,                       -- NULL = ajuste general
    revision integer NOT NULL CHECK (revision > 0),
    activo boolean NOT NULL,
    registrado_por uuid,
    registrado_en timestamptz NOT NULL DEFAULT clock_timestamp(),
    FOREIGN KEY (organizacion_id, acceso_id) REFERENCES seguridad.acceso_autorizado (organizacion_id, id)
);
CREATE UNIQUE INDEX pendientes_ayer_ajuste_revision ON seguridad.pendientes_ayer_ajuste (organizacion_id, coalesce(acceso_id, '00000000-0000-0000-0000-000000000000'::uuid), revision);
CREATE TRIGGER pendientes_ayer_ajuste_inmutable BEFORE UPDATE OR DELETE ON seguridad.pendientes_ayer_ajuste FOR EACH ROW EXECUTE FUNCTION infra.inmutable();
REVOKE ALL ON seguridad.pendientes_ayer_ajuste FROM PUBLIC;

-- El ajuste de la cuenta manda sobre el general; sin ninguno, encendido.
CREATE FUNCTION seguridad.pendientes_ayer_activo(o uuid, a uuid) RETURNS boolean
LANGUAGE sql STABLE SECURITY DEFINER SET search_path = pg_catalog AS $$
    SELECT coalesce(
        (SELECT x.activo FROM seguridad.pendientes_ayer_ajuste x WHERE x.organizacion_id = o AND x.acceso_id = a ORDER BY x.revision DESC LIMIT 1),
        (SELECT x.activo FROM seguridad.pendientes_ayer_ajuste x WHERE x.organizacion_id = o AND x.acceso_id IS NULL ORDER BY x.revision DESC LIMIT 1),
        true)
$$;
REVOKE ALL ON FUNCTION seguridad.pendientes_ayer_activo(uuid, uuid) FROM PUBLIC;

-- Plan de un día ya pasado para la persona de la sesión. Se deriva de la definición VIGENTE de seguridad.trabajo_operador (varias
-- migraciones la han parcheado) y solo cambia: sin tope de fecha, incluye las jornadas canceladas y usa la asignación de unidad vigente
-- al cierre de ESE día (cambiar hoy de operador no traspasa lo de ayer). Si una migración futura cambia trabajo_operador, debe regenerar esta.
DO $migracion$
DECLARE d text; n text;
BEGIN
    d := pg_get_functiondef('seguridad.trabajo_operador(uuid,uuid,uuid,date)'::regprocedure);
    n := replace(d, 'CREATE OR REPLACE FUNCTION seguridad.trabajo_operador(', 'CREATE FUNCTION negocio.plan_de_ayer(');
    IF n = d THEN RAISE EXCEPTION 'No se encontró el encabezado de trabajo_operador'; END IF;
    d := n;
    n := regexp_replace(d, 'IF f IS NULL OR f <[^;]*?THEN\s+RAISE EXCEPTION [^;]*?;\s+END IF;', '');
    IF n = d THEN RAISE EXCEPTION 'No se encontró el tope de fecha en trabajo_operador'; END IF;
    d := n;
    n := replace(d, 'ej.estado IN (''programada'',''en_curso'')', 'ej.estado IN (''programada'',''en_curso'',''cancelada'')');
    IF n = d THEN RAISE EXCEPTION 'No se encontró el filtro de estado en trabajo_operador'; END IF;
    d := n;
    n := regexp_replace(d, '\(SELECT x\.persona_id FROM negocio\.asignacion_unidad x.*?ORDER BY x\.revision DESC LIMIT 1\)\)',
        'negocio.persona_de_unidad_en(o, ru.sucursal_id, veh.id, ((f + 1)::timestamp AT TIME ZONE ''America/Monterrey'')))');
    IF n = d THEN RAISE EXCEPTION 'No se encontró la asignación de unidad en trabajo_operador'; END IF;
    d := n;
    -- El plan es el vigente al cierre de ese día: una parada, cantidad o unidad cambiada HOY en Rutas no altera lo que había ayer.
    n := regexp_replace(d, '(\(x\.organizacion_id,x\.sucursal_id,x\.parada_id\)=\(pa\.organizacion_id,pa\.sucursal_id,pa\.id\))',
        '\1 AND x.registrada_en < ((f + 1)::timestamp AT TIME ZONE ''America/Monterrey'')', 'g');
    IF n = d THEN RAISE EXCEPTION 'No se encontraron las revisiones de parada en trabajo_operador'; END IF;
    d := n;
    n := regexp_replace(d, '(\(pb\.organizacion_id,pb\.sucursal_id,pb\.parada_id\)=\(pa\.organizacion_id,pa\.sucursal_id,pa\.id\))',
        '\1 AND pb.dada_de_baja_en < ((f + 1)::timestamp AT TIME ZONE ''America/Monterrey'')', 'g');
    IF n = d THEN RAISE EXCEPTION 'No se encontraron las bajas de parada en trabajo_operador'; END IF;
    d := n;
    n := regexp_replace(d, '(\(x\.organizacion_id,x\.sucursal_id,x\.ruta_id\)=\(ru\.organizacion_id,ru\.sucursal_id,ru\.id\))',
        '\1 AND x.registrada_en < ((f + 1)::timestamp AT TIME ZONE ''America/Monterrey'')', 'g');
    IF n = d THEN RAISE EXCEPTION 'No se encontró el plan de ruta en trabajo_operador'; END IF;
    EXECUTE n;
END
$migracion$;
REVOKE ALL ON FUNCTION negocio.plan_de_ayer(uuid, uuid, uuid, date) FROM PUBLIC;

-- «No se hará» marcado HOY por Operaciones sobre un pendiente de ayer: se lo quita al operador (lo marcado ayer sí queda como pendiente, 0045).
-- Historial insert-only; deshacer es otra revisión con activo = false. Nunca toca lo ya capturado.
CREATE TABLE negocio.pendiente_descartado (
    organizacion_id uuid NOT NULL, sucursal_id uuid NOT NULL, jornada_id uuid NOT NULL, parada_id uuid NOT NULL,
    revision integer NOT NULL CHECK (revision > 0), activo boolean NOT NULL,
    creado_en timestamptz NOT NULL DEFAULT clock_timestamp(),
    creado_por_acceso_id uuid DEFAULT seguridad.contexto_acceso(),
    PRIMARY KEY (organizacion_id, sucursal_id, jornada_id, parada_id, revision),
    FOREIGN KEY (organizacion_id, sucursal_id, jornada_id) REFERENCES negocio.jornada (organizacion_id, sucursal_id, id) ON DELETE RESTRICT,
    FOREIGN KEY (organizacion_id, creado_por_acceso_id) REFERENCES seguridad.acceso_autorizado (organizacion_id, id)
);
CREATE INDEX pendiente_descartado_acceso ON negocio.pendiente_descartado (organizacion_id, creado_por_acceso_id);
CREATE TRIGGER conservar_historia BEFORE UPDATE OR DELETE ON negocio.pendiente_descartado FOR EACH ROW EXECUTE FUNCTION infra.inmutable();
ALTER TABLE negocio.pendiente_descartado ENABLE ROW LEVEL SECURITY;
CREATE POLICY contexto ON negocio.pendiente_descartado TO manifiestos_app USING (seguridad.puede(organizacion_id, sucursal_id, 'datos.leer')) WITH CHECK (seguridad.puede(organizacion_id, sucursal_id, 'datos.escribir'));
GRANT SELECT, INSERT ON negocio.pendiente_descartado TO manifiestos_app;

-- Datos de una unidad como los entrega el trabajo del operador (con su carga en sanitarios).
CREATE FUNCTION negocio.unidad_para_trabajo(o uuid, s uuid, p_vehiculo uuid, p_servicio text) RETURNS jsonb
LANGUAGE sql STABLE SECURITY DEFINER SET search_path = pg_catalog AS $$
    SELECT jsonb_build_object('id', v.id, 'numeroEconomico', coalesce(fv.numero_economico, v.numero_economico), 'capacidadM3', cap.valor_m3, 'clasificacion', fv.clasificacion)
        || CASE WHEN p_servicio = 'sanitarios' THEN (SELECT jsonb_build_object('carga', jsonb_build_object('banos', g.banos, 'm3', g.m3, 'capacidadM3', cap.valor_m3, 'porcentaje', g.porcentaje))
                    FROM negocio.carga_unidad_interna(o, s, v.id) g) ELSE '{}'::jsonb END
    FROM negocio.vehiculo v
    LEFT JOIN LATERAL (SELECT x.* FROM negocio.ficha_vehiculo x WHERE (x.organizacion_id, x.sucursal_id, x.vehiculo_id) = (o, s, v.id) ORDER BY x.revision DESC LIMIT 1) fv ON true
    LEFT JOIN catalogo.capacidad_vehiculo cap ON cap.id = fv.capacidad_id
    WHERE (v.organizacion_id, v.sucursal_id, v.id) = (o, s, p_vehiculo)
$$;
REVOKE ALL ON FUNCTION negocio.unidad_para_trabajo(uuid, uuid, uuid, text) FROM PUBLIC;

CREATE FUNCTION seguridad.pendientes_de_ayer(o uuid, a uuid, i uuid, f date) RETURNS jsonb
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path = pg_catalog AS $$
DECLARE c record; ayer date; corte timestamptz; plan jsonb; j jsonb; p jsonb; k jsonb; ordenes jsonb; clientes jsonb; paradas jsonb; jornadas jsonb := '[]'::jsonb; jid uuid; hechos_banos integer; hechas_fosas integer; banos integer; fosas integer; veh uuid;
BEGIN
    IF f IS NULL OR f < ((now() AT TIME ZONE 'UTC')::date - 1) OR f > ((now() AT TIME ZONE 'UTC')::date + 1) THEN
        RAISE EXCEPTION 'Fecha fuera del rango permitido' USING ERRCODE = '22023';
    END IF;
    SELECT * INTO c FROM seguridad.contexto_operador(o, a, i);
    IF NOT FOUND THEN RETURN NULL; END IF;
    ayer := f - 1;   -- el día calendario anterior, sin excepciones
    corte := (f::timestamp AT TIME ZONE 'America/Monterrey');   -- cierre de ayer (zona provisional, PG-18)
    IF NOT seguridad.pendientes_ayer_activo(o, a) THEN RETURN jsonb_build_object('fecha', ayer, 'jornadas', '[]'::jsonb); END IF;

    -- Lo que se haga hoy se cuenta en la unidad que la persona opera HOY; sin unidad no se muestran (no habría dónde contar la carga).
    veh := negocio.unidad_de_persona_en(o, c.sucursal_id, c.persona_id, clock_timestamp());
    IF veh IS NULL THEN RETURN jsonb_build_object('fecha', ayer, 'jornadas', '[]'::jsonb); END IF;
    -- Si nadie abrió la app ayer, la jornada de las rutas que salían ayer puede no existir (misma regla que asegurar_jornadas_del_dia).
    WITH nuevas AS (
        INSERT INTO negocio.jornada (organizacion_id, sucursal_id, id, ruta_id, fecha)
        SELECT ru.organizacion_id, ru.sucursal_id, gen_random_uuid(), ru.id, ayer
        FROM negocio.ruta ru
        JOIN LATERAL (SELECT x.dias FROM negocio.ruta_version x
            WHERE (x.organizacion_id, x.sucursal_id, x.ruta_id) = (ru.organizacion_id, ru.sucursal_id, ru.id) AND x.registrada_en < corte
            ORDER BY x.revision DESC LIMIT 1) rv ON true
        WHERE ru.organizacion_id = o AND ru.sucursal_id = c.sucursal_id
          AND (rv.dias & (1 << (extract(isodow FROM ayer)::int - 1))) <> 0
          AND EXISTS (SELECT 1 FROM negocio.parada p WHERE (p.organizacion_id, p.sucursal_id, p.ruta_id) = (ru.organizacion_id, ru.sucursal_id, ru.id) AND p.creada_en < corte
                      AND NOT EXISTS (SELECT 1 FROM negocio.parada_baja pb WHERE (pb.organizacion_id, pb.sucursal_id, pb.parada_id) = (p.organizacion_id, p.sucursal_id, p.id) AND pb.dada_de_baja_en < corte))
        ON CONFLICT (organizacion_id, sucursal_id, ruta_id, fecha) DO NOTHING
        RETURNING organizacion_id, sucursal_id, id)
    INSERT INTO negocio.estado_jornada (organizacion_id, sucursal_id, jornada_id, revision, estado)
    SELECT organizacion_id, sucursal_id, id, 1, 'programada' FROM nuevas;

    plan := negocio.plan_de_ayer(o, a, i, ayer);
    FOR j IN SELECT e FROM jsonb_array_elements(plan -> 'jornadas') e LOOP
        jid := (j ->> 'jornadaId')::uuid; paradas := '[]'::jsonb;
        FOR p IN SELECT e FROM jsonb_array_elements(j -> 'paradas') e LOOP
            CONTINUE WHEN p ->> 'tipo' <> 'plan';   -- las extraordinarias no pasan al día siguiente
            CONTINUE WHEN coalesce((SELECT d.activo FROM negocio.pendiente_descartado d
                WHERE (d.organizacion_id, d.sucursal_id, d.jornada_id, d.parada_id) = (o, c.sucursal_id, jid, (p ->> 'paradaId')::uuid)
                ORDER BY d.revision DESC LIMIT 1), false);
            IF j ->> 'servicio' = 'sanitarios' THEN
                -- Los baños de la parada son una captura y cada fosa propia otra (marca «fosa»): queda solo lo que falte.
                SELECT count(*) FILTER (WHERE NOT coalesce(t.fosa, false)), count(*) FILTER (WHERE t.fosa) INTO hechos_banos, hechas_fosas
                FROM negocio.captura_trabajo_efectiva t WHERE t.organizacion_id = o AND t.sucursal_id = c.sucursal_id
                    AND t.jornada_id = jid AND t.orden_derivada_id = (p ->> 'paradaId')::uuid AND t.tipo = 'SERVICIO_SANITARIOS';
                banos := CASE WHEN hechos_banos > 0 THEN 0 ELSE coalesce((p ->> 'banosAsignados')::integer, 0) END;
                fosas := greatest(coalesce((p ->> 'fosasPropias')::integer, 0) - hechas_fosas, 0);
                CONTINUE WHEN banos = 0 AND fosas = 0;
                p := p || jsonb_build_object('banosAsignados', banos, 'fosasPropias', fosas);
            END IF;
            clientes := '[]'::jsonb;
            FOR k IN SELECT e FROM jsonb_array_elements(p -> 'clientes') e LOOP
                CONTINUE WHEN (k ->> 'extraordinaria')::boolean;
                SELECT coalesce(jsonb_agg(x ORDER BY (x ->> 'vuelta')::int), '[]'::jsonb) INTO ordenes
                FROM jsonb_array_elements(k -> 'ordenes') x
                WHERE NOT EXISTS (SELECT 1 FROM negocio.captura_trabajo_efectiva t WHERE t.organizacion_id = o AND t.sucursal_id = c.sucursal_id
                    AND t.jornada_id = jid AND t.orden_derivada_id = (x ->> 'ordenId')::uuid AND t.tipo = 'SERVICIO_OPERADOR');
                CONTINUE WHEN j ->> 'servicio' = 'fosas' AND jsonb_array_length(ordenes) = 0;
                clientes := clientes || jsonb_build_array(k || jsonb_build_object('ordenes', ordenes, 'fosas', jsonb_array_length(ordenes)));
            END LOOP;
            CONTINUE WHEN jsonb_array_length(clientes) = 0;
            paradas := paradas || jsonb_build_array(p || jsonb_build_object('clientes', clientes, 'noSeHara', false));
        END LOOP;
        CONTINUE WHEN jsonb_array_length(paradas) = 0;
        veh := negocio.unidad_de_persona_en(o, c.sucursal_id, c.persona_id, clock_timestamp(), j ->> 'servicio');
        jornadas := jornadas || jsonb_build_array(j || jsonb_build_object('paradas', paradas, 'noSeHara', false,
            'unidad', negocio.unidad_para_trabajo(o, c.sucursal_id, veh, j ->> 'servicio')));
    END LOOP;
    RETURN jsonb_build_object('fecha', ayer, 'jornadas', jornadas);
END $$;
REVOKE ALL ON FUNCTION seguridad.pendientes_de_ayer(uuid, uuid, uuid, date) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION seguridad.pendientes_de_ayer(uuid, uuid, uuid, date) TO manifiestos_app;

-- Recepción de pendientes: una captura de HOY sobre una orden o parada que quedó de AYER se resuelve con la unidad que el operador tiene
-- en ese momento (no con la de la ruta de ayer). La resolución del mismo día no cambia: la anterior se conserva renombrada y se usa en lo demás.

-- Unidad que la persona opera a un instante: la que le dio un cambio del día, si no la de una ruta que sale ese día, si no su asignación.
CREATE FUNCTION negocio.unidad_de_persona_en(o uuid, s uuid, p_persona uuid, p_instante timestamptz, p_servicio text DEFAULT NULL) RETURNS uuid
LANGUAGE sql STABLE SECURITY DEFINER SET search_path = pg_catalog AS $$
    WITH dia AS (SELECT (p_instante AT TIME ZONE 'America/Monterrey')::date AS d)
    SELECT v.id FROM negocio.vehiculo v, dia
    WHERE v.organizacion_id = o AND v.sucursal_id = s AND negocio.persona_opera_unidad(o, s, v.id, p_persona, p_instante)
    ORDER BY coalesce((SELECT x.clasificacion FROM negocio.ficha_vehiculo x WHERE (x.organizacion_id, x.sucursal_id, x.vehiculo_id) = (o, s, v.id) ORDER BY x.revision DESC LIMIT 1) = p_servicio, false) DESC,
             EXISTS (SELECT 1 FROM negocio.cambio_unidad_dia c WHERE (c.organizacion_id, c.sucursal_id, c.vehiculo_id, c.fecha) = (o, s, v.id, dia.d)) DESC,
             EXISTS (SELECT 1 FROM negocio.ruta r
                     JOIN LATERAL (SELECT x.vehiculo_id, x.dias FROM negocio.ruta_version x
                         WHERE (x.organizacion_id, x.sucursal_id, x.ruta_id) = (r.organizacion_id, r.sucursal_id, r.id) ORDER BY x.revision DESC LIMIT 1) rv ON true
                     WHERE (r.organizacion_id, r.sucursal_id) = (o, s) AND rv.vehiculo_id = v.id
                       AND (rv.dias & (1 << (extract(isodow FROM dia.d)::int - 1))) <> 0) DESC,
             v.id
    LIMIT 1
$$;
REVOKE ALL ON FUNCTION negocio.unidad_de_persona_en(uuid, uuid, uuid, timestamptz, text) FROM PUBLIC;

-- Si la jornada es del día anterior al instante «ancla» (la primera captura de servicio de esa orden o parada; si no hay, la actual)
-- y la persona tenía esa ruta ayer, devuelve la unidad en que se cuenta lo hecho. NULL = no es un pendiente.
CREATE FUNCTION negocio.unidad_para_pendiente(o uuid, s uuid, p_persona uuid, p_jornada uuid, p_objetivo uuid, p_instante timestamptz) RETURNS uuid
LANGUAGE plpgsql STABLE SECURITY DEFINER SET search_path = pg_catalog AS $$
DECLARE j record; ancla timestamptz; corte timestamptz; veh_ayer uuid; persona_ayer uuid; servicio text;
BEGIN
    SELECT x.ruta_id, x.fecha INTO j FROM negocio.jornada x WHERE (x.organizacion_id, x.sucursal_id, x.id) = (o, s, p_jornada);
    IF NOT FOUND THEN RETURN NULL; END IF;
    SELECT min(k.ocurrida_en) INTO ancla
    FROM negocio.captura_trabajo t JOIN negocio.captura k ON (k.organizacion_id, k.sucursal_id, k.id) = (t.organizacion_id, t.sucursal_id, t.captura_id)
    WHERE (t.organizacion_id, t.sucursal_id, t.jornada_id, t.orden_derivada_id, t.persona_id) = (o, s, p_jornada, p_objetivo, p_persona)
      AND t.tipo IN ('SERVICIO_OPERADOR', 'SERVICIO_SANITARIOS');
    ancla := coalesce(ancla, p_instante, clock_timestamp());
    IF (ancla AT TIME ZONE 'America/Monterrey')::date <> j.fecha + 1 THEN RETURN NULL; END IF;

    corte := ((j.fecha + 1)::timestamp AT TIME ZONE 'America/Monterrey');
    SELECT coalesce(cu.vehiculo_id, v.vehiculo_id), cu.persona_id INTO veh_ayer, persona_ayer
    FROM negocio.ruta_version v LEFT JOIN LATERAL negocio.cambio_unidad_en(o, s, j.ruta_id, j.fecha) cu ON true
    WHERE (v.organizacion_id, v.sucursal_id, v.ruta_id) = (o, s, j.ruta_id) AND v.registrada_en < corte
    ORDER BY v.revision DESC LIMIT 1;
    IF veh_ayer IS NULL OR coalesce(persona_ayer, negocio.persona_de_unidad_en(o, s, veh_ayer, corte)) IS DISTINCT FROM p_persona THEN RETURN NULL; END IF;
    -- La captura de sanitarios y la orden de fosa llevan su propio servicio.
    servicio := CASE WHEN EXISTS (SELECT 1 FROM negocio.parada x WHERE (x.organizacion_id, x.sucursal_id, x.id) = (o, s, p_objetivo)) THEN 'sanitarios' ELSE 'fosas' END;
    RETURN negocio.unidad_de_persona_en(o, s, p_persona, ancla, servicio);
END $$;
REVOKE ALL ON FUNCTION negocio.unidad_para_pendiente(uuid, uuid, uuid, uuid, uuid, timestamptz) FROM PUBLIC;

ALTER FUNCTION negocio.resolver_orden_derivada(uuid, uuid, uuid, uuid, uuid, timestamptz) RENAME TO resolver_orden_derivada_del_dia;

CREATE FUNCTION negocio.resolver_orden_derivada(o uuid, s uuid, p_persona uuid, p_jornada uuid, p_orden uuid, p_instante timestamptz DEFAULT NULL)
RETURNS TABLE (parada_id uuid, cliente_id uuid, vuelta integer, vehiculo_id uuid, autorizado boolean)
LANGUAGE plpgsql STABLE SECURITY DEFINER SET search_path = pg_catalog AS $$
#variable_conflict use_column
DECLARE veh uuid; jo record; corte timestamptz;
BEGIN
    veh := negocio.unidad_para_pendiente(o, s, p_persona, p_jornada, p_orden, p_instante);
    IF veh IS NOT NULL THEN
        SELECT x.ruta_id, x.fecha INTO jo FROM negocio.jornada x WHERE (x.organizacion_id, x.sucursal_id, x.id) = (o, s, p_jornada);
        corte := ((jo.fecha + 1)::timestamp AT TIME ZONE 'America/Monterrey');
        -- Órdenes del plan que había al cierre de ayer (las extraordinarias no pasan al día siguiente).
        RETURN QUERY
        SELECT pa.id, pc.cliente_id, n.n, veh,
               EXISTS (SELECT 1 FROM negocio.cliente cl JOIN negocio.autorizacion_empresa au ON (au.organizacion_id, au.empresa_id) = (cl.organizacion_id, cl.empresa_id)
                       WHERE (cl.organizacion_id, cl.sucursal_id, cl.id) = (o, s, pc.cliente_id) AND au.autoridad = 'Medio ambiente' AND au.finalidad = 'Generador')
        FROM negocio.parada pa
        JOIN LATERAL (SELECT x.id FROM negocio.parada_version x
            WHERE (x.organizacion_id, x.sucursal_id, x.parada_id) = (pa.organizacion_id, pa.sucursal_id, pa.id) AND x.registrada_en < corte
            ORDER BY x.revision DESC LIMIT 1) pv ON true
        JOIN negocio.parada_cliente pc ON (pc.organizacion_id, pc.sucursal_id, pc.version_id) = (o, s, pv.id)
        CROSS JOIN LATERAL generate_series(1, coalesce((SELECT af.fosas FROM negocio.ajuste_fosas_dia af
            WHERE (af.organizacion_id, af.sucursal_id, af.parada_id, af.cliente_id, af.fecha) = (o, s, pa.id, pc.cliente_id, jo.fecha)
            ORDER BY af.revision DESC LIMIT 1), pc.fosas_planeadas, 0)) AS n(n)
        WHERE (pa.organizacion_id, pa.sucursal_id, pa.ruta_id) = (o, s, jo.ruta_id)
          AND NOT EXISTS (SELECT 1 FROM negocio.parada_baja pb WHERE (pb.organizacion_id, pb.sucursal_id, pb.parada_id) = (pa.organizacion_id, pa.sucursal_id, pa.id) AND pb.dada_de_baja_en < corte)
          AND md5(p_jornada::text || ':' || pa.id::text || ':' || pc.cliente_id::text || ':' || n.n)::uuid = p_orden
        LIMIT 1;
        IF FOUND THEN RETURN; END IF;
    END IF;
    RETURN QUERY SELECT * FROM negocio.resolver_orden_derivada_del_dia(o, s, p_persona, p_jornada, p_orden, p_instante);
END $$;
REVOKE ALL ON FUNCTION negocio.resolver_orden_derivada(uuid, uuid, uuid, uuid, uuid, timestamptz) FROM PUBLIC;

ALTER FUNCTION negocio.resolver_parada_derivada(uuid, uuid, uuid, uuid, uuid, timestamptz) RENAME TO resolver_parada_derivada_del_dia;

CREATE FUNCTION negocio.resolver_parada_derivada(o uuid, s uuid, p_persona uuid, p_jornada uuid, p_parada uuid, p_fecha timestamptz)
RETURNS TABLE (parada_id uuid, sitio_id uuid, ruta_id uuid, vehiculo_id uuid)
LANGUAGE plpgsql STABLE SECURITY DEFINER SET search_path = pg_catalog AS $$
#variable_conflict use_column
DECLARE veh uuid; jo record; corte timestamptz;
BEGIN
    veh := negocio.unidad_para_pendiente(o, s, p_persona, p_jornada, p_parada, p_fecha);
    IF veh IS NOT NULL THEN
        SELECT x.ruta_id, x.fecha INTO jo FROM negocio.jornada x WHERE (x.organizacion_id, x.sucursal_id, x.id) = (o, s, p_jornada);
        corte := ((jo.fecha + 1)::timestamp AT TIME ZONE 'America/Monterrey');
        RETURN QUERY
        SELECT pa.id, pv.sitio_id, jo.ruta_id, veh
        FROM negocio.parada pa
        JOIN LATERAL (SELECT x.sitio_id FROM negocio.parada_version x
            WHERE (x.organizacion_id, x.sucursal_id, x.parada_id) = (pa.organizacion_id, pa.sucursal_id, pa.id) AND x.registrada_en < corte
            ORDER BY x.revision DESC LIMIT 1) pv ON true
        WHERE (pa.organizacion_id, pa.sucursal_id, pa.ruta_id) = (o, s, jo.ruta_id) AND pa.id = p_parada;
        IF FOUND THEN RETURN; END IF;
    END IF;
    RETURN QUERY SELECT * FROM negocio.resolver_parada_derivada_del_dia(o, s, p_persona, p_jornada, p_parada, p_fecha);
END $$;
REVOKE ALL ON FUNCTION negocio.resolver_parada_derivada(uuid, uuid, uuid, uuid, uuid, timestamptz) FROM PUBLIC;

-- Administración (0045): solo el administrador ve y cambia el ajuste. Cambiar el general lo aplica a TODOS (las cuentas con ajuste propio
-- pasan al mismo valor); después se puede encender o apagar cuenta por cuenta. La confirmación con Google la exige la API.
CREATE FUNCTION seguridad.admin_pendientes_ayer(o uuid)
RETURNS TABLE (acceso_id uuid, activo boolean)
LANGUAGE plpgsql STABLE SECURITY DEFINER SET search_path = pg_catalog AS $$
BEGIN
    PERFORM seguridad.exigir_administrador(o);
    -- La fila con acceso_id NULL es el ajuste general; las demás, cada cuenta de teléfono activa con su valor efectivo.
    RETURN QUERY
    SELECT NULL::uuid, coalesce((SELECT x.activo FROM seguridad.pendientes_ayer_ajuste x WHERE x.organizacion_id = o AND x.acceso_id IS NULL ORDER BY x.revision DESC LIMIT 1), true)
    UNION ALL
    SELECT z.id, seguridad.pendientes_ayer_activo(o, z.id) FROM seguridad.acceso_autorizado z
    WHERE z.organizacion_id = o AND z.rol_codigo = 'telefono_operador' AND z.activo;
END $$;
REVOKE ALL ON FUNCTION seguridad.admin_pendientes_ayer(uuid) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION seguridad.admin_pendientes_ayer(uuid) TO manifiestos_app;

CREATE FUNCTION seguridad.admin_guardar_pendientes_ayer(o uuid, p_acceso uuid, p_activo boolean) RETURNS void
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path = pg_catalog AS $$
BEGIN
    PERFORM seguridad.exigir_administrador(o);
    IF p_activo IS NULL THEN RAISE EXCEPTION 'valor_invalido' USING ERRCODE = 'P0422'; END IF;
    PERFORM pg_advisory_xact_lock(hashtextextended('pendientes_ayer:' || o::text, 0));
    IF p_acceso IS NOT NULL THEN
        IF NOT EXISTS (SELECT 1 FROM seguridad.acceso_autorizado z WHERE z.organizacion_id = o AND z.id = p_acceso AND z.rol_codigo = 'telefono_operador') THEN
            RAISE EXCEPTION 'cuenta_no_encontrada' USING ERRCODE = 'P0002';
        END IF;
        IF seguridad.pendientes_ayer_activo(o, p_acceso) = p_activo THEN RETURN; END IF;
        INSERT INTO seguridad.pendientes_ayer_ajuste (organizacion_id, acceso_id, revision, activo, registrado_por)
        SELECT o, p_acceso, coalesce(max(x.revision), 0) + 1, p_activo, seguridad.contexto_acceso()
        FROM seguridad.pendientes_ayer_ajuste x WHERE x.organizacion_id = o AND x.acceso_id = p_acceso;
        RETURN;
    END IF;
    INSERT INTO seguridad.pendientes_ayer_ajuste (organizacion_id, acceso_id, revision, activo, registrado_por)
    SELECT o, NULL, coalesce(max(x.revision), 0) + 1, p_activo, seguridad.contexto_acceso()
    FROM seguridad.pendientes_ayer_ajuste x WHERE x.organizacion_id = o AND x.acceso_id IS NULL;
    INSERT INTO seguridad.pendientes_ayer_ajuste (organizacion_id, acceso_id, revision, activo, registrado_por)
    SELECT o, u.acceso_id, u.revision + 1, p_activo, seguridad.contexto_acceso()
    FROM (SELECT DISTINCT ON (x.acceso_id) x.acceso_id, x.revision, x.activo FROM seguridad.pendientes_ayer_ajuste x
          WHERE x.organizacion_id = o AND x.acceso_id IS NOT NULL ORDER BY x.acceso_id, x.revision DESC) u
    WHERE u.activo <> p_activo;
END $$;
REVOKE ALL ON FUNCTION seguridad.admin_guardar_pendientes_ayer(uuid, uuid, boolean) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION seguridad.admin_guardar_pendientes_ayer(uuid, uuid, boolean) TO manifiestos_app;
