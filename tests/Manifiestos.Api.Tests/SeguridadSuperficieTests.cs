using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using Manifiestos.Api.Acceso;
using Manifiestos.Api.Tests.Infraestructura;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.Extensions.DependencyInjection;

namespace Manifiestos.Api.Tests;

/// <summary>
/// Auditoría de seguridad de la superficie HTTP completa (no endpoint por endpoint): recorre TODAS las rutas registradas y comprueba
/// que (1) sin sesión ninguna responde datos salvo la lista explícita de públicas, (2) toda escritura del panel rechaza una petición
/// con sesión válida pero sin token CSRF, y (3) toda ruta de la app móvil exige token salvo la lista de acceso. Si alguien agrega un
/// endpoint nuevo y olvida el filtro, esta prueba falla sin tocarla. Datos ficticios.
/// </summary>
[Trait("Categoria", "Integracion")]
public sealed partial class SeguridadSuperficieTests(BaseDatosPrueba bd) : IClassFixture<BaseDatosPrueba>, IAsyncLifetime
{
    private ApiPrueba _api = null!;
    public Task InitializeAsync() { _api = new ApiPrueba(bd); return Task.CompletedTask; }
    public async Task DisposeAsync() => await _api.DisposeAsync();

    /// <summary>Públicas a propósito (sin sesión): cada una debe tener su motivo.</summary>
    private static readonly string[] Publicas =
    [
        "/api/auth/csrf", "/api/auth/google", "/api/auth/logout", "/api/salud",
        "/api/errores-cliente",
    ];
    private static readonly string[] PrefijosPublicos = ["/api/validacion/"];
    /// <summary>Acceso móvil previo al token (desafío, login, solicitud de equipo, renovación y versión de la app).</summary>
    private static readonly string[] MovilSinToken =
    [
        "/api/movil/auth/desafio", "/api/movil/auth/google", "/api/movil/auth/solicitar-desafio", "/api/movil/auth/solicitar-equipo",
        "/api/movil/auth/refresh", "/api/movil/app/version",
        // El APK es público a propósito: la sesión puede estar vencida y la app verifica su SHA-256 y su firma antes de instalar.
        "/api/movil/app/apk",
    ];

    [GeneratedRegex(@"\{([^}:?*]+)(?::([^}]+))?\}")]
    private static partial Regex Parametro();

    private static string Rellenar(string patron) =>
        Parametro().Replace(patron, m => m.Groups[2].Value == "guid" ? Guid.NewGuid().ToString()
            : m.Groups[1].Value.Contains("fecha", StringComparison.OrdinalIgnoreCase) ? "2026-01-05" : "x");

    private IEnumerable<(string Metodo, string Patron)> Rutas() =>
        _api.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
            .Where(e => e.RoutePattern.RawText?.StartsWith("/api/", StringComparison.Ordinal) == true)
            .SelectMany(e => (e.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods ?? ["GET"]).Select(m => (m, e.RoutePattern.RawText!)))
            .Distinct().ToList();

    private static bool EsPublica(string patron) =>
        Publicas.Contains(patron) || PrefijosPublicos.Any(p => patron.StartsWith(p, StringComparison.Ordinal)) || MovilSinToken.Contains(patron);

    [Fact]
    public void La_superficie_se_enumera_y_no_esta_vacia()
    {
        var rutas = Rutas().ToList();
        Assert.True(rutas.Count > 80, $"Solo se encontraron {rutas.Count} rutas: la enumeración no funciona.");
    }

    [Fact]
    public async Task Sin_sesion_toda_ruta_privada_responde_401()
    {
        var nav = _api.NuevoNavegador();
        var abiertas = new List<string>();
        foreach (var (metodo, patron) in Rutas().Where(r => !EsPublica(r.Patron)))
        {
            var r = await nav.SendAsync(new HttpRequestMessage(new HttpMethod(metodo), Rellenar(patron)) { Content = JsonContent.Create(new { }) });
            if (r.StatusCode != HttpStatusCode.Unauthorized) abiertas.Add($"{metodo} {patron} → {(int)r.StatusCode}");
        }
        Assert.True(abiertas.Count == 0, "Rutas privadas que responden sin sesión:\n" + string.Join("\n", abiertas));
    }

