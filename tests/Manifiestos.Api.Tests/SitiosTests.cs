using System.Net;
using System.Net.Http.Json;
using Manifiestos.Api.Acceso;
using Manifiestos.Api.Clientes;
using Manifiestos.Api.Rutas;
using Manifiestos.Api.Sitios;
using Manifiestos.Api.Tests.Infraestructura;

namespace Manifiestos.Api.Tests;

/// <summary>
/// Sitios normales (migración 0029): puntos de servicio sin dueño donde van los clientes generales. Los sitios de un cliente
/// autorizado no aparecen aquí. Un sitio no se borra: se da de baja, y solo si no está en una ruta. Datos ficticios.
/// </summary>
[Trait("Categoria", "Integracion")]
public sealed class SitiosTests : IClassFixture<BaseDatosPrueba>, IAsyncLifetime
{
    private static readonly Guid Org = BaseDatosPrueba.Organizacion;
    private static readonly Guid Suc = BaseDatosPrueba.Sucursal;

    private readonly BaseDatosPrueba _bd;
    private ApiPrueba _api = null!;

    public SitiosTests(BaseDatosPrueba bd) => _bd = bd;
    public Task InitializeAsync() { _api = new ApiPrueba(_bd); return Task.CompletedTask; }
    public async Task DisposeAsync() => await _api.DisposeAsync();

