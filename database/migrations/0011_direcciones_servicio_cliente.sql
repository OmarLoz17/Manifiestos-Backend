-- Sitios de servicio registrados para un cliente; cantidades registradas, no planeadas.
-- Una dirección puede servir a varios clientes, como permiten las paradas actuales.
CREATE TABLE negocio.cliente_sitio (
    organizacion_id uuid NOT NULL, sucursal_id uuid NOT NULL,
    cliente_id uuid NOT NULL, sitio_id uuid NOT NULL,
    revision integer NOT NULL CHECK (revision > 0),
    activo boolean NOT NULL DEFAULT true,
    banos_registrados integer CHECK (banos_registrados >= 0),
    fosas_registradas integer CHECK (fosas_registradas >= 0),
    registrado_en timestamptz NOT NULL DEFAULT clock_timestamp(),
    registrado_por_acceso_id uuid DEFAULT seguridad.contexto_acceso(),
    PRIMARY KEY (organizacion_id, sucursal_id, cliente_id, sitio_id, revision),
    FOREIGN KEY (organizacion_id, sucursal_id, cliente_id) REFERENCES negocio.cliente ON DELETE RESTRICT,
    FOREIGN KEY (organizacion_id, sucursal_id, sitio_id) REFERENCES negocio.punto_servicio ON DELETE RESTRICT,
    FOREIGN KEY (organizacion_id, registrado_por_acceso_id) REFERENCES seguridad.acceso_autorizado ON DELETE RESTRICT
);
CREATE INDEX cliente_sitio_por_sitio ON negocio.cliente_sitio (organizacion_id, sucursal_id, sitio_id);
CREATE TRIGGER conservar_historia BEFORE UPDATE OR DELETE ON negocio.cliente_sitio
FOR EACH ROW EXECUTE FUNCTION infra.inmutable();
ALTER TABLE negocio.cliente_sitio ENABLE ROW LEVEL SECURITY;
CREATE POLICY contexto ON negocio.cliente_sitio TO manifiestos_app
USING (seguridad.puede(organizacion_id, sucursal_id, 'datos.leer'))
WITH CHECK (seguridad.puede(organizacion_id, sucursal_id, 'datos.escribir'));
GRANT SELECT, INSERT ON negocio.cliente_sitio TO manifiestos_app;

-- Solo relaciones conocidas en las revisiones vigentes de las paradas.
-- No copiar domicilios fiscales ni inferir inventarios de cantidades planeadas.
INSERT INTO negocio.cliente_sitio (organizacion_id, sucursal_id, cliente_id, sitio_id, revision)
SELECT DISTINCT p.organizacion_id, p.sucursal_id, c.cliente_id, v.sitio_id, 1
FROM negocio.parada p
JOIN LATERAL (SELECT x.id, x.sitio_id FROM negocio.parada_version x
    WHERE (x.organizacion_id, x.sucursal_id, x.parada_id) = (p.organizacion_id, p.sucursal_id, p.id)
    ORDER BY x.revision DESC LIMIT 1) v ON true
JOIN negocio.parada_cliente c ON (c.organizacion_id, c.sucursal_id, c.version_id) = (p.organizacion_id, p.sucursal_id, v.id);
