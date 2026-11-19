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
            sucursalId = Suc, nombreComercial = "Cliente Fosa Propia Ficticio", autorizacion = "AMB-FICT-FOSA",
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
    public async Task Cambio_de_banos_de_un_cliente_vale_solo_ese_dia_y_no_toca_el_plan_fijo()
    {
        var (ruta, unidad, sitio, empresa, cliente, parada, version) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        await _bd.SqlComoDuenoAsync($"""
            BEGIN;
            INSERT INTO negocio.empresa VALUES ('{Org}', '{empresa}', 'CLI-{empresa:N}');
            INSERT INTO negocio.ficha_empresa(organizacion_id, empresa_id, revision, nombre_comercial) VALUES ('{Org}', '{empresa}', 1, 'Cliente Ajuste Ficticio');
            INSERT INTO negocio.empresa_sucursal VALUES ('{Org}', '{Suc}', '{empresa}');
            INSERT INTO negocio.cliente VALUES ('{Org}', '{Suc}', '{cliente}', '{empresa}', 'A-{cliente:N}');
            INSERT INTO negocio.vehiculo VALUES ('{Org}', '{Suc}', '{unidad}', 'AJS-{unidad.ToString("N")[..5]}');
            INSERT INTO negocio.ficha_vehiculo(organizacion_id, sucursal_id, vehiculo_id, revision, capacidad_id, clasificacion) VALUES ('{Org}', '{Suc}', '{unidad}', 1, 4, 'sanitarios');
            INSERT INTO negocio.sitio VALUES ('{Org}', '{Suc}', '{sitio}', 'Sitio ajuste');
            INSERT INTO negocio.ficha_sitio(organizacion_id, sucursal_id, sitio_id, revision, nombre, calle, municipio) VALUES ('{Org}', '{Suc}', '{sitio}', 1, 'Obra Ajuste', 'Calle Ficticia 8', 'Municipio Ficticio');
            INSERT INTO negocio.punto_servicio VALUES ('{Org}', '{Suc}', '{sitio}');
            INSERT INTO negocio.ruta(organizacion_id, sucursal_id, id) VALUES ('{Org}', '{Suc}', '{ruta}');
            INSERT INTO negocio.ruta_version(organizacion_id, sucursal_id, ruta_id, revision, nombre, dias, vehiculo_id) VALUES ('{Org}', '{Suc}', '{ruta}', 1, 'Ruta Ajuste Ficticia', 127, '{unidad}');
            INSERT INTO negocio.parada(organizacion_id, sucursal_id, id, ruta_id, posicion) VALUES ('{Org}', '{Suc}', '{parada}', '{ruta}', 1);
            INSERT INTO negocio.parada_version(organizacion_id, sucursal_id, id, parada_id, revision, sitio_id) VALUES ('{Org}', '{Suc}', '{version}', '{parada}', 1, '{sitio}');
            INSERT INTO negocio.parada_cliente(organizacion_id, sucursal_id, version_id, cliente_id, banos_planeados) VALUES ('{Org}', '{Suc}', '{version}', '{cliente}', 10);
            COMMIT;
            """);
        using var nav = await SesionAsync();
        var hoy = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(-6)).AddDays(1);   // mañana: siempre es hoy o futuro en cualquier zona
        var otro = hoy.AddDays(1);
        var ok = await EnviarAsync(nav, HttpMethod.Put, "/api/ordenes/ajustes-banos", new { rutaId = ruta, fecha = hoy.ToString("yyyy-MM-dd"), paradaId = parada, clienteId = cliente, banos = 25 });
        Assert.Equal(HttpStatusCode.NoContent, ok.StatusCode);
        async Task<(int Banos, bool Ajustado)> LeerAsync(DateOnly f)
        {
            var rutas = (await nav.GetFromJsonAsync<List<RutaDeOrdenes>>($"/api/ordenes?fecha={f:yyyy-MM-dd}"))!;
            var c = Assert.Single(Assert.Single(rutas, x => x.Id == ruta).Paradas).Clientes.Single();
            return (c.Banos, c.AjustadoHoy);
        }
        Assert.Equal((25, true), await LeerAsync(hoy));
        Assert.Equal((10, false), await LeerAsync(otro));   // otro día: el plan fijo
        Assert.Equal("10", await _bd.ConsultarAsync($"SELECT banos_planeados FROM negocio.parada_cliente WHERE version_id = '{version}'"));
        // Un segundo cambio queda como revisión nueva y gana el último.
        Assert.Equal(HttpStatusCode.NoContent, (await EnviarAsync(nav, HttpMethod.Put, "/api/ordenes/ajustes-banos", new { rutaId = ruta, fecha = hoy.ToString("yyyy-MM-dd"), paradaId = parada, clienteId = cliente, banos = 0 })).StatusCode);
        Assert.Equal((0, true), await LeerAsync(hoy));
        Assert.Equal("2", await _bd.ConsultarAsync($"SELECT count(*) FROM negocio.ajuste_banos_dia WHERE parada_id = '{parada}'"));
        // Validaciones: cliente que no está en la parada y día pasado.
        Assert.Equal(HttpStatusCode.BadRequest, (await EnviarAsync(nav, HttpMethod.Put, "/api/ordenes/ajustes-banos", new { rutaId = ruta, fecha = hoy.ToString("yyyy-MM-dd"), paradaId = parada, clienteId = Guid.NewGuid(), banos = 3 })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await EnviarAsync(nav, HttpMethod.Put, "/api/ordenes/ajustes-banos", new { rutaId = ruta, fecha = "2020-01-06", paradaId = parada, clienteId = cliente, banos = 3 })).StatusCode);
    }

    [Fact]
    public async Task Cambio_de_ordenes_de_fosas_vale_solo_ese_dia_conserva_el_plan_y_las_de_mas_son_extraordinarias()
    {
        var (ruta, unidad, sitio, empresa, cliente, parada, version) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        await _bd.SqlComoDuenoAsync($"""
            BEGIN;
            INSERT INTO negocio.empresa VALUES ('{Org}', '{empresa}', 'CLI-{empresa:N}');
            INSERT INTO negocio.ficha_empresa(organizacion_id, empresa_id, revision, nombre_comercial) VALUES ('{Org}', '{empresa}', 1, 'Cliente Fosas Ajuste Ficticio');
            INSERT INTO negocio.empresa_sucursal VALUES ('{Org}', '{Suc}', '{empresa}');
            INSERT INTO negocio.cliente VALUES ('{Org}', '{Suc}', '{cliente}', '{empresa}', 'F-{cliente:N}');
            INSERT INTO negocio.vehiculo VALUES ('{Org}', '{Suc}', '{unidad}', 'FAJ-{unidad.ToString("N")[..5]}');
            INSERT INTO negocio.ficha_vehiculo(organizacion_id, sucursal_id, vehiculo_id, revision, capacidad_id, clasificacion) VALUES ('{Org}', '{Suc}', '{unidad}', 1, 2, 'fosas');
            INSERT INTO negocio.sitio VALUES ('{Org}', '{Suc}', '{sitio}', 'Sitio fosas ajuste');
            INSERT INTO negocio.ficha_sitio(organizacion_id, sucursal_id, sitio_id, revision, nombre, calle, municipio) VALUES ('{Org}', '{Suc}', '{sitio}', 1, 'Obra Fosas', 'Calle Ficticia 9', 'Municipio Ficticio');
            INSERT INTO negocio.punto_servicio VALUES ('{Org}', '{Suc}', '{sitio}');
            INSERT INTO negocio.ruta(organizacion_id, sucursal_id, id) VALUES ('{Org}', '{Suc}', '{ruta}');
            INSERT INTO negocio.ruta_version(organizacion_id, sucursal_id, ruta_id, revision, nombre, dias, vehiculo_id) VALUES ('{Org}', '{Suc}', '{ruta}', 1, 'Ruta Fosas Ajuste Ficticia', 127, '{unidad}');
            INSERT INTO negocio.parada(organizacion_id, sucursal_id, id, ruta_id, posicion) VALUES ('{Org}', '{Suc}', '{parada}', '{ruta}', 1);
            INSERT INTO negocio.parada_version(organizacion_id, sucursal_id, id, parada_id, revision, sitio_id) VALUES ('{Org}', '{Suc}', '{version}', '{parada}', 1, '{sitio}');
            INSERT INTO negocio.parada_cliente(organizacion_id, sucursal_id, version_id, cliente_id, fosas_planeadas) VALUES ('{Org}', '{Suc}', '{version}', '{cliente}', 2);
            COMMIT;
            """);
        using var nav = await SesionAsync();
        var hoy = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(-6)).AddDays(1);
        var otro = hoy.AddDays(1);
        Assert.Equal(HttpStatusCode.NoContent, (await EnviarAsync(nav, HttpMethod.Put, "/api/ordenes/ajustes-fosas", new { rutaId = ruta, fecha = hoy.ToString("yyyy-MM-dd"), paradaId = parada, clienteId = cliente, fosas = 4 })).StatusCode);
        // Repetir la misma cantidad no cambia nada: no se guarda otra revisión (y por tanto no se avisa al operador).
        var antesRepetir = await _bd.ConsultarAsync("SELECT count(*) FROM negocio.ajuste_fosas_dia");
        Assert.Equal(HttpStatusCode.NoContent, (await EnviarAsync(nav, HttpMethod.Put, "/api/ordenes/ajustes-fosas", new { rutaId = ruta, fecha = hoy.ToString("yyyy-MM-dd"), paradaId = parada, clienteId = cliente, fosas = 4 })).StatusCode);
        Assert.Equal(antesRepetir, await _bd.ConsultarAsync("SELECT count(*) FROM negocio.ajuste_fosas_dia"));
        async Task<(int Fosas, int Plan, bool Ajustado)> LeerAsync(DateOnly d)
        {
            var rutas = (await nav.GetFromJsonAsync<List<RutaDeOrdenes>>($"/api/ordenes?fecha={d:yyyy-MM-dd}"))!;
            var c = Assert.Single(Assert.Single(rutas, x => x.Id == ruta).Paradas).Clientes.Single();
            return (c.Fosas, c.PlanFosas, c.AjustadoHoy);
        }
        Assert.Equal((4, 2, true), await LeerAsync(hoy));      // 2 del plan + 2 extraordinarias
        Assert.Equal((2, 2, false), await LeerAsync(otro));     // otro día: el plan fijo
        Assert.Equal("2", await _bd.ConsultarAsync($"SELECT fosas_planeadas FROM negocio.parada_cliente WHERE version_id = '{version}'"));
        // Un cliente sin órdenes planeadas en la parada no se puede cambiar.
        Assert.Equal(HttpStatusCode.BadRequest, (await EnviarAsync(nav, HttpMethod.Put, "/api/ordenes/ajustes-fosas", new { rutaId = ruta, fecha = hoy.ToString("yyyy-MM-dd"), paradaId = parada, clienteId = Guid.NewGuid(), fosas = 3 })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await EnviarAsync(nav, HttpMethod.Put, "/api/ordenes/ajustes-fosas", new { rutaId = ruta, fecha = "2020-01-06", paradaId = parada, clienteId = cliente, fosas = 3 })).StatusCode);
    }

    [Fact]
    public async Task Cambio_de_unidad_y_operador_vale_solo_ese_dia_y_no_toca_el_plan_ni_las_asignaciones()
    {
        var (ruta, u1, u2, p1, p2, p3) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        await _bd.SqlComoDuenoAsync($"""
            BEGIN;
            INSERT INTO negocio.vehiculo VALUES ('{Org}', '{Suc}', '{u1}', 'CU1-{u1.ToString("N")[..5]}'), ('{Org}', '{Suc}', '{u2}', 'CU2-{u2.ToString("N")[..5]}');
            INSERT INTO negocio.ficha_vehiculo(organizacion_id, sucursal_id, vehiculo_id, revision, capacidad_id, clasificacion) VALUES ('{Org}', '{Suc}', '{u1}', 1, 4, 'sanitarios'), ('{Org}', '{Suc}', '{u2}', 1, 2, 'fosas');
            INSERT INTO seguridad.persona(organizacion_id, id, nombre_completo) VALUES ('{Org}', '{p1}', 'Operador Uno Ficticio'), ('{Org}', '{p2}', 'Operador Dos Ficticio'), ('{Org}', '{p3}', 'Operador Tres Ficticio');
            INSERT INTO negocio.operador_ficha(organizacion_id, sucursal_id, persona_id, revision, nombre_completo, correo) VALUES ('{Org}', '{Suc}', '{p1}', 1, 'Operador Uno Ficticio', NULL), ('{Org}', '{Suc}', '{p2}', 1, 'Operador Dos Ficticio', NULL), ('{Org}', '{Suc}', '{p3}', 1, 'Operador Tres Ficticio', NULL);
            INSERT INTO negocio.asignacion_unidad(organizacion_id, sucursal_id, vehiculo_id, revision, persona_id) VALUES ('{Org}', '{Suc}', '{u1}', 1, '{p1}'), ('{Org}', '{Suc}', '{u2}', 1, '{p2}');
            INSERT INTO negocio.ruta(organizacion_id, sucursal_id, id) VALUES ('{Org}', '{Suc}', '{ruta}');
            INSERT INTO negocio.ruta_version(organizacion_id, sucursal_id, ruta_id, revision, nombre, dias, vehiculo_id) VALUES ('{Org}', '{Suc}', '{ruta}', 1, 'Ruta Cambio Ficticia', 127, '{u1}');
            COMMIT;
            """);
        using var nav = await SesionAsync();
        var hoy = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(-6)).AddDays(1);
        var otro = hoy.AddDays(1);
        async Task<(Guid Unidad, string? Operador, bool Cambio)> LeerAsync(DateOnly d)
        {
            var r = Assert.Single((await nav.GetFromJsonAsync<List<RutaDeOrdenes>>($"/api/ordenes?fecha={d:yyyy-MM-dd}"))!, x => x.Id == ruta);
            return (r.Unidad.Id, r.Operador?.Nombre, r.CambioDelDia);
        }
        Assert.Equal((u1, "Operador Uno Ficticio", false), await LeerAsync(hoy));
        // Cambia la unidad (sin operador): la ruta pasa a la unidad de fosas y a su operador asignado, solo ese día.
        Assert.Equal(HttpStatusCode.NoContent, (await EnviarAsync(nav, HttpMethod.Put, "/api/ordenes/cambios-unidad", new { rutaId = ruta, fecha = hoy.ToString("yyyy-MM-dd"), vehiculoId = u2 })).StatusCode);
        Assert.Equal((u2, "Operador Dos Ficticio", true), await LeerAsync(hoy));
        Assert.Equal((u1, "Operador Uno Ficticio", false), await LeerAsync(otro));
        // Cambia además el operador del día: otra persona en la misma unidad.
        Assert.Equal(HttpStatusCode.NoContent, (await EnviarAsync(nav, HttpMethod.Put, "/api/ordenes/cambios-unidad", new { rutaId = ruta, fecha = hoy.ToString("yyyy-MM-dd"), vehiculoId = u2, personaId = p3 })).StatusCode);
        Assert.Equal((u2, "Operador Tres Ficticio", true), await LeerAsync(hoy));
        // El plan fijo y las asignaciones quedan igual; los cambios son historial.
        Assert.Equal(u1.ToString(), await _bd.ConsultarAsync($"SELECT vehiculo_id FROM negocio.ruta_version WHERE ruta_id = '{ruta}'"));
        Assert.Equal("1", await _bd.ConsultarAsync($"SELECT count(*) FROM negocio.asignacion_unidad WHERE vehiculo_id = '{u2}'"));
        Assert.Equal("2", await _bd.ConsultarAsync($"SELECT count(*) FROM negocio.cambio_unidad_dia WHERE ruta_id = '{ruta}'"));
        // Validaciones: unidad inexistente, operador inexistente y día pasado.
        Assert.Equal(HttpStatusCode.BadRequest, (await EnviarAsync(nav, HttpMethod.Put, "/api/ordenes/cambios-unidad", new { rutaId = ruta, fecha = hoy.ToString("yyyy-MM-dd"), vehiculoId = Guid.NewGuid() })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await EnviarAsync(nav, HttpMethod.Put, "/api/ordenes/cambios-unidad", new { rutaId = ruta, fecha = hoy.ToString("yyyy-MM-dd"), vehiculoId = u2, personaId = Guid.NewGuid() })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await EnviarAsync(nav, HttpMethod.Put, "/api/ordenes/cambios-unidad", new { rutaId = ruta, fecha = "2020-01-06", vehiculoId = u2 })).StatusCode);
    }

    [Fact]
    public async Task Parada_extraordinaria_en_sanitarios_solo_admite_banos()
    {
        var (ruta, unidad, sitio, empresa, cliente) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        await _bd.SqlComoDuenoAsync($"""
            BEGIN;
            INSERT INTO negocio.empresa VALUES ('{Org}', '{empresa}', 'CLI-{empresa:N}');
            INSERT INTO negocio.ficha_empresa(organizacion_id, empresa_id, revision, nombre_comercial) VALUES ('{Org}', '{empresa}', 1, 'Cliente Sanitarios Ficticio');
            INSERT INTO negocio.empresa_sucursal VALUES ('{Org}', '{Suc}', '{empresa}');
            INSERT INTO negocio.cliente VALUES ('{Org}', '{Suc}', '{cliente}', '{empresa}', 'S-{cliente:N}');
            INSERT INTO negocio.vehiculo VALUES ('{Org}', '{Suc}', '{unidad}', 'SAN-{unidad.ToString("N")[..5]}');
            INSERT INTO negocio.ficha_vehiculo(organizacion_id, sucursal_id, vehiculo_id, revision, capacidad_id, clasificacion) VALUES ('{Org}', '{Suc}', '{unidad}', 1, 4, 'sanitarios');
            INSERT INTO negocio.sitio VALUES ('{Org}', '{Suc}', '{sitio}', 'Sitio sanitarios');
            INSERT INTO negocio.ficha_sitio(organizacion_id, sucursal_id, sitio_id, revision, nombre, calle, municipio) VALUES ('{Org}', '{Suc}', '{sitio}', 1, 'Obra Ficticia', 'Calle Ficticia 7', 'Municipio Ficticio');
            INSERT INTO negocio.punto_servicio VALUES ('{Org}', '{Suc}', '{sitio}');
            INSERT INTO negocio.autorizacion_empresa(organizacion_id, empresa_id, autoridad, finalidad, numero) VALUES ('{Org}', '{empresa}', 'Medio ambiente', 'Generador', 'AMB-FICT-EXT');
            INSERT INTO negocio.cliente_sitio(organizacion_id, sucursal_id, cliente_id, sitio_id, revision) VALUES ('{Org}', '{Suc}', '{cliente}', '{sitio}', 1);
            INSERT INTO negocio.ruta(organizacion_id, sucursal_id, id) VALUES ('{Org}', '{Suc}', '{ruta}');
            INSERT INTO negocio.ruta_version(organizacion_id, sucursal_id, ruta_id, revision, nombre, dias, vehiculo_id) VALUES ('{Org}', '{Suc}', '{ruta}', 1, 'Ruta Sanitarios Ficticia', 21, '{unidad}');
            COMMIT;
            """);
        using var nav = await SesionAsync();
        // Sanitarios solo lleva baños: una fosa se rechaza; baños y fosas juntos tampoco.
        var conFosa = await EnviarAsync(nav, HttpMethod.Post, "/api/ordenes/extraordinarias",
            new { rutaId = ruta, fecha = Fechas.Lunes, sitioId = sitio, clientes = new[] { new { clienteId = cliente, fosas = 1 } } });
        Assert.Equal(HttpStatusCode.BadRequest, conFosa.StatusCode);
        Assert.Contains("solo llevan baños", await conFosa.Content.ReadAsStringAsync());
        var ambas = await EnviarAsync(nav, HttpMethod.Post, "/api/ordenes/extraordinarias",
            new { rutaId = ruta, fecha = Fechas.Lunes, sitioId = sitio, clientes = new[] { new { clienteId = cliente, banos = 5, fosas = 1 } } });
        Assert.Equal(HttpStatusCode.BadRequest, ambas.StatusCode);
        Assert.Equal("0", await _bd.ConsultarAsync($"SELECT count(*) FROM negocio.parada_extraordinaria WHERE ruta_id = '{ruta}'"));
        // Un cliente autorizado no va en sanitarios (este cliente tiene autorización de generador), aunque se pidan baños.
        var autorizado = await EnviarAsync(nav, HttpMethod.Post, "/api/ordenes/extraordinarias",
            new { rutaId = ruta, fecha = Fechas.Lunes, sitioId = sitio, clientes = new[] { new { clienteId = cliente, banos = 5 } } });
        Assert.Equal(HttpStatusCode.BadRequest, autorizado.StatusCode);
        Assert.Equal("0", await _bd.ConsultarAsync($"SELECT count(*) FROM negocio.parada_extraordinaria WHERE ruta_id = '{ruta}'"));
        // Un cliente general con baños sí se agrega.
        var (empresaGeneral, general, sitioNormal) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        await _bd.SqlComoDuenoAsync($"""
            BEGIN;
            INSERT INTO negocio.empresa VALUES ('{Org}', '{empresaGeneral}', 'CLI-{empresaGeneral:N}');
            INSERT INTO negocio.ficha_empresa(organizacion_id, empresa_id, revision, nombre_comercial) VALUES ('{Org}', '{empresaGeneral}', 1, 'Cliente General Sanitarios Ficticio');
            INSERT INTO negocio.empresa_sucursal VALUES ('{Org}', '{Suc}', '{empresaGeneral}');
            INSERT INTO negocio.cliente VALUES ('{Org}', '{Suc}', '{general}', '{empresaGeneral}', 'G-{general:N}');
            INSERT INTO negocio.sitio VALUES ('{Org}', '{Suc}', '{sitioNormal}', 'Sitio normal ficticio');
            INSERT INTO negocio.punto_servicio VALUES ('{Org}', '{Suc}', '{sitioNormal}');
            COMMIT;
            """);
        var r = await EnviarAsync(nav, HttpMethod.Post, "/api/ordenes/extraordinarias",
            new { rutaId = ruta, fecha = Fechas.Lunes, sitioId = sitioNormal, clientes = new[] { new { clienteId = general, banos = 5 } } });
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        Assert.Equal("1", await _bd.ConsultarAsync($"SELECT count(*) FROM negocio.parada_extraordinaria WHERE ruta_id = '{ruta}'"));
    }
    [Fact]
    public async Task Extraordinaria_de_banos_en_una_ruta_de_fosas_con_unidad_de_sanitarios_ese_dia_se_acepta()
    {
        // El servicio de la salida lo define la unidad del día: si Operaciones cambió la unidad a una de sanitarios, se agregan baños (y no fosas).
        var (ruta, fosas, sanitarios, sitio, empresa, cliente) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        await _bd.SqlComoDuenoAsync($"""
            BEGIN;
            INSERT INTO negocio.empresa VALUES ('{Org}', '{empresa}', 'CLI-{empresa:N}');
            INSERT INTO negocio.ficha_empresa(organizacion_id, empresa_id, revision, nombre_comercial) VALUES ('{Org}', '{empresa}', 1, 'Cliente General Cambio Ficticio');
            INSERT INTO negocio.empresa_sucursal VALUES ('{Org}', '{Suc}', '{empresa}');
            INSERT INTO negocio.cliente VALUES ('{Org}', '{Suc}', '{cliente}', '{empresa}', 'G-{cliente:N}');
            INSERT INTO negocio.sitio VALUES ('{Org}', '{Suc}', '{sitio}', 'Sitio cambio ficticio');
            INSERT INTO negocio.punto_servicio VALUES ('{Org}', '{Suc}', '{sitio}');
            INSERT INTO negocio.vehiculo VALUES ('{Org}', '{Suc}', '{fosas}', 'FOS-{fosas.ToString("N")[..5]}');
            INSERT INTO negocio.ficha_vehiculo(organizacion_id, sucursal_id, vehiculo_id, revision, capacidad_id, clasificacion) VALUES ('{Org}', '{Suc}', '{fosas}', 1, 2, 'fosas');
            INSERT INTO negocio.vehiculo VALUES ('{Org}', '{Suc}', '{sanitarios}', 'SAN-{sanitarios.ToString("N")[..5]}');
            INSERT INTO negocio.ficha_vehiculo(organizacion_id, sucursal_id, vehiculo_id, revision, capacidad_id, clasificacion) VALUES ('{Org}', '{Suc}', '{sanitarios}', 1, 4, 'sanitarios');
            INSERT INTO negocio.ruta(organizacion_id, sucursal_id, id) VALUES ('{Org}', '{Suc}', '{ruta}');
            INSERT INTO negocio.ruta_version(organizacion_id, sucursal_id, ruta_id, revision, nombre, dias, vehiculo_id) VALUES ('{Org}', '{Suc}', '{ruta}', 1, 'Ruta Cambio Ficticia', 21, '{fosas}');
            INSERT INTO negocio.cambio_unidad_dia(organizacion_id, sucursal_id, ruta_id, fecha, revision, vehiculo_id) VALUES ('{Org}', '{Suc}', '{ruta}', '{Fechas.Lunes}', 1, '{sanitarios}');
            COMMIT;
            """);
        using var nav = await SesionAsync();
        var conFosa = await EnviarAsync(nav, HttpMethod.Post, "/api/ordenes/extraordinarias",
            new { rutaId = ruta, fecha = Fechas.Lunes, sitioId = sitio, clientes = new[] { new { clienteId = cliente, fosas = 1 } } });
        Assert.Equal(HttpStatusCode.BadRequest, conFosa.StatusCode);
        var conBanos = await EnviarAsync(nav, HttpMethod.Post, "/api/ordenes/extraordinarias",
            new { rutaId = ruta, fecha = Fechas.Lunes, sitioId = sitio, clientes = new[] { new { clienteId = cliente, banos = 5 } } });
        Assert.Equal(HttpStatusCode.Created, conBanos.StatusCode);
        // Y aparece de inmediato en las órdenes del día de esa ruta.
        var listada = Assert.Single((await nav.GetFromJsonAsync<List<RutaDeOrdenes>>($"/api/ordenes?fecha={Fechas.Lunes:yyyy-MM-dd}"))!, x => x.Id == ruta);
        Assert.Single(listada.Extraordinarias!);
    }
    [Fact]
    public async Task No_se_hara_hoy_marca_ruta_o_parada_solo_ese_dia_se_deshace_y_pide_permiso()
    {
        var (ruta, unidad, sitio, empresa, cliente) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        await _bd.SqlComoDuenoAsync($"""
            BEGIN;
            INSERT INTO negocio.empresa VALUES ('{Org}', '{empresa}', 'CLI-{empresa:N}');
            INSERT INTO negocio.ficha_empresa(organizacion_id, empresa_id, revision, nombre_comercial) VALUES ('{Org}', '{empresa}', 1, 'Cliente General No Se Hara');
            INSERT INTO negocio.empresa_sucursal VALUES ('{Org}', '{Suc}', '{empresa}');
            INSERT INTO negocio.cliente VALUES ('{Org}', '{Suc}', '{cliente}', '{empresa}', 'G-{cliente:N}');
            INSERT INTO negocio.sitio VALUES ('{Org}', '{Suc}', '{sitio}', 'Sitio no se hara');
            INSERT INTO negocio.punto_servicio VALUES ('{Org}', '{Suc}', '{sitio}');
            INSERT INTO negocio.vehiculo VALUES ('{Org}', '{Suc}', '{unidad}', 'NSH-{unidad.ToString("N")[..5]}');
            INSERT INTO negocio.ficha_vehiculo(organizacion_id, sucursal_id, vehiculo_id, revision, capacidad_id, clasificacion) VALUES ('{Org}', '{Suc}', '{unidad}', 1, 4, 'sanitarios');
            INSERT INTO negocio.ruta(organizacion_id, sucursal_id, id) VALUES ('{Org}', '{Suc}', '{ruta}');
            INSERT INTO negocio.ruta_version(organizacion_id, sucursal_id, ruta_id, revision, nombre, dias, vehiculo_id) VALUES ('{Org}', '{Suc}', '{ruta}', 1, 'Ruta No Se Hara Ficticia', 21, '{unidad}');
            COMMIT;
            """);
        using var nav = await SesionAsync();
        var dia = Fechas.Lunes;
        var creada = await EnviarAsync(nav, HttpMethod.Post, "/api/ordenes/extraordinarias",
            new { rutaId = ruta, fecha = dia, sitioId = sitio, clientes = new[] { new { clienteId = cliente, banos = 5 } } });
        Assert.Equal(HttpStatusCode.Created, creada.StatusCode);
        var parada = (await creada.Content.ReadFromJsonAsync<ExtraordinariaCreada>())!.Id;
        async Task<RutaDeOrdenes> LeerAsync() => Assert.Single((await nav.GetFromJsonAsync<List<RutaDeOrdenes>>($"/api/ordenes?fecha={dia:yyyy-MM-dd}"))!, x => x.Id == ruta);
        var antes = await LeerAsync();
        Assert.False(antes.NoSeHara); Assert.False(Assert.Single(antes.Extraordinarias!).NoSeHara);

        // Una parada: solo ella se marca; deshacer es otra revisión.
        Assert.Equal(HttpStatusCode.NoContent, (await EnviarAsync(nav, HttpMethod.Put, "/api/ordenes/no-se-hara", new { rutaId = ruta, fecha = dia, paradaId = parada, noSeHara = true })).StatusCode);
        var conParada = await LeerAsync();
        Assert.False(conParada.NoSeHara); Assert.True(Assert.Single(conParada.Extraordinarias!).NoSeHara);
        // La ruta completa.
        Assert.Equal(HttpStatusCode.NoContent, (await EnviarAsync(nav, HttpMethod.Put, "/api/ordenes/no-se-hara", new { rutaId = ruta, fecha = dia, noSeHara = true })).StatusCode);
        Assert.True((await LeerAsync()).NoSeHara);
        // Solo ese día: otro día de la ruta no se afecta.
        var otro = DateOnly.Parse(Fechas.Lunes).AddDays(2);
        var rutaOtro = Assert.Single((await nav.GetFromJsonAsync<List<RutaDeOrdenes>>($"/api/ordenes?fecha={otro:yyyy-MM-dd}"))!, x => x.Id == ruta);
        Assert.False(rutaOtro.NoSeHara);
        // Deshacer.
        Assert.Equal(HttpStatusCode.NoContent, (await EnviarAsync(nav, HttpMethod.Put, "/api/ordenes/no-se-hara", new { rutaId = ruta, fecha = dia, noSeHara = false })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await EnviarAsync(nav, HttpMethod.Put, "/api/ordenes/no-se-hara", new { rutaId = ruta, fecha = dia, paradaId = parada, noSeHara = false })).StatusCode);
        var final = await LeerAsync();
        Assert.False(final.NoSeHara); Assert.False(Assert.Single(final.Extraordinarias!).NoSeHara);
        Assert.Equal("4", await _bd.ConsultarAsync($"SELECT count(*) FROM negocio.no_se_hara_dia WHERE ruta_id = '{ruta}'"));   // historial: nada se borra
        // Validaciones: parada ajena, ruta inexistente, pasado y sin permiso.
        Assert.Equal(HttpStatusCode.BadRequest, (await EnviarAsync(nav, HttpMethod.Put, "/api/ordenes/no-se-hara", new { rutaId = ruta, fecha = dia, paradaId = Guid.NewGuid(), noSeHara = true })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await EnviarAsync(nav, HttpMethod.Put, "/api/ordenes/no-se-hara", new { rutaId = Guid.NewGuid(), fecha = dia, noSeHara = true })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await EnviarAsync(nav, HttpMethod.Put, "/api/ordenes/no-se-hara", new { rutaId = ruta, fecha = "2020-01-06", noSeHara = true })).StatusCode);
        using var lector = await SesionAsync(escribir: false);
        Assert.Equal(HttpStatusCode.Forbidden, (await EnviarAsync(lector, HttpMethod.Put, "/api/ordenes/no-se-hara", new { rutaId = ruta, fecha = dia, noSeHara = true })).StatusCode);
    }
    [Fact]
    public async Task Una_unidad_de_fosas_puede_hacer_banos_si_Operaciones_lo_decide_pero_no_con_un_cliente_de_convenio()
    {
        var (ruta, fosas, sitio, empGeneral, general, empAut, autorizado) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        await _bd.SqlComoDuenoAsync($"""
            BEGIN;
            INSERT INTO negocio.empresa VALUES ('{Org}', '{empGeneral}', 'CLI-{empGeneral:N}');
            INSERT INTO negocio.ficha_empresa(organizacion_id, empresa_id, revision, nombre_comercial) VALUES ('{Org}', '{empGeneral}', 1, 'Cliente General Mezcla');
            INSERT INTO negocio.empresa_sucursal VALUES ('{Org}', '{Suc}', '{empGeneral}');
            INSERT INTO negocio.cliente VALUES ('{Org}', '{Suc}', '{general}', '{empGeneral}', 'G-{general:N}');
            INSERT INTO negocio.empresa VALUES ('{Org}', '{empAut}', 'CLI-{empAut:N}');
            INSERT INTO negocio.ficha_empresa(organizacion_id, empresa_id, revision, nombre_comercial) VALUES ('{Org}', '{empAut}', 1, 'Cliente Convenio Mezcla');
            INSERT INTO negocio.empresa_sucursal VALUES ('{Org}', '{Suc}', '{empAut}');
            INSERT INTO negocio.cliente VALUES ('{Org}', '{Suc}', '{autorizado}', '{empAut}', 'A-{autorizado:N}');
            INSERT INTO negocio.autorizacion_empresa(organizacion_id, empresa_id, autoridad, finalidad, numero) VALUES ('{Org}', '{empAut}', 'Medio ambiente', 'Generador', 'AMB-MEZCLA');
            INSERT INTO negocio.sitio VALUES ('{Org}', '{Suc}', '{sitio}', 'Sitio mezcla');
            INSERT INTO negocio.punto_servicio VALUES ('{Org}', '{Suc}', '{sitio}');
            INSERT INTO negocio.vehiculo VALUES ('{Org}', '{Suc}', '{fosas}', 'MEZ-{fosas.ToString("N")[..5]}');
            INSERT INTO negocio.ficha_vehiculo(organizacion_id, sucursal_id, vehiculo_id, revision, capacidad_id, clasificacion) VALUES ('{Org}', '{Suc}', '{fosas}', 1, 2, 'fosas');
            INSERT INTO negocio.ruta(organizacion_id, sucursal_id, id) VALUES ('{Org}', '{Suc}', '{ruta}');
            INSERT INTO negocio.ruta_version(organizacion_id, sucursal_id, ruta_id, revision, nombre, dias, vehiculo_id) VALUES ('{Org}', '{Suc}', '{ruta}', 1, 'Ruta Mezcla Ficticia', 21, '{fosas}');
            COMMIT;
            """);
        using var nav = await SesionAsync();
        // Un cliente general con baños en una unidad de fosas: se acepta.
        Assert.Equal(HttpStatusCode.Created, (await EnviarAsync(nav, HttpMethod.Post, "/api/ordenes/extraordinarias",
            new { rutaId = ruta, fecha = Fechas.Lunes, sitioId = sitio, clientes = new[] { new { clienteId = general, banos = 8 } } })).StatusCode);
        // Un cliente con convenio no se mezcla con baños.
        Assert.Equal(HttpStatusCode.BadRequest, (await EnviarAsync(nav, HttpMethod.Post, "/api/ordenes/extraordinarias",
            new { rutaId = ruta, fecha = Fechas.Lunes, sitioId = sitio, clientes = new[] { new { clienteId = autorizado, banos = 8 } } })).StatusCode);
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
            INSERT INTO negocio.autorizacion_empresa(organizacion_id, empresa_id, autoridad, finalidad, numero) VALUES ('{Org}', '{empresa}', 'Medio ambiente', 'Generador', 'AMB-FICT-EXT');
            INSERT INTO negocio.cliente_sitio(organizacion_id, sucursal_id, cliente_id, sitio_id, revision) VALUES ('{Org}', '{Suc}', '{cliente}', '{sitio}', 1);
            INSERT INTO negocio.ruta(organizacion_id, sucursal_id, id) VALUES ('{Org}', '{Suc}', '{ruta}');
            INSERT INTO negocio.ruta_version(organizacion_id, sucursal_id, ruta_id, revision, nombre, dias, vehiculo_id) VALUES ('{Org}', '{Suc}', '{ruta}', 1, 'Ruta Extra Ficticia', 21, '{unidad}');
            INSERT INTO negocio.parada(organizacion_id, sucursal_id, id, ruta_id, posicion) VALUES ('{Org}', '{Suc}', '{parada}', '{ruta}', 1);
            INSERT INTO negocio.parada_version(organizacion_id, sucursal_id, id, parada_id, revision, sitio_id) VALUES ('{Org}', '{Suc}', '{version}', '{parada}', 1, '{sitio}');
            INSERT INTO negocio.parada_cliente(organizacion_id, sucursal_id, version_id, cliente_id, banos_planeados, fosas_planeadas) VALUES ('{Org}', '{Suc}', '{version}', '{cliente}', NULL, 1);
            COMMIT;
            """);
        using var nav = await SesionAsync();
        string lunes = Fechas.Lunes, martes = Fechas.Martes, miercoles = Fechas.Miercoles;
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
        // PG-37: el sitio ya está en la ruta ese día, así que otro cliente se fusiona con la parada existente (se permite).
        Assert.Equal(HttpStatusCode.Created, (await EnviarAsync(nav, HttpMethod.Post, "/api/ordenes/extraordinarias", new { rutaId = ruta, fecha = lunes, sitioId = sitio, clientes = new[] { new { clienteId = otro, fosas = 1 } } })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await EnviarAsync(nav, HttpMethod.Post, "/api/ordenes/extraordinarias", new { rutaId = ruta, fecha = lunes, sitioId = sitio, clientes = new[] { new { clienteId = cliente, banos = 0, fosas = 0 } } })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await EnviarAsync(nav, HttpMethod.Post, "/api/ordenes/extraordinarias", new { rutaId = Guid.NewGuid(), fecha = lunes, sitioId = sitio, clientes = new[] { new { clienteId = cliente, fosas = 1 } } })).StatusCode);
        using var lector = await SesionAsync(escribir: false);
        Assert.Equal(HttpStatusCode.Forbidden, (await EnviarAsync(lector, HttpMethod.Post, "/api/ordenes/extraordinarias", cuerpo)).StatusCode);
        using var anonimo = _api.NuevoNavegador();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonimo.PostAsJsonAsync("/api/ordenes/extraordinarias", cuerpo)).StatusCode);
    }
}
