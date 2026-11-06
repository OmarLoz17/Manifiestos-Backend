using Manifiestos.Api.Acceso;
using Manifiestos.Api.Rutas;

namespace Manifiestos.Api.Clientes;

/// <summary>
/// Pantalla "Clientes" del panel web. Cliente autorizado = su empresa tiene autorización de medio
/// ambiente como generador; cliente general = sin ella. Corregir crea una ficha nueva (revisión);
/// no se borran clientes y quitar una autorización todavía no está definido (se rechaza).
/// </summary>
public static class EndpointsClientes
{
    public const int LargoMaximoCampo = 200;
    public const string MensajeSinPermiso = "No tienes permiso para modificar clientes en esta sucursal.";
    public const string MensajeNoEncontrado = "No se encontró el cliente.";
    public const string MensajeCorreoInvalido = "El correo no tiene un formato válido.";

    public static IServiceCollection AgregarClientes(this IServiceCollection services) =>
        services.AddSingleton<IRepositorioClientes, RepositorioClientes>();

    public static void MapearClientes(this IEndpointRouteBuilder app)
    {
        // Sin AllowAnonymous: la política por defecto exige sesión revalidada.
        var g = app.MapGroup("/api/clientes");
        g.MapGet("", ListarAsync);
        g.MapPost("", CrearAsync).AddEndpointFilter(EndpointsAcceso.ValidarCsrf);
        g.MapPut("/{id:guid}", EditarAsync).AddEndpointFilter(EndpointsAcceso.ValidarCsrf);
        g.MapPut("/{id:guid}/sitios",GuardarSitiosAsync).AddEndpointFilter(EndpointsAcceso.ValidarCsrf);
    }

    private static async Task<IResult> ListarAsync(HttpContext http, IRepositorioClientes clientes)
    {
        if (!ServiciosAcceso.LeerIds(http.User, out var org, out var acceso)) return Results.Unauthorized();
        http.Response.Headers.CacheControl = "no-store";
        return Results.Ok(await clientes.ListarAsync(org, acceso, http.RequestAborted));
    }

    private static async Task<IResult> GuardarSitiosAsync(Guid id,SolicitudGuardarSitios s,HttpContext http,IRepositorioClientes clientes)
    {
        if(!ServiciosAcceso.LeerIds(http.User,out var org,out var acceso))return Results.Unauthorized();
        if(s.Sitios is null)return Error("Faltan las direcciones de servicio.");
        if(ValidarSitios(s.Sitios,out var sitios) is {} problema)return Error(problema);
        var resultado=await clientes.GuardarSitiosAsync(org,acceso,id,sitios!,http.RequestAborted);
        return resultado.Estado==EstadoEscritura.Correcta?Results.NoContent():Responder(resultado,false,null);
    }

    private static async Task<IResult> CrearAsync(SolicitudNuevoCliente s, HttpContext http, IRepositorioClientes clientes)
    {
        if (!ServiciosAcceso.LeerIds(http.User, out var org, out var acceso)) return Results.Unauthorized();
        if (s.SucursalId is not { } sucursal || sucursal == Guid.Empty) return Error("Falta la sucursal.");
        if (Validar([s.NombreFiscal, s.NombreComercial, s.Rfc, s.Calle, s.NumeroExterior, s.Colonia, s.CodigoPostal,
                     s.Municipio, s.Estado, s.Telefono, s.Correo, s.Autorizacion], out var datos) is { } error) return error;
        if (ValidarSitios(s.Sitios, out var sitios) is {} problema) return Error(problema);
        var r = await clientes.CrearAsync(org, acceso, sucursal, datos! with { Sitios = sitios }, http.RequestAborted);
        return Responder(r, creada: true, $"/api/clientes/{r.Id}");
    }

    private static async Task<IResult> EditarAsync(Guid id, SolicitudEditarCliente s, HttpContext http, IRepositorioClientes clientes)
    {
        if (!ServiciosAcceso.LeerIds(http.User, out var org, out var acceso)) return Results.Unauthorized();
        if (Validar([s.NombreFiscal, s.NombreComercial, s.Rfc, s.Calle, s.NumeroExterior, s.Colonia, s.CodigoPostal,
                     s.Municipio, s.Estado, s.Telefono, s.Correo, s.Autorizacion], out var datos) is { } error) return error;
        // 0 = el listado no tenía ficha para el cliente; el repositorio responde con un mensaje claro.
        if (s.Revision is not >= 0) return Error("Falta la revisión que se editó.");
        if (s.AutorizacionVista?.Trim().Length > LargoMaximoCampo) return Error($"Cada dato del cliente admite hasta {LargoMaximoCampo} caracteres.");
        if (ValidarSitios(s.Sitios, out var sitios) is {} problema) return Error(problema);
        return Responder(await clientes.EditarAsync(org, acceso, id, datos! with { Sitios = sitios }, s.Revision.Value, s.AutorizacionVista, http.RequestAborted),
            creada: false, null);
    }

