using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Manifiestos.Api.Acceso;
using Manifiestos.Api.Legal;
using Manifiestos.Api.Tests.Infraestructura;

namespace Manifiestos.Api.Tests;

/// <summary>
/// Aceptación de términos y aviso de privacidad (0074, decisión 0044) contra PostgreSQL real: sin aceptar la versión vigente no se usa
/// el panel ni la app, la aceptación queda atada a versión y huella del texto, es por cuenta y por canal, y una versión nueva vuelve a pedirse.
/// </summary>
[Trait("Categoria", "Integracion")]
public sealed class AceptacionTerminosTests(BaseDatosPrueba bd) : IClassFixture<BaseDatosPrueba>, IAsyncLifetime
{
    private static readonly Guid Suc = BaseDatosPrueba.Sucursal;
    private ApiPrueba _api = null!;
    public Task InitializeAsync() { _api = new ApiPrueba(bd) { ExigirAceptacion = true }; return Task.CompletedTask; }
    public async Task DisposeAsync() => await _api.DisposeAsync();

    private static readonly string Terminos = DocumentosLegalesVigentes.Todos.First(d => d.Id == "terminos").Version;
    private static readonly string Privacidad = DocumentosLegalesVigentes.Todos.First(d => d.Id == "privacidad").Version;
    private static object Ambos => new { documentos = new[] { new { id = "terminos", version = Terminos }, new { id = "privacidad", version = Privacidad } } };

    private sealed record Panel(HttpClient Nav, string Correo);

    private async Task<Panel> EntrarAsync(string rol = "operaciones")
    {
        var correo = $"acepta-{Guid.NewGuid():N}@example.invalid";
        await bd.AutorizarAsync(correo, rol, escribir: true);
        var nav = _api.NuevoNavegador();
        Assert.Equal(HttpStatusCode.NoContent, (await nav.EntrarAsync(ValidadorFicticio.Token($"s-{correo}", correo))).StatusCode);
        return new Panel(nav, correo);
    }

