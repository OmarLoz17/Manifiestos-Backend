using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Manifiestos.Api.Acceso;
using Manifiestos.Api.Tests.Infraestructura;

namespace Manifiestos.Api.Tests;

/// <summary>
/// Reglas de composición de una parada, iguales en TODOS los contratos que siguen accesibles (revisión H03, H04 y H05):
/// un cliente autorizado va solo en fosas y no va en sanitarios; los generales pueden ser varios. Una extraordinaria puede repetir un
/// sitio (con aviso a Operaciones) y un reintento con la misma clave no duplica la parada. Datos ficticios.
/// </summary>
[Trait("Categoria", "Integracion")]
public sealed class ReglasParadaTests : IClassFixture<BaseDatosPrueba>, IAsyncLifetime
{
    private static readonly Guid Org = BaseDatosPrueba.Organizacion;
    private static readonly Guid Suc = BaseDatosPrueba.Sucursal;
    private static readonly string Fecha = Fechas.Lunes;   // lunes: las rutas de la prueba salen todos los días

    private readonly BaseDatosPrueba _bd;
    private ApiPrueba _api = null!;

    public ReglasParadaTests(BaseDatosPrueba bd) => _bd = bd;
    public Task InitializeAsync() { _api = new ApiPrueba(_bd); return Task.CompletedTask; }
    public async Task DisposeAsync() => await _api.DisposeAsync();

    private sealed record Mundo(Guid Fosas, Guid Sanitarios, Guid Autorizado, Guid General1, Guid General2, Guid SitioA, Guid SitioB);

    private async Task<HttpClient> SesionAsync()
    {
        var correo = $"reglas-{Guid.NewGuid():N}@example.invalid";
        await _bd.AutorizarAsync(correo, escribir: true);
        var nav = _api.NuevoNavegador();
        Assert.Equal(HttpStatusCode.NoContent, (await nav.EntrarAsync(ValidadorFicticio.Token($"s-{correo}", correo))).StatusCode);
        return nav;
    }

    private static async Task<HttpResponseMessage> EnviarAsync(HttpClient nav, HttpMethod metodo, string url, object cuerpo)
    {
        var req = new HttpRequestMessage(metodo, url) { Content = JsonContent.Create(cuerpo) };
        req.Headers.Add(ServiciosAcceso.CabeceraCsrf, await nav.CsrfAsync());
        return await nav.SendAsync(req);
    }

    private async Task<Mundo> SembrarAsync()
    {
        var (uf, us, rf, rs, ea, e1, e2, ca, c1, c2, sa, sb) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        string Cliente(Guid empresa, Guid cliente, string nombre) => $"""
            INSERT INTO negocio.empresa VALUES ('{Org}', '{empresa}', 'CLI-{empresa:N}');
            INSERT INTO negocio.ficha_empresa(organizacion_id, empresa_id, revision, nombre_comercial) VALUES ('{Org}', '{empresa}', 1, '{nombre}');
            INSERT INTO negocio.empresa_sucursal VALUES ('{Org}', '{Suc}', '{empresa}');
            INSERT INTO negocio.cliente VALUES ('{Org}', '{Suc}', '{cliente}', '{empresa}', 'R-{cliente:N}');
            """;
        string Sitio(Guid s, string nombre) => $"""
            INSERT INTO negocio.sitio VALUES ('{Org}', '{Suc}', '{s}', '{nombre}');
            INSERT INTO negocio.ficha_sitio(organizacion_id, sucursal_id, sitio_id, revision, nombre, calle, municipio) VALUES ('{Org}', '{Suc}', '{s}', 1, '{nombre}', 'Calle Ficticia 1', 'Municipio Ficticio');
            INSERT INTO negocio.punto_servicio VALUES ('{Org}', '{Suc}', '{s}');
            """;
        await _bd.SqlComoDuenoAsync($"""
            BEGIN;
            INSERT INTO negocio.vehiculo VALUES ('{Org}', '{Suc}', '{uf}', 'RF-{uf.ToString("N")[..5]}'), ('{Org}', '{Suc}', '{us}', 'RS-{us.ToString("N")[..5]}');
            INSERT INTO negocio.ficha_vehiculo(organizacion_id, sucursal_id, vehiculo_id, revision, capacidad_id, clasificacion) VALUES
                ('{Org}', '{Suc}', '{uf}', 1, 2, 'fosas'), ('{Org}', '{Suc}', '{us}', 1, 4, 'sanitarios');
            INSERT INTO negocio.ruta(organizacion_id, sucursal_id, id) VALUES ('{Org}', '{Suc}', '{rf}'), ('{Org}', '{Suc}', '{rs}');
            INSERT INTO negocio.ruta_version(organizacion_id, sucursal_id, ruta_id, revision, nombre, dias, vehiculo_id) VALUES
                ('{Org}', '{Suc}', '{rf}', 1, 'Ruta Fosas Reglas', 127, '{uf}'), ('{Org}', '{Suc}', '{rs}', 1, 'Ruta Sanitarios Reglas', 127, '{us}');
            {Cliente(ea, ca, "Cliente Autorizado Reglas")}
            INSERT INTO negocio.autorizacion_empresa(organizacion_id, empresa_id, autoridad, finalidad, numero) VALUES ('{Org}', '{ea}', 'Medio ambiente', 'Generador', 'AMB-REGLAS');
            {Cliente(e1, c1, "Cliente General Uno")}
            {Cliente(e2, c2, "Cliente General Dos")}
            {Sitio(sa, "Sitio Reglas A")}
            {Sitio(sb, "Sitio Reglas B")}
            COMMIT;
            """);
        return new Mundo(rf, rs, ca, c1, c2, sa, sb);
    }

