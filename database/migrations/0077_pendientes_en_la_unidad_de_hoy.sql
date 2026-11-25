-- Pendientes de ayer (decisión 0045): los baños pendientes no sumaban a la barra de carga cuando el operador andaba en una unidad de fosas
-- que también carga baños: se elegía antes su unidad de sanitarios aunque ese día no saliera. Ahora manda la unidad que trae HOY.
--   * Una orden de fosa sigue exigiendo una unidad de fosas (las unidades de 1 m³ y las de fosas no se cruzan para órdenes).
--   * Después: la que le dio un cambio del día, la de una ruta que sale hoy y, entre iguales, la del mismo servicio.

CREATE OR REPLACE FUNCTION negocio.unidad_de_persona_en(o uuid, s uuid, p_persona uuid, p_instante timestamptz, p_servicio text DEFAULT NULL) RETURNS uuid
LANGUAGE sql STABLE SECURITY DEFINER SET search_path = pg_catalog AS $$
    WITH dia AS (SELECT (p_instante AT TIME ZONE 'America/Monterrey')::date AS d)
    SELECT v.id FROM negocio.vehiculo v
    CROSS JOIN dia
    LEFT JOIN LATERAL (SELECT x.clasificacion FROM negocio.ficha_vehiculo x
        WHERE (x.organizacion_id, x.sucursal_id, x.vehiculo_id) = (o, s, v.id) ORDER BY x.revision DESC LIMIT 1) f ON true
    WHERE v.organizacion_id = o AND v.sucursal_id = s AND negocio.persona_opera_unidad(o, s, v.id, p_persona, p_instante)
    ORDER BY coalesce(p_servicio = 'fosas' AND f.clasificacion = 'fosas', false) DESC,
             EXISTS (SELECT 1 FROM negocio.cambio_unidad_dia c WHERE (c.organizacion_id, c.sucursal_id, c.vehiculo_id, c.fecha) = (o, s, v.id, dia.d)) DESC,
             EXISTS (SELECT 1 FROM negocio.ruta r
                     JOIN LATERAL (SELECT x.vehiculo_id, x.dias FROM negocio.ruta_version x
                         WHERE (x.organizacion_id, x.sucursal_id, x.ruta_id) = (r.organizacion_id, r.sucursal_id, r.id) ORDER BY x.revision DESC LIMIT 1) rv ON true
                     WHERE (r.organizacion_id, r.sucursal_id) = (o, s) AND rv.vehiculo_id = v.id
                       AND (rv.dias & (1 << (extract(isodow FROM dia.d)::int - 1))) <> 0) DESC,
             coalesce(f.clasificacion = p_servicio, false) DESC,
             v.id
    LIMIT 1
$$;

-- Monitoreo muestra cada pendiente DENTRO de la ruta que hoy lleva quien lo tiene: la lectura del panel devuelve además a la persona.
DO $migracion$
DECLARE d text; n text;
BEGIN
    d := pg_get_functiondef('seguridad.pendientes_ayer_panel(uuid,date)'::regprocedure);
    n := replace(d, 'no_se_hara boolean)', 'no_se_hara boolean, persona_id uuid)');
    IF n = d THEN RAISE EXCEPTION 'No se encontraron las columnas de pendientes_ayer_panel'; END IF;
    d := n;
    n := regexp_replace(d, '(ORDER BY d\.revision DESC LIMIT 1\), false\))(\s+FROM items i)', '\1, p.persona_id\2');
    IF n = d THEN RAISE EXCEPTION 'No se encontró el final de la lista en pendientes_ayer_panel'; END IF;
    DROP FUNCTION seguridad.pendientes_ayer_panel(uuid, date);
    EXECUTE n;
END
$migracion$;
REVOKE ALL ON FUNCTION seguridad.pendientes_ayer_panel(uuid, date) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION seguridad.pendientes_ayer_panel(uuid, date) TO manifiestos_app;
