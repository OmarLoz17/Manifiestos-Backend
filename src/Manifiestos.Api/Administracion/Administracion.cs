using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;
using Manifiestos.Api.Acceso;
using Manifiestos.Api.Datos;
using Manifiestos.Api.Operadores;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Manifiestos.Api.Administracion;

// ---- Respuestas y solicitudes (JSON camelCase) ----

public sealed record PermisoCuenta(Guid SucursalId, string Pantalla, string Nivel);

/// <summary>Cuenta con acceso al sistema. Tipo: administrador, operaciones (panel) o telefono_operador (app).</summary>
public sealed record CuentaAdmin(
    Guid Id, string Correo, string Nombre, string Tipo, bool Activo, bool PrimerAcceso, DateTime? UltimoAcceso,
    string? Equipo, string? Unidad, IReadOnlyList<PermisoCuenta> Permisos, string? TerminosVersion = null, DateTime? TerminosEn = null, bool TerminosVigentes = false,
    /** Cuenta del panel que además entra como operador en el teléfono. */ bool TambienOperador = false);

public sealed record Deshabilitado(string Tipo, Guid Id, string Nombre, string? Detalle, DateTime? Desde, string? Por);

public sealed record PantallasSucursal(Guid SucursalId, Dictionary<string, string> Pantallas);

public sealed record SolicitudCuenta(string? Correo, string? Nombre, string? Tipo, List<PantallasSucursal>? Permisos, bool? TambienOperador = null);
public sealed record SolicitudEstado(bool Activo);
public sealed record SolicitudConfirmacion(string? Credential);

/// <summary>«Pendientes de ayer» (decisión 0045): el general y el valor efectivo de cada cuenta de teléfono activa.</summary>
public sealed record AjustePendientesDeCuenta(Guid CuentaId, bool Activo);
public sealed record AjustePendientes(bool General, IReadOnlyList<AjustePendientesDeCuenta> Cuentas);
public sealed record SolicitudPendientes(Guid? CuentaId, bool? Activo);

public enum ResultadoAdmin { Ok, SinPermiso, NoEncontrada, Invalida, Conflicto }

public sealed record RespuestaAdmin<T>(ResultadoAdmin Resultado, string? Motivo, T? Valor);

public interface IRepositorioAdministracion
{
    Task<IReadOnlyList<CuentaAdmin>> CuentasAsync(Guid org, Guid acceso, CancellationToken ct);
    Task<IReadOnlyList<Deshabilitado>> DeshabilitadosAsync(Guid org, Guid acceso, CancellationToken ct);
    Task<RespuestaAdmin<Guid>> GuardarCuentaAsync(Guid org, Guid acceso, Guid? cuenta, SolicitudCuenta s, CancellationToken ct);
    Task<RespuestaAdmin<bool>> EstadoCuentaAsync(Guid org, Guid acceso, Guid cuenta, bool activo, CancellationToken ct);
    Task<RespuestaAdmin<bool>> EstadoEquipoAsync(Guid org, Guid acceso, Guid dispositivo, bool habilitado, CancellationToken ct);
    Task<AjustePendientes> PendientesAyerAsync(Guid org, Guid acceso, CancellationToken ct);
    Task<RespuestaAdmin<bool>> GuardarPendientesAyerAsync(Guid org, Guid acceso, Guid? cuenta, bool activo, CancellationToken ct);
    Task<bool> SesionConfirmadaAsync(Guid org, Guid acceso, Guid sesion, CancellationToken ct);
    Task<bool> ConfirmarIdentidadAsync(Guid org, Guid acceso, Guid sesion, IdentidadGoogle identidad, CancellationToken ct);
    Task<IReadOnlyDictionary<string, string>> MisPermisosAsync(Guid org, Guid acceso, CancellationToken ct);
}

