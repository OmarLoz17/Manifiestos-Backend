-- Ruta mínima (RF de Órdenes › Fosas fijas). Sigue el UML: Ruta agrupa el trabajo
-- de una sucursal en una fecha; AsignacionRuta registra operador y unidad con revisión.
-- PG-10 sigue abierta: no hay estados, cancelación, reordenamiento ni vigencias.
-- Mientras no exista un estado, toda ruta se presenta como "Borrador".
-- Paradas (Parada/ParadaOrden) quedan para el siguiente paso.

CREATE TABLE negocio.ruta (
    organizacion_id uuid NOT NULL, sucursal_id uuid NOT NULL,
    id uuid NOT NULL DEFAULT gen_random_uuid(),
    -- Nombre operativo visible; sin UNIQUE: no está confirmado que no se repita.
    nombre text NOT NULL CHECK (btrim(nombre) <> '' AND length(nombre) <= 120),
    fecha_planificada date NOT NULL,
    creada_en timestamptz NOT NULL DEFAULT clock_timestamp(),
    -- Quién la creó: lo fija el contexto del servidor, no el cliente.
    creada_por_acceso_id uuid DEFAULT seguridad.contexto_acceso(),
    PRIMARY KEY (organizacion_id, sucursal_id, id),
    FOREIGN KEY (organizacion_id, sucursal_id) REFERENCES negocio.sucursal ON DELETE RESTRICT,
    FOREIGN KEY (organizacion_id, creada_por_acceso_id) REFERENCES seguridad.acceso_autorizado ON DELETE RESTRICT
);

-- Operador = persona (el Gmail del teléfono no es la persona). Unidad = ficha de
-- vehículo vigente al asignar, para conservar capacidad/placas de ese momento.
-- Un cambio de operador o unidad será una revisión nueva, nunca un UPDATE.
CREATE TABLE negocio.asignacion_ruta (
    organizacion_id uuid NOT NULL, sucursal_id uuid NOT NULL,
    id uuid NOT NULL DEFAULT gen_random_uuid(),
    ruta_id uuid NOT NULL,
    revision integer NOT NULL CHECK (revision > 0),
    persona_id uuid NOT NULL,
    ficha_vehiculo_id uuid NOT NULL,
    registrada_en timestamptz NOT NULL DEFAULT clock_timestamp(),
    registrada_por_acceso_id uuid DEFAULT seguridad.contexto_acceso(),
    PRIMARY KEY (organizacion_id, sucursal_id, id),
    UNIQUE (organizacion_id, sucursal_id, ruta_id, revision),
    FOREIGN KEY (organizacion_id, sucursal_id, ruta_id) REFERENCES negocio.ruta ON DELETE RESTRICT,
    FOREIGN KEY (organizacion_id, persona_id) REFERENCES seguridad.persona ON DELETE RESTRICT,
    FOREIGN KEY (organizacion_id, sucursal_id, ficha_vehiculo_id) REFERENCES negocio.ficha_vehiculo ON DELETE RESTRICT,
    FOREIGN KEY (organizacion_id, registrada_por_acceso_id) REFERENCES seguridad.acceso_autorizado ON DELETE RESTRICT
);

CREATE INDEX ruta_fecha ON negocio.ruta (organizacion_id, sucursal_id, fecha_planificada);
CREATE INDEX ruta_creada_por ON negocio.ruta (organizacion_id, creada_por_acceso_id);
CREATE INDEX asignacion_ruta_persona ON negocio.asignacion_ruta (organizacion_id, persona_id);
CREATE INDEX asignacion_ruta_ficha ON negocio.asignacion_ruta (organizacion_id, sucursal_id, ficha_vehiculo_id);
CREATE INDEX asignacion_ruta_registrada_por ON negocio.asignacion_ruta (organizacion_id, registrada_por_acceso_id);

DO $$
DECLARE t text;
BEGIN
    FOREACH t IN ARRAY ARRAY['ruta','asignacion_ruta'] LOOP
        EXECUTE format('CREATE TRIGGER conservar_historia BEFORE UPDATE OR DELETE ON negocio.%I FOR EACH ROW EXECUTE FUNCTION infra.inmutable()',t);
        EXECUTE format('ALTER TABLE negocio.%I ENABLE ROW LEVEL SECURITY',t);
        EXECUTE format('CREATE POLICY contexto ON negocio.%I TO manifiestos_app USING (seguridad.puede(organizacion_id,sucursal_id,''datos.leer'')) WITH CHECK (seguridad.puede(organizacion_id,sucursal_id,''datos.escribir''))',t);
        EXECUTE format('GRANT SELECT, INSERT ON negocio.%I TO manifiestos_app',t);
    END LOOP;
END $$;

-- Una persona asignada a una ruta visible también es visible (para mostrar su nombre).
CREATE OR REPLACE FUNCTION seguridad.persona_visible(o uuid, p uuid) RETURNS boolean
LANGUAGE sql STABLE SECURITY DEFINER SET search_path = pg_catalog AS $$
    SELECT EXISTS (SELECT 1 FROM seguridad.asignacion_equipo a WHERE a.organizacion_id=o AND a.persona_id=p
        AND seguridad.puede(o,a.sucursal_id,'datos.leer'))
    OR EXISTS (SELECT 1 FROM negocio.asignacion_ruta a WHERE a.organizacion_id=o AND a.persona_id=p
        AND seguridad.puede(o,a.sucursal_id,'datos.leer'))
$$;

-- Operadores elegibles al planear una ruta de la sucursal s. PROVISIONAL: todavía no
-- existe adscripción persona→sucursal ni cargo (Empleado en el UML), así que se
-- devuelven las personas de la organización a quien puede escribir en esa sucursal.
-- No expone correos, Gmail ni dispositivos.
CREATE FUNCTION seguridad.operadores_para_ruta(o uuid, s uuid)
RETURNS TABLE (persona_id uuid, nombre_completo text)
LANGUAGE sql STABLE SECURITY DEFINER SET search_path = pg_catalog AS $$
    SELECT p.id, p.nombre_completo FROM seguridad.persona p
    WHERE p.organizacion_id = o AND seguridad.puede(o, s, 'datos.escribir')
    ORDER BY p.nombre_completo
$$;
REVOKE ALL ON FUNCTION seguridad.operadores_para_ruta(uuid, uuid) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION seguridad.operadores_para_ruta(uuid, uuid) TO manifiestos_app;
