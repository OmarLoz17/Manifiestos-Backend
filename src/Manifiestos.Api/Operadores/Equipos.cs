using Manifiestos.Api.Acceso;
using Manifiestos.Api.Datos;
using Npgsql;

namespace Manifiestos.Api.Operadores;

// ---- Respuestas (JSON camelCase) ----

/// <summary>Solicitud pendiente de un teléfono. La persona es la de la ficha vigente del correo; PersonaActual, si el equipo ya es de otra persona.</summary>
public sealed record SolicitudEquipo(
    Guid Id, string Correo, DateTime SolicitadaEn, DateTime VenceEn, Guid SucursalId, Guid PersonaId, string Nombre, string CodigoEquipo, string? PersonaActual);

public sealed record EquipoEntregado(
    Guid DispositivoId, Guid SucursalId, Guid PersonaId, string Nombre, string Correo, DateTime Desde, int Revision, bool Habilitado, string CodigoEquipo);

public sealed record ListaEquipos(IReadOnlyList<SolicitudEquipo> Solicitudes, IReadOnlyList<EquipoEntregado> Equipos);
public sealed record SolicitudAprobarEquipo(bool? ConfirmarReasignacion);

public enum ResultadoResolucion { Aprobada, Rechazada, ConfirmarReasignacion, NoEncontrada, EquipoDeshabilitado, SinPermiso }

public interface IRepositorioEquipos
{
    Task<ListaEquipos> ListarAsync(Guid organizacion, Guid acceso, CancellationToken ct);
    Task<ResultadoResolucion> AprobarAsync(Guid organizacion, Guid acceso, Guid solicitud, bool reasignar, CancellationToken ct);
    Task<ResultadoResolucion> RechazarAsync(Guid organizacion, Guid acceso, Guid solicitud, CancellationToken ct);
}

/// <summary>
/// Equipos (migración 0070): solicitudes que llegan de la app del teléfono y entregas vigentes. Todo pasa por funciones de la base
/// que exigen datos.leer / datos.escribir en la sucursal del operador. Aprobar crea la instalación y la asignación versionada;
/// reasignar un equipo cierra la entrega anterior y no toca lo capturado.
/// </summary>
public sealed class RepositorioEquipos(NpgsqlDataSource datos) : IRepositorioEquipos
{
    public async Task<ListaEquipos> ListarAsync(Guid organizacion, Guid acceso, CancellationToken ct)
    {
        await using var tx = await TransaccionContexto.AbrirAsync(datos, organizacion, acceso, ct);
        var solicitudes = new List<SolicitudEquipo>();
        await using (var cmd = tx.Comando("SELECT id, correo, solicitada_en, vence_en, sucursal_id, persona_id, nombre, codigo_equipo, persona_actual FROM seguridad.solicitudes_equipo_pendientes($1)", organizacion))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct))
                solicitudes.Add(new SolicitudEquipo(r.GetGuid(0), r.GetString(1), r.GetDateTime(2), r.GetDateTime(3), r.GetGuid(4), r.GetGuid(5),
                    r.GetString(6), r.GetString(7), r.IsDBNull(8) ? null : r.GetString(8)));
        var equipos = new List<EquipoEntregado>();
        await using (var cmd = tx.Comando("SELECT dispositivo_id, sucursal_id, persona_id, nombre, correo, desde, revision, habilitado, codigo_equipo FROM seguridad.equipos_entregados($1)", organizacion))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct))
                equipos.Add(new EquipoEntregado(r.GetGuid(0), r.GetGuid(1), r.GetGuid(2), r.GetString(3), r.GetString(4), r.GetDateTime(5), r.GetInt32(6), r.GetBoolean(7), r.GetString(8)));
        await tx.ConfirmarAsync(ct);
        return new ListaEquipos(solicitudes, equipos);
    }

    public async Task<ResultadoResolucion> AprobarAsync(Guid organizacion, Guid acceso, Guid solicitud, bool reasignar, CancellationToken ct)
    {
        await using var tx = await TransaccionContexto.AbrirAsync(datos, organizacion, acceso, ct);
        try
        {
            string resultado;
            await using (var cmd = tx.Comando("SELECT seguridad.aprobar_solicitud_equipo($1, $2, $3)", organizacion, solicitud, reasignar))
                resultado = (string)(await cmd.ExecuteScalarAsync(ct))!;
            // Sin confirmación no hay cambios que conservar: la transacción se descarta al salir.
            if (resultado == "aprobada") { await tx.ConfirmarAsync(ct); return ResultadoResolucion.Aprobada; }
            return resultado switch
            {
                "confirmar_reasignacion" => ResultadoResolucion.ConfirmarReasignacion,
                "equipo_deshabilitado" => ResultadoResolucion.EquipoDeshabilitado,
                _ => ResultadoResolucion.NoEncontrada,
            };
        }
        catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.InsufficientPrivilege) { return ResultadoResolucion.SinPermiso; }
    }

    public async Task<ResultadoResolucion> RechazarAsync(Guid organizacion, Guid acceso, Guid solicitud, CancellationToken ct)
    {
        await using var tx = await TransaccionContexto.AbrirAsync(datos, organizacion, acceso, ct);
        try
        {
            string resultado;
            await using (var cmd = tx.Comando("SELECT seguridad.rechazar_solicitud_equipo($1, $2)", organizacion, solicitud))
                resultado = (string)(await cmd.ExecuteScalarAsync(ct))!;
            if (resultado != "rechazada") return ResultadoResolucion.NoEncontrada;
            await tx.ConfirmarAsync(ct);
            return ResultadoResolucion.Rechazada;
        }
        catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.InsufficientPrivilege) { return ResultadoResolucion.SinPermiso; }
    }
}

