\set ON_ERROR_STOP on
DO $$ BEGIN
    IF (SELECT count(*) FROM sync.comando WHERE id=md5('concurrente')::uuid)<>1
       OR (SELECT count(*) FROM sync.tarea WHERE clave='tarea-concurrente')<>1 THEN
        RAISE EXCEPTION 'Dos reintentos generaron efectos distintos';
    END IF;
END $$;
BEGIN;
INSERT INTO sync.tarea(organizacion_id,sucursal_id,clave,tipo,datos) VALUES(md5('oa')::uuid,md5('sa')::uuid,'rollback','prueba','{}');
INSERT INTO sync.comando(organizacion_id,sucursal_id,instalacion_app_id,id,solicitud,resultado) VALUES(md5('oa')::uuid,md5('sa')::uuid,md5('i')::uuid,md5('rollback')::uuid,'rollback','{}');
ROLLBACK;
DO $$ BEGIN
    IF EXISTS(SELECT 1 FROM sync.comando WHERE id=md5('rollback')::uuid)
       OR EXISTS(SELECT 1 FROM sync.tarea WHERE clave='rollback') THEN
        RAISE EXCEPTION 'Rollback no atomico';
    END IF;
END $$;
\echo 'Concurrencia: un recibo y una tarea. Rollback: ningún efecto parcial.'
