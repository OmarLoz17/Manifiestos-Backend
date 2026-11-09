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
/// Fosas = órdenes (vueltas) planeadas en la parada. FosaPropia = la fosa es nuestra, rentada: se deduce del cliente (decisión del
/// 9-nov-2026): un cliente autorizado tiene fosa propia SUYA (fija); uno general nos la renta (fosa nuestra). No se pregunta.
/// </summary>
public sealed record ClienteDeOrden(Guid ClienteId, string Nombre, bool Autorizado, int Banos, int Fosas, bool FosaPropia);
public sealed record ParadaDeOrden(Guid Id, int Posicion, string? Sitio, string? Direccion, IReadOnlyList<ClienteDeOrden> Clientes,
    decimal? Latitud = null, decimal? Longitud = null);
/// <summary>Parada extraordinaria: se agregó a la ruta de UN día (cliente no planeado que pidió succión); no se repite.</summary>
public sealed record ParadaExtraordinaria(
    Guid Id, DateOnly Fecha, string? Sitio, string? Direccion, IReadOnlyList<ClienteDeOrden> Clientes, DateTimeOffset CreadaEn,
    decimal? Latitud = null, decimal? Longitud = null);
public sealed record RutaDeOrdenes(
    Guid Id, Guid SucursalId, string Nombre, IReadOnlyList<int> Dias, UnidadDeOrden Unidad, OperadorResumen? Operador,
    IReadOnlyList<ParadaDeOrden> Paradas, IReadOnlyList<ParadaExtraordinaria>? Extraordinarias = null);

public sealed record SolicitudExtraordinaria(Guid? RutaId, string? Fecha, Guid? SitioId, SolicitudClienteParada[]? Clientes);

public interface IRepositorioOrdenes
{
    /// <summary>Con fecha, cada ruta trae además las paradas extraordinarias de ese día.</summary>
    Task<IReadOnlyList<RutaDeOrdenes>> ListarAsync(Guid organizacion, Guid acceso, DateOnly? fecha, CancellationToken ct);
    Task<ResultadoEscritura> AgregarExtraordinariaAsync(Guid organizacion, Guid acceso, Guid ruta, DateOnly fecha, Guid sitio, IReadOnlyList<DatosClienteParada> clientes, CancellationToken ct);
}

