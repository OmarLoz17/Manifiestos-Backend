using Manifiestos.Api.Acceso;
using Manifiestos.Api.Datos;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Manifiestos.Api.Legal;

/// <summary>Legal:ExigirAceptacion (por omisión sí). Solo las pruebas automatizadas y los entornos de demostración lo apagan.</summary>
public sealed class OpcionesLegal
{
    public const string Seccion = "Legal";
    public bool ExigirAceptacion { get; set; } = true;
}

public sealed record EstadoDocumento(string Id, string Titulo, string Version, bool Aceptado);
public sealed record EstadoLegal(bool Exigida, IReadOnlyList<EstadoDocumento> Documentos);
public sealed record DocumentoAceptado(string Id, string? Version);
public sealed record SolicitudAceptar(List<DocumentoAceptado>? Documentos);

public interface IRepositorioAceptacion
{
    Task<IReadOnlyList<(string Documento, string Version, string Huella)>> AceptadasAsync(Guid org, Guid acceso, CancellationToken ct);
    Task<bool> AceptarAsync(Guid org, Guid acceso, DocumentoLegalVigente doc, string canal, CancellationToken ct);
}

public sealed class RepositorioAceptacion(NpgsqlDataSource datos) : IRepositorioAceptacion
{
    public async Task<IReadOnlyList<(string Documento, string Version, string Huella)>> AceptadasAsync(Guid org, Guid acceso, CancellationToken ct)
    {
        var lista = new List<(string, string, string)>();
        await using var cmd = datos.CreateCommand("SELECT documento, version, huella FROM seguridad.aceptaciones_de($1, $2)");
        cmd.Parameters.AddWithValue(org); cmd.Parameters.AddWithValue(acceso);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) lista.Add((r.GetString(0), r.GetString(1), r.GetString(2)));
        return lista;
    }

    public async Task<bool> AceptarAsync(Guid org, Guid acceso, DocumentoLegalVigente doc, string canal, CancellationToken ct)
    {
        await using var cmd = datos.CreateCommand("SELECT seguridad.aceptar_documento($1, $2, $3, $4, $5, $6)");
        cmd.Parameters.AddWithValue(org); cmd.Parameters.AddWithValue(acceso); cmd.Parameters.AddWithValue(doc.Id);
        cmd.Parameters.AddWithValue(doc.Version); cmd.Parameters.AddWithValue(doc.Huella); cmd.Parameters.AddWithValue(canal);
        return (bool)(await cmd.ExecuteScalarAsync(ct))!;
    }
}

/// <summary>Qué documentos vigentes le faltan por aceptar a una cuenta: los que no tienen aceptada esa versión con esa huella de texto.</summary>
public sealed class ServicioAceptacion(IRepositorioAceptacion repo, IOptions<OpcionesLegal> opciones)
{
    public bool Exigida => opciones.Value.ExigirAceptacion;

    public async Task<EstadoLegal> EstadoAsync(Guid org, Guid acceso, CancellationToken ct)
    {
        var aceptadas = await repo.AceptadasAsync(org, acceso, ct);
        var docs = DocumentosLegalesVigentes.Todos
            .Select(d => new EstadoDocumento(d.Id, d.Titulo, d.Version, aceptadas.Any(a => a.Documento == d.Id && a.Version == d.Version && a.Huella == d.Huella)))
            .ToList();
        return new EstadoLegal(Exigida, docs);
    }

    public async Task<bool> HayPendientesAsync(Guid org, Guid acceso, CancellationToken ct) =>
        Exigida && (await EstadoAsync(org, acceso, ct)).Documentos.Any(d => !d.Aceptado);

    /// <summary>Acepta los documentos pedidos, siempre en su versión vigente: si el cliente mostró otra versión, no se registra nada (409).</summary>
    public async Task<IResult> AceptarAsync(Guid org, Guid acceso, SolicitudAceptar? s, string canal, CancellationToken ct)
    {
        var pedidos = s?.Documentos ?? [];
        if (pedidos.Count == 0 || pedidos.Select(p => p.Id).Distinct().Count() != pedidos.Count) return Results.BadRequest(new MensajeError("Indica los documentos que aceptas."));
        var elegidos = new List<DocumentoLegalVigente>();
        foreach (var p in pedidos)
        {
            var doc = DocumentosLegalesVigentes.Todos.FirstOrDefault(d => d.Id == p.Id);
            if (doc is null) return Results.BadRequest(new MensajeError("Documento desconocido."));
            if (p.Version != doc.Version)
                return Results.Json(new { mensaje = "Los documentos cambiaron. Recarga para leer la versión vigente.", codigo = "version_cambio" }, statusCode: StatusCodes.Status409Conflict);
            elegidos.Add(doc);
        }
        foreach (var doc in elegidos)
            if (!await repo.AceptarAsync(org, acceso, doc, canal, ct)) return Results.Forbid();
        return Results.Ok(await EstadoAsync(org, acceso, ct));
    }
}

