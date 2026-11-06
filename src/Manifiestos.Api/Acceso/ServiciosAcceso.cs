using System.Security.Claims;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Manifiestos.Api.Acceso;

/// <summary>
/// Sesión web propia: cookie de autenticación de ASP.NET Core (ADR 0007).
/// HttpOnly + Secure + SameSite=Strict, CSRF por cabecera y revalidación del
/// acceso en la base en cada petición autenticada.
/// </summary>
public static class ServiciosAcceso
{
    public const string ClaimAcceso = "manifiestos:acceso";
    public const string ClaimOrganizacion = "manifiestos:organizacion";
    public const string PoliticaLogin = "login";
    public const string CabeceraCsrf = "X-CSRF-TOKEN";
    internal const string ItemSesion = "manifiestos:sesion";

    public static IServiceCollection AgregarAccesoWeb(this IServiceCollection services)
    {
        services.AddOptions<OpcionesAcceso>().BindConfiguration(OpcionesAcceso.Seccion)
            .ValidateDataAnnotations()
            .Validate(o => o.OrganizacionId != Guid.Empty, "Acceso:OrganizacionId es obligatorio.")
            // Los tokens de prueba nunca se aceptan fuera del entorno E2E: el arranque falla.
            .Validate<IHostEnvironment>((o, env) => !o.PermitirTokensE2E || env.IsEnvironment(ValidadorTokenE2E.Entorno),
                $"Acceso:PermitirTokensE2E solo se admite con el entorno {ValidadorTokenE2E.Entorno}. " +
                "Quítalo de la configuración de este entorno.")
            .ValidateOnStart();

        services.AddSingleton(sp => NpgsqlDataSource.Create(CadenaConexion(sp.GetRequiredService<IConfiguration>())));
        services.AddSingleton<IRepositorioAcceso, RepositorioAcceso>();
        services.AddSingleton<ValidadorTokenGoogle>();
        services.AddSingleton<IValidadorTokenGoogle>(sp =>
        {
            var env = sp.GetRequiredService<IHostEnvironment>();
            var acceso = sp.GetRequiredService<IOptions<OpcionesAcceso>>().Value;
            if (!acceso.PermitirTokensE2E) return sp.GetRequiredService<ValidadorTokenGoogle>();
            if (!env.IsEnvironment(ValidadorTokenE2E.Entorno))
                throw new InvalidOperationException($"Acceso:PermitirTokensE2E solo se admite con el entorno {ValidadorTokenE2E.Entorno}.");
            sp.GetRequiredService<ILoggerFactory>().CreateLogger("Manifiestos.Acceso")
                .LogWarning("Tokens de prueba E2E habilitados: el inicio de sesión NO usa Google. Solo para pruebas automatizadas.");
            return new ValidadorTokenE2E(env);
        });

        services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie();
        services.AddOptions<CookieAuthenticationOptions>(CookieAuthenticationDefaults.AuthenticationScheme)
            .Configure<IOptions<OpcionesAcceso>>((o, acceso) =>
            {
                o.Cookie.Name = "manifiestos.sesion";
                o.Cookie.HttpOnly = true;
                o.Cookie.SecurePolicy = CookieSecurePolicy.Always;
                o.Cookie.SameSite = SameSiteMode.Strict;
                o.ExpireTimeSpan = TimeSpan.FromMinutes(acceso.Value.MinutosSesion);
                o.SlidingExpiration = true;
                // API: sin redirecciones HTML.
                o.Events.OnRedirectToLogin = c => { c.Response.StatusCode = 401; return Task.CompletedTask; };
                o.Events.OnRedirectToAccessDenied = c => { c.Response.StatusCode = 403; return Task.CompletedTask; };
                o.Events.OnValidatePrincipal = RevalidarAsync;
            });

        // Todo endpoint exige sesión salvo los marcados explícitamente como anónimos.
        services.AddAuthorizationBuilder()
            .SetFallbackPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());

