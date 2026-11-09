-- Coordenadas aproximadas del sitio de servicio (9-nov-2026), para ubicar las paradas en el mapa de Órdenes.
-- Son opcionales y no necesitan ser precisas (las captura Operaciones escribiéndolas o eligiendo un punto en el mapa).
-- Como el resto de la dirección, cambiarlas es una revisión nueva de la ficha del sitio.
ALTER TABLE negocio.ficha_sitio
    ADD COLUMN latitud numeric(9,6) CHECK (latitud BETWEEN -90 AND 90),
    ADD COLUMN longitud numeric(9,6) CHECK (longitud BETWEEN -180 AND 180),
    ADD CONSTRAINT ficha_sitio_coordenadas_par CHECK ((latitud IS NULL) = (longitud IS NULL));
