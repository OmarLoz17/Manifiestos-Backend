using System.Net;
using System.Net.Http.Json;
using Manifiestos.Api.Acceso;
using Manifiestos.Api.Tests.Infraestructura;

namespace Manifiestos.Api.Tests;

/// <summary>Login web de extremo a extremo contra PostgreSQL real, con el validador de Google sustituido.</summary>
[Trait("Categoria", "Integracion")]
public sealed class AccesoWebTests : IClassFixture<BaseDatosPrueba>, IAsyncLifetime
{
    private readonly BaseDatosPrueba _bd;
    private ApiPrueba _api = null!;

    public AccesoWebTests(BaseDatosPrueba bd) => _bd = bd;

    public Task InitializeAsync() { _api = new ApiPrueba(_bd); return Task.CompletedTask; }
    public async Task DisposeAsync() => await _api.DisposeAsync();

    private static string Correo(string prefijo) => $"{prefijo}-{Guid.NewGuid():N}@example.invalid";

    private static async Task AfirmarNoAutorizadaAsync(HttpResponseMessage r)
    {
        Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
        var cuerpo = await r.Content.ReadFromJsonAsync<MensajeError>();
        Assert.Equal(EndpointsAcceso.MensajeNoAutorizada, cuerpo!.Mensaje);
        Assert.False(r.Headers.Contains("Set-Cookie") &&
                     r.Headers.GetValues("Set-Cookie").Any(v => v.StartsWith("manifiestos.sesion=") && !v.Contains("expires=Thu, 01 Jan 1970")));
    }

