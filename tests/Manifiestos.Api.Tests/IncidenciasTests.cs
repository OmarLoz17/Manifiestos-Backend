using System.Net;
using System.Net.Http.Json;
using Manifiestos.Api.Acceso;
using Manifiestos.Api.Incidencias;
using Manifiestos.Api.Rutas;
using Manifiestos.Api.Tests.Infraestructura;

namespace Manifiestos.Api.Tests;

/// <summary>Incidencias (0016) contra PostgreSQL real: consulta, seguimiento con historial, permisos y aislamiento. Datos ficticios.</summary>
[Trait("Categoria", "Integracion")]
public sealed class IncidenciasTests : IClassFixture<BaseDatosPrueba>, IAsyncLifetime
{
    private static readonly Guid Org = BaseDatosPrueba.Organizacion;
    private static readonly Guid Suc = BaseDatosPrueba.Sucursal;
    private const string Empresa = "0f000000-0000-4000-8000-000000000002";
    private static readonly Guid Suc2 = Guid.Parse("0f000000-0000-4000-8000-0000000000a2");

    private readonly BaseDatosPrueba _bd;
    private ApiPrueba _api = null!;

    public IncidenciasTests(BaseDatosPrueba bd) => _bd = bd;

    public async Task InitializeAsync()
    {
        _api = new ApiPrueba(_bd);
        await _bd.SqlComoDuenoAsync($"""
            INSERT INTO negocio.sucursal(organizacion_id, id, empresa_id, codigo, nombre)
            VALUES ('{Org}', '{Suc2}', '{Empresa}', 'PRUEBA2', 'Otra sucursal ficticia') ON CONFLICT DO NOTHING;
            """);
    }

    public async Task DisposeAsync() => await _api.DisposeAsync();

    private async Task<HttpClient> SesionAsync(bool escribir)
    {
        var correo = $"incidencias-{Guid.NewGuid():N}@example.invalid";
        await _bd.AutorizarAsync(correo, escribir: escribir);
        var nav = _api.NuevoNavegador();
        Assert.Equal(HttpStatusCode.NoContent, (await nav.EntrarAsync(ValidadorFicticio.Token($"s-{correo}", correo))).StatusCode);
        return nav;
    }

    private static async Task<HttpResponseMessage> SeguirAsync(HttpClient nav, Guid id, object cuerpo, bool conCsrf = true)
    {
        var req = new HttpRequestMessage(HttpMethod.Put, $"/api/incidencias/{id}/seguimiento") { Content = JsonContent.Create(cuerpo) };
        if (conCsrf) req.Headers.Add(ServiciosAcceso.CabeceraCsrf, await nav.CsrfAsync());
        return await nav.SendAsync(req);
    }

    private static async Task<IncidenciaResumen> IncidenciaAsync(HttpClient nav, Guid id) =>
        Assert.Single((await nav.GetFromJsonAsync<List<IncidenciaResumen>>("/api/incidencias"))!, x => x.Id == id);

    /// <summary>Incidencia como la registrará la app: unidad, operador, cliente y, en sanitarios, baños.</summary>
    private async Task<Guid> SembrarAsync(Guid sucursal, string servicio = "fosa", string motivo = "sin_acceso", int? asignados = null, int? atendidos = null)
    {
        var (id, cliente, empresa, unidad, persona) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        string N(int? v) => v?.ToString() ?? "NULL";
        await _bd.SqlComoDuenoAsync($"""
            BEGIN;
            INSERT INTO negocio.empresa VALUES ('{Org}', '{empresa}', 'CLI-{empresa:N}');
            INSERT INTO negocio.ficha_empresa(organizacion_id, empresa_id, revision, nombre_comercial) VALUES ('{Org}', '{empresa}', 1, 'Cliente Incidencia Ficticio');
            INSERT INTO negocio.empresa_sucursal VALUES ('{Org}', '{sucursal}', '{empresa}');
            INSERT INTO negocio.cliente VALUES ('{Org}', '{sucursal}', '{cliente}', '{empresa}', 'C-{cliente:N}');
            INSERT INTO negocio.vehiculo VALUES ('{Org}', '{sucursal}', '{unidad}', 'INC-{unidad.ToString("N")[..5]}');
            INSERT INTO seguridad.persona VALUES ('{Org}', '{persona}', 'Operador Incidencia Ficticio');
            INSERT INTO negocio.incidencia(organizacion_id, sucursal_id, id, servicio, motivo, descripcion, ocurrida_en, cliente_id, vehiculo_id, persona_id,
                banos_asignados, banos_atendidos, latitud, longitud, precision_m)
            VALUES ('{Org}', '{sucursal}', '{id}', '{servicio}', '{motivo}', 'Portón cerrado, nadie atendió.', '2026-11-05T11:10:00-06:00',
                '{cliente}', '{unidad}', '{persona}', {N(asignados)}, {N(atendidos)}, 25.686614, -100.316113, 8);
            COMMIT;
            """);
        return id;
    }

