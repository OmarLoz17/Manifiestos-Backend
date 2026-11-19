using System.Net.Http.Json;
using Manifiestos.Api.Acceso;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Manifiestos.Api.Tests.Infraestructura;

/// <summary>
/// Sustituto del validador de Google. Formato del token ficticio:
/// "sujeto|correo|verificado|nombre". Cualquier otro texto = token inválido
/// (equivale a firma, emisor, audiencia o vigencia rechazados por la librería oficial).
/// </summary>
public sealed class ValidadorFicticio : IValidadorTokenGoogle
{
    public static string Token(string sujeto, string correo, bool verificado = true, string nombre = "Persona Ficticia", string? nonce = null) =>
        $"{sujeto}|{correo}|{verificado}|{nombre}" + (nonce is null ? "" : $"|{nonce}");

    public Task<IdentidadGoogle?> ValidarAsync(string idToken, CancellationToken ct)
    {
        var p = idToken.Split('|');
        return Task.FromResult(p.Length is 4 or 5 && bool.TryParse(p[2], out var v)
            ? new IdentidadGoogle(ValidadorTokenGoogle.EmisorGoogle, p[0], p[1], v, p[3], p.Length==5 ? p[4] : null)
            : null);
    }
}

/// <summary>Sustituto de Firebase: guarda los avisos que se habrían enviado.</summary>
public sealed class EmisorFicticio : Manifiestos.Api.Avisos.IEmisorPush
{
    public System.Collections.Concurrent.ConcurrentQueue<(string Token, Manifiestos.Api.Avisos.AvisoPush Aviso)> Enviados { get; } = new();
    /// <summary>Tokens que «FCM» dice que ya no existen.</summary>
    public HashSet<string> Muertos { get; } = [];
    public Task<IReadOnlyList<string>> EnviarAsync(IReadOnlyList<string> tokens, Manifiestos.Api.Avisos.AvisoPush aviso, CancellationToken ct)
    {
        foreach (var t in tokens) Enviados.Enqueue((t, aviso));
        return Task.FromResult<IReadOnlyList<string>>(tokens.Where(Muertos.Contains).ToList());
    }
}

/// <summary>Sustituto de Drive: no sale a internet. Inactivo para que el proceso de fondo no corra solo; las pruebas lo invocan a mano.</summary>
public sealed class DriveFicticio : Manifiestos.Api.Archivos.IAlmacenDrive
{
    public bool Activo => false;
    public bool Fallar { get; set; }
    public List<(Guid Archivo, string Ruta)> Subidos { get; } = [];
    /// <summary>Contenido que Drive «tiene» por ID, para probar la lectura cuando el VPS ya no conserva la copia.</summary>
    public Dictionary<string, byte[]> Contenidos { get; } = [];
    public Task<byte[]?> LeerAsync(string driveId, CancellationToken ct) => Task.FromResult(Contenidos.TryGetValue(driveId, out var b) ? b : null);
    public Task<string> SubirAsync(Manifiestos.Api.Archivos.ArchivoParaDrive archivo, string rutaLocal, CancellationToken ct)
    {
        if (Fallar) throw new HttpRequestException("Drive caído.");
        if (!File.Exists(rutaLocal)) throw new FileNotFoundException();
        Subidos.Add((archivo.ArchivoId, rutaLocal));
        return Task.FromResult("drive-" + archivo.ArchivoId.ToString("N"));
    }
}

public sealed class ApiPrueba(BaseDatosPrueba bd) : WebApplicationFactory<Program>
{
    public EmisorFicticio Emisor { get; } = new();
    public DriveFicticio Drive { get; } = new();

    /// <summary>Organización que atiende la API de prueba (la de BaseDatosPrueba salvo que la prueba siembre otra).</summary>
    public Guid Organizacion { get; init; } = BaseDatosPrueba.Organizacion;

    /// <summary>Almacén temporal de archivos de evidencia de esta API de prueba (se elimina al terminar).</summary>
    public string RutaArchivos { get; } = Path.Combine(Path.GetTempPath(), "manifiestos-pruebas-" + Guid.NewGuid().ToString("N"));

    /// <summary>Llave de firma de los QR de validación (fuera de RutaArchivos: ahí no debe haber nada que no sea evidencia).</summary>
    public string RutaLlaveValidacion { get; } = Path.Combine(Path.GetTempPath(), "llave-validacion-pruebas-" + Guid.NewGuid().ToString("N") + ".pem");

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing) try { if (Directory.Exists(RutaArchivos)) Directory.Delete(RutaArchivos, true); if (File.Exists(RutaLlaveValidacion)) File.Delete(RutaLlaveValidacion); } catch (IOException) { }
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("Archivos:Ruta", RutaArchivos);
        builder.UseSetting("Validacion:RutaLlave", RutaLlaveValidacion);
        // "Testing" evita cargar User Secrets del desarrollador.
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:Manifiestos", bd.CadenaConexion);
        // Fija también Manifiestos:BaseDatos: si el shell tuviera Manifiestos__BaseDatos (p. ej. la demo),
        // CadenaConexion sustituiría el nombre de la base y la API de prueba escribiría fuera de la temporal.
        builder.UseSetting("Manifiestos:BaseDatos", bd.Nombre);
        builder.UseSetting("Acceso:GoogleClientId", "cliente-ficticio.apps.googleusercontent.com");
        builder.UseSetting("Acceso:OrganizacionId", Organizacion.ToString());
        builder.UseSetting("Acceso:IntentosPorMinuto", "1000");
        builder.ConfigureTestServices(s => { s.AddSingleton<IValidadorTokenGoogle, ValidadorFicticio>(); s.AddSingleton<Manifiestos.Api.Avisos.IEmisorPush>(Emisor); s.AddSingleton<Manifiestos.Api.Archivos.IAlmacenDrive>(Drive); });
    }

    /// <summary>Navegador ficticio con su propio almacén de cookies, sobre https (cookies Secure).</summary>
    public HttpClient NuevoNavegador() =>
        CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false });
}

public static class ExtensionesNavegador
{
    public static async Task<string> CsrfAsync(this HttpClient c)
    {
        var r = await c.GetFromJsonAsync<RespuestaCsrf>("/api/auth/csrf");
        return r!.Token;
    }

    public static async Task<HttpResponseMessage> EntrarAsync(this HttpClient c, string token, bool conCsrf = true)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/auth/google") { Content = JsonContent.Create(new { credential = token }) };
        if (conCsrf) req.Headers.Add(ServiciosAcceso.CabeceraCsrf, await c.CsrfAsync());
        return await c.SendAsync(req);
    }

    public static async Task<HttpResponseMessage> SalirAsync(this HttpClient c, bool conCsrf = true)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/auth/logout");
        if (conCsrf) req.Headers.Add(ServiciosAcceso.CabeceraCsrf, await c.CsrfAsync());
        return await c.SendAsync(req);
    }
}
