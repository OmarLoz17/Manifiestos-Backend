\set ON_ERROR_STOP on
-- DATOS DEMO 100 % FICTICIOS para probar el panel (base manifiestos_demo y pruebas E2E).
-- Ningún nombre, domicilio, teléfono, correo o autorización corresponde a personas, empresas
-- o permisos reales: correos en example.invalid, teléfonos 81 0000 00xx y autorizaciones
-- "FI-9xx (ficticia)". Las calles llevan "Ficticia" y el par calle/número no debe coincidir con el
-- domicilio de un recinto o lugar real identificable. NUNCA cargar en la base "manifiestos" (New-Demo.ps1 lo impide).
-- Todo en una transacción: las revisiones de parada y sus clientes se insertan juntos (0008).
-- Sin estados de ruta (PG-10): todo se presenta como Borrador.
SET client_encoding = 'UTF8';

-- Identificadores estables derivados de un texto (los de organización y sucursal son literales).
CREATE FUNCTION pg_temp.uid(t text) RETURNS uuid LANGUAGE sql IMMUTABLE AS $$ SELECT md5('demo-manifiestos:' || t)::uuid $$;

BEGIN;

-- ---------------- Contexto ----------------
-- Organización demo:  de000000-0000-4000-8000-000000000001 (Acceso:OrganizacionId del perfil "demo")
-- Empresa transportista demo: de000000-0000-4000-8000-000000000002
-- Sucursal demo:      de000000-0000-4000-8000-000000000003
INSERT INTO negocio.organizacion (id, nombre) VALUES ('de000000-0000-4000-8000-000000000001', 'Grupo Demo Ficticio');
INSERT INTO negocio.empresa (organizacion_id, id, codigo)
VALUES ('de000000-0000-4000-8000-000000000001', 'de000000-0000-4000-8000-000000000002', 'DEMO-TRANSPORTE');
INSERT INTO negocio.ficha_empresa (organizacion_id, empresa_id, revision, nombre_fiscal, nombre_comercial,
    pais, estado, municipio, codigo_postal, calle, numero_exterior, colonia, telefono, correo)
VALUES ('de000000-0000-4000-8000-000000000001', 'de000000-0000-4000-8000-000000000002', 1,
    'Transportes Sanitarios Demo, S.A. de C.V. (ficticia)', 'Sanitarios Demo',
    'México', 'Nuevo León', 'Monterrey', '64000', 'Av. Ficticia de la Industria', '100', 'Centro',
    '81 0000 0000', 'operaciones@example.invalid');
-- La autorización de transporte es de la empresa, no de cada vehículo.
INSERT INTO negocio.autorizacion_empresa (organizacion_id, empresa_id, autoridad, finalidad, numero)
VALUES ('de000000-0000-4000-8000-000000000001', 'de000000-0000-4000-8000-000000000002', 'Medio ambiente', 'Transportista', 'FI-901 (ficticia)');
INSERT INTO negocio.sucursal (organizacion_id, id, empresa_id, codigo, nombre)
VALUES ('de000000-0000-4000-8000-000000000001', 'de000000-0000-4000-8000-000000000003', 'de000000-0000-4000-8000-000000000002',
    'DEMO-MTY', 'Sucursal Demo Monterrey');
INSERT INTO negocio.empresa_sucursal VALUES
    ('de000000-0000-4000-8000-000000000001', 'de000000-0000-4000-8000-000000000003', 'de000000-0000-4000-8000-000000000002');

-- ---------------- Lista de autorizados (pruebas E2E) ----------------
-- Estas cuentas solo pueden entrar con el validador de prueba del entorno E2E; en la demo con
-- Google real se autoriza otra cuenta con New-Demo.ps1 -CorreoAcceso.
INSERT INTO seguridad.acceso_autorizado (organizacion_id, id, correo_permitido, activo, rol_codigo) VALUES
    ('de000000-0000-4000-8000-000000000001', pg_temp.uid('acceso:e2e'), 'e2e@example.invalid', true, 'operaciones'),
    ('de000000-0000-4000-8000-000000000001', pg_temp.uid('acceso:lectura'), 'lectura-e2e@example.invalid', true, 'operaciones');
INSERT INTO seguridad.permiso_sucursal VALUES
    ('de000000-0000-4000-8000-000000000001', 'de000000-0000-4000-8000-000000000003', pg_temp.uid('acceso:e2e'), 'datos.leer'),
    ('de000000-0000-4000-8000-000000000001', 'de000000-0000-4000-8000-000000000003', pg_temp.uid('acceso:e2e'), 'datos.escribir'),
    ('de000000-0000-4000-8000-000000000001', 'de000000-0000-4000-8000-000000000003', pg_temp.uid('acceso:lectura'), 'datos.leer');