/// <summary>Administración (migración 0072, decisión 0042): todo pasa por funciones que exigen el rol administrador; nada se elimina.</summary>
public sealed class RepositorioAdministracion(NpgsqlDataSource datos) : IRepositorioAdministracion
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<IReadOnlyList<CuentaAdmin>> CuentasAsync(Guid org, Guid acceso, CancellationToken ct)
    {
        await using var tx = await TransaccionContexto.AbrirAsync(datos, org, acceso, ct);
        var lista = new List<CuentaAdmin>();
        await using (var cmd = tx.Comando("SELECT acceso_id, correo, nombre, rol_codigo, activo, primer_acceso, ultimo_acceso, equipo, unidad, permisos::text, terminos_version, terminos_en FROM seguridad.admin_cuentas($1)", org))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct))
                lista.Add(new CuentaAdmin(r.GetGuid(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetBoolean(4), r.GetBoolean(5),
                    r.IsDBNull(6) ? null : r.GetDateTime(6), r.IsDBNull(7) ? null : r.GetString(7), r.IsDBNull(8) ? null : r.GetString(8),
                    JsonSerializer.Deserialize<List<PermisoCuenta>>(r.GetString(9), Json) ?? [],
                    r.IsDBNull(10) ? null : r.GetString(10), r.IsDBNull(11) ? null : r.GetDateTime(11),
                    !r.IsDBNull(10) && r.GetString(10) == Legal.DocumentosLegalesVigentes.Todos.First(d => d.Id == "terminos").Version));
        var operan = new HashSet<Guid>();
        await using (var cmd = tx.Comando("SELECT acceso_id FROM seguridad.admin_cuentas_que_operan($1)", org))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct)) operan.Add(r.GetGuid(0));
        lista = lista.Select(c => c with { TambienOperador = operan.Contains(c.Id) }).ToList();
        await tx.ConfirmarAsync(ct);
        return lista;
    }

    public async Task<IReadOnlyList<Deshabilitado>> DeshabilitadosAsync(Guid org, Guid acceso, CancellationToken ct)
    {
        await using var tx = await TransaccionContexto.AbrirAsync(datos, org, acceso, ct);
        var lista = new List<Deshabilitado>();
        await using (var cmd = tx.Comando("SELECT tipo, objeto_id, nombre, detalle, desde, por FROM seguridad.admin_deshabilitados($1) ORDER BY desde DESC NULLS LAST", org))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct))
                lista.Add(new Deshabilitado(r.GetString(0), r.GetGuid(1), r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3),
                    r.IsDBNull(4) ? null : r.GetDateTime(4), r.IsDBNull(5) ? null : r.GetString(5)));
        await tx.ConfirmarAsync(ct);
        return lista;
    }

    public Task<RespuestaAdmin<Guid>> GuardarCuentaAsync(Guid org, Guid acceso, Guid? cuenta, SolicitudCuenta s, CancellationToken ct) =>
        EjecutarAsync<Guid>(org, acceso, async tx =>
        {
            var rol = s.Tipo ?? "";
            var permisos = JsonSerializer.Serialize((s.Permisos ?? []).Select(p => new { sucursalId = p.SucursalId, pantallas = p.Pantallas }), Json);
            await using var cmd = tx.Comando("SELECT seguridad.admin_guardar_cuenta($1, $2, $3, $4, $5, $6::jsonb)", org, (object?)cuenta ?? DBNull.Value, s.Correo ?? "", s.Nombre ?? "", rol, permisos);
            var id = (Guid)(await cmd.ExecuteScalarAsync(ct))!;
            if (s.TambienOperador is { } opera)
            {
                await using var marca = tx.Comando("SELECT seguridad.admin_tambien_operador($1, $2, $3)", org, id, opera);
                await marca.ExecuteNonQueryAsync(ct);
            }
            return id;
        }, ct);

    public Task<RespuestaAdmin<bool>> EstadoCuentaAsync(Guid org, Guid acceso, Guid cuenta, bool activo, CancellationToken ct) =>
        EjecutarAsync<bool>(org, acceso, async tx =>
        {
            await using var cmd = tx.Comando("SELECT seguridad.admin_estado_cuenta($1, $2, $3)", org, cuenta, activo);
            await cmd.ExecuteNonQueryAsync(ct);
            return true;
        }, ct);

    public Task<RespuestaAdmin<bool>> EstadoEquipoAsync(Guid org, Guid acceso, Guid dispositivo, bool habilitado, CancellationToken ct) =>
        EjecutarAsync<bool>(org, acceso, async tx =>
        {
            await using var cmd = tx.Comando("SELECT seguridad.admin_estado_equipo($1, $2, $3)", org, dispositivo, habilitado);
            await cmd.ExecuteNonQueryAsync(ct);
            return true;
        }, ct);

    public async Task<AjustePendientes> PendientesAyerAsync(Guid org, Guid acceso, CancellationToken ct)
    {
        await using var tx = await TransaccionContexto.AbrirAsync(datos, org, acceso, ct);
        var general = true;
        var cuentas = new List<AjustePendientesDeCuenta>();
        await using (var cmd = tx.Comando("SELECT acceso_id, activo FROM seguridad.admin_pendientes_ayer($1)", org))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct))
                if (r.IsDBNull(0)) general = r.GetBoolean(1);
                else cuentas.Add(new AjustePendientesDeCuenta(r.GetGuid(0), r.GetBoolean(1)));
        await tx.ConfirmarAsync(ct);
        return new AjustePendientes(general, cuentas);
    }

    public Task<RespuestaAdmin<bool>> GuardarPendientesAyerAsync(Guid org, Guid acceso, Guid? cuenta, bool activo, CancellationToken ct) =>
        EjecutarAsync<bool>(org, acceso, async tx =>
        {
            await using var cmd = tx.Comando("SELECT seguridad.admin_guardar_pendientes_ayer($1, $2::uuid, $3)", org, (object?)cuenta ?? DBNull.Value, activo);
            await cmd.ExecuteNonQueryAsync(ct);
            return true;
        }, ct);

    public async Task<bool> SesionConfirmadaAsync(Guid org, Guid acceso, Guid sesion, CancellationToken ct)
    {
        await using var tx = await TransaccionContexto.AbrirAsync(datos, org, acceso, ct);
        await using var cmd = tx.Comando($"SELECT seguridad.sesion_confirmada($1, $2, {EndpointsAdministracion.MinutosConfirmacion})", org, sesion);
        var ok = (bool)(await cmd.ExecuteScalarAsync(ct))!;
        await tx.ConfirmarAsync(ct);
        return ok;
    }

    public async Task<bool> ConfirmarIdentidadAsync(Guid org, Guid acceso, Guid sesion, IdentidadGoogle identidad, CancellationToken ct)
    {
        await using var tx = await TransaccionContexto.AbrirAsync(datos, org, acceso, ct);
        await using var cmd = tx.Comando("SELECT seguridad.confirmar_identidad_web($1, $2, $3, $4, $5)", org, acceso, sesion, identidad.Emisor, identidad.Sujeto);
        var ok = (bool)(await cmd.ExecuteScalarAsync(ct))!;
        await tx.ConfirmarAsync(ct);
        return ok;
    }

    public async Task<IReadOnlyDictionary<string, string>> MisPermisosAsync(Guid org, Guid acceso, CancellationToken ct)
    {
        await using var tx = await TransaccionContexto.AbrirAsync(datos, org, acceso, ct);
        var mapa = new Dictionary<string, string>();
        await using (var cmd = tx.Comando("SELECT pantalla, nivel FROM seguridad.mis_permisos($1)", org))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct)) mapa[r.GetString(0)] = r.GetString(1);
        await tx.ConfirmarAsync(ct);
        return mapa;
    }

    private async Task<RespuestaAdmin<T>> EjecutarAsync<T>(Guid org, Guid acceso, Func<TransaccionContexto, Task<T>> accion, CancellationToken ct)
    {
        await using var tx = await TransaccionContexto.AbrirAsync(datos, org, acceso, ct);
        try
        {
            var valor = await accion(tx);
            await tx.ConfirmarAsync(ct);
            return new RespuestaAdmin<T>(ResultadoAdmin.Ok, null, valor);
        }
        catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.InsufficientPrivilege)
        { return new RespuestaAdmin<T>(ResultadoAdmin.SinPermiso, null, default); }
        catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.NoDataFound || e.SqlState == "P0002")
        { return new RespuestaAdmin<T>(ResultadoAdmin.NoEncontrada, null, default); }
        catch (PostgresException e) when (e.SqlState == "P0422")
        { return new RespuestaAdmin<T>(e.MessageText is "minimo_administradores" or "no_a_ti_mismo" or "correo_en_uso" or "correo_vinculado" ? ResultadoAdmin.Conflicto : ResultadoAdmin.Invalida, e.MessageText, default); }
    }
}