public static class EndpointsLegal
{
    public const string CodigoPendiente = "aceptacion_pendiente";
    public const string MensajePendiente = "Acepta los términos y condiciones y el aviso de privacidad para continuar.";

    public static IServiceCollection AgregarAceptacionLegal(this IServiceCollection services, IConfiguration config)
    {
        services.AddOptions<OpcionesLegal>().Bind(config.GetSection(OpcionesLegal.Seccion));
        services.AddSingleton<IRepositorioAceptacion, RepositorioAceptacion>();
        services.AddSingleton<ServicioAceptacion>();
        return services;
    }

    private static IResult Pendiente() =>
        Results.Json(new { mensaje = MensajePendiente, codigo = CodigoPendiente }, statusCode: StatusCodes.Status403Forbidden);

    /// <summary>Sin aceptar la versión vigente no se usa la API: ni el panel (cookie) ni la app (token). Solo quedan abiertas la sesión, la propia aceptación y la versión de la app.</summary>
    public static IApplicationBuilder UsarAceptacionLegal(this IApplicationBuilder app) =>
        app.Use(async (http, next) =>
        {
            var ruta = http.Request.Path;
            if (ruta.StartsWithSegments("/api"))
            {
                var servicio = http.RequestServices.GetRequiredService<ServicioAceptacion>();
                if (servicio.Exigida)
                {
                    Guid org = default, acceso = default;
                    bool aplica;
                    if (ruta.StartsWithSegments("/api/movil"))
                    {
                        aplica = !ruta.StartsWithSegments("/api/movil/auth") && !ruta.StartsWithSegments("/api/movil/app")
                                 && http.Items[AccesoMovil.ItemContextoPublico] is ContextoOperador c && ((org = http.RequestServices.GetRequiredService<IOptions<OpcionesAcceso>>().Value.OrganizacionId) != default) && ((acceso = c.AccesoId) != default);
                    }
                    else
                    {
                        aplica = !ruta.StartsWithSegments("/api/auth") && !ruta.StartsWithSegments("/api/legal") && !ruta.StartsWithSegments("/api/validacion")
                                 && !ruta.StartsWithSegments("/api/errores-cliente") && !ruta.StartsWithSegments("/api/salud") && !ruta.StartsWithSegments("/api/geografia")
                                 && ServiciosAcceso.LeerIds(http.User, out org, out acceso);
                    }
                    if (aplica && await servicio.HayPendientesAsync(org, acceso, http.RequestAborted))
                    {
                        await Pendiente().ExecuteAsync(http);
                        return;
                    }
                }
            }
            await next();
        });

    public static void MapearLegal(this IEndpointRouteBuilder app)
    {
        var web = app.MapGroup("/api/legal");
        web.MapGet("/estado", async (HttpContext http, ServicioAceptacion s) =>
        {
            if (!ServiciosAcceso.LeerIds(http.User, out var org, out var acceso)) return Results.Unauthorized();
            http.Response.Headers.CacheControl = "no-store";
            return Results.Ok(await s.EstadoAsync(org, acceso, http.RequestAborted));
        });
        web.MapPost("/aceptar", async (SolicitudAceptar? solicitud, HttpContext http, ServicioAceptacion s) =>
        {
            if (!ServiciosAcceso.LeerIds(http.User, out var org, out var acceso)) return Results.Unauthorized();
            return await s.AceptarAsync(org, acceso, solicitud, "web", http.RequestAborted);
        }).AddEndpointFilter(EndpointsAcceso.ValidarCsrf);

        // Dentro de /api/movil/auth: la app ya tiene una sola ruta base para el acceso y estas dos quedan abiertas aunque falte aceptar.
        var movil = app.MapGroup("/api/movil/auth");
        movil.MapGet("/legal-estado", async (HttpContext http, ServicioAceptacion s, IOptions<OpcionesAcceso> o) =>
        {
            var c = (ContextoOperador)http.Items[AccesoMovil.ItemContextoPublico]!;
            http.Response.Headers.CacheControl = "no-store";
            return Results.Ok(await s.EstadoAsync(o.Value.OrganizacionId, c.AccesoId, http.RequestAborted));
        }).RequireAuthorization(AccesoMovil.Politica);
        movil.MapPost("/legal-aceptar", async (SolicitudAceptar? solicitud, HttpContext http, ServicioAceptacion s, IOptions<OpcionesAcceso> o) =>
        {
            var c = (ContextoOperador)http.Items[AccesoMovil.ItemContextoPublico]!;
            return await s.AceptarAsync(o.Value.OrganizacionId, c.AccesoId, solicitud, "movil", http.RequestAborted);
        }).RequireAuthorization(AccesoMovil.Politica);
    }
}
