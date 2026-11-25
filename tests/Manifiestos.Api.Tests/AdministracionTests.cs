using System.Net;
using System.Net.Http.Json;
using Manifiestos.Api.Acceso;
using Manifiestos.Api.Administracion;
using Manifiestos.Api.Tests.Infraestructura;

namespace Manifiestos.Api.Tests;

/// <summary>
/// Administración (0072) contra PostgreSQL real: solo el administrador gestiona cuentas, toda escritura exige confirmar con Google,
/// los permisos por pantalla se aplican en el servidor, nada se elimina (se deshabilita) y siempre quedan 2 administradores. Datos ficticios.
/// </summary>
[Trait("Categoria", "Integracion")]
public sealed class AdministracionTests(BaseDatosPrueba bd) : IClassFixture<BaseDatosPrueba>, IAsyncLifetime
{
    private static readonly Guid Suc = BaseDatosPrueba.Sucursal;
    private ApiPrueba _api = null!;
    public Task InitializeAsync() { _api = new ApiPrueba(bd); return Task.CompletedTask; }
    public async Task DisposeAsync() => await _api.DisposeAsync();

    private sealed record Sesion(HttpClient Nav, string Correo);

    private async Task<Sesion> EntrarAsync(string rol, bool escribir = true)
    {
        var correo = $"adm-{Guid.NewGuid():N}@example.invalid";
        await bd.AutorizarAsync(correo, rol, escribir: escribir);
        var nav = _api.NuevoNavegador();
        Assert.Equal(HttpStatusCode.NoContent, (await nav.EntrarAsync(ValidadorFicticio.Token($"s-{correo}", correo))).StatusCode);
        return new Sesion(nav, correo);
    }

    private static async Task<HttpResponseMessage> EnviarAsync(HttpClient nav, HttpMethod metodo, string url, object? cuerpo = null)
    {
        var req = new HttpRequestMessage(metodo, url) { Content = JsonContent.Create(cuerpo ?? new { }) };
        req.Headers.Add(ServiciosAcceso.CabeceraCsrf, await nav.CsrfAsync());
        return await nav.SendAsync(req);
    }

    private static Task<HttpResponseMessage> ConfirmarAsync(Sesion s, string? correo = null) =>
        EnviarAsync(s.Nav, HttpMethod.Post, "/api/admin/confirmar", new { credential = ValidadorFicticio.Token($"s-{correo ?? s.Correo}", correo ?? s.Correo) });

    private static object Cuenta(string correo, string tipo, params (string pantalla, string nivel)[] pantallas) => new
    {
        correo, nombre = "Persona de prueba", tipo,
        permisos = new[] { new { sucursalId = Suc, pantallas = pantallas.ToDictionary(p => p.pantalla, p => p.nivel) } },
    };

    private static async Task<Guid> IdDe(HttpResponseMessage r) =>
        (await r.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>()).GetProperty("id").GetGuid();

    [Fact]
    public async Task Operaciones_no_ve_ni_cambia_cuentas_ni_equipos()
    {
        var op = await EntrarAsync("operaciones");
        Assert.Equal("Operaciones", (await op.Nav.GetFromJsonAsync<UsuarioActual>("/api/auth/me"))!.Rol.Nombre);
        var adm = await EntrarAsync("administrador");
        Assert.Equal("Administrador", (await adm.Nav.GetFromJsonAsync<UsuarioActual>("/api/auth/me"))!.Rol.Nombre);   // sin «provisional»
        Assert.Equal(HttpStatusCode.Forbidden, (await op.Nav.GetAsync("/api/admin/cuentas")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await op.Nav.GetAsync("/api/equipos")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await EnviarAsync(op.Nav, HttpMethod.Post, "/api/admin/cuentas", Cuenta("x@example.invalid", "operaciones", ("rutas", "ver")))).StatusCode);
    }