/// <summary>Pantallas del panel y los prefijos de API que las representan. Dashboard, Monitoreo y Reportes no tienen API propia.</summary>
public static class PantallasPanel
{
    private static readonly (string Prefijo, string Pantalla)[] Prefijos =
    [
        ("/api/rutas", "rutas"), ("/api/ordenes", "monitoreo"), ("/api/jornadas", "monitoreo"),
        ("/api/clientes", "clientes"), ("/api/sitios", "sitios"), ("/api/plantas", "sitios"),
        ("/api/unidades", "unidades"), ("/api/operadores", "operadores"),
        ("/api/manifiestos", "manifiestos"), ("/api/incidencias", "incidencias"),
    ];

    /// <summary>Estas pantallas se leen también como catálogo de las que arman o revisan servicios.</summary>
    private static readonly string[] Catalogos = ["clientes", "sitios", "unidades", "operadores"];
    private static readonly string[] Consumidoras = ["rutas", "monitoreo", "manifiestos", "incidencias"];

    /// <summary>Marca que devuelve mis_permisos cuando la cuenta ya tiene permisos por pantalla.</summary>
    public const string Explicito = "_explicito";

    /// <summary>Las fotos y firmas se ven desde estas pantallas (Incidencias, Manifiestos, Órdenes/Monitoreo y Rutas, que muestra lo del día): sin acceso a ninguna, la evidencia tampoco se sirve.</summary>
    private static readonly string[] ConsumidorasDeArchivos = ["incidencias", "manifiestos", "monitoreo", "rutas"];

