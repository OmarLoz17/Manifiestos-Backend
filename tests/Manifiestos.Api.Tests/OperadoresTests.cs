using System.Net;
using System.Net.Http.Json;
using Manifiestos.Api.Acceso;
using Manifiestos.Api.Operadores;
using Manifiestos.Api.Rutas;
using Manifiestos.Api.Tests.Infraestructura;

namespace Manifiestos.Api.Tests;

/// <summary>
/// Operadores (0013) contra PostgreSQL real: alta y corrección por revisión, personas conocidas solo por
/// una unidad, aislamiento por sucursal, permisos y CSRF. Datos ficticios.
/// </summary>
[Trait("Categoria", "Integracion")]
public sealed class OperadoresTests : IClassFixture<BaseDatosPrueba>, IAsyncLifetime
{
    private static readonly Guid Org = BaseDatosPrueba.Organizacion;
    private static readonly Guid Suc = BaseDatosPrueba.Sucursal;
    private const string Empresa = "0f000000-0000-4000-8000-000000000002";
    private static readonly Guid Suc2 = Guid.Parse("0f000000-0000-4000-8000-0000000000a2");

    private readonly BaseDatosPrueba _bd;
    private ApiPrueba _api = null!;

    public OperadoresTests(BaseDatosPrueba bd) => _bd = bd;

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
        var correo = $"operadores-{Guid.NewGuid():N}@example.invalid";
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

    private static async Task<List<OperadorFicha>> ListarAsync(HttpClient nav) =>
        (await nav.GetFromJsonAsync<List<OperadorFicha>>("/api/operadores"))!;

    private static async Task<OperadorFicha> OperadorAsync(HttpClient nav, Guid id) =>
        Assert.Single(await ListarAsync(nav), x => x.Id == id);

    private static object Nuevo(string nombre, string? cargo = "Chofer - Operador", string? correo = null) => new { sucursalId = Suc, nombre, cargo, correo };
    private static object Edicion(int revision, string nombre, string? cargo = "Chofer - Operador", string? correo = null) => new { sucursalId = Suc, nombre, cargo, correo, revision };
    private static string Cuenta() => $"telefono-{Guid.NewGuid():N}@example.invalid";
    private Task<string> EstadoCuentaAsync(string correo) => _bd.ConsultarAsync(
        $"SELECT coalesce(string_agg(rol_codigo || ':' || activo, ','), 'sin entrada') FROM seguridad.acceso_autorizado WHERE correo_permitido = '{correo}'");
    /// <summary>El inicio de sesión web de la cuenta debe ser rechazado: es un teléfono, no Operaciones.</summary>
    private async Task AfirmarSinPanelAsync(string correo)
    {
        using var nav = _api.NuevoNavegador();
        Assert.Equal(HttpStatusCode.Forbidden, (await nav.EntrarAsync(ValidadorFicticio.Token($"s-{correo}", correo))).StatusCode);
    }

