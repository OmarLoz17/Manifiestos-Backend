using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.BearerToken;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Manifiestos.Api.Acceso;

public sealed record SolicitudDesafioMovil(Guid InstalacionId);
public sealed record DesafioMovil(Guid Id, string Nonce, int ExpiresIn);
public sealed record SolicitudGoogleMovil(string? Credential, Guid InstalacionId, Guid DesafioId);
public sealed record SolicitudRenovarMovil(string? RefreshToken);
public sealed record ContextoOperador(Guid AccesoId, Guid SucursalId, Guid PersonaId, string Nombre, string Correo,
    Guid IdentidadId, Guid InstalacionId, Guid AsignacionEquipoId, int RevisionEquipo);
public sealed record UsuarioMovil(Guid OrganizacionId, ContextoOperador Operador, string Rol = "telefono_operador");

public sealed record SolicitudTokenAviso(string? Token);

public sealed class RepositorioAccesoMovil(NpgsqlDataSource datos)
{
    public async Task<string> RenovarAsync(Guid org, Guid sesion, CancellationToken ct)
    {
        await using var cmd = datos.CreateCommand("SELECT seguridad.renovar_sesion_movil($1,$2)");
        cmd.Parameters.AddWithValue(org); cmd.Parameters.AddWithValue(sesion);
        return (string)(await cmd.ExecuteScalarAsync(ct))!;
    }
    /// <summary>Guarda el token de avisos push (FCM) de esta instalación. False = sin contexto vigente. Ver migración 0037.</summary>
    public async Task<bool> RegistrarTokenPushAsync(Guid org, Guid acceso, Guid instalacion, string token, CancellationToken ct)
    {
        await using var cmd = datos.CreateCommand("SELECT seguridad.registrar_token_push($1,$2,$3,$4)");
        cmd.Parameters.AddWithValue(org); cmd.Parameters.AddWithValue(acceso); cmd.Parameters.AddWithValue(instalacion); cmd.Parameters.AddWithValue(token);
        return (bool)(await cmd.ExecuteScalarAsync(ct))!;
    }
    /// <summary>Trabajo del día del operador autenticado (JSON ya armado en la base, solo lectura). Null = sin contexto vigente.</summary>
    public async Task<string?> TrabajoAsync(Guid org, Guid acceso, Guid instalacion, DateOnly fecha, CancellationToken ct)
    {
        // Las rutas fijas están programadas por omisión: se crea la jornada del día de las que salen hoy y aún no tienen fila (migración 0049).
        await using (var asegurar = datos.CreateCommand("SELECT seguridad.asegurar_jornadas_del_dia($1,$2,$3,$4)"))
        {
            asegurar.Parameters.AddWithValue(org); asegurar.Parameters.AddWithValue(acceso); asegurar.Parameters.AddWithValue(instalacion); asegurar.Parameters.AddWithValue(fecha);
            await asegurar.ExecuteNonQueryAsync(ct);
        }
        await using var cmd = datos.CreateCommand("SELECT seguridad.trabajo_operador($1,$2,$3,$4)::text");
        cmd.Parameters.AddWithValue(org); cmd.Parameters.AddWithValue(acceso); cmd.Parameters.AddWithValue(instalacion);
        cmd.Parameters.AddWithValue(fecha);
        return await cmd.ExecuteScalarAsync(ct) as string;
    }
    /// <summary>Estado de «Terminar parada» enviado por el teléfono ({"paradas":[{jornadaId,paradaId,terminada}]}); solo registra cambios. Migración 0056.</summary>
    public async Task<string> RegistrarParadasTerminadasAsync(Guid org, Guid acceso, Guid instalacion, string json, CancellationToken ct)
    {
        // Resultado POR PARADA (aceptada, duplicada o rechazada): el teléfono solo da por confirmado lo aceptado o duplicado (migración 0065).
        await using var cmd = datos.CreateCommand("SELECT seguridad.registrar_paradas_terminadas_detalle($1,$2,$3,$4::jsonb)::text");
        cmd.Parameters.AddWithValue(org); cmd.Parameters.AddWithValue(acceso); cmd.Parameters.AddWithValue(instalacion); cmd.Parameters.AddWithValue(json);
        return (string)(await cmd.ExecuteScalarAsync(ct))!;
    }
    /// <summary>Registra una captura (hecho recibido) con el sobre EXACTO; idempotente por (instalación, id). Ver migración 0024.</summary>
    public async Task<string> RecibirCapturaAsync(Guid org, Guid acceso, Guid instalacion, byte[] solicitud, CancellationToken ct)
    {
        await using var cmd = datos.CreateCommand("SELECT sync.recibir_captura($1,$2,$3,$4)::text");
        cmd.Parameters.AddWithValue(org); cmd.Parameters.AddWithValue(acceso); cmd.Parameters.AddWithValue(instalacion);
        cmd.Parameters.AddWithValue(solicitud);
        return (string)(await cmd.ExecuteScalarAsync(ct))!;
    }
    public async Task<string?> ConsultarCapturaAsync(Guid org, Guid acceso, Guid instalacion, Guid captura, CancellationToken ct)
    {
        await using var cmd = datos.CreateCommand("SELECT sync.consultar_captura($1,$2,$3,$4)::text");
        cmd.Parameters.AddWithValue(org); cmd.Parameters.AddWithValue(acceso); cmd.Parameters.AddWithValue(instalacion); cmd.Parameters.AddWithValue(captura);
        return await cmd.ExecuteScalarAsync(ct) as string;
    }
    public sealed record EvidenciaEsperada(long Tamano, byte[] Sha256, string Rol, bool Recibida);
    public async Task<EvidenciaEsperada?> EvidenciaEsperadaAsync(Guid org, Guid acceso, Guid instalacion, Guid evidencia, CancellationToken ct)
    {
        await using var cmd = datos.CreateCommand("SELECT tamano_bytes, sha256, rol, recibida FROM sync.evidencia_esperada($1,$2,$3,$4)");
        cmd.Parameters.AddWithValue(org); cmd.Parameters.AddWithValue(acceso); cmd.Parameters.AddWithValue(instalacion); cmd.Parameters.AddWithValue(evidencia);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? new(r.GetInt64(0), (byte[])r[1], r.GetString(2), r.GetBoolean(3)) : null;
    }
    public async Task<string?> RegistrarArchivoAsync(Guid org, Guid acceso, Guid instalacion, Guid evidencia, string mime, string clave, CancellationToken ct)
    {
        await using var cmd = datos.CreateCommand("SELECT sync.registrar_archivo($1,$2,$3,$4,$5,$6)::text");
        cmd.Parameters.AddWithValue(org); cmd.Parameters.AddWithValue(acceso); cmd.Parameters.AddWithValue(instalacion);
        cmd.Parameters.AddWithValue(evidencia); cmd.Parameters.AddWithValue(mime); cmd.Parameters.AddWithValue(clave);
        return await cmd.ExecuteScalarAsync(ct) as string;
    }
    public async Task<string> PlantasAsync(Guid org, Guid acceso, Guid instalacion, CancellationToken ct)
    {
        await using var cmd = datos.CreateCommand("SELECT seguridad.plantas_operador($1,$2,$3)::text");
        cmd.Parameters.AddWithValue(org); cmd.Parameters.AddWithValue(acceso); cmd.Parameters.AddWithValue(instalacion);
        return (string)(await cmd.ExecuteScalarAsync(ct))!;
    }
    public async Task<Guid?> CrearDesafioAsync(Guid org, Guid instalacion, byte[] hash, CancellationToken ct)
    {
        await using var cmd = datos.CreateCommand("SELECT seguridad.crear_desafio_movil($1,$2,$3)");
        cmd.Parameters.AddWithValue(org); cmd.Parameters.AddWithValue(instalacion); cmd.Parameters.AddWithValue(hash);
        var id = await cmd.ExecuteScalarAsync(ct); return id is Guid g ? g : null;
    }
    public async Task<Guid?> IniciarAsync(Guid org, IdentidadGoogle identidad, Guid instalacion, Guid desafio, CancellationToken ct)
    {
        await using var cmd = datos.CreateCommand("SELECT sesion FROM seguridad.iniciar_sesion_movil($1,$2,$3,$4,$5,$6,$7)");
        cmd.Parameters.AddWithValue(org); cmd.Parameters.AddWithValue(identidad.Emisor); cmd.Parameters.AddWithValue(identidad.Sujeto);
        cmd.Parameters.AddWithValue(identidad.Correo); cmd.Parameters.AddWithValue(instalacion); cmd.Parameters.AddWithValue(desafio);
        cmd.Parameters.AddWithValue(SHA256.HashData(Encoding.UTF8.GetBytes(identidad.Nonce!)));
        var id = await cmd.ExecuteScalarAsync(ct); return id is Guid g ? g : null;
    }
    /// <summary>Desafío para pedir acceso desde un equipo que todavía no está vinculado (migración 0070).</summary>
    public async Task<Guid> CrearDesafioSolicitudAsync(Guid org, Guid instalacion, byte[] hash, CancellationToken ct)
    {
        await using var cmd = datos.CreateCommand("SELECT seguridad.crear_desafio_solicitud_equipo($1,$2,$3)");
        cmd.Parameters.AddWithValue(org); cmd.Parameters.AddWithValue(instalacion); cmd.Parameters.AddWithValue(hash);
        return (Guid)(await cmd.ExecuteScalarAsync(ct))!;
    }
    /// <summary>'solicitada', 'vinculado' o el motivo del rechazo. Solo correos ya autorizados pueden solicitar; no habilita nada.</summary>
    public async Task<string> SolicitarEquipoAsync(Guid org, IdentidadGoogle identidad, Guid instalacion, Guid desafio, CancellationToken ct)
    {
        await using var cmd = datos.CreateCommand("SELECT seguridad.solicitar_equipo($1,$2,$3,$4,$5,$6,$7)");
        cmd.Parameters.AddWithValue(org); cmd.Parameters.AddWithValue(identidad.Emisor); cmd.Parameters.AddWithValue(identidad.Sujeto);
        cmd.Parameters.AddWithValue(identidad.Correo); cmd.Parameters.AddWithValue(instalacion); cmd.Parameters.AddWithValue(desafio);
        cmd.Parameters.AddWithValue(SHA256.HashData(Encoding.UTF8.GetBytes(identidad.Nonce!)));
        return (string)(await cmd.ExecuteScalarAsync(ct))!;
    }
    public async Task<ContextoOperador?> ObtenerAsync(Guid org, Guid sesion, CancellationToken ct)
    {
        await using var cmd = datos.CreateCommand("SELECT * FROM seguridad.sesion_operador($1,$2)");
        cmd.Parameters.AddWithValue(org); cmd.Parameters.AddWithValue(sesion);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? new(r.GetGuid(0),r.GetGuid(1),r.GetGuid(2),r.GetString(3),r.GetString(4),r.GetGuid(5),r.GetGuid(6),r.GetGuid(7),r.GetInt32(8)) : null;
    }
    public async Task CerrarAsync(Guid org, Guid sesion, CancellationToken ct)
    {
        await using var cmd = datos.CreateCommand("SELECT seguridad.cerrar_sesion_movil($1,$2)");
        cmd.Parameters.AddWithValue(org); cmd.Parameters.AddWithValue(sesion); await cmd.ExecuteNonQueryAsync(ct);
    }
}

