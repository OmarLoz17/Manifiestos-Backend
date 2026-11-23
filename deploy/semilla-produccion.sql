-- Arranque de una base REAL y limpia (sin demo): organización, empresa, sucursal y la primera cuenta de Operaciones.
-- Los nombres y códigos los da la empresa: NO se inventan. Empresa y sucursal son inmutables en la base; revisarlos antes de ejecutar.
-- La organización lleva el UUID que va en deploy/.env (ORGANIZACION_ID). Repetible: reutiliza lo existente y no cambia rol ni estado.
-- Uso (desde backend/deploy, con la pila ya migrada):
--   docker compose exec -T postgres psql -X -v ON_ERROR_STOP=1 -U manifiestos_owner -d manifiestos \
--     -v org_id='<uuid>' -v org_nombre='<nombre>' -v emp_codigo='<codigo>' -v suc_codigo='<codigo>' -v suc_nombre='<nombre>' \
--     -v correo='<correo de Operaciones>' < semilla-produccion.sql
\set ON_ERROR_STOP on
BEGIN;
INSERT INTO negocio.organizacion(id, nombre) VALUES (:'org_id', :'org_nombre') ON CONFLICT DO NOTHING;
INSERT INTO negocio.empresa(organizacion_id, codigo) VALUES (:'org_id', :'emp_codigo') ON CONFLICT DO NOTHING;
SELECT id AS emp_id FROM negocio.empresa WHERE organizacion_id = :'org_id' AND codigo = :'emp_codigo' \gset
INSERT INTO negocio.sucursal(organizacion_id, empresa_id, codigo, nombre)
VALUES (:'org_id', :'emp_id', :'suc_codigo', :'suc_nombre') ON CONFLICT DO NOTHING;
SELECT id AS suc_id FROM negocio.sucursal WHERE organizacion_id = :'org_id' AND empresa_id = :'emp_id' AND codigo = :'suc_codigo' \gset
INSERT INTO negocio.empresa_sucursal VALUES (:'org_id', :'suc_id', :'emp_id') ON CONFLICT DO NOTHING;
INSERT INTO seguridad.acceso_autorizado(organizacion_id, correo_permitido, activo, rol_codigo)
VALUES (:'org_id', lower(btrim(:'correo')), true, 'operaciones') ON CONFLICT DO NOTHING;
SELECT id AS acc_id FROM seguridad.acceso_autorizado WHERE organizacion_id = :'org_id' AND lower(btrim(correo_permitido)) = lower(btrim(:'correo')) \gset
-- Operaciones necesita leer y escribir en su sucursal (operadores, equipos, rutas, clientes).
INSERT INTO seguridad.permiso_sucursal VALUES (:'org_id', :'suc_id', :'acc_id', 'datos.leer') ON CONFLICT DO NOTHING;
INSERT INTO seguridad.permiso_sucursal VALUES (:'org_id', :'suc_id', :'acc_id', 'datos.escribir') ON CONFLICT DO NOTHING;
COMMIT;
SELECT 'Arranque listo: organización ' || :'org_id' AS resultado;
