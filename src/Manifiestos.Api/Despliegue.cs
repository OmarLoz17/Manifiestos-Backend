using Microsoft.AspNetCore.DataProtection;
using Npgsql;

namespace Manifiestos.Api;

/// <summary>
/// Piezas del despliegue en contenedores (ver backend/deploy y operacion.MD):
/// secretos montados como archivos, claves de sesión persistentes y comprobación de salud.
/// </summary>
public static class Despliegue
{
    /// <summary>
    /// Lee cada archivo del directorio de secretos (por defecto /run/secrets, o <c>Secretos:Ruta</c>) como una clave de configuración:
    /// el nombre del archivo es la clave y «__» equivale a «:» (p. ej. <c>ConnectionStrings__Manifiestos</c>). El valor es el contenido sin saltos finales.
    /// Así la contraseña de la base nunca viaja en variables de entorno ni queda en la imagen. Si el directorio no existe no hace nada.
    /// </summary>
    public static void AgregarSecretosEnArchivos(this WebApplicationBuilder builder)
    {
        var ruta = builder.Configuration["Secretos:Ruta"] ?? "/run/secrets";
        if (!Directory.Exists(ruta)) return;
        var valores = new Dictionary<string, string?>();
        foreach (var archivo in Directory.EnumerateFiles(ruta))
        {
            var nombre = Path.GetFileName(archivo);
            // Solo nombres con forma de clave de configuración; el resto de secretos (llaves PEM, credenciales JSON) se lee por su ruta.
            if (!nombre.Contains("__", StringComparison.Ordinal) || nombre.StartsWith('.')) continue;
            valores[nombre.Replace("__", ":", StringComparison.Ordinal)] = File.ReadAllText(archivo).TrimEnd('\r', '\n');
        }
        builder.Configuration.AddInMemoryCollection(valores);
    }

    /// <summary>
    /// Persiste las claves de Data Protection (cookies de sesión, CSRF y tokens móviles) en <c>DataProtection:Ruta</c>.
    /// Sin ellas, recrear el contenedor cerraría todas las sesiones y los tokens de renovación móviles. En Production la ruta es obligatoria.
    /// </summary>
    public static void AgregarClavesDeSesionPersistentes(this WebApplicationBuilder builder)
    {
        var ruta = builder.Configuration["DataProtection:Ruta"];
        if (string.IsNullOrWhiteSpace(ruta))
        {
            if (builder.Environment.IsProduction())
                throw new InvalidOperationException("Falta DataProtection:Ruta: sin claves persistentes cada despliegue cerraría las sesiones.");
            return;
        }
        Directory.CreateDirectory(ruta);
        builder.Services.AddDataProtection().SetApplicationName("Manifiestos").PersistKeysToFileSystem(new DirectoryInfo(ruta));
    }

    /// <summary>
    /// GET /api/salud: sin sesión. 200 si la base responde con el rol limitado y la llave del QR está cargada; 503 si no.
    /// No devuelve datos internos, solo «ok» o «no disponible».
    /// </summary>
    public static void MapearSalud(this IEndpointRouteBuilder app) =>
        app.MapGet("/api/salud", async (NpgsqlDataSource datos, Manifiestos.Api.Documentos.LlaveValidacion llave, CancellationToken ct) =>
        {
            try
            {
                await using var cmd = datos.CreateCommand("SELECT 1");
                await cmd.ExecuteScalarAsync(ct);
                return string.IsNullOrEmpty(llave.LlaveId) ? Results.StatusCode(503) : Results.Text("ok");
            }
            catch (Exception e) when (e is not OperationCanceledException) { return Results.StatusCode(503); }
        }).AllowAnonymous();
}
