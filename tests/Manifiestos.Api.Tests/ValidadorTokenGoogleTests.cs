using Manifiestos.Api.Acceso;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Manifiestos.Api.Tests;

/// <summary>
/// Lo que se puede comprobar sin un token firmado por Google: la configuración que se
/// entrega a la librería oficial y el rechazo de entradas mal formadas.
/// </summary>
public sealed class ValidadorTokenGoogleTests
{
    private static ValidadorTokenGoogle Crear() => new(
        Options.Create(new OpcionesAcceso { GoogleClientId = "cliente-ficticio.apps.googleusercontent.com", OrganizacionId = Guid.NewGuid() }),
        NullLogger<ValidadorTokenGoogle>.Instance);

    [Fact]
    public void La_audiencia_exigida_es_solo_el_Client_ID_configurado()
    {
        var c = Crear().CrearConfiguracion();
        Assert.Equal(["cliente-ficticio.apps.googleusercontent.com"], c.Audience);
        Assert.True(c.ExpirationTimeClockTolerance <= TimeSpan.FromMinutes(1));
        Assert.Null(c.HostedDomain); // Operadores usan @gmail.com: la lista local decide, no el dominio.
    }

    [Theory]
    [InlineData("")]
    [InlineData("no-es-un-jwt")]
    [InlineData("a.b.c")]
    [InlineData("e30.e30.firma")]           // JSON vacío, sin campos
    [InlineData("%%%.%%%.%%%")]
    public async Task Token_mal_formado_se_rechaza(string token)
    {
        Assert.Null(await Crear().ValidarAsync(token, CancellationToken.None));
    }
}
