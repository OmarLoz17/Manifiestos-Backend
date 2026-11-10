using System.Net.Http.Json;
using Manifiestos.Api.Acceso;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Manifiestos.Api.Tests.Infraestructura;

/// <summary>
/// Sustituto del validador de Google. Formato del token ficticio:
/// "sujeto|correo|verificado|nombre". Cualquier otro texto = token inválido
/// (equivale a firma, emisor, audiencia o vigencia rechazados por la librería oficial).
/// </summary>
public sealed class ValidadorFicticio : IValidadorTokenGoogle
{
    public static string Token(string sujeto, string correo, bool verificado = true, string nombre = "Persona Ficticia", string? nonce = null) =>
        $"{sujeto}|{correo}|{verificado}|{nombre}" + (nonce is null ? "" : $"|{nonce}");

    public Task<IdentidadGoogle?> ValidarAsync(string idToken, CancellationToken ct)
    {
        var p = idToken.Split('|');
        return Task.FromResult(p.Length is 4 or 5 && bool.TryParse(p[2], out var v)
            ? new IdentidadGoogle(ValidadorTokenGoogle.EmisorGoogle, p[0], p[1], v, p[3], p.Length==5 ? p[4] : null)
            : null);
    }
}

public sealed class ApiPrueba(BaseDatosPrueba bd) : WebApplicationFactory<Program>
{
    /// <summary>Organización que atiende la API de prueba (la de BaseDatosPrueba salvo que la prueba siembre otra).</summary>
    public Guid Organizacion { get; init; } = BaseDatosPrueba.Organizacion;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // "Testing" evita cargar User Secrets del desarrollador.
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:Manifiestos", bd.CadenaConexion);
        // Fija también Manifiestos:BaseDatos: si el shell tuviera Manifiestos__BaseDatos (p. ej. la demo),
        // CadenaConexion sustituiría el nombre de la base y la API de prueba escribiría fuera de la temporal.
        builder.UseSetting("Manifiestos:BaseDatos", bd.Nombre);
        builder.UseSetting("Acceso:GoogleClientId", "cliente-ficticio.apps.googleusercontent.com");
        builder.UseSetting("Acceso:OrganizacionId", Organizacion.ToString());
        builder.UseSetting("Acceso:IntentosPorMinuto", "1000");
        builder.ConfigureTestServices(s => s.AddSingleton<IValidadorTokenGoogle, ValidadorFicticio>());
    }

    /// <summary>Navegador ficticio con su propio almacén de cookies, sobre https (cookies Secure).</summary>
    public HttpClient NuevoNavegador() =>
        CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false });
}

public static class ExtensionesNavegador
{
    public static async Task<string> CsrfAsync(this HttpClient c)
    {
        var r = await c.GetFromJsonAsync<RespuestaCsrf>("/api/auth/csrf");
        return r!.Token;
    }

    public static async Task<HttpResponseMessage> EntrarAsync(this HttpClient c, string token, bool conCsrf = true)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/auth/google") { Content = JsonContent.Create(new { credential = token }) };
        if (conCsrf) req.Headers.Add(ServiciosAcceso.CabeceraCsrf, await c.CsrfAsync());
        return await c.SendAsync(req);
    }

    public static async Task<HttpResponseMessage> SalirAsync(this HttpClient c, bool conCsrf = true)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/auth/logout");
        if (conCsrf) req.Headers.Add(ServiciosAcceso.CabeceraCsrf, await c.CsrfAsync());
        return await c.SendAsync(req);
    }
}
