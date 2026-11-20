using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Manifiestos.Api.Archivos;

/// <summary>
/// Drive en segundo plano: todo archivo (fotos, firmas y, más adelante, PDF) se recibe primero en el VPS y de ahí se archiva en Drive sin que
/// la app ni el panel esperen. La clave de la cuenta de servicio vive FUERA del repositorio (<c>Drive:CredencialesRuta</c>).
/// </summary>
public sealed class OpcionesDrive
{
    /// <summary>Ruta del JSON de la cuenta de servicio. Vacío = la subida a Drive queda desactivada y los archivos se quedan en el VPS.</summary>
    public string? CredencialesRuta { get; set; }
    /// <summary>ID de la unidad compartida.</summary>
    public string? UnidadId { get; set; }
    /// <summary>Carpeta raíz dentro de la unidad. Vacío = «manifiestos-local» fuera de producción y «manifiestos» en producción.</summary>
    public string? CarpetaRaiz { get; set; }
    public int SegundosEntreRevisiones { get; set; } = 10;
}

public sealed record ArchivoParaDrive(Guid TareaId, Guid Organizacion, Guid Sucursal, Guid ArchivoId, string ClaveVps, string Mime, byte[] Sha256, long Tamano, DateTimeOffset Ocurrida, int Intentos);

public interface IAlmacenDrive
{
    bool Activo { get; }
    /// <summary>Sube (o reconoce el ya subido con el mismo identificador estable) y devuelve el ID de Drive tras verificar tamaño y MD5.</summary>
    Task<string> SubirAsync(ArchivoParaDrive archivo, string rutaLocal, CancellationToken ct);
    /// <summary>Lee un archivo ya subido (cuando la copia del VPS se limpió). null si Drive no está configurado o el archivo no existe.</summary>
    Task<byte[]?> LeerAsync(string driveId, CancellationToken ct);
}

public sealed class AlmacenDrive(HttpClient http, IOptions<OpcionesDrive> opciones, IHostEnvironment entorno, ILogger<AlmacenDrive> log) : IAlmacenDrive
{
    private const string Api = "https://www.googleapis.com/drive/v3";
    private readonly SemaphoreSlim _candado = new(1, 1);
    private readonly ConcurrentDictionary<string, string> _carpetas = new();
    private string? _acceso; private DateTimeOffset _vence;
    private (string Correo, string TokenUri, RSA Clave)? _cuenta; private bool _leida;

    public bool Activo => LeerCuenta() is not null && !string.IsNullOrWhiteSpace(opciones.Value.UnidadId);
    private string Raiz => string.IsNullOrWhiteSpace(opciones.Value.CarpetaRaiz) ? (entorno.IsProduction() ? "manifiestos" : "manifiestos-local") : opciones.Value.CarpetaRaiz!;

