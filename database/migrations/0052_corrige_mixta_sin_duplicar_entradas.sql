-- Corrección de 0051: al volver constante «mixta» la subconsulta seguía recorriendo los clientes y repetía la entrada de la jornada una vez por cliente.
-- Se quita ese recorrido; «mixta» queda como constante verdadera (una sola fila por jornada).
DO $migracion$
DECLARE d text; n text;
BEGIN
    d := pg_get_functiondef('seguridad.trabajo_operador(uuid,uuid,uuid,date)'::regprocedure);
    n := regexp_replace(d, 'true AS mixta\s+FROM paradas p2 JOIN clientes k2 ON k2\.parada_id=p2\.parada_id AND k2\.tipo=p2\.tipo WHERE p2\.jornada_id=jo\.jornada_id', 'true AS mixta');
    IF n = d THEN RAISE EXCEPTION 'No se encontró el recorrido de «mixta» en trabajo_operador'; END IF;
    EXECUTE n;
END
$migracion$;
