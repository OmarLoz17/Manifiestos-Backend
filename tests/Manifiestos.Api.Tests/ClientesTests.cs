using System.Net;
using System.Net.Http.Json;
using Manifiestos.Api.Acceso;
using Manifiestos.Api.Clientes;
using Manifiestos.Api.Rutas;
using Manifiestos.Api.Tests.Infraestructura;

namespace Manifiestos.Api.Tests;

/// <summary>
/// Clientes (0009) contra PostgreSQL real: autorizados (autorización 'Medio ambiente'/'Generador')
/// y generales, asignaciones a paradas, altas, correcciones por revisión y permisos. Datos ficticios.
/// </summary>
[Trait("Categoria", "Integracion")]
public sealed class ClientesTests : IClassFixture<BaseDatosPrueba>, IAsyncLifetime
{
    private static readonly Guid Org = BaseDatosPrueba.Organizacion;
    private static readonly Guid Suc = BaseDatosPrueba.Sucursal;
    private const string Empresa = "0f000000-0000-4000-8000-000000000002";
    private static readonly Guid Suc2 = Guid.Parse("0f000000-0000-4000-8000-0000000000a2");

    private readonly BaseDatosPrueba _bd;
    private ApiPrueba _api = null!;

    public ClientesTests(BaseDatosPrueba bd) => _bd = bd;

    public async Task InitializeAsync()
    {
        _api = new ApiPrueba(_bd);
        await _bd.SqlComoDuenoAsync($"""
            INSERT INTO negocio.sucursal(organizacion_id, id, empresa_id, codigo, nombre)
            VALUES ('{Org}', '{Suc2}', '{Empresa}', 'PRUEBA2', 'Otra sucursal ficticia') ON CONFLICT DO NOTHING;
            """);
    }

    public async Task DisposeAsync() => await _api.DisposeAsync();

    // ---------------- Apoyos ----------------

