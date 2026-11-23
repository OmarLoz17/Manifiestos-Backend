using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Manifiestos.Api.Acceso;
using Manifiestos.Api.Avisos;
using Manifiestos.Api.Tests.Infraestructura;
using Microsoft.Extensions.DependencyInjection;

namespace Manifiestos.Api.Tests;

/// <summary>
/// Al cambiar el operador de una unidad (0071) los dos teléfonos reciben el aviso para descargar su trabajo: el operador nuevo, que ve la
/// ruta de hoy, y el que sale, que la deja de ver. Antes no avisaba nadie y el aviso de /api/rutas nunca se enviaba (consulta sin contexto).
/// Datos ficticios.
/// </summary>
[Trait("Categoria", "Integracion")]
public sealed class AvisosCambioOperadorTests(BaseDatosPrueba bd) : IClassFixture<BaseDatosPrueba>, IAsyncLifetime
{
    private static readonly Guid Org = BaseDatosPrueba.Organizacion, Suc = BaseDatosPrueba.Sucursal;
    private static string Hoy => DateOnly.FromDateTime(DateTime.UtcNow).ToString("yyyy-MM-dd");
    private ApiPrueba _api = null!;
    public Task InitializeAsync() { _api = new ApiPrueba(bd); return Task.CompletedTask; }
    public async Task DisposeAsync() => await _api.DisposeAsync();

    private sealed record Tokens(string TokenType, string AccessToken, int ExpiresIn, string RefreshToken);
    private sealed record Telefono(HttpClient Http, Guid Persona, string Token);

