CREATE FUNCTION infra.inmutable() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    RAISE EXCEPTION 'Registro histórico inmutable: %.%', TG_TABLE_SCHEMA, TG_TABLE_NAME USING ERRCODE = '23514';
END $$;
CREATE FUNCTION infra.solo_cierre_asignacion() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF TG_OP = 'UPDATE' AND OLD.hasta IS NULL AND NEW.hasta IS NOT NULL
       AND (to_jsonb(NEW) - 'hasta') = (to_jsonb(OLD) - 'hasta') THEN
        RETURN NEW;
    END IF;
    RAISE EXCEPTION 'La asignación solo admite registrar su cierre una vez' USING ERRCODE = '23514';
END $$;
DO $$
DECLARE t text;
BEGIN
    FOREACH t IN ARRAY ARRAY[
        'negocio.empresa','negocio.sucursal','negocio.cliente','negocio.sitio','negocio.vehiculo',
        'negocio.ficha_empresa','negocio.ficha_sitio','negocio.ficha_vehiculo','negocio.autorizacion_empresa',
        'catalogo.capacidad_vehiculo','seguridad.persona','seguridad.dispositivo',
        'negocio.orden_servicio','negocio.orden_version','negocio.captura','negocio.posicion_captura',
        'negocio.archivo','negocio.contenido_aceptado','negocio.firma_generador','sync.comando','sync.conflicto'
    ] LOOP
        EXECUTE format('CREATE TRIGGER conservar_historia BEFORE UPDATE OR DELETE ON %s FOR EACH ROW EXECUTE FUNCTION infra.inmutable()',t);
    END LOOP;
END $$;
CREATE TRIGGER conservar_asignacion BEFORE UPDATE OR DELETE ON seguridad.asignacion_equipo
FOR EACH ROW EXECUTE FUNCTION infra.solo_cierre_asignacion();
-- FK de identidad no puede reatribuirse después del primer enlace.
CREATE FUNCTION infra.conservar_identidad() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF TG_TABLE_NAME = 'acceso_autorizado' THEN
        IF NEW.organizacion_id <> OLD.organizacion_id OR NEW.id <> OLD.id OR
           (OLD.identidad_id IS NOT NULL AND NEW.identidad_id IS DISTINCT FROM OLD.identidad_id) THEN
            RAISE EXCEPTION 'No se permite reatribuir la identidad' USING ERRCODE='23514';
        END IF;
    ELSIF TG_TABLE_NAME = 'identidad_externa' THEN
        IF (to_jsonb(NEW)-'correo_verificado') <> (to_jsonb(OLD)-'correo_verificado') THEN
            RAISE EXCEPTION 'Emisor y sujeto son estables' USING ERRCODE='23514';
        END IF;
    ELSIF (to_jsonb(NEW)-'habilitada') <> (to_jsonb(OLD)-'habilitada') THEN
        RAISE EXCEPTION 'No se permite cambiar el dispositivo de una instalación' USING ERRCODE='23514';
    END IF;
    RETURN NEW;
END $$;
CREATE TRIGGER identidad_estable BEFORE UPDATE ON seguridad.acceso_autorizado FOR EACH ROW EXECUTE FUNCTION infra.conservar_identidad();
CREATE TRIGGER identidad_estable BEFORE UPDATE ON seguridad.identidad_externa FOR EACH ROW EXECUTE FUNCTION infra.conservar_identidad();
CREATE TRIGGER identidad_estable BEFORE UPDATE ON seguridad.instalacion_app FOR EACH ROW EXECUTE FUNCTION infra.conservar_identidad();

-- Índices de todas las FKs; evita exploraciones completas al comprobar referencias.
DO $$
DECLARE r record; cols text;
BEGIN
    FOR r IN SELECT conrelid, conname, conkey FROM pg_constraint
             WHERE contype='f' AND connamespace IN ('negocio'::regnamespace,'seguridad'::regnamespace,'sync'::regnamespace)
    LOOP
        IF NOT EXISTS (SELECT 1 FROM pg_index i WHERE i.indrelid=r.conrelid
            AND (i.indkey::smallint[])[0:cardinality(r.conkey)-1] @> r.conkey
            AND i.indpred IS NULL) THEN
            SELECT string_agg(quote_ident(a.attname),', ' ORDER BY k.n) INTO cols
            FROM unnest(r.conkey) WITH ORDINALITY k(attnum,n)
            JOIN pg_attribute a ON a.attrelid=r.conrelid AND a.attnum=k.attnum;
            EXECUTE format('CREATE INDEX %I ON %s (%s)', 'fk_' || r.conrelid || '_' || r.conname, r.conrelid::regclass, cols);
        END IF;
    END LOOP;
END $$;

-- Rol sin LOGIN: el backend futuro tendrá su credencial propia, nunca owner.
DO $$ BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname='manifiestos_app') THEN
        CREATE ROLE manifiestos_app NOLOGIN NOSUPERUSER NOBYPASSRLS;
    END IF;
END $$;
GRANT USAGE ON SCHEMA negocio, catalogo, seguridad, sync TO manifiestos_app;
GRANT SELECT ON ALL TABLES IN SCHEMA negocio, catalogo, seguridad, sync TO manifiestos_app;
GRANT INSERT ON negocio.orden_servicio, negocio.orden_version, negocio.captura, negocio.posicion_captura,
    negocio.archivo, negocio.ubicacion_archivo, negocio.contenido_aceptado, negocio.firma_generador,
    sync.comando, sync.conflicto, sync.tarea TO manifiestos_app;
GRANT UPDATE (verificada_en) ON negocio.ubicacion_archivo TO manifiestos_app;
GRANT UPDATE (intentos, disponible_en, reserva_hasta, completada_en) ON sync.tarea TO manifiestos_app;