-- ---------------- Clientes ----------------
-- clave, nombre fiscal, nombre comercial, calle, número, colonia, CP, municipio, teléfono, correo, autorización
CREATE TEMP TABLE demo_cliente (clave text, fiscal text, comercial text, calle text, numero text, colonia text,
    cp text, municipio text, telefono text, correo text, autorizacion text);
INSERT INTO demo_cliente VALUES
    -- Autorizados: su empresa tiene autorización de medio ambiente como generador.
    ('a1', 'Constructora Cerro Demo, S.A. de C.V.', 'Obra Cerro Demo', 'Av. Ficticia Universidad', '1200', 'Anáhuac',
        '66450', 'San Nicolás de los Garza', '81 0000 0001', 'obra.cerro@example.invalid', 'FI-911 (ficticia)'),
    ('a2', 'Desarrollos Lomas Demo, S.A. de C.V.', 'Residencial Lomas Demo', 'Calle Ficticia Roble', '45', 'Las Puentes',
        '66460', 'San Nicolás de los Garza', '81 0000 0002', 'lomas@example.invalid', 'FI-912 (ficticia)'),
    ('a3', 'Manufacturas Huinalá Demo, S.A. de C.V.', 'Planta Huinalá Demo', 'Carretera Ficticia a Huinalá', 'km 3', 'Parque Industrial Demo',
        '66640', 'Apodaca', '81 0000 0003', 'planta.huinala@example.invalid', 'FI-913 (ficticia)'),
    ('a4', 'Logística Aeropuerto Demo, S.A. de C.V.', 'Bodegas Aeropuerto Demo', 'Blvd. Ficticio Aeropuerto', '800', 'Centro de Apodaca',
        '66600', 'Apodaca', '81 0000 0004', 'bodegas@example.invalid', 'FI-914 (ficticia)'),
    ('a5', 'Corporativo Valle Demo, S.A. de C.V.', 'Torre Valle Demo', 'Av. Ficticia Vasconcelos', '300', 'Del Valle',
        '66220', 'San Pedro Garza García', '81 0000 0005', 'torre.valle@example.invalid', 'FI-915 (ficticia)'),
    ('a6', 'Edificaciones La Fama Demo, S.A. de C.V.', 'Obra La Fama Demo', 'Calle Ficticia Morelos', '210', 'La Fama',
        '66100', 'Santa Catarina', '81 0000 0006', 'lafama@example.invalid', 'FI-916 (ficticia)'),
    ('a7', 'Inmobiliaria Centro Demo, S.A. de C.V.', 'Torre Centro Demo', 'Av. Ficticia Constitución', '500', 'Centro',
        '64000', 'Monterrey', '81 0000 0007', 'torre.centro@example.invalid', 'FI-917 (ficticia)'),
    ('a8', 'Espectáculos Fundidora Demo, S.A. de C.V.', 'Eventos Fundidora Demo', 'Calle Ficticia de las Fundiciones', '1450', 'Obrera',
        '64010', 'Monterrey', '81 0000 0008', 'eventos@example.invalid', 'FI-918 (ficticia)'),
    -- Generales: sin autorización de medio ambiente.
    ('g1', NULL, 'Feria Vecinal Demo', 'Calle Ficticia Hidalgo', '12', 'Linda Vista',
        '67130', 'Guadalupe', '81 0000 0009', 'feria@example.invalid', NULL),
    ('g2', NULL, 'Taller Mecánico Demo', 'Calle Ficticia Juárez', '88', 'Centro de Apodaca',
        '66600', 'Apodaca', '81 0000 0010', NULL, NULL),
    ('g3', 'Escuela Particular Demo, A.C.', 'Colegio Demo', 'Calle Ficticia Zaragoza', '300', 'La Fama',
        '66100', 'Santa Catarina', '81 0000 0011', 'colegio@example.invalid', NULL),
    ('g4', NULL, 'Mercado Rodante Demo', 'Calle Ficticia Madero', '7', 'Obrera',
        '64010', 'Monterrey', '81 0000 0012', NULL, NULL),
    ('g5', NULL, 'Rancho Las Lomas Demo', 'Camino Ficticio al Topo', 's/n', 'Topo Chico',
        '66050', 'General Escobedo', '81 0000 0013', 'rancho@example.invalid', NULL);