    private async Task<HttpClient> SesionAsync(bool escribir = true, bool admin = true)
    {
        var correo = $"sitios-{Guid.NewGuid():N}@example.invalid";
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

    private static object Cuerpo(string nombre, decimal? lat = 25.6866m, decimal? lon = -100.3161m, int? revision = null) =>
        new { sucursalId = Suc, revision, nombre, calle = "Calle Ficticia", numeroExterior = "10", municipio = "Municipio Ficticio", latitud = lat, longitud = lon };

    private static async Task<Guid> CrearAsync(HttpClient nav, string nombre)
    {
        var r = await EnviarAsync(nav, HttpMethod.Post, "/api/sitios", Cuerpo(nombre));
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        return (await r.Content.ReadFromJsonAsync<RevisionGuardada>())!.Id;
    }

    private static async Task<SitioResumen?> BuscarAsync(HttpClient nav, Guid id) =>
        (await nav.GetFromJsonAsync<List<SitioResumen>>("/api/sitios"))!.SingleOrDefault(s => s.Id == id);

    [Fact]
    public async Task Crea_edita_con_revision_y_lista_el_sitio_normal()
    {
        using var nav = await SesionAsync();
        var nombre = $"Sitio {Guid.NewGuid():N}"[..14];
        var id = await CrearAsync(nav, nombre);
        var s = (await BuscarAsync(nav, id))!;
        Assert.Equal((nombre, 1, 25.6866m, -100.3161m), (s.Nombre, s.Revision, s.Latitud, s.Longitud));
        Assert.Empty(s.Rutas);

        var editado = await EnviarAsync(nav, HttpMethod.Put, $"/api/sitios/{id}", Cuerpo(nombre + " B", revision: 1));
        Assert.Equal(2, (await editado.Content.ReadFromJsonAsync<RevisionGuardada>())!.Revision);
        Assert.Equal((nombre + " B", 2), ((await BuscarAsync(nav, id))!.Nombre, (await BuscarAsync(nav, id))!.Revision));
        // Revisión vieja: otra persona ya lo cambió.
        Assert.Equal(HttpStatusCode.Conflict, (await EnviarAsync(nav, HttpMethod.Put, $"/api/sitios/{id}", Cuerpo("Otro", revision: 1))).StatusCode);
    }

    [Fact]
    public async Task Fotos_de_un_sitio_sin_capturas_es_una_lista_vacia_y_sin_permiso_o_inexistente_no_se_entrega()
    {
        using var nav = await SesionAsync();
        var id = await CrearAsync(nav, "Sitio sin fotos");
        Assert.Equal("[]", await nav.GetStringAsync($"/api/sitios/{id}/fotos"));
        Assert.Equal(HttpStatusCode.NotFound, (await nav.GetAsync($"/api/sitios/{Guid.NewGuid()}/fotos")).StatusCode);

        // Un sitio de una sucursal sin permiso no existe para esta cuenta.
        var (otraSucursal, ajeno) = (Guid.NewGuid(), Guid.NewGuid());
        await _bd.SqlComoDuenoAsync($"""
            INSERT INTO negocio.sucursal(organizacion_id, id, empresa_id, codigo, nombre)
            SELECT organizacion_id, '{otraSucursal}', empresa_id, 'AJENA-{otraSucursal:N}', 'Sucursal ajena' FROM negocio.sucursal WHERE id = '{Suc}';
            INSERT INTO negocio.sitio VALUES ('{Org}', '{otraSucursal}', '{ajeno}', 'Sitio de otra sucursal');
            """);
        Assert.Equal(HttpStatusCode.NotFound, (await nav.GetAsync($"/api/sitios/{ajeno}/fotos")).StatusCode);

        // Sin permiso de lectura no se llega a las fotos: sin sucursal no entra al panel y, si se le quita con la sesión abierta, la sesión deja de valer.
        var correo = $"sin-lectura-{Guid.NewGuid():N}@example.invalid";
        await _bd.AutorizarAsync(correo);
        using var sinPermiso = _api.NuevoNavegador();
        Assert.Equal(HttpStatusCode.NoContent, (await sinPermiso.EntrarAsync(ValidadorFicticio.Token($"s-{correo}", correo))).StatusCode);
        await _bd.SqlComoDuenoAsync($"DELETE FROM seguridad.permiso_sucursal WHERE acceso_id = (SELECT id FROM seguridad.acceso_autorizado WHERE correo_permitido = '{correo}')");
        Assert.Equal(HttpStatusCode.Unauthorized, (await sinPermiso.GetAsync($"/api/sitios/{id}/fotos")).StatusCode);
        using var anonimo = _api.NuevoNavegador();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonimo.GetAsync($"/api/sitios/{id}/fotos")).StatusCode);
    }

    [Fact]
    public async Task Valida_nombre_ubicacion_obligatoria_permisos_y_csrf()
    {
        using var nav = await SesionAsync();
        Assert.Equal(HttpStatusCode.BadRequest, (await EnviarAsync(nav, HttpMethod.Post, "/api/sitios", Cuerpo("Sin punto", lat: null, lon: null))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await EnviarAsync(nav, HttpMethod.Post, "/api/sitios", Cuerpo("Fuera de rango", lat: 95m))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await EnviarAsync(nav, HttpMethod.Post, "/api/sitios", new { sucursalId = Suc, latitud = 25m, longitud = -100m })).StatusCode);
        var sinCsrf = new HttpRequestMessage(HttpMethod.Post, "/api/sitios") { Content = JsonContent.Create(Cuerpo("Sin csrf")) };
        Assert.False((await nav.SendAsync(sinCsrf)).IsSuccessStatusCode);
        using var lector = await SesionAsync(escribir: false);
        Assert.Equal(HttpStatusCode.Forbidden, (await EnviarAsync(lector, HttpMethod.Post, "/api/sitios", Cuerpo("Solo lectura"))).StatusCode);
        using var anonimo = _api.NuevoNavegador();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonimo.GetAsync("/api/sitios")).StatusCode);
    }

    [Fact]
    public async Task Elimina_solo_sin_ruta_vigente_y_el_sitio_dado_de_baja_deja_de_listarse_ni_ofrecerse()
    {
        using var nav = await SesionAsync();
        var id = await CrearAsync(nav, "Sitio para baja");
        var unidad = Guid.NewGuid();
        await _bd.SqlComoDuenoAsync($"""
            INSERT INTO negocio.vehiculo VALUES ('{Org}', '{Suc}', '{unidad}', 'SB-{unidad.ToString("N")[..5]}');
            INSERT INTO negocio.ficha_vehiculo(organizacion_id, sucursal_id, vehiculo_id, revision, capacidad_id, numero_economico, clasificacion)
            VALUES ('{Org}', '{Suc}', '{unidad}', 1, 4, 'SB-{unidad.ToString("N")[..5]}', 'sanitarios');
            """);
        var ruta = (await (await EnviarAsync(nav, HttpMethod.Post, "/api/rutas", new { sucursalId = Suc, dias = new[] { 1, 3, 5 }, unidadId = unidad })).Content.ReadFromJsonAsync<RevisionGuardada>())!;
        var parada = (await (await EnviarAsync(nav, HttpMethod.Post, $"/api/rutas/{ruta.Id}/paradas", new { sitioId = id })).Content.ReadFromJsonAsync<RevisionGuardada>())!;
        Assert.Single((await BuscarAsync(nav, id))!.Rutas);

        // En una ruta vigente no se elimina.
        var enRuta = await EnviarAsync(nav, HttpMethod.Delete, $"/api/sitios/{id}");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, enRuta.StatusCode);
        Assert.Equal("sitio_en_ruta", (await enRuta.Content.ReadFromJsonAsync<ErrorNegocio>())!.Error);

        // Sin ruta vigente se da de baja (la historia se conserva) y deja de ofrecerse.
        Assert.Equal(HttpStatusCode.NoContent, (await EnviarAsync(nav, HttpMethod.Delete, $"/api/rutas/{ruta.Id}/paradas/{parada.Id}?revision=1")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await EnviarAsync(nav, HttpMethod.Delete, $"/api/sitios/{id}")).StatusCode);
        Assert.Null(await BuscarAsync(nav, id));
        Assert.Equal("1", await _bd.ConsultarAsync($"SELECT count(*) FROM negocio.sitio WHERE id = '{id}'"));
        var opciones = (await nav.GetFromJsonAsync<OpcionesRuta>($"/api/rutas/opciones?sucursalId={Suc}&servicio=sanitarios"))!;
        Assert.DoesNotContain(opciones.Sitios!, x => x.Id == id);
        Assert.Equal(HttpStatusCode.NotFound, (await EnviarAsync(nav, HttpMethod.Delete, $"/api/sitios/{id}")).StatusCode);
    }

    [Fact]
    public async Task Los_sitios_de_un_cliente_autorizado_son_suyos_y_no_aparecen_en_la_pestana_de_sitios()
    {
        using var nav = await SesionAsync();
        var r = await EnviarAsync(nav, HttpMethod.Post, "/api/clientes", new
        {
            sucursalId = Suc, nombreComercial = "Cliente Autorizado Sitios Ficticio", autorizacion = "AMB-FICT-SITIOS", telefono = "5550000099", correo = "fixture@example.invalid",
            sitios = new[] { new { nombre = "Dirección del autorizado", municipio = "Municipio Ficticio", latitud = 25.6m, longitud = -100.3m } },
        });
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        var cliente = (await r.Content.ReadFromJsonAsync<RevisionGuardada>())!.Id;
        var propio = Assert.Single(Assert.Single((await nav.GetFromJsonAsync<List<ClienteResumen>>("/api/clientes"))!, c => c.Id == cliente).Sitios!);
        Assert.Null(await BuscarAsync(nav, propio.SitioId));
        // No se edita ni se elimina desde aquí: es del cliente.
        Assert.Equal(HttpStatusCode.NotFound, (await EnviarAsync(nav, HttpMethod.Delete, $"/api/sitios/{propio.SitioId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await EnviarAsync(nav, HttpMethod.Put, $"/api/sitios/{propio.SitioId}", Cuerpo("X", revision: 1))).StatusCode);
        // En las opciones de fosas se ofrece con su dueño; en sanitarios no.
        var fosas = (await nav.GetFromJsonAsync<OpcionesRuta>($"/api/rutas/opciones?sucursalId={Suc}&servicio=fosas"))!;
        Assert.Equal(cliente, Assert.Single(fosas.Sitios!, x => x.Id == propio.SitioId).Propietario!.Id);
        var sanitarios = (await nav.GetFromJsonAsync<OpcionesRuta>($"/api/rutas/opciones?sucursalId={Suc}&servicio=sanitarios"))!;
        Assert.DoesNotContain(sanitarios.Sitios!, x => x.Id == propio.SitioId);
    }

    [Fact]
    public async Task El_dueno_autorizado_cuenta_solo_mientras_su_direccion_este_activa_y_si_se_desactiva_el_sitio_vuelve_a_ser_normal()
    {
        using var nav = await SesionAsync();
        var r = await EnviarAsync(nav, HttpMethod.Post, "/api/clientes", new
        {
            sucursalId = Suc, nombreComercial = "Cliente Autorizado Baja Ficticio", autorizacion = "AMB-FICT-BAJA", telefono = "5550000099", correo = "fixture@example.invalid",
            sitios = new[] { new { nombre = "Dirección que se desactiva", municipio = "Municipio Ficticio", latitud = 25.6m, longitud = -100.3m } },
        });
        var cliente = (await r.Content.ReadFromJsonAsync<RevisionGuardada>())!.Id;
        var propio = Assert.Single(Assert.Single((await nav.GetFromJsonAsync<List<ClienteResumen>>("/api/clientes"))!, c => c.Id == cliente).Sitios!);
        Assert.Null(await BuscarAsync(nav, propio.SitioId));

        // Última revisión inactiva: el autorizado deja de ser dueño y el sitio es normal (aparece en Clientes › Sitios y en sanitarios).
        await _bd.SqlComoDuenoAsync($"""
            INSERT INTO negocio.cliente_sitio(organizacion_id,sucursal_id,cliente_id,sitio_id,revision,activo)
            VALUES ('{Org}','{Suc}','{cliente}','{propio.SitioId}',{propio.Revision + 1},false);
            """);
        Assert.NotNull(await BuscarAsync(nav, propio.SitioId));
        var sanitarios = (await nav.GetFromJsonAsync<OpcionesRuta>($"/api/rutas/opciones?sucursalId={Suc}&servicio=sanitarios"))!;
        Assert.Null(Assert.Single(sanitarios.Sitios!, x => x.Id == propio.SitioId).Propietario);

        // Si se reactiva, vuelve a ser suyo.
        await _bd.SqlComoDuenoAsync($"""
            INSERT INTO negocio.cliente_sitio(organizacion_id,sucursal_id,cliente_id,sitio_id,revision,activo)
            VALUES ('{Org}','{Suc}','{cliente}','{propio.SitioId}',{propio.Revision + 2},true);
            """);
        Assert.Null(await BuscarAsync(nav, propio.SitioId));
    }

    [Fact]
    public async Task Un_cliente_general_no_tiene_direcciones_propias()
    {
        using var nav = await SesionAsync();
        var sinAutorizacion = await EnviarAsync(nav, HttpMethod.Post, "/api/clientes", new
        {
            sucursalId = Suc, nombreComercial = "Cliente General Ficticio",
            sitios = new[] { new { nombre = "No debe guardarse", municipio = "Municipio Ficticio" } },
        });
        Assert.Equal(HttpStatusCode.BadRequest, sinAutorizacion.StatusCode);
        var general = await EnviarAsync(nav, HttpMethod.Post, "/api/clientes", new { sucursalId = Suc, nombreComercial = "Cliente General Ficticio" });
        Assert.Equal(HttpStatusCode.Created, general.StatusCode);
        var id = (await general.Content.ReadFromJsonAsync<RevisionGuardada>())!.Id;
        var guardar = await EnviarAsync(nav, HttpMethod.Put, $"/api/clientes/{id}/sitios", new { sitios = new[] { new { nombre = "Tampoco", municipio = "Municipio Ficticio" } } });
        Assert.Equal(HttpStatusCode.BadRequest, guardar.StatusCode);
        Assert.Empty(Assert.Single((await nav.GetFromJsonAsync<List<ClienteResumen>>("/api/clientes"))!, c => c.Id == id).Sitios!);
    }

    [Fact]
    public async Task Editar_un_cliente_general_no_le_crea_direcciones_pero_el_autorizado_si_puede_recibirlas()
    {
        using var nav = await SesionAsync();
        var creado = await EnviarAsync(nav, HttpMethod.Post, "/api/clientes", new { sucursalId = Suc, nombreComercial = "General Editable Ficticio" });
        var id = (await creado.Content.ReadFromJsonAsync<RevisionGuardada>())!.Id;
        var cliente = Assert.Single((await nav.GetFromJsonAsync<List<ClienteResumen>>("/api/clientes"))!, c => c.Id == id);
        var sitios = new[] { new { nombre = "Direccion no permitida", municipio = "Municipio Ficticio", latitud = 25.6m, longitud = -100.3m } };
        Assert.Equal(HttpStatusCode.BadRequest, (await EnviarAsync(nav, HttpMethod.Put, $"/api/clientes/{id}", new { nombreComercial = "General Editable Ficticio", revision = cliente.Revision, sitios })).StatusCode);
        Assert.Empty(Assert.Single((await nav.GetFromJsonAsync<List<ClienteResumen>>("/api/clientes"))!, c => c.Id == id).Sitios!);
        // Si la misma solicitud le da autorización, ya es autorizado y sí puede tener direcciones.
        var autorizado = await EnviarAsync(nav, HttpMethod.Put, $"/api/clientes/{id}", new { nombreComercial = "General Editable Ficticio", autorizacion = "AMB-FICT-EDIT", telefono = "5550000099", correo = "fixture@example.invalid", revision = cliente.Revision, sitios });
        Assert.Equal(HttpStatusCode.OK, autorizado.StatusCode);
        Assert.Single(Assert.Single((await nav.GetFromJsonAsync<List<ClienteResumen>>("/api/clientes"))!, c => c.Id == id).Sitios!);
    }

    [Fact]
    public async Task La_direccion_de_un_autorizado_no_se_elimina_si_su_parada_esta_en_una_ruta_aunque_no_tenga_clientes()
    {
        using var nav = await SesionAsync();
        var alta = await EnviarAsync(nav, HttpMethod.Post, "/api/clientes", new
        {
            sucursalId = Suc, nombreComercial = "Autorizado Con Parada Ficticio", autorizacion = "AMB-FICT-PARADA", telefono = "5550000099", correo = "fixture@example.invalid",
            sitios = new[] { new { nombre = "Direccion con parada", municipio = "Municipio Ficticio", latitud = 25.6m, longitud = -100.3m } },
        });
        var id = (await alta.Content.ReadFromJsonAsync<RevisionGuardada>())!.Id;
        var cliente = Assert.Single((await nav.GetFromJsonAsync<List<ClienteResumen>>("/api/clientes"))!, c => c.Id == id);
        var sitio = Assert.Single(cliente.Sitios!);
        var unidad = Guid.NewGuid();
        await _bd.SqlComoDuenoAsync($"""
            INSERT INTO negocio.vehiculo VALUES ('{Org}', '{Suc}', '{unidad}', 'SF-{unidad.ToString("N")[..5]}');
            INSERT INTO negocio.ficha_vehiculo(organizacion_id, sucursal_id, vehiculo_id, revision, capacidad_id, numero_economico, clasificacion)
            VALUES ('{Org}', '{Suc}', '{unidad}', 1, 3, 'SF-{unidad.ToString("N")[..5]}', 'fosas');
            """);
        var ruta = (await (await EnviarAsync(nav, HttpMethod.Post, "/api/rutas", new { sucursalId = Suc, dias = new[] { 1, 3, 5 }, unidadId = unidad })).Content.ReadFromJsonAsync<RevisionGuardada>())!;
        // La parada es el sitio: existe sin clientes y aun así la dirección está en uso.
        Assert.Equal(HttpStatusCode.Created, (await EnviarAsync(nav, HttpMethod.Post, $"/api/rutas/{ruta.Id}/paradas", new { sitioId = sitio.SitioId })).StatusCode);
        Assert.True(Assert.Single((await nav.GetFromJsonAsync<List<ClienteResumen>>("/api/clientes"))!, c => c.Id == id).Sitios!.Single().EnRuta);
        var eliminar = await EnviarAsync(nav, HttpMethod.Put, $"/api/clientes/{id}", new
        {
            nombreComercial = "Autorizado Con Parada Ficticio", autorizacion = "AMB-FICT-PARADA", telefono = "5550000099", correo = "fixture@example.invalid", revision = cliente.Revision,
            sitios = new[] { new { sitio.SitioId, sitio.Revision, sitio.RevisionFicha, eliminar = true } },
        });
        Assert.Equal(HttpStatusCode.BadRequest, eliminar.StatusCode);
    }

    [Fact]
    public async Task Una_parada_extraordinaria_no_acepta_una_PTAR_ni_un_sitio_dado_de_baja_para_un_cliente_general()
    {
        using var nav = await SesionAsync();
        var (unidad, empresa, cliente, ptar) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        await _bd.SqlComoDuenoAsync($"""
            INSERT INTO negocio.vehiculo VALUES ('{Org}', '{Suc}', '{unidad}', 'EX-{unidad.ToString("N")[..5]}');
            INSERT INTO negocio.ficha_vehiculo(organizacion_id, sucursal_id, vehiculo_id, revision, capacidad_id, numero_economico, clasificacion)
            VALUES ('{Org}', '{Suc}', '{unidad}', 1, 3, 'EX-{unidad.ToString("N")[..5]}', 'fosas');
            INSERT INTO negocio.empresa VALUES ('{Org}', '{empresa}', 'E-{empresa.ToString("N")[..8]}');
            INSERT INTO negocio.ficha_empresa(organizacion_id, empresa_id, revision, nombre_comercial) VALUES ('{Org}', '{empresa}', 1, 'General Extraordinario Ficticio');
            INSERT INTO negocio.empresa_sucursal VALUES ('{Org}', '{Suc}', '{empresa}');
            INSERT INTO negocio.cliente VALUES ('{Org}', '{Suc}', '{cliente}', '{empresa}', 'G-{cliente.ToString("N")[..8]}');
            INSERT INTO negocio.sitio VALUES ('{Org}', '{Suc}', '{ptar}', 'PTAR ficticia');
            """);
        var ruta = (await (await EnviarAsync(nav, HttpMethod.Post, "/api/rutas", new { sucursalId = Suc, dias = new[] { 1, 3, 5 }, unidadId = unidad })).Content.ReadFromJsonAsync<RevisionGuardada>())!;
        var lunes = DateTime.UtcNow.Date.AddDays(1);
        while (lunes.DayOfWeek != DayOfWeek.Monday) lunes = lunes.AddDays(1);
        object Cuerpo(Guid sitio) => new { rutaId = ruta.Id, fecha = lunes.ToString("yyyy-MM-dd"), sitioId = sitio, clientes = new[] { new { clienteId = cliente, banos = 0, fosas = 1 } } };
        // Una PTAR (sitio de descarga, no punto de servicio) nunca es parada.
        Assert.Equal(HttpStatusCode.BadRequest, (await EnviarAsync(nav, HttpMethod.Post, "/api/ordenes/extraordinarias", Cuerpo(ptar))).StatusCode);
        // Un sitio normal vigente sí; con una extraordinaria de hoy en adelante no se elimina.
        var normal = await CrearAsync(nav, "Sitio extraordinario");
        Assert.Equal(HttpStatusCode.Created, (await EnviarAsync(nav, HttpMethod.Post, "/api/ordenes/extraordinarias", Cuerpo(normal))).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await EnviarAsync(nav, HttpMethod.Delete, $"/api/sitios/{normal}")).StatusCode);
        // Un sitio dado de baja ya no se ofrece como parada extraordinaria.
        var otro = await CrearAsync(nav, "Sitio dado de baja");
        Assert.Equal(HttpStatusCode.NoContent, (await EnviarAsync(nav, HttpMethod.Delete, $"/api/sitios/{otro}")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await EnviarAsync(nav, HttpMethod.Post, "/api/ordenes/extraordinarias", Cuerpo(otro))).StatusCode);
    }
}
