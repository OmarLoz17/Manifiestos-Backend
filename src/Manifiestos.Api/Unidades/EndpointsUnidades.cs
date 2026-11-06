using Manifiestos.Api.Acceso;
using Manifiestos.Api.Rutas;

namespace Manifiestos.Api.Unidades;

/// <summary>
/// Pantalla "Unidades" del panel. Alta y corrección de la ficha (número económico, placas, capacidad del
/// catálogo 10/15/18 m³, clasificación y descripción). El operador y la ruta se muestran pero se asignan en Rutas.
/// No se borran unidades.
/// </summary>
public static class EndpointsUnidades
{
    public const int LargoMaximoCampo = 200;
    public const string MensajeSinPermiso = "No tienes permiso para modificar unidades en esta sucursal.";
    public const string MensajeNoEncontrada = "No se encontró la unidad.";
    public const string MensajeSinNumero = "Escribe el número económico de la unidad.";
    public const string MensajeSinCapacidad = "Elige la capacidad de la unidad.";
    public const string MensajeClasificacion = "La clasificación debe ser fosas o sanitarios.";

    public static IServiceCollection AgregarUnidades(this IServiceCollection services) =>
        services.AddSingleton<IRepositorioUnidades, RepositorioUnidades>();

    public static void MapearUnidades(this IEndpointRouteBuilder app)
    {
        // Sin AllowAnonymous: la política por defecto exige sesión revalidada.
        var g = app.MapGroup("/api/unidades");
        g.MapGet("", ListarAsync);
        g.MapPost("", CrearAsync).AddEndpointFilter(EndpointsAcceso.ValidarCsrf);
        g.MapPut("/{id:guid}", EditarAsync).AddEndpointFilter(EndpointsAcceso.ValidarCsrf);
    }

    private static async Task<IResult> ListarAsync(HttpContext http, IRepositorioUnidades unidades)
    {
        if (!ServiciosAcceso.LeerIds(http.User, out var org, out var acceso)) return Results.Unauthorized();
        http.Response.Headers.CacheControl = "no-store";
        return Results.Ok(await unidades.ListarAsync(org, acceso, http.RequestAborted));
    }

    private static async Task<IResult> CrearAsync(SolicitudNuevaUnidad s, HttpContext http, IRepositorioUnidades unidades)
    {
        if (!ServiciosAcceso.LeerIds(http.User, out var org, out var acceso)) return Results.Unauthorized();
        if (s.SucursalId is not { } sucursal || sucursal == Guid.Empty) return Error("Falta la sucursal.");
        if (Validar(s.NumeroEconomico, s.Placas, s.Marca, s.Tipo, s.CapacidadId, s.Clasificacion, s.Descripcion, out var datos) is { } error) return error;
        var r = await unidades.CrearAsync(org, acceso, sucursal, datos!, http.RequestAborted);
        return Responder(r, creada: true, $"/api/unidades/{r.Id}");
    }

    private static async Task<IResult> EditarAsync(Guid id, SolicitudEditarUnidad s, HttpContext http, IRepositorioUnidades unidades)
    {
        if (!ServiciosAcceso.LeerIds(http.User, out var org, out var acceso)) return Results.Unauthorized();
        if (Validar(s.NumeroEconomico, s.Placas, s.Marca, s.Tipo, s.CapacidadId, s.Clasificacion, s.Descripcion, out var datos) is { } error) return error;
        // 0 = la unidad no tenía ficha en el listado; la primera ficha se crea con revisión 1.
        if (s.Revision is not >= 0) return Error("Falta la revisión que se editó.");
        return Responder(await unidades.EditarAsync(org, acceso, id, datos!, s.Revision.Value, http.RequestAborted), creada: false, null);
    }

    private static IResult? Validar(string? numero, string? placas, string? marca, string? tipo, short? capacidad,
        string? clasificacion, string? descripcion, out DatosUnidad? datos)
    {
        datos = null;
        static string? T(string? t) => string.IsNullOrWhiteSpace(t) ? null : t.Trim();
        var n = T(numero);
        if (n is null) return Error(MensajeSinNumero);
        if (capacidad is not { } cap) return Error(MensajeSinCapacidad);
        string?[] campos = [n, T(placas), T(marca), T(tipo), T(clasificacion), T(descripcion)];
        if (campos.Any(c => c?.Length > LargoMaximoCampo)) return Error($"Cada dato de la unidad admite hasta {LargoMaximoCampo} caracteres.");
        var clase = campos[4]?.ToLowerInvariant();
        if (clase is not null and not ("fosas" or "sanitarios")) return Error(MensajeClasificacion);
        datos = new DatosUnidad(n, campos[1], campos[2], campos[3], cap, clase, campos[5]);
        return null;
    }

    private static IResult Responder(ResultadoEscritura r, bool creada, string? ubicacion) => r.Estado switch
    {
        EstadoEscritura.Correcta when creada => Results.Created(ubicacion, new RevisionGuardada(r.Id, r.Revision)),
        EstadoEscritura.Correcta => Results.Ok(new RevisionGuardada(r.Id, r.Revision)),
        EstadoEscritura.NoEncontrada => Results.Json(new MensajeError(MensajeNoEncontrada), statusCode: StatusCodes.Status404NotFound),
        EstadoEscritura.SinPermiso => Results.Json(new MensajeError(MensajeSinPermiso), statusCode: StatusCodes.Status403Forbidden),
        EstadoEscritura.Conflicto => Results.Json(new MensajeError(r.Mensaje!), statusCode: StatusCodes.Status409Conflict),
        _ => Error(r.Mensaje!),
    };

    private static IResult Error(string mensaje) => Results.BadRequest(new MensajeError(mensaje));
}