    [Theory]
    [InlineData("token-con-firma-invalida")]
    [InlineData("token-expirado")]
    [InlineData("token-con-audiencia-de-otro-cliente")]
    [InlineData("")]
    public async Task Token_rechazado_por_el_validador_responde_401_sin_sesion(string token)
    {
        var nav = _api.NuevoNavegador();
        var r = await nav.EntrarAsync(token);
        Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await nav.GetAsync("/api/auth/me")).StatusCode);
    }

    [Fact]
    public async Task Sin_token_CSRF_no_procesa_el_inicio()
    {
        var correo = Correo("csrf");
        await _bd.AutorizarAsync(correo);
        var r = await _api.NuevoNavegador().EntrarAsync(ValidadorFicticio.Token("s-csrf", correo), conCsrf: false);
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Equal("t", await _bd.ConsultarAsync($"SELECT identidad_id IS NULL FROM seguridad.acceso_autorizado WHERE correo_permitido='{correo}'"));
    }

    [Fact]
    public async Task Correo_no_verificado_no_entra_ni_vincula()
    {
        var correo = Correo("noverif");
        await _bd.AutorizarAsync(correo);
        await AfirmarNoAutorizadaAsync(await _api.NuevoNavegador().EntrarAsync(ValidadorFicticio.Token("s-noverif", correo, verificado: false)));
        Assert.Equal("t", await _bd.ConsultarAsync($"SELECT identidad_id IS NULL FROM seguridad.acceso_autorizado WHERE correo_permitido='{correo}'"));
    }

    [Fact]
    public async Task Cuenta_fuera_de_la_lista_recibe_mensaje_generico()
    {
        await AfirmarNoAutorizadaAsync(await _api.NuevoNavegador().EntrarAsync(ValidadorFicticio.Token("s-fuera", Correo("fuera"))));
    }

    [Fact]
    public async Task Entrada_inactiva_recibe_el_mismo_mensaje()
    {
        var correo = Correo("inactiva");
        await _bd.AutorizarAsync(correo, activo: false);
        await AfirmarNoAutorizadaAsync(await _api.NuevoNavegador().EntrarAsync(ValidadorFicticio.Token("s-inactiva", correo)));
    }

    [Fact]
    public async Task Cuenta_de_telefono_de_operador_no_entra_al_panel_web()
    {
        var correo = Correo("telefono");
        await _bd.AutorizarAsync(correo, rol: "telefono_operador");
        await AfirmarNoAutorizadaAsync(await _api.NuevoNavegador().EntrarAsync(ValidadorFicticio.Token("s-telefono", correo)));
        Assert.Equal("t", await _bd.ConsultarAsync($"SELECT identidad_id IS NULL FROM seguridad.acceso_autorizado WHERE correo_permitido='{correo}'"));
    }

    [Fact]
    public async Task Primer_acceso_vincula_y_me_devuelve_nombre_correo_rol_y_sucursal()
    {
        var correo = Correo("primer");
        var sujeto = "s-" + Guid.NewGuid().ToString("N");
        await _bd.AutorizarAsync(correo);
        var nav = _api.NuevoNavegador();

        Assert.Equal(HttpStatusCode.NoContent, (await nav.EntrarAsync(ValidadorFicticio.Token(sujeto, correo.ToUpperInvariant(), nombre: "Nombre Ficticio"))).StatusCode);
        var yo = await nav.GetFromJsonAsync<UsuarioActual>("/api/auth/me");
        Assert.Equal("Nombre Ficticio", yo!.Nombre);
        Assert.Equal(correo, yo.Correo);
        Assert.Equal("operaciones", yo.Rol.Codigo);
        Assert.Equal([BaseDatosPrueba.NombreSucursal], yo.Sucursales.Select(s => s.Nombre));

        // Segundo acceso desde otro navegador: reconoce por sujeto, sin duplicar identidad.
        Assert.Equal(HttpStatusCode.NoContent, (await _api.NuevoNavegador().EntrarAsync(ValidadorFicticio.Token(sujeto, correo))).StatusCode);
        Assert.Equal("1", await _bd.ConsultarAsync($"SELECT count(*) FROM seguridad.identidad_externa WHERE sujeto='{sujeto}'"));
    }

    [Fact]
    public async Task Doble_primer_acceso_simultaneo_crea_una_sola_identidad()
    {
        var correo = Correo("doble");
        var sujeto = "s-" + Guid.NewGuid().ToString("N");
        await _bd.AutorizarAsync(correo);
        var token = ValidadorFicticio.Token(sujeto, correo);
        var respuestas = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => _api.NuevoNavegador().EntrarAsync(token)));
        Assert.All(respuestas, r => Assert.Equal(HttpStatusCode.NoContent, r.StatusCode));
        Assert.Equal("1", await _bd.ConsultarAsync($"SELECT count(*) FROM seguridad.identidad_externa WHERE sujeto='{sujeto}'"));
    }

    [Fact]
    public async Task Dos_cuentas_distintas_con_el_mismo_correo_a_la_vez_solo_vincula_una()
    {
        var correo = Correo("carrera");
        await _bd.AutorizarAsync(correo);
        var a = "s-" + Guid.NewGuid().ToString("N");
        var b = "s-" + Guid.NewGuid().ToString("N");
        var respuestas = await Task.WhenAll(
            _api.NuevoNavegador().EntrarAsync(ValidadorFicticio.Token(a, correo)),
            _api.NuevoNavegador().EntrarAsync(ValidadorFicticio.Token(b, correo)));
        Assert.Single(respuestas, r => r.StatusCode == HttpStatusCode.NoContent);
        Assert.Single(respuestas, r => r.StatusCode == HttpStatusCode.Forbidden);
        Assert.Equal("1", await _bd.ConsultarAsync($"SELECT count(*) FROM seguridad.identidad_externa WHERE sujeto IN ('{a}','{b}')"));
    }

    [Fact]
    public async Task Baja_con_sesion_abierta_bloquea_la_siguiente_peticion()
    {
        var correo = Correo("baja");
        await _bd.AutorizarAsync(correo);
        var nav = _api.NuevoNavegador();
        Assert.Equal(HttpStatusCode.NoContent, (await nav.EntrarAsync(ValidadorFicticio.Token("s-" + Guid.NewGuid().ToString("N"), correo))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await nav.GetAsync("/api/auth/me")).StatusCode);

        await _bd.DesactivarAsync(correo);
        Assert.Equal(HttpStatusCode.Unauthorized, (await nav.GetAsync("/api/auth/me")).StatusCode);
    }

    [Fact]
    public async Task Cerrar_sesion_requiere_CSRF_y_termina_la_sesion()
    {
        var correo = Correo("salir");
        await _bd.AutorizarAsync(correo);
        var nav = _api.NuevoNavegador();
        Assert.Equal(HttpStatusCode.NoContent, (await nav.EntrarAsync(ValidadorFicticio.Token("s-" + Guid.NewGuid().ToString("N"), correo))).StatusCode);

        Assert.Equal(HttpStatusCode.BadRequest, (await nav.SalirAsync(conCsrf: false)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await nav.GetAsync("/api/auth/me")).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await nav.SalirAsync()).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await nav.GetAsync("/api/auth/me")).StatusCode);
    }

    [Fact]
    public async Task La_cookie_de_sesion_es_HttpOnly_Secure_y_SameSite_Strict()
    {
        var correo = Correo("cookie");
        await _bd.AutorizarAsync(correo);
        var r = await _api.NuevoNavegador().EntrarAsync(ValidadorFicticio.Token("s-" + Guid.NewGuid().ToString("N"), correo));
        var cookie = r.Headers.GetValues("Set-Cookie").Single(v => v.StartsWith("manifiestos.sesion="));
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("secure", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", cookie, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Sin_sesion_me_responde_401()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await _api.NuevoNavegador().GetAsync("/api/auth/me")).StatusCode);
    }
}
