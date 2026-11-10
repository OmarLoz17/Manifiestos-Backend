-- Canal móvil exclusivo para operadores, con instalación/asignación autorizadas.
-- El ID token se valida con Google ANTES de llamar estas funciones.
ALTER TABLE seguridad.inicio_sesion DROP CONSTRAINT inicio_sesion_canal_check;
ALTER TABLE seguridad.inicio_sesion ADD CHECK (canal IN ('web','movil'));
ALTER TABLE seguridad.inicio_sesion DROP CONSTRAINT inicio_sesion_resultado_check;
ALTER TABLE seguridad.inicio_sesion ADD CHECK (resultado IN
    ('aceptado','no_autorizado','inactivo','sin_acceso_web','sin_alcance','sin_acceso_movil','sin_equipo','desafio_invalido'));

CREATE TABLE seguridad.desafio_movil (
    organizacion_id uuid NOT NULL, id uuid NOT NULL DEFAULT gen_random_uuid(), instalacion_id uuid NOT NULL,
    nonce_sha256 bytea NOT NULL CHECK(octet_length(nonce_sha256)=32),
    vence_en timestamptz NOT NULL DEFAULT (clock_timestamp()+interval '2 minutes'), consumido_en timestamptz,
    PRIMARY KEY(organizacion_id,id),
    FOREIGN KEY(organizacion_id,instalacion_id) REFERENCES seguridad.instalacion_app ON DELETE RESTRICT
);
CREATE TABLE seguridad.sesion_movil (
    organizacion_id uuid NOT NULL, id uuid NOT NULL DEFAULT gen_random_uuid(), acceso_id uuid NOT NULL,
    sucursal_id uuid NOT NULL, instalacion_id uuid NOT NULL, asignacion_id uuid NOT NULL,
    creada_en timestamptz NOT NULL DEFAULT clock_timestamp(),
    vence_en timestamptz NOT NULL DEFAULT (clock_timestamp()+interval '8 hours'), revocada_en timestamptz,
    PRIMARY KEY(organizacion_id,id),
    FOREIGN KEY(organizacion_id,acceso_id) REFERENCES seguridad.acceso_autorizado ON DELETE RESTRICT,
    FOREIGN KEY(organizacion_id,instalacion_id) REFERENCES seguridad.instalacion_app ON DELETE RESTRICT,
    FOREIGN KEY(organizacion_id,sucursal_id,asignacion_id) REFERENCES seguridad.asignacion_equipo ON DELETE RESTRICT
);
ALTER TABLE seguridad.desafio_movil ENABLE ROW LEVEL SECURITY;
ALTER TABLE seguridad.sesion_movil ENABLE ROW LEVEL SECURITY;
CREATE INDEX desafio_movil_instalacion ON seguridad.desafio_movil(organizacion_id,instalacion_id);
CREATE INDEX sesion_movil_acceso ON seguridad.sesion_movil(organizacion_id,acceso_id);
CREATE INDEX sesion_movil_instalacion ON seguridad.sesion_movil(organizacion_id,instalacion_id);
CREATE INDEX sesion_movil_asignacion ON seguridad.sesion_movil(organizacion_id,sucursal_id,asignacion_id);
-- Sin GRANT de tablas ni políticas para la aplicación: acceso mediante funciones acotadas.

CREATE FUNCTION seguridad.contexto_operador(o uuid,a uuid,i uuid)
RETURNS TABLE(sucursal_id uuid,persona_id uuid,nombre text,identidad_id uuid,asignacion_id uuid,revision_equipo integer)
LANGUAGE sql STABLE SECURITY DEFINER SET search_path=pg_catalog AS $$
    SELECT e.sucursal_id,e.persona_id,f.nombre_completo,x.identidad_id,e.id,e.revision
    FROM seguridad.acceso_autorizado x
    JOIN seguridad.instalacion_app ins ON ins.organizacion_id=x.organizacion_id AND ins.id=i AND ins.habilitada
    JOIN LATERAL(SELECT ae.* FROM seguridad.asignacion_equipo ae
        WHERE ae.organizacion_id=o AND ae.dispositivo_id=ins.dispositivo_id ORDER BY ae.revision DESC LIMIT 1) e
        ON e.acceso_id=x.id AND e.desde<=now() AND (e.hasta IS NULL OR e.hasta>now())
    JOIN LATERAL(SELECT fi.* FROM negocio.operador_ficha fi
        WHERE (fi.organizacion_id,fi.sucursal_id,fi.persona_id)=(o,e.sucursal_id,e.persona_id)
        ORDER BY fi.revision DESC LIMIT 1) f ON f.correo=lower(btrim(x.correo_permitido))
    WHERE x.organizacion_id=o AND x.id=a AND x.activo AND x.rol_codigo='telefono_operador'
