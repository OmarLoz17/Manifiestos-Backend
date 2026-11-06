CREATE TABLE seguridad.identidad_externa (
    organizacion_id uuid NOT NULL REFERENCES negocio.organizacion ON DELETE RESTRICT,
    id uuid NOT NULL DEFAULT gen_random_uuid(),
    emisor text NOT NULL CHECK (btrim(emisor) <> ''),
    sujeto text NOT NULL CHECK (btrim(sujeto) <> ''),
    correo_verificado text,
    PRIMARY KEY (organizacion_id, id), UNIQUE (organizacion_id, emisor, sujeto)
);
CREATE TABLE seguridad.acceso_autorizado (
    organizacion_id uuid NOT NULL REFERENCES negocio.organizacion ON DELETE RESTRICT,
    id uuid NOT NULL DEFAULT gen_random_uuid(), identidad_id uuid,
    correo_permitido text NOT NULL CHECK (btrim(correo_permitido) <> ''),
    activo boolean NOT NULL DEFAULT false,
    PRIMARY KEY (organizacion_id, id), UNIQUE (organizacion_id, identidad_id),
    FOREIGN KEY (organizacion_id, identidad_id) REFERENCES seguridad.identidad_externa ON DELETE RESTRICT
);
CREATE UNIQUE INDEX acceso_correo ON seguridad.acceso_autorizado (organizacion_id, lower(btrim(correo_permitido)));
CREATE TABLE seguridad.permiso_sucursal (
    organizacion_id uuid NOT NULL, sucursal_id uuid NOT NULL, acceso_id uuid NOT NULL,
    permiso text NOT NULL CHECK (btrim(permiso) <> ''),
    PRIMARY KEY (organizacion_id, sucursal_id, acceso_id, permiso),
    FOREIGN KEY (organizacion_id, sucursal_id) REFERENCES negocio.sucursal ON DELETE RESTRICT,
    FOREIGN KEY (organizacion_id, acceso_id) REFERENCES seguridad.acceso_autorizado ON DELETE RESTRICT
);
CREATE TABLE seguridad.persona (
    organizacion_id uuid NOT NULL REFERENCES negocio.organizacion ON DELETE RESTRICT,
    id uuid NOT NULL DEFAULT gen_random_uuid(), nombre_completo text NOT NULL CHECK (btrim(nombre_completo) <> ''),
    PRIMARY KEY (organizacion_id, id)
);
CREATE TABLE seguridad.dispositivo (
    organizacion_id uuid NOT NULL REFERENCES negocio.organizacion ON DELETE RESTRICT,
    id uuid NOT NULL DEFAULT gen_random_uuid(), codigo_inventario text,
    PRIMARY KEY (organizacion_id, id), UNIQUE (organizacion_id, codigo_inventario)
);
CREATE TABLE seguridad.instalacion_app (
    organizacion_id uuid NOT NULL, id uuid NOT NULL DEFAULT gen_random_uuid(), dispositivo_id uuid NOT NULL,
    habilitada boolean NOT NULL DEFAULT false,
    PRIMARY KEY (organizacion_id, id), UNIQUE (organizacion_id, dispositivo_id, id),
    FOREIGN KEY (organizacion_id, dispositivo_id) REFERENCES seguridad.dispositivo ON DELETE RESTRICT
);
CREATE TABLE seguridad.asignacion_equipo (
    organizacion_id uuid NOT NULL, sucursal_id uuid NOT NULL,
    id uuid NOT NULL DEFAULT gen_random_uuid(), dispositivo_id uuid NOT NULL,
    acceso_id uuid NOT NULL, persona_id uuid NOT NULL,
    revision integer NOT NULL CHECK (revision > 0),
    desde timestamptz NOT NULL, hasta timestamptz,
    registrada_en timestamptz NOT NULL DEFAULT clock_timestamp(),
    PRIMARY KEY (organizacion_id, sucursal_id, id),
    UNIQUE (organizacion_id, dispositivo_id, revision),
    UNIQUE (organizacion_id, sucursal_id, id, dispositivo_id),
    FOREIGN KEY (organizacion_id, sucursal_id) REFERENCES negocio.sucursal ON DELETE RESTRICT,
    FOREIGN KEY (organizacion_id, dispositivo_id) REFERENCES seguridad.dispositivo ON DELETE RESTRICT,
    FOREIGN KEY (organizacion_id, acceso_id) REFERENCES seguridad.acceso_autorizado ON DELETE RESTRICT,
    FOREIGN KEY (organizacion_id, persona_id) REFERENCES seguridad.persona ON DELETE RESTRICT,
    CHECK (hasta IS NULL OR hasta > desde)
);
-- No exclusión temporal aún: entrega offline/solapamientos pendientes PG-20/22.
CREATE TABLE negocio.orden_servicio (
    organizacion_id uuid NOT NULL, sucursal_id uuid NOT NULL,
    id uuid NOT NULL DEFAULT gen_random_uuid(), cliente_id uuid NOT NULL, referencia text,
    PRIMARY KEY (organizacion_id, sucursal_id, id),
    FOREIGN KEY (organizacion_id, sucursal_id, cliente_id) REFERENCES negocio.cliente ON DELETE RESTRICT
);
CREATE TABLE negocio.orden_version (
    organizacion_id uuid NOT NULL, sucursal_id uuid NOT NULL,
    id uuid NOT NULL DEFAULT gen_random_uuid(), orden_id uuid NOT NULL,
    revision integer NOT NULL CHECK (revision > 0),
    ficha_sitio_id uuid, generador_empresa_id uuid, generador_ficha_id uuid,
    criterio_generador text, justificacion_generador text,
    instrucciones text, tipo_servicio text,
    creada_en timestamptz NOT NULL DEFAULT clock_timestamp(),
    PRIMARY KEY (organizacion_id, sucursal_id, id),
    UNIQUE (organizacion_id, sucursal_id, orden_id, revision),
    FOREIGN KEY (organizacion_id, sucursal_id, orden_id) REFERENCES negocio.orden_servicio ON DELETE RESTRICT,
    FOREIGN KEY (organizacion_id, sucursal_id, ficha_sitio_id) REFERENCES negocio.ficha_sitio ON DELETE RESTRICT,
    FOREIGN KEY (organizacion_id, sucursal_id, generador_empresa_id) REFERENCES negocio.empresa_sucursal ON DELETE RESTRICT,
    FOREIGN KEY (organizacion_id, generador_empresa_id, generador_ficha_id) REFERENCES negocio.ficha_empresa(organizacion_id, empresa_id, id) ON DELETE RESTRICT,
    CHECK ((generador_empresa_id IS NULL) = (generador_ficha_id IS NULL)),
    CHECK (generador_empresa_id IS NULL OR (criterio_generador IS NOT NULL AND btrim(criterio_generador) <> ''))
);
CREATE TABLE negocio.archivo (
    organizacion_id uuid NOT NULL, sucursal_id uuid NOT NULL,
    id uuid NOT NULL DEFAULT gen_random_uuid(),
    sha256 bytea NOT NULL CHECK (octet_length(sha256) = 32),
    tamano_bytes bigint NOT NULL CHECK (tamano_bytes >= 0),
    mime text NOT NULL CHECK (btrim(mime) <> ''),
    PRIMARY KEY (organizacion_id, sucursal_id, id),
    FOREIGN KEY (organizacion_id, sucursal_id) REFERENCES negocio.sucursal ON DELETE RESTRICT
);
CREATE TABLE negocio.ubicacion_archivo (
    organizacion_id uuid NOT NULL, sucursal_id uuid NOT NULL,
    id uuid NOT NULL DEFAULT gen_random_uuid(), archivo_id uuid NOT NULL,
    proveedor text NOT NULL CHECK (proveedor IN ('vps','drive')),
    clave_externa text NOT NULL CHECK (btrim(clave_externa) <> ''),
    verificada_en timestamptz,
    PRIMARY KEY (organizacion_id, sucursal_id, id),
    UNIQUE (organizacion_id, sucursal_id, proveedor, clave_externa),
    FOREIGN KEY (organizacion_id, sucursal_id, archivo_id) REFERENCES negocio.archivo ON DELETE RESTRICT
);
-- Hecho recibido inmutable; no exige ruta/manifiesto o resultados de negocio no acordados.
CREATE TABLE negocio.captura (
    organizacion_id uuid NOT NULL, sucursal_id uuid NOT NULL,
    id uuid NOT NULL, -- Identidad estable generada offline.
    asignacion_equipo_id uuid NOT NULL, dispositivo_id uuid NOT NULL, instalacion_app_id uuid NOT NULL,
    orden_version_id uuid,
    ocurrida_en timestamptz NOT NULL, offset_minutos smallint CHECK (offset_minutos BETWEEN -840 AND 840),
    recibida_en timestamptz NOT NULL DEFAULT clock_timestamp(),
    resultado_declarado text, incidencia_declarada text,
    PRIMARY KEY (organizacion_id, sucursal_id, id),
    FOREIGN KEY (organizacion_id, sucursal_id, asignacion_equipo_id, dispositivo_id) REFERENCES seguridad.asignacion_equipo(organizacion_id,sucursal_id,id,dispositivo_id) ON DELETE RESTRICT,
    FOREIGN KEY (organizacion_id, dispositivo_id, instalacion_app_id) REFERENCES seguridad.instalacion_app(organizacion_id,dispositivo_id,id) ON DELETE RESTRICT,
    FOREIGN KEY (organizacion_id, sucursal_id, orden_version_id) REFERENCES negocio.orden_version ON DELETE RESTRICT
);
CREATE TABLE negocio.posicion_captura (
    organizacion_id uuid NOT NULL, sucursal_id uuid NOT NULL,
    id uuid NOT NULL DEFAULT gen_random_uuid(), captura_id uuid NOT NULL,
    latitud numeric(10,7) NOT NULL CHECK (latitud BETWEEN -90 AND 90),
    longitud numeric(10,7) NOT NULL CHECK (longitud BETWEEN -180 AND 180),
    precision_metros numeric(12,3) CHECK (precision_metros >= 0 AND precision_metros <> 'NaN'::numeric),
    antiguedad_segundos numeric(12,3) CHECK (antiguedad_segundos >= 0 AND antiguedad_segundos <> 'NaN'::numeric),
    lectura_en timestamptz NOT NULL,
    PRIMARY KEY (organizacion_id, sucursal_id, id),
    FOREIGN KEY (organizacion_id, sucursal_id, captura_id) REFERENCES negocio.captura ON DELETE RESTRICT
);
-- Bytes exactos aceptados, no JSONB reserializado ni referencias a catálogos actuales.
-- El contrato/alcance semántico se definirá en PG-05, no aquí.
CREATE TABLE negocio.contenido_aceptado (
    organizacion_id uuid NOT NULL, sucursal_id uuid NOT NULL,
    id uuid NOT NULL DEFAULT gen_random_uuid(), captura_id uuid NOT NULL,
    formato text NOT NULL CHECK (btrim(formato) <> ''), version_contrato text NOT NULL CHECK (btrim(version_contrato) <> ''),
    contenido bytea NOT NULL CHECK (octet_length(contenido) > 0),
    sha256 bytea GENERATED ALWAYS AS (sha256(contenido)) STORED,
    PRIMARY KEY (organizacion_id, sucursal_id, id),
    FOREIGN KEY (organizacion_id, sucursal_id, captura_id) REFERENCES negocio.captura ON DELETE RESTRICT
);
CREATE TABLE negocio.firma_generador (
    organizacion_id uuid NOT NULL, sucursal_id uuid NOT NULL,
    id uuid NOT NULL DEFAULT gen_random_uuid(), contenido_aceptado_id uuid NOT NULL, archivo_id uuid NOT NULL,
    nombre_declarado text NOT NULL CHECK (btrim(nombre_declarado) <> ''),
    firmada_en timestamptz NOT NULL,
    PRIMARY KEY (organizacion_id, sucursal_id, id),
    FOREIGN KEY (organizacion_id, sucursal_id, contenido_aceptado_id) REFERENCES negocio.contenido_aceptado ON DELETE RESTRICT,
    FOREIGN KEY (organizacion_id, sucursal_id, archivo_id) REFERENCES negocio.archivo ON DELETE RESTRICT
);
-- Recibo transaccional: conserva incluso datos no normalizables por conflicto.
CREATE TABLE sync.comando (
    organizacion_id uuid NOT NULL, sucursal_id uuid NOT NULL,
    instalacion_app_id uuid NOT NULL, id uuid NOT NULL,
    solicitud bytea NOT NULL,
    sha256 bytea GENERATED ALWAYS AS (sha256(solicitud)) STORED,
    resultado jsonb NOT NULL CHECK (jsonb_typeof(resultado) = 'object'),
    recibido_en timestamptz NOT NULL DEFAULT clock_timestamp(),
    PRIMARY KEY (organizacion_id, sucursal_id, instalacion_app_id, id),
    FOREIGN KEY (organizacion_id, sucursal_id) REFERENCES negocio.sucursal ON DELETE RESTRICT,
    FOREIGN KEY (organizacion_id, instalacion_app_id) REFERENCES seguridad.instalacion_app ON DELETE RESTRICT
);
CREATE TABLE sync.conflicto (
    organizacion_id uuid NOT NULL, sucursal_id uuid NOT NULL,
    id uuid NOT NULL DEFAULT gen_random_uuid(), instalacion_app_id uuid NOT NULL, comando_id uuid NOT NULL,
    motivo text NOT NULL CHECK (btrim(motivo) <> ''), detalle jsonb NOT NULL,
    creado_en timestamptz NOT NULL DEFAULT clock_timestamp(),
    PRIMARY KEY (organizacion_id, sucursal_id, id),
    FOREIGN KEY (organizacion_id, sucursal_id, instalacion_app_id, comando_id) REFERENCES sync.comando ON DELETE RESTRICT
);
CREATE TABLE sync.tarea (
    organizacion_id uuid NOT NULL, sucursal_id uuid NOT NULL,
    id uuid NOT NULL DEFAULT gen_random_uuid(), clave text NOT NULL,
    tipo text NOT NULL, datos jsonb NOT NULL CHECK (jsonb_typeof(datos) = 'object'),
    intentos integer NOT NULL DEFAULT 0 CHECK (intentos >= 0),
    disponible_en timestamptz NOT NULL DEFAULT clock_timestamp(),
    reserva_hasta timestamptz, completada_en timestamptz,
    PRIMARY KEY (organizacion_id, sucursal_id, id), UNIQUE (organizacion_id, sucursal_id, clave),
    FOREIGN KEY (organizacion_id, sucursal_id) REFERENCES negocio.sucursal ON DELETE RESTRICT
);
CREATE INDEX tarea_pendiente ON sync.tarea (disponible_en) WHERE completada_en IS NULL;
CREATE INDEX captura_recibida ON negocio.captura (organizacion_id, sucursal_id, recibida_en);