INSERT INTO negocio.empresa (organizacion_id, id, codigo)
SELECT 'de000000-0000-4000-8000-000000000001', pg_temp.uid('empresa:' || clave), 'DEMO-CLI-' || upper(clave) FROM demo_cliente;
INSERT INTO negocio.ficha_empresa (organizacion_id, empresa_id, revision, nombre_fiscal, nombre_comercial,
    pais, estado, municipio, codigo_postal, calle, numero_exterior, colonia, telefono, correo)
SELECT 'de000000-0000-4000-8000-000000000001', pg_temp.uid('empresa:' || clave), 1, fiscal, comercial,
    'México', 'Nuevo León', municipio, cp, calle, numero, colonia, telefono, correo FROM demo_cliente;
INSERT INTO negocio.empresa_sucursal
SELECT 'de000000-0000-4000-8000-000000000001', 'de000000-0000-4000-8000-000000000003', pg_temp.uid('empresa:' || clave) FROM demo_cliente;
INSERT INTO negocio.cliente (organizacion_id, sucursal_id, id, empresa_id)
SELECT 'de000000-0000-4000-8000-000000000001', 'de000000-0000-4000-8000-000000000003',
    pg_temp.uid('cliente:' || clave), pg_temp.uid('empresa:' || clave) FROM demo_cliente;
INSERT INTO negocio.autorizacion_empresa (organizacion_id, empresa_id, autoridad, finalidad, numero)
SELECT 'de000000-0000-4000-8000-000000000001', pg_temp.uid('empresa:' || clave), 'Medio ambiente', 'Generador', autorizacion
FROM demo_cliente WHERE autorizacion IS NOT NULL;

-- ---------------- Operadores y unidades ----------------
INSERT INTO seguridad.persona (organizacion_id, id, nombre_completo)
SELECT 'de000000-0000-4000-8000-000000000001', pg_temp.uid('persona:' || clave), nombre
FROM (VALUES ('p1', 'Andrés Ficticio Garza'), ('p2', 'Beatriz Ficticia Treviño'), ('p3', 'Carlos Ficticio Salinas'),
             ('p4', 'Diana Ficticia Cantú'), ('p5', 'Ernesto Ficticio Villarreal'), ('p6', 'Fernanda Ficticia Leal')) AS t(clave, nombre);

-- Capacidades del catálogo confirmado: 1 = 10 m³, 2 = 15 m³, 3 = 18 m³.
CREATE TEMP TABLE demo_unidad (clave text, economico text, capacidad smallint, placas text, operador text);
INSERT INTO demo_unidad VALUES
    ('u1', 'U-101', 1, 'DEMO-101', 'p1'),
    ('u2', 'U-102', 2, 'DEMO-102', 'p2'),
    ('u3', 'U-103', 3, 'DEMO-103', 'p3'),
    ('u4', 'U-104', 2, 'DEMO-104', 'p4');
INSERT INTO negocio.vehiculo (organizacion_id, sucursal_id, id, numero_economico)
SELECT 'de000000-0000-4000-8000-000000000001', 'de000000-0000-4000-8000-000000000003', pg_temp.uid('unidad:' || clave), economico FROM demo_unidad;
INSERT INTO negocio.ficha_vehiculo (organizacion_id, sucursal_id, vehiculo_id, revision, capacidad_id, marca, tipo, placas)
SELECT 'de000000-0000-4000-8000-000000000001', 'de000000-0000-4000-8000-000000000003', pg_temp.uid('unidad:' || clave), 1,
    capacidad, 'Marca Demo', 'Pipa de vacío', placas FROM demo_unidad;
INSERT INTO negocio.asignacion_unidad (organizacion_id, sucursal_id, vehiculo_id, revision, persona_id)
SELECT 'de000000-0000-4000-8000-000000000001', 'de000000-0000-4000-8000-000000000003', pg_temp.uid('unidad:' || clave), 1,
    pg_temp.uid('persona:' || operador) FROM demo_unidad;

-- ---------------- Sitios ----------------
-- PTAR ficticia: sitio de descarga, SIN punto de servicio (no puede elegirse como parada).
INSERT INTO negocio.sitio (organizacion_id, sucursal_id, id, referencia)
VALUES ('de000000-0000-4000-8000-000000000001', 'de000000-0000-4000-8000-000000000003', pg_temp.uid('sitio:ptar'), 'PTAR Demo Norte (ficticia)');
INSERT INTO negocio.ficha_sitio (organizacion_id, sucursal_id, sitio_id, revision, nombre, pais, estado, municipio,
    codigo_postal, calle, numero_exterior, colonia, referencia_localizacion)