    private async Task<int> ParadasAsync(Guid ruta) => int.Parse(await _bd.ConsultarAsync($"SELECT count(*) FROM negocio.parada WHERE ruta_id = '{ruta}'"));

    [Fact]
    public async Task Parada_completa_en_fosas_no_mezcla_un_autorizado_con_otros_clientes_ni_en_POST_ni_en_PUT()
    {
        var m = await SembrarAsync();
        using var nav = await SesionAsync();
        // POST: un autorizado con un general se rechaza y no deja nada escrito.
        var mezcla = await EnviarAsync(nav, HttpMethod.Post, $"/api/rutas/{m.Fosas}/paradas", new { sitioId = m.SitioA, clientes = new object[] { new { clienteId = m.Autorizado, fosas = 1 }, new { clienteId = m.General1, fosas = 1 } } });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, mezcla.StatusCode);
        Assert.Equal(0, await ParadasAsync(m.Fosas));
        // Dos autorizados juntos tampoco: se comprueba con el mismo autorizado repetido como cliente distinto no aplica; basta el caso autorizado + general.
        // Varios generales sí; un autorizado solo (en un sitio normal) también.
        var generales = await EnviarAsync(nav, HttpMethod.Post, $"/api/rutas/{m.Fosas}/paradas", new { sitioId = m.SitioA, clientes = new object[] { new { clienteId = m.General1, fosas = 1 }, new { clienteId = m.General2, fosas = 2 } } });
        Assert.Equal(HttpStatusCode.Created, generales.StatusCode);
        var solo = await EnviarAsync(nav, HttpMethod.Post, $"/api/rutas/{m.Fosas}/paradas", new { sitioId = m.SitioB, clientes = new object[] { new { clienteId = m.Autorizado, fosas = 1 } } });
        Assert.Equal(HttpStatusCode.Created, solo.StatusCode);
        Assert.Equal(2, await ParadasAsync(m.Fosas));
        // PUT: convertir la parada de generales en autorizado + general se rechaza y no crea revisión.
        var parada = (await generales.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        var revision = await _bd.ConsultarAsync($"SELECT max(revision) FROM negocio.parada_version WHERE parada_id = '{parada}'");
        var put = await EnviarAsync(nav, HttpMethod.Put, $"/api/rutas/{m.Fosas}/paradas/{parada}",
            new { sitioId = m.SitioA, revision = int.Parse(revision), clientes = new object[] { new { clienteId = m.Autorizado, fosas = 1 }, new { clienteId = m.General1, fosas = 1 } } });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, put.StatusCode);
        Assert.Equal(revision, await _bd.ConsultarAsync($"SELECT max(revision) FROM negocio.parada_version WHERE parada_id = '{parada}'"));
    }

    [Fact]
    public async Task Extraordinarias_aplican_las_mismas_reglas_de_clientes_que_el_plan()
    {
        var m = await SembrarAsync();
        using var nav = await SesionAsync();
        Task<HttpResponseMessage> Extra(Guid ruta, object[] clientes, Guid? clave = null) =>
            EnviarAsync(nav, HttpMethod.Post, "/api/ordenes/extraordinarias", new { rutaId = ruta, fecha = Fecha, sitioId = m.SitioA, clientes, claveSolicitud = clave });
        // Sanitarios: un autorizado se rechaza aunque se pidan baños y el sitio sea normal; un general sí.
        Assert.Equal(HttpStatusCode.BadRequest, (await Extra(m.Sanitarios, [new { clienteId = m.Autorizado, banos = 5 }])).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await Extra(m.Sanitarios, [new { clienteId = m.General1, banos = 5 }])).StatusCode);
        // Fosas: autorizado + general se rechaza; varios generales y un autorizado solo, sí.
        Assert.Equal(HttpStatusCode.BadRequest, (await Extra(m.Fosas, [new { clienteId = m.Autorizado, fosas = 1 }, new { clienteId = m.General1, fosas = 1 }])).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await Extra(m.Fosas, [new { clienteId = m.General1, fosas = 1 }, new { clienteId = m.General2, fosas = 1 }])).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await Extra(m.Fosas, [new { clienteId = m.Autorizado, fosas = 1 }])).StatusCode);
    }

    [Fact]
    public async Task Extraordinaria_puede_repetir_un_sitio_con_aviso_y_un_reintento_con_la_misma_clave_no_duplica()
    {
        var m = await SembrarAsync();
        using var nav = await SesionAsync();
        async Task<JsonElement> Extra(Guid sitio, Guid? clave, int esperado = 201)
        {
            var r = await EnviarAsync(nav, HttpMethod.Post, "/api/ordenes/extraordinarias",
                new { rutaId = m.Fosas, fecha = Fecha, sitioId = sitio, clientes = new object[] { new { clienteId = m.General1, fosas = 1 } }, claveSolicitud = clave });
            Assert.Equal((HttpStatusCode)esperado, r.StatusCode);
            return await r.Content.ReadFromJsonAsync<JsonElement>();
        }
        // Primera vez en la ruta: sin aviso.
        var clave1 = Guid.NewGuid();
        var primera = await Extra(m.SitioB, clave1);
        Assert.Equal(JsonValueKind.Null, primera.GetProperty("aviso").ValueKind);
        // Reintento de la misma solicitud (respuesta perdida): misma parada, no se duplica.
        var reintento = await Extra(m.SitioB, clave1);
        Assert.Equal(primera.GetProperty("id").GetGuid(), reintento.GetProperty("id").GetGuid());
        Assert.Equal("1", await _bd.ConsultarAsync($"SELECT count(*) FROM negocio.parada_extraordinaria WHERE ruta_id = '{m.Fosas}' AND sitio_id = '{m.SitioB}'"));
        // Otra solicitud para el mismo sitio y día: se permite (solo en extraordinarias) y Operaciones recibe el aviso.
        var repetida = await Extra(m.SitioB, Guid.NewGuid());
        Assert.NotEqual(primera.GetProperty("id").GetGuid(), repetida.GetProperty("id").GetGuid());
        Assert.Equal("Este sitio ya está en la ruta de hoy: la orden se agregó a su parada como extraordinaria.", repetida.GetProperty("aviso").GetString());
        Assert.Equal(primera.GetProperty("id").GetGuid().ToString(), await _bd.ConsultarAsync($"SELECT fusionada_en FROM negocio.parada_extraordinaria WHERE id = '{repetida.GetProperty("id").GetGuid()}'"));
        // Un sitio que ya está en el plan de la ruta también se permite con aviso.
        Assert.Equal(HttpStatusCode.Created, (await EnviarAsync(nav, HttpMethod.Post, $"/api/rutas/{m.Fosas}/paradas", new { sitioId = m.SitioA, clientes = new object[] { new { clienteId = m.General2, fosas = 1 } } })).StatusCode);
        Assert.Equal("Este sitio ya está en la ruta de hoy: la orden se agregó a su parada como extraordinaria.", (await Extra(m.SitioA, null)).GetProperty("aviso").GetString());
    }

    [Fact]
    public async Task Ordenes_por_rango_trae_las_extraordinarias_de_cada_dia_con_su_fecha_para_los_reportes()
    {
        var m = await SembrarAsync();
        using var nav = await SesionAsync();
        foreach (var dia in new[] { Fechas.Lunes, Fechas.Martes })
            Assert.Equal(HttpStatusCode.Created, (await EnviarAsync(nav, HttpMethod.Post, "/api/ordenes/extraordinarias",
                new { rutaId = m.Fosas, fecha = dia, sitioId = m.SitioA, clientes = new object[] { new { clienteId = m.General1, fosas = 1 } } })).StatusCode);
        async Task<string[]> FechasAsync(string url)
        {
            var rutas = JsonDocument.Parse(await nav.GetStringAsync(url)).RootElement;
            return rutas.EnumerateArray().Single(r => r.GetProperty("id").GetGuid() == m.Fosas).GetProperty("extraordinarias").EnumerateArray()
                .Select(x => x.GetProperty("fecha").GetString()!).OrderBy(x => x).ToArray();
        }
        Assert.Equal([Fechas.Lunes, Fechas.Martes], await FechasAsync($"/api/ordenes?desde={Fechas.Lunes}&hasta={Fechas.Martes}"));
        Assert.Equal([Fechas.Martes], await FechasAsync($"/api/ordenes?desde={Fechas.Martes}&hasta={Fechas.Martes}"));
        Assert.Equal([Fechas.Lunes], await FechasAsync($"/api/ordenes?fecha={Fechas.Lunes}"));
        Assert.Equal(HttpStatusCode.BadRequest, (await nav.GetAsync("/api/ordenes?desde=2026-01-01&hasta=2026-12-31")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await nav.GetAsync($"/api/ordenes?desde={Fechas.Martes}&hasta={Fechas.Lunes}")).StatusCode);
    }

    [Fact]
    public async Task Las_opciones_de_sitio_informan_todas_las_rutas_donde_ya_esta_no_solo_la_primera()
    {
        var m = await SembrarAsync();
        var otra = Guid.NewGuid();
        var unidadDeFosas = await _bd.ConsultarAsync($"SELECT vehiculo_id FROM negocio.ruta_version WHERE ruta_id = '{m.Fosas}' AND revision = 1");
        await _bd.SqlComoDuenoAsync($"""
            BEGIN;
            INSERT INTO negocio.ruta(organizacion_id, sucursal_id, id) VALUES ('{Org}', '{Suc}', '{otra}');
            INSERT INTO negocio.ruta_version(organizacion_id, sucursal_id, ruta_id, revision, nombre, dias, vehiculo_id) VALUES ('{Org}', '{Suc}', '{otra}', 1, 'Ruta Fosas Otra', 42, '{unidadDeFosas}');
            COMMIT;
            """);
        using var nav = await SesionAsync();
        foreach (var ruta in new[] { m.Fosas, otra })   // el mismo sitio en dos rutas a la vez está permitido (reagenda o emergencia)
            Assert.Equal(HttpStatusCode.Created, (await EnviarAsync(nav, HttpMethod.Post, $"/api/rutas/{ruta}/paradas", new { sitioId = m.SitioA, clientes = new object[] { new { clienteId = m.General1, fosas = 1 } } })).StatusCode);
        var opciones = JsonDocument.Parse(await nav.GetStringAsync($"/api/rutas/opciones?sucursalId={Suc}&servicio=fosas")).RootElement;
        var sitio = opciones.GetProperty("sitios").EnumerateArray().Single(x => x.GetProperty("id").GetGuid() == m.SitioA);
        Assert.Equal(new[] { m.Fosas, otra }.OrderBy(x => x), sitio.GetProperty("rutas").EnumerateArray().Select(x => x.GetProperty("id").GetGuid()).OrderBy(x => x));
    }
}
