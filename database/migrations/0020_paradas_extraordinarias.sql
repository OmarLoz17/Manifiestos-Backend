-- Paradas extraordinarias (9-nov-2026): Operaciones agrega a la ruta DE UN DÍA un cliente que avisó que necesita succión
-- ese día y no estaba planeado. Es solo de esa fecha: NO forma parte de la ruta fija, así que no se repite.
-- El operador recibirá un aviso en su teléfono («se agregó una parada en tu ruta, revisa la app»): `por_avisar`
-- queda en true hasta que la integración con la app (Android) envíe la notificación; todavía no existe.
CREATE TABLE negocio.parada_extraordinaria (
    organizacion_id uuid NOT NULL, sucursal_id uuid NOT NULL,
    id uuid NOT NULL DEFAULT gen_random_uuid(),
    ruta_id uuid NOT NULL,
    fecha date NOT NULL,
    sitio_id uuid NOT NULL,
    por_avisar boolean NOT NULL DEFAULT true,
    creada_en timestamptz NOT NULL DEFAULT clock_timestamp(),
    creada_por_acceso_id uuid DEFAULT seguridad.contexto_acceso(),
    PRIMARY KEY (organizacion_id, sucursal_id, id),
    FOREIGN KEY (organizacion_id, sucursal_id, ruta_id) REFERENCES negocio.ruta ON DELETE RESTRICT,
    FOREIGN KEY (organizacion_id, sucursal_id, sitio_id) REFERENCES negocio.sitio ON DELETE RESTRICT,
    FOREIGN KEY (organizacion_id, creada_por_acceso_id) REFERENCES seguridad.acceso_autorizado ON DELETE RESTRICT
);
CREATE INDEX parada_extraordinaria_dia ON negocio.parada_extraordinaria (organizacion_id, sucursal_id, fecha, ruta_id);
CREATE INDEX parada_extraordinaria_sitio ON negocio.parada_extraordinaria (organizacion_id, sucursal_id, sitio_id);
CREATE INDEX parada_extraordinaria_creada_por ON negocio.parada_extraordinaria (organizacion_id, creada_por_acceso_id);

CREATE TABLE negocio.parada_extraordinaria_cliente (
    organizacion_id uuid NOT NULL, sucursal_id uuid NOT NULL,
    parada_extraordinaria_id uuid NOT NULL,
    cliente_id uuid NOT NULL,
    banos_planeados integer CHECK (banos_planeados >= 0),
    fosas_planeadas integer CHECK (fosas_planeadas >= 0),
    PRIMARY KEY (organizacion_id, sucursal_id, parada_extraordinaria_id, cliente_id),
    FOREIGN KEY (organizacion_id, sucursal_id, parada_extraordinaria_id) REFERENCES negocio.parada_extraordinaria ON DELETE RESTRICT,
    FOREIGN KEY (organizacion_id, sucursal_id, cliente_id) REFERENCES negocio.cliente ON DELETE RESTRICT,
    CHECK (coalesce(banos_planeados, 0) + coalesce(fosas_planeadas, 0) > 0)
);
CREATE INDEX parada_extraordinaria_cliente_cliente ON negocio.parada_extraordinaria_cliente (organizacion_id, sucursal_id, cliente_id);

DO $$
DECLARE t text;
BEGIN
    FOREACH t IN ARRAY ARRAY['parada_extraordinaria', 'parada_extraordinaria_cliente'] LOOP
        EXECUTE format('CREATE TRIGGER conservar_historia BEFORE DELETE ON negocio.%I FOR EACH ROW EXECUTE FUNCTION infra.inmutable()', t);
        EXECUTE format('ALTER TABLE negocio.%I ENABLE ROW LEVEL SECURITY', t);
        EXECUTE format('CREATE POLICY contexto ON negocio.%I TO manifiestos_app USING (seguridad.puede(organizacion_id, sucursal_id, ''datos.leer'')) WITH CHECK (seguridad.puede(organizacion_id, sucursal_id, ''datos.escribir''))', t);
        EXECUTE format('GRANT SELECT, INSERT ON negocio.%I TO manifiestos_app', t);
    END LOOP;
END $$;
