using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Manifiestos.Api.Acceso;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Manifiestos.Api.Avisos;

/// <summary>
/// Avisos push (Firebase Cloud Messaging, API HTTP v1) al teléfono del operador cuando Operaciones cambia el trabajo de HOY (PG-37).
/// La clave de servicio vive FUERA del repositorio (<c>Push:CredencialesRuta</c>); sin ella el emisor no hace nada. La app además consulta
/// el trabajo periódicamente, así que un aviso perdido no pierde el cambio.
/// </summary>
public sealed class OpcionesPush
{
    /// <summary>Ruta del JSON de la cuenta de servicio de Firebase. Vacío = avisos desactivados.</summary>
    public string? CredencialesRuta { get; set; }
}

public sealed record AvisoPush(string Titulo, string Texto);

public interface IEmisorPush
{
    /// <summary>Envía el aviso a cada token; devuelve los tokens que FCM ya no reconoce (hay que olvidarlos).</summary>
    Task<IReadOnlyList<string>> EnviarAsync(IReadOnlyList<string> tokens, AvisoPush aviso, CancellationToken ct);
}

/// <summary>Emisor FCM v1. Pide el token de acceso OAuth con un JWT firmado por la cuenta de servicio y lo reutiliza hasta que está por vencer.</summary>
public sealed class EmisorFcm(HttpClient http, IOptions<OpcionesPush> opciones, ILogger<EmisorFcm> log) : IEmisorPush
{
    private readonly SemaphoreSlim _candado = new(1, 1);
    private string? _acceso; private DateTimeOffset _vence;
    private Cuenta? _cuenta; private bool _leida;

    private sealed record Cuenta(string ProyectoId, string Correo, string TokenUri, RSA Clave);

    private Cuenta? LeerCuenta()
    {
        if (_leida) return _cuenta;
        _leida = true;
        var ruta = opciones.Value.CredencialesRuta;
        if (string.IsNullOrWhiteSpace(ruta) || !File.Exists(ruta)) return null;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(ruta));
            var r = doc.RootElement;
            var rsa = RSA.Create();
            rsa.ImportFromPem(r.GetProperty("private_key").GetString());
            _cuenta = new Cuenta(r.GetProperty("project_id").GetString()!, r.GetProperty("client_email").GetString()!,
                r.TryGetProperty("token_uri", out var u) ? u.GetString()! : "https://oauth2.googleapis.com/token", rsa);
        }
        catch (Exception e) { log.LogError(e, "No se pudo leer la cuenta de servicio de Firebase; los avisos quedan desactivados."); }
        return _cuenta;
    }

    private static string B64(ReadOnlySpan<byte> b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private async Task<string?> AccesoAsync(Cuenta c, CancellationToken ct)
    {
        await _candado.WaitAsync(ct);
        try
        {
            if (_acceso is not null && _vence > DateTimeOffset.UtcNow.AddMinutes(5)) return _acceso;
            var ahora = DateTimeOffset.UtcNow;
            var cabecera = B64(Encoding.UTF8.GetBytes("{\"alg\":\"RS256\",\"typ\":\"JWT\"}"));
            var cuerpo = B64(JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object>
            {
                ["iss"] = c.Correo, ["scope"] = "https://www.googleapis.com/auth/firebase.messaging", ["aud"] = c.TokenUri,
                ["iat"] = ahora.ToUnixTimeSeconds(), ["exp"] = ahora.AddMinutes(55).ToUnixTimeSeconds(),
            }));
            var firma = B64(c.Clave.SignData(Encoding.ASCII.GetBytes($"{cabecera}.{cuerpo}"), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
            using var r = await http.PostAsync(c.TokenUri, new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "urn:ietf:params:oauth:grant-type:jwt-bearer", ["assertion"] = $"{cabecera}.{cuerpo}.{firma}",
            }), ct);
            if (!r.IsSuccessStatusCode) { log.LogWarning("Firebase rechazó el acceso de la cuenta de servicio ({Estado}).", (int)r.StatusCode); return null; }
            var t = await r.Content.ReadFromJsonAsync<JsonElement>(ct);
            _acceso = t.GetProperty("access_token").GetString();
            _vence = ahora.AddSeconds(t.GetProperty("expires_in").GetInt32());
            return _acceso;
        }
        finally { _candado.Release(); }
    }

    public async Task<IReadOnlyList<string>> EnviarAsync(IReadOnlyList<string> tokens, AvisoPush aviso, CancellationToken ct)
    {
        var olvidar = new List<string>();
        var c = LeerCuenta();
        if (c is null || tokens.Count == 0) return olvidar;
        var acceso = await AccesoAsync(c, ct);
        if (acceso is null) return olvidar;
        foreach (var token in tokens)
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, $"https://fcm.googleapis.com/v1/projects/{c.ProyectoId}/messages:send")
            {
                Content = JsonContent.Create(new
                {
                    message = new
                    {
                        token,
                        // Solo datos: así la app despierta aunque esté en segundo plano, descarga el trabajo y arma ella misma la notificación.
                        data = new Dictionary<string, string> { ["tipo"] = "trabajo", ["titulo"] = aviso.Titulo, ["texto"] = aviso.Texto },
                        android = new { priority = "HIGH" },
                    },
                }),
            };
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", acceso);
            try
            {
                using var r = await http.SendAsync(req, ct);
                if (r.IsSuccessStatusCode) continue;
                // 404 UNREGISTERED / 400 INVALID_ARGUMENT del token: ya no sirve. No se registra el token (identifica una instalación).
                if ((int)r.StatusCode is 404 or 400) olvidar.Add(token);
                else log.LogWarning("Firebase no entregó un aviso ({Estado}).", (int)r.StatusCode);
            }
            catch (HttpRequestException e) { log.LogWarning(e, "No se pudo contactar a Firebase."); }
        }
        return olvidar;
    }
}

