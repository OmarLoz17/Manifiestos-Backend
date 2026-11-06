using Manifiestos.Api.Acceso;
using Manifiestos.Api.Datos;
using Manifiestos.Api.Rutas;
using Npgsql;

namespace Manifiestos.Api.Ordenes;

// ---- Respuestas (JSON camelCase) ----

public sealed record UnidadDeOrden(Guid Id, string? NumeroEconomico, decimal? CapacidadM3, string? Clasificacion);

/// <summary>
/// Cliente atendido en una parada. Autorizado = tiene autorización de medio ambiente como generador:
/// él es el generador y firma; si no, el generador es nuestra empresa y no firma (requisitos 1.4).
/// Fosas = órdenes (vueltas) planeadas en la parada. FosaPropia = la fosa del sitio es nuestra, rentada.
/// </summary>
public sealed record ClienteDeOrden(Guid ClienteId, string Nombre, bool Autorizado, int Banos, int Fosas, bool FosaPropia);
public sealed record ParadaDeOrden(Guid Id, int Posicion, string? Sitio, string? Direccion, IReadOnlyList<ClienteDeOrden> Clientes);
public sealed record RutaDeOrdenes(
    Guid Id, Guid SucursalId, string Nombre, IReadOnlyList<int> Dias, UnidadDeOrden Unidad, OperadorResumen? Operador,
    IReadOnlyList<ParadaDeOrden> Paradas);

public interface IRepositorioOrdenes
{
    Task<IReadOnlyList<RutaDeOrdenes>> ListarAsync(Guid organizacion, Guid acceso, CancellationToken ct);
}