/// <summary>Órdenes por ruta y parada (solo lectura) a partir del plan vigente de Rutas y los sitios de cada cliente.</summary>
public sealed class RepositorioOrdenes(NpgsqlDataSource datos) : IRepositorioOrdenes
{
    public async Task<IReadOnlyList<RutaDeOrdenes>> ListarAsync(Guid organizacion, Guid acceso, DateOnly? fecha, CancellationToken ct)
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
                   coalesce(pc.banos_planeados, 0), coalesce(pc.fosas_planeadas, 0), NOT EXISTS (SELECT 1 FROM negocio.autorizacion_empresa a WHERE (a.organizacion_id, a.empresa_id) = (c.organizacion_id, c.empresa_id) AND a.autoridad = 'Medio ambiente' AND a.finalidad = 'Generador')
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
                   nullif(concat_ws(', ', nullif(btrim(concat_ws(' ', fs.calle, fs.numero_exterior)), ''), fs.colonia, fs.municipio), ''),
                   fs.latitud, fs.longitud
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
                l.Add(new ParadaDeOrden(r.GetGuid(1), r.GetInt32(2), T(r, 3), T(r, 4), clientes.GetValueOrDefault(r.GetGuid(1)) ?? [],
                    r.IsDBNull(5) ? null : r.GetDecimal(5), r.IsDBNull(6) ? null : r.GetDecimal(6)));
            }

        // Paradas extraordinarias del día pedido, por ruta.
        var extras = new Dictionary<Guid, List<ParadaExtraordinaria>>();
        if (fecha is { } dia)
        {
            var porParada = new Dictionary<Guid, (Guid Ruta, string? Sitio, string? Dir, decimal? Lat, decimal? Lon, DateTimeOffset Creada, List<ClienteDeOrden> Clientes)>();
            await using (var cmd = tx.Comando("""
                SELECT pe.ruta_id, pe.id, coalesce(nullif(btrim(fs.nombre), ''), s.referencia),
                       nullif(concat_ws(', ', nullif(btrim(concat_ws(' ', fs.calle, fs.numero_exterior)), ''), fs.colonia, fs.municipio), ''),
                       fs.latitud, fs.longitud, pe.creada_en,
                       pc.cliente_id,
                       coalesce(nullif(btrim(fe.nombre_comercial), ''), nullif(btrim(fe.nombre_fiscal), ''), nullif(btrim(c.codigo_comercial), ''), 'Cliente sin nombre'),
                       EXISTS (SELECT 1 FROM negocio.autorizacion_empresa a WHERE (a.organizacion_id, a.empresa_id) = (c.organizacion_id, c.empresa_id)
                               AND a.autoridad = 'Medio ambiente' AND a.finalidad = 'Generador'),
                       coalesce(pc.banos_planeados, 0), coalesce(pc.fosas_planeadas, 0), NOT EXISTS (SELECT 1 FROM negocio.autorizacion_empresa a WHERE (a.organizacion_id, a.empresa_id) = (c.organizacion_id, c.empresa_id) AND a.autoridad = 'Medio ambiente' AND a.finalidad = 'Generador')
                FROM negocio.parada_extraordinaria pe
                JOIN negocio.sitio s ON (s.organizacion_id, s.sucursal_id, s.id) = (pe.organizacion_id, pe.sucursal_id, pe.sitio_id)
                LEFT JOIN LATERAL (SELECT x.* FROM negocio.ficha_sitio x
                    WHERE (x.organizacion_id, x.sucursal_id, x.sitio_id) = (s.organizacion_id, s.sucursal_id, s.id)
                    ORDER BY x.revision DESC LIMIT 1) fs ON true
                JOIN negocio.parada_extraordinaria_cliente pc ON (pc.organizacion_id, pc.sucursal_id, pc.parada_extraordinaria_id) = (pe.organizacion_id, pe.sucursal_id, pe.id)
                JOIN negocio.cliente c ON (c.organizacion_id, c.sucursal_id, c.id) = (pe.organizacion_id, pe.sucursal_id, pc.cliente_id)
                LEFT JOIN LATERAL (SELECT x.nombre_fiscal, x.nombre_comercial FROM negocio.ficha_empresa x
                    WHERE (x.organizacion_id, x.empresa_id) = (c.organizacion_id, c.empresa_id) ORDER BY x.revision DESC LIMIT 1) fe ON true
                LEFT JOIN LATERAL (SELECT x.fosa_propia FROM negocio.cliente_sitio x
                    WHERE (x.organizacion_id, x.sucursal_id, x.cliente_id, x.sitio_id) = (pe.organizacion_id, pe.sucursal_id, pc.cliente_id, pe.sitio_id)
                    ORDER BY x.revision DESC LIMIT 1) cs ON true
                WHERE pe.organizacion_id = $1 AND pe.fecha = $2::date
                ORDER BY pe.creada_en, pe.id, 9
                """, organizacion, dia))
            await using (var r = await cmd.ExecuteReaderAsync(ct))
                while (await r.ReadAsync(ct))
                {
                    var id = r.GetGuid(1);
                    if (!porParada.TryGetValue(id, out var e))
                        porParada[id] = e = (r.GetGuid(0), T(r, 2), T(r, 3), r.IsDBNull(4) ? null : r.GetDecimal(4), r.IsDBNull(5) ? null : r.GetDecimal(5),
                            new DateTimeOffset(DateTime.SpecifyKind(r.GetDateTime(6), DateTimeKind.Utc)), []);
                    e.Clientes.Add(new ClienteDeOrden(r.GetGuid(7), r.GetString(8), r.GetBoolean(9), r.GetInt32(10), r.GetInt32(11), r.GetBoolean(12)));
                }
            foreach (var (id, e) in porParada.OrderBy(x => x.Value.Creada))
            {
                if (!extras.TryGetValue(e.Ruta, out var lista)) extras[e.Ruta] = lista = [];
                lista.Add(new ParadaExtraordinaria(id, dia, e.Sitio, e.Dir, e.Clientes, e.Creada, e.Lat, e.Lon));
            }
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
                    paradas.GetValueOrDefault(r.GetGuid(0)) ?? [], extras.GetValueOrDefault(r.GetGuid(0)) ?? []));
        await tx.ConfirmarAsync(ct);
        return rutas;
    }

    public async Task<ResultadoEscritura> AgregarExtraordinariaAsync(Guid organizacion, Guid acceso, Guid ruta, DateOnly fecha, Guid sitio, IReadOnlyList<DatosClienteParada> clientes, CancellationToken ct)
    {
        await using var tx = await TransaccionContexto.AbrirAsync(datos, organizacion, acceso, ct);
        Guid sucursal; short dias;
        await using (var cmd = tx.Comando("""
            SELECT ru.sucursal_id, rv.dias FROM negocio.ruta ru
            JOIN LATERAL (SELECT x.dias FROM negocio.ruta_version x
                WHERE (x.organizacion_id, x.sucursal_id, x.ruta_id) = (ru.organizacion_id, ru.sucursal_id, ru.id)
                ORDER BY x.revision DESC LIMIT 1) rv ON true
            WHERE ru.organizacion_id = $1 AND ru.id = $2
            """, organizacion, ruta))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
        {
            if (!await r.ReadAsync(ct)) return ResultadoEscritura.NoEncontrada;
            (sucursal, dias) = (r.GetGuid(0), r.GetInt16(1));
        }
        await using (var cmd = tx.Comando("SELECT coalesce(seguridad.puede($1, $2, 'datos.escribir'), false)", organizacion, sucursal))
            if (!(bool)(await cmd.ExecuteScalarAsync(ct))!) return ResultadoEscritura.SinPermiso;
        // La parada extraordinaria va en una salida que existe: la ruta sale ese día de la semana.
        if ((dias & (1 << (((int)fecha.DayOfWeek + 6) % 7))) == 0) return ResultadoEscritura.Invalida(EndpointsOrdenes.MensajeDia);
        await using (var cmd = tx.Comando("SELECT EXISTS (SELECT 1 FROM negocio.sitio WHERE organizacion_id = $1 AND sucursal_id = $2 AND id = $3)", organizacion, sucursal, sitio))
            if (!(bool)(await cmd.ExecuteScalarAsync(ct))!) return ResultadoEscritura.Invalida("No se encontró el sitio.");
        foreach (var c in clientes)
            await using (var cmd = tx.Comando("""
                SELECT coalesce((SELECT x.activo FROM negocio.cliente_sitio x
                    WHERE (x.organizacion_id, x.sucursal_id, x.cliente_id, x.sitio_id) = ($1, $2, $3, $4) ORDER BY x.revision DESC LIMIT 1), false)
                """, organizacion, sucursal, c.ClienteId, sitio))
                if (!(bool)(await cmd.ExecuteScalarAsync(ct))!) return ResultadoEscritura.Invalida(EndpointsOrdenes.MensajeSitio);
        var id = Guid.NewGuid();
        await using (var cmd = tx.Comando("INSERT INTO negocio.parada_extraordinaria (organizacion_id, sucursal_id, id, ruta_id, fecha, sitio_id) VALUES ($1, $2, $3, $4, $5::date, $6)", organizacion, sucursal, id, ruta, fecha, sitio))
            await cmd.ExecuteNonQueryAsync(ct);
        foreach (var c in clientes)
            await using (var cmd = tx.Comando("INSERT INTO negocio.parada_extraordinaria_cliente (organizacion_id, sucursal_id, parada_extraordinaria_id, cliente_id, banos_planeados, fosas_planeadas) VALUES ($1, $2, $3, $4, $5::int, $6::int)",
                organizacion, sucursal, id, c.ClienteId, (object?)c.Banos ?? DBNull.Value, (object?)c.Fosas ?? DBNull.Value))
                await cmd.ExecuteNonQueryAsync(ct);
        await tx.ConfirmarAsync(ct);
        return ResultadoEscritura.Correcta(id, 1);
    }
}

