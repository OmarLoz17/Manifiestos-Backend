\set ON_ERROR_STOP on
-- Se ejecuta en paralelo con distintos :sujeto/:correo. La pausa mantiene los
-- bloqueos para que la otra petición tenga que esperar al commit.
BEGIN;
SET LOCAL ROLE manifiestos_app;
SELECT resultado_login FROM seguridad.iniciar_sesion_web(md5('ow')::uuid,'https://accounts.google.com',:'sujeto',:'correo');
SELECT pg_sleep(3);
COMMIT;