    public static bool EsArchivo(PathString ruta) => ruta.StartsWithSegments("/api/archivos", StringComparison.OrdinalIgnoreCase);

    public static bool PermitidoArchivo(IReadOnlyDictionary<string, string> permisos) =>
        ConsumidorasDeArchivos.Any(p => permisos.TryGetValue(p, out var n) && n != "sin");

    public static string? De(PathString ruta)
    {
        foreach (var (prefijo, pantalla) in Prefijos)
            if (ruta.StartsWithSegments(prefijo, StringComparison.OrdinalIgnoreCase)) return pantalla;
        return null;
    }

    public static bool Permitido(string pantalla, bool lectura, IReadOnlyDictionary<string, string> permisos)
    {
        string Nivel(string p) => permisos.TryGetValue(p, out var n) ? n : "sin";
        var propio = Nivel(pantalla);
        if (!lectura) return propio == "editar" || (pantalla == "manifiestos" && propio == "ver");   // imprimir/validar es consulta
        if (propio != "sin") return true;
        if (pantalla == "monitoreo" && Nivel("rutas") != "sin") return true;   // Rutas muestra lo que ocurre cada día
        return Catalogos.Contains(pantalla) && Consumidoras.Any(p => Nivel(p) != "sin");
    }
}

public static class EndpointsAdministracion
{
    public const string MensajeSinPermiso = "Solo un administrador puede hacer esto.";
    public const string MensajeConfirmar = "Confirma tu identidad con Google para continuar.";
    public const string CodigoConfirmar = "confirmar";
    public const int MinutosConfirmacion = 5;

    public static IServiceCollection AgregarAdministracion(this IServiceCollection services) =>
        services.AddSingleton<IRepositorioAdministracion, RepositorioAdministracion>();

    /// <summary>Niega por pantalla las peticiones del panel según el permiso de la cuenta (además de RLS en la base).</summary>
    public static IApplicationBuilder UsarPermisosPorPantalla(this IApplicationBuilder app) =>
        app.Use(async (http, next) =>
        {
            var pantalla = PantallasPanel.De(http.Request.Path);
            var esArchivo = PantallasPanel.EsArchivo(http.Request.Path);
            if ((pantalla is not null || esArchivo) && ServiciosAcceso.LeerIds(http.User, out var org, out var acceso))
            {
                var repo = http.RequestServices.GetRequiredService<IRepositorioAdministracion>();
                var permisos = await repo.MisPermisosAsync(org, acceso, http.RequestAborted);
                // Cuentas anteriores a 0072 (sin permisos por pantalla): decide cada endpoint con datos.leer / datos.escribir.
                if (!permisos.ContainsKey(PantallasPanel.Explicito)) { await next(); return; }
                var lectura = HttpMethods.IsGet(http.Request.Method) || HttpMethods.IsHead(http.Request.Method);
                var permitido = esArchivo ? PantallasPanel.PermitidoArchivo(permisos) : PantallasPanel.Permitido(pantalla!, lectura, permisos);
                if (!permitido)
                {
                    http.Response.StatusCode = StatusCodes.Status403Forbidden;
                    await http.Response.WriteAsJsonAsync(new MensajeError("Tu cuenta no tiene permiso para esta pantalla."), http.RequestAborted);
                    return;
                }
            }
            await next();
        });

