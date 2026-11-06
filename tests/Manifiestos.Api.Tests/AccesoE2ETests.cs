using Manifiestos.Api.Acceso;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Manifiestos.Api.Tests;

/// <summary>
/// Acceso de prueba para Playwright: el validador de tokens "e2e|correo|nombre" solo existe con el
/// entorno E2E y Acceso:PermitirTokensE2E = true; en cualquier otro entorno esa opción impide arrancar.
/// No necesita base de datos: solo se construye el contenedor de servicios.
/// </summary>
public sealed class AccesoE2ETests
{
    private sealed class ApiEnEntorno(string entorno, string? permitirTokensE2E) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment(entorno);
            builder.UseSetting("ConnectionStrings:Manifiestos", "Host=127.0.0.1;Port=1;Database=sin_uso;Username=nadie;Password=x");
            builder.UseSetting("Acceso:GoogleClientId", "cliente-ficticio.apps.googleusercontent.com");
            builder.UseSetting("Acceso:OrganizacionId", Guid.NewGuid().ToString());
            if (permitirTokensE2E is not null) builder.UseSetting("Acceso:PermitirTokensE2E", permitirTokensE2E);
        }
    }

    private static IValidadorTokenGoogle Validador(string entorno, string? permitir)
    {
        using var api = new ApiEnEntorno(entorno, permitir);
        return api.Services.GetRequiredService<IValidadorTokenGoogle>();
    }

    [Theory]
    [InlineData("Development", null)]
    [InlineData("Production", null)]
    [InlineData("Development", "false")]
    [InlineData("Production", "false")]
    [InlineData("E2E", null)]
    [InlineData("E2E", "false")]
    public void Sin_la_opcion_activa_se_usa_el_validador_de_Google(string entorno, string? permitir)
    {
        Assert.IsType<ValidadorTokenGoogle>(Validador(entorno, permitir));
    }

    [Theory]
    [InlineData("Development")]
    [InlineData("Production")]
    [InlineData("Staging")]
    [InlineData("Testing")]
    public void Activar_tokens_E2E_fuera_del_entorno_E2E_impide_arrancar(string entorno)
    {
        using var api = new ApiEnEntorno(entorno, "true");
        var ex = Assert.ThrowsAny<Exception>(() => api.Services);
        Assert.Contains("PermitirTokensE2E", ex.ToString());
    }

    [Fact]
    public void Solo_en_E2E_con_la_opcion_activa_se_registra_el_validador_de_prueba()
    {
        Assert.IsType<ValidadorTokenE2E>(Validador("E2E", "true"));
    }

    [Fact]
    public async Task El_validador_E2E_acepta_solo_su_formato_con_correo_verificado()
    {
        var v = Validador("E2E", "true");
        var id = await v.ValidarAsync("e2e|Persona@Example.invalid|Persona E2E", CancellationToken.None);
        Assert.NotNull(id);
        Assert.Equal(ValidadorTokenE2E.Emisor, id.Emisor);
        Assert.NotEqual(ValidadorTokenGoogle.EmisorGoogle, id.Emisor);
        Assert.Equal("e2e:persona@example.invalid", id.Sujeto);
        Assert.True(id.CorreoVerificado);
        Assert.Equal("Persona E2E", id.Nombre);

        string[] rechazados =
        [
            "", "e2e", "e2e|persona@example.invalid", "e2e|persona@example.invalid|", "e2e|sin-arroba|Nombre",
            "E2E|persona@example.invalid|Nombre", "otro|persona@example.invalid|Nombre",
            "s-1|persona@example.invalid|True|Nombre", "e2e|persona@example.invalid|Nombre|extra",
            "eyJhbGciOiJSUzI1NiJ9.e30.firma", "e2e|" + new string('a', 600) + "@example.invalid|Nombre",
        ];
        foreach (var t in rechazados) Assert.Null(await v.ValidarAsync(t, CancellationToken.None));
    }

    [Fact]
    public void El_validador_E2E_no_se_puede_construir_fuera_de_E2E()
    {
        using var api = new ApiEnEntorno("Production", null);
        var env = api.Services.GetRequiredService<Microsoft.Extensions.Hosting.IHostEnvironment>();
        Assert.Throws<InvalidOperationException>(() => new ValidadorTokenE2E(env));
    }

    [Fact]
    public void Manifiestos_BaseDatos_sustituye_solo_el_nombre_de_la_base()
    {
        const string cadena = "Host=127.0.0.1;Port=55432;Database=manifiestos;Username=manifiestos_api;Password=x";
        var sin = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Manifiestos"] = cadena,
        }).Build();
        Assert.Equal(cadena, ServiciosAcceso.CadenaConexion(sin));

        var con = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Manifiestos"] = cadena,
            ["Manifiestos:BaseDatos"] = " manifiestos_demo ",
        }).Build();
        var b = new NpgsqlConnectionStringBuilder(ServiciosAcceso.CadenaConexion(con));
        Assert.Equal("manifiestos_demo", b.Database);
        Assert.Equal("127.0.0.1", b.Host);
        Assert.Equal(55432, b.Port);
        Assert.Equal("manifiestos_api", b.Username);
    }
}
