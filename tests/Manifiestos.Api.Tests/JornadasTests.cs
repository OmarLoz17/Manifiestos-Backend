using System.Net;
using System.Net.Http.Json;
using Manifiestos.Api.Acceso;
using Manifiestos.Api.Jornadas;
using Manifiestos.Api.Rutas;
using Manifiestos.Api.Tests.Infraestructura;

namespace Manifiestos.Api.Tests;

/// <summary>Jornadas (0019) contra PostgreSQL real: publicar una ruta por fecha, transiciones, validaciones y permisos. Datos ficticios.</summary>
[Trait("Categoria", "Integracion")]
public sealed class JornadasTests : IClassFixture<BaseDatosPrueba>, IAsyncLifetime
{
    private static readonly Guid Org = BaseDatosPrueba.Organizacion;
    private static readonly Guid Suc = BaseDatosPrueba.Sucursal;
    // 2026-11-09 es lunes; la ruta de prueba sale lunes y miércoles (1 + 4 = 5).
    private const string Lunes = "2026-11-09";
    private const string Martes = "2026-11-10";

    private readonly BaseDatosPrueba _bd;
    private ApiPrueba _api = null!;

    public JornadasTests(BaseDatosPrueba bd) => _bd = bd;
    public Task InitializeAsync() { _api = new ApiPrueba(_bd); return Task.CompletedTask; }
    public async Task DisposeAsync() => await _api.DisposeAsync();

    private async Task<HttpClient> SesionAsync(bool escribir = true)
    {
        var correo = $"jornadas-{Guid.NewGuid():N}@example.invalid";
        await _bd.AutorizarAsync(correo, escribir: escribir);
        var nav = _api.NuevoNavegador();
        Assert.Equal(HttpStatusCode.NoContent, (await nav.EntrarAsync(ValidadorFicticio.Token($"s-{correo}", correo))).StatusCode);
        return nav;
    }

    private static async Task<HttpResponseMessage> CambiarAsync(HttpClient nav, Guid ruta, string fecha, string estado, int revision)
    {
        var req = new HttpRequestMessage(HttpMethod.Put, $"/api/jornadas/{ruta}/{fecha}/estado") { Content = JsonContent.Create(new { estado, revision }) };
        req.Headers.Add(ServiciosAcceso.CabeceraCsrf, await nav.CsrfAsync());
        return await nav.SendAsync(req);
    }

    private async Task<Guid> SembrarRutaAsync(bool completa)
    {
        var (ruta, unidad, persona, parada, sitio, version, empresa, cliente) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        await _bd.SqlComoDuenoAsync($"""
            BEGIN;
            INSERT INTO negocio.vehiculo VALUES ('{Org}', '{Suc}', '{unidad}', 'JOR-{unidad.ToString("N")[..5]}');
            INSERT INTO negocio.ficha_vehiculo(organizacion_id, sucursal_id, vehiculo_id, revision, capacidad_id, clasificacion) VALUES ('{Org}', '{Suc}', '{unidad}', 1, 2, 'fosas');
            INSERT INTO negocio.ruta(organizacion_id, sucursal_id, id) VALUES ('{Org}', '{Suc}', '{ruta}');
            INSERT INTO negocio.ruta_version(organizacion_id, sucursal_id, ruta_id, revision, nombre, dias, vehiculo_id) VALUES ('{Org}', '{Suc}', '{ruta}', 1, 'Ruta Jornada Ficticia', 5, '{unidad}');
            {(completa ? $"""
            INSERT INTO seguridad.persona VALUES ('{Org}', '{persona}', 'Operador Jornada Ficticio');
            INSERT INTO negocio.asignacion_unidad(organizacion_id, sucursal_id, vehiculo_id, revision, persona_id) VALUES ('{Org}', '{Suc}', '{unidad}', 1, '{persona}');
            INSERT INTO negocio.empresa VALUES ('{Org}', '{empresa}', 'CLI-{empresa:N}');
            INSERT INTO negocio.ficha_empresa(organizacion_id, empresa_id, revision, nombre_comercial) VALUES ('{Org}', '{empresa}', 1, 'Cliente Jornada Ficticio');
            INSERT INTO negocio.empresa_sucursal VALUES ('{Org}', '{Suc}', '{empresa}');
            INSERT INTO negocio.cliente VALUES ('{Org}', '{Suc}', '{cliente}', '{empresa}', 'J-{cliente:N}');
            INSERT INTO negocio.sitio VALUES ('{Org}', '{Suc}', '{sitio}', 'Sitio jornada');
            INSERT INTO negocio.punto_servicio VALUES ('{Org}', '{Suc}', '{sitio}');
            INSERT INTO negocio.parada(organizacion_id, sucursal_id, id, ruta_id, posicion) VALUES ('{Org}', '{Suc}', '{parada}', '{ruta}', 1);
            INSERT INTO negocio.parada_version(organizacion_id, sucursal_id, id, parada_id, revision, sitio_id) VALUES ('{Org}', '{Suc}', '{version}', '{parada}', 1, '{sitio}');
            INSERT INTO negocio.parada_cliente(organizacion_id, sucursal_id, version_id, cliente_id, banos_planeados, fosas_planeadas) VALUES ('{Org}', '{Suc}', '{version}', '{cliente}', NULL, 1);
            """ : "")}
            COMMIT;
            """);
        return ruta;
    }

