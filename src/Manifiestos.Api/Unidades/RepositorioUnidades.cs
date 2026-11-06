using Manifiestos.Api.Datos;
using Manifiestos.Api.Rutas;
using Npgsql;

namespace Manifiestos.Api.Unidades;

public interface IRepositorioUnidades
{
    Task<ListaUnidades> ListarAsync(Guid organizacion, Guid acceso, CancellationToken ct);
    Task<ResultadoEscritura> CrearAsync(Guid organizacion, Guid acceso, Guid sucursal, DatosUnidad datos, CancellationToken ct);
    Task<ResultadoEscritura> EditarAsync(Guid organizacion, Guid acceso, Guid unidad, DatosUnidad datos, int revision, CancellationToken ct);
    Task<ResultadoEscritura> AsignarOperadorAsync(Guid organizacion, Guid acceso, Guid unidad, Guid? operador, int revisionVista, CancellationToken ct);
}

/// <summary>
/// Unidades (migración 0012). vehiculo y ficha_vehiculo son inmutables: crear inserta el vehículo y su
/// primera ficha; corregir inserta una ficha nueva con revisión + 1 (no se sobrescribe). Operador y ruta
/// asignados se muestran de las tablas vigentes; asignarlos sigue siendo de Rutas.
/// </summary>
public sealed class RepositorioUnidades(NpgsqlDataSource datos) : IRepositorioUnidades
{
    public const string MensajeModificada = "Alguien más modificó esta unidad. Recarga para ver los cambios.";
    public const string MensajeNumeroRepetido = "Ya existe una unidad con ese número económico en esta sucursal.";
    public const string MensajeCapacidadInvalida = "La capacidad elegida no está en el catálogo.";
    public const string MensajeOperadorModificado = "Alguien más cambió el operador de esta unidad. Recarga para ver los cambios.";
    public const string MensajeOperadorInvalido = "El operador elegido no está disponible.";

    private const string UltimaFicha = """
        LEFT JOIN LATERAL (SELECT x.* FROM negocio.ficha_vehiculo x
            WHERE (x.organizacion_id, x.sucursal_id, x.vehiculo_id) = (veh.organizacion_id, veh.sucursal_id, veh.id)
            ORDER BY x.revision DESC LIMIT 1) f ON true
        """;