VALUES ('de000000-0000-4000-8000-000000000001', 'de000000-0000-4000-8000-000000000003', pg_temp.uid('sitio:ptar'), 1,
    'PTAR Demo Norte (ficticia)', 'México', 'Nuevo León', 'Apodaca', '66600', 'Camino Ficticio a la Planta', 's/n',
    'Zona Industrial Demo', 'Sitio de descarga ficticio');

CREATE TEMP TABLE demo_punto (clave text, nombre text, calle text, numero text, colonia text, cp text, municipio text, referencia text);
INSERT INTO demo_punto VALUES
    ('s01', 'Obra Torre Centro Demo', 'Av. Ficticia Constitución', '500', 'Centro', '64000', 'Monterrey', 'Acceso por la calle lateral'),
    ('s02', 'Explanada Fundidora Demo', 'Calle Ficticia de las Fundiciones', '1450', 'Obrera', '64010', 'Monterrey', 'Puerta 3, junto al estacionamiento'),
    ('s03', 'Plaza Anáhuac Demo', 'Av. Ficticia Universidad', '1200', 'Anáhuac', '66450', 'San Nicolás de los Garza', NULL),
    ('s04', 'Fraccionamiento Las Puentes Demo', 'Calle Ficticia Roble', '45', 'Las Puentes', '66460', 'San Nicolás de los Garza', 'Caseta de vigilancia'),
    ('s05', 'Nave Industrial Huinalá Demo', 'Carretera Ficticia a Huinalá', 'km 3', 'Parque Industrial Demo', '66640', 'Apodaca', 'Andén 2'),
    ('s06', 'Bodegas Aeropuerto Demo', 'Blvd. Ficticio Aeropuerto', '800', 'Centro de Apodaca', '66600', 'Apodaca', NULL),
    ('s07', 'Obra Linda Vista Demo', 'Calle Ficticia Hidalgo', '12', 'Linda Vista', '67130', 'Guadalupe', 'Frente a la plaza'),
    ('s08', 'Estadio Demo Acceso Norte', 'Av. Ficticia de los Deportes', '930', 'Valle Soleado', '67170', 'Guadalupe', 'Acceso norte'),
    ('s09', 'Torre Valle Demo', 'Av. Ficticia Vasconcelos', '300', 'Del Valle', '66220', 'San Pedro Garza García', 'Estacionamiento sótano 1'),
    ('s10', 'Obra La Fama Demo', 'Calle Ficticia Morelos', '210', 'La Fama', '66100', 'Santa Catarina', NULL);
INSERT INTO negocio.sitio (organizacion_id, sucursal_id, id, referencia)
SELECT 'de000000-0000-4000-8000-000000000001', 'de000000-0000-4000-8000-000000000003', pg_temp.uid('sitio:' || clave), nombre FROM demo_punto;
INSERT INTO negocio.ficha_sitio (organizacion_id, sucursal_id, sitio_id, revision, nombre, pais, estado, municipio,
    codigo_postal, calle, numero_exterior, colonia, referencia_localizacion)
SELECT 'de000000-0000-4000-8000-000000000001', 'de000000-0000-4000-8000-000000000003', pg_temp.uid('sitio:' || clave), 1,
    nombre, 'México', 'Nuevo León', municipio, cp, calle, numero, colonia, referencia FROM demo_punto;
INSERT INTO negocio.punto_servicio (organizacion_id, sucursal_id, sitio_id)
SELECT 'de000000-0000-4000-8000-000000000001', 'de000000-0000-4000-8000-000000000003', pg_temp.uid('sitio:' || clave) FROM demo_punto;

-- ---------------- Rutas y paradas ----------------
-- dias: máscara ISO (lunes = 1, martes = 2, miércoles = 4, jueves = 8, viernes = 16, sábado = 32).
-- Lun·Mié·Vie = 21; Mar·Jue·Sáb = 42.
INSERT INTO negocio.ruta (organizacion_id, sucursal_id, id)
SELECT 'de000000-0000-4000-8000-000000000001', 'de000000-0000-4000-8000-000000000003', pg_temp.uid('ruta:' || clave)
FROM (VALUES ('r1'), ('r2'), ('r3'), ('r4')) AS t(clave);
INSERT INTO negocio.ruta_version (organizacion_id, sucursal_id, ruta_id, revision, nombre, dias, vehiculo_id)
SELECT 'de000000-0000-4000-8000-000000000001', 'de000000-0000-4000-8000-000000000003', pg_temp.uid('ruta:' || clave), 1,
    nombre, dias, pg_temp.uid('unidad:' || unidad)
