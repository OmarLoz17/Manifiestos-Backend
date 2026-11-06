using Manifiestos.Api.Acceso;

namespace Manifiestos.Api.Rutas;

/// <summary>
/// Módulo "Rutas y servicios" del panel web. Una ruta es un plan fijo por días de la semana,
/// asociado a una unidad (y la unidad a un operador); sus paradas son puntos de servicio con
/// los baños planeados de cada cliente. Cada cambio es una revisión nueva; no se borran rutas ni paradas.
/// </summary>
public static class EndpointsRutas
{
    public const int LargoMaximoNombre = 120;
    public const int LargoMaximoCampoSitio = 200;
    public const int MaximoClientesPorParada = 50;
    public const int MaximoBanos = 10000;
    public const string MensajeSinPermiso = "No tienes permiso para modificar rutas en esta sucursal.";

    public static IServiceCollection AgregarRutas(this IServiceCollection services) =>
        services.AddSingleton<IRepositorioRutas, RepositorioRutas>();

    public static void MapearRutas(this IEndpointRouteBuilder app)
    {
        // Sin AllowAnonymous: la política por defecto exige sesión revalidada.
        var g = app.MapGroup("/api/rutas");
        g.MapGet("", ListarAsync);
        g.MapGet("/opciones", OpcionesAsync);
        g.MapGet("/{id:guid}/paradas", ParadasAsync);
        g.MapPost("", CrearAsync).AddEndpointFilter(EndpointsAcceso.ValidarCsrf);
        g.MapPut("/{id:guid}", EditarAsync).AddEndpointFilter(EndpointsAcceso.ValidarCsrf);
        g.MapPost("/{id:guid}/paradas", AgregarParadaAsync).AddEndpointFilter(EndpointsAcceso.ValidarCsrf);
        g.MapPut("/{id:guid}/paradas/{paradaId:guid}", EditarParadaAsync).AddEndpointFilter(EndpointsAcceso.ValidarCsrf);
        g.MapPut("/{id:guid}/orden", OrdenarAsync).AddEndpointFilter(EndpointsAcceso.ValidarCsrf);
    }

    // ---------------- Lectura ----------------

    private static async Task<IResult> ListarAsync(HttpContext http, IRepositorioRutas rutas)
    {
        if (!ServiciosAcceso.LeerIds(http.User, out var org, out var acceso)) return Results.Unauthorized();
        http.Response.Headers.CacheControl = "no-store";
        return Results.Ok(await rutas.ListarAsync(org, acceso, http.RequestAborted));
    }

    private static async Task<IResult> ParadasAsync(Guid id, HttpContext http, IRepositorioRutas rutas)
    {
        if (!ServiciosAcceso.LeerIds(http.User, out var org, out var acceso)) return Results.Unauthorized();
        var paradas = await rutas.ParadasAsync(org, acceso, id, http.RequestAborted);
        if (paradas is null) return RutaNoEncontrada();
        http.Response.Headers.CacheControl = "no-store";
        return Results.Ok(paradas);
    }

    private static async Task<IResult> OpcionesAsync(Guid? sucursalId, HttpContext http, IRepositorioRutas rutas)
    {
        if (!ServiciosAcceso.LeerIds(http.User, out var org, out var acceso)) return Results.Unauthorized();
        if (sucursalId is not { } s || s == Guid.Empty) return Error("Falta la sucursal.");
        http.Response.Headers.CacheControl = "no-store";
        return Results.Ok(await rutas.OpcionesAsync(org, acceso, s, http.RequestAborted));
    }

    // ---------------- Rutas ----------------

    private static async Task<IResult> CrearAsync(SolicitudNuevaRuta solicitud, HttpContext http, IRepositorioRutas rutas)
    {
        if (!ServiciosAcceso.LeerIds(http.User, out var org, out var acceso)) return Results.Unauthorized();
        if (solicitud.SucursalId is not { } s || s == Guid.Empty) return Error("Falta la sucursal.");
        if (ValidarRuta(solicitud.Nombre, solicitud.Dias, solicitud.UnidadId, solicitud.OperadorId, out var datos) is { } error) return error;
        var r = await rutas.CrearAsync(org, acceso, s, datos!, http.RequestAborted);
        return Responder(r, creada: true, $"/api/rutas/{r.Id}");
    }

    private static async Task<IResult> EditarAsync(Guid id, SolicitudEditarRuta solicitud, HttpContext http, IRepositorioRutas rutas)
    {
        if (!ServiciosAcceso.LeerIds(http.User, out var org, out var acceso)) return Results.Unauthorized();
        if (ValidarRuta(solicitud.Nombre, solicitud.Dias, solicitud.UnidadId, solicitud.OperadorId, out var datos) is { } error) return error;
        if (solicitud.Revision is not > 0) return Error("Falta la revisión que se editó.");
        return Responder(await rutas.EditarAsync(org, acceso, id, datos!, solicitud.Revision.Value, http.RequestAborted), creada: false, null);
    }

    private static IResult? ValidarRuta(string? nombreRecibido, int[]? dias, Guid? unidadId, Guid? operadorId, out DatosRuta? datos)
    {
        datos = null;
        var nombre = nombreRecibido?.Trim() ?? "";
        if (nombre.Length == 0) return Error("Escribe el nombre de la ruta.");
        if (nombre.Length > LargoMaximoNombre) return Error($"El nombre admite hasta {LargoMaximoNombre} caracteres.");
        if (!DiasSemana.TryMascara(dias, out var mascara)) return Error("Elige al menos un día válido de la semana.");
        if (unidadId is not { } unidad || unidad == Guid.Empty) return Error("Elige una unidad.");
        if (operadorId == Guid.Empty) return Error("El operador elegido no está disponible.");
        datos = new DatosRuta(nombre, mascara, unidad, operadorId);
        return null;
    }

