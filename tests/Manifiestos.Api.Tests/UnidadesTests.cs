using System.Net;
using System.Net.Http.Json;
using Manifiestos.Api.Acceso;
using Manifiestos.Api.Rutas;
using Manifiestos.Api.Tests.Infraestructura;
using Manifiestos.Api.Unidades;

namespace Manifiestos.Api.Tests;

/// <summary>
/// Unidades (0012) contra PostgreSQL real: alta, corrección por revisión, número económico único por
/// sucursal, permisos y CSRF. Datos ficticios.
/// </summary>
[Trait("Categoria", "Integracion")]
public sealed class UnidadesTests : IClassFixture<BaseDatosPrueba>, IAsyncLifetime
{
    private static readonly Guid Org = BaseDatosPrueba.Organizacion;
    private static readonly Guid Suc = BaseDatosPrueba.Sucursal;
    private const string Empresa = "0f000000-0000-4000-8000-000000000002";
    private static readonly Guid Suc2 = Guid.Parse("0f000000-0000-4000-8000-0000000000a2");

    private readonly BaseDatosPrueba _bd;
    private ApiPrueba _api = null!;

    public UnidadesTests(BaseDatosPrueba bd) => _bd = bd;

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
        var correo = $"unidades-{Guid.NewGuid():N}@example.invalid";
        await _bd.AutorizarAsync(correo, escribir: escribir);
        var nav = _api.NuevoNavegador();
        Assert.Equal(HttpStatusCode.NoContent, (await nav.EntrarAsync(ValidadorFicticio.Token($"s-{correo}", correo))).StatusCode);
        return nav;
    }

    private static async Task<HttpResponseMessage> EnviarAsync(HttpClient nav, HttpMethod metodo, string url, object cuerpo, bool conCsrf = true)
    {
        var req = new HttpRequestMessage(metodo, url) { Content = JsonContent.Create(cuerpo) };
        if (conCsrf) req.Headers.Add(ServiciosAcceso.CabeceraCsrf, await nav.CsrfAsync());
        return await nav.SendAsync(req);
    }

    private static async Task<ListaUnidades> ListarAsync(HttpClient nav) =>
        (await nav.GetFromJsonAsync<ListaUnidades>("/api/unidades"))!;

    private static async Task<UnidadFicha> UnidadAsync(HttpClient nav, Guid id) =>
        Assert.Single((await ListarAsync(nav)).Unidades, x => x.Id == id);

    private static object Nueva(string numero, short capacidad = 3, string? clasificacion = "fosas") =>
        new { sucursalId = Suc, numeroEconomico = numero, placas = "PL-0001-A", marca = "Freightliner", tipo = "Pipa",
              capacidadId = capacidad, clasificacion, descripcion = "Pipa de succión ficticia" };

    private static object Edicion(int revision, string numero, short capacidad = 3, string? clasificacion = "fosas") =>
        new { numeroEconomico = numero, placas = "PL-0001-A", marca = "Freightliner", tipo = "Pipa", capacidadId = capacidad, clasificacion,
              descripcion = "Pipa de succión ficticia", revision };

    private static async Task<RevisionGuardada> CrearAsync(HttpClient nav, object cuerpo)
    {
        var r = await EnviarAsync(nav, HttpMethod.Post, "/api/unidades", cuerpo);
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        return (await r.Content.ReadFromJsonAsync<RevisionGuardada>())!;
    }

    private static async Task AfirmarErrorAsync(HttpResponseMessage r, HttpStatusCode codigo, string? mensaje = null)
    {
        Assert.Equal(codigo, r.StatusCode);
        var cuerpo = (await r.Content.ReadFromJsonAsync<MensajeError>())!;
        if (mensaje is null) Assert.False(string.IsNullOrWhiteSpace(cuerpo.Mensaje));
        else Assert.Equal(mensaje, cuerpo.Mensaje);
    }

    [Fact]
    public async Task Crear_exige_sesion_csrf_y_permiso_de_escritura_y_lista_el_catalogo()
    {
        using var nav = await SesionAsync(true);
        var numero = $"ARM-{Guid.NewGuid():N}"[..12];
        Assert.False((await EnviarAsync(nav, HttpMethod.Post, "/api/unidades", Nueva(numero), conCsrf: false)).IsSuccessStatusCode);
        var creada = await CrearAsync(nav, Nueva(numero));
        Assert.Equal(1, creada.Revision);

        var lista = await ListarAsync(nav);
        Assert.Equal([10m, 15m, 18m], lista.Capacidades.Select(c => c.ValorM3));
        var u = await UnidadAsync(nav, creada.Id);
        Assert.Equal(numero, u.NumeroEconomico);
        Assert.Equal(18m, u.CapacidadM3);
        Assert.Equal("fosas", u.Clasificacion);
        Assert.Null(u.Operador);
        Assert.Empty(u.Rutas);

        using var lector = await SesionAsync(false);
        await AfirmarErrorAsync(await EnviarAsync(lector, HttpMethod.Post, "/api/unidades", Nueva($"{numero}-L")), HttpStatusCode.Forbidden, EndpointsUnidades.MensajeSinPermiso);
        Assert.Contains(await ListarAsync(lector) is var l ? l.Unidades : [], x => x.Id == creada.Id);
        using var anonimo = _api.NuevoNavegador();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonimo.GetAsync("/api/unidades")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonimo.PostAsJsonAsync("/api/unidades", Nueva("X"))).StatusCode);
    }

    [Fact]
    public async Task Rechaza_datos_invalidos_y_numero_economico_repetido_en_la_sucursal()
    {
        using var nav = await SesionAsync(true);
        var numero = $"ARM-{Guid.NewGuid():N}"[..12];
        await AfirmarErrorAsync(await EnviarAsync(nav, HttpMethod.Post, "/api/unidades", Nueva("  ")), HttpStatusCode.BadRequest, EndpointsUnidades.MensajeSinNumero);
        await AfirmarErrorAsync(await EnviarAsync(nav, HttpMethod.Post, "/api/unidades", Nueva(numero, capacidad: 99)), HttpStatusCode.BadRequest, RepositorioUnidades.MensajeCapacidadInvalida);
        await AfirmarErrorAsync(await EnviarAsync(nav, HttpMethod.Post, "/api/unidades", Nueva(numero, clasificacion: "otra")), HttpStatusCode.BadRequest, EndpointsUnidades.MensajeClasificacion);
        await AfirmarErrorAsync(await EnviarAsync(nav, HttpMethod.Post, "/api/unidades", new { sucursalId = Suc, numeroEconomico = numero }), HttpStatusCode.BadRequest, EndpointsUnidades.MensajeSinCapacidad);
        await AfirmarErrorAsync(await EnviarAsync(nav, HttpMethod.Post, "/api/unidades", Nueva(new string('x', 201))), HttpStatusCode.BadRequest);
        var antes = (await ListarAsync(nav)).Unidades.Count;
        // El mismo número en otra sucursal sí se permite (no es visible desde esta sesión).
        await _bd.SqlComoDuenoAsync($"INSERT INTO negocio.vehiculo VALUES ('{Org}', '{Suc2}', gen_random_uuid(), '{numero}')");

        var primera = await CrearAsync(nav, Nueva(numero));
        await AfirmarErrorAsync(await EnviarAsync(nav, HttpMethod.Post, "/api/unidades", Nueva($" {numero.ToLowerInvariant()} ")), HttpStatusCode.BadRequest, RepositorioUnidades.MensajeNumeroRepetido);
        Assert.Equal(antes + 1, (await ListarAsync(nav)).Unidades.Count);

        var otra = await CrearAsync(nav, Nueva($"{numero}-B"));
        await AfirmarErrorAsync(await EnviarAsync(nav, HttpMethod.Put, $"/api/unidades/{otra.Id}", Edicion(1, numero)), HttpStatusCode.BadRequest, RepositorioUnidades.MensajeNumeroRepetido);
        Assert.Equal(1, (await UnidadAsync(nav, otra.Id)).Revision);
        Assert.Equal(1, (await UnidadAsync(nav, primera.Id)).Revision);
    }

    [Fact]
    public async Task Corregir_crea_una_revision_nueva_y_una_edicion_vieja_recibe_409()
    {
        using var nav = await SesionAsync(true);
        var numero = $"ARM-{Guid.NewGuid():N}"[..12];
        var creada = await CrearAsync(nav, Nueva(numero, capacidad: 1));
        var url = $"/api/unidades/{creada.Id}";

        // Sin cambios no se crea una revisión vacía.
        var igual = await (await EnviarAsync(nav, HttpMethod.Put, url, Edicion(1, numero, capacidad: 1))).Content.ReadFromJsonAsync<RevisionGuardada>();
        Assert.Equal(1, igual!.Revision);

        var r2 = await EnviarAsync(nav, HttpMethod.Put, url, Edicion(1, $"{numero}-X", capacidad: 2, clasificacion: "sanitarios"));
        Assert.Equal(HttpStatusCode.OK, r2.StatusCode);
        Assert.Equal(2, (await r2.Content.ReadFromJsonAsync<RevisionGuardada>())!.Revision);
        var u = await UnidadAsync(nav, creada.Id);
        Assert.Equal(($"{numero}-X", 15m, "sanitarios", 2), (u.NumeroEconomico, u.CapacidadM3, u.Clasificacion, u.Revision));

        // Quien editó sobre la revisión 1 no pisa la 2.
        await AfirmarErrorAsync(await EnviarAsync(nav, HttpMethod.Put, url, Edicion(1, "OTRO")), HttpStatusCode.Conflict, RepositorioUnidades.MensajeModificada);
        Assert.Equal($"{numero}-X", (await UnidadAsync(nav, creada.Id)).NumeroEconomico);
        // El historial conserva ambas fichas.
        Assert.Equal("2", await _bd.ConsultarAsync($"SELECT count(*) FROM negocio.ficha_vehiculo WHERE vehiculo_id = '{creada.Id}'"));

        await AfirmarErrorAsync(await EnviarAsync(nav, HttpMethod.Put, $"/api/unidades/{Guid.NewGuid()}", Edicion(1, "Z")), HttpStatusCode.NotFound, EndpointsUnidades.MensajeNoEncontrada);
        Assert.False((await EnviarAsync(nav, HttpMethod.Put, url, Edicion(2, "Z"), conCsrf: false)).IsSuccessStatusCode);

        using var lector = await SesionAsync(false);
        await AfirmarErrorAsync(await EnviarAsync(lector, HttpMethod.Put, url, Edicion(2, "Z")), HttpStatusCode.Forbidden, EndpointsUnidades.MensajeSinPermiso);
    }

    [Fact]
    public async Task Una_unidad_sin_ficha_conserva_su_numero_y_se_corrige_con_revision_cero()
    {
        var id = Guid.NewGuid();
        var numero = $"V-{Guid.NewGuid():N}"[..10];
        await _bd.SqlComoDuenoAsync($"INSERT INTO negocio.vehiculo VALUES ('{Org}', '{Suc}', '{id}', '{numero}')");
        using var nav = await SesionAsync(true);
        var u = await UnidadAsync(nav, id);
        Assert.Equal((0, numero, (short?)null), (u.Revision, u.NumeroEconomico, u.CapacidadId));

        var r = await EnviarAsync(nav, HttpMethod.Put, $"/api/unidades/{id}", Edicion(0, numero));
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal(1, (await UnidadAsync(nav, id)).Revision);
    }

    [Fact]
    public async Task Muestra_operador_y_ruta_vigentes_sin_modificar_la_asignacion()
    {
        using var nav = await SesionAsync(true);
        var numero = $"ARM-{Guid.NewGuid():N}"[..12];
        var creada = await CrearAsync(nav, Nueva(numero));
        var persona = Guid.NewGuid();
        var ruta = Guid.NewGuid();
        await _bd.SqlComoDuenoAsync($"""
            BEGIN;
            INSERT INTO seguridad.persona VALUES ('{Org}', '{persona}', 'Operador Ficticio Unidades');
            INSERT INTO negocio.asignacion_unidad(organizacion_id, sucursal_id, vehiculo_id, revision, persona_id)
            VALUES ('{Org}', '{Suc}', '{creada.Id}', 1, '{persona}');
            INSERT INTO negocio.ruta(organizacion_id, sucursal_id, id) VALUES ('{Org}', '{Suc}', '{ruta}');
            INSERT INTO negocio.ruta_version(organizacion_id, sucursal_id, ruta_id, revision, nombre, dias, vehiculo_id)
            VALUES ('{Org}', '{Suc}', '{ruta}', 1, 'R-Ficticia Norte', 1, '{creada.Id}');
            COMMIT;
            """);
        var u = await UnidadAsync(nav, creada.Id);
        Assert.Equal("Operador Ficticio Unidades", u.Operador?.Nombre);
        Assert.Equal("R-Ficticia Norte", Assert.Single(u.Rutas).Nombre);
        // Corregir la ficha no toca la asignación del operador.
        Assert.Equal(HttpStatusCode.OK, (await EnviarAsync(nav, HttpMethod.Put, $"/api/unidades/{creada.Id}", Edicion(1, numero, capacidad: 2))).StatusCode);
        Assert.Equal("1", await _bd.ConsultarAsync($"SELECT count(*) FROM negocio.asignacion_unidad WHERE vehiculo_id = '{creada.Id}'"));
        Assert.Equal("Operador Ficticio Unidades", (await UnidadAsync(nav, creada.Id)).Operador?.Nombre);
    }
}
