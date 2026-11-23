-- Avisos push cuando cambia el operador de una unidad (23-nov-2026, hallado en pruebas con dos teléfonos: «al cambiar el operador de una
-- unidad no se actualiza la ruta de hoy en el teléfono»). Dos causas:
--  1. Cambiar el operador desde Unidades (PUT /api/unidades/{id}/operador) no avisaba a nadie: el aviso solo existía para /api/rutas.
--  2. El aviso de /api/rutas preguntaba «¿sale hoy?» con una consulta directa a negocio.ruta_version desde el rol de la API SIN contexto de
--     usuario: las políticas de seguridad de filas no devolvían ninguna fila y el aviso nunca se enviaba.
-- Además, quien dejaba la unidad no se enteraba nunca (el aviso solo iba al operador vigente): su teléfono conservaba la ruta hasta la
-- siguiente consulta periódica. Ahora el aviso va al operador nuevo y al anterior si el cambio es reciente (10 minutos).

-- ¿La ruta sale ese día de la semana (1 = lunes ... 7 = domingo) según su última revisión? Sin depender del contexto de usuario.
CREATE FUNCTION seguridad.ruta_sale_hoy(o uuid, p_ruta uuid, p_dia integer) RETURNS boolean
LANGUAGE sql STABLE SECURITY DEFINER SET search_path = pg_catalog AS $$
    SELECT coalesce((SELECT (rv.dias & (1 << (p_dia - 1))) <> 0 FROM negocio.ruta_version rv
                     WHERE rv.organizacion_id = o AND rv.ruta_id = p_ruta ORDER BY rv.revision DESC LIMIT 1), false)
$$;

-- Personas a avisar por una unidad: el operador vigente y, si el cambio es de hace menos de 10 minutos, el que acaba de salir.
CREATE FUNCTION seguridad.personas_a_avisar_de_unidad(o uuid, s uuid, v uuid) RETURNS uuid[]
LANGUAGE sql STABLE SECURITY DEFINER SET search_path = pg_catalog AS $$
    WITH ult AS (
        SELECT au.persona_id, au.revision, au.registrada_en FROM negocio.asignacion_unidad au
        WHERE (au.organizacion_id, au.sucursal_id, au.vehiculo_id) = (o, s, v) ORDER BY au.revision DESC LIMIT 2)
    SELECT coalesce(array_agg(DISTINCT x.persona_id) FILTER (WHERE x.persona_id IS NOT NULL), '{}'::uuid[])
    FROM (
        (SELECT persona_id FROM ult ORDER BY revision DESC LIMIT 1)
        UNION ALL
        (SELECT persona_id FROM ult ORDER BY revision DESC OFFSET 1 LIMIT 1)
    ) x
    WHERE x.persona_id IS NOT NULL
      AND (x.persona_id = (SELECT persona_id FROM ult ORDER BY revision DESC LIMIT 1)
           OR (SELECT registrada_en FROM ult ORDER BY revision DESC LIMIT 1) > clock_timestamp() - interval '10 minutes')
$$;

-- Tokens de los teléfonos cuyo equipo está hoy entregado a alguna de esas personas.
CREATE FUNCTION seguridad.tokens_de_personas(o uuid, p_personas uuid[]) RETURNS TABLE(token text)
LANGUAGE sql STABLE SECURITY DEFINER SET search_path = pg_catalog AS $$
    SELECT DISTINCT tp.token
    FROM seguridad.asignacion_equipo ae
    JOIN seguridad.instalacion_app ins ON ins.organizacion_id = o AND ins.dispositivo_id = ae.dispositivo_id AND ins.habilitada
    JOIN seguridad.token_push tp ON tp.organizacion_id = o AND tp.instalacion_id = ins.id
    WHERE ae.organizacion_id = o AND ae.persona_id = ANY (p_personas) AND ae.desde <= now() AND (ae.hasta IS NULL OR ae.hasta > now())
      AND ae.revision = (SELECT max(x.revision) FROM seguridad.asignacion_equipo x WHERE x.organizacion_id = o AND x.dispositivo_id = ae.dispositivo_id)
$$;

-- Destinatarios de un cambio de la ruta (misma firma que 0037): ahora incluye al operador que acaba de salir de la unidad.
CREATE OR REPLACE FUNCTION seguridad.destinatarios_aviso_ruta(o uuid, ruta uuid) RETURNS TABLE(token text)
LANGUAGE sql STABLE SECURITY DEFINER SET search_path = pg_catalog AS $$
    SELECT t.token
    FROM (SELECT r.sucursal_id, r.vehiculo_id FROM negocio.ruta_version r WHERE r.organizacion_id = o AND r.ruta_id = ruta ORDER BY r.revision DESC LIMIT 1) rv,
         LATERAL seguridad.tokens_de_personas(o, seguridad.personas_a_avisar_de_unidad(o, rv.sucursal_id, rv.vehiculo_id)) t
$$;

-- Destinatarios de un cambio de operador de la unidad: solo si alguna de sus rutas sale ese día de la semana.
CREATE FUNCTION seguridad.destinatarios_aviso_cambio_operador(o uuid, p_unidad uuid, p_dia integer) RETURNS TABLE(token text)
LANGUAGE sql STABLE SECURITY DEFINER SET search_path = pg_catalog AS $$
    SELECT t.token
    FROM (SELECT v.sucursal_id FROM negocio.vehiculo v WHERE v.organizacion_id = o AND v.id = p_unidad LIMIT 1) veh,
         LATERAL seguridad.tokens_de_personas(o, seguridad.personas_a_avisar_de_unidad(o, veh.sucursal_id, p_unidad)) t
    WHERE EXISTS (
        SELECT 1 FROM negocio.ruta ru
        JOIN LATERAL (SELECT x.dias, x.vehiculo_id FROM negocio.ruta_version x
            WHERE (x.organizacion_id, x.sucursal_id, x.ruta_id) = (ru.organizacion_id, ru.sucursal_id, ru.id) ORDER BY x.revision DESC LIMIT 1) rv ON rv.vehiculo_id = p_unidad
        WHERE ru.organizacion_id = o AND (rv.dias & (1 << (p_dia - 1))) <> 0)
$$;

REVOKE ALL ON FUNCTION seguridad.ruta_sale_hoy(uuid,uuid,integer) FROM PUBLIC;
REVOKE ALL ON FUNCTION seguridad.personas_a_avisar_de_unidad(uuid,uuid,uuid) FROM PUBLIC;
REVOKE ALL ON FUNCTION seguridad.tokens_de_personas(uuid,uuid[]) FROM PUBLIC;
REVOKE ALL ON FUNCTION seguridad.destinatarios_aviso_cambio_operador(uuid,uuid,integer) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION seguridad.ruta_sale_hoy(uuid,uuid,integer) TO manifiestos_app;
GRANT EXECUTE ON FUNCTION seguridad.destinatarios_aviso_cambio_operador(uuid,uuid,integer) TO manifiestos_app;
-- personas_a_avisar_de_unidad y tokens_de_personas son internas: solo las llaman otras funciones definidas por el propietario.
