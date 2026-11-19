-- Continuación de 0050 (decisión 0033): en trabajo_operador el indicador «mixta» (la ruta del día tiene baños) ya no condiciona que una fosa propia
-- viaje como parada de sanitarios con `fosa: true`; siempre lo hace. Se reemplaza solo esa expresión de la función vigente.
DO $migracion$
DECLARE d text;
BEGIN
    d := pg_get_functiondef('seguridad.trabajo_operador(uuid,uuid,uuid,date)'::regprocedure);
    IF position('coalesce(bool_or(k2.banos > 0 AND NOT k2.autorizado), false) AS mixta' IN d) = 0 THEN
        RAISE EXCEPTION 'No se encontró la expresión de «mixta» en trabajo_operador';
    END IF;
    d := replace(d, 'coalesce(bool_or(k2.banos > 0 AND NOT k2.autorizado), false) AS mixta', 'true AS mixta');
    EXECUTE d;
END
$migracion$;
