using System.Text.Json.Serialization;
using Manifiestos.Api.Acceso;

namespace Manifiestos.Api.Rutas;

/// <summary>Error de negocio con código estable: {"error":"codigo","mensaje":"es", ...extras}.</summary>
public sealed record ErrorNegocio(
    string Error, string Mensaje,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] RutaReferencia? RutaActual = null);

/// <summary>
/// Módulo "Rutas y servicios" del panel web. Una ruta es un plan fijo por días de la semana (terciado), asociado a una
/// unidad (y la unidad a un operador) y es solo de sanitarios o solo de fosas según la clasificación de la unidad.
/// Parada = sitio; en sanitarios los clientes (generales) se agregan uno por uno con sus baños; en fosas llevan la
/// cantidad de órdenes. Cada cambio es una revisión nueva; las paradas no se borran, se dan de baja.
/// </summary>
public static class EndpointsRutas
{
    public const int LargoMaximoCampoSitio = 200;
    public const int MaximoClientesPorParada = 50;
    public const int MaximoBanos = 10000;
    public const string MensajeSinPermiso = "No tienes permiso para modificar rutas en esta sucursal.";
    public const string MensajeServicioInvalido = "El servicio debe ser sanitarios o fosas.";

    public static IServiceCollection AgregarRutas(this IServiceCollection services) =>
        services.AddSingleton<IRepositorioRutas, RepositorioRutas>();

    public static void MapearRutas(this IEndpointRouteBuilder app)
    {
        // Sin AllowAnonymous: la política por defecto exige sesión revalidada.
        var g = app.MapGroup("/api/rutas");
        // PG-37: un cambio de Operaciones a la ruta que sale HOY avisa al operador (push). No avisan el orden de las paradas ni la ubicación.
        g.AddEndpointFilter(async (ctx, next) =>
        {
            var resultado = await next(ctx);
            var http = ctx.HttpContext;
            if (HttpMethods.IsGet(http.Request.Method) || resultado is not IStatusCodeHttpResult { StatusCode: >= 200 and < 300 }) return resultado;
            var ruta = http.Request.Path.Value ?? "";
            if (ruta.EndsWith("/orden", StringComparison.Ordinal) || !Guid.TryParse(http.Request.RouteValues["id"]?.ToString(), out var id)) return resultado;
            var texto = ruta.Contains("/clientes", StringComparison.Ordinal) ? "Cambió una orden en tu ruta de hoy."
                : HttpMethods.IsDelete(http.Request.Method) ? "Se quitó una parada de tu ruta de hoy."
                : HttpMethods.IsPost(http.Request.Method) && ruta.EndsWith("/paradas", StringComparison.Ordinal) ? "Se agregó una parada a tu ruta de hoy."
                : "Cambió tu ruta de hoy.";
            var avisos = http.RequestServices.GetRequiredService<Avisos.AvisosTrabajo>();
            _ = Task.Run(() => avisos.AvisarSiSaleHoyAsync(id, texto));
            return resultado;
        });
        g.MapGet("", ListarAsync);
        g.MapGet("/opciones", OpcionesAsync);
        g.MapGet("/{id:guid}/paradas", ParadasAsync);
        g.MapPost("", CrearAsync).AddEndpointFilter(EndpointsAcceso.ValidarCsrf);
        g.MapPut("/{id:guid}", EditarAsync).AddEndpointFilter(EndpointsAcceso.ValidarCsrf);
        g.MapPost("/{id:guid}/paradas", AgregarParadaAsync).AddEndpointFilter(EndpointsAcceso.ValidarCsrf);
        g.MapPut("/{id:guid}/paradas/{paradaId:guid}", EditarParadaAsync).AddEndpointFilter(EndpointsAcceso.ValidarCsrf);
        g.MapDelete("/{id:guid}/paradas/{paradaId:guid}", EliminarParadaAsync).AddEndpointFilter(EndpointsAcceso.ValidarCsrf);
        g.MapPost("/{id:guid}/paradas/{paradaId:guid}/clientes", AgregarClienteAsync).AddEndpointFilter(EndpointsAcceso.ValidarCsrf);
        g.MapPut("/{id:guid}/paradas/{paradaId:guid}/clientes/{clienteId:guid}", EditarClienteAsync).AddEndpointFilter(EndpointsAcceso.ValidarCsrf);
        g.MapDelete("/{id:guid}/paradas/{paradaId:guid}/clientes/{clienteId:guid}", QuitarClienteAsync).AddEndpointFilter(EndpointsAcceso.ValidarCsrf);
        g.MapPut("/{id:guid}/orden", OrdenarAsync).AddEndpointFilter(EndpointsAcceso.ValidarCsrf);
    }