    // ---------------- Paradas ----------------

    private static async Task<IResult> AgregarParadaAsync(Guid id, SolicitudParada solicitud, HttpContext http, IRepositorioRutas rutas)
    {
        if (!ServiciosAcceso.LeerIds(http.User, out var org, out var acceso)) return Results.Unauthorized();
        if (ValidarParada(solicitud, out var datos) is { } error) return error;
        var r = await rutas.AgregarParadaAsync(org, acceso, id, datos!, http.RequestAborted);
        return Responder(r, creada: true, $"/api/rutas/{id}/paradas/{r.Id}");
    }

    private static async Task<IResult> EditarParadaAsync(Guid id, Guid paradaId, SolicitudParada solicitud, HttpContext http, IRepositorioRutas rutas)
    {
        if (!ServiciosAcceso.LeerIds(http.User, out var org, out var acceso)) return Results.Unauthorized();
        if (ValidarParada(solicitud, out var datos) is { } error) return error;
        if (solicitud.Revision is not > 0) return Error("Falta la revisión que se editó.");
        return Responder(await rutas.EditarParadaAsync(org, acceso, id, paradaId, datos!, solicitud.Revision.Value, http.RequestAborted), creada: false, null);
    }

    private static async Task<IResult> OrdenarAsync(Guid id, SolicitudOrden solicitud, HttpContext http, IRepositorioRutas rutas)
    {
        if (!ServiciosAcceso.LeerIds(http.User, out var org, out var acceso)) return Results.Unauthorized();
        if (solicitud.Paradas is null) return Error("Falta el orden de las paradas.");
        var r = await rutas.OrdenarAsync(org, acceso, id, solicitud.Paradas, http.RequestAborted);
        return r.Estado == EstadoEscritura.Correcta ? Results.NoContent() : Responder(r, creada: false, null);
    }

    private static IResult? ValidarParada(SolicitudParada s, out DatosParada? datos)
    {
        datos = null;
        var conSitio = s.SitioId is { } id && id != Guid.Empty;
        if (conSitio == (s.NuevoSitio is not null)) return Error("Elige un punto de servicio existente o captura uno nuevo.");

        DatosNuevoSitio? nuevo = null;
        if (s.NuevoSitio is { } n)
        {
            var nombre = n.Nombre?.Trim() ?? "";
            if (nombre.Length == 0) return Error("Escribe el nombre del punto de servicio.");
            string?[] campos = [nombre, n.Calle, n.Colonia, n.Municipio, n.Estado, n.CodigoPostal, n.Referencia];
            if (campos.Any(c => c?.Trim().Length > LargoMaximoCampoSitio))
                return Error($"Cada dato del punto de servicio admite hasta {LargoMaximoCampoSitio} caracteres.");
            nuevo = new DatosNuevoSitio(nombre, Limpio(n.Calle), Limpio(n.Colonia), Limpio(n.Municipio), Limpio(n.Estado), Limpio(n.CodigoPostal), Limpio(n.Referencia));
        }

        var clientes = s.Clientes ?? [];
        if (clientes.Length == 0) return Error("Agrega al menos un cliente a la parada.");
        if (clientes.Length > MaximoClientesPorParada) return Error($"Una parada admite hasta {MaximoClientesPorParada} clientes.");
        var lista = new List<DatosClienteParada>();
        foreach (var c in clientes)
        {
            if (c?.ClienteId is not { } cliente || cliente == Guid.Empty) return Error("Elige el cliente.");
            if (c.Banos is < 0 or > MaximoBanos || c.Fosas is < 0 or > MaximoBanos)
                return Error($"Las cantidades planeadas deben ser enteros de 0 a {MaximoBanos}.");
            if ((c.Banos ?? 0) == 0 && (c.Fosas ?? 0) == 0)
                return Error("Indica al menos un baño o una fosa para cada cliente.");
            lista.Add(new DatosClienteParada(cliente, c.Banos is > 0 ? c.Banos : null, c.Fosas));
        }
        if (lista.Select(c => c.ClienteId).Distinct().Count() != lista.Count) return Error("Un cliente aparece más de una vez en la parada.");

        datos = new DatosParada(conSitio ? s.SitioId : null, nuevo, lista);
        return null;
    }

    private static string? Limpio(string? valor) => string.IsNullOrWhiteSpace(valor) ? null : valor.Trim();

    // ---------------- Respuestas ----------------

    private static IResult Responder(ResultadoEscritura r, bool creada, string? ubicacion) => r.Estado switch
    {
        EstadoEscritura.Correcta when creada => Results.Created(ubicacion, new RevisionGuardada(r.Id, r.Revision)),
        EstadoEscritura.Correcta => Results.Ok(new RevisionGuardada(r.Id, r.Revision)),
        EstadoEscritura.NoEncontrada => RutaNoEncontrada(),
        EstadoEscritura.SinPermiso => Results.Json(new MensajeError(MensajeSinPermiso), statusCode: StatusCodes.Status403Forbidden),
        EstadoEscritura.Conflicto => Results.Json(new MensajeError(r.Mensaje!), statusCode: StatusCodes.Status409Conflict),
        _ => Error(r.Mensaje!),
    };

    private static IResult RutaNoEncontrada() =>
        Results.Json(new MensajeError("No se encontró la ruta o la parada."), statusCode: StatusCodes.Status404NotFound);

    private static IResult Error(string mensaje) => Results.BadRequest(new MensajeError(mensaje));
}
