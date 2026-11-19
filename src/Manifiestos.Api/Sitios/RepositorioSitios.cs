using Manifiestos.Api.Datos;
using Manifiestos.Api.Rutas;
using Npgsql;

namespace Manifiestos.Api.Sitios;

/// <summary>Sitio normal (sin dueño): punto de servicio donde van los clientes generales. Revision = revisión de su ficha.</summary>
public sealed record SitioResumen(
    Guid Id, Guid SucursalId, int Revision, string? Nombre, string? Calle, string? NumeroExterior, string? Colonia,
    string? CodigoPostal, string? Municipio, string? Estado, decimal? Latitud, decimal? Longitud,
    IReadOnlyList<RutaReferencia> Rutas);

public sealed record DatosSitio(
    string? Nombre, string? Calle, string? NumeroExterior, string? Colonia, string? CodigoPostal,
    string? Municipio, string? Estado, decimal? Latitud, decimal? Longitud);

public sealed record SolicitudSitio(
    Guid? SucursalId, int? Revision, string? Nombre, string? Calle, string? NumeroExterior, string? Colonia,
    string? CodigoPostal, string? Municipio, string? Estado, decimal? Latitud, decimal? Longitud);

public interface IRepositorioSitios
{
    Task<IReadOnlyList<SitioResumen>> ListarAsync(Guid organizacion, Guid acceso, CancellationToken ct);
    Task<ResultadoEscritura> CrearAsync(Guid organizacion, Guid acceso, Guid sucursal, DatosSitio datos, CancellationToken ct);
    Task<ResultadoEscritura> EditarAsync(Guid organizacion, Guid acceso, Guid sitio, DatosSitio datos, int revision, CancellationToken ct);
    Task<ResultadoEscritura> EliminarAsync(Guid organizacion, Guid acceso, Guid sitio, CancellationToken ct);
}

/// <summary>
/// Sitios normales (migración 0029). Las fichas son revisiones nuevas; un sitio no se borra, se da de baja (sitio_baja) y solo
/// si no está en una ruta vigente. Los sitios de clientes autorizados no se administran aquí: son del cliente.
/// </summary>
public sealed class RepositorioSitios(NpgsqlDataSource datos) : IRepositorioSitios
{
    public const string MensajeSitioModificado = "Alguien más modificó este sitio. Recarga para ver los cambios.";
    public const string MensajeEnRuta = "No puedes eliminar un sitio que está en una ruta. Quítalo de la ruta primero.";