public static class EndpointsOrdenes
{
    public const string MensajeSinPermiso = "No tienes permiso para agregar paradas en esta sucursal.";
    public const string MensajeDia = "La ruta no sale ese día de la semana.";
    public const string MensajeSitio = "Ese cliente no tiene ese sitio registrado.";

    public static IServiceCollection AgregarOrdenes(this IServiceCollection services) =>
        services.AddSingleton<IRepositorioOrdenes, RepositorioOrdenes>();

    public static void MapearOrdenes(this IEndpointRouteBuilder app)
    {
        // ?fecha=AAAA-MM-DD añade las paradas extraordinarias de ese día.
        app.MapGet("/api/ordenes", async (string? fecha, HttpContext http, IRepositorioOrdenes ordenes) =>
        {
            if (!ServiciosAcceso.LeerIds(http.User, out var org, out var acceso)) return Results.Unauthorized();
            DateOnly? dia = null;
            if (fecha is not null)
            {
                if (!DateOnly.TryParseExact(fecha, "yyyy-MM-dd", out var d)) return Results.BadRequest(new MensajeError("Indica la fecha como AAAA-MM-DD."));
                dia = d;
            }
            http.Response.Headers.CacheControl = "no-store";
            return Results.Ok(await ordenes.ListarAsync(org, acceso, dia, http.RequestAborted));
        });

        // Parada extraordinaria: solo de ese día, no se repite en la ruta fija. El aviso al operador queda pendiente (por_avisar).
        app.MapPost("/api/ordenes/extraordinarias", async (SolicitudExtraordinaria s, HttpContext http, IRepositorioOrdenes ordenes) =>
        {
            if (!ServiciosAcceso.LeerIds(http.User, out var org, out var acceso)) return Results.Unauthorized();
            if (s.RutaId is not { } ruta || ruta == Guid.Empty) return Results.BadRequest(new MensajeError("Elige la ruta."));
            if (!DateOnly.TryParseExact(s.Fecha, "yyyy-MM-dd", out var fecha)) return Results.BadRequest(new MensajeError("Indica la fecha como AAAA-MM-DD."));
            if (s.SitioId is not { } sitio || sitio == Guid.Empty) return Results.BadRequest(new MensajeError("Elige el sitio del cliente."));
            var clientes = s.Clientes ?? [];
            if (clientes.Length is 0 or > 50) return Results.BadRequest(new MensajeError("Agrega al menos un cliente."));
            var lista = new List<DatosClienteParada>();
            foreach (var c in clientes)
            {
                if (c?.ClienteId is not { } cliente || cliente == Guid.Empty) return Results.BadRequest(new MensajeError("Elige el cliente."));
                if (c.Banos is < 0 or > 10_000 || c.Fosas is < 0 or > 10_000 || (c.Banos ?? 0) + (c.Fosas ?? 0) == 0)
                    return Results.BadRequest(new MensajeError("Indica al menos un baño o una fosa, de 0 a 10000."));
                lista.Add(new DatosClienteParada(cliente, c.Banos is > 0 ? c.Banos : null, c.Fosas));
            }
            if (lista.Select(c => c.ClienteId).Distinct().Count() != lista.Count) return Results.BadRequest(new MensajeError("Un cliente aparece más de una vez."));
            var r = await ordenes.AgregarExtraordinariaAsync(org, acceso, ruta, fecha, sitio, lista, http.RequestAborted);
            return r.Estado switch
            {
                EstadoEscritura.Correcta => Results.Created($"/api/ordenes/extraordinarias/{r.Id}", new RevisionGuardada(r.Id, r.Revision)),
                EstadoEscritura.NoEncontrada => Results.Json(new MensajeError("No se encontró la ruta."), statusCode: StatusCodes.Status404NotFound),
                EstadoEscritura.SinPermiso => Results.Json(new MensajeError(MensajeSinPermiso), statusCode: StatusCodes.Status403Forbidden),
                _ => Results.BadRequest(new MensajeError(r.Mensaje!)),
            };
        }).AddEndpointFilter(EndpointsAcceso.ValidarCsrf);
    }
}
