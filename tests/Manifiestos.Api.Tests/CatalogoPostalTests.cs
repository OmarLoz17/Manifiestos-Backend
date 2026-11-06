using System.IO.Compression;
using System.Net;
using System.Text;
using Manifiestos.Api.Geografia;
using Microsoft.Extensions.Caching.Memory;

namespace Manifiestos.Api.Tests;

public sealed class CatalogoPostalTests
{
    private const string Datos="""
        Nota de fuente de prueba ficticia
        d_codigo|d_asenta|d_tipo_asenta|D_mnpio|d_estado|d_ciudad|d_CP|c_estado|c_oficina|c_CP|c_tipo_asenta|c_mnpio|id_asenta_cpcons|d_zona|c_cve_ciudad
        01000|Colonia Ficticia|Colonia|Municipio Ficticio|Estado Ficticio||01001|09||||010|||01
        64000|Centro Ficticio|Colonia|Monterrey|Nuevo León||64001|19||||039|||01
        64000|Otra Colonia Ficticia|Colonia|Monterrey|Nuevo León||64001|19||||039|||01
        """;

    private sealed class Proveedor:HttpMessageHandler,IHttpClientFactory
    {
        public int Llamadas {get;private set;}
        public HttpClient CreateClient(string name)=>new(this,disposeHandler:false);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage r,CancellationToken ct)
        {
            Llamadas++;
            if(r.Method==HttpMethod.Get)return new(HttpStatusCode.OK){Content=new StringContent("<input type=\"hidden\" name=\"__VIEWSTATE\" value=\"a&amp;b\" />")};
            var formulario=await r.Content!.ReadAsStringAsync(ct);
            Assert.Contains("rblTipo=txt",formulario);Assert.Contains("cboEdo=00",formulario);Assert.Contains("a%26b",formulario);
            using var memoria=new MemoryStream();
            using(var zip=new ZipArchive(memoria,ZipArchiveMode.Create,leaveOpen:true))
            using(var escritor=new StreamWriter(zip.CreateEntry("CPdescarga.txt").Open(),Encoding.Latin1))await escritor.WriteAsync(Datos);
            return new(HttpStatusCode.OK){Content=new ByteArrayContent(memoria.ToArray())};
        }
    }

    [Fact]
    public async Task Conserva_ceros_acentos_y_todas_las_colonias_y_no_descarga_por_cada_cp()
    {
        using var proveedor=new Proveedor();using var cache=new MemoryCache(new MemoryCacheOptions());
        var catalogo=new CatalogoPostal(proveedor,cache);
        var primero=Assert.Single(await catalogo.ConsultarAsync("01000",CancellationToken.None));
        Assert.Equal("09",primero.EstadoId);Assert.Equal("010",primero.MunicipioId);
        var opciones=await catalogo.ConsultarAsync("64000",CancellationToken.None);
        Assert.Equal(2,opciones.Length);Assert.All(opciones,o=>{Assert.Equal("Nuevo León",o.Estado);Assert.Equal("039",o.MunicipioId);});
        Assert.Empty(await catalogo.ConsultarAsync("00000",CancellationToken.None));
        Assert.Equal(2,proveedor.Llamadas);
    }

    [Theory]
    [InlineData("6400")][InlineData("123456")][InlineData("64x00")][InlineData("../00")]
    public async Task Solo_admite_cinco_digitos_sin_consultar_al_proveedor(string cp)
    {
        using var proveedor=new Proveedor();using var cache=new MemoryCache(new MemoryCacheOptions());
        await Assert.ThrowsAsync<ArgumentException>(()=>new CatalogoPostal(proveedor,cache).ConsultarAsync(cp,CancellationToken.None));
        Assert.Equal(0,proveedor.Llamadas);
    }
}
