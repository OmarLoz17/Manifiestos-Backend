\set ON_ERROR_STOP on
DO $$ BEGIN
    -- Mismo sujeto dos veces a la vez: una identidad, un vínculo, dos inicios aceptados.
    IF (SELECT count(*) FROM seguridad.identidad_externa WHERE sujeto='s-conc')<>1
       OR (SELECT count(*) FROM seguridad.acceso_autorizado x JOIN seguridad.identidad_externa i
           ON (i.organizacion_id,i.id)=(x.organizacion_id,x.identidad_id)
           WHERE x.id=md5('a_conc')::uuid AND i.sujeto='s-conc')<>1
       OR (SELECT count(*) FROM seguridad.inicio_sesion WHERE acceso_id=md5('a_conc')::uuid AND resultado='aceptado')<>2 THEN
        RAISE EXCEPTION 'Doble primer acceso del mismo sujeto no fue idempotente';
    END IF;
    -- Dos sujetos distintos con el mismo correo a la vez: solo uno queda vinculado.
    IF (SELECT count(*) FROM seguridad.identidad_externa WHERE sujeto IN ('s-x1','s-x2'))<>1
       OR (SELECT count(*) FROM seguridad.inicio_sesion WHERE acceso_id=md5('a_carrera')::uuid AND resultado='aceptado')<>1
       OR (SELECT identidad_id IS NULL FROM seguridad.acceso_autorizado WHERE id=md5('a_carrera')::uuid) THEN
        RAISE EXCEPTION 'Dos sujetos se disputaron la misma entrada';
    END IF;
END $$;
\echo 'Acceso web concurrente: un vínculo por entrada, sin duplicados.'