$$;

CREATE FUNCTION seguridad.crear_desafio_movil(o uuid,i uuid,n bytea) RETURNS uuid
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE d uuid;
BEGIN
    IF EXISTS(SELECT 1 FROM seguridad.instalacion_app ins WHERE ins.organizacion_id=o AND ins.id=i AND ins.habilitada) THEN
        INSERT INTO seguridad.desafio_movil(organizacion_id,instalacion_id,nonce_sha256) VALUES(o,i,n) RETURNING id INTO d;
    END IF;
    RETURN d;
END $$;

CREATE FUNCTION seguridad.iniciar_sesion_movil(o uuid,p_emisor text,p_sujeto text,p_correo text,i uuid,d uuid,n bytea)
RETURNS TABLE(resultado_login text,sesion uuid)
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE v_correo text:=lower(btrim(p_correo)); v_identidad uuid; v_acceso uuid; v_motivo text; v_sesion uuid;
    v_desafio seguridad.desafio_movil%ROWTYPE; v_contexto record;
BEGIN
    IF o IS NULL OR coalesce(btrim(p_emisor),'')='' OR coalesce(btrim(p_sujeto),'')='' OR coalesce(v_correo,'')='' THEN
        RAISE EXCEPTION 'Identidad incompleta' USING ERRCODE='22023';
    END IF;
    SELECT * INTO v_desafio FROM seguridad.desafio_movil x WHERE x.organizacion_id=o AND x.id=d FOR UPDATE;
    IF v_desafio.id IS NULL OR v_desafio.instalacion_id<>i OR v_desafio.vence_en<=clock_timestamp()
       OR v_desafio.consumido_en IS NOT NULL OR v_desafio.nonce_sha256 IS DISTINCT FROM n THEN
        RETURN QUERY SELECT 'desafio_invalido'::text,NULL::uuid; RETURN;
    END IF;
    UPDATE seguridad.desafio_movil SET consumido_en=clock_timestamp() WHERE organizacion_id=o AND id=d;
    -- Mismo lock que el login web: una identidad no puede vincularse dos veces en una carrera entre canales.
    PERFORM pg_advisory_xact_lock(hashtextextended('login'||chr(31)||o::text||chr(31)||p_emisor||chr(31)||p_sujeto,0));
    SELECT id INTO v_identidad FROM seguridad.identidad_externa x WHERE x.organizacion_id=o AND x.emisor=p_emisor AND x.sujeto=p_sujeto;
    IF v_identidad IS NOT NULL THEN
        SELECT id INTO v_acceso FROM seguridad.acceso_autorizado x WHERE x.organizacion_id=o AND x.identidad_id=v_identidad FOR UPDATE;
    END IF;
    IF v_acceso IS NULL THEN
        SELECT id INTO v_acceso FROM seguridad.acceso_autorizado x
        WHERE x.organizacion_id=o AND lower(btrim(x.correo_permitido))=v_correo AND x.identidad_id IS NULL FOR UPDATE;
    END IF;
    SELECT CASE WHEN x.id IS NULL THEN 'no_autorizado' WHEN NOT x.activo THEN 'inactivo'
        WHEN x.rol_codigo IS DISTINCT FROM 'telefono_operador' THEN 'sin_acceso_movil' END INTO v_motivo
    FROM (SELECT 1) uno LEFT JOIN seguridad.acceso_autorizado x ON x.organizacion_id=o AND x.id=v_acceso;
    IF v_motivo IS NULL THEN
        SELECT * INTO v_contexto FROM seguridad.contexto_operador(o,v_acceso,i);
        IF NOT FOUND THEN v_motivo:='sin_equipo'; END IF;
    END IF;
    IF v_motivo IS NULL THEN
        IF v_identidad IS NULL THEN
            INSERT INTO seguridad.identidad_externa(organizacion_id,emisor,sujeto,correo_verificado)
            VALUES(o,p_emisor,p_sujeto,v_correo) RETURNING id INTO v_identidad;
        END IF;
        UPDATE seguridad.acceso_autorizado SET identidad_id=v_identidad WHERE organizacion_id=o AND id=v_acceso AND identidad_id IS NULL;
        UPDATE seguridad.identidad_externa SET correo_verificado=v_correo WHERE organizacion_id=o AND id=v_identidad AND correo_verificado IS DISTINCT FROM v_correo;
        INSERT INTO seguridad.sesion_movil(organizacion_id,acceso_id,sucursal_id,instalacion_id,asignacion_id)
        VALUES(o,v_acceso,v_contexto.sucursal_id,i,v_contexto.asignacion_id) RETURNING id INTO v_sesion;
    END IF;
    INSERT INTO seguridad.inicio_sesion(organizacion_id,canal,resultado,acceso_id,identidad_id)
    VALUES(o,'movil',coalesce(v_motivo,'aceptado'),v_acceso,v_identidad);
    RETURN QUERY SELECT coalesce(v_motivo,'aceptado'),v_sesion;
