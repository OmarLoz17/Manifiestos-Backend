DROP POLICY lectura ON negocio.organizacion;
CREATE POLICY lectura ON negocio.organizacion FOR SELECT TO manifiestos_app USING (
    id=seguridad.contexto_org() AND EXISTS (SELECT 1 FROM seguridad.acceso_autorizado a
    WHERE a.organizacion_id=organizacion.id AND a.id=seguridad.contexto_acceso() AND a.activo));

-- Reservar la clave ANTES de aplicar efectos; mantener la misma transacción hasta
-- insertar comando/resultado y tareas. NULL = primera ejecución; objeto = reintento.
CREATE FUNCTION sync.resultado_previo(o uuid,s uuid,i uuid,c uuid,contenido bytea) RETURNS jsonb
LANGUAGE plpgsql SECURITY INVOKER AS $$
DECLARE previo sync.comando%ROWTYPE;
BEGIN
    IF NOT coalesce(seguridad.puede(o,s,'datos.escribir'),false) THEN
        RAISE EXCEPTION 'Contexto no autorizado' USING ERRCODE='42501';
    END IF;
    PERFORM pg_advisory_xact_lock(hashtextextended(o::text||s::text||i::text||c::text,0));
    SELECT * INTO previo FROM sync.comando WHERE organizacion_id=o AND sucursal_id=s AND instalacion_app_id=i AND id=c;
    IF FOUND THEN
        IF previo.solicitud IS DISTINCT FROM contenido THEN
            RAISE EXCEPTION 'Clave de idempotencia reutilizada con contenido diferente' USING ERRCODE='23514';
        END IF;
        RETURN previo.resultado;
    END IF;
    RETURN NULL;
END $$;
REVOKE ALL ON FUNCTION sync.resultado_previo(uuid,uuid,uuid,uuid,bytea) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION sync.resultado_previo(uuid,uuid,uuid,uuid,bytea) TO manifiestos_app;

-- Nombres de índices reproducibles, independientes del OID asignado al restaurar.
DO $$
DECLARE r record;
BEGIN
    FOR r IN SELECT schemaname,indexname FROM pg_indexes WHERE schemaname IN ('negocio','seguridad','sync') AND indexname ~ '^fk_[0-9]+_' LOOP
        EXECUTE format('ALTER INDEX %I.%I RENAME TO %I',r.schemaname,r.indexname,
            'fk_'||substr(md5(r.schemaname||regexp_replace(r.indexname,'^fk_[0-9]+_','')),1,24));
    END LOOP;
END $$;