    /// <summary>Operador con equipo, sesión móvil y token de aviso registrado.</summary>
    private async Task<Telefono> TelefonoAsync()
    {
        var (correo, sujeto, persona, instalacion, dispositivo) = ($"aviso-{Guid.NewGuid():N}@example.invalid", $"s-{Guid.NewGuid():N}", Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        await bd.AutorizarAsync(correo, "telefono_operador", conSucursal: false);
        await bd.SqlComoDuenoAsync($"""
            INSERT INTO seguridad.persona(organizacion_id,id,nombre_completo) VALUES('{Org}','{persona}','Operador sintético');
            INSERT INTO negocio.operador_ficha(organizacion_id,sucursal_id,persona_id,revision,nombre_completo,correo) VALUES('{Org}','{Suc}','{persona}',1,'Operador sintético','{correo}');
            INSERT INTO seguridad.dispositivo(organizacion_id,id) VALUES('{Org}','{dispositivo}');
            INSERT INTO seguridad.instalacion_app(organizacion_id,id,dispositivo_id,habilitada) VALUES('{Org}','{instalacion}','{dispositivo}',true);
            INSERT INTO seguridad.asignacion_equipo(organizacion_id,sucursal_id,dispositivo_id,acceso_id,persona_id,revision,desde)
            SELECT organizacion_id,'{Suc}','{dispositivo}',id,'{persona}',1,now()-interval '1 minute' FROM seguridad.acceso_autorizado WHERE correo_permitido='{correo}';
            """);
        var http = _api.NuevoNavegador();
        var d = (await (await http.PostAsJsonAsync("/api/movil/auth/desafio", new { instalacionId = instalacion })).Content.ReadFromJsonAsync<DesafioMovil>())!;
        var r = await http.PostAsJsonAsync("/api/movil/auth/google", new SolicitudGoogleMovil(ValidadorFicticio.Token(sujeto, correo, true, nonce: d.Nonce), instalacion, d.Id));
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", (await r.Content.ReadFromJsonAsync<Tokens>())!.AccessToken);
        var token = "fcm-ficticio-" + Guid.NewGuid().ToString("N");
        Assert.Equal(HttpStatusCode.NoContent, (await http.PostAsJsonAsync("/api/movil/auth/token-aviso", new { token })).StatusCode);
        return new Telefono(http, persona, token);
    }

    /// <summary>Unidad con una ruta (días indicados, 127 = todos) y una parada; el operador inicial es <paramref name="operador"/>.</summary>
    private async Task<(Guid Unidad, Guid Ruta, Guid Jornada)> RutaAsync(Guid operador, int dias = 127)
    {
        var (ruta, unidad, parada, sitio, version, empresa, cliente, jornada) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        await bd.SqlComoDuenoAsync($"""
            BEGIN;
            INSERT INTO negocio.vehiculo VALUES ('{Org}', '{Suc}', '{unidad}', 'AVS-{unidad.ToString("N")[..5]}');
            INSERT INTO negocio.ficha_vehiculo(organizacion_id, sucursal_id, vehiculo_id, revision, capacidad_id, clasificacion) VALUES ('{Org}', '{Suc}', '{unidad}', 1, 2, 'fosas');
            INSERT INTO negocio.ruta(organizacion_id, sucursal_id, id) VALUES ('{Org}', '{Suc}', '{ruta}');
            INSERT INTO negocio.ruta_version(organizacion_id, sucursal_id, ruta_id, revision, nombre, dias, vehiculo_id) VALUES ('{Org}', '{Suc}', '{ruta}', 1, 'Ruta Aviso Ficticia', {dias}, '{unidad}');
            INSERT INTO negocio.asignacion_unidad(organizacion_id, sucursal_id, vehiculo_id, revision, persona_id, registrada_en) VALUES ('{Org}', '{Suc}', '{unidad}', 1, '{operador}', now() - interval '1 day');
            INSERT INTO negocio.empresa VALUES ('{Org}', '{empresa}', 'CLI-{empresa:N}');
            INSERT INTO negocio.ficha_empresa(organizacion_id, empresa_id, revision, nombre_comercial) VALUES ('{Org}', '{empresa}', 1, 'Cliente Aviso Ficticio');
            INSERT INTO negocio.empresa_sucursal VALUES ('{Org}', '{Suc}', '{empresa}');
            INSERT INTO negocio.cliente VALUES ('{Org}', '{Suc}', '{cliente}', '{empresa}', 'A-{cliente:N}');
            INSERT INTO negocio.sitio VALUES ('{Org}', '{Suc}', '{sitio}', 'Sitio aviso');
            INSERT INTO negocio.punto_servicio VALUES ('{Org}', '{Suc}', '{sitio}');
            INSERT INTO negocio.parada(organizacion_id, sucursal_id, id, ruta_id, posicion) VALUES ('{Org}', '{Suc}', '{parada}', '{ruta}', 1);
            INSERT INTO negocio.parada_version(organizacion_id, sucursal_id, id, parada_id, revision, sitio_id) VALUES ('{Org}', '{Suc}', '{version}', '{parada}', 1, '{sitio}');
            INSERT INTO negocio.parada_cliente(organizacion_id, sucursal_id, version_id, cliente_id, banos_planeados, fosas_planeadas) VALUES ('{Org}', '{Suc}', '{version}', '{cliente}', NULL, 1);
            INSERT INTO negocio.autorizacion_empresa(organizacion_id, empresa_id, autoridad, finalidad, numero) VALUES ('{Org}', '{empresa}', 'Medio ambiente', 'Generador', 'AUT-FICTICIA');
            INSERT INTO negocio.jornada(organizacion_id, sucursal_id, id, ruta_id, fecha) VALUES ('{Org}', '{Suc}', '{jornada}', '{ruta}', '{Hoy}');
            INSERT INTO negocio.estado_jornada(organizacion_id, sucursal_id, jornada_id, revision, estado) VALUES ('{Org}', '{Suc}', '{jornada}', 1, 'programada');
            COMMIT;
            """);
        return (unidad, ruta, jornada);
    }

    private async Task<HttpClient> PanelAsync()
    {
        var correo = $"panel-aviso-{Guid.NewGuid():N}@example.invalid";
        await bd.AutorizarAsync(correo, escribir: true);
        var nav = _api.NuevoNavegador();
        Assert.Equal(HttpStatusCode.NoContent, (await nav.EntrarAsync(ValidadorFicticio.Token($"s-{correo}", correo))).StatusCode);
        return nav;
    }

    private static async Task<HttpResponseMessage> CambiarOperadorAsync(HttpClient panel, Guid unidad, Guid operador, int revision)
    {
        var req = new HttpRequestMessage(HttpMethod.Put, $"/api/unidades/{unidad}/operador") { Content = JsonContent.Create(new { operadorId = operador, revision }) };
        req.Headers.Add(ServiciosAcceso.CabeceraCsrf, await panel.CsrfAsync());
        return await panel.SendAsync(req);
    }

    /// <summary>El aviso sale en segundo plano: espera a que llegue el número esperado de envíos.</summary>
    private async Task<List<string>> EsperarEnviosAsync(int esperados, Func<(string Token, AvisoPush Aviso), bool> filtro)
    {
        for (var i = 0; i < 60; i++)
        {
            var enviados = _api.Emisor.Enviados.Where(filtro).Select(x => x.Token).ToList();
            if (enviados.Count >= esperados) return enviados;
            await Task.Delay(100);
        }
        return _api.Emisor.Enviados.Where(filtro).Select(x => x.Token).ToList();
    }

    private static async Task<List<Guid>> JornadasAsync(HttpClient c)
    {
        var r = await c.GetAsync($"/api/movil/trabajo?fecha={Hoy}");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var t = JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement;
        return t.GetProperty("jornadas").EnumerateArray().Select(x => x.GetProperty("jornadaId").GetGuid()).ToList();
    }

    [Fact]
    public async Task Cambiar_el_operador_de_la_unidad_avisa_al_nuevo_y_al_que_sale_y_la_ruta_cambia_de_telefono()
    {
        var a = await TelefonoAsync(); var b = await TelefonoAsync();
        var (unidad, _, jornada) = await RutaAsync(a.Persona);
        Assert.Contains(jornada, await JornadasAsync(a.Http));
        Assert.DoesNotContain(jornada, await JornadasAsync(b.Http));
        using var panel = await PanelAsync();

        var r = await CambiarOperadorAsync(panel, unidad, b.Persona, revision: 1);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);

        var avisados = await EsperarEnviosAsync(2, x => x.Token == a.Token || x.Token == b.Token);
        Assert.Contains(a.Token, avisados);    // el que sale descarga y deja de ver la ruta
        Assert.Contains(b.Token, avisados);    // el nuevo descarga y la ve
        Assert.All(_api.Emisor.Enviados.Where(x => x.Token == a.Token || x.Token == b.Token), x => Assert.Equal("Cambió tu ruta de hoy.", x.Aviso.Texto));
        // Lo que descargarán: la ruta ya es del operador nuevo.
        Assert.Contains(jornada, await JornadasAsync(b.Http));
        Assert.DoesNotContain(jornada, await JornadasAsync(a.Http));
    }