    private (string Correo, string TokenUri, RSA Clave)? LeerCuenta()
    {
        if (_leida) return _cuenta;
        _leida = true;
        var ruta = opciones.Value.CredencialesRuta;
        if (string.IsNullOrWhiteSpace(ruta) || !File.Exists(ruta)) return null;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(ruta));
            var r = doc.RootElement; var rsa = RSA.Create();
            rsa.ImportFromPem(r.GetProperty("private_key").GetString());
            _cuenta = (r.GetProperty("client_email").GetString()!, r.TryGetProperty("token_uri", out var u) ? u.GetString()! : "https://oauth2.googleapis.com/token", rsa);
        }
        catch (Exception e) { log.LogError(e, "No se pudo leer la cuenta de servicio de Drive; los archivos se quedan en el VPS."); }
        return _cuenta;
    }

    private static string B64(ReadOnlySpan<byte> b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private async Task<string> AccesoAsync(CancellationToken ct)
    {
        var c = LeerCuenta() ?? throw new InvalidOperationException("Drive no está configurado.");
        await _candado.WaitAsync(ct);
        try
        {
            if (_acceso is not null && _vence > DateTimeOffset.UtcNow.AddMinutes(5)) return _acceso;
            var ahora = DateTimeOffset.UtcNow;
            var cab = B64(Encoding.UTF8.GetBytes("{\"alg\":\"RS256\",\"typ\":\"JWT\"}"));
            var cuerpo = B64(JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object>
            {
                ["iss"] = c.Correo, ["scope"] = "https://www.googleapis.com/auth/drive", ["aud"] = c.TokenUri,
                ["iat"] = ahora.ToUnixTimeSeconds(), ["exp"] = ahora.AddMinutes(55).ToUnixTimeSeconds(),
            }));
            var firma = B64(c.Clave.SignData(Encoding.ASCII.GetBytes($"{cab}.{cuerpo}"), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
            using var r = await http.PostAsync(c.TokenUri, new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "urn:ietf:params:oauth:grant-type:jwt-bearer", ["assertion"] = $"{cab}.{cuerpo}.{firma}",
            }), ct);
            if (!r.IsSuccessStatusCode) throw new HttpRequestException($"Google rechazó el acceso de la cuenta de servicio ({(int)r.StatusCode}).");
            var t = await r.Content.ReadFromJsonAsync<JsonElement>(ct);
            _acceso = t.GetProperty("access_token").GetString();
            _vence = ahora.AddSeconds(t.GetProperty("expires_in").GetInt32());
            return _acceso!;
        }
        finally { _candado.Release(); }
    }

    private async Task<JsonElement> PedirAsync(HttpMethod metodo, string url, HttpContent? contenido, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(metodo, url) { Content = contenido };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await AccesoAsync(ct));
        using var r = await http.SendAsync(req, ct);
        if (!r.IsSuccessStatusCode) throw new HttpRequestException($"Drive respondió {(int)r.StatusCode}.");
        return await r.Content.ReadFromJsonAsync<JsonElement>(ct);
    }

    private static string Q(string s) => s.Replace("\\", "\\\\").Replace("'", "\\'");

    /// <summary>Busca o crea una carpeta por nombre dentro de su padre. El nombre visible no es clave del negocio; solo ordena el archivo.</summary>
    private async Task<string> CarpetaAsync(string nombre, string padre, CancellationToken ct)
    {
        var llave = $"{padre}/{nombre}";
        if (_carpetas.TryGetValue(llave, out var id)) return id;
        var q = $"name='{Q(nombre)}' and mimeType='application/vnd.google-apps.folder' and '{padre}' in parents and trashed=false";
        var lista = await PedirAsync(HttpMethod.Get, $"{Api}/files?q={Uri.EscapeDataString(q)}&corpora=drive&driveId={opciones.Value.UnidadId}&includeItemsFromAllDrives=true&supportsAllDrives=true&fields=files(id)", null, ct);
        var existentes = lista.GetProperty("files");
        if (existentes.GetArrayLength() > 0) id = existentes[0].GetProperty("id").GetString()!;
        else
        {
            var nueva = await PedirAsync(HttpMethod.Post, $"{Api}/files?supportsAllDrives=true&fields=id",
                JsonContent.Create(new { name = nombre, mimeType = "application/vnd.google-apps.folder", parents = new[] { padre } }), ct);
            id = nueva.GetProperty("id").GetString()!;
        }
        _carpetas[llave] = id;
        return id;
    }

    public async Task<byte[]?> LeerAsync(string driveId, CancellationToken ct)
    {
        if (!Activo || string.IsNullOrWhiteSpace(driveId)) return null;
        using var req = new HttpRequestMessage(HttpMethod.Get, $"{Api}/files/{Uri.EscapeDataString(driveId)}?alt=media&supportsAllDrives=true");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await AccesoAsync(ct));
        using var r = await http.SendAsync(req, ct);
        if (r.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        if (!r.IsSuccessStatusCode) throw new HttpRequestException($"Drive respondió {(int)r.StatusCode}.");
        return await r.Content.ReadAsByteArrayAsync(ct);
    }

    public async Task<string> SubirAsync(ArchivoParaDrive a, string rutaLocal, CancellationToken ct)
    {
        var unidad = opciones.Value.UnidadId!;
        var bytes = await File.ReadAllBytesAsync(rutaLocal, ct);
        if (bytes.LongLength != a.Tamano || !SHA256.HashData(bytes).AsSpan().SequenceEqual(a.Sha256))
            throw new InvalidDataException("El archivo local no coincide con lo recibido; no se sube.");
        var md5 = Convert.ToHexString(MD5.HashData(bytes)).ToLowerInvariant();
        var clave = a.ArchivoId.ToString("N");

        // Idempotencia: un reintento tras un resultado perdido reconoce el archivo ya subido por su identificador estable.
        var q = $"appProperties has {{ key='archivoId' and value='{clave}' }} and trashed=false";
        var previo = await PedirAsync(HttpMethod.Get, $"{Api}/files?q={Uri.EscapeDataString(q)}&corpora=drive&driveId={unidad}&includeItemsFromAllDrives=true&supportsAllDrives=true&fields=files(id,size,md5Checksum)", null, ct);
        foreach (var f in previo.GetProperty("files").EnumerateArray())
            if (f.TryGetProperty("md5Checksum", out var m) && m.GetString() == md5) return f.GetProperty("id").GetString()!;

        var raiz = await CarpetaAsync(Raiz, unidad, ct);
        var dia = await CarpetaAsync(a.Ocurrida.UtcDateTime.ToString("yyyy-MM-dd"), raiz, ct);
        var extension = a.Mime == "image/png" ? ".png" : a.Mime == "application/pdf" ? ".pdf" : ".jpg";
        var cuerpo = new MultipartContent("related");
        cuerpo.Add(JsonContent.Create(new { name = clave + extension, parents = new[] { dia }, appProperties = new Dictionary<string, string> { ["archivoId"] = clave } }));
        var datos = new ByteArrayContent(bytes); datos.Headers.ContentType = new MediaTypeHeaderValue(a.Mime);
        cuerpo.Add(datos);
        var subido = await PedirAsync(HttpMethod.Post, "https://www.googleapis.com/upload/drive/v3/files?uploadType=multipart&supportsAllDrives=true&fields=id,size,md5Checksum", cuerpo, ct);
        if (subido.GetProperty("md5Checksum").GetString() != md5 || long.Parse(subido.GetProperty("size").GetString()!) != a.Tamano)
            throw new InvalidDataException("Drive devolvió un archivo distinto al enviado.");
        return subido.GetProperty("id").GetString()!;
    }
}