    public async Task<IReadOnlyList<SitioResumen>> ListarAsync(Guid organizacion, Guid acceso, CancellationToken ct)
    {
        await using var tx = await TransaccionContexto.AbrirAsync(datos, organizacion, acceso, ct);
        var rutas = new Dictionary<Guid, List<RutaReferencia>>();
        await using (var cmd = tx.Comando("""
            SELECT DISTINCT pv.sitio_id, pa.ruta_id, rv.nombre
            FROM negocio.parada pa
            JOIN LATERAL (SELECT y.sitio_id FROM negocio.parada_version y
                WHERE (y.organizacion_id, y.sucursal_id, y.parada_id) = (pa.organizacion_id, pa.sucursal_id, pa.id)
                ORDER BY y.revision DESC LIMIT 1) pv ON true
            JOIN LATERAL (SELECT x.nombre FROM negocio.ruta_version x
                WHERE (x.organizacion_id, x.sucursal_id, x.ruta_id) = (pa.organizacion_id, pa.sucursal_id, pa.ruta_id)
                ORDER BY x.revision DESC LIMIT 1) rv ON true
            WHERE pa.organizacion_id = $1
              AND NOT EXISTS (SELECT 1 FROM negocio.parada_baja pb WHERE (pb.organizacion_id, pb.sucursal_id, pb.parada_id) = (pa.organizacion_id, pa.sucursal_id, pa.id))
            ORDER BY rv.nombre
            """, organizacion))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
        {
            while (await r.ReadAsync(ct))
            {
                if (!rutas.TryGetValue(r.GetGuid(0), out var l)) rutas[r.GetGuid(0)] = l = [];
                l.Add(new RutaReferencia(r.GetGuid(1), r.GetString(2)));
            }
        }

        var lista = new List<SitioResumen>();
        await using (var cmd = tx.Comando($"""
            SELECT s.id, s.sucursal_id, coalesce(f.revision, 0), coalesce(nullif(btrim(f.nombre), ''), s.referencia),
                   f.calle, f.numero_exterior, f.colonia, f.codigo_postal, f.municipio, f.estado, f.latitud, f.longitud
            FROM negocio.punto_servicio ps
            JOIN negocio.sitio s ON (s.organizacion_id, s.sucursal_id, s.id) = (ps.organizacion_id, ps.sucursal_id, ps.sitio_id)
            LEFT JOIN LATERAL (SELECT * FROM negocio.ficha_sitio x
                WHERE (x.organizacion_id, x.sucursal_id, x.sitio_id) = (s.organizacion_id, s.sucursal_id, s.id)
                ORDER BY x.revision DESC LIMIT 1) f ON true
            WHERE ps.organizacion_id = $1 AND {ReglasSitio.SinBaja} AND NOT {ReglasSitio.DeAutorizado}
            ORDER BY 4 NULLS LAST, s.id
            """, organizacion))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
        {
            while (await r.ReadAsync(ct))
            {
                string? T(int i) => r.IsDBNull(i) ? null : r.GetString(i);
                lista.Add(new SitioResumen(r.GetGuid(0), r.GetGuid(1), r.GetInt32(2), T(3), T(4), T(5), T(6), T(7), T(8), T(9),
                    r.IsDBNull(10) ? null : r.GetDecimal(10), r.IsDBNull(11) ? null : r.GetDecimal(11),
                    rutas.GetValueOrDefault(r.GetGuid(0)) ?? []));
            }
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
        await using (var cmd = tx.Comando("INSERT INTO negocio.punto_servicio (organizacion_id, sucursal_id, sitio_id) VALUES ($1, $2, $3)", organizacion, sucursal, id))
            await cmd.ExecuteNonQueryAsync(ct);
        await InsertarFichaAsync(tx, organizacion, sucursal, id, 1, d, ct);
        await tx.ConfirmarAsync(ct);
        return ResultadoEscritura.Correcta(id, 1);
    }

    public async Task<ResultadoEscritura> EditarAsync(Guid organizacion, Guid acceso, Guid sitio, DatosSitio d, int revision, CancellationToken ct)
    {
        try
        {
            await using var tx = await TransaccionContexto.AbrirAsync(datos, organizacion, acceso, ct);
            var (sucursal, actual) = await BloquearYLeerAsync(tx, organizacion, sitio, ct);
            if (sucursal == Guid.Empty) return ResultadoEscritura.NoEncontrada;
            if (!await PuedeEscribirAsync(tx, organizacion, sucursal, ct)) return ResultadoEscritura.SinPermiso;
            if (actual != revision) return ResultadoEscritura.Conflicto(MensajeSitioModificado);
            await InsertarFichaAsync(tx, organizacion, sucursal, sitio, actual + 1, d, ct);
            await tx.ConfirmarAsync(ct);
            return ResultadoEscritura.Correcta(sitio, actual + 1);
        }
        catch (PostgresException e) when (e.SqlState is PostgresErrorCodes.UniqueViolation or PostgresErrorCodes.SerializationFailure)
        {
            return ResultadoEscritura.Conflicto(MensajeSitioModificado);
        }
    }

    public async Task<ResultadoEscritura> EliminarAsync(Guid organizacion, Guid acceso, Guid sitio, CancellationToken ct)
    {
        await using var tx = await TransaccionContexto.AbrirAsync(datos, organizacion, acceso, ct);
        var (sucursal, _) = await BloquearYLeerAsync(tx, organizacion, sitio, ct);
        if (sucursal == Guid.Empty) return ResultadoEscritura.NoEncontrada;
        if (!await PuedeEscribirAsync(tx, organizacion, sucursal, ct)) return ResultadoEscritura.SinPermiso;
        // Solo el administrador elimina sitios.
        if (!await EsAdministradorAsync(tx, organizacion, acceso, ct)) return ResultadoEscritura.SinPermiso;
        await using (var cmd = tx.Comando("""
            SELECT EXISTS (SELECT 1 FROM negocio.parada pa
                JOIN LATERAL (SELECT y.sitio_id FROM negocio.parada_version y
                    WHERE (y.organizacion_id, y.sucursal_id, y.parada_id) = (pa.organizacion_id, pa.sucursal_id, pa.id)
                    ORDER BY y.revision DESC LIMIT 1) pv ON true
                WHERE pa.organizacion_id = $1 AND pa.sucursal_id = $2 AND pv.sitio_id = $3
                  AND NOT EXISTS (SELECT 1 FROM negocio.parada_baja pb WHERE (pb.organizacion_id, pb.sucursal_id, pb.parada_id) = (pa.organizacion_id, pa.sucursal_id, pa.id)))
            """, organizacion, sucursal, sitio))
            if ((bool)(await cmd.ExecuteScalarAsync(ct))!) return ResultadoEscritura.Regla("sitio_en_ruta", MensajeEnRuta);
        await using (var cmd = tx.Comando("""
            SELECT EXISTS (SELECT 1 FROM negocio.parada_extraordinaria x
                WHERE x.organizacion_id = $1 AND x.sucursal_id = $2 AND x.sitio_id = $3 AND x.fecha >= (now() AT TIME ZONE 'UTC')::date)
            """, organizacion, sucursal, sitio))
            if ((bool)(await cmd.ExecuteScalarAsync(ct))!) return ResultadoEscritura.Regla("sitio_en_ruta", MensajeEnRuta);
        await using (var cmd = tx.Comando("INSERT INTO negocio.sitio_baja (organizacion_id, sucursal_id, sitio_id) VALUES ($1, $2, $3)", organizacion, sucursal, sitio))
            await cmd.ExecuteNonQueryAsync(ct);
        await tx.ConfirmarAsync(ct);
        return ResultadoEscritura.Correcta(sitio, 0);
    }

    /// <summary>Bloquea el sitio (mismo candado que las altas de parada) y devuelve su sucursal y la última revisión de ficha; Guid.Empty = no es un sitio normal vigente.</summary>
    private static async Task<(Guid Sucursal, int Revision)> BloquearYLeerAsync(TransaccionContexto tx, Guid org, Guid sitio, CancellationToken ct)
    {
        await using (var candado = tx.Comando("SELECT pg_advisory_xact_lock(hashtextextended('sitio:' || $1::text || ':' || $2::text, 0))", org, sitio))
            await candado.ExecuteNonQueryAsync(ct);
        await using var cmd = tx.Comando($"""
            SELECT s.sucursal_id, coalesce((SELECT max(f.revision) FROM negocio.ficha_sitio f
                    WHERE (f.organizacion_id, f.sucursal_id, f.sitio_id) = (s.organizacion_id, s.sucursal_id, s.id)), 0)
            FROM negocio.sitio s
            JOIN negocio.punto_servicio ps ON (ps.organizacion_id, ps.sucursal_id, ps.sitio_id) = (s.organizacion_id, s.sucursal_id, s.id)
            WHERE s.organizacion_id = $1 AND s.id = $2 AND {ReglasSitio.SinBaja} AND NOT {ReglasSitio.DeAutorizado}
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

    /// <summary>Solo el rol administrador (provisional, PG-11) puede eliminar sitios y plantas.</summary>
    internal static async Task<bool> EsAdministradorAsync(TransaccionContexto tx, Guid org, Guid acceso, CancellationToken ct)
    {
        await using var cmd = tx.Comando("SELECT seguridad.es_administrador($1, $2)", org, acceso);
        return (bool)(await cmd.ExecuteScalarAsync(ct))!;
    }

    private static object Nulo(string? valor) => (object?)valor ?? DBNull.Value;
}