    /// <summary>Campos en el orden de DatosCliente; recorta y convierte vacíos en null.</summary>
    private static string? ValidarSitios(SolicitudSitioCliente[]? solicitud, out SolicitudSitioCliente[]? sitios)
    {
        sitios=null;
        if (solicitud is null) return null;
        if (solicitud.Length>50 || solicitud.Any(s=>s is null)) return "Revisa las direcciones de servicio (máximo técnico: 50 por envío).";
        if (solicitud.Where(s=>s.SitioId.HasValue).GroupBy(s=>s.SitioId).Any(g=>g.Count()>1)) return "Una dirección aparece más de una vez.";
        var lista=new List<SolicitudSitioCliente>();
        foreach (var s in solicitud)
        {
            if (s.SitioId==Guid.Empty || s.SitioId.HasValue && (s.Revision is not >0 || s.RevisionFicha is not >=0)) return "Falta la revisión de la dirección que se editó.";
            if (s.Eliminar) { if (!s.SitioId.HasValue) return "La dirección todavía no está registrada."; lista.Add(s); continue; }
            if (s.Banos is <0 or >EndpointsRutas.MaximoBanos || s.Fosas is <0 or >EndpointsRutas.MaximoBanos) return "Las cantidades registradas deben ser enteros de 0 a 10000.";
            var campos=new[]{s.Nombre,s.Calle,s.NumeroExterior,s.Colonia,s.CodigoPostal,s.Municipio,s.Estado};
            if (campos.Any(c=>c?.Trim().Length>LargoMaximoCampo)) return "Cada dato de dirección admite hasta 200 caracteres.";
            if (campos.All(string.IsNullOrWhiteSpace)) return "Escribe el nombre o la dirección del sitio de servicio.";
            string? T(string? t)=>string.IsNullOrWhiteSpace(t)?null:t.Trim();
            lista.Add(s with { Nombre=T(s.Nombre),Calle=T(s.Calle),NumeroExterior=T(s.NumeroExterior),Colonia=T(s.Colonia),CodigoPostal=T(s.CodigoPostal),Municipio=T(s.Municipio),Estado=T(s.Estado) });
        }
        sitios=lista.ToArray();
        return null;
    }

    private static IResult? Validar(string?[] campos, out DatosCliente? datos)
    {
        datos = null;
        var c = campos.Select(x => string.IsNullOrWhiteSpace(x) ? null : x.Trim()).ToArray();
        if (c[0] is null && c[1] is null) return Error(RepositorioClientes.MensajeSinNombre);
        if (c.Any(x => x?.Length > LargoMaximoCampo)) return Error($"Cada dato del cliente admite hasta {LargoMaximoCampo} caracteres.");
        if (c[10] is { } correo && !CorreoValido(correo)) return Error(MensajeCorreoInvalido);
        datos = new DatosCliente(c[0], c[1], c[2], c[3], c[4], c[5], c[6], c[7], c[8], c[9], c[10], c[11]);
        return null;
    }

    /// <summary>Forma básica: algo@dominio.ext, sin espacios. No comprueba que el buzón exista.</summary>
    internal static bool CorreoValido(string correo)
    {
        var arroba = correo.IndexOf('@');
        if (arroba <= 0 || arroba != correo.LastIndexOf('@') || correo.Any(char.IsWhiteSpace)) return false;
        var dominio = correo[(arroba + 1)..];
        var punto = dominio.LastIndexOf('.');
        return punto > 0 && punto < dominio.Length - 1;
    }

    private static IResult Responder(ResultadoEscritura r, bool creada, string? ubicacion) => r.Estado switch
    {
        EstadoEscritura.Correcta when creada => Results.Created(ubicacion, new RevisionGuardada(r.Id, r.Revision)),
        EstadoEscritura.Correcta => Results.Ok(new RevisionGuardada(r.Id, r.Revision)),
        EstadoEscritura.NoEncontrada => Results.Json(new MensajeError(MensajeNoEncontrado), statusCode: StatusCodes.Status404NotFound),
        EstadoEscritura.SinPermiso => Results.Json(new MensajeError(MensajeSinPermiso), statusCode: StatusCodes.Status403Forbidden),
        EstadoEscritura.Conflicto => Results.Json(new MensajeError(r.Mensaje!), statusCode: StatusCodes.Status409Conflict),
        _ => Error(r.Mensaje!),
    };

    private static IResult Error(string mensaje) => Results.BadRequest(new MensajeError(mensaje));
}
