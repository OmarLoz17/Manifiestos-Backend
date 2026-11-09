using System.Net;
using System.Net.Http.Json;
using Manifiestos.Api.Acceso;
using Manifiestos.Api.Clientes;
using Manifiestos.Api.Ordenes;
using Manifiestos.Api.Rutas;
using Manifiestos.Api.Tests.Infraestructura;

namespace Manifiestos.Api.Tests;

/// <summary>Órdenes por ruta y parada (solo lectura) y propiedad de la fosa en los sitios del cliente (0017). Datos ficticios.</summary>
[Trait("Categoria", "Integracion")]
public sealed class OrdenesTests : IClassFixture<BaseDatosPrueba>, IAsyncLifetime
{
    private static readonly Guid Org = BaseDatosPrueba.Organizacion;
    private static readonly Guid Suc = BaseDatosPrueba.Sucursal;

    private readonly BaseDatosPrueba _bd;
    private ApiPrueba _api = null!;

    public OrdenesTests(BaseDatosPrueba bd) => _bd = bd;
    public Task InitializeAsync() { _api = new ApiPrueba(_bd); return Task.CompletedTask; }
    public async Task DisposeAsync() => await _api.DisposeAsync();

    private async Task<HttpClient> SesionAsync(bool escribir = true)
    {
        var correo = $"ordenes-{Guid.NewGuid():N}@example.invalid";
        await _bd.AutorizarAsync(correo, escribir: escribir);
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

    [Fact]
    public async Task La_propiedad_de_la_fosa_se_guarda_por_sitio_con_revision_y_se_conserva_al_dar_de_baja()
    {
        using var nav = await SesionAsync();
        var r = await EnviarAsync(nav, HttpMethod.Post, "/api/clientes", new
        {
            sucursalId = Suc, nombreComercial = "Cliente Fosa Propia Ficticio",
            sitios = new[] { new { nombre = "Sitio rentado ficticio", municipio = "Municipio ficticio", fosas = 1, fosaPropia = true } },
        });
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        var id = (await r.Content.ReadFromJsonAsync<RevisionGuardada>())!.Id;
        var sitio = Assert.Single(Assert.Single((await nav.GetFromJsonAsync<List<ClienteResumen>>("/api/clientes"))!, c => c.Id == id).Sitios!);
        Assert.True(sitio.FosaPropia);

        var cambio = await EnviarAsync(nav, HttpMethod.Put, $"/api/clientes/{id}/sitios", new
        {
            sitios = new[] { new { sitioId = sitio.SitioId, revision = sitio.Revision, revisionFicha = sitio.RevisionFicha, nombre = sitio.Nombre, municipio = sitio.Municipio, fosas = 1, fosaPropia = false } },
        });
        Assert.Equal(HttpStatusCode.NoContent, cambio.StatusCode);
        Assert.Equal("t,f", await _bd.ConsultarAsync($"SELECT string_agg(fosa_propia::text, ',' ORDER BY revision) FROM negocio.cliente_sitio WHERE cliente_id = '{id}'").ContinueWith(t => t.Result.Replace("true", "t").Replace("false", "f")));
    }

    [Fact]
    public async Task Ordenes_lista_cada_ruta_con_paradas_clientes_autorizacion_cantidades_y_propiedad()
    {
        var (ruta, unidad, parada, sitio) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var (autorizado, general, empA, empG) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var version = Guid.NewGuid();
        await _bd.SqlComoDuenoAsync($"""
            BEGIN;
            INSERT INTO negocio.empresa VALUES ('{Org}', '{empA}', 'CLI-{empA:N}'), ('{Org}', '{empG}', 'CLI-{empG:N}');
            INSERT INTO negocio.ficha_empresa(organizacion_id, empresa_id, revision, nombre_comercial) VALUES
                ('{Org}', '{empA}', 1, 'Autorizado Ordenes Ficticio'), ('{Org}', '{empG}', 1, 'General Ordenes Ficticio');
            INSERT INTO negocio.empresa_sucursal VALUES ('{Org}', '{Suc}', '{empA}'), ('{Org}', '{Suc}', '{empG}');
            INSERT INTO negocio.autorizacion_empresa(organizacion_id, empresa_id, autoridad, finalidad, numero) VALUES ('{Org}', '{empA}', 'Medio ambiente', 'Generador', 'FI-ORD');
            INSERT INTO negocio.cliente VALUES ('{Org}', '{Suc}', '{autorizado}', '{empA}', 'A-{autorizado:N}'), ('{Org}', '{Suc}', '{general}', '{empG}', 'G-{general:N}');
            INSERT INTO negocio.vehiculo VALUES ('{Org}', '{Suc}', '{unidad}', 'ORD-{unidad.ToString("N")[..5]}');
            INSERT INTO negocio.ficha_vehiculo(organizacion_id, sucursal_id, vehiculo_id, revision, capacidad_id, clasificacion) VALUES ('{Org}', '{Suc}', '{unidad}', 1, 2, 'fosas');
            INSERT INTO negocio.sitio VALUES ('{Org}', '{Suc}', '{sitio}', 'Sitio ordenes');
            INSERT INTO negocio.ficha_sitio(organizacion_id, sucursal_id, sitio_id, revision, nombre, calle, numero_exterior, municipio)
            VALUES ('{Org}', '{Suc}', '{sitio}', 1, 'Plaza Ordenes Ficticia', 'Calle Ficticia', '10', 'Municipio Ficticio');
            INSERT INTO negocio.punto_servicio VALUES ('{Org}', '{Suc}', '{sitio}');
            INSERT INTO negocio.cliente_sitio(organizacion_id, sucursal_id, cliente_id, sitio_id, revision, fosa_propia) VALUES ('{Org}', '{Suc}', '{general}', '{sitio}', 1, true);
            INSERT INTO negocio.ruta(organizacion_id, sucursal_id, id) VALUES ('{Org}', '{Suc}', '{ruta}');
            INSERT INTO negocio.ruta_version(organizacion_id, sucursal_id, ruta_id, revision, nombre, dias, vehiculo_id) VALUES ('{Org}', '{Suc}', '{ruta}', 1, 'Ruta Ordenes Ficticia', 21, '{unidad}');
            INSERT INTO negocio.parada(organizacion_id, sucursal_id, id, ruta_id, posicion) VALUES ('{Org}', '{Suc}', '{parada}', '{ruta}', 1);
            INSERT INTO negocio.parada_version(organizacion_id, sucursal_id, id, parada_id, revision, sitio_id) VALUES ('{Org}', '{Suc}', '{version}', '{parada}', 1, '{sitio}');
            INSERT INTO negocio.parada_cliente(organizacion_id, sucursal_id, version_id, cliente_id, banos_planeados, fosas_planeadas) VALUES
                ('{Org}', '{Suc}', '{version}', '{autorizado}', NULL, 3), ('{Org}', '{Suc}', '{version}', '{general}', 6, 1);
            COMMIT;
            """);
        using var nav = await SesionAsync(escribir: false);
        var r = Assert.Single((await nav.GetFromJsonAsync<List<RutaDeOrdenes>>("/api/ordenes"))!, x => x.Id == ruta);
        Assert.Equal(("Ruta Ordenes Ficticia", 15m, "fosas"), (r.Nombre, r.Unidad.CapacidadM3, r.Unidad.Clasificacion));
        Assert.Equal([1, 3, 5], r.Dias);
        var p = Assert.Single(r.Paradas);
        Assert.Equal(("Plaza Ordenes Ficticia", 1), (p.Sitio, p.Posicion));
        Assert.Contains("Calle Ficticia 10", p.Direccion);
        var a = Assert.Single(p.Clientes, c => c.ClienteId == autorizado);
        var g = Assert.Single(p.Clientes, c => c.ClienteId == general);
        Assert.Equal((true, 0, 3, false), (a.Autorizado, a.Banos, a.Fosas, a.FosaPropia));
        Assert.Equal((false, 6, 1, true), (g.Autorizado, g.Banos, g.Fosas, g.FosaPropia));
        using var anonimo = _api.NuevoNavegador();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonimo.GetAsync("/api/ordenes")).StatusCode);
    }

    [Fact]
    public async Task Parada_extraordinaria_es_de_un_dia_no_forma_parte_de_la_ruta_fija_y_valida_dia_sitio_y_permisos()
    {
        var (ruta, unidad, parada, sitio, version, empresa, cliente, otro, empresaOtro) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        await _bd.SqlComoDuenoAsync($"""
            BEGIN;
            INSERT INTO negocio.empresa VALUES ('{Org}', '{empresa}', 'CLI-{empresa:N}'), ('{Org}', '{empresaOtro}', 'CLI-{empresaOtro:N}');
            INSERT INTO negocio.ficha_empresa(organizacion_id, empresa_id, revision, nombre_comercial) VALUES
                ('{Org}', '{empresa}', 1, 'Cliente Extra Ficticio'), ('{Org}', '{empresaOtro}', 1, 'Otro Cliente Ficticio');
            INSERT INTO negocio.empresa_sucursal VALUES ('{Org}', '{Suc}', '{empresa}'), ('{Org}', '{Suc}', '{empresaOtro}');
            INSERT INTO negocio.cliente VALUES ('{Org}', '{Suc}', '{cliente}', '{empresa}', 'X-{cliente:N}'), ('{Org}', '{Suc}', '{otro}', '{empresaOtro}', 'Y-{otro:N}');
            INSERT INTO negocio.vehiculo VALUES ('{Org}', '{Suc}', '{unidad}', 'EXT-{unidad.ToString("N")[..5]}');
            INSERT INTO negocio.ficha_vehiculo(organizacion_id, sucursal_id, vehiculo_id, revision, capacidad_id, clasificacion) VALUES ('{Org}', '{Suc}', '{unidad}', 1, 2, 'fosas');
            INSERT INTO negocio.sitio VALUES ('{Org}', '{Suc}', '{sitio}', 'Sitio extra');
            INSERT INTO negocio.ficha_sitio(organizacion_id, sucursal_id, sitio_id, revision, nombre, calle, municipio) VALUES ('{Org}', '{Suc}', '{sitio}', 1, 'Planta Extra Ficticia', 'Calle Ficticia 5', 'Municipio Ficticio');
            INSERT INTO negocio.punto_servicio VALUES ('{Org}', '{Suc}', '{sitio}');
            INSERT INTO negocio.cliente_sitio(organizacion_id, sucursal_id, cliente_id, sitio_id, revision) VALUES ('{Org}', '{Suc}', '{cliente}', '{sitio}', 1);
            INSERT INTO negocio.ruta(organizacion_id, sucursal_id, id) VALUES ('{Org}', '{Suc}', '{ruta}');
            INSERT INTO negocio.ruta_version(organizacion_id, sucursal_id, ruta_id, revision, nombre, dias, vehiculo_id) VALUES ('{Org}', '{Suc}', '{ruta}', 1, 'Ruta Extra Ficticia', 21, '{unidad}');
            INSERT INTO negocio.parada(organizacion_id, sucursal_id, id, ruta_id, posicion) VALUES ('{Org}', '{Suc}', '{parada}', '{ruta}', 1);
            INSERT INTO negocio.parada_version(organizacion_id, sucursal_id, id, parada_id, revision, sitio_id) VALUES ('{Org}', '{Suc}', '{version}', '{parada}', 1, '{sitio}');
            INSERT INTO negocio.parada_cliente(organizacion_id, sucursal_id, version_id, cliente_id, banos_planeados, fosas_planeadas) VALUES ('{Org}', '{Suc}', '{version}', '{cliente}', NULL, 1);
            COMMIT;
            """);
        using var nav = await SesionAsync();
        const string lunes = "2026-11-09", martes = "2026-11-10", miercoles = "2026-11-11";
        var cuerpo = new { rutaId = ruta, fecha = lunes, sitioId = sitio, clientes = new[] { new { clienteId = cliente, banos = 0, fosas = 2 } } };
        Assert.Equal(HttpStatusCode.Created, (await EnviarAsync(nav, HttpMethod.Post, "/api/ordenes/extraordinarias", cuerpo)).StatusCode);

        // Solo existe ese lunes: el miércoles (que también sale) y el plan fijo no la traen.
        var lun = Assert.Single((await nav.GetFromJsonAsync<List<RutaDeOrdenes>>($"/api/ordenes?fecha={lunes}"))!, x => x.Id == ruta);
        var extra = Assert.Single(lun.Extraordinarias!);
        Assert.Equal(("Planta Extra Ficticia", 2), (extra.Sitio, Assert.Single(extra.Clientes).Fosas));
        Assert.Empty(Assert.Single((await nav.GetFromJsonAsync<List<RutaDeOrdenes>>($"/api/ordenes?fecha={miercoles}"))!, x => x.Id == ruta).Extraordinarias!);
        var fija = Assert.Single((await nav.GetFromJsonAsync<List<RutaDeOrdenes>>("/api/ordenes"))!, x => x.Id == ruta);
        Assert.Single(fija.Paradas); // la ruta fija no cambió
        Assert.Empty(fija.Extraordinarias ?? []);

        // Validaciones: día en que no sale, cliente sin ese sitio, cantidades y permisos.
        Assert.Equal(HttpStatusCode.BadRequest, (await EnviarAsync(nav, HttpMethod.Post, "/api/ordenes/extraordinarias", new { rutaId = ruta, fecha = martes, sitioId = sitio, clientes = new[] { new { clienteId = cliente, fosas = 1 } } })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await EnviarAsync(nav, HttpMethod.Post, "/api/ordenes/extraordinarias", new { rutaId = ruta, fecha = lunes, sitioId = sitio, clientes = new[] { new { clienteId = otro, fosas = 1 } } })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await EnviarAsync(nav, HttpMethod.Post, "/api/ordenes/extraordinarias", new { rutaId = ruta, fecha = lunes, sitioId = sitio, clientes = new[] { new { clienteId = cliente, banos = 0, fosas = 0 } } })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await EnviarAsync(nav, HttpMethod.Post, "/api/ordenes/extraordinarias", new { rutaId = Guid.NewGuid(), fecha = lunes, sitioId = sitio, clientes = new[] { new { clienteId = cliente, fosas = 1 } } })).StatusCode);
        using var lector = await SesionAsync(escribir: false);
        Assert.Equal(HttpStatusCode.Forbidden, (await EnviarAsync(lector, HttpMethod.Post, "/api/ordenes/extraordinarias", cuerpo)).StatusCode);
        using var anonimo = _api.NuevoNavegador();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonimo.PostAsJsonAsync("/api/ordenes/extraordinarias", cuerpo)).StatusCode);
    }
}
