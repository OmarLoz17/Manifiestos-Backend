using Manifiestos.Api.Acceso;
using Manifiestos.Api.Datos;
using Manifiestos.Api.Rutas;
using Npgsql;

namespace Manifiestos.Api.Jornadas;

public sealed record JornadaDeRuta(Guid RutaId, DateOnly Fecha, string Estado, int Revision, DateTimeOffset? CambiadoEn);
public sealed record SolicitudEstadoJornada(string? Estado, int? Revision);

public interface IRepositorioJornadas
{
    Task<IReadOnlyList<JornadaDeRuta>> ListarAsync(Guid organizacion, Guid acceso, DateOnly desde, DateOnly hasta, CancellationToken ct);
    Task<ResultadoEscritura> CambiarEstadoAsync(Guid organizacion, Guid acceso, Guid ruta, DateOnly fecha, string estado, int revision, CancellationToken ct);
}

/// <summary>
/// Jornadas de ruta (migración 0019): Operaciones publica la ruta de una fecha para que el operador la vea.
/// Sin fila = borrador. El panel solo cambia entre borrador, programada y cancelada; en_curso y completada son de la app.
/// </summary>
public sealed class RepositorioJornadas(NpgsqlDataSource datos) : IRepositorioJornadas
{
    public const string MensajeModificada = "Alguien más cambió esta jornada. Recarga para ver el estado actual.";
    public const string MensajeSinPermiso = "No tienes permiso para publicar rutas en esta sucursal.";
    public const string MensajeNoEncontrada = "No se encontró la ruta.";
    public const string MensajeDia = "La ruta no sale ese día de la semana.";
    public const string MensajeIncompleta = "Para publicarla la ruta necesita unidad, operador y al menos una parada.";
    public const string MensajeEstado = "El estado debe ser borrador, programada o cancelada.";
    public static readonly string[] EstadosDelPanel = ["borrador", "programada", "cancelada"];

    /// <summary>Transiciones que permite el panel (desde el estado actual).</summary>
    public static bool Permitida(string desde, string hacia) => (desde, hacia) switch
    {
        ("borrador", "programada" or "cancelada") => true,
        ("programada", "borrador" or "cancelada") => true,
        ("cancelada", "borrador") => true,
        _ => false,
    };