/// <summary>Órdenes por ruta y parada (solo lectura) a partir del plan vigente de Rutas y los sitios de cada cliente.</summary>
public sealed class RepositorioOrdenes(NpgsqlDataSource datos) : IRepositorioOrdenes
{
    public async Task<IReadOnlyList<RutaDeOrdenes>> ListarAsync(Guid organizacion, Guid acceso, CancellationToken ct)
    {
        await using var tx = await TransaccionContexto.AbrirAsync(datos, organizacion, acceso, ct);
        string? T(NpgsqlDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);

        // Clientes de la última revisión de cada parada.
        var clientes = new Dictionary<Guid, List<ClienteDeOrden>>();
        await using (var cmd = tx.Comando("""
            SELECT pa.id, pc.cliente_id,
                   coalesce(nullif(btrim(fe.nombre_comercial), ''), nullif(btrim(fe.nombre_fiscal), ''), nullif(btrim(c.codigo_comercial), ''), 'Cliente sin nombre'),
                   EXISTS (SELECT 1 FROM negocio.autorizacion_empresa a WHERE (a.organizacion_id, a.empresa_id) = (c.organizacion_id, c.empresa_id)
                           AND a.autoridad = 'Medio ambiente' AND a.finalidad = 'Generador'),
                   coalesce(pc.banos_planeados, 0), coalesce(pc.fosas_planeadas, 0), coalesce(cs.fosa_propia, false)
            FROM negocio.parada pa
            JOIN LATERAL (SELECT x.id, x.sitio_id FROM negocio.parada_version x
                WHERE (x.organizacion_id, x.sucursal_id, x.parada_id) = (pa.organizacion_id, pa.sucursal_id, pa.id)
                ORDER BY x.revision DESC LIMIT 1) pv ON true
            JOIN negocio.parada_cliente pc ON (pc.organizacion_id, pc.sucursal_id, pc.version_id) = (pa.organizacion_id, pa.sucursal_id, pv.id)
            JOIN negocio.cliente c ON (c.organizacion_id, c.sucursal_id, c.id) = (pa.organizacion_id, pa.sucursal_id, pc.cliente_id)
            LEFT JOIN LATERAL (SELECT x.nombre_fiscal, x.nombre_comercial FROM negocio.ficha_empresa x
                WHERE (x.organizacion_id, x.empresa_id) = (c.organizacion_id, c.empresa_id) ORDER BY x.revision DESC LIMIT 1) fe ON true
            LEFT JOIN LATERAL (SELECT x.fosa_propia FROM negocio.cliente_sitio x
                WHERE (x.organizacion_id, x.sucursal_id, x.cliente_id, x.sitio_id) = (pa.organizacion_id, pa.sucursal_id, pc.cliente_id, pv.sitio_id)
                ORDER BY x.revision DESC LIMIT 1) cs ON true
            WHERE pa.organizacion_id = $1
            ORDER BY pa.id, 3
            """, organizacion))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct))
            {
                if (!clientes.TryGetValue(r.GetGuid(0), out var l)) clientes[r.GetGuid(0)] = l = [];
                l.Add(new ClienteDeOrden(r.GetGuid(1), r.GetString(2), r.GetBoolean(3), r.GetInt32(4), r.GetInt32(5), r.GetBoolean(6)));
            }

        var paradas = new Dictionary<Guid, List<ParadaDeOrden>>();
        await using (var cmd = tx.Comando("""
            SELECT pa.ruta_id, pa.id, pa.posicion, coalesce(nullif(btrim(fs.nombre), ''), s.referencia),
                   nullif(concat_ws(', ', nullif(btrim(concat_ws(' ', fs.calle, fs.numero_exterior)), ''), fs.colonia, fs.municipio), '')
            FROM negocio.parada pa
            JOIN LATERAL (SELECT x.sitio_id FROM negocio.parada_version x
                WHERE (x.organizacion_id, x.sucursal_id, x.parada_id) = (pa.organizacion_id, pa.sucursal_id, pa.id)
                ORDER BY x.revision DESC LIMIT 1) pv ON true
            JOIN negocio.sitio s ON (s.organizacion_id, s.sucursal_id, s.id) = (pa.organizacion_id, pa.sucursal_id, pv.sitio_id)
            LEFT JOIN LATERAL (SELECT x.* FROM negocio.ficha_sitio x
                WHERE (x.organizacion_id, x.sucursal_id, x.sitio_id) = (s.organizacion_id, s.sucursal_id, s.id)
                ORDER BY x.revision DESC LIMIT 1) fs ON true
            WHERE pa.organizacion_id = $1
            ORDER BY pa.ruta_id, pa.posicion
            """, organizacion))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct))
            {
                if (!paradas.TryGetValue(r.GetGuid(0), out var l)) paradas[r.GetGuid(0)] = l = [];
                l.Add(new ParadaDeOrden(r.GetGuid(1), r.GetInt32(2), T(r, 3), T(r, 4), clientes.GetValueOrDefault(r.GetGuid(1)) ?? []));
            }

        var rutas = new List<RutaDeOrdenes>();
        await using (var cmd = tx.Comando("""
            SELECT ru.id, ru.sucursal_id, rv.nombre, rv.dias, veh.id, coalesce(fv.numero_economico, veh.numero_economico), cap.valor_m3, fv.clasificacion,
                   op.persona_id, coalesce(ofi.nombre_completo, pe.nombre_completo)
            FROM negocio.ruta ru
            JOIN LATERAL (SELECT x.nombre, x.dias, x.vehiculo_id FROM negocio.ruta_version x
                WHERE (x.organizacion_id, x.sucursal_id, x.ruta_id) = (ru.organizacion_id, ru.sucursal_id, ru.id)
                ORDER BY x.revision DESC LIMIT 1) rv ON true
            JOIN negocio.vehiculo veh ON (veh.organizacion_id, veh.sucursal_id, veh.id) = (ru.organizacion_id, ru.sucursal_id, rv.vehiculo_id)
            LEFT JOIN LATERAL (SELECT x.* FROM negocio.ficha_vehiculo x
                WHERE (x.organizacion_id, x.sucursal_id, x.vehiculo_id) = (veh.organizacion_id, veh.sucursal_id, veh.id)
                ORDER BY x.revision DESC LIMIT 1) fv ON true
            LEFT JOIN catalogo.capacidad_vehiculo cap ON cap.id = fv.capacidad_id
            LEFT JOIN LATERAL (SELECT a.persona_id FROM negocio.asignacion_unidad a
                WHERE (a.organizacion_id, a.sucursal_id, a.vehiculo_id) = (veh.organizacion_id, veh.sucursal_id, veh.id)
                ORDER BY a.revision DESC LIMIT 1) op ON true
            LEFT JOIN seguridad.persona pe ON (pe.organizacion_id, pe.id) = (ru.organizacion_id, op.persona_id)
            LEFT JOIN LATERAL (SELECT x.nombre_completo FROM negocio.operador_ficha x
                WHERE (x.organizacion_id, x.sucursal_id, x.persona_id) = (ru.organizacion_id, ru.sucursal_id, op.persona_id)
                ORDER BY x.revision DESC LIMIT 1) ofi ON true
            WHERE ru.organizacion_id = $1
            ORDER BY rv.nombre, ru.id
            """, organizacion))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct))
                rutas.Add(new RutaDeOrdenes(
                    r.GetGuid(0), r.GetGuid(1), r.GetString(2), DiasSemana.Lista(r.GetInt16(3)),
                    new UnidadDeOrden(r.GetGuid(4), T(r, 5), r.IsDBNull(6) ? null : r.GetDecimal(6), T(r, 7)),
                    r.IsDBNull(8) ? null : new OperadorResumen(r.GetGuid(8), T(r, 9) ?? "Operador sin nombre"),
                    paradas.GetValueOrDefault(r.GetGuid(0)) ?? []));
        await tx.ConfirmarAsync(ct);
        return rutas;
    }
}

public static class EndpointsOrdenes
{
    public static IServiceCollection AgregarOrdenes(this IServiceCollection services) =>
        services.AddSingleton<IRepositorioOrdenes, RepositorioOrdenes>();

    public static void MapearOrdenes(this IEndpointRouteBuilder app) =>
        app.MapGet("/api/ordenes", async (HttpContext http, IRepositorioOrdenes ordenes) =>
        {
            if (!ServiciosAcceso.LeerIds(http.User, out var org, out var acceso)) return Results.Unauthorized();
            http.Response.Headers.CacheControl = "no-store";
            return Results.Ok(await ordenes.ListarAsync(org, acceso, http.RequestAborted));
        });
}