    // ---------------- Lectura ----------------

    private static async Task<IResult> ListarAsync(Guid? sucursalId, string? servicio, HttpContext http, IRepositorioRutas rutas)
    {
        if (!ServiciosAcceso.LeerIds(http.User, out var org, out var acceso)) return Results.Unauthorized();
        if (!ServicioValido(servicio, out var filtro)) return Error(MensajeServicioInvalido);
        http.Response.Headers.CacheControl = "no-store";
        return Results.Ok(await rutas.ListarAsync(org, acceso, sucursalId, filtro, http.RequestAborted));
    }

    private static async Task<IResult> ParadasAsync(Guid id, HttpContext http, IRepositorioRutas rutas)
    {
        if (!ServiciosAcceso.LeerIds(http.User, out var org, out var acceso)) return Results.Unauthorized();
        var paradas = await rutas.ParadasAsync(org, acceso, id, http.RequestAborted);
        if (paradas is null) return RutaNoEncontrada();
        http.Response.Headers.CacheControl = "no-store";
        return Results.Ok(paradas);
    }

    private static async Task<IResult> OpcionesAsync(Guid? sucursalId, string? servicio, HttpContext http, IRepositorioRutas rutas)
    {
        if (!ServiciosAcceso.LeerIds(http.User, out var org, out var acceso)) return Results.Unauthorized();
        if (sucursalId is not { } s || s == Guid.Empty) return Error("Falta la sucursal.");
        if (!ServicioValido(servicio, out var filtro)) return Error(MensajeServicioInvalido);
        http.Response.Headers.CacheControl = "no-store";
        return Results.Ok(await rutas.OpcionesAsync(org, acceso, s, filtro, http.RequestAborted));
    }

    private static bool ServicioValido(string? servicio, out string? filtro)
    {
        filtro = string.IsNullOrWhiteSpace(servicio) ? null : servicio.Trim().ToLowerInvariant();
        return filtro is null or "sanitarios" or "fosas";
    }

    // ---------------- Rutas ----------------

    private static async Task<IResult> CrearAsync(SolicitudNuevaRuta solicitud, HttpContext http, IRepositorioRutas rutas)
    {
        if (!ServiciosAcceso.LeerIds(http.User, out var org, out var acceso)) return Results.Unauthorized();
        if (solicitud.SucursalId is not { } s || s == Guid.Empty) return Error("Falta la sucursal.");
        if (ValidarRuta(solicitud.Dias, solicitud.UnidadId, solicitud.OperadorId, out var datos) is { } error) return error;
        var r = await rutas.CrearAsync(org, acceso, s, datos!, http.RequestAborted);
        return Responder(r, creada: true, $"/api/rutas/{r.Id}");
    }

    private static async Task<IResult> EditarAsync(Guid id, SolicitudEditarRuta solicitud, HttpContext http, IRepositorioRutas rutas)
    {
        if (!ServiciosAcceso.LeerIds(http.User, out var org, out var acceso)) return Results.Unauthorized();
        if (ValidarRuta(solicitud.Dias, solicitud.UnidadId, solicitud.OperadorId, out var datos) is { } error) return error;
        if (solicitud.Revision is not > 0) return Error("Falta la revisión que se editó.");
        return Responder(await rutas.EditarAsync(org, acceso, id, datos!, solicitud.Revision.Value, http.RequestAborted), creada: false, null);
    }

    /// <summary>El nombre ya no se acepta: lo genera el servidor (número económico + terciado).</summary>
    private static IResult? ValidarRuta(int[]? dias, Guid? unidadId, Guid? operadorId, out DatosRuta? datos)
    {
        datos = null;
        if (!DiasSemana.TryMascara(dias, out var mascara)) return Error("Elige al menos un día válido de la semana.");
        if (unidadId is not { } unidad || unidad == Guid.Empty) return Error("Elige una unidad.");
        if (operadorId == Guid.Empty) return Error("El operador elegido no está disponible.");
        datos = new DatosRuta(mascara, unidad, operadorId);
        return null;
    }

    // ---------------- Paradas ----------------

