namespace Manifiestos.Api.Acceso;

/// <summary>
/// Validador sustituto SOLO para las pruebas de punta a punta (Playwright). Acepta exactamente
/// tokens "e2e|correo|nombre" con el correo marcado como verificado; cualquier otro texto se rechaza.
/// Solo se registra con el entorno "E2E" y Acceso:PermitirTokensE2E = true; si esa opción está activa
/// en otro entorno, el arranque falla. El resto del acceso (lista de autorizados, cookie, CSRF y
/// revalidación) es el real. Las identidades usan un emisor propio, distinto del de Google,
/// para que nunca se confundan con una cuenta real.
/// </summary>
public sealed class ValidadorTokenE2E : IValidadorTokenGoogle
{
    public const string Entorno = "E2E";
    public const string Emisor = "urn:manifiestos:e2e";
    private const string Prefijo = "e2e";
    private const int LargoMaximo = 512;

    public ValidadorTokenE2E(IHostEnvironment entorno)
    {
        // Segunda barrera: aunque alguien lo registrara a mano, fuera de E2E no se puede construir.
        if (!entorno.IsEnvironment(Entorno))
            throw new InvalidOperationException($"El validador de tokens E2E solo puede usarse en el entorno {Entorno}.");
    }

    public Task<IdentidadGoogle?> ValidarAsync(string idToken, CancellationToken ct) => Task.FromResult(Leer(idToken));

    private static IdentidadGoogle? Leer(string? token)
    {
        if (string.IsNullOrEmpty(token) || token.Length > LargoMaximo) return null;
        var partes = token.Split('|');
        if (partes.Length != 3 || partes[0] != Prefijo) return null;
        var correo = partes[1].Trim();
        var nombre = partes[2].Trim();
        if (!CorreoConForma(correo) || nombre.Length == 0) return null;
        return new IdentidadGoogle(Emisor, "e2e:" + correo.ToLowerInvariant(), correo, CorreoVerificado: true, nombre);
    }

    /// <summary>Forma básica algo@dominio.ext, sin espacios.</summary>
    private static bool CorreoConForma(string correo)
    {
        var arroba = correo.IndexOf('@');
        if (arroba <= 0 || arroba != correo.LastIndexOf('@') || correo.Any(char.IsWhiteSpace)) return false;
        var dominio = correo[(arroba + 1)..];
        var punto = dominio.LastIndexOf('.');
        return punto > 0 && punto < dominio.Length - 1;
    }
}