    [Fact]
    public async Task Toda_escritura_del_panel_con_sesion_valida_pero_sin_token_csrf_se_rechaza()
    {
        var correo = $"seg-{Guid.NewGuid():N}@example.invalid";
        await bd.AutorizarAsync(correo, "administrador", escribir: true);
        var nav = _api.NuevoNavegador();
        Assert.Equal(HttpStatusCode.NoContent, (await nav.EntrarAsync(ValidadorFicticio.Token($"s-{correo}", correo))).StatusCode);

        var sinProteccion = new List<string>();
        string[] escrituras = ["POST", "PUT", "DELETE", "PATCH"];
        foreach (var (metodo, patron) in Rutas().Where(r => escrituras.Contains(r.Metodo) && !r.Patron.StartsWith("/api/movil", StringComparison.Ordinal)))
        {
            // Se envía la sesión (cookie) pero ninguna cabecera X-CSRF-TOKEN.
            var r = await nav.SendAsync(new HttpRequestMessage(new HttpMethod(metodo), Rellenar(patron)) { Content = JsonContent.Create(new { }) });
            var cuerpo = await r.Content.ReadAsStringAsync();
            var rechazadaPorCsrf = r.StatusCode == HttpStatusCode.BadRequest && cuerpo.Contains("Recarga la página", StringComparison.Ordinal);
            // Las anónimas de solo lectura (comparar un PDF, registrar un error del navegador) no cambian estado de la cuenta.
            var exenta = patron == "/api/errores-cliente" || patron.EndsWith("/verificar", StringComparison.Ordinal);
            if (!rechazadaPorCsrf && !exenta) sinProteccion.Add($"{metodo} {patron} → {(int)r.StatusCode}");
        }
        Assert.True(sinProteccion.Count == 0, "Escrituras sin protección CSRF:\n" + string.Join("\n", sinProteccion));
    }

    [Fact]
    public async Task Toda_ruta_movil_exige_token_salvo_el_acceso()
    {
        var nav = _api.NuevoNavegador();
        var abiertas = new List<string>();
        foreach (var (metodo, patron) in Rutas().Where(r => r.Patron.StartsWith("/api/movil", StringComparison.Ordinal) && !MovilSinToken.Contains(r.Patron)))
        {
            var r = await nav.SendAsync(new HttpRequestMessage(new HttpMethod(metodo), Rellenar(patron)) { Content = JsonContent.Create(new { }) });
            if (r.StatusCode != HttpStatusCode.Unauthorized) abiertas.Add($"{metodo} {patron} → {(int)r.StatusCode}");
        }
        Assert.True(abiertas.Count == 0, "Rutas móviles abiertas sin token:\n" + string.Join("\n", abiertas));
    }

    [Fact]
    public async Task Los_permisos_por_pantalla_no_se_evaden_con_variantes_de_la_ruta()
    {
        // Administrador crea una cuenta que solo ve el Dashboard (sin pantallas con API); esa cuenta intenta Rutas y Clientes con variantes de mayúsculas, barras y puntos.
        var admin = $"adm-{Guid.NewGuid():N}@example.invalid";
        await bd.AutorizarAsync(admin, "administrador", escribir: true);
        var navAdmin = _api.NuevoNavegador();
        Assert.Equal(HttpStatusCode.NoContent, (await navAdmin.EntrarAsync(ValidadorFicticio.Token($"s-{admin}", admin))).StatusCode);
        async Task<HttpResponseMessage> Enviar(HttpClient n, HttpMethod m, string url, object cuerpo)
        {
            var req = new HttpRequestMessage(m, url) { Content = JsonContent.Create(cuerpo) };
            req.Headers.Add(ServiciosAcceso.CabeceraCsrf, await n.CsrfAsync());
            return await n.SendAsync(req);
        }
        Assert.Equal(HttpStatusCode.NoContent, (await Enviar(navAdmin, HttpMethod.Post, "/api/admin/confirmar", new { credential = ValidadorFicticio.Token($"s-{admin}", admin) })).StatusCode);
        var limitada = $"lim-{Guid.NewGuid():N}@example.invalid";
        var alta = await Enviar(navAdmin, HttpMethod.Post, "/api/admin/cuentas", new
        {
            correo = limitada, nombre = "Persona limitada", tipo = "operaciones",
            permisos = new[] { new { sucursalId = BaseDatosPrueba.Sucursal, pantallas = new Dictionary<string, string> { ["dashboard"] = "ver" } } },
        });
        Assert.Equal(HttpStatusCode.OK, alta.StatusCode);

        var nav = _api.NuevoNavegador();
        Assert.Equal(HttpStatusCode.NoContent, (await nav.EntrarAsync(ValidadorFicticio.Token($"s-{limitada}", limitada))).StatusCode);
        var evadidas = new List<string>();
        foreach (var ruta in new[]
        {
            "/api/incidencias", "/api/manifiestos", "/api/rutas", "/API/RUTAS", "/api/Rutas/", "/api//rutas", "/api/./rutas", "/api/x/../rutas", "/api/rutas%2F", "/api/%72utas", "/api/rutas;x=1",
            "/api/clientes", "/api/CLIENTES", "/api/unidades", "/api/operadores", "/api/sitios", "/api/plantas", "/api/ordenes", "/api/jornadas",
        })
        {
            HttpResponseMessage r;
            try { r = await nav.GetAsync(ruta); } catch (Exception) { continue; }
            // Permitido solo si el servidor ni siquiera la reconoce (404/400) o la niega; nunca una respuesta con datos.
            if (r.StatusCode is HttpStatusCode.OK or HttpStatusCode.NoContent) evadidas.Add($"GET {ruta} → {(int)r.StatusCode}");
        }
        Assert.True(evadidas.Count == 0, "Rutas que respondieron a una cuenta sin ese permiso:\n" + string.Join("\n", evadidas));
        // Las evidencias exigen alguna pantalla que las muestre: sin ella 403 (no 404), con ella la consulta llega a la base (404 si no existe).
        foreach (var ruta in new[] { $"/api/archivos/{Guid.NewGuid()}", $"/API/Archivos/{Guid.NewGuid()}" })
            Assert.Equal(HttpStatusCode.Forbidden, (await nav.GetAsync(ruta)).StatusCode);
        var conIncidencias = $"inc-{Guid.NewGuid():N}@example.invalid";
        Assert.Equal(HttpStatusCode.OK, (await Enviar(navAdmin, HttpMethod.Post, "/api/admin/cuentas", new
        {
            correo = conIncidencias, nombre = "Persona incidencias", tipo = "operaciones",
            permisos = new[] { new { sucursalId = BaseDatosPrueba.Sucursal, pantallas = new Dictionary<string, string> { ["incidencias"] = "ver" } } },
        })).StatusCode);
        var navInc = _api.NuevoNavegador();
        Assert.Equal(HttpStatusCode.NoContent, (await navInc.EntrarAsync(ValidadorFicticio.Token($"s-{conIncidencias}", conIncidencias))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await navInc.GetAsync($"/api/archivos/{Guid.NewGuid()}")).StatusCode);

        // La sesión sigue siendo válida (la prueba no pasa solo porque todo se niegue).
        Assert.Equal(HttpStatusCode.OK, (await nav.GetAsync("/api/auth/me")).StatusCode);
    }