    public async Task<IReadOnlyList<JornadaDeRuta>> ListarAsync(Guid organizacion, Guid acceso, DateOnly desde, DateOnly hasta, CancellationToken ct)
    {
        await using var tx = await TransaccionContexto.AbrirAsync(datos, organizacion, acceso, ct);
        var lista = new List<JornadaDeRuta>();
        await using (var cmd = tx.Comando("""
            SELECT j.ruta_id, j.fecha, e.estado, e.revision, e.registrado_en
            FROM negocio.jornada j
            JOIN LATERAL (SELECT x.estado, x.revision, x.registrado_en FROM negocio.estado_jornada x
                WHERE (x.organizacion_id, x.sucursal_id, x.jornada_id) = (j.organizacion_id, j.sucursal_id, j.id)
                ORDER BY x.revision DESC LIMIT 1) e ON true
            WHERE j.organizacion_id = $1 AND j.fecha BETWEEN $2::date AND $3::date
            ORDER BY j.fecha
            """, organizacion, desde, hasta))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct))
                lista.Add(new JornadaDeRuta(r.GetGuid(0), DateOnly.FromDateTime(r.GetDateTime(1)), r.GetString(2), r.GetInt32(3), new DateTimeOffset(DateTime.SpecifyKind(r.GetDateTime(4), DateTimeKind.Utc))));
        await tx.ConfirmarAsync(ct);
        return lista;
    }

    public async Task<ResultadoEscritura> CambiarEstadoAsync(Guid organizacion, Guid acceso, Guid ruta, DateOnly fecha, string estado, int revision, CancellationToken ct)
    {
        try
        {
            await using var tx = await TransaccionContexto.AbrirAsync(datos, organizacion, acceso, ct);
            Guid sucursal; int dias; Guid? vehiculo;
            await using (var cmd = tx.Comando("""
                SELECT ru.sucursal_id, rv.dias, rv.vehiculo_id FROM negocio.ruta ru
                JOIN LATERAL (SELECT x.dias, x.vehiculo_id FROM negocio.ruta_version x
                    WHERE (x.organizacion_id, x.sucursal_id, x.ruta_id) = (ru.organizacion_id, ru.sucursal_id, ru.id)
                    ORDER BY x.revision DESC LIMIT 1) rv ON true
                WHERE ru.organizacion_id = $1 AND ru.id = $2
                """, organizacion, ruta))
            await using (var r = await cmd.ExecuteReaderAsync(ct))
            {
                if (!await r.ReadAsync(ct)) return ResultadoEscritura.NoEncontrada;
                (sucursal, dias, vehiculo) = (r.GetGuid(0), r.GetInt16(1), r.IsDBNull(2) ? null : r.GetGuid(2));
            }
            await using (var cmd = tx.Comando("SELECT coalesce(seguridad.puede($1, $2, 'datos.escribir'), false)", organizacion, sucursal))
                if (!(bool)(await cmd.ExecuteScalarAsync(ct))!) return ResultadoEscritura.SinPermiso;
            if ((dias & (1 << (((int)fecha.DayOfWeek + 6) % 7))) == 0) return ResultadoEscritura.Invalida(MensajeDia);

            await using (var cmd = tx.Comando("SELECT pg_advisory_xact_lock(hashtextextended($1, 0))", $"jornada:{ruta}:{fecha:yyyy-MM-dd}"))
                await cmd.ExecuteNonQueryAsync(ct);
            Guid? jornada = null; int actual = 0; string estadoActual = "borrador";
            await using (var cmd = tx.Comando("""
                SELECT j.id, e.revision, e.estado FROM negocio.jornada j
                JOIN LATERAL (SELECT x.revision, x.estado FROM negocio.estado_jornada x
                    WHERE (x.organizacion_id, x.sucursal_id, x.jornada_id) = (j.organizacion_id, j.sucursal_id, j.id)
                    ORDER BY x.revision DESC LIMIT 1) e ON true
                WHERE j.organizacion_id = $1 AND j.sucursal_id = $2 AND j.ruta_id = $3 AND j.fecha = $4::date
                """, organizacion, sucursal, ruta, fecha))
            await using (var r = await cmd.ExecuteReaderAsync(ct))
                if (await r.ReadAsync(ct)) (jornada, actual, estadoActual) = (r.GetGuid(0), r.GetInt32(1), r.GetString(2));
            if (actual != revision) return ResultadoEscritura.Conflicto(MensajeModificada);
            if (estado == estadoActual) return ResultadoEscritura.Correcta(ruta, actual);
            if (!Permitida(estadoActual, estado)) return ResultadoEscritura.Conflicto($"No se puede pasar de {estadoActual} a {estado}.");

            if (estado == "programada")
            {
                await using var cmd = tx.Comando("""
                    SELECT $4::uuid IS NOT NULL
                       AND EXISTS (SELECT 1 FROM negocio.asignacion_unidad a
                            WHERE (a.organizacion_id, a.sucursal_id, a.vehiculo_id) = ($1, $2, $4)
                              AND a.revision = (SELECT max(b.revision) FROM negocio.asignacion_unidad b
                                    WHERE (b.organizacion_id, b.sucursal_id, b.vehiculo_id) = ($1, $2, $4))
                              AND a.persona_id IS NOT NULL)
                       AND EXISTS (SELECT 1 FROM negocio.parada p WHERE (p.organizacion_id, p.sucursal_id, p.ruta_id) = ($1, $2, $3))
                    """, organizacion, sucursal, ruta, (object?)vehiculo ?? DBNull.Value);
                if (!(bool)(await cmd.ExecuteScalarAsync(ct))!) return ResultadoEscritura.Invalida(MensajeIncompleta);
            }

            if (jornada is null)
            {
                jornada = Guid.NewGuid();
                await using (var cmd = tx.Comando("INSERT INTO negocio.jornada (organizacion_id, sucursal_id, id, ruta_id, fecha) VALUES ($1, $2, $3, $4, $5::date)", organizacion, sucursal, jornada.Value, ruta, fecha))
                    await cmd.ExecuteNonQueryAsync(ct);
                // Toda jornada con fila tiene su revisión 1 en borrador; el cambio pedido es la 2.
                await using (var cmd = tx.Comando("INSERT INTO negocio.estado_jornada (organizacion_id, sucursal_id, jornada_id, revision, estado) VALUES ($1, $2, $3, 1, 'borrador')", organizacion, sucursal, jornada.Value))
                    await cmd.ExecuteNonQueryAsync(ct);
                actual = 1;
            }
            await using (var cmd = tx.Comando("INSERT INTO negocio.estado_jornada (organizacion_id, sucursal_id, jornada_id, revision, estado) VALUES ($1, $2, $3, $4, $5)", organizacion, sucursal, jornada.Value, actual + 1, estado))
                await cmd.ExecuteNonQueryAsync(ct);
            await tx.ConfirmarAsync(ct);
            return ResultadoEscritura.Correcta(ruta, actual + 1);
        }
        catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.UniqueViolation) { return ResultadoEscritura.Conflicto(MensajeModificada); }
    }
}

