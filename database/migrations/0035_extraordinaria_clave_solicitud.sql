-- 12-nov-2026 (revisión H05): una parada extraordinaria puede repetir un sitio que ya está o estuvo en la ruta (decisión PG-35: solo en
-- extraordinarias, con un aviso a Operaciones), pero un REINTENTO de la misma solicitud no debe crear una segunda parada. El panel envía una
-- clave por solicitud; si ya existe, se devuelve la parada creada en lugar de duplicarla (también ante envíos simultáneos).
ALTER TABLE negocio.parada_extraordinaria ADD COLUMN clave_solicitud uuid;
CREATE UNIQUE INDEX parada_extraordinaria_clave ON negocio.parada_extraordinaria (organizacion_id, sucursal_id, clave_solicitud)
    WHERE clave_solicitud IS NOT NULL;
