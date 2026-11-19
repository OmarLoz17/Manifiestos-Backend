-- Decisión del 18-nov-2026 (0033): una fosa propia (cliente general) es «un baño muy grande». En la app del operador SIEMPRE viaja como parada
-- de sanitarios (una foto con ubicación por parada, sin cantidad y sin descarga por orden en la planta); la tirada con dos firmas
-- queda disponible todo el día. Antes solo lo era cuando la ruta del día también tenía baños («mixta»).
-- Solo las fosas de clientes con convenio (fosa fija) siguen siendo órdenes: cada una con la unidad llena y su descarga.
-- El argumento p_mixta se conserva por compatibilidad con las funciones que ya lo reciben, pero ya no cambia el resultado.
CREATE OR REPLACE FUNCTION negocio.demanda_servicio(p_servicio text, p_banos integer, p_fosas integer, p_autorizado boolean, p_mixta boolean) RETURNS boolean
LANGUAGE sql IMMUTABLE AS $$
    SELECT CASE WHEN p_servicio = 'sanitarios' THEN NOT p_autorizado AND (p_banos > 0 OR p_fosas > 0)
                ELSE p_fosas > 0 AND p_autorizado END
$$;
