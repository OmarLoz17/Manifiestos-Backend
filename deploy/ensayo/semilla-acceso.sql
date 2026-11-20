-- Solo ensayo, sobre la demo 100 % ficticia: una cuenta de Operaciones (lectura/escritura) y un teléfono de operador ficticio.
\set ON_ERROR_STOP on
\set org 'de000000-0000-4000-8000-000000000001'
\set suc 'de000000-0000-4000-8000-000000000003'
\set ops 'ensayo.operaciones@example.invalid'
\set tel 'ensayo.telefono@example.invalid'
\set persona 'e5a70000-0000-4000-8000-000000000001'
\set dispositivo 'e5a70000-0000-4000-8000-000000000002'
\set instalacion 'e5a70000-0000-4000-8000-000000000003'
BEGIN;
WITH a AS (INSERT INTO seguridad.acceso_autorizado(organizacion_id, correo_permitido, activo, rol_codigo)
           VALUES (:'org', :'ops', true, 'operaciones') ON CONFLICT DO NOTHING RETURNING id)
INSERT INTO seguridad.permiso_sucursal SELECT :'org', :'suc', a.id, p FROM a, unnest(ARRAY['datos.leer','datos.escribir']) p;
INSERT INTO seguridad.acceso_autorizado(organizacion_id, correo_permitido, activo, rol_codigo)
VALUES (:'org', :'tel', true, 'telefono_operador') ON CONFLICT DO NOTHING;
INSERT INTO seguridad.persona(organizacion_id,id,nombre_completo) VALUES (:'org', :'persona', 'Operador de ensayo') ON CONFLICT DO NOTHING;
INSERT INTO negocio.operador_ficha(organizacion_id,sucursal_id,persona_id,revision,nombre_completo,correo)
VALUES (:'org', :'suc', :'persona', 1, 'Operador de ensayo', :'tel') ON CONFLICT DO NOTHING;
INSERT INTO seguridad.dispositivo(organizacion_id,id) VALUES (:'org', :'dispositivo') ON CONFLICT DO NOTHING;
INSERT INTO seguridad.instalacion_app(organizacion_id,id,dispositivo_id,habilitada) VALUES (:'org', :'instalacion', :'dispositivo', true) ON CONFLICT DO NOTHING;
INSERT INTO seguridad.asignacion_equipo(organizacion_id,sucursal_id,dispositivo_id,acceso_id,persona_id,revision,desde)
SELECT organizacion_id, :'suc', :'dispositivo', id, :'persona', 1, now() - interval '1 minute'
FROM seguridad.acceso_autorizado WHERE correo_permitido = :'tel'
  AND NOT EXISTS (SELECT 1 FROM seguridad.asignacion_equipo WHERE dispositivo_id = :'dispositivo');
COMMIT;
SELECT 'semilla de acceso lista';
