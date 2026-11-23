using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Manifiestos.Api.Acceso;
using Manifiestos.Api.Tests.Infraestructura;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Manifiestos.Api.Tests;

/// <summary>
/// Actualizaciones de la app desde el servidor (decisión 0041): solo se ofrece un APK que existe y cuya huella SHA-256 coincide con version.json;
/// cualquier archivo a medio subir, JSON inválido o huella distinta equivale a «no hay actualización». Datos ficticios.
/// </summary>
[Trait("Categoria", "Integracion")]
public sealed class ActualizacionesAppTests(BaseDatosPrueba bd) : IClassFixture<BaseDatosPrueba>, IAsyncLifetime
{
    private ApiPrueba _api = null!;
    private readonly List<string> _carpetas = [];
    public Task InitializeAsync() { _api = new ApiPrueba(bd); return Task.CompletedTask; }
    public async Task DisposeAsync()
    {
        await _api.DisposeAsync();
        foreach (var c in _carpetas) try { Directory.Delete(c, true); } catch (IOException) { }
    }

    private string NuevaCarpeta()
    {
        var c = Path.Combine(Path.GetTempPath(), "actualizaciones-pruebas-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(c); _carpetas.Add(c); return c;
    }

    private HttpClient Cliente(string carpeta) =>
        _api.WithWebHostBuilder(b => b.UseSetting("Actualizaciones:Ruta", carpeta))
            .CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false });

    private static string Huella(byte[] b) => Convert.ToHexStringLower(SHA256.HashData(b));

    private static void Publicar(string carpeta, byte[] apk, object version)
    {
        File.WriteAllBytes(Path.Combine(carpeta, AlmacenActualizaciones.NombreApk), apk);
        File.WriteAllText(Path.Combine(carpeta, AlmacenActualizaciones.NombreVersion), JsonSerializer.Serialize(version));
    }

    private static byte[] ApkFalso(int n) => [.. System.Text.Encoding.ASCII.GetBytes("PK-apk-ficticio-"), .. Enumerable.Range(0, 5000 + n).Select(i => (byte)(i * 31 + n))];

    [Fact]
    public async Task Sin_archivos_no_hay_actualizacion()
    {
        var c = Cliente(NuevaCarpeta());
        Assert.Equal(HttpStatusCode.NoContent, (await c.GetAsync("/api/movil/app/version")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync("/api/movil/app/apk")).StatusCode);
    }

    [Fact]
    public async Task Version_valida_se_ofrece_sin_sesion_y_el_apk_se_descarga_integro()
    {
        var carpeta = NuevaCarpeta(); var apk = ApkFalso(1);
        Publicar(carpeta, apk, new { versionCode = 5, versionName = "0.5.0-demo", sha256 = Huella(apk), obligatoria = true, notas = "Corrección de avisos." });
        var c = Cliente(carpeta);

        var v = await c.GetAsync("/api/movil/app/version");
        Assert.Equal(HttpStatusCode.OK, v.StatusCode);
        Assert.Contains("no-store", v.Headers.CacheControl!.ToString());
        var oferta = (await v.Content.ReadFromJsonAsync<OfertaApp>())!;
        Assert.Equal(5, oferta.VersionCode); Assert.Equal("0.5.0-demo", oferta.VersionName); Assert.True(oferta.Obligatoria);
        Assert.Equal("Corrección de avisos.", oferta.Notas); Assert.Equal(apk.Length, oferta.Tamano); Assert.Equal(Huella(apk), oferta.Sha256);

        var d = await c.GetAsync("/api/movil/app/apk");
        Assert.Equal(HttpStatusCode.OK, d.StatusCode);
        Assert.Equal(AlmacenActualizaciones.TipoApk, d.Content.Headers.ContentType!.MediaType);
        Assert.Equal(apk, await d.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task Si_la_huella_no_coincide_o_el_json_es_invalido_no_se_ofrece_ni_se_descarga()
    {
        var apk = ApkFalso(2);
        var casos = new (string Nombre, object Version)[]
        {
            ("huella distinta", new { versionCode = 2, versionName = "x", sha256 = Huella(ApkFalso(3)) }),
            ("huella mal formada", new { versionCode = 2, versionName = "x", sha256 = "abc" }),
            ("sin versionCode", new { versionName = "x", sha256 = Huella(apk) }),
            ("versionCode cero", new { versionCode = 0, versionName = "x", sha256 = Huella(apk) }),
            ("sin nombre", new { versionCode = 2, sha256 = Huella(apk) }),
        };
        foreach (var (nombre, version) in casos)
        {
            var carpeta = NuevaCarpeta(); Publicar(carpeta, apk, version); var c = Cliente(carpeta);
            Assert.True((await c.GetAsync("/api/movil/app/version")).StatusCode == HttpStatusCode.NoContent, nombre);
            Assert.True((await c.GetAsync("/api/movil/app/apk")).StatusCode == HttpStatusCode.NotFound, nombre);
        }
        var rota = NuevaCarpeta(); Publicar(rota, apk, new { versionCode = 2, versionName = "x", sha256 = Huella(apk) });
        File.WriteAllText(Path.Combine(rota, AlmacenActualizaciones.NombreVersion), "{ esto no es json");
        Assert.Equal(HttpStatusCode.NoContent, (await Cliente(rota).GetAsync("/api/movil/app/version")).StatusCode);
        // Solo el JSON (el APK aún se está subiendo): tampoco se ofrece.
        var sinApk = NuevaCarpeta(); File.WriteAllText(Path.Combine(sinApk, AlmacenActualizaciones.NombreVersion), JsonSerializer.Serialize(new { versionCode = 2, versionName = "x", sha256 = Huella(apk) }));
        Assert.Equal(HttpStatusCode.NoContent, (await Cliente(sinApk).GetAsync("/api/movil/app/version")).StatusCode);
    }

    [Fact]
    public async Task Al_publicar_una_version_nueva_se_vuelve_a_comprobar_la_huella()
    {
        var carpeta = NuevaCarpeta(); var c = Cliente(carpeta);
        var a = ApkFalso(4); Publicar(carpeta, a, new { versionCode = 3, versionName = "0.3.0-demo", sha256 = Huella(a) });
        Assert.Equal(3, (await (await c.GetAsync("/api/movil/app/version")).Content.ReadFromJsonAsync<OfertaApp>())!.VersionCode);
        var b = ApkFalso(9); Publicar(carpeta, b, new { versionCode = 4, versionName = "0.4.0-demo", sha256 = Huella(b) });
        File.SetLastWriteTimeUtc(Path.Combine(carpeta, AlmacenActualizaciones.NombreApk), DateTime.UtcNow.AddMinutes(1));
        var oferta = (await (await c.GetAsync("/api/movil/app/version")).Content.ReadFromJsonAsync<OfertaApp>())!;
        Assert.Equal(4, oferta.VersionCode); Assert.Equal(b.Length, oferta.Tamano);
        Assert.Equal(b, await (await c.GetAsync("/api/movil/app/apk")).Content.ReadAsByteArrayAsync());
        // Se sube un APK distinto sin cambiar version.json: la huella declarada ya no coincide y deja de ofrecerse.
        File.WriteAllBytes(Path.Combine(carpeta, AlmacenActualizaciones.NombreApk), ApkFalso(11));
        Assert.Equal(HttpStatusCode.NoContent, (await c.GetAsync("/api/movil/app/version")).StatusCode);
    }
}
