using System.ComponentModel.DataAnnotations;

namespace Manifiestos.Api.Acceso;

/// <summary>Configuración del acceso web. Valores reales en User Secrets o variables de entorno.</summary>
public sealed class OpcionesAcceso
{
    public const string Seccion = "Acceso";

    /// <summary>Client ID OAuth "Aplicación web". Público; no es un secreto.</summary>
    [Required, MinLength(10)]
    public string GoogleClientId { get; set; } = "";

    /// <summary>Organización única de esta instalación (provisional: un solo grupo por despliegue).</summary>
    [Required]
    public Guid OrganizacionId { get; set; }

    /// <summary>Duración de la sesión web sin actividad.</summary>
    [Range(5, 24 * 60)]
    public int MinutosSesion { get; set; } = 8 * 60;

    /// <summary>Intentos de inicio de sesión por minuto y dirección IP.</summary>
    [Range(1, 10_000)]
    public int IntentosPorMinuto { get; set; } = 10;

    /// <summary>
    /// Acepta tokens de prueba "e2e|correo|nombre" en lugar de Google (ver <see cref="ValidadorTokenE2E"/>).
    /// Solo se admite en el entorno "E2E"; en cualquier otro entorno el arranque falla.
    /// </summary>
    public bool PermitirTokensE2E { get; set; }
}
