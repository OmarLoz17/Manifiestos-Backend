using Npgsql;

namespace Manifiestos.Api.Archivos;

/// <summary>
/// Retira del VPS los archivos de evidencia que ya tienen copia verificada en Drive hace al menos <c>Archivos:RetencionDias</c> días
/// (0 = desactivado; mínimo efectivo 7). Así el disco solo guarda lo reciente y Drive es el archivo definitivo.
/// Nunca toca un archivo sin copia verificada en Drive (lo decide la base) ni uno cuyo tamaño no coincide con lo registrado.
/// El panel sigue sirviendo el archivo retirado leyéndolo de Drive. Solo corre si Drive está activo.
/// </summary>
public sealed class LimpiezaArchivosLocales(NpgsqlDataSource datos, IAlmacenDrive drive, IConfiguration config, IHostEnvironment entorno, ILogger<LimpiezaArchivosLocales> log) : BackgroundService
{
    public const int RetencionMinima = 7;
    private const int Lote = 200;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var dias = config.GetValue("Archivos:RetencionDias", 0);
        if (dias <= 0) { log.LogInformation("Limpieza de archivos locales desactivada (Archivos:RetencionDias = 0): el VPS conserva todo."); return; }
        if (!drive.Activo) { log.LogWarning("Archivos:RetencionDias está definido pero Drive no está activo: no se limpia nada."); return; }
        if (dias < RetencionMinima) { log.LogWarning("Archivos:RetencionDias = {Dias} es menor que el mínimo; se usan {Minimo} días.", dias, RetencionMinima); dias = RetencionMinima; }
        var cada = TimeSpan.FromSeconds(Math.Max(60, config.GetValue("Archivos:SegundosEntreLimpiezas", 3600)));
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var (retirados, bytes) = await LimpiarAsync(dias, ct);
                if (retirados > 0) log.LogInformation("Limpieza de archivos locales: {Archivos} retirados del VPS ({Mib} MiB), ya archivados en Drive.", retirados, bytes / 1048576);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception e) { log.LogWarning(e, "La limpieza de archivos locales falló; se reintenta."); }
            try { await Task.Delay(cada, ct); } catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>Una pasada: retira los archivos elegibles (por lotes). Devuelve cuántos se retiraron y cuántos bytes.</summary>
    public async Task<(int Archivos, long Bytes)> LimpiarAsync(int dias, CancellationToken ct)
    {
        var raiz = Path.GetFullPath(config["Archivos:Ruta"] ?? Path.GetFullPath(Path.Combine(entorno.ContentRootPath, "..", "..", ".local", "archivos")));
        var prefijo = raiz.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        int total = 0; long bytes = 0;
        for (var lotes = 0; lotes < 50; lotes++)
        {
            var candidatos = new List<(Guid Org, Guid Suc, Guid Archivo, string Clave, long Tamano)>();
            await using (var cmd = datos.CreateCommand("SELECT organizacion_id, sucursal_id, archivo_id, clave_vps, tamano_bytes FROM sync.archivos_para_limpieza($1, $2)"))
            {
                cmd.Parameters.AddWithValue(dias); cmd.Parameters.AddWithValue(Lote);
                await using var r = await cmd.ExecuteReaderAsync(ct);
                while (await r.ReadAsync(ct)) candidatos.Add((r.GetGuid(0), r.GetGuid(1), r.GetGuid(2), r.GetString(3), r.GetInt64(4)));
            }
            if (candidatos.Count == 0) break;
            var avance = 0;
            foreach (var c in candidatos)
            {
                try
                {
                    var ruta = Path.GetFullPath(Path.Combine(raiz, c.Clave));
                    // Solo dentro de la carpeta de archivos y solo si el archivo es el que la base describe.
                    if (!ruta.StartsWith(prefijo, StringComparison.Ordinal)) { log.LogWarning("Se omite un archivo con clave fuera de la carpeta de archivos."); continue; }
                    long liberado = 0;
                    if (File.Exists(ruta))
                    {
                        liberado = new FileInfo(ruta).Length;
                        if (liberado != c.Tamano) { log.LogWarning("Se omite un archivo cuyo tamaño en disco no coincide con el registrado."); continue; }
                        File.Delete(ruta);
                    }
                    await using var ok = datos.CreateCommand("SELECT sync.registrar_archivo_local_limpiado($1, $2, $3)");
                    ok.Parameters.AddWithValue(c.Org); ok.Parameters.AddWithValue(c.Suc); ok.Parameters.AddWithValue(c.Archivo);
                    await ok.ExecuteNonQueryAsync(ct);
                    avance++; if (liberado > 0) { total++; bytes += liberado; }
                }
                catch (Exception e) when (e is not OperationCanceledException) { log.LogWarning("No se pudo retirar un archivo del VPS: {Motivo}", e.Message); }
            }
            if (avance == 0) break;   // nada avanzó (todo omitido): no se vuelve a pedir el mismo lote
        }
        return (total, bytes);
    }
}