    [Fact]
    public async Task AllowedHosts_rechaza_un_host_ajeno_y_acepta_el_declarado()
    {
        await using var restringida = _api.WithWebHostBuilder(b => b.UseSetting("AllowedHosts", "manifiestos.example.test;localhost"));
        var nav = restringida.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false });
        Assert.Equal(HttpStatusCode.OK, (await nav.GetAsync("/api/auth/csrf")).StatusCode);   // localhost declarado
        foreach (var host in new[] { "evil.example", "manifiestos.example.test.evil.example" })
        {
            var req = new HttpRequestMessage(HttpMethod.Get, "/api/auth/csrf");
            req.Headers.Host = host;
            Assert.Equal(HttpStatusCode.BadRequest, (await nav.SendAsync(req)).StatusCode);
        }
        var bueno = new HttpRequestMessage(HttpMethod.Get, "/api/auth/csrf");
        bueno.Headers.Host = "manifiestos.example.test";
        Assert.Equal(HttpStatusCode.OK, (await nav.SendAsync(bueno)).StatusCode);
    }

    [Fact]
    public async Task Una_cookie_manipulada_o_ajena_no_abre_sesion()
    {
        var nav = _api.NuevoNavegador();
        foreach (var valor in new[] { "x", "CfDJ8AAAA", new string('A', 4000), "../../etc/passwd" })
        {
            var req = new HttpRequestMessage(HttpMethod.Get, "/api/auth/me");
            req.Headers.Add("Cookie", $"manifiestos.sesion={valor}");
            Assert.Equal(HttpStatusCode.Unauthorized, (await nav.SendAsync(req)).StatusCode);
        }
        // Un token Bearer móvil inventado tampoco sirve en el panel ni en la app.
        var movil = new HttpRequestMessage(HttpMethod.Get, "/api/movil/trabajo");
        movil.Headers.Add("Authorization", "Bearer inventado");
        Assert.Equal(HttpStatusCode.Unauthorized, (await nav.SendAsync(movil)).StatusCode);
        var panel = new HttpRequestMessage(HttpMethod.Get, "/api/auth/me");
        panel.Headers.Add("Authorization", "Bearer inventado");
        Assert.Equal(HttpStatusCode.Unauthorized, (await nav.SendAsync(panel)).StatusCode);
    }

    [Fact]
    public async Task Cookies_y_cabeceras_de_la_api_son_seguras()
    {
        var nav = _api.NuevoNavegador();
        var r = await nav.GetAsync("/api/auth/csrf");
        var cookies = string.Join(";", r.Headers.GetValues("Set-Cookie"));
        Assert.Contains("httponly", cookies, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", cookies, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("no-store", r.Headers.CacheControl?.ToString());
        // El error de una ruta inexistente o de un cuerpo mal formado no devuelve rastros de pila.
        var mal = await nav.PostAsync("/api/auth/google", new StringContent("{no es json", System.Text.Encoding.UTF8, "application/json"));
        var texto = await mal.Content.ReadAsStringAsync();
        Assert.DoesNotContain("   at ", texto);
        Assert.DoesNotContain("Npgsql", texto);
    }
}
