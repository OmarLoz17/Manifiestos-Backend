using Manifiestos.Api.Acceso;
using Manifiestos.Api.Datos;
using Npgsql;

namespace Manifiestos.Api.Archivos;

/// <summary>
/// Archivos de evidencia para el panel: se sirven desde el backend (copia del VPS) a quien tenga acceso a la sucursal del archivo.
/// Sin enlaces públicos ni acceso del navegador a Drive. El acceso lo decide la política de la base (la consulta corre con el contexto de la cuenta).
/// </summary>
public static class EndpointsArchivos
{
    public static void MapearArchivos(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/archivos/{id:guid}", (Guid id, HttpContext http, NpgsqlDataSource datos, IConfiguration config, IWebHostEnvironment entorno, IAlmacenDrive drive) =>
            ServirAsync(id, http, datos, config, entorno, drive));
    }

    /// <summary>
    /// Sirve un archivo a quien tenga acceso a su sucursal: primero la copia del VPS y, si ya no está (limpieza por retención),
    /// la copia de Drive leída por el backend. Nunca hay enlaces públicos ni acceso del navegador a Drive.
    /// </summary>
    public static async Task<IResult> ServirAsync(Guid id, HttpContext http, NpgsqlDataSource datos, IConfiguration config, IWebHostEnvironment entorno, IAlmacenDrive drive)
    {
        if (!ServiciosAcceso.LeerIds(http.User, out var org, out var acceso)) return Results.Unauthorized();
        string mime; string? clave, enDrive;
        await using (var tx = await TransaccionContexto.AbrirAsync(datos, org, acceso, http.RequestAborted))
        {
            await using var cmd = tx.Comando("""
                SELECT a.mime, v.clave_externa, d.clave_externa FROM negocio.archivo a
                LEFT JOIN negocio.ubicacion_archivo v ON (v.organizacion_id, v.sucursal_id, v.archivo_id) = (a.organizacion_id, a.sucursal_id, a.id) AND v.proveedor = 'vps'
                LEFT JOIN negocio.ubicacion_archivo d ON (d.organizacion_id, d.sucursal_id, d.archivo_id) = (a.organizacion_id, a.sucursal_id, a.id) AND d.proveedor = 'drive'
                WHERE a.organizacion_id = $1 AND a.id = $2
                """, org, id);
            await using var r = await cmd.ExecuteReaderAsync(http.RequestAborted);
            if (!await r.ReadAsync(http.RequestAborted)) return Results.NotFound();
            (mime, clave, enDrive) = (r.GetString(0), r.IsDBNull(1) ? null : r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2));
        }
        http.Response.Headers.CacheControl = "private, no-store";
        http.Response.Headers["X-Content-Type-Options"] = "nosniff";
        if (clave is not null)
        {
            var raiz = config["Archivos:Ruta"] ?? Path.GetFullPath(Path.Combine(entorno.ContentRootPath, "..", "..", ".local", "archivos"));
            var ruta = Path.GetFullPath(Path.Combine(raiz, clave));
            if (ruta.StartsWith(Path.GetFullPath(raiz) + Path.DirectorySeparatorChar, StringComparison.Ordinal) && File.Exists(ruta)) return Results.File(ruta, mime);
        }
        if (enDrive is not null)
        {
            var bytes = await drive.LeerAsync(enDrive, http.RequestAborted);
            if (bytes is not null) return Results.File(bytes, mime);
        }
        return Results.NotFound();
    }
}
