\set ON_ERROR_STOP on
BEGIN;
SET LOCAL ROLE manifiestos_app;
SELECT set_config('app.organizacion_id',md5('oa')::uuid::text,true),set_config('app.acceso_id',md5('a')::uuid::text,true);
DO $$
DECLARE previo jsonb;
BEGIN
    previo := sync.resultado_previo(md5('oa')::uuid,md5('sa')::uuid,md5('i')::uuid,md5('concurrente')::uuid,'contenido idéntico');
    IF previo IS NULL THEN
        PERFORM pg_sleep(2);
        INSERT INTO sync.tarea(organizacion_id,sucursal_id,id,clave,tipo,datos)
        VALUES(md5('oa')::uuid,md5('sa')::uuid,gen_random_uuid(),'tarea-concurrente','prueba','{}');
        INSERT INTO sync.comando(organizacion_id,sucursal_id,instalacion_app_id,id,solicitud,resultado)
        VALUES(md5('oa')::uuid,md5('sa')::uuid,md5('i')::uuid,md5('concurrente')::uuid,'contenido idéntico','{"prueba":"concurrente"}');
    ELSIF previo <> '{"prueba":"concurrente"}'::jsonb THEN
        RAISE EXCEPTION 'Resultado diferente';
    END IF;
END $$;
COMMIT;
