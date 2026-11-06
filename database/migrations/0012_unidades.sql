-- Pantalla Unidades: alta y corrección de vehículos con revisiones.
-- vehiculo y ficha_vehiculo son inmutables (0003): corregir una unidad es una ficha nueva.
-- Por eso el número económico vigente vive en la ficha; vehiculo.numero_economico conserva el
-- valor original y se usa solo para unidades cuya ficha todavía no lo trae.
-- clasificacion: NULL = sin clasificar (no se asume el valor de unidades ya existentes).
ALTER TABLE negocio.ficha_vehiculo
    ADD COLUMN numero_economico text CHECK (numero_economico IS NULL OR btrim(numero_economico) <> ''),
    ADD COLUMN clasificacion text CHECK (clasificacion IN ('fosas', 'sanitarios')),
    ADD COLUMN descripcion text,
    ADD COLUMN registrada_en timestamptz NOT NULL DEFAULT clock_timestamp(),
    ADD COLUMN registrada_por_acceso_id uuid DEFAULT seguridad.contexto_acceso();
ALTER TABLE negocio.ficha_vehiculo
    ADD FOREIGN KEY (organizacion_id, registrada_por_acceso_id) REFERENCES seguridad.acceso_autorizado ON DELETE RESTRICT;
CREATE INDEX ficha_vehiculo_registrada_por ON negocio.ficha_vehiculo (organizacion_id, registrada_por_acceso_id);

-- Las políticas RLS 'contexto' ya exigen datos.escribir en WITH CHECK.
GRANT INSERT ON negocio.vehiculo, negocio.ficha_vehiculo TO manifiestos_app;