    public async Task<ListaUnidades> ListarAsync(Guid organizacion, Guid acceso, CancellationToken ct)
    {
        await using var tx = await TransaccionContexto.AbrirAsync(datos, organizacion, acceso, ct);

        var capacidades = new List<CapacidadOpcion>();
        await using (var cmd = tx.Comando("SELECT id, valor_m3 FROM catalogo.capacidad_vehiculo ORDER BY valor_m3"))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct)) capacidades.Add(new CapacidadOpcion(r.GetInt16(0), r.GetDecimal(1)));

        // Ruta asignada: rutas cuya última revisión usa la unidad.
        var rutas = new Dictionary<Guid, List<RutaDeUnidad>>();
        var masc = new Dictionary<Guid, List<(string Ruta, short Dias, string? Unidad)>>();
        await using (var cmd = tx.Comando("""
            SELECT rv.vehiculo_id, ru.id, rv.nombre, rv.dias
            FROM negocio.ruta ru
            JOIN LATERAL (SELECT x.vehiculo_id, x.nombre, x.dias FROM negocio.ruta_version x
                WHERE (x.organizacion_id, x.sucursal_id, x.ruta_id) = (ru.organizacion_id, ru.sucursal_id, ru.id)
                ORDER BY x.revision DESC LIMIT 1) rv ON true
            WHERE ru.organizacion_id = $1
            ORDER BY rv.nombre, ru.id
            """, organizacion))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct))
            {
                var unidad = r.GetGuid(0);
                if (!rutas.TryGetValue(unidad, out var l)) rutas[unidad] = l = [];
                l.Add(new RutaDeUnidad(r.GetGuid(1), r.GetString(2), DiasSemana.Lista(r.GetInt16(3))));
                if (!masc.TryGetValue(unidad, out var m)) masc[unidad] = m = [];
                m.Add((r.GetString(2), r.GetInt16(3), null));
            }

        var lista = new List<UnidadFicha>();
        await using (var cmd = tx.Comando($"""
            SELECT veh.id, veh.sucursal_id, coalesce(f.revision, 0), coalesce(f.numero_economico, veh.numero_economico),
                   f.placas, f.marca, f.tipo, f.capacidad_id, cap.valor_m3, f.clasificacion, f.descripcion,
                   op.persona_id, p.nombre_completo, coalesce(op.revision, 0)
            FROM negocio.vehiculo veh
            {UltimaFicha}
            LEFT JOIN catalogo.capacidad_vehiculo cap ON cap.id = f.capacidad_id
            LEFT JOIN LATERAL (SELECT a.persona_id, a.revision FROM negocio.asignacion_unidad a
                WHERE (a.organizacion_id, a.sucursal_id, a.vehiculo_id) = (veh.organizacion_id, veh.sucursal_id, veh.id)
                ORDER BY a.revision DESC LIMIT 1) op ON true
            LEFT JOIN seguridad.persona p ON (p.organizacion_id, p.id) = (veh.organizacion_id, op.persona_id)
            WHERE veh.organizacion_id = $1
            ORDER BY coalesce(f.numero_economico, veh.numero_economico) NULLS LAST, veh.id
            """, organizacion))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct))
            {
                string? T(int i) => r.IsDBNull(i) ? null : r.GetString(i);
                var id = r.GetGuid(0);
                lista.Add(new UnidadFicha(
                    id, r.GetGuid(1), r.GetInt32(2), T(3), T(4), T(5), T(6),
                    r.IsDBNull(7) ? null : r.GetInt16(7), r.IsDBNull(8) ? null : r.GetDecimal(8), T(9), T(10),
                    r.IsDBNull(11) ? null : new OperadorResumen(r.GetGuid(11), r.GetString(12)),
                    rutas.GetValueOrDefault(id) ?? [], r.GetInt32(13),
                    AvisosDeDias.Coincidencias(masc.GetValueOrDefault(id) ?? [])));
            }
        await tx.ConfirmarAsync(ct);
        return new ListaUnidades(lista, capacidades);
    }

    public Task<ResultadoEscritura> CrearAsync(Guid organizacion, Guid acceso, Guid sucursal, DatosUnidad d, CancellationToken ct) =>
        ConConflictoAsync(async () =>
        {
            await using var tx = await TransaccionContexto.AbrirAsync(datos, organizacion, acceso, ct);
            if (!await PuedeEscribirAsync(tx, organizacion, sucursal, ct)) return ResultadoEscritura.SinPermiso;
            if (!await SucursalExisteAsync(tx, organizacion, sucursal, ct)) return ResultadoEscritura.Invalida("La sucursal no existe.");
            await BloquearSucursalAsync(tx, sucursal, ct);
            if (await ValidarAsync(tx, organizacion, sucursal, null, d, ct) is { } invalida) return invalida;

            Guid id;
            await using (var cmd = tx.Comando("INSERT INTO negocio.vehiculo (organizacion_id, sucursal_id, numero_economico) VALUES ($1, $2, $3) RETURNING id",
                organizacion, sucursal, d.NumeroEconomico))
                id = (Guid)(await cmd.ExecuteScalarAsync(ct))!;
            await InsertarFichaAsync(tx, organizacion, sucursal, id, 1, d, ct);
            await tx.ConfirmarAsync(ct);
            return ResultadoEscritura.Correcta(id, 1);
        });

    public Task<ResultadoEscritura> EditarAsync(Guid organizacion, Guid acceso, Guid unidad, DatosUnidad d, int revision, CancellationToken ct) =>
        ConConflictoAsync(async () =>
        {
            await using var tx = await TransaccionContexto.AbrirAsync(datos, organizacion, acceso, ct);
            Guid sucursal;
            await using (var cmd = tx.Comando("SELECT sucursal_id FROM negocio.vehiculo WHERE organizacion_id = $1 AND id = $2 LIMIT 1", organizacion, unidad))
            {
                if (await cmd.ExecuteScalarAsync(ct) is not Guid s) return ResultadoEscritura.NoEncontrada;
                sucursal = s;
            }
            if (!await PuedeEscribirAsync(tx, organizacion, sucursal, ct)) return ResultadoEscritura.SinPermiso;
            await BloquearSucursalAsync(tx, sucursal, ct);

            int actual = 0; DatosUnidad? vigente = null;
            await using (var cmd = tx.Comando($"""
                SELECT coalesce(f.revision, 0), coalesce(f.numero_economico, veh.numero_economico),
                       f.placas, f.marca, f.tipo, f.capacidad_id, f.clasificacion, f.descripcion
                FROM negocio.vehiculo veh
                {UltimaFicha}
                WHERE veh.organizacion_id = $1 AND veh.sucursal_id = $2 AND veh.id = $3
                """, organizacion, sucursal, unidad))
            await using (var r = await cmd.ExecuteReaderAsync(ct))
            {
                if (!await r.ReadAsync(ct)) return ResultadoEscritura.NoEncontrada;
                string? T(int i) => r.IsDBNull(i) ? null : r.GetString(i);
                actual = r.GetInt32(0);
                if (actual > 0) vigente = new DatosUnidad(T(1) ?? "", T(2), T(3), T(4), r.GetInt16(5), T(6), T(7));
            }
            if (actual != revision) return ResultadoEscritura.Conflicto(MensajeModificada);
            if (await ValidarAsync(tx, organizacion, sucursal, unidad, d, ct) is { } invalida) return invalida;
            // Sin cambios no se crea una revisión vacía.
            if (vigente == d) return ResultadoEscritura.Correcta(unidad, actual);

            await InsertarFichaAsync(tx, organizacion, sucursal, unidad, actual + 1, d, ct);
            await tx.ConfirmarAsync(ct);
            return ResultadoEscritura.Correcta(unidad, actual + 1);
        });

    /// <summary>
    /// Operador de la unidad con historial: asignación nueva (revisión + 1), que puede ser «sin operador».
    /// Como el operador vive en la unidad pero cambiarlo afecta a sus rutas, cada ruta que hoy usa la unidad
    /// recibe también una revisión nueva (igual que al editar el operador desde Rutas): quien editó la ruta
    /// sobre la revisión anterior recibe 409 y no revierte el cambio sin aviso. Las rutas se bloquean antes
    /// que la unidad, en el mismo orden que Rutas, para no provocar bloqueos mutuos.
    /// </summary>
    public Task<ResultadoEscritura> AsignarOperadorAsync(Guid organizacion, Guid acceso, Guid unidad, Guid? operador, int revisionVista, CancellationToken ct) =>
        ConConflictoAsync(async () =>
        {
            await using var tx = await TransaccionContexto.AbrirAsync(datos, organizacion, acceso, ct);
            Guid sucursal;
            await using (var cmd = tx.Comando("SELECT sucursal_id FROM negocio.vehiculo WHERE organizacion_id = $1 AND id = $2 LIMIT 1", organizacion, unidad))
            {
                if (await cmd.ExecuteScalarAsync(ct) is not Guid s) return ResultadoEscritura.NoEncontrada;
                sucursal = s;
            }
            if (!await PuedeEscribirAsync(tx, organizacion, sucursal, ct)) return ResultadoEscritura.SinPermiso;

            var rutas = await RutasDeLaUnidadAsync(tx, organizacion, sucursal, unidad, ct);
            foreach (var ruta in rutas.OrderBy(x => x)) await BloquearAsync(tx, "ruta", ruta, ct);
            await BloquearAsync(tx, "unidad", unidad, ct);
            // Con los bloqueos tomados se vuelve a leer: una ruta pudo cambiar de unidad mientras tanto.
            rutas = await RutasDeLaUnidadAsync(tx, organizacion, sucursal, unidad, ct);
            foreach (var ruta in rutas.OrderBy(x => x)) await BloquearAsync(tx, "ruta", ruta, ct);

            int ultima = 0; Guid? actual = null;
            await using (var cmd = tx.Comando("""
                SELECT revision, persona_id FROM negocio.asignacion_unidad
                WHERE organizacion_id = $1 AND sucursal_id = $2 AND vehiculo_id = $3
                ORDER BY revision DESC LIMIT 1
                """, organizacion, sucursal, unidad))
            await using (var r = await cmd.ExecuteReaderAsync(ct))
                if (await r.ReadAsync(ct)) (ultima, actual) = (r.GetInt32(0), r.IsDBNull(1) ? null : r.GetGuid(1));
            if (ultima != revisionVista) return ResultadoEscritura.Conflicto(MensajeOperadorModificado);

            if (operador is { } persona)
            {
                await using var cmd = tx.Comando("SELECT EXISTS (SELECT 1 FROM seguridad.operadores_para_ruta($1, $2) WHERE persona_id = $3)", organizacion, sucursal, persona);
                if (!(bool)(await cmd.ExecuteScalarAsync(ct))!) return ResultadoEscritura.Invalida(MensajeOperadorInvalido);
            }
            // Mismo operador (o sigue sin operador): no hay nada que registrar.
            if (actual == operador && (ultima > 0 || operador is null)) return ResultadoEscritura.Correcta(unidad, ultima);

            await using (var ins = tx.Comando("""
                INSERT INTO negocio.asignacion_unidad (organizacion_id, sucursal_id, vehiculo_id, revision, persona_id)
                VALUES ($1, $2, $3, $4, $5)
                """, organizacion, sucursal, unidad, ultima + 1, (object?)operador ?? DBNull.Value))
                await ins.ExecuteNonQueryAsync(ct);
            foreach (var ruta in rutas)
            {
                await using var nueva = tx.Comando("""
                    INSERT INTO negocio.ruta_version (organizacion_id, sucursal_id, ruta_id, revision, nombre, dias, vehiculo_id)
                    SELECT v.organizacion_id, v.sucursal_id, v.ruta_id, v.revision + 1, v.nombre, v.dias, v.vehiculo_id
                    FROM negocio.ruta_version v
                    WHERE (v.organizacion_id, v.sucursal_id, v.ruta_id) = ($1, $2, $3)
                    ORDER BY v.revision DESC LIMIT 1
                    """, organizacion, sucursal, ruta);
                await nueva.ExecuteNonQueryAsync(ct);
            }
            await tx.ConfirmarAsync(ct);
            return ResultadoEscritura.Correcta(unidad, ultima + 1);
        });

    private static async Task<List<Guid>> RutasDeLaUnidadAsync(TransaccionContexto tx, Guid org, Guid suc, Guid unidad, CancellationToken ct)
    {
        var rutas = new List<Guid>();
        await using var cmd = tx.Comando("""
            SELECT ru.id FROM negocio.ruta ru
            JOIN LATERAL (SELECT x.vehiculo_id FROM negocio.ruta_version x
                WHERE (x.organizacion_id, x.sucursal_id, x.ruta_id) = (ru.organizacion_id, ru.sucursal_id, ru.id)
                ORDER BY x.revision DESC LIMIT 1) rv ON true
            WHERE ru.organizacion_id = $1 AND ru.sucursal_id = $2 AND rv.vehiculo_id = $3
            """, org, suc, unidad);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) rutas.Add(r.GetGuid(0));
        return rutas;
    }

    /// <summary>Serializa las escrituras sobre la misma ruta o unidad (misma clave que Rutas).</summary>
    private static async Task BloquearAsync(TransaccionContexto tx, string tipo, Guid id, CancellationToken ct)
    {
        await using var cmd = tx.Comando("SELECT pg_advisory_xact_lock(hashtextextended($1, 0))", $"{tipo}:{id}");
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task<ResultadoEscritura?> ValidarAsync(TransaccionContexto tx, Guid org, Guid suc, Guid? unidad, DatosUnidad d, CancellationToken ct)
    {
        await using (var cmd = tx.Comando("SELECT EXISTS (SELECT 1 FROM catalogo.capacidad_vehiculo WHERE id = $1)", d.CapacidadId))
            if (!(bool)(await cmd.ExecuteScalarAsync(ct))!) return ResultadoEscritura.Invalida(MensajeCapacidadInvalida);
        await using (var cmd = tx.Comando($"""
            SELECT EXISTS (
                SELECT 1 FROM negocio.vehiculo veh
                {UltimaFicha}
                WHERE veh.organizacion_id = $1 AND veh.sucursal_id = $2 AND ($3::uuid IS NULL OR veh.id <> $3)
                  AND lower(btrim(coalesce(f.numero_economico, veh.numero_economico))) = lower($4))
            """, org, suc, (object?)unidad ?? DBNull.Value, d.NumeroEconomico))
            if ((bool)(await cmd.ExecuteScalarAsync(ct))!) return ResultadoEscritura.Invalida(MensajeNumeroRepetido);
        return null;
    }

    private static async Task InsertarFichaAsync(TransaccionContexto tx, Guid org, Guid suc, Guid unidad, int revision, DatosUnidad d, CancellationToken ct)
    {
        await using var cmd = tx.Comando("""
            INSERT INTO negocio.ficha_vehiculo
                (organizacion_id, sucursal_id, vehiculo_id, revision, capacidad_id, marca, tipo, placas, numero_economico, clasificacion, descripcion)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11)
            """, org, suc, unidad, revision, d.CapacidadId,
            (object?)d.Marca ?? DBNull.Value, (object?)d.Tipo ?? DBNull.Value, (object?)d.Placas ?? DBNull.Value,
            d.NumeroEconomico, (object?)d.Clasificacion ?? DBNull.Value, (object?)d.Descripcion ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task<bool> PuedeEscribirAsync(TransaccionContexto tx, Guid org, Guid suc, CancellationToken ct)
    {
        await using var cmd = tx.Comando("SELECT coalesce(seguridad.puede($1, $2, 'datos.escribir'), false)", org, suc);
        return (bool)(await cmd.ExecuteScalarAsync(ct))!;
    }

    private static async Task<bool> SucursalExisteAsync(TransaccionContexto tx, Guid org, Guid suc, CancellationToken ct)
    {
        await using var cmd = tx.Comando("SELECT EXISTS (SELECT 1 FROM negocio.sucursal WHERE organizacion_id = $1 AND id = $2)", org, suc);
        return (bool)(await cmd.ExecuteScalarAsync(ct))!;
    }

    /// <summary>Serializa las altas y correcciones de una sucursal: el número económico no puede repetirse.</summary>
    private static async Task BloquearSucursalAsync(TransaccionContexto tx, Guid suc, CancellationToken ct)
    {
        await using var cmd = tx.Comando("SELECT pg_advisory_xact_lock(hashtextextended($1, 0))", $"unidades:{suc}");
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task<ResultadoEscritura> ConConflictoAsync(Func<Task<ResultadoEscritura>> accion)
    {
        try { return await accion(); }
        catch (PostgresException e) when (e.SqlState is PostgresErrorCodes.UniqueViolation or PostgresErrorCodes.DeadlockDetected)
        { return ResultadoEscritura.Conflicto(RepositorioUnidades.MensajeModificada); }
    }
}
