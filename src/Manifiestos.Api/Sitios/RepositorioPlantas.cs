using Manifiestos.Api.Datos;
using Manifiestos.Api.Rutas;
using Npgsql;

namespace Manifiestos.Api.Sitios;

public sealed record DescargaReciente(DateTimeOffset Fecha, string Servicio, string? Manifiesto);

/// <summary>Planta tratadora: sitio de descarga (sin punto de servicio). Descargas = recepciones y tiradas ya registradas en ella.</summary>
public sealed record PlantaResumen(
    Guid Id, Guid SucursalId, int Revision, string? Nombre, string? Calle, string? NumeroExterior, string? Colonia,
    string? CodigoPostal, string? Municipio, string? Estado, decimal? Latitud, decimal? Longitud,
    int Descargas, DateTimeOffset? UltimaDescarga, IReadOnlyList<DescargaReciente> Recientes);

public sealed record SolicitudPlanta(
    Guid? SucursalId, int? Revision, string? Nombre, string? Calle, string? NumeroExterior, string? Colonia,
    string? CodigoPostal, string? Municipio, string? Estado, decimal? Latitud, decimal? Longitud);

public interface IRepositorioPlantas
{
    Task<IReadOnlyList<PlantaResumen>> ListarAsync(Guid organizacion, Guid acceso, CancellationToken ct);
    Task<ResultadoEscritura> CrearAsync(Guid organizacion, Guid acceso, Guid sucursal, DatosSitio datos, CancellationToken ct);
    Task<ResultadoEscritura> EditarAsync(Guid organizacion, Guid acceso, Guid planta, DatosSitio datos, int revision, CancellationToken ct);
    Task<ResultadoEscritura> EliminarAsync(Guid organizacion, Guid acceso, Guid planta, CancellationToken ct);
}

/// <summary>
/// Plantas tratadoras: un sitio que NO es punto de servicio (nunca es parada). Las fichas son revisiones nuevas; una planta sin
/// pendientes se da de baja (sitio_baja, se oculta sin borrar el historial); con capturas por revisar no se elimina. Solo se guarda el nombre y la dirección: los datos legales
/// de la planta siguen pendientes (PG-08).
/// </summary>
public sealed class RepositorioPlantas(NpgsqlDataSource datos) : IRepositorioPlantas
{
    public const string MensajeModificada = "Alguien más modificó esta planta. Recarga para ver los cambios.";
    public const string MensajePendiente = "No puedes eliminar una planta con capturas pendientes de revisar.";

    /// <summary>Capturas de descarga en una planta (recepción de fosas o tirada de sanitarios), sin las enviadas a revisión.</summary>
    private const string Descargas = """
        SELECT ct.planta_id, k.ocurrida_en AS fecha,
               CASE ct.tipo WHEN 'TIRADA_SANITARIOS' THEN 'Sanitarios' ELSE 'Fosas' END AS servicio,
               (SELECT v.numero_manifiesto FROM negocio.version_manifiesto v
                WHERE (v.organizacion_id, v.sucursal_id) = (ct.organizacion_id, ct.sucursal_id)
                  AND v.manifiesto_id = coalesce(
                      (SELECT tm.manifiesto_id FROM negocio.tirada_manifiesto tm WHERE (tm.organizacion_id, tm.sucursal_id, tm.tirada_id) = (ct.organizacion_id, ct.sucursal_id, ct.orden_derivada_id)),
                      (SELECT om.manifiesto_id FROM negocio.orden_manifiesto om WHERE (om.organizacion_id, om.sucursal_id, om.orden_derivada_id) = (ct.organizacion_id, ct.sucursal_id, ct.orden_derivada_id)))
                ORDER BY v.revision DESC LIMIT 1) AS manifiesto
        FROM negocio.captura_trabajo ct
        JOIN negocio.captura k ON (k.organizacion_id, k.sucursal_id, k.id) = (ct.organizacion_id, ct.sucursal_id, ct.captura_id)
        WHERE ct.organizacion_id = $1 AND ct.tipo IN ('RECEPCION_PLANTA', 'TIRADA_SANITARIOS') AND ct.planta_id IS NOT NULL
          AND NOT EXISTS (SELECT 1 FROM negocio.captura_por_revisar r WHERE (r.organizacion_id, r.sucursal_id, r.captura_id) = (ct.organizacion_id, ct.sucursal_id, ct.captura_id))
        """;

    private const string EsPlanta = """
        NOT EXISTS (SELECT 1 FROM negocio.punto_servicio ps WHERE (ps.organizacion_id, ps.sucursal_id, ps.sitio_id) = (s.organizacion_id, s.sucursal_id, s.id))
        """;

