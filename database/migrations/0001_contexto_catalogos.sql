-- Contexto técnico. No se precargan empresas, personas ni datos de clientes.
CREATE SCHEMA catalogo;
CREATE SCHEMA negocio;
CREATE SCHEMA seguridad;
CREATE SCHEMA sync;
REVOKE CREATE ON SCHEMA public FROM PUBLIC;
CREATE TABLE negocio.organizacion (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    nombre text NOT NULL CHECK (btrim(nombre) <> '')
);
CREATE TABLE negocio.empresa (
    organizacion_id uuid NOT NULL REFERENCES negocio.organizacion(id) ON DELETE RESTRICT,
    id uuid NOT NULL DEFAULT gen_random_uuid(),
    codigo text NOT NULL CHECK (btrim(codigo) <> ''),
    PRIMARY KEY (organizacion_id, id), UNIQUE (organizacion_id, codigo)
);
CREATE TABLE negocio.ficha_empresa (
    organizacion_id uuid NOT NULL,
    id uuid NOT NULL DEFAULT gen_random_uuid(),
    empresa_id uuid NOT NULL,
    revision integer NOT NULL CHECK (revision > 0),
    nombre_fiscal text,
    nombre_comercial text,
    rfc text,
    -- Componentes documentales, sin FK mutable a un catálogo geográfico.
    pais text, estado text, municipio text, codigo_postal text,
    calle text, numero_exterior text, numero_interior text, colonia text,
    referencia_localizacion text, telefono text, correo text,
    creada_en timestamptz NOT NULL DEFAULT clock_timestamp(),
    PRIMARY KEY (organizacion_id, id),
    UNIQUE (organizacion_id, empresa_id, revision),
    UNIQUE (organizacion_id, empresa_id, id),
    FOREIGN KEY (organizacion_id, empresa_id) REFERENCES negocio.empresa ON DELETE RESTRICT
);
CREATE TABLE negocio.sucursal (
    organizacion_id uuid NOT NULL,
    id uuid NOT NULL DEFAULT gen_random_uuid(),
    empresa_id uuid NOT NULL,
    codigo text NOT NULL CHECK (btrim(codigo) <> ''),
    nombre text NOT NULL CHECK (btrim(nombre) <> ''),
    zona_horaria text, -- Pendiente PG-18; no adoptar la zona del servidor como regla.
    PRIMARY KEY (organizacion_id, id),
    UNIQUE (organizacion_id, empresa_id, codigo),
    FOREIGN KEY (organizacion_id, empresa_id) REFERENCES negocio.empresa ON DELETE RESTRICT
);
-- Compartición explícita, administrada; ninguna empresa se comparte automáticamente.
CREATE TABLE negocio.empresa_sucursal (
    organizacion_id uuid NOT NULL,
    sucursal_id uuid NOT NULL,
    empresa_id uuid NOT NULL,
    PRIMARY KEY (organizacion_id, sucursal_id, empresa_id),
    FOREIGN KEY (organizacion_id, sucursal_id) REFERENCES negocio.sucursal ON DELETE RESTRICT,
    FOREIGN KEY (organizacion_id, empresa_id) REFERENCES negocio.empresa ON DELETE RESTRICT
);
CREATE TABLE catalogo.capacidad_vehiculo (
    id smallint PRIMARY KEY,
    valor_m3 numeric(8,3) NOT NULL UNIQUE CHECK (valor_m3 > 0 AND valor_m3 <> 'NaN'::numeric)
);
INSERT INTO catalogo.capacidad_vehiculo VALUES (1,10), (2,15), (3,18);
CREATE TABLE negocio.autorizacion_empresa (
    organizacion_id uuid NOT NULL,
    id uuid NOT NULL DEFAULT gen_random_uuid(),
    empresa_id uuid NOT NULL,
    autoridad text NOT NULL CHECK (btrim(autoridad) <> ''),
    finalidad text NOT NULL CHECK (btrim(finalidad) <> ''),
    numero text NOT NULL CHECK (btrim(numero) <> ''),
    vigente_desde date, vigente_hasta date,
    PRIMARY KEY (organizacion_id, id),
    UNIQUE (organizacion_id, empresa_id, id),
    FOREIGN KEY (organizacion_id, empresa_id) REFERENCES negocio.empresa ON DELETE RESTRICT,
    CHECK (vigente_hasta IS NULL OR vigente_desde IS NULL OR vigente_hasta >= vigente_desde)
);
CREATE TABLE negocio.cliente (
    organizacion_id uuid NOT NULL, sucursal_id uuid NOT NULL,
    id uuid NOT NULL DEFAULT gen_random_uuid(), empresa_id uuid NOT NULL,
    codigo_comercial text,
    PRIMARY KEY (organizacion_id, sucursal_id, id),
    FOREIGN KEY (organizacion_id, sucursal_id, empresa_id) REFERENCES negocio.empresa_sucursal ON DELETE RESTRICT
);
-- No UNIQUE por nombre, RFC, placa, correo de persona ni folio no confirmado.
CREATE TABLE negocio.sitio (
    organizacion_id uuid NOT NULL, sucursal_id uuid NOT NULL,
    id uuid NOT NULL DEFAULT gen_random_uuid(), referencia text,
    PRIMARY KEY (organizacion_id, sucursal_id, id),
    FOREIGN KEY (organizacion_id, sucursal_id) REFERENCES negocio.sucursal ON DELETE RESTRICT
);
CREATE TABLE negocio.ficha_sitio (
    organizacion_id uuid NOT NULL, sucursal_id uuid NOT NULL,
    id uuid NOT NULL DEFAULT gen_random_uuid(), sitio_id uuid NOT NULL,
    revision integer NOT NULL CHECK (revision > 0),
    nombre text, pais text, estado text, municipio text, codigo_postal text,
    calle text, numero_exterior text, numero_interior text, colonia text,
    referencia_localizacion text,
    PRIMARY KEY (organizacion_id, sucursal_id, id),
    UNIQUE (organizacion_id, sucursal_id, sitio_id, revision),
    FOREIGN KEY (organizacion_id, sucursal_id, sitio_id) REFERENCES negocio.sitio ON DELETE RESTRICT
);
CREATE TABLE negocio.vehiculo (
    organizacion_id uuid NOT NULL, sucursal_id uuid NOT NULL,
    id uuid NOT NULL DEFAULT gen_random_uuid(), numero_economico text,
    PRIMARY KEY (organizacion_id, sucursal_id, id),
    FOREIGN KEY (organizacion_id, sucursal_id) REFERENCES negocio.sucursal ON DELETE RESTRICT
);
CREATE TABLE negocio.ficha_vehiculo (
    organizacion_id uuid NOT NULL, sucursal_id uuid NOT NULL,
    id uuid NOT NULL DEFAULT gen_random_uuid(), vehiculo_id uuid NOT NULL,
    revision integer NOT NULL CHECK (revision > 0),
    capacidad_id smallint NOT NULL REFERENCES catalogo.capacidad_vehiculo ON DELETE RESTRICT,
    marca text, tipo text, placas text,
    PRIMARY KEY (organizacion_id, sucursal_id, id),
    UNIQUE (organizacion_id, sucursal_id, vehiculo_id, revision),
    FOREIGN KEY (organizacion_id, sucursal_id, vehiculo_id) REFERENCES negocio.vehiculo ON DELETE RESTRICT
);

