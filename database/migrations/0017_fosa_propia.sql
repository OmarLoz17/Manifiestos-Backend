-- Propiedad de la fosa en cada sitio del cliente (6-nov-2026), para separar en Órdenes:
-- - false (por omisión): fosa FIJA, del cliente (convenio); cada orden va con la unidad llena (requisitos 1.3).
-- - true: fosa PROPIA, nuestra y rentada al cliente; el generador es nuestra empresa (RF-20). Su regla de
--   volumen sigue pendiente (PG-24).
-- Como el resto del vínculo cliente–sitio, cambiarlo es una revisión nueva.
ALTER TABLE negocio.cliente_sitio ADD COLUMN fosa_propia boolean NOT NULL DEFAULT false;
