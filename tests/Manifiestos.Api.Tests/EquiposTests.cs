using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Manifiestos.Api.Acceso;
using Manifiestos.Api.Operadores;
using Manifiestos.Api.Tests.Infraestructura;

namespace Manifiestos.Api.Tests;

/// <summary>
/// Alta de equipos por solicitud (0070) contra PostgreSQL real: solo correos ya autorizados pueden solicitar, Operaciones
/// aprueba o rechaza, reasignar cierra la entrega anterior y exige confirmación, y nada se habilita por la solicitud sola.
/// Datos ficticios.
/// </summary>
[Trait("Categoria", "Integracion")]
public sealed class EquiposTests(BaseDatosPrueba bd) : IClassFixture<BaseDatosPrueba>, IAsyncLifetime
{
    private static readonly Guid Org = BaseDatosPrueba.Organizacion;
    private static readonly Guid Suc = BaseDatosPrueba.Sucursal;
    private ApiPrueba _api = null!;
    public Task InitializeAsync() { _api = new ApiPrueba(bd); return Task.CompletedTask; }
    public async Task DisposeAsync() => await _api.DisposeAsync();

    private sealed record Operador(string Correo, string Sujeto, Guid Persona, string Nombre);
    private sealed record Tokens(string AccessToken, string RefreshToken);
    private sealed record Estado(string Estado_);

    /// <summary>Operador con cuenta autorizada y ficha vigente, SIN equipo (todavía no entregado).</summary>
    private async Task<Operador> NuevoOperadorAsync(string rol = "telefono_operador", bool activo = true, bool conFicha = true, bool conEquipoPrevio = true)
    {
        var o = new Operador($"equipo-{Guid.NewGuid():N}@example.invalid", $"s-{Guid.NewGuid():N}", Guid.NewGuid(), $"Operador {Guid.NewGuid():N}"[..20]);
        await bd.AutorizarAsync(o.Correo, rol, activo, conSucursal: false);
        if (conFicha)
            await bd.SqlComoDuenoAsync($"""
                INSERT INTO seguridad.persona(organizacion_id,id,nombre_completo) VALUES('{Org}','{o.Persona}','{o.Nombre}');
                INSERT INTO negocio.operador_ficha(organizacion_id,sucursal_id,persona_id,revision,nombre_completo,correo)
                VALUES('{Org}','{Suc}','{o.Persona}',1,'{o.Nombre}','{o.Correo}');
                """);
        // Con un equipo anterior (ya devuelto) el siguiente teléfono necesita aprobación; sin él, el primero se registra solo (0073).
        if (conFicha && conEquipoPrevio)
            await bd.SqlComoDuenoAsync($"""
                WITH d AS (INSERT INTO seguridad.dispositivo(organizacion_id) VALUES('{Org}') RETURNING id)
                INSERT INTO seguridad.asignacion_equipo(organizacion_id,sucursal_id,dispositivo_id,acceso_id,persona_id,revision,desde,hasta)
                SELECT '{Org}','{Suc}',d.id,a.id,'{o.Persona}',1,now()-interval '2 days',now()-interval '1 day'
                FROM d, seguridad.acceso_autorizado a WHERE a.organizacion_id='{Org}' AND a.correo_permitido='{o.Correo}';
                """);
        return o;
    }

    private async Task<HttpClient> PanelAsync(bool escribir)
    {
        var correo = $"panel-{Guid.NewGuid():N}@example.invalid";
        await bd.AutorizarAsync(correo, escribir ? "administrador" : "operaciones", escribir: escribir);
        var nav = _api.NuevoNavegador();
        Assert.Equal(HttpStatusCode.NoContent, (await nav.EntrarAsync(ValidadorFicticio.Token($"s-{correo}", correo))).StatusCode);
        return nav;
    }