END $$;

CREATE FUNCTION seguridad.sesion_operador(o uuid,s uuid)
RETURNS TABLE(acceso_id uuid,sucursal_id uuid,persona_id uuid,nombre text,correo text,identidad_id uuid,
    instalacion_id uuid,asignacion_id uuid,revision_equipo integer)
LANGUAGE sql STABLE SECURITY DEFINER SET search_path=pg_catalog AS $$
    SELECT x.acceso_id,c.sucursal_id,c.persona_id,c.nombre,a.correo_permitido,c.identidad_id,x.instalacion_id,c.asignacion_id,c.revision_equipo
    FROM seguridad.sesion_movil x
    JOIN seguridad.acceso_autorizado a ON a.organizacion_id=x.organizacion_id AND a.id=x.acceso_id
    JOIN LATERAL seguridad.contexto_operador(o,x.acceso_id,x.instalacion_id) c ON c.asignacion_id=x.asignacion_id AND c.sucursal_id=x.sucursal_id
    WHERE x.organizacion_id=o AND x.id=s AND x.revocada_en IS NULL AND x.vence_en>now() AND c.identidad_id IS NOT NULL
$$;
CREATE FUNCTION seguridad.cerrar_sesion_movil(o uuid,s uuid) RETURNS void
LANGUAGE sql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
    UPDATE seguridad.sesion_movil SET revocada_en=clock_timestamp() WHERE organizacion_id=o AND id=s AND revocada_en IS NULL
$$;

REVOKE ALL ON FUNCTION seguridad.contexto_operador(uuid,uuid,uuid) FROM PUBLIC;
REVOKE ALL ON FUNCTION seguridad.crear_desafio_movil(uuid,uuid,bytea) FROM PUBLIC;
REVOKE ALL ON FUNCTION seguridad.iniciar_sesion_movil(uuid,text,text,text,uuid,uuid,bytea) FROM PUBLIC;
REVOKE ALL ON FUNCTION seguridad.sesion_operador(uuid,uuid) FROM PUBLIC;
REVOKE ALL ON FUNCTION seguridad.cerrar_sesion_movil(uuid,uuid) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION seguridad.crear_desafio_movil(uuid,uuid,bytea) TO manifiestos_app;
GRANT EXECUTE ON FUNCTION seguridad.iniciar_sesion_movil(uuid,text,text,text,uuid,uuid,bytea) TO manifiestos_app;
GRANT EXECUTE ON FUNCTION seguridad.sesion_operador(uuid,uuid) TO manifiestos_app;
GRANT EXECUTE ON FUNCTION seguridad.cerrar_sesion_movil(uuid,uuid) TO manifiestos_app;