    private static async Task<HttpResponseMessage> PostAsync(HttpClient nav, string url, object? cuerpo = null, bool csrf = true)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(cuerpo ?? new { }) };
        if (csrf) req.Headers.Add(ServiciosAcceso.CabeceraCsrf, await nav.CsrfAsync());
        return await nav.SendAsync(req);
    }

    private static async Task AfirmarPendienteAsync(HttpResponseMessage r)
    {
        Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
        Assert.Contains("aceptacion_pendiente", await r.Content.ReadAsStringAsync());
    }

    private Task<string> FilasAsync(string correo, string? extra = null) => bd.ConsultarAsync(
        $"SELECT count(*) FROM seguridad.aceptacion_documento d JOIN seguridad.acceso_autorizado a ON a.id=d.acceso_id AND a.organizacion_id=d.organizacion_id WHERE a.correo_permitido='{correo}' {extra}");

    [Fact]
    public async Task Sin_aceptar_no_se_usa_el_panel_pero_la_sesion_y_la_aceptacion_siguen_abiertas()
    {
        var p = await EntrarAsync();
        await AfirmarPendienteAsync(await p.Nav.GetAsync("/api/clientes"));
        await AfirmarPendienteAsync(await p.Nav.GetAsync("/api/rutas"));
        await AfirmarPendienteAsync(await p.Nav.GetAsync("/api/admin/cuentas"));
        Assert.Equal(HttpStatusCode.OK, (await p.Nav.GetAsync("/api/auth/me")).StatusCode);
        var estado = (await p.Nav.GetFromJsonAsync<EstadoLegal>("/api/legal/estado"))!;
        Assert.True(estado.Exigida);
        Assert.All(estado.Documentos, d => Assert.False(d.Aceptado));
        Assert.Equal(["terminos", "privacidad"], estado.Documentos.Select(d => d.Id));
        Assert.Equal(HttpStatusCode.NoContent, (await PostAsync(p.Nav, "/api/auth/logout")).StatusCode);
    }

    [Fact]
    public async Task Aceptar_los_dos_documentos_abre_el_panel_y_guarda_version_huella_y_canal()
    {
        var p = await EntrarAsync();
        var r = await PostAsync(p.Nav, "/api/legal/aceptar", Ambos);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.All((await r.Content.ReadFromJsonAsync<EstadoLegal>())!.Documentos, d => Assert.True(d.Aceptado));
        Assert.NotEqual(HttpStatusCode.Forbidden, (await p.Nav.GetAsync("/api/clientes")).StatusCode);
        Assert.Equal("2", await FilasAsync(p.Correo, "AND d.canal='web'"));
        var huella = DocumentosLegalesVigentes.Todos.First(d => d.Id == "terminos").Huella;
        Assert.Equal("1", await FilasAsync(p.Correo, $"AND d.documento='terminos' AND d.version='{Terminos}' AND d.huella='{huella}'"));
    }

    [Fact]
    public async Task Aceptar_solo_uno_no_alcanza()
    {
        var p = await EntrarAsync();
        Assert.Equal(HttpStatusCode.OK, (await PostAsync(p.Nav, "/api/legal/aceptar", new { documentos = new[] { new { id = "terminos", version = Terminos } } })).StatusCode);
        await AfirmarPendienteAsync(await p.Nav.GetAsync("/api/clientes"));
        Assert.Equal(HttpStatusCode.OK, (await PostAsync(p.Nav, "/api/legal/aceptar", new { documentos = new[] { new { id = "privacidad", version = Privacidad } } })).StatusCode);
        Assert.NotEqual(HttpStatusCode.Forbidden, (await p.Nav.GetAsync("/api/clientes")).StatusCode);
    }

    [Fact]
    public async Task Una_version_que_no_es_la_vigente_o_un_documento_ajeno_no_registran_nada()
    {
        var p = await EntrarAsync();
        var vieja = await PostAsync(p.Nav, "/api/legal/aceptar", new { documentos = new[] { new { id = "terminos", version = "2020-01-01" } } });
        Assert.Equal(HttpStatusCode.Conflict, vieja.StatusCode);
        Assert.Contains("version_cambio", await vieja.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync(p.Nav, "/api/legal/aceptar", new { documentos = new[] { new { id = "contrato", version = Terminos } } })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync(p.Nav, "/api/legal/aceptar", new { documentos = Array.Empty<object>() })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync(p.Nav, "/api/legal/aceptar", new { documentos = new[] { new { id = "terminos", version = Terminos }, new { id = "terminos", version = Terminos } } })).StatusCode);
        Assert.Equal("0", await FilasAsync(p.Correo));
        await AfirmarPendienteAsync(await p.Nav.GetAsync("/api/clientes"));
    }

    [Fact]
    public async Task Aceptar_exige_sesion_y_csrf_y_repetirlo_conserva_la_fecha_original()
    {
        var p = await EntrarAsync();
        Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync(p.Nav, "/api/legal/aceptar", Ambos, csrf: false)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _api.NuevoNavegador().GetAsync("/api/legal/estado")).StatusCode);
        Assert.Equal("0", await FilasAsync(p.Correo));
        Assert.Equal(HttpStatusCode.OK, (await PostAsync(p.Nav, "/api/legal/aceptar", Ambos)).StatusCode);
        var antes = await bd.ConsultarAsync($"SELECT min(aceptada_en)::text FROM seguridad.aceptacion_documento d JOIN seguridad.acceso_autorizado a ON a.id=d.acceso_id WHERE a.correo_permitido='{p.Correo}'");
        Assert.Equal(HttpStatusCode.OK, (await PostAsync(p.Nav, "/api/legal/aceptar", Ambos)).StatusCode);
        Assert.Equal("2", await FilasAsync(p.Correo));
        Assert.Equal(antes, await bd.ConsultarAsync($"SELECT min(aceptada_en)::text FROM seguridad.aceptacion_documento d JOIN seguridad.acceso_autorizado a ON a.id=d.acceso_id WHERE a.correo_permitido='{p.Correo}'"));
    }

    [Fact]
    public async Task La_aceptacion_es_de_cada_cuenta_y_el_registro_no_se_puede_modificar()
    {
        var a = await EntrarAsync(); var b = await EntrarAsync();
        await PostAsync(a.Nav, "/api/legal/aceptar", Ambos);
        Assert.NotEqual(HttpStatusCode.Forbidden, (await a.Nav.GetAsync("/api/clientes")).StatusCode);
        await AfirmarPendienteAsync(await b.Nav.GetAsync("/api/clientes"));
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => bd.SqlComoDuenoAsync("UPDATE seguridad.aceptacion_documento SET version = 'x';"));
        Assert.Contains("inmutable", ex.Message);
        await Assert.ThrowsAsync<InvalidOperationException>(() => bd.SqlComoDuenoAsync("DELETE FROM seguridad.aceptacion_documento;"));
    }

    [Fact]
    public async Task Una_version_nueva_o_un_texto_distinto_vuelven_a_pedir_la_aceptacion()
    {
        var p = await EntrarAsync();
        // Aceptó una versión anterior de los dos documentos: la vigente sigue pendiente.
        await bd.SqlComoDuenoAsync($"""
            INSERT INTO seguridad.aceptacion_documento(organizacion_id, acceso_id, documento, version, huella, canal)
            SELECT organizacion_id, id, d.doc, '2020-01-01', repeat('a', 64), 'web' FROM seguridad.acceso_autorizado, (VALUES ('terminos'), ('privacidad')) d(doc)
            WHERE correo_permitido = '{p.Correo}';
            """);
        await AfirmarPendienteAsync(await p.Nav.GetAsync("/api/clientes"));
        // Misma versión pero con otra huella de texto (el texto cambió sin cambiar la fecha): tampoco cuenta.
        await bd.SqlComoDuenoAsync($"""
            INSERT INTO seguridad.aceptacion_documento(organizacion_id, acceso_id, documento, version, huella, canal)
            SELECT organizacion_id, id, d.doc, d.v, repeat('b', 64), 'web' FROM seguridad.acceso_autorizado, (VALUES ('terminos', '{Terminos}'), ('privacidad', '{Privacidad}')) d(doc, v)
            WHERE correo_permitido = '{p.Correo}';
            """);
        await AfirmarPendienteAsync(await p.Nav.GetAsync("/api/clientes"));
        Assert.Equal(HttpStatusCode.OK, (await PostAsync(p.Nav, "/api/legal/aceptar", Ambos)).StatusCode);
    }

    [Fact]
    public async Task Administracion_muestra_quien_acepto_los_terminos_vigentes()
    {
        var admin = await EntrarAsync("administrador");
        await PostAsync(admin.Nav, "/api/legal/aceptar", Ambos);
        var otro = await EntrarAsync();
        var cuentas = (await admin.Nav.GetFromJsonAsync<List<Manifiestos.Api.Administracion.CuentaAdmin>>("/api/admin/cuentas"))!;
        var yo = Assert.Single(cuentas, c => c.Correo == admin.Correo);
        Assert.True(yo.TerminosVigentes); Assert.Equal(Terminos, yo.TerminosVersion); Assert.NotNull(yo.TerminosEn);
        var sin = Assert.Single(cuentas, c => c.Correo == otro.Correo);
        Assert.False(sin.TerminosVigentes); Assert.Null(sin.TerminosVersion);
    }

    // ---- App del operador ----
    private sealed record Telefono(string Correo, Guid Instalacion);

    private async Task<Telefono> NuevoTelefonoAsync()
    {
        var correo = $"tel-{Guid.NewGuid():N}@example.invalid"; var persona = Guid.NewGuid(); var ins = Guid.NewGuid(); var org = BaseDatosPrueba.Organizacion;
        await bd.AutorizarAsync(correo, "telefono_operador", conSucursal: false);
        await bd.SqlComoDuenoAsync($"""
            INSERT INTO seguridad.persona(organizacion_id,id,nombre_completo) VALUES('{org}','{persona}','Operador {persona:N}');
            INSERT INTO negocio.operador_ficha(organizacion_id,sucursal_id,persona_id,revision,nombre_completo,correo)
            VALUES('{org}','{Suc}','{persona}',1,'Operador {persona:N}','{correo}');
            WITH d AS (INSERT INTO seguridad.dispositivo(organizacion_id) VALUES('{org}') RETURNING id),
            i AS (INSERT INTO seguridad.instalacion_app(organizacion_id,id,dispositivo_id,habilitada) SELECT '{org}','{ins}',d.id,true FROM d RETURNING dispositivo_id)
            INSERT INTO seguridad.asignacion_equipo(organizacion_id,sucursal_id,dispositivo_id,acceso_id,persona_id,revision,desde)
            SELECT '{org}','{Suc}',i.dispositivo_id,a.id,'{persona}',1,now()-interval '1 day' FROM i, seguridad.acceso_autorizado a WHERE a.correo_permitido='{correo}';
            """);
        return new Telefono(correo, ins);
    }

    private sealed record Tokens(string AccessToken, string RefreshToken);

    private async Task<HttpClient> EntrarTelefonoAsync(Telefono t)
    {
        var c = _api.NuevoNavegador();
        var d = (await (await c.PostAsJsonAsync("/api/movil/auth/desafio", new { instalacionId = t.Instalacion })).Content.ReadFromJsonAsync<DesafioMovil>())!;
        var r = await c.PostAsJsonAsync("/api/movil/auth/google", new SolicitudGoogleMovil(ValidadorFicticio.Token($"s-{t.Correo}", t.Correo, nonce: d.Nonce), t.Instalacion, d.Id));
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var tokens = (await r.Content.ReadFromJsonAsync<Tokens>())!;
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);
        return c;
    }

    [Fact]
    public async Task En_la_app_sin_aceptar_no_se_trabaja_y_aceptar_la_abre()
    {
        var t = await NuevoTelefonoAsync();
        var app = await EntrarTelefonoAsync(t);
        Assert.Equal(HttpStatusCode.OK, (await app.GetAsync("/api/movil/auth/me")).StatusCode);
        await AfirmarPendienteAsync(await app.GetAsync("/api/movil/plantas"));
        await AfirmarPendienteAsync(await app.GetAsync("/api/movil/trabajo"));
        var estado = (await app.GetFromJsonAsync<EstadoLegal>("/api/movil/auth/legal-estado"))!;
        Assert.All(estado.Documentos, d => Assert.False(d.Aceptado));

        Assert.Equal(HttpStatusCode.OK, (await app.PostAsJsonAsync("/api/movil/auth/legal-aceptar", Ambos)).StatusCode);
        Assert.NotEqual(HttpStatusCode.Forbidden, (await app.GetAsync("/api/movil/plantas")).StatusCode);
        Assert.Equal("2", await FilasAsync(t.Correo, "AND d.canal='movil'"));
    }

    [Fact]
    public async Task La_app_no_acepta_una_version_vieja_y_sin_token_no_hay_acceso()
    {
        var t = await NuevoTelefonoAsync();
        var app = await EntrarTelefonoAsync(t);
        var r = await app.PostAsJsonAsync("/api/movil/auth/legal-aceptar", new { documentos = new[] { new { id = "terminos", version = "2020-01-01" } } });
        Assert.Equal(HttpStatusCode.Conflict, r.StatusCode);
        Assert.Equal("0", await FilasAsync(t.Correo));
        Assert.Equal(HttpStatusCode.Unauthorized, (await _api.NuevoNavegador().GetAsync("/api/movil/auth/legal-estado")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _api.NuevoNavegador().PostAsJsonAsync("/api/movil/auth/legal-aceptar", Ambos)).StatusCode);
        // Aceptar en el panel no abre la app y viceversa: la aceptación es por cuenta, y esta cuenta de teléfono no entra al panel.
        await AfirmarPendienteAsync(await app.GetAsync("/api/movil/plantas"));
    }

    [Fact]
    public async Task Con_la_exigencia_apagada_no_se_bloquea_nada()
    {
        await using var libre = new ApiPrueba(bd) { ExigirAceptacion = false };
        var correo = $"libre-{Guid.NewGuid():N}@example.invalid";
        await bd.AutorizarAsync(correo, escribir: true);
        var nav = libre.NuevoNavegador();
        await nav.EntrarAsync(ValidadorFicticio.Token($"s-{correo}", correo));
        Assert.NotEqual(HttpStatusCode.Forbidden, (await nav.GetAsync("/api/clientes")).StatusCode);
        Assert.False((await nav.GetFromJsonAsync<EstadoLegal>("/api/legal/estado"))!.Exigida);
    }
}