public static class AccesoMovil
{
    public const string Esquema = "operador-movil";
    public const string Politica = "operador-movil-vigente";
    private const string ClaimSesion = "manifiestos:sesion-movil";
    private const string ItemContexto = "manifiestos:operador-movil";
    /// <summary>Clave de HttpContext.Items con el operador ya validado (la usa la exigencia de aceptación de términos).</summary>
    public const string ItemContextoPublico = ItemContexto;

    public static IServiceCollection AgregarAccesoMovil(this IServiceCollection services)
    {
        services.AddSingleton<RepositorioAccesoMovil>();
        services.AgregarActualizacionesApp();
        // Tokens opacos del framework. Refresh y sesión se renuevan juntos (migración 0022).
        services.AddAuthentication().AddBearerToken(Esquema, o => {
            o.BearerTokenExpiration=TimeSpan.FromMinutes(15); o.RefreshTokenExpiration=TimeSpan.FromDays(30);
        });
        services.AddAuthorizationBuilder().AddPolicy(Politica, new AuthorizationPolicyBuilder(Esquema)
            .RequireAuthenticatedUser().RequireAssertion(async c => {
                if (c.Resource is not HttpContext http || !http.Request.IsHttps) return false;
                var operador = await RevalidarAsync(http.User,http);
                if (operador is null) return false;
                http.Items[ItemContexto]=operador; return true;
            }).Build());
        return services;
    }
    public static void MapearAccesoMovil(this IEndpointRouteBuilder app)
    {
        app.MapearActualizacionesApp();
        var g=app.MapGroup("/api/movil/auth");
        g.AddEndpointFilter(async (c,next) => {
            c.HttpContext.Response.Headers.CacheControl="no-store";
            if (!c.HttpContext.Request.IsHttps) return Results.BadRequest(new MensajeError("El acceso móvil requiere HTTPS."));
            return await next(c);
        });
        g.MapPost("/desafio", async (SolicitudDesafioMovil s,HttpContext http,RepositorioAccesoMovil repo,IOptions<OpcionesAcceso> opciones) => {
            if (s.InstalacionId==Guid.Empty) return Results.BadRequest();
            var nonce=Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
            var id=await repo.CrearDesafioAsync(opciones.Value.OrganizacionId,s.InstalacionId,SHA256.HashData(Encoding.UTF8.GetBytes(nonce)),http.RequestAborted);
            return id is null ? Rechazo() : Results.Ok(new DesafioMovil(id.Value,nonce,120));
        }).AllowAnonymous().RequireRateLimiting(ServiciosAcceso.PoliticaLogin);
        g.MapPost("/google", async (SolicitudGoogleMovil s,HttpContext http,IValidadorTokenGoogle google,RepositorioAccesoMovil repo,IOptions<OpcionesAcceso> opciones) => {
            if (s.InstalacionId==Guid.Empty || s.DesafioId==Guid.Empty) return Results.BadRequest();
            var identidad=await google.ValidarAsync(s.Credential??"",http.RequestAborted);
            if (identidad is null) return Results.Json(new MensajeError(EndpointsAcceso.MensajeTokenInvalido),statusCode:401);
            if (!identidad.CorreoVerificado || string.IsNullOrWhiteSpace(identidad.Nonce) || identidad.Nonce.Length>256) return Rechazo();
            var org=opciones.Value.OrganizacionId;
            var sesion=await repo.IniciarAsync(org,identidad,s.InstalacionId,s.DesafioId,http.RequestAborted);
            return sesion is null ? Rechazo() : Emitir(org,sesion.Value);
        }).AllowAnonymous().RequireRateLimiting(ServiciosAcceso.PoliticaLogin);
        // «Solicitar acceso»: un equipo sin vincular pide que Operaciones lo apruebe para una cuenta ya autorizada.
        // Misma validación de Google y nonce de un solo uso que el login; la solicitud no habilita nada.
        g.MapPost("/solicitar-desafio", async (SolicitudDesafioMovil s,HttpContext http,RepositorioAccesoMovil repo,IOptions<OpcionesAcceso> opciones) => {
            if (s.InstalacionId==Guid.Empty) return Results.BadRequest();
            var nonce=Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
            var id=await repo.CrearDesafioSolicitudAsync(opciones.Value.OrganizacionId,s.InstalacionId,SHA256.HashData(Encoding.UTF8.GetBytes(nonce)),http.RequestAborted);
            return Results.Ok(new DesafioMovil(id,nonce,120));
        }).AllowAnonymous().RequireRateLimiting(ServiciosAcceso.PoliticaLogin);
        g.MapPost("/solicitar-equipo", async (SolicitudGoogleMovil s,HttpContext http,IValidadorTokenGoogle google,RepositorioAccesoMovil repo,IOptions<OpcionesAcceso> opciones) => {
            if (s.InstalacionId==Guid.Empty || s.DesafioId==Guid.Empty) return Results.BadRequest();
            var identidad=await google.ValidarAsync(s.Credential??"",http.RequestAborted);
            if (identidad is null) return Results.Json(new MensajeError(EndpointsAcceso.MensajeTokenInvalido),statusCode:401);
            if (!identidad.CorreoVerificado || string.IsNullOrWhiteSpace(identidad.Nonce) || identidad.Nonce.Length>256) return Rechazo();
            var resultado=await repo.SolicitarEquipoAsync(opciones.Value.OrganizacionId,identidad,s.InstalacionId,s.DesafioId,http.RequestAborted);
            return resultado is "solicitada" or "vinculado" ? Results.Ok(new { estado = resultado=="solicitada" ? "pendiente" : "vinculado" }) : Rechazo();
        }).AllowAnonymous().RequireRateLimiting(ServiciosAcceso.PoliticaLogin);
        g.MapPost("/refresh", async (SolicitudRenovarMovil s,HttpContext http,IOptionsMonitor<BearerTokenOptions> tokens,
            RepositorioAccesoMovil repo,IOptions<OpcionesAcceso> acceso) => {
            if (string.IsNullOrWhiteSpace(s.RefreshToken) || s.RefreshToken.Length>20000) return Results.Unauthorized();
            var opciones=tokens.Get(Esquema);
            var ticket=opciones.RefreshTokenProtector.Unprotect(s.RefreshToken);
            if (ticket?.Properties.ExpiresUtc is not { } vence || vence<=TimeProvider.System.GetUtcNow()
                || !LeerSesion(ticket.Principal,out var org,out var sesion) || org!=acceso.Value.OrganizacionId) return Results.Unauthorized();
            var resultado=await repo.RenovarAsync(org,sesion,http.RequestAborted);
            return resultado=="renovada" ? Emitir(org,sesion)
                : resultado=="caducada" ? Results.Unauthorized() : Rechazo();
        }).AllowAnonymous().RequireRateLimiting(ServiciosAcceso.PoliticaRenovar);
        g.MapGet("/me", (HttpContext http,IOptions<OpcionesAcceso> opciones) =>
            Results.Ok(new UsuarioMovil(opciones.Value.OrganizacionId,(ContextoOperador)http.Items[ItemContexto]!))).RequireAuthorization(Politica);
        g.MapPost("/token-aviso", async (SolicitudTokenAviso s,HttpContext http,RepositorioAccesoMovil repo,IOptions<OpcionesAcceso> opciones) => {
            var token=s.Token?.Trim();
            if (token is null || token.Length is < 20 or > 4096) return Results.BadRequest(new MensajeError("Token de aviso inválido."));
            var c=(ContextoOperador)http.Items[ItemContexto]!;
            return await repo.RegistrarTokenPushAsync(opciones.Value.OrganizacionId,c.AccesoId,c.InstalacionId,token,http.RequestAborted) ? Results.NoContent() : Rechazo();
        }).RequireAuthorization(Politica);
        g.MapPost("/logout", async (HttpContext http,RepositorioAccesoMovil repo) => {
            LeerSesion(http.User,out var org,out var sesion); await repo.CerrarAsync(org,sesion,http.RequestAborted);
            return Results.NoContent();
        }).RequireAuthorization(Politica);

        // Trabajo del día (A5): solo lectura, limitado a la persona de la sesión. Sin capturas/firmas/manifiestos previos.
        var t=app.MapGroup("/api/movil/trabajo");
        t.AddEndpointFilter(async (c,next) => {
            c.HttpContext.Response.Headers.CacheControl="no-store";
            if (!c.HttpContext.Request.IsHttps) return Results.BadRequest(new MensajeError("El acceso móvil requiere HTTPS."));
            return await next(c);
        });
        t.MapGet("", async (string? fecha,HttpContext http,RepositorioAccesoMovil repo,IOptions<OpcionesAcceso> opciones) => {
            if (!DateOnly.TryParseExact(fecha,"yyyy-MM-dd",out var f)) return Results.BadRequest(new MensajeError("Indica la fecha como AAAA-MM-DD."));
            var c=(ContextoOperador)http.Items[ItemContexto]!;
            string? json;
            try { json=await repo.TrabajoAsync(opciones.Value.OrganizacionId,c.AccesoId,c.InstalacionId,f,http.RequestAborted); }
            catch (PostgresException e) when (e.SqlState=="22023") { return Results.BadRequest(new MensajeError("Solo se descarga el trabajo de hoy.")); }
            if (json is null) return Rechazo();
            // Huella para que el teléfono compare versiones sin reinterpretar el contenido.
            var huella="\""+Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(json)))+"\"";
            http.Response.Headers.ETag=huella;
            if (http.Request.Headers.IfNoneMatch==huella) return Results.StatusCode(StatusCodes.Status304NotModified);
            return Results.Text(json,"application/json",Encoding.UTF8);
        }).RequireAuthorization(Politica);
    }
    public static void MapearCapturasMovil(this IEndpointRouteBuilder app)
    {
        // Recepción de capturas (A6a). Solo guarda hechos: no genera manifiestos, folios ni incidencias de panel.
        var g=app.MapGroup("/api/movil/capturas");
        g.AddEndpointFilter(async (c,next) => {
            c.HttpContext.Response.Headers.CacheControl="no-store";
            if (!c.HttpContext.Request.IsHttps) return Results.BadRequest(new MensajeError("El acceso móvil requiere HTTPS."));
            return await next(c);
        });
        g.MapPost("", async (HttpContext http,RepositorioAccesoMovil repo,IOptions<OpcionesAcceso> opciones) => {
            if (!(http.Request.ContentType??"").StartsWith("application/json",StringComparison.OrdinalIgnoreCase))
                return Results.Json(new MensajeError("El envío debe ser JSON."),statusCode:415);
            // El sobre se conserva byte a byte: no se deserializa ni se reescribe antes de guardarlo.
            using var ms=new MemoryStream(); var buffer=new byte[8192]; int n; long total=0;
            while ((n=await http.Request.Body.ReadAsync(buffer,http.RequestAborted))>0) {
                total+=n; if (total>262144) return Results.Json(new MensajeError("El envío es demasiado grande."),statusCode:413);
                ms.Write(buffer,0,n);
            }
            var c=(ContextoOperador)http.Items[ItemContexto]!;
            try { return Results.Text(await repo.RecibirCapturaAsync(opciones.Value.OrganizacionId,c.AccesoId,c.InstalacionId,ms.ToArray(),http.RequestAborted),"application/json",Encoding.UTF8); }
            catch (PostgresException e) when (e.SqlState=="42501") { return Rechazo(); }
            catch (PostgresException e) when (e.SqlState=="P0409") { return Results.Json(new MensajeError("Esa captura ya se recibió con otro contenido."),statusCode:409); }
            catch (PostgresException e) when (e.SqlState=="P0422") { return Results.Json(new MensajeError(e.MessageText),statusCode:422); }
            catch (PostgresException e) when (e.SqlState is "22023" or "22P02" or "22003" or "22007" or "23502" or "23514" or "23503" or "23505")
                { return Results.BadRequest(new MensajeError("El envío no es válido.")); }
        }).RequireAuthorization(Politica);
        g.MapGet("/{id:guid}", async (Guid id,HttpContext http,RepositorioAccesoMovil repo,IOptions<OpcionesAcceso> opciones) => {
            var c=(ContextoOperador)http.Items[ItemContexto]!;
            var json=await repo.ConsultarCapturaAsync(opciones.Value.OrganizacionId,c.AccesoId,c.InstalacionId,id,http.RequestAborted);
            return json is null ? Results.NotFound() : Results.Text(json,"application/json",Encoding.UTF8);
        }).RequireAuthorization(Politica);

        // «Terminar parada» / «Reabrir parada»: el teléfono envía el estado de sus paradas terminadas (idempotente, solo cambios).
        app.MapPut("/api/movil/paradas-terminadas",async (HttpContext http,RepositorioAccesoMovil repo,IOptions<OpcionesAcceso> opciones) => {
            if (!http.Request.IsHttps) return Results.BadRequest(new MensajeError("El acceso móvil requiere HTTPS."));
            http.Response.Headers.CacheControl="no-store";
            if (!(http.Request.ContentType??"").StartsWith("application/json",StringComparison.OrdinalIgnoreCase))
                return Results.Json(new MensajeError("El envío debe ser JSON."),statusCode:415);
            using var ms=new MemoryStream(); var buffer=new byte[8192]; int n; long total=0;
            while ((n=await http.Request.Body.ReadAsync(buffer,http.RequestAborted))>0) {
                total+=n; if (total>65536) return Results.Json(new MensajeError("El envío es demasiado grande."),statusCode:413);
                ms.Write(buffer,0,n);
            }
            var c=(ContextoOperador)http.Items[ItemContexto]!;
            try {
                var detalle=await repo.RegistrarParadasTerminadasAsync(opciones.Value.OrganizacionId,c.AccesoId,c.InstalacionId,Encoding.UTF8.GetString(ms.ToArray()),http.RequestAborted);
                using var doc=JsonDocument.Parse(detalle);
                return Results.Ok(new { registradas=doc.RootElement.EnumerateArray().Count(x => x.GetProperty("resultado").GetString()=="aceptada"), paradas=doc.RootElement.Clone() });
            }
            catch (PostgresException e) when (e.SqlState=="42501") { return Rechazo(); }
            catch (PostgresException e) when (e.SqlState is "22023" or "22P02" or "22003" or "22007" or "23502" or "23514" or "23503" or "23505")
                { return Results.BadRequest(new MensajeError("El envío no es válido.")); }
        }).RequireAuthorization(Politica);

        // Plantas (PTAR) para el paso 3. Lista provisional: sitios que no son punto de servicio (ver migración 0026).
        app.MapGet("/api/movil/plantas",async (HttpContext http,RepositorioAccesoMovil repo,IOptions<OpcionesAcceso> opciones) => {
            if (!http.Request.IsHttps) return Results.BadRequest(new MensajeError("El acceso móvil requiere HTTPS."));
            http.Response.Headers.CacheControl="no-store";
            var c=(ContextoOperador)http.Items[ItemContexto]!;
            var json=await repo.PlantasAsync(opciones.Value.OrganizacionId,c.AccesoId,c.InstalacionId,http.RequestAborted);
            var huella="\""+Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(json)))+"\"";
            http.Response.Headers.ETag=huella;
            if (http.Request.Headers.IfNoneMatch==huella) return Results.StatusCode(StatusCodes.Status304NotModified);
            return Results.Text(json,"application/json",Encoding.UTF8);
        }).RequireAuthorization(Politica);

        // Archivos de evidencia: el cuerpo se verifica contra el tamaño y SHA-256 DECLARADOS en el sobre antes de guardarse;
        // se guarda primero en el VPS y la subida a Drive la hará solo el backend (tarea encolada, no ejecutada aquí).
        app.MapPut("/api/movil/archivos/{id:guid}",async (Guid id,HttpContext http,RepositorioAccesoMovil repo,IOptions<OpcionesAcceso> opciones,
            IConfiguration config,IWebHostEnvironment entorno) => {
            if (!http.Request.IsHttps) return Results.BadRequest(new MensajeError("El acceso móvil requiere HTTPS."));
            http.Response.Headers.CacheControl="no-store";
            var org=opciones.Value.OrganizacionId; var c=(ContextoOperador)http.Items[ItemContexto]!; var ct=http.RequestAborted;
            var esperado=await repo.EvidenciaEsperadaAsync(org,c.AccesoId,c.InstalacionId,id,ct);
            if (esperado is null) return Results.NotFound();
            if (esperado.Recibida) return Results.Ok(new { estado="recibida",id });   // reintento: no se vuelve a leer ni a guardar
            var mime=(http.Request.ContentType??"").Split(';')[0].Trim().ToLowerInvariant();
            if (mime is not ("image/jpeg" or "image/png")) return Results.Json(new MensajeError("Tipo de archivo no permitido."),statusCode:415);
            // El tipo debe corresponder al rol de la evidencia: la foto es JPEG y las firmas son PNG.
            if (ValidadorImagen.MimeDelRol(esperado.Rol)!=mime) return Results.Json(new MensajeError("El tipo de archivo no corresponde a la evidencia."),statusCode:422);
            // Tope de tamaño propio (no el declarado por el cliente): una foto optimizada pesa ~0.5 MB; la firma (PNG), decenas de KB. Configurable.
            var tope=mime=="image/png" ? config.GetValue("Archivos:MaxFirmaBytes",512*1024L) : config.GetValue("Archivos:MaxFotoBytes",2*1024*1024L);
            if (esperado.Tamano>tope) return Results.Json(new MensajeError("El archivo es demasiado grande."),statusCode:413);
            var raiz=config["Archivos:Ruta"] ?? (entorno.IsDevelopment() ? Path.GetFullPath(Path.Combine(entorno.ContentRootPath,"..","..",".local","archivos")) : null);
            if (raiz is null) return Results.Json(new MensajeError("El almacenamiento de archivos no está configurado."),statusCode:503);
            // La clave física lleva todo el ámbito de la identidad (organización, sucursal e id): un mismo id en otra sucursal no puede chocar.
            var relativa=Path.Combine(org.ToString("N"),c.SucursalId.ToString("N"),id.ToString("N")[..2],id.ToString("N")+(mime=="image/png"?".png":".jpg"));
            var destino=Path.Combine(raiz,relativa); Directory.CreateDirectory(Path.GetDirectoryName(destino)!);
            var temporal=destino+".parcial-"+Guid.NewGuid().ToString("N");
            try {
                long total=0; using var hash=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                await using (var salida=new FileStream(temporal,FileMode.CreateNew,FileAccess.Write,FileShare.None,81920,FileOptions.WriteThrough)) {
                    var buffer=new byte[81920]; int n;
                    while ((n=await http.Request.Body.ReadAsync(buffer,ct))>0) {
                        total+=n;
                        if (total>esperado.Tamano) return Results.Json(new MensajeError("El archivo no coincide con lo declarado."),statusCode:422);
                        hash.AppendData(buffer,0,n); await salida.WriteAsync(buffer.AsMemory(0,n),ct);
                    }
                    await salida.FlushAsync(ct);
                }
                if (total!=esperado.Tamano || !hash.GetHashAndReset().AsSpan().SequenceEqual(esperado.Sha256))
                    return Results.Json(new MensajeError("El archivo no coincide con lo declarado."),statusCode:422);
                // El tipo se comprueba por el CONTENIDO: estructura completa, dimensiones y datos decodificables, no solo la firma ni el encabezado del cliente.
                if (!ValidadorImagen.Valida(temporal,mime)) return Results.Json(new MensajeError("El archivo no es una imagen del tipo declarado."),statusCode:422);
                // Idempotente ante reintentos concurrentes: el archivo ya fue verificado (tamaño y SHA-256 contra lo declarado), así que si otra
                // solicitud lo dejó primero en su lugar, el contenido es el mismo y no se sobrescribe. File.Move sin sobrescribir es atómico: el
                // perdedor de la carrera recibe IOException y solo descarta su copia temporal.
                try { File.Move(temporal,destino); }
                catch (IOException) when (File.Exists(destino)) { File.Delete(temporal); }
                var json=await repo.RegistrarArchivoAsync(org,c.AccesoId,c.InstalacionId,id,mime,relativa.Replace('\\','/'),ct);
                return json is null ? Results.NotFound() : Results.Text(json,"application/json",Encoding.UTF8);
            }
            catch (PostgresException e) when (e.SqlState=="42501") { return Rechazo(); }
            finally { try { if (File.Exists(temporal)) File.Delete(temporal); } catch (IOException) { /* queda para limpieza; nunca borra evidencia ya verificada */ } }
        }).RequireAuthorization(Politica);
    }
    private static IResult Rechazo() => Results.Json(new MensajeError(EndpointsAcceso.MensajeNoAutorizada),statusCode:403);
    private static IResult Emitir(Guid org,Guid sesion) => Results.SignIn(new ClaimsPrincipal(new ClaimsIdentity([
        new Claim(ServiciosAcceso.ClaimOrganizacion,org.ToString()),new Claim(ClaimSesion,sesion.ToString())],Esquema)),authenticationScheme:Esquema);
    private static bool LeerSesion(ClaimsPrincipal p,out Guid org,out Guid sesion) {
        sesion=Guid.Empty; return Guid.TryParse(p.FindFirstValue(ServiciosAcceso.ClaimOrganizacion),out org)
            && Guid.TryParse(p.FindFirstValue(ClaimSesion),out sesion);
    }
    private static async Task<ContextoOperador?> RevalidarAsync(ClaimsPrincipal p,HttpContext http) {
        if (!LeerSesion(p,out var org,out var sesion) || org!=http.RequestServices.GetRequiredService<IOptions<OpcionesAcceso>>().Value.OrganizacionId) return null;
        return await http.RequestServices.GetRequiredService<RepositorioAccesoMovil>().ObtenerAsync(org,sesion,http.RequestAborted);
    }
}