    [Fact]
    public async Task Si_ninguna_ruta_de_la_unidad_sale_hoy_no_se_avisa_a_nadie()
    {
        var a = await TelefonoAsync(); var b = await TelefonoAsync();
        var hoy = AvisosTrabajo.HoyLocal();
        var mañana = (int)hoy.AddDays(1).DayOfWeek is var d && d == 0 ? 7 : (int)hoy.AddDays(1).DayOfWeek;
        var (unidad, _, _) = await RutaAsync(a.Persona, dias: 1 << (mañana - 1));
        using var panel = await PanelAsync();
        Assert.Equal(HttpStatusCode.OK, (await CambiarOperadorAsync(panel, unidad, b.Persona, 1)).StatusCode);
        await Task.Delay(1500);
        Assert.DoesNotContain(_api.Emisor.Enviados, x => x.Token == a.Token || x.Token == b.Token);
    }

    [Fact]
    public async Task El_aviso_de_cambios_de_ruta_ve_la_ruta_de_hoy_sin_contexto_de_usuario_y_no_avisa_a_quien_dejo_la_unidad_hace_tiempo()
    {
        var a = await TelefonoAsync(); var b = await TelefonoAsync();
        var (unidad, ruta, _) = await RutaAsync(a.Persona);
        // B recibió la unidad hace una hora: A ya no debe recibir avisos de esa ruta.
        await bd.SqlComoDuenoAsync($"INSERT INTO negocio.asignacion_unidad(organizacion_id, sucursal_id, vehiculo_id, revision, persona_id, registrada_en) VALUES ('{Org}', '{Suc}', '{unidad}', 2, '{b.Persona}', now() - interval '1 hour');");
        var avisos = _api.Services.GetRequiredService<AvisosTrabajo>();
        // Antes de 0071 esta consulta (sin contexto de usuario) no veía ninguna fila y el aviso nunca salía.
        await avisos.AvisarSiSaleHoyAsync(ruta, "Se agregó una parada a tu ruta de hoy.");
        var avisados = _api.Emisor.Enviados.Where(x => x.Token == a.Token || x.Token == b.Token).Select(x => x.Token).ToList();
        Assert.Equal([b.Token], avisados);
    }
}
