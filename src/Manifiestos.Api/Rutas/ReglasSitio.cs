using Manifiestos.Api.Datos;

namespace Manifiestos.Api.Rutas;

/// <summary>
/// Quién puede ir en cada sitio (12-nov-2026). Los sitios de un cliente AUTORIZADO son suyos (cliente_sitio) y solo él va ahí;
/// los demás son sitios normales, sin dueño, donde van los clientes GENERALES y también cualquier AUTORIZADO (de cualquier ruta y parada).
/// </summary>
internal static class ReglasSitio
{
    /// <summary>El sitio s (alias) está ligado (dirección activa, última revisión) a un cliente autorizado: es de ese cliente, no un sitio normal.</summary>
    public const string DeAutorizado = """
        EXISTS (SELECT 1 FROM negocio.cliente_sitio csa
                JOIN negocio.cliente cpa ON (cpa.organizacion_id, cpa.sucursal_id, cpa.id) = (csa.organizacion_id, csa.sucursal_id, csa.cliente_id)
                WHERE (csa.organizacion_id, csa.sucursal_id, csa.sitio_id) = (s.organizacion_id, s.sucursal_id, s.id)
                  AND csa.activo
                  AND csa.revision = (SELECT max(z.revision) FROM negocio.cliente_sitio z
                                      WHERE (z.organizacion_id, z.sucursal_id, z.cliente_id, z.sitio_id) = (csa.organizacion_id, csa.sucursal_id, csa.cliente_id, csa.sitio_id))
                  AND EXISTS (SELECT 1 FROM negocio.autorizacion_empresa aua
                              WHERE (aua.organizacion_id, aua.empresa_id) = (cpa.organizacion_id, cpa.empresa_id)
                                AND aua.autoridad = 'Medio ambiente' AND aua.finalidad = 'Generador'))
        """;

    /// <summary>El sitio s (alias) no tiene baja.</summary>
    public const string SinBaja = """
        NOT EXISTS (SELECT 1 FROM negocio.sitio_baja sb
                    WHERE (sb.organizacion_id, sb.sucursal_id, sb.sitio_id) = (s.organizacion_id, s.sucursal_id, s.id))
        """;

    /// <summary>Sitio normal vigente: punto de servicio sin dueño autorizado y sin baja.</summary>
    public static async Task<bool> EsNormalAsync(TransaccionContexto tx, Guid org, Guid suc, Guid sitio, CancellationToken ct)
    {
        await using var cmd = tx.Comando($"""
            SELECT EXISTS (SELECT 1 FROM negocio.sitio s
                JOIN negocio.punto_servicio ps ON (ps.organizacion_id, ps.sucursal_id, ps.sitio_id) = (s.organizacion_id, s.sucursal_id, s.id)
                WHERE s.organizacion_id = $1 AND s.sucursal_id = $2 AND s.id = $3 AND {SinBaja} AND NOT {DeAutorizado})
            """, org, suc, sitio);
        return (bool)(await cmd.ExecuteScalarAsync(ct))!;
    }

    /// <summary>
    /// Todos los clientes pueden ir en el sitio (vigente, sin baja): un AUTORIZADO en su dirección ACTIVA o en cualquier sitio normal;
    /// un GENERAL solo en un sitio normal. La regla «una parada con un autorizado no admite otros clientes» la aplica quien arma la parada.
    /// </summary>
    public static async Task<bool> ClientesValidosAsync(TransaccionContexto tx, Guid org, Guid suc, Guid sitio,
        IReadOnlyList<Guid> clientes, CancellationToken ct)
    {
        if (clientes.Count == 0) return true;
        await using var cmd = tx.Comando($"""
            SELECT count(*)::int FROM negocio.cliente c
            WHERE c.organizacion_id = $1 AND c.sucursal_id = $2 AND c.id = ANY($4)
              AND EXISTS (SELECT 1 FROM negocio.sitio s JOIN negocio.punto_servicio ps ON (ps.organizacion_id, ps.sucursal_id, ps.sitio_id) = (s.organizacion_id, s.sucursal_id, s.id)
                          WHERE (s.organizacion_id, s.sucursal_id, s.id) = ($1, $2, $3) AND {SinBaja})
              AND (EXISTS (SELECT 1 FROM negocio.sitio s WHERE (s.organizacion_id, s.sucursal_id, s.id) = ($1, $2, $3) AND NOT {DeAutorizado})
                   OR ({RepositorioRutas.Autorizado}
                       AND coalesce((SELECT x.activo FROM negocio.cliente_sitio x
                                     WHERE (x.organizacion_id, x.sucursal_id, x.cliente_id, x.sitio_id) = (c.organizacion_id, c.sucursal_id, c.id, $3)
                                     ORDER BY x.revision DESC LIMIT 1), false)))
            """, org, suc, sitio, clientes.Distinct().ToArray());
        return (int)(await cmd.ExecuteScalarAsync(ct))! == clientes.Distinct().Count();
    }
}
