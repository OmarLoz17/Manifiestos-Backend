using Manifiestos.Api.Acceso;
using Manifiestos.Api.Rutas;

namespace Manifiestos.Api.Sitios;

/// <summary>Pestaña «Plantas tratadoras» de Sitios y plantas: sitios de descarga (nunca son parada).</summary>
public static class EndpointsPlantas
{
    public const string MensajeSinPermiso = "No tienes permiso para modificar plantas en esta sucursal. Eliminar es solo del administrador.";
    public const string MensajeNoEncontrada = "No se encontró la planta.";

    public static IServiceCollection AgregarPlantas(this IServiceCollection services) =>
        services.AddSingleton<IRepositorioPlantas, RepositorioPlantas>();

    public static void MapearPlantas(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/plantas");
        g.MapGet("", async (HttpContext http, IRepositorioPlantas plantas) =>
        {
            if (!ServiciosAcceso.LeerIds(http.User, out var org, out var acceso)) return Results.Unauthorized();
            http.Response.Headers.CacheControl = "no-store";
            return Results.Ok(await plantas.ListarAsync(org, acceso, http.RequestAborted));
        });
        g.MapPost("", async (SolicitudPlanta s, HttpContext http, IRepositorioPlantas plantas) =>
        {
            if (!ServiciosAcceso.LeerIds(http.User, out var org, out var acceso)) return Results.Unauthorized();
            if (s.SucursalId is not { } sucursal || sucursal == Guid.Empty) return Error("Falta la sucursal.");
            if (Validar(s, out var datos) is { } problema) return Error(problema);
            return Responder(await plantas.CrearAsync(org, acceso, sucursal, datos!, http.RequestAborted), true);
        }).AddEndpointFilter(EndpointsAcceso.ValidarCsrf);
        g.MapPut("/{id:guid}", async (Guid id, SolicitudPlanta s, HttpContext http, IRepositorioPlantas plantas) =>
        {
            if (!ServiciosAcceso.LeerIds(http.User, out var org, out var acceso)) return Results.Unauthorized();
            if (s.Revision is not >= 0) return Error("Falta la revisión que se editó.");
            if (Validar(s, out var datos) is { } problema) return Error(problema);
            return Responder(await plantas.EditarAsync(org, acceso, id, datos!, s.Revision.Value, http.RequestAborted), false);
        }).AddEndpointFilter(EndpointsAcceso.ValidarCsrf);
        g.MapDelete("/{id:guid}", async (Guid id, HttpContext http, IRepositorioPlantas plantas) =>
        {
            if (!ServiciosAcceso.LeerIds(http.User, out var org, out var acceso)) return Results.Unauthorized();
            var r = await plantas.EliminarAsync(org, acceso, id, http.RequestAborted);
            return r.Estado == EstadoEscritura.Correcta ? Results.NoContent() : Responder(r, false);
        }).AddEndpointFilter(EndpointsAcceso.ValidarCsrf);
    }

    /// <summary>El nombre es obligatorio; la dirección es opcional y la ubicación va completa (latitud y longitud) o no va.</summary>
    private static string? Validar(SolicitudPlanta s, out DatosSitio? datos)
    {
        datos = null;
        string? T(string? t) => string.IsNullOrWhiteSpace(t) ? null : t.Trim();
        var campos = new[] { T(s.Nombre), T(s.Calle), T(s.NumeroExterior), T(s.Colonia), T(s.CodigoPostal), T(s.Municipio), T(s.Estado) };
        if (campos[0] is null) return "Escribe el nombre de la planta.";
        if (campos.Any(c => c?.Length > EndpointsSitios.LargoMaximoCampo)) return $"Cada dato de la planta admite hasta {EndpointsSitios.LargoMaximoCampo} caracteres.";
        if ((s.Latitud is null) != (s.Longitud is null)) return "La ubicación necesita latitud y longitud.";
        if (s.Latitud is < -90 or > 90 || s.Longitud is < -180 or > 180) return "La latitud va de -90 a 90 y la longitud de -180 a 180.";
        datos = new DatosSitio(campos[0], campos[1], campos[2], campos[3], campos[4], campos[5], campos[6],
            s.Latitud is { } la ? Math.Round(la, 6) : null, s.Longitud is { } lo ? Math.Round(lo, 6) : null);
        return null;
    }

    private static IResult Responder(ResultadoEscritura r, bool creada) => r.Estado switch
    {
        EstadoEscritura.Correcta when creada => Results.Created($"/api/plantas/{r.Id}", new RevisionGuardada(r.Id, r.Revision)),
        EstadoEscritura.Correcta => Results.Ok(new RevisionGuardada(r.Id, r.Revision)),
        EstadoEscritura.NoEncontrada => Results.Json(new MensajeError(MensajeNoEncontrada), statusCode: StatusCodes.Status404NotFound),
        EstadoEscritura.SinPermiso => Results.Json(new MensajeError(MensajeSinPermiso), statusCode: StatusCodes.Status403Forbidden),
        EstadoEscritura.Conflicto => Results.Json(new MensajeError(r.Mensaje!), statusCode: StatusCodes.Status409Conflict),
        EstadoEscritura.Regla => Results.Json(new ErrorNegocio(r.Codigo!, r.Mensaje!), statusCode: StatusCodes.Status422UnprocessableEntity),
        _ => Error(r.Mensaje!),
    };

    private static IResult Error(string mensaje) => Results.BadRequest(new MensajeError(mensaje));
}
