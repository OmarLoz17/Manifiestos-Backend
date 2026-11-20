-- Sanitarios (19-nov-2026): el aviso que el sistema crea cuando se atienden menos baños de los asignados dice solo «Se atendieron 9 de 12 baños.»,
-- sin el prefijo «Aviso automático:». Solo cambia el texto de las incidencias NUEVAS (el historial no se reescribe; el panel quita el prefijo al mostrar las anteriores).
CREATE OR REPLACE FUNCTION negocio.incidencia_por_banos_incompletos() RETURNS trigger
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE ruta uuid;
BEGIN
    IF NEW.banos_succionados >= NEW.banos_asignados THEN RETURN NEW; END IF;
    SELECT j.ruta_id INTO ruta FROM negocio.jornada j WHERE (j.organizacion_id,j.sucursal_id,j.id)=(NEW.organizacion_id,NEW.sucursal_id,NEW.jornada_id);
    INSERT INTO negocio.incidencia(organizacion_id,sucursal_id,servicio,motivo,descripcion,ocurrida_en,ruta_id,parada_id,cliente_id,vehiculo_id,persona_id,
        banos_asignados,banos_atendidos)
    VALUES(NEW.organizacion_id,NEW.sucursal_id,'sanitario','servicio_parcial',
        'Se atendieron '||NEW.banos_succionados||' de '||NEW.banos_asignados||' baños.',
        NEW.fecha_captura,ruta,NEW.parada_id,NULL,NEW.vehiculo_id,NEW.persona_id,NEW.banos_asignados,NEW.banos_succionados);
    RETURN NEW;
END $$;