FROM (VALUES ('r1', 'Ruta Norte', 21, 'u1'), ('r2', 'Ruta Sur', 21, 'u2'),
             ('r3', 'Ruta Centro', 42, 'u3'), ('r4', 'Ruta Oriente', 42, 'u4')) AS t(clave, nombre, dias, unidad);

-- ruta, posición, punto de servicio, cliente, baños planeados (piezas, no volumen).
CREATE TEMP TABLE demo_parada (ruta text, posicion int, sitio text, cliente text, banos int);
INSERT INTO demo_parada VALUES
    ('r1', 1, 's03', 'a1', 4), ('r1', 1, 's03', 'a2', 2),
    ('r1', 2, 's04', 'g1', 3),
    ('r1', 3, 's05', 'a3', 6),
    ('r1', 4, 's06', 'a4', 2), ('r1', 4, 's06', 'g2', 1),
    ('r2', 1, 's09', 'a5', 5),
    ('r2', 2, 's10', 'a6', 3), ('r2', 2, 's10', 'g3', 2),
    ('r2', 3, 's01', 'a7', 4),
    ('r3', 1, 's01', 'a7', 2),
    ('r3', 2, 's02', 'a8', 8), ('r3', 2, 's02', 'g4', 2),
    ('r3', 3, 's07', 'a1', 3),
    ('r3', 4, 's08', 'g1', 1), ('r3', 4, 's08', 'a2', 2),
    ('r3', 5, 's09', 'a5', 1),
    ('r4', 1, 's07', 'g2', 2),
    ('r4', 2, 's08', 'a3', 4),
    ('r4', 3, 's05', 'a4', 3),
    ('r4', 4, 's06', 'g3', 1);

INSERT INTO negocio.parada (organizacion_id, sucursal_id, id, ruta_id, posicion)
SELECT DISTINCT 'de000000-0000-4000-8000-000000000001'::uuid, 'de000000-0000-4000-8000-000000000003'::uuid,
    pg_temp.uid('parada:' || ruta || ':' || posicion), pg_temp.uid('ruta:' || ruta), posicion FROM demo_parada;
INSERT INTO negocio.parada_version (organizacion_id, sucursal_id, id, parada_id, revision, sitio_id)
SELECT DISTINCT 'de000000-0000-4000-8000-000000000001'::uuid, 'de000000-0000-4000-8000-000000000003'::uuid,
    pg_temp.uid('parada-version:' || ruta || ':' || posicion), pg_temp.uid('parada:' || ruta || ':' || posicion), 1,
    pg_temp.uid('sitio:' || sitio) FROM demo_parada;
-- Misma transacción que la revisión de la parada (trigger agregado_inmutable de 0008).
INSERT INTO negocio.parada_cliente (organizacion_id, sucursal_id, version_id, cliente_id, banos_planeados)
SELECT 'de000000-0000-4000-8000-000000000001', 'de000000-0000-4000-8000-000000000003',
    pg_temp.uid('parada-version:' || ruta || ':' || posicion), pg_temp.uid('cliente:' || cliente), banos FROM demo_parada;

-- Direcciones conocidas por cliente. Inventario aún sin capturar: no inferirlo del plan.
INSERT INTO negocio.cliente_sitio (organizacion_id,sucursal_id,cliente_id,sitio_id,revision)
SELECT DISTINCT p.organizacion_id,p.sucursal_id,c.cliente_id,v.sitio_id,1
FROM negocio.parada p JOIN negocio.parada_version v ON (v.organizacion_id,v.sucursal_id,v.parada_id)=(p.organizacion_id,p.sucursal_id,p.id)
JOIN negocio.parada_cliente c ON (c.organizacion_id,c.sucursal_id,c.version_id)=(v.organizacion_id,v.sucursal_id,v.id)
WHERE p.organizacion_id='de000000-0000-4000-8000-000000000001'
ON CONFLICT DO NOTHING;

DROP TABLE demo_cliente, demo_unidad, demo_punto, demo_parada;
COMMIT;

\echo 'Datos demo cargados (todos ficticios). Acceso:OrganizacionId = de000000-0000-4000-8000-000000000001'