    public async Task<IReadOnlyList<PlantaResumen>> ListarAsync(Guid organizacion, Guid acceso, CancellationToken ct)
    {
        await using var tx = await TransaccionContexto.AbrirAsync(datos, organizacion, acceso, ct);
        var recientes = new Dictionary<string, List<DescargaReciente>>();
        var totales = new Dictionary<string, int>();
        await using (var cmd = tx.Comando($"SELECT planta_id, fecha, servicio, manifiesto FROM ({Descargas}) d ORDER BY fecha DESC", organizacion))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct))
            {
                var planta = r.GetString(0);
                totales[planta] = totales.GetValueOrDefault(planta) + 1;
                if (!recientes.TryGetValue(planta, out var l)) recientes[planta] = l = [];
                if (l.Count < 5) l.Add(new DescargaReciente(new DateTimeOffset(DateTime.SpecifyKind(r.GetDateTime(1), DateTimeKind.Utc)), r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3)));
            }

        var lista = new List<PlantaResumen>();
        await using (var cmd = tx.Comando($"""
            SELECT s.id, s.sucursal_id, coalesce(f.revision, 0), coalesce(nullif(btrim(f.nombre), ''), s.referencia),
                   f.calle, f.numero_exterior, f.colonia, f.codigo_postal, f.municipio, f.estado, f.latitud, f.longitud
            FROM negocio.sitio s
            LEFT JOIN LATERAL (SELECT * FROM negocio.ficha_sitio x
                WHERE (x.organizacion_id, x.sucursal_id, x.sitio_id) = (s.organizacion_id, s.sucursal_id, s.id)
                ORDER BY x.revision DESC LIMIT 1) f ON true
            WHERE s.organizacion_id = $1 AND {EsPlanta} AND {ReglasSitio.SinBaja}
            ORDER BY 4 NULLS LAST, s.id
            """, organizacion))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct))
            {
                string? T(int i) => r.IsDBNull(i) ? null : r.GetString(i);
                var id = r.GetGuid(0).ToString();
                var rec = recientes.GetValueOrDefault(id) ?? [];
                lista.Add(new PlantaResumen(r.GetGuid(0), r.GetGuid(1), r.GetInt32(2), T(3), T(4), T(5), T(6), T(7), T(8), T(9),
                    r.IsDBNull(10) ? null : r.GetDecimal(10), r.IsDBNull(11) ? null : r.GetDecimal(11),
                    totales.GetValueOrDefault(id), rec.Count > 0 ? rec[0].Fecha : null, rec));
            }
        await tx.ConfirmarAsync(ct);
        return lista;
    }

    public async Task<ResultadoEscritura> CrearAsync(Guid organizacion, Guid acceso, Guid sucursal, DatosSitio d, CancellationToken ct)
    {
        await using var tx = await TransaccionContexto.AbrirAsync(datos, organizacion, acceso, ct);
        if (!await PuedeEscribirAsync(tx, organizacion, sucursal, ct)) return ResultadoEscritura.SinPermiso;
        Guid id;
        await using (var cmd = tx.Comando("INSERT INTO negocio.sitio (organizacion_id, sucursal_id, referencia) VALUES ($1, $2, $3::text) RETURNING id",
                         organizacion, sucursal, Nulo(d.Nombre)))
            id = (Guid)(await cmd.ExecuteScalarAsync(ct))!;
        await InsertarFichaAsync(tx, organizacion, sucursal, id, 1, d, ct);
        await tx.ConfirmarAsync(ct);
        return ResultadoEscritura.Correcta(id, 1);
    }

    public async Task<ResultadoEscritura> EditarAsync(Guid organizacion, Guid acceso, Guid planta, DatosSitio d, int revision, CancellationToken ct)
    {
        try
        {
            await using var tx = await TransaccionContexto.AbrirAsync(datos, organizacion, acceso, ct);
            var (sucursal, actual) = await BloquearYLeerAsync(tx, organizacion, planta, ct);
            if (sucursal == Guid.Empty) return ResultadoEscritura.NoEncontrada;
            if (!await PuedeEscribirAsync(tx, organizacion, sucursal, ct)) return ResultadoEscritura.SinPermiso;
            if (actual != revision) return ResultadoEscritura.Conflicto(MensajeModificada);
            await InsertarFichaAsync(tx, organizacion, sucursal, planta, actual + 1, d, ct);
            await tx.ConfirmarAsync(ct);
            return ResultadoEscritura.Correcta(planta, actual + 1);
        }
        catch (PostgresException e) when (e.SqlState is PostgresErrorCodes.UniqueViolation or PostgresErrorCodes.SerializationFailure)
        {
            return ResultadoEscritura.Conflicto(MensajeModificada);
        }
    }

    public async Task<ResultadoEscritura> EliminarAsync(Guid organizacion, Guid acceso, Guid planta, CancellationToken ct)
    {
        await using var tx = await TransaccionContexto.AbrirAsync(datos, organizacion, acceso, ct);
        var (sucursal, _) = await BloquearYLeerAsync(tx, organizacion, planta, ct);
        if (sucursal == Guid.Empty) return ResultadoEscritura.NoEncontrada;
        if (!await PuedeEscribirAsync(tx, organizacion, sucursal, ct)) return ResultadoEscritura.SinPermiso;
        // Solo el administrador elimina plantas.
        if (!await RepositorioSitios.EsAdministradorAsync(tx, organizacion, acceso, ct)) return ResultadoEscritura.SinPermiso;
        // Eliminar = ocultar (sitio_baja; el historial de descargas se conserva) y solo si no hay un proceso pendiente con la planta:
        // una descarga recibida cuya captura sigue por revisar.
        await using (var cmd = tx.Comando("""
            SELECT EXISTS (SELECT 1 FROM negocio.captura_trabajo ct
                JOIN negocio.captura_por_revisar r ON (r.organizacion_id, r.sucursal_id, r.captura_id) = (ct.organizacion_id, ct.sucursal_id, ct.captura_id)
                WHERE ct.organizacion_id = $1 AND ct.planta_id = $2::text AND ct.tipo IN ('RECEPCION_PLANTA', 'TIRADA_SANITARIOS'))
            """, organizacion, planta))
            if ((bool)(await cmd.ExecuteScalarAsync(ct))!) return ResultadoEscritura.Regla("planta_con_pendientes", MensajePendiente);
        await using (var cmd = tx.Comando("INSERT INTO negocio.sitio_baja (organizacion_id, sucursal_id, sitio_id) VALUES ($1, $2, $3)", organizacion, sucursal, planta))
            await cmd.ExecuteNonQueryAsync(ct);
        await tx.ConfirmarAsync(ct);
        return ResultadoEscritura.Correcta(planta, 0);
    }

    private static async Task<(Guid Sucursal, int Revision)> BloquearYLeerAsync(TransaccionContexto tx, Guid org, Guid sitio, CancellationToken ct)
    {
        await using (var candado = tx.Comando("SELECT pg_advisory_xact_lock(hashtextextended('sitio:' || $1::text || ':' || $2::text, 0))", org, sitio))
            await candado.ExecuteNonQueryAsync(ct);
        await using var cmd = tx.Comando($"""
            SELECT s.sucursal_id, coalesce((SELECT max(f.revision) FROM negocio.ficha_sitio f
                    WHERE (f.organizacion_id, f.sucursal_id, f.sitio_id) = (s.organizacion_id, s.sucursal_id, s.id)), 0)
            FROM negocio.sitio s
            WHERE s.organizacion_id = $1 AND s.id = $2 AND {EsPlanta} AND {ReglasSitio.SinBaja}
            """, org, sitio);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? (r.GetGuid(0), r.GetInt32(1)) : (Guid.Empty, 0);
    }

    private static async Task InsertarFichaAsync(TransaccionContexto tx, Guid org, Guid suc, Guid sitio, int revision, DatosSitio d, CancellationToken ct)
    {
        await using var cmd = tx.Comando("""
            INSERT INTO negocio.ficha_sitio (organizacion_id, sucursal_id, sitio_id, revision, nombre, calle, numero_exterior, colonia,
                codigo_postal, municipio, estado, latitud, longitud, pais, numero_interior, referencia_localizacion)
            SELECT $1, $2, $3, $4, $5::text, $6::text, $7::text, $8::text, $9::text, $10::text, $11::text, $12::numeric, $13::numeric,
                   previa.pais, previa.numero_interior, previa.referencia_localizacion
            FROM (SELECT 1) base LEFT JOIN LATERAL (
                SELECT pais, numero_interior, referencia_localizacion FROM negocio.ficha_sitio
                WHERE organizacion_id = $1 AND sucursal_id = $2 AND sitio_id = $3 ORDER BY revision DESC LIMIT 1) previa ON true
            """, org, suc, sitio, revision, Nulo(d.Nombre), Nulo(d.Calle), Nulo(d.NumeroExterior), Nulo(d.Colonia), Nulo(d.CodigoPostal),
            Nulo(d.Municipio), Nulo(d.Estado), (object?)d.Latitud ?? DBNull.Value, (object?)d.Longitud ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task<bool> PuedeEscribirAsync(TransaccionContexto tx, Guid org, Guid suc, CancellationToken ct)
    {
        await using var cmd = tx.Comando("SELECT coalesce(seguridad.puede($1, $2, 'datos.escribir'), false)", org, suc);
        return (bool)(await cmd.ExecuteScalarAsync(ct))!;
    }

    private static object Nulo(string? valor) => (object?)valor ?? DBNull.Value;
}
