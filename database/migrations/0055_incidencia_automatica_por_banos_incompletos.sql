-- Sanitarios (decisión 19-nov-2026): si el operador guarda menos baños de los asignados a la parada, el sistema registra solo una incidencia
-- «servicio_parcial» para Operaciones. Es un aviso: no pide nada más al operador (sin comentario ni foto aparte) y no cambia lo capturado.
-- Se dispara al recibir la captura (negocio.succion_parada), una sola vez por captura; la recepción de una captura repetida no inserta de nuevo.
CREATE OR REPLACE FUNCTION negocio.incidencia_por_banos_incompletos() RETURNS trigger
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE ruta uuid;
BEGIN
    IF NEW.banos_succionados >= NEW.banos_asignados THEN RETURN NEW; END IF;
    SELECT j.ruta_id INTO ruta FROM negocio.jornada j WHERE (j.organizacion_id,j.sucursal_id,j.id)=(NEW.organizacion_id,NEW.sucursal_id,NEW.jornada_id);
    INSERT INTO negocio.incidencia(organizacion_id,sucursal_id,servicio,motivo,descripcion,ocurrida_en,ruta_id,parada_id,cliente_id,vehiculo_id,persona_id,
        banos_asignados,banos_atendidos)
    VALUES(NEW.organizacion_id,NEW.sucursal_id,'sanitario','servicio_parcial',
        'Aviso automático: se atendieron '||NEW.banos_succionados||' de '||NEW.banos_asignados||' baños.',
        NEW.fecha_captura,ruta,NEW.parada_id,NULL,NEW.vehiculo_id,NEW.persona_id,NEW.banos_asignados,NEW.banos_succionados);
    RETURN NEW;
END $$;

DROP TRIGGER IF EXISTS incidencia_banos_incompletos ON negocio.succion_parada;
CREATE TRIGGER incidencia_banos_incompletos AFTER INSERT ON negocio.succion_parada
    FOR EACH ROW EXECUTE FUNCTION negocio.incidencia_por_banos_incompletos();
