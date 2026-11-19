using Manifiestos.Api.Acceso;
using Manifiestos.Api.Rutas;

namespace Manifiestos.Api.Sitios;

/// <summary>
/// Pestaña «Sitios» de Clientes: los sitios normales (cada sitio = una parada posible) donde van los clientes generales.
/// Los sitios de un cliente autorizado se administran dentro del cliente.
/// </summary>
public static class EndpointsSitios
{
    public const int LargoMaximoCampo = 200;
    public const string MensajeSinPermiso = "No tienes permiso para modificar sitios en esta sucursal. Eliminar es solo del administrador.";
    public const string MensajeNoEncontrado = "No se encontró el sitio.";

    public static IServiceCollection AgregarSitios(this IServiceCollection services) =>
        services.AddSingleton<IRepositorioSitios, RepositorioSitios>();

    public static void MapearSitios(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/sitios");
        g.MapGet("", ListarAsync);
        g.MapPost("", CrearAsync).AddEndpointFilter(EndpointsAcceso.ValidarCsrf);
        g.MapPut("/{id:guid}", EditarAsync).AddEndpointFilter(EndpointsAcceso.ValidarCsrf);
        g.MapDelete("/{id:guid}", EliminarAsync).AddEndpointFilter(EndpointsAcceso.ValidarCsrf);
    }

    private static async Task<IResult> ListarAsync(HttpContext http, IRepositorioSitios sitios)
    {
        if (!ServiciosAcceso.LeerIds(http.User, out var org, out var acceso)) return Results.Unauthorized();
        http.Response.Headers.CacheControl = "no-store";
        return Results.Ok(await sitios.ListarAsync(org, acceso, http.RequestAborted));
    }

    private static async Task<IResult> CrearAsync(SolicitudSitio s, HttpContext http, IRepositorioSitios sitios)
    {
        if (!ServiciosAcceso.LeerIds(http.User, out var org, out var acceso)) return Results.Unauthorized();
        if (s.SucursalId is not { } sucursal || sucursal == Guid.Empty) return Error("Falta la sucursal.");
        if (Validar(s, out var datos) is { } problema) return Error(problema);
        return Responder(await sitios.CrearAsync(org, acceso, sucursal, datos!, http.RequestAborted), true);
    }

    private static async Task<IResult> EditarAsync(Guid id, SolicitudSitio s, HttpContext http, IRepositorioSitios sitios)
    {
        if (!ServiciosAcceso.LeerIds(http.User, out var org, out var acceso)) return Results.Unauthorized();
        if (s.Revision is not >= 0) return Error("Falta la revisión que se editó.");
        if (Validar(s, out var datos) is { } problema) return Error(problema);
        return Responder(await sitios.EditarAsync(org, acceso, id, datos!, s.Revision.Value, http.RequestAborted), false);
    }

    private static async Task<IResult> EliminarAsync(Guid id, HttpContext http, IRepositorioSitios sitios)
    {
        if (!ServiciosAcceso.LeerIds(http.User, out var org, out var acceso)) return Results.Unauthorized();
        var r = await sitios.EliminarAsync(org, acceso, id, http.RequestAborted);
        return r.Estado == EstadoEscritura.Correcta ? Results.NoContent() : Responder(r, false);
    }

    /// <summary>Recorta, vacía en null y valida: nombre o dirección, ubicación obligatoria (ambas coordenadas) y largos.</summary>
    private static string? Validar(SolicitudSitio s, out DatosSitio? datos)
    {
        datos = null;
        string? T(string? t) => string.IsNullOrWhiteSpace(t) ? null : t.Trim();
        var campos = new[] { T(s.Nombre), T(s.Calle), T(s.NumeroExterior), T(s.Colonia), T(s.CodigoPostal), T(s.Municipio), T(s.Estado) };
        if (campos.All(c => c is null)) return "Escribe el nombre o la dirección del sitio.";
        if (campos.Any(c => c?.Length > LargoMaximoCampo)) return $"Cada dato del sitio admite hasta {LargoMaximoCampo} caracteres.";
        if (s.Latitud is null || s.Longitud is null) return "Elige la ubicación del sitio en el mapa.";
        if (s.Latitud is < -90 or > 90 || s.Longitud is < -180 or > 180) return "La latitud va de -90 a 90 y la longitud de -180 a 180.";
        datos = new DatosSitio(campos[0], campos[1], campos[2], campos[3], campos[4], campos[5], campos[6],
            Math.Round(s.Latitud.Value, 6), Math.Round(s.Longitud.Value, 6));
        return null;
    }

    private static IResult Responder(ResultadoEscritura r, bool creada) => r.Estado switch
    {
        EstadoEscritura.Correcta when creada => Results.Created($"/api/sitios/{r.Id}", new RevisionGuardada(r.Id, r.Revision)),
        EstadoEscritura.Correcta => Results.Ok(new RevisionGuardada(r.Id, r.Revision)),
        EstadoEscritura.NoEncontrada => Results.Json(new MensajeError(MensajeNoEncontrado), statusCode: StatusCodes.Status404NotFound),
        EstadoEscritura.SinPermiso => Results.Json(new MensajeError(MensajeSinPermiso), statusCode: StatusCodes.Status403Forbidden),
        EstadoEscritura.Conflicto => Results.Json(new MensajeError(r.Mensaje!), statusCode: StatusCodes.Status409Conflict),
        EstadoEscritura.Regla => Results.Json(new ErrorNegocio(r.Codigo!, r.Mensaje!), statusCode: StatusCodes.Status422UnprocessableEntity),
        _ => Error(r.Mensaje!),
    };

    private static IResult Error(string mensaje) => Results.BadRequest(new MensajeError(mensaje));
}