    private async Task<HttpClient> SesionAsync(bool escribir, string? soloLecturaEn = null)
    {
        var correo = $"clientes-{Guid.NewGuid():N}@example.invalid";
        await _bd.AutorizarAsync(correo, escribir: escribir);
        if (soloLecturaEn is not null)
            await _bd.SqlComoDuenoAsync($"""
                INSERT INTO seguridad.permiso_sucursal
                SELECT '{Org}', '{soloLecturaEn}', a.id, 'datos.leer' FROM seguridad.acceso_autorizado a WHERE a.correo_permitido = '{correo}';
                """);
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

    private static async Task<List<ClienteResumen>> ListarAsync(HttpClient nav) =>
        (await nav.GetFromJsonAsync<List<ClienteResumen>>("/api/clientes"))!;

    private static async Task<ClienteResumen> ClienteAsync(HttpClient nav, Guid id) =>
        Assert.Single(await ListarAsync(nav), x => x.Id == id);

    private static async Task<RevisionGuardada> CrearAsync(HttpClient nav, object cuerpo)
    {
        var r = await EnviarAsync(nav, HttpMethod.Post, "/api/clientes", cuerpo);
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

    /// <summary>Huella de todo lo que el módulo puede escribir.</summary>
    private Task<string> HuellaAsync() => _bd.ConsultarAsync("""
        SELECT (SELECT count(*) FROM negocio.empresa) || '|' || (SELECT count(*) FROM negocio.ficha_empresa) || '|' ||
               (SELECT count(*) FROM negocio.autorizacion_empresa) || '|' || (SELECT count(*) FROM negocio.empresa_sucursal) || '|' ||
               (SELECT count(*) FROM negocio.cliente)
        """.ReplaceLineEndings(" "));

    private static object Edicion(int revision, string nombreFiscal = "Editado Ficticio SA", string? autorizacion = null, string? correo = null) =>
        new { nombreFiscal, nombreComercial = (string?)null, autorizacion, correo, revision };

    // ---------------- Lectura ----------------

    [Fact]
    public async Task Sitios_se_guardan_independientes_de_la_ficha_con_permisos_y_csrf()
    {
        using var nav = await SesionAsync(true);
        var creado = await CrearAsync(nav, new { sucursalId = Suc, nombreFiscal = "Cliente de obra ficticio", calle = "Domicilio fiscal ficticio" });
        var url = $"/api/clientes/{creado.Id}/sitios";
        var cuerpo = new { sitios = new[] { new { nombre = "Obra sin catálogo", estado = "Estado manual", municipio = "Municipio manual", banos = 10, fosas = 1 } } };
        Assert.False((await EnviarAsync(nav, HttpMethod.Put, url, cuerpo, conCsrf: false)).IsSuccessStatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await EnviarAsync(nav, HttpMethod.Put, url, cuerpo)).StatusCode);
        var actual = await ClienteAsync(nav, creado.Id);
        Assert.Equal(creado.Revision, actual.Revision);
        Assert.Equal("Domicilio fiscal ficticio", actual.Calle);
        var sitio = Assert.Single(actual.Sitios!);
        Assert.Equal("Municipio manual", sitio.Municipio);
        Assert.Equal(10, sitio.Banos);
        Assert.Equal(1, sitio.Fosas);
        using var lector = await SesionAsync(false);
        Assert.Equal(HttpStatusCode.Forbidden, (await EnviarAsync(lector, HttpMethod.Put, url, cuerpo)).StatusCode);
        using var anonimo = _api.NuevoNavegador();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonimo.PutAsJsonAsync(url, cuerpo)).StatusCode);
        Assert.Single((await ClienteAsync(nav, creado.Id)).Sitios!);
    }

    [Fact]
    public async Task Sin_sesion_no_lista_ni_crea()
    {
        var nav = _api.NuevoNavegador();
        Assert.Equal(HttpStatusCode.Unauthorized, (await nav.GetAsync("/api/clientes")).StatusCode);
        var antes = await HuellaAsync();
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await EnviarAsync(nav, HttpMethod.Post, "/api/clientes", new { sucursalId = Suc, nombreFiscal = "Anónimo Ficticio" })).StatusCode);
        Assert.Equal(antes, await HuellaAsync());
    }

    [Fact]
    public async Task Lista_autorizados_y_generales_con_asignaciones_y_banos()
    {
        var p = Guid.NewGuid().ToString("N")[..8];
        Guid N() => Guid.NewGuid();
        var (eA, eG, eS, eO) = (N(), N(), N(), N());
        var (cA, cG, cS, cO) = (N(), N(), N(), N());
        var (veh, sitio, r1, r2) = (N(), N(), N(), N());
        var (p1, p2, p3, p4) = (N(), N(), N(), N());
        var (v11, v12, v21, v31, v32, v41) = (N(), N(), N(), N(), N(), N());

        // A: autorizado (dos autorizaciones; vale la última), ficha corregida (rev. 2).
        // G: general (solo otra autorización ajena a medio ambiente/generador). S: sin ficha. O: otra sucursal.
        // Ruta r1 renombrada; p1 corregida (A 5 → A 7 + G 3); p2 sin baños; p3 ya no incluye a G.
        await _bd.SqlComoDuenoAsync($"""
            BEGIN;
            INSERT INTO negocio.empresa VALUES ('{Org}', '{eA}', 'CLI-A-{p}'), ('{Org}', '{eG}', 'CLI-G-{p}'), ('{Org}', '{eS}', 'CLI-S-{p}'), ('{Org}', '{eO}', 'CLI-O-{p}');
            INSERT INTO negocio.ficha_empresa(organizacion_id, empresa_id, revision, nombre_fiscal, nombre_comercial, rfc,
                calle, numero_exterior, colonia, codigo_postal, municipio, estado, telefono, correo) VALUES
                ('{Org}', '{eA}', 1, 'Viejo {p} SA', NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL),
                ('{Org}', '{eA}', 2, 'Alfa Fiscal {p} SA', '{p} Alfa Comercial', 'XAXX010101000', 'Calle Ficticia', '10', 'Centro',
                 '00000', 'Municipio Ficticio', 'Estado Ficticio', '5550000000', 'alfa@example.invalid'),
                ('{Org}', '{eG}', 1, '{p} Gamma Fiscal SA', '  ', NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL),
                ('{Org}', '{eO}', 1, '{p} Otra Sucursal SA', NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL);
            INSERT INTO negocio.autorizacion_empresa(organizacion_id, empresa_id, autoridad, finalidad, numero, registrada_en) VALUES
                ('{Org}', '{eA}', 'Medio ambiente', 'Generador', 'MA-VIEJA', clock_timestamp() - interval '1 day'),
                ('{Org}', '{eA}', 'Medio ambiente', 'Generador', 'MA-{p}', clock_timestamp()),
                ('{Org}', '{eG}', 'Otra autoridad', 'Transportista', 'OT-{p}', clock_timestamp());
            INSERT INTO negocio.empresa_sucursal VALUES ('{Org}', '{Suc}', '{eA}'), ('{Org}', '{Suc}', '{eG}'), ('{Org}', '{Suc}', '{eS}'), ('{Org}', '{Suc2}', '{eO}');
            INSERT INTO negocio.cliente VALUES ('{Org}', '{Suc}', '{cA}', '{eA}', NULL), ('{Org}', '{Suc}', '{cG}', '{eG}', NULL),
                ('{Org}', '{Suc}', '{cS}', '{eS}', '{p} Código Sin Ficha'), ('{Org}', '{Suc2}', '{cO}', '{eO}', NULL);
            INSERT INTO negocio.vehiculo VALUES ('{Org}', '{Suc}', '{veh}', 'U-{p}');
            INSERT INTO negocio.sitio VALUES ('{Org}', '{Suc}', '{sitio}', 'Obra {p}');
            INSERT INTO negocio.punto_servicio(organizacion_id, sucursal_id, sitio_id) VALUES ('{Org}', '{Suc}', '{sitio}');
            INSERT INTO negocio.ruta(organizacion_id, sucursal_id, id) VALUES ('{Org}', '{Suc}', '{r1}'), ('{Org}', '{Suc}', '{r2}');
            INSERT INTO negocio.ruta_version(organizacion_id, sucursal_id, ruta_id, revision, nombre, dias, vehiculo_id) VALUES
                ('{Org}', '{Suc}', '{r1}', 1, 'Z nombre viejo {p}', 21, '{veh}'),
                ('{Org}', '{Suc}', '{r1}', 2, '{p} B Ruta', 21, '{veh}'),
                ('{Org}', '{Suc}', '{r2}', 1, '{p} A Ruta', 42, '{veh}');
            INSERT INTO negocio.parada(organizacion_id, sucursal_id, id, ruta_id, posicion) VALUES
                ('{Org}', '{Suc}', '{p1}', '{r1}', 1), ('{Org}', '{Suc}', '{p2}', '{r1}', 2), ('{Org}', '{Suc}', '{p3}', '{r1}', 3),
                ('{Org}', '{Suc}', '{p4}', '{r2}', 1);
            INSERT INTO negocio.parada_version(organizacion_id, sucursal_id, id, parada_id, revision, sitio_id) VALUES
                ('{Org}', '{Suc}', '{v11}', '{p1}', 1, '{sitio}'), ('{Org}', '{Suc}', '{v12}', '{p1}', 2, '{sitio}'),
                ('{Org}', '{Suc}', '{v21}', '{p2}', 1, '{sitio}'), ('{Org}', '{Suc}', '{v31}', '{p3}', 1, '{sitio}'),
                ('{Org}', '{Suc}', '{v32}', '{p3}', 2, '{sitio}'), ('{Org}', '{Suc}', '{v41}', '{p4}', 1, '{sitio}');
            INSERT INTO negocio.parada_cliente(organizacion_id, sucursal_id, version_id, cliente_id, banos_planeados) VALUES
                ('{Org}', '{Suc}', '{v11}', '{cA}', 5),
                ('{Org}', '{Suc}', '{v12}', '{cA}', 7), ('{Org}', '{Suc}', '{v12}', '{cG}', 3),
                ('{Org}', '{Suc}', '{v21}', '{cA}', NULL),
                ('{Org}', '{Suc}', '{v31}', '{cG}', 4), ('{Org}', '{Suc}', '{v32}', '{cA}', 1),
                ('{Org}', '{Suc}', '{v41}', '{cA}', 2);
            COMMIT;
            """);

        var nav = await SesionAsync(escribir: false);
        var lista = await ListarAsync(nav);

        var a = Assert.Single(lista, x => x.Id == cA);
        Assert.Equal((Suc, 2, $"{p} Alfa Comercial", $"MA-{p}"), (a.SucursalId, a.Revision, a.Nombre, a.Autorizacion));
        Assert.Equal(("Alfa Fiscal " + p + " SA", "XAXX010101000", "Calle Ficticia", "10", "Centro", "00000"),
            (a.NombreFiscal, a.Rfc, a.Calle, a.NumeroExterior, a.Colonia, a.CodigoPostal));
        Assert.Equal(("Municipio Ficticio", "Estado Ficticio", "5550000000", "alfa@example.invalid"), (a.Municipio, a.Estado, a.Telefono, a.Correo));
        // Orden: nombre de la ruta (última revisión), luego posición.
        Assert.Equal(
        [
            new AsignacionCliente(r2, $"{p} A Ruta", p4, 1, 2, SitioId:sitio),
            new AsignacionCliente(r1, $"{p} B Ruta", p1, 1, 7, SitioId:sitio),
            new AsignacionCliente(r1, $"{p} B Ruta", p2, 2, null, SitioId:sitio),
            new AsignacionCliente(r1, $"{p} B Ruta", p3, 3, 1, SitioId:sitio),
        ], a.Asignaciones);
        Assert.Equal(10, a.Banos);

        // General: la autorización de otra autoridad no lo vuelve autorizado; comercial en blanco → fiscal.
        var g = Assert.Single(lista, x => x.Id == cG);
        Assert.Equal((1, $"{p} Gamma Fiscal SA", (string?)null), (g.Revision, g.Nombre, g.Autorizacion));
        Assert.Equal([new AsignacionCliente(r1, $"{p} B Ruta", p1, 1, 3, SitioId:sitio)], g.Asignaciones);
        Assert.Equal(3, g.Banos);

        // Sin ficha: código comercial, revisión 0, sin asignaciones.
        var s = Assert.Single(lista, x => x.Id == cS);
        Assert.Equal((0, $"{p} Código Sin Ficha", (string?)null, (string?)null), (s.Revision, s.Nombre, s.NombreFiscal, s.Autorizacion));
        Assert.Empty(s.Asignaciones);
        Assert.Equal(0, s.Banos);

        // Otra sucursal no se ve; el orden es por nombre.
        Assert.DoesNotContain(lista, x => x.Id == cO);
        var propios = lista.Where(x => x.Nombre.StartsWith(p)).Select(x => x.Id).ToList();
        Assert.Equal([cA, cS, cG], propios);
    }

    // ---------------- Altas ----------------

    [Fact]
    public async Task Alta_autorizada_y_general()
    {
        var nav = await SesionAsync(escribir: true);
        var autorizado = await CrearAsync(nav, new
        {
            sucursalId = Suc, nombreFiscal = "  Autorizado Ficticio SA ", nombreComercial = "Autorizado Comercial Ficticio", rfc = "XAXX010101000",
            calle = "Avenida Inventada", numeroExterior = "5", colonia = "Centro", codigoPostal = "00000", municipio = "Municipio Ficticio",
            estado = "Estado Ficticio", telefono = "5550000001", correo = "autorizado@example.invalid", autorizacion = " MA-ALTA-1 ",
        });
        Assert.Equal(1, autorizado.Revision);
        var a = await ClienteAsync(nav, autorizado.Id);
        Assert.Equal((Suc, 1, "Autorizado Comercial Ficticio", "Autorizado Ficticio SA", "MA-ALTA-1"),
            (a.SucursalId, a.Revision, a.Nombre, a.NombreFiscal, a.Autorizacion));
        Assert.Equal(("Avenida Inventada", "5", "autorizado@example.invalid"), (a.Calle, a.NumeroExterior, a.Correo));
        Assert.Empty(a.Asignaciones);
        Assert.Equal("Medio ambiente|Generador|true", await _bd.ConsultarAsync(
            $"SELECT a.autoridad || '|' || a.finalidad || '|' || (a.registrada_por_acceso_id IS NOT NULL) FROM negocio.autorizacion_empresa a JOIN negocio.cliente c ON c.empresa_id = a.empresa_id WHERE c.id = '{autorizado.Id}'"));

        var general = await CrearAsync(nav, new { sucursalId = Suc, nombreComercial = "General Ficticio", autorizacion = "   " });
        var g = await ClienteAsync(nav, general.Id);
        Assert.Equal(("General Ficticio", (string?)null, (string?)null, (string?)null), (g.Nombre, g.NombreFiscal, g.Rfc, g.Autorizacion));
        Assert.Equal("0", await _bd.ConsultarAsync(
            $"SELECT count(*) FROM negocio.autorizacion_empresa a JOIN negocio.cliente c ON c.empresa_id = a.empresa_id WHERE c.id = '{general.Id}'"));
    }

    [Fact]
    public async Task Datos_invalidos_responden_400_sin_escribir()
    {
        var nav = await SesionAsync(escribir: true);
        var largo = new string('x', EndpointsClientes.LargoMaximoCampo + 1);
        var casos = new (string Caso, object Cuerpo)[]
        {
            ("sin sucursal", new { nombreFiscal = "Sin Sucursal Ficticia" }),
            ("sin nombre", new { sucursalId = Suc, nombreFiscal = "  ", nombreComercial = "", rfc = "XAXX010101000" }),
            ("campo largo", new { sucursalId = Suc, nombreFiscal = "Largo Ficticio", calle = largo }),
            ("nombre largo", new { sucursalId = Suc, nombreComercial = largo }),
            ("correo sin arroba", new { sucursalId = Suc, nombreFiscal = "Correo Ficticio", correo = "correo.example.invalid" }),
            ("correo sin punto", new { sucursalId = Suc, nombreFiscal = "Correo Ficticio", correo = "persona@localhost" }),
            ("correo con espacio", new { sucursalId = Suc, nombreFiscal = "Correo Ficticio", correo = "per sona@example.invalid" }),
        };
        var antes = await HuellaAsync();
        foreach (var (caso, cuerpo) in casos)
        {
            var r = await EnviarAsync(nav, HttpMethod.Post, "/api/clientes", cuerpo);
            Assert.True(r.StatusCode == HttpStatusCode.BadRequest, caso);
            await AfirmarErrorAsync(r, HttpStatusCode.BadRequest);
        }
        Assert.Equal(antes, await HuellaAsync());

        var creado = await CrearAsync(nav, new { sucursalId = Suc, nombreFiscal = "Para Validar Ficticio SA" });
        antes = await HuellaAsync();
        await AfirmarErrorAsync(await EnviarAsync(nav, HttpMethod.Put, $"/api/clientes/{creado.Id}", new { nombreFiscal = " ", revision = 1 }),
            HttpStatusCode.BadRequest, RepositorioClientes.MensajeSinNombre);
        await AfirmarErrorAsync(await EnviarAsync(nav, HttpMethod.Put, $"/api/clientes/{creado.Id}", Edicion(1, correo: "sin-arroba")),
            HttpStatusCode.BadRequest, EndpointsClientes.MensajeCorreoInvalido);
        await AfirmarErrorAsync(await EnviarAsync(nav, HttpMethod.Put, $"/api/clientes/{creado.Id}", new { nombreFiscal = "Sin Revisión SA" }),
            HttpStatusCode.BadRequest);
        Assert.Equal(antes, await HuellaAsync());
    }

    // ---------------- Correcciones ----------------

    [Fact]
    public async Task Edita_con_revision_vigente_y_rechaza_revision_vieja()
    {
        var nav = await SesionAsync(escribir: true);
        var c = await CrearAsync(nav, new { sucursalId = Suc, nombreFiscal = "Original Ficticio SA", telefono = "5550000002" });
        var url = $"/api/clientes/{c.Id}";

        // General → autorizado y corrección de datos en la misma revisión.
        var r = await EnviarAsync(nav, HttpMethod.Put, url, new
        {
            nombreFiscal = "Corregido Ficticio SA", nombreComercial = "Corregido Comercial", telefono = "5550000002",
            correo = "corregido@example.invalid", autorizacion = "MA-EDIT-1", revision = 1,
        });
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal(new RevisionGuardada(c.Id, 2), await r.Content.ReadFromJsonAsync<RevisionGuardada>());
        var editado = await ClienteAsync(nav, c.Id);
        Assert.Equal((2, "Corregido Comercial", "Corregido Ficticio SA", "corregido@example.invalid", "MA-EDIT-1"),
            (editado.Revision, editado.Nombre, editado.NombreFiscal, editado.Correo, editado.Autorizacion));

        // Alguien más ya guardó la revisión 2: editar sobre la 1 es conflicto y no escribe.
        var antes = await HuellaAsync();
        await AfirmarErrorAsync(await EnviarAsync(nav, HttpMethod.Put, url, Edicion(1, "Vieja Ficticia SA", "MA-EDIT-1")),
            HttpStatusCode.Conflict, RepositorioClientes.MensajeClienteModificado);
        Assert.Equal(antes, await HuellaAsync());

        // Solo cambia el número de autorización: autorización nueva, misma ficha; la vigente es la última.
        r = await EnviarAsync(nav, HttpMethod.Put, url, new
        {
            nombreFiscal = "Corregido Ficticio SA", nombreComercial = "Corregido Comercial", telefono = "5550000002",
            correo = "corregido@example.invalid", autorizacion = "MA-EDIT-2", revision = 2, autorizacionVista = " MA-EDIT-1 ",
        });
        Assert.Equal(new RevisionGuardada(c.Id, 2), await r.Content.ReadFromJsonAsync<RevisionGuardada>());
        Assert.Equal("MA-EDIT-2", (await ClienteAsync(nav, c.Id)).Autorizacion);
        Assert.Equal("2|2", await _bd.ConsultarAsync($"""
            SELECT (SELECT count(*) FROM negocio.ficha_empresa f WHERE f.empresa_id = c.empresa_id) || '|' ||
                   (SELECT count(*) FROM negocio.autorizacion_empresa a WHERE a.empresa_id = c.empresa_id)
            FROM negocio.cliente c WHERE c.id = '{c.Id}'
            """.ReplaceLineEndings(" ")));
        // La revisión 1 se conserva intacta.
        Assert.Equal("Original Ficticio SA", await _bd.ConsultarAsync(
            $"SELECT f.nombre_fiscal FROM negocio.ficha_empresa f JOIN negocio.cliente c ON c.empresa_id = f.empresa_id WHERE c.id = '{c.Id}' AND f.revision = 1"));
    }

    [Fact]
    public async Task Quitar_autorizacion_responde_400_sin_cambios()
    {
        var nav = await SesionAsync(escribir: true);
        var c = await CrearAsync(nav, new { sucursalId = Suc, nombreFiscal = "Con Autorización Ficticia SA", autorizacion = "MA-QUITAR" });
        var antes = await HuellaAsync();

        // Aunque también cambie el nombre, la corrección completa se rechaza.
        await AfirmarErrorAsync(await EnviarAsync(nav, HttpMethod.Put, $"/api/clientes/{c.Id}", Edicion(1, "Otro Nombre Ficticio SA", autorizacion: " ")),
            HttpStatusCode.BadRequest, RepositorioClientes.MensajeQuitarAutorizacion);

        Assert.Equal(antes, await HuellaAsync());
        var cliente = await ClienteAsync(nav, c.Id);
        Assert.Equal((1, "Con Autorización Ficticia SA", "MA-QUITAR"), (cliente.Revision, cliente.Nombre, cliente.Autorizacion));
    }

    [Fact]
    public async Task Cambio_ajeno_solo_de_autorizacion_responde_409_sin_cambios()
    {
        var nav = await SesionAsync(escribir: true);
        var c = await CrearAsync(nav, new { sucursalId = Suc, nombreFiscal = "Concurrente Ficticio SA", autorizacion = "MA-1" });
        var url = $"/api/clientes/{c.Id}";

        // A y B abren la revisión 1 con MA-1. A cambia solo la autorización: la ficha sigue en la revisión 1.
        var r = await EnviarAsync(nav, HttpMethod.Put, url, new
        {
            nombreFiscal = "Concurrente Ficticio SA", autorizacion = "MA-2", revision = 1, autorizacionVista = "MA-1",
        });
        Assert.Equal(new RevisionGuardada(c.Id, 1), await r.Content.ReadFromJsonAsync<RevisionGuardada>());

        // B guarda sobre lo que vio (MA-1): la revisión de la ficha coincide, pero la autorización cambió.
        var antes = await HuellaAsync();
        await AfirmarErrorAsync(await EnviarAsync(nav, HttpMethod.Put, url, new
        {
            nombreFiscal = "Concurrente Ficticio SA", telefono = "5550000009", autorizacion = "MA-1", revision = 1, autorizacionVista = "MA-1",
        }), HttpStatusCode.Conflict, RepositorioClientes.MensajeClienteModificado);
        Assert.Equal(antes, await HuellaAsync());
        var cliente = await ClienteAsync(nav, c.Id);
        Assert.Equal(("MA-2", (string?)null), (cliente.Autorizacion, cliente.Telefono));

        // Un cliente general se ve como "" : si alguien le registró autorización, también es conflicto.
        var g = await CrearAsync(nav, new { sucursalId = Suc, nombreFiscal = "General Concurrente SA" });
        Assert.Equal(HttpStatusCode.OK, (await EnviarAsync(nav, HttpMethod.Put, $"/api/clientes/{g.Id}", new
        {
            nombreFiscal = "General Concurrente SA", autorizacion = "MA-G", revision = 1, autorizacionVista = "",
        })).StatusCode);
        antes = await HuellaAsync();
        await AfirmarErrorAsync(await EnviarAsync(nav, HttpMethod.Put, $"/api/clientes/{g.Id}", new
        {
            nombreFiscal = "General Concurrente SA", autorizacion = "MA-OTRA", revision = 1, autorizacionVista = "",
        }), HttpStatusCode.Conflict, RepositorioClientes.MensajeClienteModificado);
        Assert.Equal(antes, await HuellaAsync());
    }

    [Fact]
    public async Task Cliente_sin_ficha_responde_400_claro_sin_escribir()
    {
        var (empresa, cliente) = (Guid.NewGuid(), Guid.NewGuid());
        await _bd.SqlComoDuenoAsync($"""
            INSERT INTO negocio.empresa VALUES ('{Org}', '{empresa}', 'CLI-{empresa:N}');
            INSERT INTO negocio.empresa_sucursal VALUES ('{Org}', '{Suc}', '{empresa}');
            INSERT INTO negocio.cliente VALUES ('{Org}', '{Suc}', '{cliente}', '{empresa}', 'Código Sin Ficha Ficticio');
            """);
        var nav = await SesionAsync(escribir: true);
        Assert.Equal(0, (await ClienteAsync(nav, cliente)).Revision);
        var antes = await HuellaAsync();

        await AfirmarErrorAsync(await EnviarAsync(nav, HttpMethod.Put, $"/api/clientes/{cliente}", Edicion(0, autorizacion: "MA-SF")),
            HttpStatusCode.BadRequest, RepositorioClientes.MensajeSinFicha);
        Assert.Equal(antes, await HuellaAsync());

        // Sin escritura sigue siendo 403.
        await AfirmarErrorAsync(await EnviarAsync(await SesionAsync(escribir: false), HttpMethod.Put, $"/api/clientes/{cliente}", Edicion(0)),
            HttpStatusCode.Forbidden, EndpointsClientes.MensajeSinPermiso);
        // Revisión 0 con ficha existente = el cliente cambió.
        var c = await CrearAsync(nav, new { sucursalId = Suc, nombreFiscal = "Con Ficha Ficticia SA" });
        antes = await HuellaAsync();
        await AfirmarErrorAsync(await EnviarAsync(nav, HttpMethod.Put, $"/api/clientes/{c.Id}", Edicion(0)),
            HttpStatusCode.Conflict, RepositorioClientes.MensajeClienteModificado);
        Assert.Equal(antes, await HuellaAsync());
    }

    [Fact]
    public async Task Empresa_compartida_exige_escritura_en_todas_sus_sucursales()
    {
        var (empresa, c1, c2) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        await _bd.SqlComoDuenoAsync($"""
            INSERT INTO negocio.empresa VALUES ('{Org}', '{empresa}', 'CLI-{empresa:N}');
            INSERT INTO negocio.ficha_empresa(organizacion_id, empresa_id, revision, nombre_fiscal) VALUES ('{Org}', '{empresa}', 1, 'Compartida Ficticia SA');
            INSERT INTO negocio.empresa_sucursal VALUES ('{Org}', '{Suc}', '{empresa}'), ('{Org}', '{Suc2}', '{empresa}');
            INSERT INTO negocio.cliente VALUES ('{Org}', '{Suc}', '{c1}', '{empresa}', NULL), ('{Org}', '{Suc2}', '{c2}', '{empresa}', NULL);
            """);
        // Escribe en Suc, solo lee en Suc2: no puede cambiar la ficha que también ve el cliente de Suc2.
        var nav = await SesionAsync(escribir: true, soloLecturaEn: Suc2.ToString());
        Assert.Single(await ListarAsync(nav), x => x.Id == c2);
        var antes = await HuellaAsync();
        await AfirmarErrorAsync(await EnviarAsync(nav, HttpMethod.Put, $"/api/clientes/{c1}", Edicion(1, "Cambiada Ficticia SA", "MA-COMP")),
            HttpStatusCode.Forbidden, EndpointsClientes.MensajeSinPermiso);
        Assert.Equal(antes, await HuellaAsync());
        Assert.Equal("Compartida Ficticia SA|1", await _bd.ConsultarAsync(
            $"SELECT string_agg(nombre_fiscal || '|' || revision, ',') FROM negocio.ficha_empresa WHERE empresa_id = '{empresa}'"));
    }

    // ---------------- Permisos, CSRF y no encontrados ----------------

    [Fact]
    public async Task Solo_lectura_recibe_403_y_no_cambia_nada()
    {
        var c = await CrearAsync(await SesionAsync(escribir: true), new { sucursalId = Suc, nombreFiscal = "Lectura Ficticia SA" });
        var nav = await SesionAsync(escribir: false);
        Assert.Single(await ListarAsync(nav), x => x.Id == c.Id);
        var antes = await HuellaAsync();

        await AfirmarErrorAsync(await EnviarAsync(nav, HttpMethod.Post, "/api/clientes", new { sucursalId = Suc, nombreFiscal = "No Debe Existir SA", autorizacion = "MA-NO" }),
            HttpStatusCode.Forbidden, EndpointsClientes.MensajeSinPermiso);
        await AfirmarErrorAsync(await EnviarAsync(nav, HttpMethod.Put, $"/api/clientes/{c.Id}", Edicion(1, autorizacion: "MA-NO")),
            HttpStatusCode.Forbidden, EndpointsClientes.MensajeSinPermiso);

        // Con escritura en su sucursal, pero no en otra.
        var escritor = await SesionAsync(escribir: true);
        await AfirmarErrorAsync(await EnviarAsync(escritor, HttpMethod.Post, "/api/clientes", new { sucursalId = Suc2, nombreFiscal = "Otra Sucursal SA" }),
            HttpStatusCode.Forbidden, EndpointsClientes.MensajeSinPermiso);

        Assert.Equal(antes, await HuellaAsync());
    }

    [Fact]
    public async Task Sin_token_CSRF_no_escribe()
    {
        var nav = await SesionAsync(escribir: true);
        var c = await CrearAsync(nav, new { sucursalId = Suc, nombreFiscal = "CSRF Ficticio SA" });
        var antes = await HuellaAsync();

        Assert.Equal(HttpStatusCode.BadRequest, (await EnviarAsync(nav, HttpMethod.Post, "/api/clientes",
            new { sucursalId = Suc, nombreFiscal = "Sin CSRF SA" }, conCsrf: false)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await EnviarAsync(nav, HttpMethod.Put, $"/api/clientes/{c.Id}",
            Edicion(1, autorizacion: "MA-CSRF"), conCsrf: false)).StatusCode);

        Assert.Equal(antes, await HuellaAsync());
    }

    [Fact]
    public async Task Cliente_de_otra_sucursal_o_inexistente_responde_404()
    {
        var (empresa, cliente) = (Guid.NewGuid(), Guid.NewGuid());
        await _bd.SqlComoDuenoAsync($"""
            INSERT INTO negocio.empresa VALUES ('{Org}', '{empresa}', 'CLI-{empresa:N}');
            INSERT INTO negocio.ficha_empresa(organizacion_id, empresa_id, revision, nombre_fiscal) VALUES ('{Org}', '{empresa}', 1, 'Ajeno Ficticio SA');
            INSERT INTO negocio.empresa_sucursal VALUES ('{Org}', '{Suc2}', '{empresa}');
            INSERT INTO negocio.cliente VALUES ('{Org}', '{Suc2}', '{cliente}', '{empresa}', NULL);
            """);
        var nav = await SesionAsync(escribir: true);
        var antes = await HuellaAsync();

        await AfirmarErrorAsync(await EnviarAsync(nav, HttpMethod.Put, $"/api/clientes/{cliente}", Edicion(1)),
            HttpStatusCode.NotFound, EndpointsClientes.MensajeNoEncontrado);
        await AfirmarErrorAsync(await EnviarAsync(nav, HttpMethod.Put, $"/api/clientes/{Guid.NewGuid()}", Edicion(1)),
            HttpStatusCode.NotFound, EndpointsClientes.MensajeNoEncontrado);

        Assert.Equal(antes, await HuellaAsync());
        Assert.Equal("Ajeno Ficticio SA|1", await _bd.ConsultarAsync(
            $"SELECT string_agg(nombre_fiscal || '|' || revision, ',') FROM negocio.ficha_empresa WHERE empresa_id = '{empresa}'"));
    }
}
