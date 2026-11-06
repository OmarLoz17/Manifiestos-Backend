using Google.Apis.Auth;
using Microsoft.Extensions.Options;

namespace Manifiestos.Api.Acceso;

/// <summary>Identidad extraída de un ID token de Google ya validado.</summary>
public sealed record IdentidadGoogle(string Emisor, string Sujeto, string Correo, bool CorreoVerificado, string? Nombre);

/// <summary>Sustituible en pruebas. Devuelve null si el token no es válido.</summary>
public interface IValidadorTokenGoogle
{
    Task<IdentidadGoogle?> ValidarAsync(string idToken, CancellationToken ct);
}

/// <summary>
/// Usa la librería oficial de Google: firma con las claves públicas de Google, emisor,
/// audiencia = Client ID configurado y vigencia. No se implementa criptografía propia.
/// </summary>
public sealed class ValidadorTokenGoogle(IOptions<OpcionesAcceso> opciones, ILogger<ValidadorTokenGoogle> log)
    : IValidadorTokenGoogle
{
    /// <summary>Emisor normalizado: Google usa dos formas para el mismo emisor.</summary>
    public const string EmisorGoogle = "https://accounts.google.com";

    public GoogleJsonWebSignature.ValidationSettings CrearConfiguracion() => new()
    {
        Audience = [opciones.Value.GoogleClientId],
        IssuedAtClockTolerance = TimeSpan.FromSeconds(30),
        ExpirationTimeClockTolerance = TimeSpan.FromSeconds(30),
    };

    public async Task<IdentidadGoogle?> ValidarAsync(string idToken, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(idToken) || idToken.Length > 8192) return null;
        try
        {
            var p = await GoogleJsonWebSignature.ValidateAsync(idToken, CrearConfiguracion());
            if (string.IsNullOrWhiteSpace(p.Subject) || string.IsNullOrWhiteSpace(p.Email)) return null;
            return new IdentidadGoogle(EmisorGoogle, p.Subject, p.Email, p.EmailVerified, p.Name);
        }
        // Token mal formado o rechazado. Los fallos de red al obtener las claves de Google
        // no se capturan: son un problema del servidor, no del token.
        catch (Exception ex) when (ex is InvalidJwtException or FormatException or ArgumentException
                                       or Newtonsoft.Json.JsonException)
        {
            // Nunca registrar el token: solo el motivo general.
            log.LogInformation("ID token de Google rechazado: {Motivo}", ex.GetType().Name);
            return null;
        }
    }
}