        services.AddAntiforgery(o =>
        {
            o.HeaderName = CabeceraCsrf;
            o.Cookie.Name = "manifiestos.csrf";
            o.Cookie.HttpOnly = true;
            o.Cookie.SecurePolicy = CookieSecurePolicy.Always;
            o.Cookie.SameSite = SameSiteMode.Strict;
        });
        // Antiforgery lanza excepción si la cookie exige Secure y la petición no es HTTPS.
        // En desarrollo local Vite reenvía por http a localhost; solo ahí se relaja ESTA cookie.
        // La cookie de sesión sigue siendo Secure siempre. En producción (Cloudflare Tunnel)
        // habrá que configurar ForwardedHeaders para que la petición se reconozca como HTTPS.
        // Las pruebas E2E también corren por http://localhost.
        services.AddOptions<AntiforgeryOptions>().Configure<IHostEnvironment>((o, env) =>
        {
            if (env.IsDevelopment() || env.IsEnvironment(ValidadorTokenE2E.Entorno))
                o.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        });

        services.AddRateLimiter(o =>
        {
            o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            o.AddPolicy(PoliticaLogin, http =>
            {
                var limite = http.RequestServices.GetRequiredService<IOptions<OpcionesAcceso>>().Value.IntentosPorMinuto;
                return RateLimitPartition.GetFixedWindowLimiter(
                    http.Connection.RemoteIpAddress?.ToString() ?? "sin-ip",
                    _ => new FixedWindowRateLimiterOptions { PermitLimit = limite, Window = TimeSpan.FromMinutes(1) });
            });
        });
        return services;
    }

    /// <summary>
    /// ConnectionStrings:Manifiestos; si existe Manifiestos:BaseDatos, sustituye el nombre de la base
    /// (p. ej. manifiestos_demo o una base temporal de pruebas) sin tocar User Secrets.
    /// </summary>
    public static string CadenaConexion(IConfiguration config)
    {
        var cadena = config.GetConnectionString("Manifiestos")
            ?? throw new InvalidOperationException("Falta ConnectionStrings:Manifiestos (User Secrets).");
        var baseDatos = config["Manifiestos:BaseDatos"];
        if (string.IsNullOrWhiteSpace(baseDatos)) return cadena;
        return new NpgsqlConnectionStringBuilder(cadena) { Database = baseDatos.Trim() }.ConnectionString;
    }

    public static ClaimsPrincipal CrearPrincipal(Guid organizacion, Guid acceso, string nombre) => new(
        new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, acceso.ToString()),
            new Claim(ClaimAcceso, acceso.ToString()),
            new Claim(ClaimOrganizacion, organizacion.ToString()),
            new Claim(ClaimTypes.Name, nombre),
        ], CookieAuthenticationDefaults.AuthenticationScheme));

    public static bool LeerIds(ClaimsPrincipal? p, out Guid organizacion, out Guid acceso)
    {
        acceso = Guid.Empty;
        return Guid.TryParse(p?.FindFirstValue(ClaimOrganizacion), out organizacion)
            & Guid.TryParse(p?.FindFirstValue(ClaimAcceso), out acceso);
    }

    /// <summary>Una baja, un cambio de rol o la pérdida de alcance cierran la sesión en la siguiente petición.</summary>
    private static async Task RevalidarAsync(CookieValidatePrincipalContext c)
    {
        var http = c.HttpContext;
        var config = http.RequestServices.GetRequiredService<IOptions<OpcionesAcceso>>().Value;
        SesionVigente? sesion = null;
        if (LeerIds(c.Principal, out var org, out var acceso) && org == config.OrganizacionId)
        {
            sesion = await http.RequestServices.GetRequiredService<IRepositorioAcceso>()
                .ObtenerSesionAsync(org, acceso, http.RequestAborted);
        }
        if (sesion is null)
        {
            c.RejectPrincipal();
            await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return;
        }
        http.Items[ItemSesion] = sesion;
    }
}