/// <summary>Proceso en segundo plano: toma las subidas pendientes de la cola, las archiva en Drive y reprograma con espera creciente si algo falla.</summary>
public sealed class ProcesoSubidaDrive(NpgsqlDataSource datos, IAlmacenDrive drive, IOptions<OpcionesDrive> opciones, IConfiguration config, IHostEnvironment entorno, ILogger<ProcesoSubidaDrive> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (!drive.Activo) { log.LogInformation("Subida a Drive desactivada: los archivos permanecen en el VPS."); return; }
        var espera = TimeSpan.FromSeconds(Math.Max(1, opciones.Value.SegundosEntreRevisiones));
        while (!ct.IsCancellationRequested)
        {
            try { if (await ProcesarUnoAsync(ct)) continue; }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception e) { log.LogWarning(e, "El ciclo de subida a Drive falló; se reintenta."); }
            try { await Task.Delay(espera, ct); } catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>Procesa una subida pendiente. Devuelve false cuando no hay nada que hacer.</summary>
    public async Task<bool> ProcesarUnoAsync(CancellationToken ct)
    {
        ArchivoParaDrive? a;
        await using (var cmd = datos.CreateCommand("SELECT tarea_id,organizacion_id,sucursal_id,archivo_id,clave_vps,mime,sha256,tamano_bytes,ocurrida_en,intentos FROM sync.reservar_subida_drive()"))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            a = await r.ReadAsync(ct) ? new ArchivoParaDrive(r.GetGuid(0), r.GetGuid(1), r.GetGuid(2), r.GetGuid(3), r.GetString(4), r.GetString(5), (byte[])r[6], r.GetInt64(7), r.GetFieldValue<DateTimeOffset>(8), r.GetInt32(9)) : null;
        if (a is null) return false;
        try
        {
            var raiz = config["Archivos:Ruta"] ?? Path.GetFullPath(Path.Combine(entorno.ContentRootPath, "..", "..", ".local", "archivos"));
            var id = await drive.SubirAsync(a, Path.Combine(raiz, a.ClaveVps), ct);
            await using var ok = datos.CreateCommand("SELECT sync.completar_subida_drive($1,$2,$3,$4,$5)");
            ok.Parameters.AddWithValue(a.TareaId); ok.Parameters.AddWithValue(a.Organizacion); ok.Parameters.AddWithValue(a.Sucursal); ok.Parameters.AddWithValue(a.ArchivoId); ok.Parameters.AddWithValue(id);
            await ok.ExecuteNonQueryAsync(ct);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // Sin datos personales ni nombres de archivo en el registro; la evidencia local no se toca.
            log.LogWarning("No se pudo archivar un archivo en Drive (intento {Intentos}): {Motivo}", a.Intentos, e.Message);
            await using var mal = datos.CreateCommand("SELECT sync.reprogramar_subida_drive($1,$2,$3)");
            mal.Parameters.AddWithValue(a.TareaId); mal.Parameters.AddWithValue(a.Organizacion); mal.Parameters.AddWithValue(a.Sucursal);
            await mal.ExecuteNonQueryAsync(CancellationToken.None);
        }
        return true;
    }
}

public static class ServiciosDrive
{
    public static IServiceCollection AgregarDrive(this IServiceCollection services, IConfiguration config)
    {
        services.Configure<OpcionesDrive>(config.GetSection("Drive"));
        services.AddHttpClient<IAlmacenDrive, AlmacenDrive>();
        services.AddSingleton<ProcesoSubidaDrive>();
        services.AddHostedService(sp => sp.GetRequiredService<ProcesoSubidaDrive>());
        services.AddSingleton<LimpiezaArchivosLocales>();
        services.AddHostedService(sp => sp.GetRequiredService<LimpiezaArchivosLocales>());
        return services;
    }
}