    private static async Task<IResult> AgregarParadaAsync(Guid id, SolicitudParada solicitud, HttpContext http, IRepositorioRutas rutas)
    {
        if (!ServiciosAcceso.LeerIds(http.User, out var org, out var acceso)) return Results.Unauthorized();
        // Contrato nuevo: solo el sitio (parada sin clientes). El contrato con clientes[]/nuevoSitio se conserva.
        if (solicitud.Clientes is null && solicitud.NuevoSitio is null)
        {
            if (solicitud.SitioId is not { } sitio || sitio == Guid.Empty) return Error("Elige un punto de servicio.");
            var nueva = await rutas.AgregarParadaSitioAsync(org, acceso, id, sitio, solicitud.Reasignar == true, http.RequestAborted);
            return Responder(nueva, creada: true, $"/api/rutas/{id}/paradas/{nueva.Id}");
        }
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

    private static async Task<IResult> EliminarParadaAsync(Guid id, Guid paradaId, int? revision, HttpContext http, IRepositorioRutas rutas)
    {
        if (!ServiciosAcceso.LeerIds(http.User, out var org, out var acceso)) return Results.Unauthorized();
        if (revision is <= 0) return Error("La revisión que se vio no es válida.");
        var r = await rutas.EliminarParadaAsync(org, acceso, id, paradaId, revision, http.RequestAborted);
        return r.Estado == EstadoEscritura.Correcta ? Results.NoContent() : Responder(r, creada: false, null);
    }

    private static async Task<IResult> AgregarClienteAsync(Guid id, Guid paradaId, SolicitudAgregarCliente s, HttpContext http, IRepositorioRutas rutas)
    {
        if (!ServiciosAcceso.LeerIds(http.User, out var org, out var acceso)) return Results.Unauthorized();
        if (s.ClienteId is not { } cliente || cliente == Guid.Empty) return Error("Elige el cliente.");
        if (s.Revision is not > 0) return Error("Falta la revisión de la parada que se vio.");
        if (CantidadFueraDeRango(s.Banos) || CantidadFueraDeRango(s.Ordenes)) return Error($"Las cantidades deben ser enteros de 0 a {MaximoBanos}.");
        var r = await rutas.AgregarClienteAsync(org, acceso, id, paradaId, cliente, s.Banos, s.Ordenes, s.Reasignar == true, s.Revision.Value, http.RequestAborted);
        return Responder(r, creada: true, $"/api/rutas/{id}/paradas/{paradaId}");
    }

    private static async Task<IResult> EditarClienteAsync(Guid id, Guid paradaId, Guid clienteId, SolicitudEditarClienteParada s, HttpContext http, IRepositorioRutas rutas)
    {
        if (!ServiciosAcceso.LeerIds(http.User, out var org, out var acceso)) return Results.Unauthorized();
        if (s.Revision is not > 0) return Error("Falta la revisión de la parada que se vio.");
        if (CantidadFueraDeRango(s.Banos) || CantidadFueraDeRango(s.Ordenes)) return Error($"Las cantidades deben ser enteros de 0 a {MaximoBanos}.");
        return Responder(await rutas.EditarClienteAsync(org, acceso, id, paradaId, clienteId, s.Banos, s.Ordenes, s.Revision.Value, http.RequestAborted), creada: false, null);
    }

    private static async Task<IResult> QuitarClienteAsync(Guid id, Guid paradaId, Guid clienteId, int? revision, HttpContext http, IRepositorioRutas rutas)
    {
        if (!ServiciosAcceso.LeerIds(http.User, out var org, out var acceso)) return Results.Unauthorized();
        if (revision is not > 0) return Error("Falta la revisión de la parada que se vio.");
        return Responder(await rutas.QuitarClienteAsync(org, acceso, id, paradaId, clienteId, revision.Value, http.RequestAborted), creada: false, null);
    }

    private static bool CantidadFueraDeRango(int? cantidad) => cantidad is < 0 or > MaximoBanos;

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
        EstadoEscritura.Conflicto when r.Codigo is not null =>
            Results.Json(new ErrorNegocio(r.Codigo, r.Mensaje!, r.RutaActual), statusCode: StatusCodes.Status409Conflict),
        EstadoEscritura.Conflicto => Results.Json(new MensajeError(r.Mensaje!), statusCode: StatusCodes.Status409Conflict),
        EstadoEscritura.Regla => Results.Json(new ErrorNegocio(r.Codigo!, r.Mensaje!), statusCode: StatusCodes.Status422UnprocessableEntity),
        _ => Error(r.Mensaje!),
    };

    private static IResult RutaNoEncontrada() =>
        Results.Json(new MensajeError("No se encontró la ruta o la parada."), statusCode: StatusCodes.Status404NotFound);

    private static IResult Error(string mensaje) => Results.BadRequest(new MensajeError(mensaje));
}
