-- Cantidad planeada por cliente y sitio de cada revisión de parada.
-- NULL en revisiones previas = sin dato; no se deduce de baños ni se reescribe historia.
ALTER TABLE negocio.parada_cliente
    ADD COLUMN fosas_planeadas integer CHECK (fosas_planeadas >= 0);
COMMENT ON COLUMN negocio.parada_cliente.fosas_planeadas IS
    'Cantidad planeada de fosas para este cliente en el sitio de la revisión; NULL = sin dato. No representa volumen succionado.';