public static class EndpointsEquipos
{
    public const string MensajeSinPermiso = "No tienes permiso para administrar equipos en esta sucursal.";
    public const string MensajeNoDisponible = "La solicitud ya no está disponible.";
    public const string MensajeReasignacion = "Este equipo ya está entregado a otra persona. Confirma la reasignación.";
    public const string MensajeDeshabilitado = "Este equipo está deshabilitado y no se puede aprobar.";

    public static IServiceCollection AgregarEquipos(this IServiceCollection services) =>
        services.AddSingleton<IRepositorioEquipos, RepositorioEquipos>();

    public static void MapearEquipos(this IEndpointRouteBuilder app)
    {
        // Sin AllowAnonymous: la política por defecto exige sesión web revalidada; un Bearer móvil no la satisface.
        var g = app.MapGroup("/api/equipos");
        g.MapGet("", async (HttpContext http, IRepositorioEquipos equipos) =>
        {
            if (!ServiciosAcceso.LeerIds(http.User, out var org, out var acceso)) return Results.Unauthorized();
            http.Response.Headers.CacheControl = "no-store";
            return Results.Ok(await equipos.ListarAsync(org, acceso, http.RequestAborted));
        });
        g.MapPost("/solicitudes/{id:guid}/aprobar", async (Guid id, SolicitudAprobarEquipo? s, HttpContext http, IRepositorioEquipos equipos) =>
        {
            if (!ServiciosAcceso.LeerIds(http.User, out var org, out var acceso)) return Results.Unauthorized();
            return Responder(await equipos.AprobarAsync(org, acceso, id, s?.ConfirmarReasignacion ?? false, http.RequestAborted));
        }).AddEndpointFilter(EndpointsAcceso.ValidarCsrf);
        g.MapPost("/solicitudes/{id:guid}/rechazar", async (Guid id, HttpContext http, IRepositorioEquipos equipos) =>
        {
            if (!ServiciosAcceso.LeerIds(http.User, out var org, out var acceso)) return Results.Unauthorized();
            return Responder(await equipos.RechazarAsync(org, acceso, id, http.RequestAborted));
        }).AddEndpointFilter(EndpointsAcceso.ValidarCsrf);
    }

    private static IResult Responder(ResultadoResolucion r) => r switch
    {
        ResultadoResolucion.Aprobada => Results.Ok(new { estado = "aprobada" }),
        ResultadoResolucion.Rechazada => Results.Ok(new { estado = "rechazada" }),
        ResultadoResolucion.ConfirmarReasignacion => Results.Json(new MensajeError(MensajeReasignacion), statusCode: StatusCodes.Status409Conflict),
        ResultadoResolucion.EquipoDeshabilitado => Results.Json(new MensajeError(MensajeDeshabilitado), statusCode: StatusCodes.Status409Conflict),
        ResultadoResolucion.SinPermiso => Results.Json(new MensajeError(MensajeSinPermiso), statusCode: StatusCodes.Status403Forbidden),
        _ => Results.Json(new MensajeError(MensajeNoDisponible), statusCode: StatusCodes.Status404NotFound),
    };
}