/// <summary>Decide a quién avisar cuando cambia el trabajo de una ruta de hoy y manda el aviso sin frenar ni romper la operación que lo causó.</summary>
public sealed class AvisosTrabajo(NpgsqlDataSource datos, IEmisorPush emisor, IOptions<OpcionesAcceso> acceso, ILogger<AvisosTrabajo> log)
{
    public const string Titulo = "Tu ruta cambió";

    /// <summary>Avisa a los teléfonos del operador de la ruta SI la ruta sale en la fecha indicada y esa fecha es hoy. Nunca lanza.</summary>
    public async Task AvisarAsync(Guid rutaId, DateOnly fecha, string texto, CancellationToken ct = default)
    {
        try
        {
            if (fecha != HoyLocal()) return;
            var org = acceso.Value.OrganizacionId;
            await using var cmd = datos.CreateCommand("SELECT token FROM seguridad.destinatarios_aviso_ruta($1,$2)");
            cmd.Parameters.AddWithValue(org); cmd.Parameters.AddWithValue(rutaId);
            var tokens = new List<string>();
            await using (var lector = await cmd.ExecuteReaderAsync(ct)) while (await lector.ReadAsync(ct)) tokens.Add(lector.GetString(0));
            if (tokens.Count == 0) return;
            foreach (var muerto in await emisor.EnviarAsync(tokens, new AvisoPush(Titulo, texto), ct))
            {
                await using var del = datos.CreateCommand("SELECT seguridad.olvidar_token_push($1,$2)");
                del.Parameters.AddWithValue(org); del.Parameters.AddWithValue(muerto);
                await del.ExecuteNonQueryAsync(ct);
            }
        }
        catch (Exception e) { log.LogWarning(e, "No se pudo avisar el cambio de la ruta {Ruta}.", rutaId); }
    }

    /// <summary>La ruta sale hoy (según su última revisión)? Para los cambios del plan, que no traen fecha.</summary>
    public async Task AvisarSiSaleHoyAsync(Guid rutaId, string texto, CancellationToken ct = default)
    {
        try
        {
            var hoy = HoyLocal();
            await using var cmd = datos.CreateCommand("SELECT coalesce((SELECT (dias & (1 << ($3 - 1))) <> 0 FROM negocio.ruta_version WHERE organizacion_id=$1 AND ruta_id=$2 ORDER BY revision DESC LIMIT 1), false)");
            cmd.Parameters.AddWithValue(acceso.Value.OrganizacionId); cmd.Parameters.AddWithValue(rutaId); cmd.Parameters.AddWithValue((int)(hoy.DayOfWeek == DayOfWeek.Sunday ? 7 : (int)hoy.DayOfWeek));
            if ((bool)(await cmd.ExecuteScalarAsync(ct))!) await AvisarAsync(rutaId, hoy, texto, ct);
        }
        catch (Exception e) { log.LogWarning(e, "No se pudo revisar si la ruta {Ruta} sale hoy.", rutaId); }
    }

    /// <summary>«Hoy» de las rutas: zona horaria del negocio (Monterrey); la zona definitiva sigue pendiente (PG-18).</summary>
    public static DateOnly HoyLocal()
    {
        try { return DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeBySystemTimeZoneId(DateTime.UtcNow, "America/Monterrey")); }
        catch (TimeZoneNotFoundException) { return DateOnly.FromDateTime(DateTime.UtcNow.AddHours(-6)); }
    }
}

public static class ServiciosAvisos
{
    public static IServiceCollection AgregarAvisosPush(this IServiceCollection services, IConfiguration config)
    {
        services.Configure<OpcionesPush>(config.GetSection("Push"));
        services.AddHttpClient<IEmisorPush, EmisorFcm>();
        services.AddSingleton<AvisosTrabajo>();
        return services;
    }
}
