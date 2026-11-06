using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Extensions.Options;

namespace Manifiestos.Api.Acceso;

public sealed record SolicitudGoogle(string? Credential);
public sealed record RespuestaCsrf(string Token);
public sealed record RolDto(string Codigo, string Nombre);
public sealed record SucursalDto(Guid Id, string Nombre);
public sealed record UsuarioActual(string Nombre, string Correo, RolDto Rol, IReadOnlyList<SucursalDto> Sucursales);
public sealed record MensajeError(string Mensaje);

public static class EndpointsAcceso
{
    /// <summary>Mismo texto para toda cuenta rechazada: no revela si el correo existe en la lista.</summary>
    public const string MensajeNoAutorizada = "Esta cuenta no está autorizada. Pide a Administración que la habilite.";
    public const string MensajeTokenInvalido = "No se pudo verificar la cuenta de Google. Intenta de nuevo.";

    public static void MapearAcceso(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/auth");

        // Token CSRF para la cabecera X-CSRF-TOKEN. Pedir uno nuevo después de iniciar o cerrar sesión.
        g.MapGet("/csrf", (HttpContext http, IAntiforgery af) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            return Results.Ok(new RespuestaCsrf(af.GetAndStoreTokens(http).RequestToken!));
        }).AllowAnonymous();

        g.MapPost("/google", IniciarConGoogleAsync)
            .AllowAnonymous()
            .AddEndpointFilter(ValidarCsrf)
            .RequireRateLimiting(ServiciosAcceso.PoliticaLogin);

        g.MapGet("/me", ObtenerUsuarioAsync);

        g.MapPost("/logout", async (HttpContext http) =>
        {
            await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return Results.NoContent();
        }).AllowAnonymous().AddEndpointFilter(ValidarCsrf);
    }

    private static async Task<IResult> IniciarConGoogleAsync(
        SolicitudGoogle solicitud, HttpContext http, IValidadorTokenGoogle validador,
        IRepositorioAcceso repositorio, IOptions<OpcionesAcceso> opciones, ILoggerFactory logs)
    {
        var log = logs.CreateLogger("Manifiestos.Acceso");
        var ct = http.RequestAborted;
        var identidad = await validador.ValidarAsync(solicitud.Credential ?? "", ct);
        if (identidad is null)
            return Results.Json(new MensajeError(MensajeTokenInvalido), statusCode: StatusCodes.Status401Unauthorized);

        // Un correo no verificado nunca se usa para buscar ni vincular una entrada.
        if (!identidad.CorreoVerificado)
        {
            log.LogInformation("Inicio web rechazado: correo no verificado");
            return NoAutorizada();
        }

        var org = opciones.Value.OrganizacionId;
        var resultado = await repositorio.IniciarSesionWebAsync(org, identidad, ct);
        if (!resultado.Aceptado)
        {
            log.LogInformation("Inicio web rechazado: {Resultado}", resultado.Resultado);
            return NoAutorizada();
        }

        var nombre = string.IsNullOrWhiteSpace(identidad.Nombre) ? identidad.Correo : identidad.Nombre.Trim();
        await http.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme,
            ServiciosAcceso.CrearPrincipal(org, resultado.AccesoId!.Value, nombre));
        return Results.NoContent();
    }

    private static async Task<IResult> ObtenerUsuarioAsync(HttpContext http, IRepositorioAcceso repositorio)
    {
        if (!ServiciosAcceso.LeerIds(http.User, out var org, out var acceso)) return Results.Unauthorized();
        var sesion = http.Items[ServiciosAcceso.ItemSesion] as SesionVigente
            ?? await repositorio.ObtenerSesionAsync(org, acceso, http.RequestAborted);
        if (sesion is null) return Results.Unauthorized();
        var sucursales = await repositorio.ListarSucursalesAsync(org, acceso, http.RequestAborted);
        http.Response.Headers.CacheControl = "no-store";
        return Results.Ok(new UsuarioActual(
            http.User.Identity?.Name ?? sesion.Correo,
            sesion.Correo,
            new RolDto(sesion.RolCodigo, sesion.RolNombre),
            sucursales.Select(s => new SucursalDto(s.Id, s.Nombre)).ToList()));
    }

    private static IResult NoAutorizada() =>
        Results.Json(new MensajeError(MensajeNoAutorizada), statusCode: StatusCodes.Status403Forbidden);

    internal static async ValueTask<object?> ValidarCsrf(EndpointFilterInvocationContext c, EndpointFilterDelegate next)
    {
        var af = c.HttpContext.RequestServices.GetRequiredService<IAntiforgery>();
        if (!await af.IsRequestValidAsync(c.HttpContext))
            return Results.Json(new MensajeError("La solicitud no es válida. Recarga la página."), statusCode: StatusCodes.Status400BadRequest);
        return await next(c);
    }
}