    [Fact]
    public async Task Lista_incidencias_abiertas_con_folio_referencias_banos_y_ubicacion()
    {
        var fosa = await SembrarAsync(Suc);
        var sanitario = await SembrarAsync(Suc, "sanitario", "servicio_parcial", asignados: 12, atendidos: 8);
        using var nav = await SesionAsync(false);
        var f = await IncidenciaAsync(nav, fosa);
        Assert.Matches("^INC-\\d{6}$", f.Folio);
        Assert.Equal(("fosa", "sin_acceso", "abierta", 1), (f.Servicio, f.Motivo, f.Estatus, f.Revision));
        Assert.Equal(("Cliente Incidencia Ficticio", "Operador Incidencia Ficticio"), (f.Cliente?.Nombre, f.Operador?.Nombre));
        Assert.StartsWith("INC-", f.Unidad?.Nombre);
        Assert.Equal(25.686614m, f.Ubicacion?.Latitud);
        Assert.False(f.TieneEvidencia);
        Assert.Single(f.Historial);
        var s = await IncidenciaAsync(nav, sanitario);
        Assert.Equal((12, 8), (s.BanosAsignados, s.BanosAtendidos));
    }

    [Fact]
    public async Task El_seguimiento_crea_revisiones_y_una_edicion_vieja_recibe_409()
    {
        var id = await SembrarAsync(Suc);
        using var nav = await SesionAsync(true);
        Assert.False((await SeguirAsync(nav, id, new { estatus = "en_proceso", revision = 1 }, conCsrf: false)).IsSuccessStatusCode);
        var r = await SeguirAsync(nav, id, new { estatus = "en_proceso", comentario = "  Se llamó al cliente.  ", revision = 1 });
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal(2, (await r.Content.ReadFromJsonAsync<RevisionGuardada>())!.Revision);
        Assert.Equal(HttpStatusCode.Conflict, (await SeguirAsync(nav, id, new { estatus = "cerrada", revision = 1 })).StatusCode);
        // Mismo estatus sin comentario: no se registra nada.
        Assert.Equal(2, (await (await SeguirAsync(nav, id, new { estatus = "en_proceso", revision = 2 })).Content.ReadFromJsonAsync<RevisionGuardada>())!.Revision);
        Assert.Equal(HttpStatusCode.OK, (await SeguirAsync(nav, id, new { estatus = "cerrada", comentario = "Se reprogramó la orden.", revision = 2 })).StatusCode);
        var i = await IncidenciaAsync(nav, id);
        Assert.Equal(("cerrada", 3), (i.Estatus, i.Revision));
        Assert.Equal(["abierta", "en_proceso", "cerrada"], i.Historial.Select(h => h.Estatus));
        Assert.Equal("Se llamó al cliente.", i.Historial[1].Comentario);

        Assert.Equal(HttpStatusCode.BadRequest, (await SeguirAsync(nav, id, new { estatus = "resuelta", revision = 3 })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await SeguirAsync(nav, id, new { estatus = "abierta", comentario = new string('x', 1001), revision = 3 })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await SeguirAsync(nav, Guid.NewGuid(), new { estatus = "abierta", revision = 1 })).StatusCode);
        using var lector = await SesionAsync(false);
        Assert.Equal(HttpStatusCode.Forbidden, (await SeguirAsync(lector, id, new { estatus = "abierta", revision = 3 })).StatusCode);
        Assert.Equal("3", await _bd.ConsultarAsync($"SELECT count(*) FROM negocio.seguimiento_incidencia WHERE incidencia_id = '{id}'"));
    }

    [Fact]
    public async Task Solo_ve_sus_sucursales_exige_sesion_y_la_base_rechaza_datos_incoherentes()
    {
        var ajena = await SembrarAsync(Suc2);
        using var nav = await SesionAsync(true);
        Assert.DoesNotContain((await nav.GetFromJsonAsync<List<IncidenciaResumen>>("/api/incidencias"))!, x => x.Id == ajena);
        Assert.Equal(HttpStatusCode.NotFound, (await SeguirAsync(nav, ajena, new { estatus = "cerrada", revision = 1 })).StatusCode);
        using var anonimo = _api.NuevoNavegador();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonimo.GetAsync("/api/incidencias")).StatusCode);
        // Más baños atendidos que asignados, o baños en una incidencia de fosa, no se aceptan.
        await Assert.ThrowsAsync<InvalidOperationException>(() => SembrarAsync(Suc, "sanitario", "servicio_parcial", asignados: 5, atendidos: 6));
        await Assert.ThrowsAsync<InvalidOperationException>(() => SembrarAsync(Suc, "fosa", "sin_acceso", asignados: 5));
    }
}
