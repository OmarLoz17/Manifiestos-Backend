using System.Net;
using System.Text;
using Manifiestos.Api.Geografia;
using Microsoft.Extensions.Caching.Memory;

namespace Manifiestos.Api.Tests;

public sealed class CatalogoInegiTests
{
    private sealed class Servidor(string cuerpo, HttpStatusCode estado = HttpStatusCode.OK) : HttpMessageHandler
    {
        public int Llamadas { get; private set; }
        public Uri? Uri { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Llamadas++;
            Uri = request.RequestUri;
            return Task.FromResult(new HttpResponseMessage(estado) { Content = new StringContent(cuerpo, Encoding.UTF8, "application/json") });
        }
    }

    [Theory]
    [InlineData("estados", "", "", "mgee")]
    [InlineData("municipios", "19", "", "mgem/19")]
    [InlineData("colonias", "19", "039", "asentamientos/19/039")]
    [InlineData("calles", "19", "039", "vialidades/19/039")]
    public void Usa_solo_rutas_conocidas(string catalogo, string estado, string municipio, string ruta) =>
        Assert.Equal(ruta, CatalogoInegi.Ruta(catalogo, estado, municipio));

    [Theory]
    [InlineData("municipios", "../", "")]
    [InlineData("calles", "33", "039")]
    [InlineData("colonias", "19", "000")]
    [InlineData("https://example.invalid", "19", "039")]
    public void No_admite_destinos_o_claves_arbitrarios(string catalogo, string estado, string municipio) =>
        Assert.Throws<ArgumentException>(() => CatalogoInegi.Ruta(catalogo, estado, municipio));

    [Fact]
    public async Task Normaliza_deduplica_y_reutiliza_el_catalogo_en_memoria()
    {
        using var servidor = new Servidor("""{"datos":[{"nom_asen":"Zona Ficticia"},{"nom_asen":"Centro Ficticio"},{"nom_asen":"Centro Ficticio"}]}""");
        using var http = new HttpClient(servidor) { BaseAddress = new Uri("https://gaia.inegi.org.mx/wscatgeo/v2/") };
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var catalogo = new CatalogoInegi(http, cache);
        var opciones = await catalogo.ConsultarAsync("colonias", "19", "039", CancellationToken.None);
        Assert.Equal(["Centro Ficticio", "Zona Ficticia"], opciones.Select(o => o.Nombre));
        Assert.Equal(opciones, await catalogo.ConsultarAsync("colonias", "19", "039", CancellationToken.None));
        Assert.Equal(1, servidor.Llamadas);
        Assert.Equal("/wscatgeo/v2/asentamientos/19/039", servidor.Uri!.AbsolutePath);
    }

    [Fact]
    public async Task Conserva_el_tipo_de_vialidad_para_distinguir_nombres_iguales()
    {
        using var servidor = new Servidor("""{"datos":[{"nomvial":"Ficticia","tipovial":"CALLE"},{"nomvial":"Ficticia","tipovial":"AVENIDA"}]}""");
        using var http = new HttpClient(servidor) { BaseAddress = new Uri("https://gaia.inegi.org.mx/wscatgeo/v2/") };
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var opciones = await new CatalogoInegi(http, cache).ConsultarAsync("calles", "19", "039", CancellationToken.None);
        Assert.Equal(["AVENIDA Ficticia", "CALLE Ficticia"], opciones.Select(o => o.Nombre));
    }

    [Fact]
    public async Task Un_error_del_proveedor_no_se_guarda_como_catalogo_vacio()
    {
        using var servidor = new Servidor("", HttpStatusCode.ServiceUnavailable);
        using var http = new HttpClient(servidor) { BaseAddress = new Uri("https://gaia.inegi.org.mx/wscatgeo/v2/") };
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var catalogo = new CatalogoInegi(http, cache);
        for (var i = 0; i < 2; i++)
            await Assert.ThrowsAsync<HttpRequestException>(() => catalogo.ConsultarAsync("estados", null, null, CancellationToken.None));
        Assert.Equal(2, servidor.Llamadas);
    }
}
