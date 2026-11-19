-- Parada mixta (decisión del 18-nov-2026): una parada de sanitarios puede traer baños y fosas de clientes generales. La app muestra UNA orden «Baños»
-- (si hay baños) y una orden por fosa, cada una con su foto con ubicación. El trabajo del operador agrega por parada cuántas fosas hay (`fosasPropias`,
-- con el ajuste del día). Cada foto de fosa viaja como una captura SERVICIO_SANITARIOS con la marca «fosa» y cantidad 0, que el servidor ya cuenta
-- como una fosa propia más (coordenada y volumen por fosa en el manifiesto de la descarga).
DO $migracion$
DECLARE d text; n text;
BEGIN
    d := pg_get_functiondef('seguridad.trabajo_operador(uuid,uuid,uuid,date)'::regprocedure);
    n := replace(d, '''banosAsignados'', CASE WHEN s.sv=''sanitarios'' THEN',
        '''fosasPropias'', CASE WHEN s.sv=''sanitarios'' THEN (SELECT coalesce(sum(k.fosas),0)::integer FROM clientes k WHERE k.parada_id=p.parada_id AND k.tipo=p.tipo AND NOT k.autorizado) ELSE 0 END, ''banosAsignados'', CASE WHEN s.sv=''sanitarios'' THEN');
    IF n = d THEN RAISE EXCEPTION 'No se encontró banosAsignados en trabajo_operador'; END IF;
    EXECUTE n;
END
$migracion$;
