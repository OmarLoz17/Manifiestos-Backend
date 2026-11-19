using System.Net;
using System.Net.Http.Json;
using Manifiestos.Api.Acceso;
using Manifiestos.Api.Rutas;
using Manifiestos.Api.Sitios;
using Manifiestos.Api.Tests.Infraestructura;

namespace Manifiestos.Api.Tests;

/// <summary>
/// Plantas tratadoras (pantalla «Sitios y plantas», migración 0033): sitios de descarga, sin punto de servicio. Nunca aparecen entre los
/// sitios ni son parada. Se dan de baja si no tienen descargas. Datos ficticios.
/// </summary>
[Trait("Categoria", "Integracion")]
public sealed class PlantasTests : IClassFixture<BaseDatosPrueba>, IAsyncLifetime
{
    private static readonly Guid Suc = BaseDatosPrueba.Sucursal;

    private readonly BaseDatosPrueba _bd;
    private ApiPrueba _api = null!;

    public PlantasTests(BaseDatosPrueba bd) => _bd = bd;
    public Task InitializeAsync() { _api = new ApiPrueba(_bd); return Task.CompletedTask; }
    public async Task DisposeAsync() => await _api.DisposeAsync();

    private async Task<HttpClient> SesionAsync(bool escribir = true, bool admin = true)
    {
        var correo = $"plantas-{Guid.NewGuid():N}@example.invalid";
        await _bd.AutorizarAsync(correo, rol: admin ? "administrador" : "operaciones", escribir: escribir);
        var nav = _api.NuevoNavegador();
        Assert.Equal(HttpStatusCode.NoContent, (await nav.EntrarAsync(ValidadorFicticio.Token($"s-{correo}", correo))).StatusCode);
        return nav;
    }

    private static async Task<HttpResponseMessage> EnviarAsync(HttpClient nav, HttpMethod metodo, string url, object? cuerpo = null)
    {
        var req = new HttpRequestMessage(metodo, url) { Content = cuerpo is null ? null : JsonContent.Create(cuerpo) };
        req.Headers.Add(ServiciosAcceso.CabeceraCsrf, await nav.CsrfAsync());
        return await nav.SendAsync(req);
    }

    private static object Cuerpo(string nombre, decimal? lat = null, decimal? lon = null, int? revision = null) =>
        new { sucursalId = Suc, revision, nombre, calle = "Calle Ficticia", municipio = "Municipio Ficticio", latitud = lat, longitud = lon };

    private static async Task<PlantaResumen?> BuscarAsync(HttpClient nav, Guid id) =>
        (await nav.GetFromJsonAsync<List<PlantaResumen>>("/api/plantas"))!.SingleOrDefault(p => p.Id == id);

    [Fact]
    public async Task Crea_edita_con_revision_y_da_de_baja_una_planta_sin_descargas_sin_mezclarse_con_los_sitios()
    {
        using var nav = await SesionAsync();
        var nombre = $"Planta {Guid.NewGuid():N}"[..14];
        var r = await EnviarAsync(nav, HttpMethod.Post, "/api/plantas", Cuerpo(nombre));   // sin ubicación: es opcional en una planta
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        var id = (await r.Content.ReadFromJsonAsync<RevisionGuardada>())!.Id;
        var p = (await BuscarAsync(nav, id))!;
        Assert.Equal((nombre, 1, null, 0), (p.Nombre, p.Revision, p.Latitud, p.Descargas));
        Assert.Empty(p.Recientes);
        // No es un sitio de servicio: no sale en Sitios ni puede ser parada.
        Assert.DoesNotContain((await nav.GetFromJsonAsync<List<SitioResumen>>("/api/sitios"))!, s => s.Id == id);
        Assert.Equal("0", await _bd.ConsultarAsync($"SELECT count(*) FROM negocio.punto_servicio WHERE sitio_id='{id}'"));

        var editada = await EnviarAsync(nav, HttpMethod.Put, $"/api/plantas/{id}", Cuerpo(nombre + " B", 25.6866m, -100.3161m, revision: 1));
        Assert.Equal(2, (await editada.Content.ReadFromJsonAsync<RevisionGuardada>())!.Revision);
        var tras = (await BuscarAsync(nav, id))!;
        Assert.Equal((nombre + " B", 2, 25.6866m), (tras.Nombre, tras.Revision, tras.Latitud));
        Assert.Equal(HttpStatusCode.Conflict, (await EnviarAsync(nav, HttpMethod.Put, $"/api/plantas/{id}", Cuerpo(nombre, revision: 1))).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await EnviarAsync(nav, HttpMethod.Delete, $"/api/plantas/{id}")).StatusCode);
        Assert.Null(await BuscarAsync(nav, id));
        Assert.Equal("1", await _bd.ConsultarAsync($"SELECT count(*) FROM negocio.sitio_baja WHERE sitio_id='{id}'"));
        Assert.Equal(HttpStatusCode.NotFound, (await EnviarAsync(nav, HttpMethod.Delete, $"/api/plantas/{id}")).StatusCode);
    }

    [Fact]
    public async Task Solo_el_administrador_elimina_plantas_y_sitios()
    {
        using var admin = await SesionAsync();
        var r = await EnviarAsync(admin, HttpMethod.Post, "/api/plantas", Cuerpo($"Planta {Guid.NewGuid():N}"[..14]));
        var id = (await r.Content.ReadFromJsonAsync<RevisionGuardada>())!.Id;
        using var operaciones = await SesionAsync(admin: false);   // puede leer y escribir, pero no es administrador
        Assert.Equal(HttpStatusCode.Forbidden, (await EnviarAsync(operaciones, HttpMethod.Delete, $"/api/plantas/{id}")).StatusCode);
        Assert.NotNull(await BuscarAsync(operaciones, id));
        var sitio = await EnviarAsync(admin, HttpMethod.Post, "/api/sitios", new { sucursalId = Suc, nombre = "Sitio ficticio", latitud = 25.6m, longitud = -100.3m });
        var sitioId = (await sitio.Content.ReadFromJsonAsync<RevisionGuardada>())!.Id;
        Assert.Equal(HttpStatusCode.Forbidden, (await EnviarAsync(operaciones, HttpMethod.Delete, $"/api/sitios/{sitioId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await EnviarAsync(admin, HttpMethod.Delete, $"/api/sitios/{sitioId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await EnviarAsync(admin, HttpMethod.Delete, $"/api/plantas/{id}")).StatusCode);
    }

    [Fact]
    public async Task Valida_los_datos_y_exige_permiso_de_escritura()
    {
        using var nav = await SesionAsync();
        Assert.Equal(HttpStatusCode.BadRequest, (await EnviarAsync(nav, HttpMethod.Post, "/api/plantas", Cuerpo("  "))).StatusCode);                 // sin nombre
        Assert.Equal(HttpStatusCode.BadRequest, (await EnviarAsync(nav, HttpMethod.Post, "/api/plantas", Cuerpo("Planta X", lat: 25m))).StatusCode);  // media ubicación
        Assert.Equal(HttpStatusCode.BadRequest, (await EnviarAsync(nav, HttpMethod.Post, "/api/plantas", Cuerpo("Planta X", 95m, -100m))).StatusCode);
        using var lector = await SesionAsync(escribir: false);
        Assert.Equal(HttpStatusCode.Forbidden, (await EnviarAsync(lector, HttpMethod.Post, "/api/plantas", Cuerpo("Planta lectora"))).StatusCode);
        using var anonimo = _api.NuevoNavegador();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonimo.GetAsync("/api/plantas")).StatusCode);
    }
}