    public static void MapearAdministracion(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/admin");

        // Para que el panel abra «Confirma que eres tú» en cuanto se pulsa, sin intentar antes la acción.
        g.MapGet("/confirmacion", async (HttpContext http, IRepositorioAdministracion r) =>
        {
            if (!ServiciosAcceso.LeerIds(http.User, out var org, out var acceso) || !Guid.TryParse(http.User.FindFirstValue(ServiciosAcceso.ClaimSesionWeb), out var sesion)) return Results.Unauthorized();
            http.Response.Headers.CacheControl = "no-store";
            return Results.Ok(new { vigente = await r.SesionConfirmadaAsync(org, acceso, sesion, http.RequestAborted), minutos = MinutosConfirmacion });
        });

        g.MapGet("/cuentas", (HttpContext http, IRepositorioAdministracion r) => Leer(http, async (o, a, ct) => Results.Ok(await r.CuentasAsync(o, a, ct))));
        g.MapGet("/deshabilitados", (HttpContext http, IRepositorioAdministracion r) => Leer(http, async (o, a, ct) => Results.Ok(await r.DeshabilitadosAsync(o, a, ct))));

        g.MapGet("/pendientes-ayer", (HttpContext http, IRepositorioAdministracion r) => Leer(http, async (o, a, ct) => Results.Ok(await r.PendientesAyerAsync(o, a, ct))));
        g.MapPut("/pendientes-ayer", (SolicitudPendientes s, HttpContext http, IRepositorioAdministracion r) =>
            Escribir(http, r, async (o, a, ct) => s.Activo is not { } activo
                ? Results.BadRequest(new MensajeError("Indica si se activa o se desactiva."))
                : Respuesta(await r.GuardarPendientesAyerAsync(o, a, s.CuentaId, activo, ct), _ => Results.NoContent())))
            .AddEndpointFilter(EndpointsAcceso.ValidarCsrf);

        g.MapPost("/confirmar", async (SolicitudConfirmacion s, HttpContext http, IRepositorioAdministracion r, IValidadorTokenGoogle validador, IOptions<OpcionesAcceso> opciones) =>
        {
            if (!ServiciosAcceso.LeerIds(http.User, out var org, out var acceso) || !Guid.TryParse(http.User.FindFirstValue(ServiciosAcceso.ClaimSesionWeb), out var sesion)) return Results.Unauthorized();
            var identidad = await validador.ValidarAsync(s.Credential ?? "", http.RequestAborted);
            if (identidad is null || !identidad.CorreoVerificado || !await r.ConfirmarIdentidadAsync(org, acceso, sesion, identidad, http.RequestAborted))
                return Results.Json(new MensajeError("No se pudo confirmar tu identidad. Usa la cuenta con la que iniciaste sesión."), statusCode: StatusCodes.Status403Forbidden);
            return Results.NoContent();
        }).AddEndpointFilter(EndpointsAcceso.ValidarCsrf).RequireRateLimiting(ServiciosAcceso.PoliticaLogin);

        g.MapPost("/cuentas", (SolicitudCuenta s, HttpContext http, IRepositorioAdministracion r) =>
            Escribir(http, r, async (o, a, ct) => Respuesta(await r.GuardarCuentaAsync(o, a, null, s, ct), id => Results.Ok(new { id }))))
            .AddEndpointFilter(EndpointsAcceso.ValidarCsrf);
        g.MapPut("/cuentas/{id:guid}", (Guid id, SolicitudCuenta s, HttpContext http, IRepositorioAdministracion r) =>
            Escribir(http, r, async (o, a, ct) => Respuesta(await r.GuardarCuentaAsync(o, a, id, s, ct), v => Results.Ok(new { id = v }))))
            .AddEndpointFilter(EndpointsAcceso.ValidarCsrf);
        g.MapPost("/cuentas/{id:guid}/estado", (Guid id, SolicitudEstado s, HttpContext http, IRepositorioAdministracion r) =>
            Escribir(http, r, async (o, a, ct) => Respuesta(await r.EstadoCuentaAsync(o, a, id, s.Activo, ct), _ => Results.NoContent())))
            .AddEndpointFilter(EndpointsAcceso.ValidarCsrf);
        g.MapPost("/equipos/{id:guid}/estado", (Guid id, SolicitudEstado s, HttpContext http, IRepositorioAdministracion r) =>
            Escribir(http, r, async (o, a, ct) => Respuesta(await r.EstadoEquipoAsync(o, a, id, s.Activo, ct), _ => Results.NoContent())))
            .AddEndpointFilter(EndpointsAcceso.ValidarCsrf);
    }

