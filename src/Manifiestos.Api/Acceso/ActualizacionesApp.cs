using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Manifiestos.Api.Acceso;

namespace Manifiestos.Api.Acceso;

/// <summary>Versión de la app que el servidor ofrece a los teléfonos. El tamaño se mide del archivo; el resto viene de version.json.</summary>
public sealed record OfertaApp(int VersionCode, string VersionName, string Sha256, long Tamano, bool Obligatoria, string? Notas);

/// <summary>
/// Actualizaciones de la app Android desde el propio servidor (decisión 0041), mientras no exista Google Play. Una carpeta con DOS archivos de
/// nombre fijo: <c>app-piloto.apk</c> y <c>version.json</c> (versionCode, versionName, sha256, obligatoria, notas). Solo se ofrece una versión cuyo
/// APK existe y cuya huella SHA-256 coincide con la declarada; cualquier cosa rara (archivo a medio subir, JSON inválido, huella distinta)
/// se trata como «no hay actualización». El APK no es secreto (la app es pública y solo entra quien está en la lista de autorizados); Android
/// además solo instala una actualización firmada con la misma llave que la app ya instalada.
/// </summary>
public sealed partial class AlmacenActualizaciones(IConfiguration config, IWebHostEnvironment entorno, ILogger<AlmacenActualizaciones> log)
{
    public const string NombreApk = "app-piloto.apk";
    public const string NombreVersion = "version.json";
    public const string TipoApk = "application/vnd.android.package-archive";

    [GeneratedRegex("^[0-9a-f]{64}$")] private static partial Regex Hexadecimal();

    private readonly object _candado = new();
    private (long Tamano, DateTime Escrito, string Sha256)? _huellaMedida;

    private string Carpeta => config["Actualizaciones:Ruta"]
        ?? (entorno.IsDevelopment() ? Path.GetFullPath(Path.Combine(entorno.ContentRootPath, "..", "..", ".local", "actualizaciones")) : "/datos/actualizaciones");

    /// <summary>La oferta vigente y la ruta de su APK, o null si no hay una válida.</summary>
    public (OfertaApp Oferta, string RutaApk)? Vigente()
    {
        try
        {
            var rutaJson = Path.Combine(Carpeta, NombreVersion);
            var rutaApk = Path.Combine(Carpeta, NombreApk);
            if (!File.Exists(rutaJson) || !File.Exists(rutaApk)) return null;
            if (new FileInfo(rutaJson).Length > 16 * 1024) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(rutaJson));
            var r = doc.RootElement;
            if (!r.TryGetProperty("versionCode", out var vc) || !vc.TryGetInt32(out var versionCode) || versionCode < 1) return null;
            var nombre = r.TryGetProperty("versionName", out var vn) && vn.ValueKind == JsonValueKind.String ? vn.GetString()?.Trim() : null;
            if (string.IsNullOrEmpty(nombre) || nombre.Length > 40) return null;
            var huella = r.TryGetProperty("sha256", out var h) && h.ValueKind == JsonValueKind.String ? h.GetString()?.Trim().ToLowerInvariant() : null;
            if (huella is null || !Hexadecimal().IsMatch(huella)) return null;
            var obligatoria = r.TryGetProperty("obligatoria", out var o) && o.ValueKind == JsonValueKind.True;
            var notas = r.TryGetProperty("notas", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString()?.Trim() : null;
            if (notas is { Length: > 500 }) return null;
            var info = new FileInfo(rutaApk);
            if (info.Length is 0 or > 200L * 1024 * 1024) return null;
            if (!HuellaCoincide(rutaApk, info, huella)) { log.LogWarning("La huella del APK de actualización no coincide con version.json; no se ofrece."); return null; }
            return (new OfertaApp(versionCode, nombre, huella, info.Length, obligatoria, string.IsNullOrEmpty(notas) ? null : notas), rutaApk);
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            log.LogWarning(e, "No se pudo leer la actualización de la app.");
            return null;
        }
    }

    // El APK pesa decenas de MB: la huella se mide una vez por cambio del archivo (tamaño y fecha), no en cada consulta.
    private bool HuellaCoincide(string rutaApk, FileInfo info, string esperada)
    {
        lock (_candado)
        {
            if (_huellaMedida is { } m && m.Tamano == info.Length && m.Escrito == info.LastWriteTimeUtc) return m.Sha256 == esperada;
            using var flujo = File.OpenRead(rutaApk);
            var medida = Convert.ToHexStringLower(SHA256.HashData(flujo));
            _huellaMedida = (info.Length, info.LastWriteTimeUtc, medida);
            return medida == esperada;
        }
    }
}

public static class EndpointsActualizacionesApp
{
    public static IServiceCollection AgregarActualizacionesApp(this IServiceCollection services) => services.AddSingleton<AlmacenActualizaciones>();

    public static void MapearActualizacionesApp(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/movil/app");
        g.AddEndpointFilter(async (c, next) =>
        {
            c.HttpContext.Response.Headers.CacheControl = "no-store";
            if (!c.HttpContext.Request.IsHttps) return Results.BadRequest(new MensajeError("El acceso móvil requiere HTTPS."));
            return await next(c);
        });
        // Anónimas a propósito: la consulta ocurre también con la sesión vencida, y así una versión rota se puede reemplazar. Con el limitador del login.
        g.MapGet("/version", (AlmacenActualizaciones almacen) =>
            almacen.Vigente() is { } v ? Results.Ok(v.Oferta) : Results.NoContent()).AllowAnonymous().RequireRateLimiting(ServiciosAcceso.PoliticaLogin);
        g.MapGet("/apk", (AlmacenActualizaciones almacen) =>
            almacen.Vigente() is { } v
                ? Results.File(v.RutaApk, AlmacenActualizaciones.TipoApk, "ar-logistics.apk", enableRangeProcessing: true)
                : Results.NotFound()).AllowAnonymous().RequireRateLimiting(ServiciosAcceso.PoliticaLogin);
    }
}