    [Fact]
    public async Task Escribir_exige_confirmar_con_la_misma_cuenta_de_google()
    {
        var admin = await EntrarAsync("administrador");
        var nueva = $"nueva-{Guid.NewGuid():N}@example.invalid";
        var sinConfirmar = await EnviarAsync(admin.Nav, HttpMethod.Post, "/api/admin/cuentas", Cuenta(nueva, "operaciones", ("rutas", "ver")));
        Assert.Equal(HttpStatusCode.Forbidden, sinConfirmar.StatusCode);
        Assert.Contains("confirmar", await sinConfirmar.Content.ReadAsStringAsync());
        Assert.Contains("\"vigente\":false", await admin.Nav.GetStringAsync("/api/admin/confirmacion"));

        Assert.Equal(HttpStatusCode.Forbidden, (await ConfirmarAsync(admin, "otra-persona@example.invalid")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await EnviarAsync(admin.Nav, HttpMethod.Post, "/api/admin/cuentas", Cuenta(nueva, "operaciones", ("rutas", "ver")))).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await ConfirmarAsync(admin)).StatusCode);
        Assert.Contains("\"vigente\":true", await admin.Nav.GetStringAsync("/api/admin/confirmacion"));
        Assert.Equal(HttpStatusCode.OK, (await EnviarAsync(admin.Nav, HttpMethod.Post, "/api/admin/cuentas", Cuenta(nueva, "operaciones", ("rutas", "ver")))).StatusCode);
    }

    [Fact]
    public async Task Permisos_por_pantalla_se_aplican_en_el_servidor_y_se_ven_en_me()
    {
        var admin = await EntrarAsync("administrador");
        await ConfirmarAsync(admin);
        var correo = $"parcial-{Guid.NewGuid():N}@example.invalid";
        Assert.Equal(HttpStatusCode.OK, (await EnviarAsync(admin.Nav, HttpMethod.Post, "/api/admin/cuentas",
            Cuenta(correo, "operaciones", ("incidencias", "ver"), ("clientes", "sin"), ("rutas", "sin")))).StatusCode);

        var nav = _api.NuevoNavegador();
        Assert.Equal(HttpStatusCode.NoContent, (await nav.EntrarAsync(ValidadorFicticio.Token($"s-{correo}", correo))).StatusCode);
        var yo = (await nav.GetFromJsonAsync<UsuarioActual>("/api/auth/me"))!;
        Assert.Equal("ver", yo.Permisos!["incidencias"]);
        Assert.Equal("sin", yo.Permisos["rutas"]);

        Assert.Equal(HttpStatusCode.Forbidden, (await nav.GetAsync("/api/rutas")).StatusCode);          // sin acceso a Rutas
        Assert.NotEqual(HttpStatusCode.Forbidden, (await nav.GetAsync("/api/incidencias")).StatusCode); // solo ver Incidencias
        Assert.NotEqual(HttpStatusCode.Forbidden, (await nav.GetAsync("/api/clientes")).StatusCode);    // catálogo que usa Incidencias
        Assert.Equal(HttpStatusCode.Forbidden, (await EnviarAsync(nav, HttpMethod.Post, "/api/clientes")).StatusCode); // escribir exige «editar»
        Assert.Equal(HttpStatusCode.Forbidden, (await nav.GetAsync("/api/admin/cuentas")).StatusCode);
    }

    [Fact]
    public async Task Deshabilitar_no_borra_bloquea_el_acceso_y_se_puede_reactivar()
    {
        var admin = await EntrarAsync("administrador");
        await ConfirmarAsync(admin);
        var correo = $"baja-{Guid.NewGuid():N}@example.invalid";
        var id = await IdDe(await EnviarAsync(admin.Nav, HttpMethod.Post, "/api/admin/cuentas", Cuenta(correo, "operaciones", ("rutas", "editar"))));

        Assert.Equal(HttpStatusCode.NoContent, (await EnviarAsync(admin.Nav, HttpMethod.Post, $"/api/admin/cuentas/{id}/estado", new { activo = false })).StatusCode);
        Assert.Equal("1", await bd.ConsultarAsync($"SELECT count(*) FROM seguridad.acceso_autorizado WHERE id='{id}' AND NOT activo"));
        Assert.Equal(HttpStatusCode.Forbidden, (await _api.NuevoNavegador().EntrarAsync(ValidadorFicticio.Token($"s-{correo}", correo))).StatusCode);
        Assert.Contains(correo, await admin.Nav.GetStringAsync("/api/admin/deshabilitados"));

        Assert.Equal(HttpStatusCode.NoContent, (await EnviarAsync(admin.Nav, HttpMethod.Post, $"/api/admin/cuentas/{id}/estado", new { activo = true })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await _api.NuevoNavegador().EntrarAsync(ValidadorFicticio.Token($"s-{correo}", correo))).StatusCode);
        Assert.True(int.Parse(await bd.ConsultarAsync($"SELECT count(*) FROM seguridad.administracion_historial WHERE objeto_id='{id}'")) >= 3);
    }

    [Fact]
    public async Task Siempre_quedan_dos_administradores_y_nadie_se_deshabilita_a_si_mismo()
    {
        await bd.SqlComoDuenoAsync("UPDATE seguridad.acceso_autorizado SET activo = false WHERE rol_codigo = 'administrador';");
        var a = await EntrarAsync("administrador");
        await ConfirmarAsync(a);
        var b = await IdDe(await EnviarAsync(a.Nav, HttpMethod.Post, "/api/admin/cuentas", Cuenta($"segundo-{Guid.NewGuid():N}@example.invalid", "administrador")));

        // Con solo A y B activos, A no puede dejar fuera a B (quedaría uno).
        var intento = await EnviarAsync(a.Nav, HttpMethod.Post, $"/api/admin/cuentas/{b}/estado", new { activo = false });
        Assert.Equal(HttpStatusCode.Conflict, intento.StatusCode);
        Assert.Contains("minimo_administradores", await intento.Content.ReadAsStringAsync());

        var idA = Guid.Parse(await bd.ConsultarAsync($"SELECT id FROM seguridad.acceso_autorizado WHERE correo_permitido='{a.Correo}'"));
        var propio = await EnviarAsync(a.Nav, HttpMethod.Post, $"/api/admin/cuentas/{idA}/estado", new { activo = false });
        Assert.Equal(HttpStatusCode.Conflict, propio.StatusCode);
        Assert.Contains("no_a_ti_mismo", await propio.Content.ReadAsStringAsync());

        // Con un tercero activo, B ya se puede deshabilitar.
        await EnviarAsync(a.Nav, HttpMethod.Post, "/api/admin/cuentas", Cuenta($"tercero-{Guid.NewGuid():N}@example.invalid", "administrador"));
        Assert.Equal(HttpStatusCode.NoContent, (await EnviarAsync(a.Nav, HttpMethod.Post, $"/api/admin/cuentas/{b}/estado", new { activo = false })).StatusCode);
    }

    [Fact]
    public async Task Cuenta_de_operaciones_necesita_al_menos_una_pantalla_y_correo_unico()
    {
        var admin = await EntrarAsync("administrador");
        await ConfirmarAsync(admin);
        var correo = $"valida-{Guid.NewGuid():N}@example.invalid";
        Assert.Equal(HttpStatusCode.BadRequest, (await EnviarAsync(admin.Nav, HttpMethod.Post, "/api/admin/cuentas", Cuenta(correo, "operaciones", ("rutas", "sin")))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await EnviarAsync(admin.Nav, HttpMethod.Post, "/api/admin/cuentas", Cuenta(correo, "operaciones", ("dashboard", "editar")))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await EnviarAsync(admin.Nav, HttpMethod.Post, "/api/admin/cuentas", Cuenta(correo, "operaciones", ("rutas", "ver")))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await EnviarAsync(admin.Nav, HttpMethod.Post, "/api/admin/cuentas", Cuenta(correo, "operaciones", ("rutas", "ver")))).StatusCode);
        var cuentas = await admin.Nav.GetFromJsonAsync<List<CuentaAdmin>>("/api/admin/cuentas");
        Assert.Contains(cuentas!, c => c.Correo == correo && c.Tipo == "operaciones" && c.Permisos.Any(p => p.Pantalla == "rutas" && p.Nivel == "ver"));
    }

    // ---- Operador de teléfono con equipo entregado, para probar bloqueos ----
    private sealed record Telefono(string Correo, string Sujeto, Guid Acceso, Guid Instalacion, Guid Dispositivo);

    private async Task<Telefono> NuevoTelefonoAsync()
    {
        var correo = $"tel-{Guid.NewGuid():N}@example.invalid"; var persona = Guid.NewGuid(); var ins = Guid.NewGuid();
        await bd.AutorizarAsync(correo, "telefono_operador", conSucursal: false);
        await bd.SqlComoDuenoAsync($"""
            INSERT INTO seguridad.persona(organizacion_id,id,nombre_completo) VALUES('{BaseDatosPrueba.Organizacion}','{persona}','Operador {persona:N}');
            INSERT INTO negocio.operador_ficha(organizacion_id,sucursal_id,persona_id,revision,nombre_completo,correo)
            VALUES('{BaseDatosPrueba.Organizacion}','{Suc}','{persona}',1,'Operador {persona:N}','{correo}');
            WITH d AS (INSERT INTO seguridad.dispositivo(organizacion_id) VALUES('{BaseDatosPrueba.Organizacion}') RETURNING id),
            i AS (INSERT INTO seguridad.instalacion_app(organizacion_id,id,dispositivo_id,habilitada) SELECT '{BaseDatosPrueba.Organizacion}','{ins}',d.id,true FROM d RETURNING dispositivo_id)
            INSERT INTO seguridad.asignacion_equipo(organizacion_id,sucursal_id,dispositivo_id,acceso_id,persona_id,revision,desde)
            SELECT '{BaseDatosPrueba.Organizacion}','{Suc}',i.dispositivo_id,a.id,'{persona}',1,now()-interval '1 day'
            FROM i, seguridad.acceso_autorizado a WHERE a.correo_permitido='{correo}';
            """);
        var acceso = Guid.Parse(await bd.ConsultarAsync($"SELECT id FROM seguridad.acceso_autorizado WHERE correo_permitido='{correo}'"));
        var disp = Guid.Parse(await bd.ConsultarAsync($"SELECT dispositivo_id FROM seguridad.instalacion_app WHERE id='{ins}'"));
        return new Telefono(correo, $"s-{correo}", acceso, ins, disp);
    }

    private static async Task<HttpStatusCode> EntrarTelefonoAsync(HttpClient c, Telefono t)
    {
        var d = await c.PostAsJsonAsync("/api/movil/auth/desafio", new { instalacionId = t.Instalacion });
        if (d.StatusCode != HttpStatusCode.OK) return d.StatusCode;
        var desafio = (await d.Content.ReadFromJsonAsync<DesafioMovil>())!;
        return (await c.PostAsJsonAsync("/api/movil/auth/google", new SolicitudGoogleMovil(ValidadorFicticio.Token(t.Sujeto, t.Correo, nonce: desafio.Nonce), t.Instalacion, desafio.Id))).StatusCode;
    }

    [Fact]
    public async Task Cuenta_de_telefono_deshabilitada_no_entra_y_reactivada_si()
    {
        var admin = await EntrarAsync("administrador"); await ConfirmarAsync(admin);
        var t = await NuevoTelefonoAsync();
        Assert.Equal(HttpStatusCode.OK, await EntrarTelefonoAsync(_api.NuevoNavegador(), t));

        Assert.Equal(HttpStatusCode.NoContent, (await EnviarAsync(admin.Nav, HttpMethod.Post, $"/api/admin/cuentas/{t.Acceso}/estado", new { activo = false })).StatusCode);
        Assert.NotEqual(HttpStatusCode.OK, await EntrarTelefonoAsync(_api.NuevoNavegador(), t));
        Assert.Contains(t.Correo, await admin.Nav.GetStringAsync("/api/admin/deshabilitados"));

        Assert.Equal(HttpStatusCode.NoContent, (await EnviarAsync(admin.Nav, HttpMethod.Post, $"/api/admin/cuentas/{t.Acceso}/estado", new { activo = true })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, await EntrarTelefonoAsync(_api.NuevoNavegador(), t));
    }

    [Fact]
    public async Task Equipo_deshabilitado_no_entra_y_reactivado_si_y_solo_el_administrador_lo_cambia()
    {
        var admin = await EntrarAsync("administrador"); await ConfirmarAsync(admin);
        var op = await EntrarAsync("operaciones");
        var t = await NuevoTelefonoAsync();
        Assert.Equal(HttpStatusCode.OK, await EntrarTelefonoAsync(_api.NuevoNavegador(), t));

        Assert.Equal(HttpStatusCode.Forbidden, (await EnviarAsync(op.Nav, HttpMethod.Post, $"/api/admin/equipos/{t.Dispositivo}/estado", new { activo = false })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, await EntrarTelefonoAsync(_api.NuevoNavegador(), t));

        Assert.Equal(HttpStatusCode.NoContent, (await EnviarAsync(admin.Nav, HttpMethod.Post, $"/api/admin/equipos/{t.Dispositivo}/estado", new { activo = false })).StatusCode);
        Assert.NotEqual(HttpStatusCode.OK, await EntrarTelefonoAsync(_api.NuevoNavegador(), t));
        Assert.Contains("Equipo", await admin.Nav.GetStringAsync("/api/admin/deshabilitados"));

        Assert.Equal(HttpStatusCode.NoContent, (await EnviarAsync(admin.Nav, HttpMethod.Post, $"/api/admin/equipos/{t.Dispositivo}/estado", new { activo = true })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, await EntrarTelefonoAsync(_api.NuevoNavegador(), t));
        Assert.Equal(HttpStatusCode.NotFound, (await EnviarAsync(admin.Nav, HttpMethod.Post, $"/api/admin/equipos/{Guid.NewGuid()}/estado", new { activo = false })).StatusCode);
    }

    [Fact]
    public async Task Sesion_abierta_de_una_cuenta_deshabilitada_deja_de_servir_al_instante()
    {
        var admin = await EntrarAsync("administrador"); await ConfirmarAsync(admin);
        var op = await EntrarAsync("operaciones");
        Assert.Equal(HttpStatusCode.OK, (await op.Nav.GetAsync("/api/auth/me")).StatusCode);
        var id = await bd.ConsultarAsync($"SELECT id FROM seguridad.acceso_autorizado WHERE correo_permitido='{op.Correo}'");
        Assert.Equal(HttpStatusCode.NoContent, (await EnviarAsync(admin.Nav, HttpMethod.Post, $"/api/admin/cuentas/{id}/estado", new { activo = false })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await op.Nav.GetAsync("/api/auth/me")).StatusCode);
    }

    [Fact]
    public async Task Editar_cambia_permisos_y_tipo_pero_no_el_correo_de_una_cuenta_que_ya_entro()
    {
        var admin = await EntrarAsync("administrador"); await ConfirmarAsync(admin);
        var correo = $"edita-{Guid.NewGuid():N}@example.invalid";
        var id = await IdDe(await EnviarAsync(admin.Nav, HttpMethod.Post, "/api/admin/cuentas", Cuenta(correo, "operaciones", ("rutas", "ver"))));

        // Aún no inicia sesión: el correo se puede corregir.
        var corregido = $"corregido-{Guid.NewGuid():N}@example.invalid";
        Assert.Equal(HttpStatusCode.OK, (await EnviarAsync(admin.Nav, HttpMethod.Put, $"/api/admin/cuentas/{id}", Cuenta(corregido, "operaciones", ("rutas", "editar"), ("clientes", "ver")))).StatusCode);
        var lista = (await admin.Nav.GetFromJsonAsync<List<CuentaAdmin>>("/api/admin/cuentas"))!;
        var c = Assert.Single(lista, x => x.Id == id);
        Assert.Equal(corregido, c.Correo);
        Assert.Contains(c.Permisos, p => p.Pantalla == "rutas" && p.Nivel == "editar");
        Assert.Contains(c.Permisos, p => p.Pantalla == "clientes" && p.Nivel == "ver");
        Assert.Contains(c.Permisos, p => p.Pantalla == "unidades" && p.Nivel == "sin");

        // Ya inició sesión: el correo queda fijo (está vinculado a su identidad de Google).
        var nav = _api.NuevoNavegador();
        Assert.Equal(HttpStatusCode.NoContent, (await nav.EntrarAsync(ValidadorFicticio.Token($"s-{corregido}", corregido))).StatusCode);
        var otro = await EnviarAsync(admin.Nav, HttpMethod.Put, $"/api/admin/cuentas/{id}", Cuenta($"otro-{Guid.NewGuid():N}@example.invalid", "operaciones", ("rutas", "editar")));
        Assert.Equal(HttpStatusCode.Conflict, otro.StatusCode);
        Assert.Contains("correo_vinculado", await otro.Content.ReadAsStringAsync());

        // Pasa a administrador: ve Administración; sus permisos de pantalla pasan a todo.
        Assert.Equal(HttpStatusCode.OK, (await EnviarAsync(admin.Nav, HttpMethod.Put, $"/api/admin/cuentas/{id}", Cuenta(corregido, "administrador"))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await nav.GetAsync("/api/admin/cuentas")).StatusCode);
        // Una cuenta de teléfono no se edita desde aquí.
        var t = await NuevoTelefonoAsync();
        Assert.Equal(HttpStatusCode.BadRequest, (await EnviarAsync(admin.Nav, HttpMethod.Put, $"/api/admin/cuentas/{t.Acceso}", Cuenta(t.Correo, "operaciones", ("rutas", "ver")))).StatusCode);
    }

    [Fact]
    public async Task Quitar_el_rol_de_administrador_respeta_el_minimo_y_no_se_hace_a_uno_mismo()
    {
        await bd.SqlComoDuenoAsync("UPDATE seguridad.acceso_autorizado SET activo = false WHERE rol_codigo = 'administrador';");
        var a = await EntrarAsync("administrador"); await ConfirmarAsync(a);
        var correoB = $"adm2-{Guid.NewGuid():N}@example.invalid";
        var b = await IdDe(await EnviarAsync(a.Nav, HttpMethod.Post, "/api/admin/cuentas", Cuenta(correoB, "administrador")));
        var idA = Guid.Parse(await bd.ConsultarAsync($"SELECT id FROM seguridad.acceso_autorizado WHERE correo_permitido='{a.Correo}'"));

        var aB = await EnviarAsync(a.Nav, HttpMethod.Put, $"/api/admin/cuentas/{b}", Cuenta(correoB, "operaciones", ("rutas", "ver")));
        Assert.Equal(HttpStatusCode.Conflict, aB.StatusCode); Assert.Contains("minimo_administradores", await aB.Content.ReadAsStringAsync());
        var aA = await EnviarAsync(a.Nav, HttpMethod.Put, $"/api/admin/cuentas/{idA}", Cuenta(a.Correo, "operaciones", ("rutas", "ver")));
        Assert.Equal(HttpStatusCode.Conflict, aA.StatusCode); Assert.Contains("no_a_ti_mismo", await aA.Content.ReadAsStringAsync());
        Assert.Equal("2", await bd.ConsultarAsync("SELECT count(*) FROM seguridad.acceso_autorizado WHERE rol_codigo='administrador' AND activo"));
    }

    [Fact]
    public async Task Niveles_ver_y_editar_se_aplican_por_pantalla()
    {
        var admin = await EntrarAsync("administrador"); await ConfirmarAsync(admin);
        var correo = $"niveles-{Guid.NewGuid():N}@example.invalid";
        await EnviarAsync(admin.Nav, HttpMethod.Post, "/api/admin/cuentas", Cuenta(correo, "operaciones", ("clientes", "ver"), ("unidades", "editar"), ("sitios", "sin"), ("operadores", "sin")));
        var nav = _api.NuevoNavegador();
        await nav.EntrarAsync(ValidadorFicticio.Token($"s-{correo}", correo));

        Assert.NotEqual(HttpStatusCode.Forbidden, (await nav.GetAsync("/api/clientes")).StatusCode);                // ver: lee
        Assert.Equal(HttpStatusCode.Forbidden, (await EnviarAsync(nav, HttpMethod.Post, "/api/clientes")).StatusCode); // ver: no escribe
        Assert.NotEqual(HttpStatusCode.Forbidden, (await nav.GetAsync("/api/unidades")).StatusCode);
        Assert.NotEqual(HttpStatusCode.Forbidden, (await EnviarAsync(nav, HttpMethod.Post, "/api/unidades")).StatusCode); // editar: llega al endpoint (validación propia)
        Assert.Equal(HttpStatusCode.Forbidden, (await nav.GetAsync("/api/sitios")).StatusCode);                     // sin acceso
        Assert.Equal(HttpStatusCode.Forbidden, (await nav.GetAsync("/api/operadores")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await nav.GetAsync("/api/rutas")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await nav.GetAsync("/api/incidencias")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await nav.GetAsync("/api/equipos")).StatusCode);
    }

    [Fact]
    public async Task La_confirmacion_vence_y_cada_escritura_la_vuelve_a_exigir()
    {
        var admin = await EntrarAsync("administrador"); await ConfirmarAsync(admin);
        var correo = $"vence-{Guid.NewGuid():N}@example.invalid";
        Assert.Equal(HttpStatusCode.OK, (await EnviarAsync(admin.Nav, HttpMethod.Post, "/api/admin/cuentas", Cuenta(correo, "operaciones", ("rutas", "ver")))).StatusCode);
        await bd.SqlComoDuenoAsync($"ALTER TABLE seguridad.sesion_web_confirmacion DISABLE TRIGGER conservar_historia; UPDATE seguridad.sesion_web_confirmacion SET confirmada_en = now() - interval '10 minutes' WHERE sesion_id IN (SELECT id FROM seguridad.sesion_web w WHERE w.acceso_id = (SELECT id FROM seguridad.acceso_autorizado WHERE correo_permitido='{admin.Correo}'));  ALTER TABLE seguridad.sesion_web_confirmacion ENABLE TRIGGER conservar_historia;");
        var vencida = await EnviarAsync(admin.Nav, HttpMethod.Post, "/api/admin/cuentas", Cuenta($"vence2-{Guid.NewGuid():N}@example.invalid", "operaciones", ("rutas", "ver")));
        Assert.Equal(HttpStatusCode.Forbidden, vencida.StatusCode);
        Assert.Contains("confirmar", await vencida.Content.ReadAsStringAsync());
        await ConfirmarAsync(admin);
        Assert.Equal(HttpStatusCode.OK, (await EnviarAsync(admin.Nav, HttpMethod.Post, "/api/admin/cuentas", Cuenta($"vence3-{Guid.NewGuid():N}@example.invalid", "operaciones", ("rutas", "ver")))).StatusCode);
    }

    [Fact]
    public async Task Las_escrituras_de_administracion_exigen_csrf_y_sesion()
    {
        var admin = await EntrarAsync("administrador"); await ConfirmarAsync(admin);
        var sinCsrf = await admin.Nav.PostAsJsonAsync("/api/admin/cuentas", Cuenta($"csrf-{Guid.NewGuid():N}@example.invalid", "operaciones", ("rutas", "ver")));
        Assert.Equal(HttpStatusCode.BadRequest, sinCsrf.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _api.NuevoNavegador().GetAsync("/api/admin/cuentas")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _api.NuevoNavegador().GetAsync("/api/admin/deshabilitados")).StatusCode);
    }

    [Fact]
    public async Task Administrador_se_crea_en_las_sucursales_elegidas_y_exige_al_menos_una_valida()
    {
        var admin = await EntrarAsync("administrador"); await ConfirmarAsync(admin);
        var correo = $"adm-suc-{Guid.NewGuid():N}@example.invalid";
        object Con(params Guid[] sucursales) => new { correo, nombre = "Admin de prueba", tipo = "administrador", permisos = sucursales.Select(x => new { sucursalId = x, pantallas = new Dictionary<string, string>() }).ToArray() };
        Assert.Equal(HttpStatusCode.BadRequest, (await EnviarAsync(admin.Nav, HttpMethod.Post, "/api/admin/cuentas", Con())).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await EnviarAsync(admin.Nav, HttpMethod.Post, "/api/admin/cuentas", Con(Guid.NewGuid()))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await EnviarAsync(admin.Nav, HttpMethod.Post, "/api/admin/cuentas", Con(Suc))).StatusCode);
        Assert.Equal("1", await bd.ConsultarAsync($"SELECT count(DISTINCT sucursal_id) FROM seguridad.permiso_sucursal p JOIN seguridad.acceso_autorizado a ON a.id=p.acceso_id WHERE a.correo_permitido='{correo}' AND p.permiso='datos.escribir'"));
    }

    [Fact]
    public async Task Una_cuenta_de_una_sucursal_no_ve_ni_escribe_en_otra()
    {
        var suc2 = Guid.Parse("0f000000-0000-4000-8000-0000000000b2");
        var persona = Guid.NewGuid(); var nombre = $"Operador Ajeno {persona:N}";
        await bd.SqlComoDuenoAsync($"""
            INSERT INTO negocio.sucursal(organizacion_id, id, empresa_id, codigo, nombre)
            VALUES ('{BaseDatosPrueba.Organizacion}', '{suc2}', '0f000000-0000-4000-8000-000000000002', 'ADM2', 'Otra sucursal ficticia') ON CONFLICT DO NOTHING;
            INSERT INTO seguridad.persona(organizacion_id,id,nombre_completo) VALUES('{BaseDatosPrueba.Organizacion}','{persona}','{nombre}');
            INSERT INTO negocio.operador_ficha(organizacion_id,sucursal_id,persona_id,revision,nombre_completo,correo)
            VALUES('{BaseDatosPrueba.Organizacion}','{suc2}','{persona}',1,'{nombre}','ajeno-{persona:N}@example.invalid');
            """);
        var admin = await EntrarAsync("administrador"); await ConfirmarAsync(admin);
        var correo = $"solo-suc-{Guid.NewGuid():N}@example.invalid";
        Assert.Equal(HttpStatusCode.OK, (await EnviarAsync(admin.Nav, HttpMethod.Post, "/api/admin/cuentas", Cuenta(correo, "operaciones", ("operadores", "editar"), ("clientes", "editar"), ("unidades", "editar"), ("rutas", "editar"), ("manifiestos", "ver")))).StatusCode);
        var nav = _api.NuevoNavegador();
        await nav.EntrarAsync(ValidadorFicticio.Token($"s-{correo}", correo));

        var yo = (await nav.GetFromJsonAsync<UsuarioActual>("/api/auth/me"))!;
        Assert.Equal([Suc], yo.Sucursales.Select(x => x.Id));
        Assert.DoesNotContain(nombre, await nav.GetStringAsync("/api/operadores"));
        foreach (var url in new[] { "/api/clientes", "/api/unidades", "/api/rutas", "/api/manifiestos" })
            Assert.DoesNotContain(suc2.ToString(), await nav.GetStringAsync(url));
        // Escribir en la sucursal ajena se rechaza aunque la pantalla sea «Ver y editar».
        var r = await EnviarAsync(nav, HttpMethod.Post, "/api/operadores", new { sucursalId = suc2, nombre = "Intruso", cargo = (string?)null, correo = (string?)null });
        Assert.NotEqual(HttpStatusCode.OK, r.StatusCode); Assert.NotEqual(HttpStatusCode.Created, r.StatusCode);
    }

    [Fact]
    public async Task Una_cuenta_de_operaciones_marcada_tambien_entra_al_telefono_y_se_puede_dar_de_alta_como_operador()
    {
        var admin = await EntrarAsync("administrador");
        await ConfirmarAsync(admin);
        var correo = $"doble-{Guid.NewGuid():N}@example.invalid";
        object Doble(bool opera) => new { correo, nombre = "Persona Doble", tipo = "operaciones", tambienOperador = opera,
            permisos = new[] { new { sucursalId = Suc, pantallas = new Dictionary<string, string> { ["rutas"] = "editar", ["operadores"] = "editar" } } } };
        var id = await IdDe(await EnviarAsync(admin.Nav, HttpMethod.Post, "/api/admin/cuentas", Doble(true)));
        var cuentas = await admin.Nav.GetFromJsonAsync<System.Text.Json.JsonElement>("/api/admin/cuentas");
        Assert.True(cuentas.EnumerateArray().Single(c => c.GetProperty("id").GetGuid() == id).GetProperty("tambienOperador").GetBoolean());
        // Operadores admite ese correo (antes lo rechazaba por ser de panel) y la app lo reconoce como operador.
        var org = BaseDatosPrueba.Organizacion;
        await bd.SqlComoDuenoAsync($"SELECT negocio.autorizar_cuenta_operador('{org}','{correo}');");
        Assert.Equal("operaciones", await bd.ConsultarAsync($"SELECT rol_codigo FROM seguridad.acceso_autorizado WHERE correo_permitido='{correo}'"));
        Task<string> MotivoMovilAsync() => bd.ConsultarAsync($"SELECT count(*) FROM seguridad.acceso_autorizado x WHERE x.correo_permitido='{correo}' AND (x.rol_codigo='telefono_operador' OR x.tambien_operador)");
        Assert.Equal("1", await MotivoMovilAsync());
        Assert.Equal(HttpStatusCode.OK, (await EnviarAsync(admin.Nav, HttpMethod.Put, $"/api/admin/cuentas/{id}", Doble(false))).StatusCode);
        Assert.Equal("0", await MotivoMovilAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => bd.SqlComoDuenoAsync($"SELECT negocio.autorizar_cuenta_operador('{org}','{correo}');"));
    }
}