    private static async Task<IResult> Leer(HttpContext http, Func<Guid, Guid, CancellationToken, Task<IResult>> accion)
    {
        if (!ServiciosAcceso.LeerIds(http.User, out var org, out var acceso)) return Results.Unauthorized();
        http.Response.Headers.CacheControl = "no-store";
        try { return await accion(org, acceso, http.RequestAborted); }
        catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.InsufficientPrivilege)
        { return Results.Json(new MensajeError(MensajeSinPermiso), statusCode: StatusCodes.Status403Forbidden); }
    }

    /// <summary>Toda escritura de Administración exige una confirmación reciente de Google y, dentro de la base, el rol administrador.</summary>
    private static async Task<IResult> Escribir(HttpContext http, IRepositorioAdministracion r, Func<Guid, Guid, CancellationToken, Task<IResult>> accion)
    {
        if (!ServiciosAcceso.LeerIds(http.User, out var org, out var acceso) || !Guid.TryParse(http.User.FindFirstValue(ServiciosAcceso.ClaimSesionWeb), out var sesion)) return Results.Unauthorized();
        if (!await r.SesionConfirmadaAsync(org, acceso, sesion, http.RequestAborted))
            return Results.Json(new { mensaje = MensajeConfirmar, codigo = CodigoConfirmar }, statusCode: StatusCodes.Status403Forbidden);
        return await accion(org, acceso, http.RequestAborted);
    }

    private static IResult Respuesta<T>(RespuestaAdmin<T> r, Func<T, IResult> ok) => r.Resultado switch
    {
        ResultadoAdmin.Ok => ok(r.Valor!),
        ResultadoAdmin.SinPermiso => Results.Json(new MensajeError(MensajeSinPermiso), statusCode: StatusCodes.Status403Forbidden),
        ResultadoAdmin.NoEncontrada => Results.Json(new MensajeError("No se encontró lo que intentas cambiar."), statusCode: StatusCodes.Status404NotFound),
        ResultadoAdmin.Conflicto => Results.Json(new { mensaje = MensajeMotivo(r.Motivo), codigo = r.Motivo }, statusCode: StatusCodes.Status409Conflict),
        _ => Results.Json(new { mensaje = MensajeMotivo(r.Motivo), codigo = r.Motivo }, statusCode: StatusCodes.Status400BadRequest),
    };

    private static string MensajeMotivo(string? motivo) => motivo switch
    {
        "minimo_administradores" => "Debe quedar al menos otro administrador activo.",
        "no_a_ti_mismo" => "No puedes hacer este cambio en tu propia cuenta.",
        "correo_en_uso" => "Ese correo ya tiene una cuenta.",
        "correo_vinculado" => "Esta cuenta ya inició sesión: su correo no se puede cambiar.",
        "correo_invalido" => "Escribe un correo válido.",
        "nombre_invalido" => "Escribe el nombre.",
        "rol_invalido" => "Elige el tipo de cuenta.",
        "sin_acceso" => "Elige al menos una pantalla.",
        "cuenta_de_operador" => "Las cuentas de teléfono se manejan en Operadores.",
        _ => "Revisa los datos de la cuenta.",
    };
}