-- Estos settings son fijados por el servidor tras autenticar, con SET LOCAL.
-- No son credenciales; jamás exponer conexiones SQL al navegador/Android.
CREATE FUNCTION seguridad.contexto_org() RETURNS uuid LANGUAGE sql STABLE AS $$
    SELECT nullif(current_setting('app.organizacion_id',true),'')::uuid
$$;
CREATE FUNCTION seguridad.contexto_acceso() RETURNS uuid LANGUAGE sql STABLE AS $$
    SELECT nullif(current_setting('app.acceso_id',true),'')::uuid
$$;
CREATE FUNCTION seguridad.puede(o uuid, s uuid, p text) RETURNS boolean
LANGUAGE sql STABLE SECURITY DEFINER SET search_path = pg_catalog AS $$
    SELECT o = seguridad.contexto_org() AND EXISTS (
        SELECT 1 FROM seguridad.acceso_autorizado a
        JOIN seguridad.permiso_sucursal x ON x.organizacion_id=a.organizacion_id AND x.acceso_id=a.id
        WHERE a.organizacion_id=o AND a.id=seguridad.contexto_acceso() AND a.activo
        AND x.sucursal_id=s AND x.permiso=p)
$$;
CREATE FUNCTION seguridad.empresa_visible(o uuid, e uuid) RETURNS boolean
LANGUAGE sql STABLE SECURITY DEFINER SET search_path = pg_catalog AS $$
    SELECT EXISTS (SELECT 1 FROM negocio.empresa_sucursal x WHERE x.organizacion_id=o AND x.empresa_id=e
        AND seguridad.puede(o,x.sucursal_id,'datos.leer'))
$$;
CREATE FUNCTION seguridad.equipo_visible(o uuid, d uuid) RETURNS boolean
LANGUAGE sql STABLE SECURITY DEFINER SET search_path = pg_catalog AS $$
    SELECT EXISTS (SELECT 1 FROM seguridad.asignacion_equipo a WHERE a.organizacion_id=o AND a.dispositivo_id=d
        AND seguridad.puede(o,a.sucursal_id,'datos.leer'))
$$;
CREATE FUNCTION seguridad.persona_visible(o uuid, p uuid) RETURNS boolean
LANGUAGE sql STABLE SECURITY DEFINER SET search_path = pg_catalog AS $$
    SELECT EXISTS (SELECT 1 FROM seguridad.asignacion_equipo a WHERE a.organizacion_id=o AND a.persona_id=p
        AND seguridad.puede(o,a.sucursal_id,'datos.leer'))
$$;
DO $$
DECLARE r record; expr text;
BEGIN
    FOR r IN SELECT table_schema, table_name FROM information_schema.tables
             WHERE table_schema IN ('negocio','seguridad','sync') AND table_type='BASE TABLE'
    LOOP
        EXECUTE format('ALTER TABLE %I.%I ENABLE ROW LEVEL SECURITY',r.table_schema,r.table_name);
        IF EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema=r.table_schema AND table_name=r.table_name AND column_name='sucursal_id') THEN
            EXECUTE format('CREATE POLICY contexto ON %I.%I TO manifiestos_app USING (seguridad.puede(organizacion_id,sucursal_id,''datos.leer'')) WITH CHECK (seguridad.puede(organizacion_id,sucursal_id,''datos.escribir''))',r.table_schema,r.table_name);
        ELSE
            expr := CASE r.table_name
                WHEN 'organizacion' THEN 'id=seguridad.contexto_org() AND EXISTS (SELECT 1 FROM seguridad.acceso_autorizado a WHERE a.organizacion_id=id AND a.id=seguridad.contexto_acceso() AND a.activo)'
                WHEN 'empresa' THEN 'seguridad.empresa_visible(organizacion_id,id)'
                WHEN 'ficha_empresa' THEN 'seguridad.empresa_visible(organizacion_id,empresa_id)'
                WHEN 'autorizacion_empresa' THEN 'seguridad.empresa_visible(organizacion_id,empresa_id)'
                WHEN 'sucursal' THEN 'seguridad.puede(organizacion_id,id,''datos.leer'')'
                WHEN 'persona' THEN 'seguridad.persona_visible(organizacion_id,id)'
                WHEN 'dispositivo' THEN 'seguridad.equipo_visible(organizacion_id,id)'
                WHEN 'instalacion_app' THEN 'seguridad.equipo_visible(organizacion_id,dispositivo_id)'
                WHEN 'acceso_autorizado' THEN 'organizacion_id=seguridad.contexto_org() AND id=seguridad.contexto_acceso() AND activo'
                WHEN 'identidad_externa' THEN 'organizacion_id=seguridad.contexto_org() AND EXISTS (SELECT 1 FROM seguridad.acceso_autorizado a WHERE a.organizacion_id=identidad_externa.organizacion_id AND a.identidad_id=identidad_externa.id AND a.id=seguridad.contexto_acceso() AND a.activo)'
            END;
            IF expr IS NULL THEN RAISE EXCEPTION 'Falta política de %',r.table_name; END IF;
            EXECUTE format('CREATE POLICY lectura ON %I.%I FOR SELECT TO manifiestos_app USING (%s)',r.table_schema,r.table_name,expr);
        END IF;
    END LOOP;
END $$;
REVOKE ALL ON ALL FUNCTIONS IN SCHEMA seguridad, infra FROM PUBLIC;
GRANT EXECUTE ON ALL FUNCTIONS IN SCHEMA seguridad TO manifiestos_app;
-- Propietario/migrador conserva bypass para administración y mantenimiento.
-- FORCE RLS no se aplica a las tablas consultadas por los helpers SECURITY DEFINER.