    private static async Task<HttpResponseMessage> PostPanelAsync(HttpClient nav, string url, object? cuerpo = null, bool conCsrf = true)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(cuerpo ?? new { }) };
        if (conCsrf) req.Headers.Add(ServiciosAcceso.CabeceraCsrf, await nav.CsrfAsync());
        return await nav.SendAsync(req);
    }

    private static async Task<DesafioMovil> DesafioAsync(HttpClient c, string ruta, Guid instalacion) =>
        (await (await c.PostAsJsonAsync($"/api/movil/auth/{ruta}", new { instalacionId = instalacion })).Content.ReadFromJsonAsync<DesafioMovil>())!;

    private static async Task<HttpResponseMessage> SolicitarAsync(HttpClient c, Operador o, Guid instalacion)
    {
        var d = await DesafioAsync(c, "solicitar-desafio", instalacion);
        return await c.PostAsJsonAsync("/api/movil/auth/solicitar-equipo",
            new SolicitudGoogleMovil(ValidadorFicticio.Token(o.Sujeto, o.Correo, nonce: d.Nonce), instalacion, d.Id));
    }

    private static async Task<HttpResponseMessage> EntrarAsync(HttpClient c, Operador o, Guid instalacion)
    {
        var d = await (await c.PostAsJsonAsync("/api/movil/auth/desafio", new { instalacionId = instalacion })).Content.ReadFromJsonAsync<DesafioMovil>();
        return await c.PostAsJsonAsync("/api/movil/auth/google",
            new SolicitudGoogleMovil(ValidadorFicticio.Token(o.Sujeto, o.Correo, nonce: d!.Nonce), instalacion, d.Id));
    }

    private static async Task<ListaEquipos> ListaAsync(HttpClient panel) => (await panel.GetFromJsonAsync<ListaEquipos>("/api/equipos"))!;
    private Task<string> PendientesAsync(Operador o) => bd.ConsultarAsync(
        $"SELECT count(*) FROM seguridad.solicitud_equipo s JOIN seguridad.acceso_autorizado a ON a.id=s.acceso_id AND a.organizacion_id=s.organizacion_id WHERE a.correo_permitido='{o.Correo}' AND s.estado='pendiente'");

    [Fact]
    public async Task Equipo_nuevo_solicita_operaciones_aprueba_y_el_operador_entra()
    {
        var o = await NuevoOperadorAsync(); var instalacion = Guid.NewGuid(); var tel = _api.NuevoNavegador();
        // Sin vincular: no hay desafío de login ni sesión; la solicitud no habilita nada por sí sola.
        Assert.Equal(HttpStatusCode.Forbidden, (await tel.PostAsJsonAsync("/api/movil/auth/desafio", new { instalacionId = instalacion })).StatusCode);
        var r = await SolicitarAsync(tel, o, instalacion);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal("pendiente", (await r.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>()).GetProperty("estado").GetString());
        Assert.Equal("0", await bd.ConsultarAsync($"SELECT count(*) FROM seguridad.instalacion_app WHERE id='{instalacion}'"));
        // Repetir la solicitud no la duplica.
        Assert.Equal(HttpStatusCode.OK, (await SolicitarAsync(tel, o, instalacion)).StatusCode);
        Assert.Equal("1", await PendientesAsync(o));

        using var panel = await PanelAsync(escribir: true);
        var pendiente = Assert.Single((await ListaAsync(panel)).Solicitudes, s => s.Correo == o.Correo);
        Assert.Equal(o.Persona, pendiente.PersonaId); Assert.Equal(o.Nombre, pendiente.Nombre); Assert.Null(pendiente.PersonaActual);
        Assert.False((await PostPanelAsync(panel, $"/api/equipos/solicitudes/{pendiente.Id}/aprobar", conCsrf: false)).IsSuccessStatusCode);
        Assert.Equal(HttpStatusCode.OK, (await PostPanelAsync(panel, $"/api/equipos/solicitudes/{pendiente.Id}/aprobar")).StatusCode);

        var entrada = await EntrarAsync(tel, o, instalacion);
        Assert.Equal(HttpStatusCode.OK, entrada.StatusCode);
        var tokens = (await entrada.Content.ReadFromJsonAsync<Tokens>())!;
        tel.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);
        var yo = (await tel.GetFromJsonAsync<UsuarioMovil>("/api/movil/auth/me"))!;
        Assert.Equal(o.Persona, yo.Operador.PersonaId); Assert.Equal(instalacion, yo.Operador.InstalacionId); Assert.Equal(1, yo.Operador.RevisionEquipo);
        Assert.DoesNotContain((await ListaAsync(panel)).Solicitudes, s => s.Id == pendiente.Id);
        Assert.Contains((await ListaAsync(panel)).Equipos, e => e.PersonaId == o.Persona && e.Correo == o.Correo);
        // Aprobar de nuevo ya no existe; el operador sigue sin permisos del panel.
        Assert.Equal(HttpStatusCode.NotFound, (await PostPanelAsync(panel, $"/api/equipos/solicitudes/{pendiente.Id}/aprobar")).StatusCode);
        Assert.Equal("0", await bd.ConsultarAsync($"SELECT count(*) FROM seguridad.permiso_sucursal p JOIN seguridad.acceso_autorizado a ON a.id=p.acceso_id AND a.organizacion_id=p.organizacion_id WHERE a.correo_permitido='{o.Correo}'"));
    }

    [Fact]
    public async Task Primer_equipo_de_la_cuenta_se_registra_solo_y_el_segundo_queda_pendiente()
    {
        var o = await NuevoOperadorAsync(conEquipoPrevio: false);
        var primero = Guid.NewGuid(); var segundo = Guid.NewGuid(); var tel1 = _api.NuevoNavegador(); var tel2 = _api.NuevoNavegador();

        var r = await SolicitarAsync(tel1, o, primero);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal("vinculado", (await r.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>()).GetProperty("estado").GetString());
        Assert.Equal("0", await PendientesAsync(o));
        Assert.Equal("1", await bd.ConsultarAsync($"SELECT count(*) FROM seguridad.instalacion_app WHERE id='{primero}' AND habilitada"));
        Assert.Equal("1", await bd.ConsultarAsync($"SELECT count(*) FROM seguridad.administracion_historial WHERE accion='registro_automatico' AND detalle->>'correo'='{o.Correo}'"));
        Assert.Equal(HttpStatusCode.OK, (await EntrarAsync(tel1, o, primero)).StatusCode);

        // Repetir en el mismo teléfono no crea nada nuevo.
        Assert.Equal(HttpStatusCode.OK, (await SolicitarAsync(tel1, o, primero)).StatusCode);
        Assert.Equal("1", await bd.ConsultarAsync($"SELECT count(*) FROM seguridad.asignacion_equipo ae JOIN seguridad.acceso_autorizado a ON a.id=ae.acceso_id WHERE a.correo_permitido='{o.Correo}'"));

        // Otro teléfono: ya no se registra solo, queda pendiente y no puede entrar.
        var r2 = await SolicitarAsync(tel2, o, segundo);
        Assert.Equal("pendiente", (await r2.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>()).GetProperty("estado").GetString());
        Assert.Equal("0", await bd.ConsultarAsync($"SELECT count(*) FROM seguridad.instalacion_app WHERE id='{segundo}'"));
        Assert.Equal(HttpStatusCode.Forbidden, (await tel2.PostAsJsonAsync("/api/movil/auth/desafio", new { instalacionId = segundo })).StatusCode);
        Assert.Equal("1", await PendientesAsync(o));
    }

    [Fact]
    public async Task El_registro_automatico_no_aplica_a_cuentas_no_habilitadas_ni_a_equipos_deshabilitados()
    {
        var tel = _api.NuevoNavegador();
        // Sin ficha, inactiva o de Operaciones: nada se registra.
        foreach (var o in new[] { await NuevoOperadorAsync(conFicha: false), await NuevoOperadorAsync(activo: false, conEquipoPrevio: false), await NuevoOperadorAsync("operaciones", conEquipoPrevio: false) })
        {
            var i = Guid.NewGuid();
            Assert.Equal(HttpStatusCode.Forbidden, (await SolicitarAsync(tel, o, i)).StatusCode);
            Assert.Equal("0", await bd.ConsultarAsync($"SELECT count(*) FROM seguridad.instalacion_app WHERE id='{i}'"));
        }
        // Una instalación que ya existe (deshabilitada) no se reactiva ni se reasigna sola.
        var cuenta = await NuevoOperadorAsync(conEquipoPrevio: false); var existente = Guid.NewGuid();
        await bd.SqlComoDuenoAsync($"""
            WITH d AS (INSERT INTO seguridad.dispositivo(organizacion_id) VALUES('{Org}') RETURNING id)
            INSERT INTO seguridad.instalacion_app(organizacion_id,id,dispositivo_id,habilitada) SELECT '{Org}','{existente}',d.id,false FROM d;
            """);
        Assert.Equal(HttpStatusCode.Forbidden, (await SolicitarAsync(tel, cuenta, existente)).StatusCode);
        Assert.Equal("0", await bd.ConsultarAsync($"SELECT count(*) FROM seguridad.asignacion_equipo ae JOIN seguridad.acceso_autorizado a ON a.id=ae.acceso_id WHERE a.correo_permitido='{cuenta.Correo}'"));
    }

    [Fact]
    public async Task Dos_cuentas_no_comparten_el_primer_equipo()
    {
        var a = await NuevoOperadorAsync(conEquipoPrevio: false); var b = await NuevoOperadorAsync(conEquipoPrevio: false);
        var instalacion = Guid.NewGuid(); var tel = _api.NuevoNavegador();
        Assert.Equal("vinculado", (await (await SolicitarAsync(tel, a, instalacion)).Content.ReadFromJsonAsync<System.Text.Json.JsonElement>()).GetProperty("estado").GetString());
        // B intenta en el teléfono de A: ese teléfono ya es de A, no se le entrega solo; queda para aprobación del administrador.
        var r = await SolicitarAsync(tel, b, instalacion);
        Assert.Equal("pendiente", (await r.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>()).GetProperty("estado").GetString());
        Assert.Equal("1", await PendientesAsync(b));
    }

    [Fact]
    public async Task Solo_correos_autorizados_con_ficha_pueden_solicitar()
    {
        var tel = _api.NuevoNavegador(); var instalacion = Guid.NewGuid();
        var fuera = new Operador($"fuera-{Guid.NewGuid():N}@example.invalid", $"s-{Guid.NewGuid():N}", Guid.NewGuid(), "x");
        Assert.Equal(HttpStatusCode.Forbidden, (await SolicitarAsync(tel, fuera, instalacion)).StatusCode);
        foreach (var o in new[] { await NuevoOperadorAsync(activo: false), await NuevoOperadorAsync("operaciones"), await NuevoOperadorAsync(conFicha: false) })
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await SolicitarAsync(tel, o, instalacion)).StatusCode);
            Assert.Equal("0", await PendientesAsync(o));
        }
        Assert.Equal("0", await bd.ConsultarAsync($"SELECT count(*) FROM seguridad.solicitud_equipo WHERE instalacion_id='{instalacion}'"));
        // Token inválido, correo sin verificar y desafío repetido o ajeno tampoco crean nada.
        var bueno = await NuevoOperadorAsync(); var d = await DesafioAsync(tel, "solicitar-desafio", instalacion);
        Assert.Equal(HttpStatusCode.Unauthorized, (await tel.PostAsJsonAsync("/api/movil/auth/solicitar-equipo", new SolicitudGoogleMovil("basura", instalacion, d.Id))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await tel.PostAsJsonAsync("/api/movil/auth/solicitar-equipo",
            new SolicitudGoogleMovil(ValidadorFicticio.Token(bueno.Sujeto, bueno.Correo, verificado: false, nonce: d.Nonce), instalacion, d.Id))).StatusCode);
        var valida = new SolicitudGoogleMovil(ValidadorFicticio.Token(bueno.Sujeto, bueno.Correo, nonce: d.Nonce), instalacion, d.Id);
        Assert.Equal(HttpStatusCode.OK, (await tel.PostAsJsonAsync("/api/movil/auth/solicitar-equipo", valida)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await tel.PostAsJsonAsync("/api/movil/auth/solicitar-equipo", valida)).StatusCode);   // nonce de un solo uso
        var otro = await DesafioAsync(tel, "solicitar-desafio", Guid.NewGuid());
        Assert.Equal(HttpStatusCode.Forbidden, (await tel.PostAsJsonAsync("/api/movil/auth/solicitar-equipo",
            new SolicitudGoogleMovil(ValidadorFicticio.Token(bueno.Sujeto, bueno.Correo, nonce: otro.Nonce), instalacion, otro.Id))).StatusCode);
    }

    [Fact]
    public async Task Rechazar_deja_el_equipo_sin_acceso_y_permite_pedirlo_otra_vez()
    {
        var o = await NuevoOperadorAsync(); var instalacion = Guid.NewGuid(); var tel = _api.NuevoNavegador();
        Assert.Equal(HttpStatusCode.OK, (await SolicitarAsync(tel, o, instalacion)).StatusCode);
        using var panel = await PanelAsync(escribir: true);
        var s = Assert.Single((await ListaAsync(panel)).Solicitudes, x => x.Correo == o.Correo);
        Assert.Equal(HttpStatusCode.OK, (await PostPanelAsync(panel, $"/api/equipos/solicitudes/{s.Id}/rechazar")).StatusCode);
        Assert.Equal("0", await PendientesAsync(o));
        Assert.Equal(HttpStatusCode.Forbidden, (await tel.PostAsJsonAsync("/api/movil/auth/desafio", new { instalacionId = instalacion })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await PostPanelAsync(panel, $"/api/equipos/solicitudes/{s.Id}/aprobar")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await SolicitarAsync(tel, o, instalacion)).StatusCode);
        Assert.Equal("1", await PendientesAsync(o));
    }

    [Fact]
    public async Task Solo_el_administrador_ve_aprueba_o_rechaza()
    {
        var o = await NuevoOperadorAsync(); var instalacion = Guid.NewGuid(); var tel = _api.NuevoNavegador();
        Assert.Equal(HttpStatusCode.OK, (await SolicitarAsync(tel, o, instalacion)).StatusCode);
        using var lector = await PanelAsync(escribir: false);   // Operaciones: ya no ve Equipos
        Assert.Equal(HttpStatusCode.Forbidden, (await lector.GetAsync("/api/equipos")).StatusCode);
        using var admin = await PanelAsync(escribir: true);
        var s = Assert.Single((await ListaAsync(admin)).Solicitudes, x => x.Correo == o.Correo);
        Assert.Equal(HttpStatusCode.Forbidden, (await PostPanelAsync(lector, $"/api/equipos/solicitudes/{s.Id}/aprobar")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await PostPanelAsync(lector, $"/api/equipos/solicitudes/{s.Id}/rechazar")).StatusCode);
        Assert.Equal("1", await PendientesAsync(o));
        Assert.Equal("0", await bd.ConsultarAsync($"SELECT count(*) FROM seguridad.instalacion_app WHERE id='{instalacion}'"));
        // Un Bearer móvil no sirve en el panel de equipos.
        Assert.Equal(HttpStatusCode.Unauthorized, (await tel.GetAsync("/api/equipos")).StatusCode);
    }

    [Fact]
    public async Task Gmail_reciclado_reasigna_el_equipo_con_confirmacion_y_conserva_la_entrega_anterior()
    {
        var a = await NuevoOperadorAsync(); var b = await NuevoOperadorAsync(); var instalacion = Guid.NewGuid();
        var telA = _api.NuevoNavegador(); using var panel = await PanelAsync(escribir: true);
        Assert.Equal(HttpStatusCode.OK, (await SolicitarAsync(telA, a, instalacion)).StatusCode);
        var sa = Assert.Single((await ListaAsync(panel)).Solicitudes, x => x.Correo == a.Correo);
        Assert.Equal(HttpStatusCode.OK, (await PostPanelAsync(panel, $"/api/equipos/solicitudes/{sa.Id}/aprobar")).StatusCode);
        var entradaA = await EntrarAsync(telA, a, instalacion);
        Assert.Equal(HttpStatusCode.OK, entradaA.StatusCode);
        telA.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", (await entradaA.Content.ReadFromJsonAsync<Tokens>())!.AccessToken);

        // La misma instalación (otra persona recibe el teléfono): el login de B ya no sirve y B solicita.
        var telB = _api.NuevoNavegador();
        Assert.Equal(HttpStatusCode.Forbidden, (await EntrarAsync(telB, b, instalacion)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await SolicitarAsync(telB, b, instalacion)).StatusCode);
        var sb = Assert.Single((await ListaAsync(panel)).Solicitudes, x => x.Correo == b.Correo);
        Assert.Equal(a.Nombre, sb.PersonaActual);
        // Sin confirmar no cambia nada.
        Assert.Equal(HttpStatusCode.Conflict, (await PostPanelAsync(panel, $"/api/equipos/solicitudes/{sb.Id}/aprobar")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await telA.GetAsync("/api/movil/auth/me")).StatusCode);
        Assert.Equal("1", await bd.ConsultarAsync($"SELECT count(*) FROM seguridad.asignacion_equipo ae JOIN seguridad.instalacion_app i ON i.dispositivo_id=ae.dispositivo_id WHERE i.id='{instalacion}'"));
        Assert.Equal(HttpStatusCode.OK, (await PostPanelAsync(panel, $"/api/equipos/solicitudes/{sb.Id}/aprobar", new { confirmarReasignacion = true })).StatusCode);

        // La entrega de A queda cerrada (no se borra) y la de B abre la revisión 2; la sesión de A deja de servir.
        Assert.Equal("2", await bd.ConsultarAsync($"SELECT count(*) FROM seguridad.asignacion_equipo ae JOIN seguridad.instalacion_app i ON i.dispositivo_id=ae.dispositivo_id WHERE i.id='{instalacion}'"));
        Assert.Equal("t", await bd.ConsultarAsync($"SELECT hasta IS NOT NULL FROM seguridad.asignacion_equipo ae JOIN seguridad.instalacion_app i ON i.dispositivo_id=ae.dispositivo_id WHERE i.id='{instalacion}' AND ae.revision=1"));
        Assert.Equal(HttpStatusCode.Forbidden, (await telA.GetAsync("/api/movil/auth/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await EntrarAsync(_api.NuevoNavegador(), a, instalacion)).StatusCode);
        var entradaB = await EntrarAsync(telB, b, instalacion);
        Assert.Equal(HttpStatusCode.OK, entradaB.StatusCode);
        telB.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", (await entradaB.Content.ReadFromJsonAsync<Tokens>())!.AccessToken);
        var yo = (await telB.GetFromJsonAsync<UsuarioMovil>("/api/movil/auth/me"))!;
        Assert.Equal(b.Persona, yo.Operador.PersonaId); Assert.Equal(2, yo.Operador.RevisionEquipo);
        Assert.Contains((await ListaAsync(panel)).Equipos, e => e.PersonaId == b.Persona);
        Assert.DoesNotContain((await ListaAsync(panel)).Equipos, e => e.PersonaId == a.Persona);
    }

    [Fact]
    public async Task Equipo_deshabilitado_no_se_reactiva_por_solicitud_y_vinculado_se_indica()
    {
        var o = await NuevoOperadorAsync(); var instalacion = Guid.NewGuid(); var tel = _api.NuevoNavegador();
        using var panel = await PanelAsync(escribir: true);
        Assert.Equal(HttpStatusCode.OK, (await SolicitarAsync(tel, o, instalacion)).StatusCode);
        var s = Assert.Single((await ListaAsync(panel)).Solicitudes, x => x.Correo == o.Correo);
        Assert.Equal(HttpStatusCode.OK, (await PostPanelAsync(panel, $"/api/equipos/solicitudes/{s.Id}/aprobar")).StatusCode);
        // Ya vinculado: la solicitud lo indica y no crea otra.
        var r = await SolicitarAsync(tel, o, instalacion);
        Assert.Equal("vinculado", (await r.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>()).GetProperty("estado").GetString());
        Assert.Equal("0", await PendientesAsync(o));
        // Administración lo deshabilita: pedir acceso no lo reactiva.
        await bd.SqlComoDuenoAsync($"UPDATE seguridad.instalacion_app SET habilitada=false WHERE id='{instalacion}'");
        Assert.Equal(HttpStatusCode.Forbidden, (await SolicitarAsync(tel, o, instalacion)).StatusCode);
        Assert.Equal("0", await PendientesAsync(o));
    }

    [Fact]
    public async Task Tope_de_solicitudes_pendientes_por_cuenta()
    {
        var o = await NuevoOperadorAsync(); var tel = _api.NuevoNavegador();
        for (var i = 0; i < 5; i++) Assert.Equal(HttpStatusCode.OK, (await SolicitarAsync(tel, o, Guid.NewGuid())).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await SolicitarAsync(tel, o, Guid.NewGuid())).StatusCode);
        Assert.Equal("5", await PendientesAsync(o));
    }
}