/// <summary>Publicación de rutas por fecha: GET lista las jornadas con fila; PUT cambia el estado de una.</summary>
public static class EndpointsJornadas
{
    public static IServiceCollection AgregarJornadas(this IServiceCollection services) =>
        services.AddSingleton<IRepositorioJornadas, RepositorioJornadas>();

    public static void MapearJornadas(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/jornadas");
        // ?fecha=AAAA-MM-DD (un día) o ?desde=AAAA-MM-DD&hasta=AAAA-MM-DD (hasta 31 días).
        g.MapGet("", async (string? fecha, string? desde, string? hasta, HttpContext http, IRepositorioJornadas jornadas) =>
        {
            if (!ServiciosAcceso.LeerIds(http.User, out var org, out var acceso)) return Results.Unauthorized();
            DateOnly d, h;
            if (fecha is not null)
            {
                if (!DateOnly.TryParseExact(fecha, "yyyy-MM-dd", out d)) return Results.BadRequest(new MensajeError("Indica la fecha como AAAA-MM-DD."));
                h = d;
            }
            else if (!DateOnly.TryParseExact(desde, "yyyy-MM-dd", out d) || !DateOnly.TryParseExact(hasta, "yyyy-MM-dd", out h))
                return Results.BadRequest(new MensajeError("Indica fecha, o desde y hasta como AAAA-MM-DD."));
            if (h < d || h.DayNumber - d.DayNumber > 31) return Results.BadRequest(new MensajeError("El rango va de 1 a 32 días."));
            http.Response.Headers.CacheControl = "no-store";
            return Results.Ok(await jornadas.ListarAsync(org, acceso, d, h, http.RequestAborted));
        });
        g.MapPut("/{ruta:guid}/{fecha}/estado", async (Guid ruta, string fecha, SolicitudEstadoJornada s, HttpContext http, IRepositorioJornadas jornadas) =>
        {
            if (!ServiciosAcceso.LeerIds(http.User, out var org, out var acceso)) return Results.Unauthorized();
            if (!DateOnly.TryParseExact(fecha, "yyyy-MM-dd", out var f)) return Results.BadRequest(new MensajeError("Indica la fecha como AAAA-MM-DD."));
            if (s.Estado is not { } estado || !RepositorioJornadas.EstadosDelPanel.Contains(estado)) return Results.BadRequest(new MensajeError(RepositorioJornadas.MensajeEstado));
            if (s.Revision is not >= 0) return Results.BadRequest(new MensajeError("Falta la revisión que se vio."));
            var r = await jornadas.CambiarEstadoAsync(org, acceso, ruta, f, estado, s.Revision.Value, http.RequestAborted);
            return r.Estado switch
            {
                EstadoEscritura.Correcta => Results.Ok(new RevisionGuardada(r.Id, r.Revision)),
                EstadoEscritura.NoEncontrada => Results.Json(new MensajeError(RepositorioJornadas.MensajeNoEncontrada), statusCode: StatusCodes.Status404NotFound),
                EstadoEscritura.SinPermiso => Results.Json(new MensajeError(RepositorioJornadas.MensajeSinPermiso), statusCode: StatusCodes.Status403Forbidden),
                EstadoEscritura.Conflicto => Results.Json(new MensajeError(r.Mensaje!), statusCode: StatusCodes.Status409Conflict),
                _ => Results.BadRequest(new MensajeError(r.Mensaje!)),
            };
        }).AddEndpointFilter(EndpointsAcceso.ValidarCsrf);
    }
}