    private static async Task<RevisionGuardada> CrearAsync(HttpClient nav, object cuerpo)
    {
        var r = await EnviarAsync(nav, HttpMethod.Post, "/api/operadores", cuerpo);
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
    public async Task Crear_exige_sesion_csrf_y_permiso_y_el_operador_nuevo_se_ve_aunque_no_tenga_unidad()
    {
        using var nav = await SesionAsync(true);
        var nombre = $"Operador Ficticio {Guid.NewGuid():N}"[..28];
        Assert.False((await EnviarAsync(nav, HttpMethod.Post, "/api/operadores", Nuevo(nombre), conCsrf: false)).IsSuccessStatusCode);
        var creado = await CrearAsync(nav, Nuevo($"  {nombre}  "));
        Assert.Equal(1, creado.Revision);

        var o = await OperadorAsync(nav, creado.Id);
        Assert.Equal((nombre, "Chofer - Operador", 1, Suc), (o.Nombre, o.Cargo, o.Revision, o.SucursalId));
        Assert.Empty(o.Unidades);

        using var lector = await SesionAsync(false);
        await AfirmarErrorAsync(await EnviarAsync(lector, HttpMethod.Post, "/api/operadores", Nuevo($"{nombre} L")), HttpStatusCode.Forbidden, EndpointsOperadores.MensajeSinPermiso);
        Assert.Equal(nombre, (await OperadorAsync(lector, creado.Id)).Nombre);
        using var anonimo = _api.NuevoNavegador();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonimo.GetAsync("/api/operadores")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonimo.PostAsJsonAsync("/api/operadores", Nuevo("X"))).StatusCode);
    }

    [Fact]
    public async Task Rechaza_datos_invalidos_y_sucursal_inexistente_sin_crear_nada()
    {
        using var nav = await SesionAsync(true);
        var antes = await _bd.ConsultarAsync("SELECT count(*) FROM seguridad.persona");
        await AfirmarErrorAsync(await EnviarAsync(nav, HttpMethod.Post, "/api/operadores", Nuevo("   ")), HttpStatusCode.BadRequest, RepositorioOperadores.MensajeSinNombre);
        await AfirmarErrorAsync(await EnviarAsync(nav, HttpMethod.Post, "/api/operadores", Nuevo(new string('x', 201))), HttpStatusCode.BadRequest);
        await AfirmarErrorAsync(await EnviarAsync(nav, HttpMethod.Post, "/api/operadores", new { nombre = "Sin sucursal" }), HttpStatusCode.BadRequest, "Falta la sucursal.");
        var r = await EnviarAsync(nav, HttpMethod.Post, "/api/operadores", new { sucursalId = Guid.NewGuid(), nombre = "Sucursal inexistente" });
        Assert.NotEqual(HttpStatusCode.Created, r.StatusCode);
        Assert.Equal(antes, await _bd.ConsultarAsync("SELECT count(*) FROM seguridad.persona"));
    }

    [Fact]
    public async Task Corregir_crea_una_revision_nueva_conserva_el_nombre_original_y_una_edicion_vieja_recibe_409()
    {
        using var nav = await SesionAsync(true);
        var nombre = $"Operador Ficticio {Guid.NewGuid():N}"[..28];
        var creado = await CrearAsync(nav, Nuevo(nombre, cargo: null));
        var url = $"/api/operadores/{creado.Id}";
        Assert.Null((await OperadorAsync(nav, creado.Id)).Cargo);

        // Sin cambios no se crea una revisión vacía.
        var igual = await (await EnviarAsync(nav, HttpMethod.Put, url, Edicion(1, nombre, cargo: null))).Content.ReadFromJsonAsync<RevisionGuardada>();
        Assert.Equal(1, igual!.Revision);

        var r2 = await EnviarAsync(nav, HttpMethod.Put, url, Edicion(1, $"{nombre} Corregido", "Chofer"));
        Assert.Equal(HttpStatusCode.OK, r2.StatusCode);
        Assert.Equal(2, (await r2.Content.ReadFromJsonAsync<RevisionGuardada>())!.Revision);
        var o = await OperadorAsync(nav, creado.Id);
        Assert.Equal(($"{nombre} Corregido", "Chofer", 2), (o.Nombre, o.Cargo, o.Revision));

        // Quien editó sobre la revisión 1 no pisa la 2.
        await AfirmarErrorAsync(await EnviarAsync(nav, HttpMethod.Put, url, Edicion(1, "OTRO")), HttpStatusCode.Conflict, RepositorioOperadores.MensajeModificado);
        Assert.Equal($"{nombre} Corregido", (await OperadorAsync(nav, creado.Id)).Nombre);
        // El historial conserva ambas fichas y el nombre original de la persona.
        Assert.Equal("2", await _bd.ConsultarAsync($"SELECT count(*) FROM negocio.operador_ficha WHERE persona_id = '{creado.Id}'"));
        Assert.Equal(nombre, await _bd.ConsultarAsync($"SELECT nombre_completo FROM seguridad.persona WHERE id = '{creado.Id}'"));

        await AfirmarErrorAsync(await EnviarAsync(nav, HttpMethod.Put, url, Edicion(2, "   ")), HttpStatusCode.BadRequest, RepositorioOperadores.MensajeSinNombre);
        await AfirmarErrorAsync(await EnviarAsync(nav, HttpMethod.Put, $"/api/operadores/{Guid.NewGuid()}", Edicion(1, "Z")), HttpStatusCode.NotFound, EndpointsOperadores.MensajeNoEncontrado);
        Assert.False((await EnviarAsync(nav, HttpMethod.Put, url, Edicion(2, "Z"), conCsrf: false)).IsSuccessStatusCode);
        using var lector = await SesionAsync(false);
        await AfirmarErrorAsync(await EnviarAsync(lector, HttpMethod.Put, url, Edicion(2, "Z")), HttpStatusCode.Forbidden, EndpointsOperadores.MensajeSinPermiso);
    }

    [Fact]
    public async Task Una_persona_conocida_solo_por_su_unidad_aparece_sin_ficha_y_se_corrige_con_revision_cero()
    {
        var persona = Guid.NewGuid();
        var unidad = Guid.NewGuid();
        var nombre = $"Operador Ficticio {Guid.NewGuid():N}"[..28];
        await _bd.SqlComoDuenoAsync($"""
            BEGIN;
            INSERT INTO seguridad.persona VALUES ('{Org}', '{persona}', '{nombre}');
            INSERT INTO negocio.vehiculo VALUES ('{Org}', '{Suc}', '{unidad}', 'OP-{unidad.ToString("N")[..6]}');
            INSERT INTO negocio.asignacion_unidad(organizacion_id, sucursal_id, vehiculo_id, revision, persona_id)
            VALUES ('{Org}', '{Suc}', '{unidad}', 1, '{persona}');
            COMMIT;
            """);
        using var nav = await SesionAsync(true);
        var o = await OperadorAsync(nav, persona);
        Assert.Equal((0, nombre, null), (o.Revision, o.Nombre, o.Cargo));
        Assert.Equal($"OP-{unidad.ToString("N")[..6]}", Assert.Single(o.Unidades).NumeroEconomico);

        var r = await EnviarAsync(nav, HttpMethod.Put, $"/api/operadores/{persona}", Edicion(0, nombre, "Chofer - Operador"));
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var despues = await OperadorAsync(nav, persona);
        Assert.Equal((1, "Chofer - Operador"), (despues.Revision, despues.Cargo));
        // Su unidad y su asignación no cambian.
        Assert.Single(despues.Unidades);
        Assert.Equal("1", await _bd.ConsultarAsync($"SELECT count(*) FROM negocio.asignacion_unidad WHERE persona_id = '{persona}'"));
    }

    [Fact]
    public async Task No_ve_ni_corrige_personas_de_otra_sucursal_o_sin_relacion_con_la_sucursal()
    {
        var ajena = Guid.NewGuid();
        var nombre = $"Operador Ajeno {Guid.NewGuid():N}"[..26];
        await _bd.SqlComoDuenoAsync($"""
            BEGIN;
            INSERT INTO seguridad.persona VALUES ('{Org}', '{ajena}', '{nombre}');
            INSERT INTO negocio.operador_ficha(organizacion_id, sucursal_id, persona_id, revision, nombre_completo)
            VALUES ('{Org}', '{Suc2}', '{ajena}', 1, '{nombre}');
            COMMIT;
            """);
        using var nav = await SesionAsync(true);
        Assert.DoesNotContain(await ListarAsync(nav), x => x.Id == ajena);
        // Corregirla desde la sucursal propia no la encuentra; tampoco desde la ajena, donde no hay permiso.
        await AfirmarErrorAsync(await EnviarAsync(nav, HttpMethod.Put, $"/api/operadores/{ajena}", Edicion(1, "Intento")), HttpStatusCode.NotFound, EndpointsOperadores.MensajeNoEncontrado);
        var enAjena = await EnviarAsync(nav, HttpMethod.Put, $"/api/operadores/{ajena}", new { sucursalId = Suc2, nombre = "Intento", revision = 1 });
        Assert.Equal(HttpStatusCode.Forbidden, enAjena.StatusCode);
        Assert.Equal(nombre, await _bd.ConsultarAsync($"SELECT nombre_completo FROM negocio.operador_ficha WHERE persona_id = '{ajena}'"));
    }

    [Fact]
    public async Task La_cuenta_del_operador_se_autoriza_para_el_telefono_sin_abrir_el_panel_y_se_retira_al_quitarla()
    {
        using var nav = await SesionAsync(true);
        var correo = Cuenta();
        var creado = await CrearAsync(nav, Nuevo($"Operador Ficticio {Guid.NewGuid():N}"[..28], correo: $"  {correo.ToUpperInvariant()} "));
        Assert.Equal(correo, (await OperadorAsync(nav, creado.Id)).Correo);
        Assert.Equal("telefono_operador:true", await EstadoCuentaAsync(correo));
        await AfirmarSinPanelAsync(correo);
        // La cuenta no recibe permisos de datos: la app móvil los definirá con su inicio de sesión.
        Assert.Equal("0", await _bd.ConsultarAsync($"SELECT count(*) FROM seguridad.permiso_sucursal p JOIN seguridad.acceso_autorizado a ON a.id = p.acceso_id WHERE a.correo_permitido = '{correo}'"));

        // Sin cambios no hay revisión; quitar la cuenta crea la revisión 2 y la desactiva.
        var url = $"/api/operadores/{creado.Id}";
        var o = await OperadorAsync(nav, creado.Id);
        Assert.Equal(1, (await (await EnviarAsync(nav, HttpMethod.Put, url, Edicion(1, o.Nombre, o.Cargo, correo))).Content.ReadFromJsonAsync<RevisionGuardada>())!.Revision);
        Assert.Equal(2, (await (await EnviarAsync(nav, HttpMethod.Put, url, Edicion(1, o.Nombre, o.Cargo, null))).Content.ReadFromJsonAsync<RevisionGuardada>())!.Revision);
        Assert.Null((await OperadorAsync(nav, creado.Id)).Correo);
        Assert.Equal("telefono_operador:false", await EstadoCuentaAsync(correo));
        // El historial conserva quién tuvo la cuenta.
        Assert.Equal("1", await _bd.ConsultarAsync($"SELECT count(*) FROM negocio.operador_ficha WHERE persona_id = '{creado.Id}' AND correo = '{correo}'"));
        await AfirmarSinPanelAsync(correo);
    }

    [Fact]
    public async Task Una_cuenta_no_se_comparte_se_reasigna_quitandosela_antes_y_no_toca_cuentas_de_operaciones()
    {
        using var nav = await SesionAsync(true);
        var correo = Cuenta();
        var a = await CrearAsync(nav, Nuevo($"Operador A {Guid.NewGuid():N}"[..24], correo: correo));
        var antes = await _bd.ConsultarAsync("SELECT count(*) FROM seguridad.persona");
        // Otro operador no puede tomar una cuenta que otro tiene hoy; nada se crea.
        await AfirmarErrorAsync(await EnviarAsync(nav, HttpMethod.Post, "/api/operadores", Nuevo("Operador B", correo: correo)), HttpStatusCode.BadRequest, RepositorioOperadores.MensajeCorreoEnUso);
        Assert.Equal(antes, await _bd.ConsultarAsync("SELECT count(*) FROM seguridad.persona"));
        var b = await CrearAsync(nav, Nuevo($"Operador B {Guid.NewGuid():N}"[..24]));
        await AfirmarErrorAsync(await EnviarAsync(nav, HttpMethod.Put, $"/api/operadores/{b.Id}", Edicion(1, "Operador B", correo: correo)), HttpStatusCode.BadRequest, RepositorioOperadores.MensajeCorreoEnUso);
        Assert.Equal("telefono_operador:true", await EstadoCuentaAsync(correo));

        // Se le quita a A y B la recibe: la cuenta sigue autorizada y el historial de A se conserva.
        Assert.Equal(HttpStatusCode.OK, (await EnviarAsync(nav, HttpMethod.Put, $"/api/operadores/{a.Id}", Edicion(1, (await OperadorAsync(nav, a.Id)).Nombre, correo: null))).StatusCode);
        Assert.Equal("telefono_operador:false", await EstadoCuentaAsync(correo));
        Assert.Equal(HttpStatusCode.OK, (await EnviarAsync(nav, HttpMethod.Put, $"/api/operadores/{b.Id}", Edicion(1, "Operador B", correo: correo))).StatusCode);
        Assert.Equal("telefono_operador:true", await EstadoCuentaAsync(correo));
        Assert.Equal(correo, (await OperadorAsync(nav, b.Id)).Correo);
        Assert.Null((await OperadorAsync(nav, a.Id)).Correo);
        Assert.Equal("1", await _bd.ConsultarAsync($"SELECT count(*) FROM negocio.operador_ficha WHERE persona_id = '{a.Id}' AND correo = '{correo}'"));

        // Una cuenta de Operaciones (o cualquier otra) no se puede asignar a un operador ni se modifica.
        var operaciones = $"operaciones-{Guid.NewGuid():N}@example.invalid";
        await _bd.AutorizarAsync(operaciones, escribir: true);
        await AfirmarErrorAsync(await EnviarAsync(nav, HttpMethod.Post, "/api/operadores", Nuevo("Operador C", correo: operaciones)), HttpStatusCode.BadRequest, RepositorioOperadores.MensajeCorreoReservado);
        await AfirmarErrorAsync(await EnviarAsync(nav, HttpMethod.Put, $"/api/operadores/{b.Id}", Edicion(2, "Operador B", correo: operaciones)), HttpStatusCode.BadRequest, RepositorioOperadores.MensajeCorreoReservado);
        Assert.Equal("operaciones:true", await EstadoCuentaAsync(operaciones));
        Assert.Equal(correo, (await OperadorAsync(nav, b.Id)).Correo);
        // La cuenta con que quita B tampoco se retira por error: sigue activa mientras B la tenga.
        Assert.Equal("telefono_operador:true", await EstadoCuentaAsync(correo));
    }

    [Fact]
    public async Task Rechaza_un_correo_con_formato_invalido_sin_crear_nada()
    {
        using var nav = await SesionAsync(true);
        var antes = await _bd.ConsultarAsync("SELECT (SELECT count(*) FROM seguridad.persona) || '|' || (SELECT count(*) FROM seguridad.acceso_autorizado)");
        foreach (var malo in new[] { "sin-arroba", "a@b", "con espacio@x.mx", "dos@@x.mx" })
            await AfirmarErrorAsync(await EnviarAsync(nav, HttpMethod.Post, "/api/operadores", Nuevo("Operador D", correo: malo)), HttpStatusCode.BadRequest, RepositorioOperadores.MensajeCorreoInvalido);
        Assert.Equal(antes, await _bd.ConsultarAsync("SELECT (SELECT count(*) FROM seguridad.persona) || '|' || (SELECT count(*) FROM seguridad.acceso_autorizado)"));
    }
}