    private static async Task<List<JornadaDeRuta>> ListarAsync(HttpClient nav, string fecha) =>
        (await nav.GetFromJsonAsync<List<JornadaDeRuta>>($"/api/jornadas?fecha={fecha}"))!;

    [Fact]
    public async Task Publicar_retirar_y_cancelar_una_jornada_guarda_el_historial_con_revision()
    {
        var ruta = await SembrarRutaAsync(completa: true);
        using var nav = await SesionAsync();
        Assert.DoesNotContain(await ListarAsync(nav, Lunes), j => j.RutaId == ruta); // sin fila = borrador

        Assert.Equal(HttpStatusCode.OK, (await CambiarAsync(nav, ruta, Lunes, "programada", 0)).StatusCode);
        var publicada = Assert.Single(await ListarAsync(nav, Lunes), j => j.RutaId == ruta);
        Assert.Equal(("programada", 2), (publicada.Estado, publicada.Revision));
        Assert.DoesNotContain(await ListarAsync(nav, "2026-11-11"), j => j.RutaId == ruta); // otra fecha: sin publicar
        var semana = (await nav.GetFromJsonAsync<List<JornadaDeRuta>>("/api/jornadas?desde=2026-11-09&hasta=2026-11-14"))!;
        Assert.Equal(DateOnly.Parse(Lunes), Assert.Single(semana, j => j.RutaId == ruta).Fecha); // el rango trae su fecha
        Assert.Equal(HttpStatusCode.BadRequest, (await nav.GetAsync("/api/jornadas?desde=2026-11-09&hasta=2026-12-30")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await nav.GetAsync("/api/jornadas?desde=2026-11-09")).StatusCode);

        // Datos viejos: 409. Mismo estado: sin cambios.
        Assert.Equal(HttpStatusCode.Conflict, (await CambiarAsync(nav, ruta, Lunes, "cancelada", 0)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await CambiarAsync(nav, ruta, Lunes, "programada", 2)).StatusCode);
        Assert.Equal(2, Assert.Single(await ListarAsync(nav, Lunes), j => j.RutaId == ruta).Revision);

        Assert.Equal(HttpStatusCode.OK, (await CambiarAsync(nav, ruta, Lunes, "borrador", 2)).StatusCode); // retirar
        Assert.Equal(HttpStatusCode.OK, (await CambiarAsync(nav, ruta, Lunes, "cancelada", 3)).StatusCode);
        var cancelada = Assert.Single(await ListarAsync(nav, Lunes), j => j.RutaId == ruta);
        Assert.Equal(("cancelada", 4), (cancelada.Estado, cancelada.Revision));
        Assert.Equal("borrador,programada,borrador,cancelada",
            await _bd.ConsultarAsync($"SELECT string_agg(e.estado, ',' ORDER BY e.revision) FROM negocio.estado_jornada e JOIN negocio.jornada j ON j.id = e.jornada_id WHERE j.ruta_id = '{ruta}' AND j.fecha = '{Lunes}'"));
    }

    [Fact]
    public async Task Valida_dia_de_la_semana_ruta_completa_estado_permisos_y_sesion()
    {
        var completa = await SembrarRutaAsync(completa: true);
        var incompleta = await SembrarRutaAsync(completa: false);
        using var nav = await SesionAsync();
        Assert.Equal(HttpStatusCode.BadRequest, (await CambiarAsync(nav, completa, Martes, "programada", 0)).StatusCode); // no sale martes
        Assert.Equal(HttpStatusCode.BadRequest, (await CambiarAsync(nav, incompleta, Lunes, "programada", 0)).StatusCode); // sin operador ni paradas
        Assert.Equal(HttpStatusCode.BadRequest, (await CambiarAsync(nav, completa, Lunes, "en_curso", 0)).StatusCode); // eso lo pone la app
        Assert.Equal(HttpStatusCode.BadRequest, (await CambiarAsync(nav, completa, "no-es-fecha", "programada", 0)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await CambiarAsync(nav, Guid.NewGuid(), Lunes, "programada", 0)).StatusCode);
        Assert.DoesNotContain(await ListarAsync(nav, Lunes), j => j.RutaId == completa || j.RutaId == incompleta);

        using var lector = await SesionAsync(escribir: false);
        Assert.Equal(HttpStatusCode.Forbidden, (await CambiarAsync(lector, completa, Lunes, "programada", 0)).StatusCode);
        using var anonimo = _api.NuevoNavegador();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonimo.GetAsync($"/api/jornadas?fecha={Lunes}")).StatusCode);
    }
}
