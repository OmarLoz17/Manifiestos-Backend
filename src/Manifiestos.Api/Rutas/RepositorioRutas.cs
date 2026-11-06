using Manifiestos.Api.Datos;
using Manifiestos.Api.Clientes;
using Npgsql;

namespace Manifiestos.Api.Rutas;

public interface IRepositorioRutas
{
    Task<IReadOnlyList<RutaResumen>> ListarAsync(Guid organizacion, Guid acceso, CancellationToken ct);
    Task<IReadOnlyList<ParadaDetalle>?> ParadasAsync(Guid organizacion, Guid acceso, Guid ruta, CancellationToken ct);
    Task<OpcionesRuta> OpcionesAsync(Guid organizacion, Guid acceso, Guid sucursal, CancellationToken ct);
    Task<ResultadoEscritura> CrearAsync(Guid organizacion, Guid acceso, Guid sucursal, DatosRuta datos, CancellationToken ct);
    Task<ResultadoEscritura> EditarAsync(Guid organizacion, Guid acceso, Guid ruta, DatosRuta datos, int revision, CancellationToken ct);
    Task<ResultadoEscritura> AgregarParadaAsync(Guid organizacion, Guid acceso, Guid ruta, DatosParada datos, CancellationToken ct);
    Task<ResultadoEscritura> EditarParadaAsync(Guid organizacion, Guid acceso, Guid ruta, Guid parada, DatosParada datos, int revision, CancellationToken ct);
    Task<ResultadoEscritura> OrdenarAsync(Guid organizacion, Guid acceso, Guid ruta, IReadOnlyList<Guid> paradas, CancellationToken ct);
}

/// <summary>
/// Rutas recurrentes y paradas (migración 0008) con el usuario manifiestos_api.
/// Toda consulta corre en una TransaccionContexto: RLS limita a las sucursales con
/// datos.leer / datos.escribir. Los cambios son revisiones nuevas; el único UPDATE es parada.posicion.
/// </summary>
public sealed class RepositorioRutas(NpgsqlDataSource datos) : IRepositorioRutas
{
    public const string MensajeRutaModificada = "Alguien más modificó esta ruta. Recarga para ver los cambios.";
    public const string MensajeParadaModificada = "Alguien más modificó esta parada. Recarga para ver los cambios.";
    public const string MensajeParadasCambiaron = "Las paradas de esta ruta cambiaron. Recarga para ver los cambios.";
    public const string MensajeNoEsPuntoServicio = "Ese sitio no es un punto de servicio.";
    public const string MensajeUnidadInvalida = "La unidad elegida no está disponible.";
    public const string MensajeOperadorInvalido = "El operador elegido no está disponible.";
    public const string MensajeClienteInvalido = "Uno de los clientes no está disponible en esta sucursal.";

    // Nombre visible del cliente: última ficha_empresa (comercial, si no fiscal), si no código comercial.
    private const string NombreCliente = """
        coalesce((SELECT coalesce(nullif(btrim(f.nombre_comercial), ''), nullif(btrim(f.nombre_fiscal), ''))
                  FROM negocio.ficha_empresa f
                  WHERE f.organizacion_id = c.organizacion_id AND f.empresa_id = c.empresa_id
                  ORDER BY f.revision DESC LIMIT 1),
                 nullif(btrim(c.codigo_comercial), ''), 'Cliente sin nombre')
        """;

    // Dirección de la ficha fs: "calle número, colonia, municipio, estado, CP" omitiendo vacíos.
    private const string Direccion = """
        nullif(concat_ws(', ',
            nullif(concat_ws(' ', nullif(btrim(fs.calle), ''), nullif(btrim(fs.numero_exterior), '')), ''),
            nullif(btrim(fs.colonia), ''), nullif(btrim(fs.municipio), ''),
            nullif(btrim(fs.estado), ''), nullif(btrim(fs.codigo_postal), '')), '')
        """;

    private const string FichaSitio = """
        LEFT JOIN LATERAL (SELECT * FROM negocio.ficha_sitio x
            WHERE (x.organizacion_id, x.sucursal_id, x.sitio_id) = (s.organizacion_id, s.sucursal_id, s.id)
            ORDER BY x.revision DESC LIMIT 1) fs ON true
        """;

    // ---------------- Lectura ----------------

    public async Task<IReadOnlyList<RutaResumen>> ListarAsync(Guid organizacion, Guid acceso, CancellationToken ct)
    {
        await using var tx = await TransaccionContexto.AbrirAsync(datos, organizacion, acceso, ct);
        await using var cmd = tx.Comando("""
            SELECT r.id, r.sucursal_id, v.revision, v.nombre, v.dias,
                   veh.id, veh.numero_economico, cap.valor_m3,
                   op.persona_id, p.nombre_completo,
                   coalesce(st.paradas, 0)::int, coalesce(st.clientes, 0)::int, coalesce(st.banos, 0)::int
            FROM negocio.ruta r
            JOIN LATERAL (SELECT x.revision, x.nombre, x.dias, x.vehiculo_id FROM negocio.ruta_version x
                WHERE (x.organizacion_id, x.sucursal_id, x.ruta_id) = (r.organizacion_id, r.sucursal_id, r.id)
                ORDER BY x.revision DESC LIMIT 1) v ON true
            JOIN negocio.vehiculo veh ON (veh.organizacion_id, veh.sucursal_id, veh.id) = (r.organizacion_id, r.sucursal_id, v.vehiculo_id)
            LEFT JOIN LATERAL (SELECT f.capacidad_id FROM negocio.ficha_vehiculo f
                WHERE (f.organizacion_id, f.sucursal_id, f.vehiculo_id) = (veh.organizacion_id, veh.sucursal_id, veh.id)
                ORDER BY f.revision DESC LIMIT 1) fv ON true
            LEFT JOIN catalogo.capacidad_vehiculo cap ON cap.id = fv.capacidad_id
            LEFT JOIN LATERAL (SELECT a.persona_id FROM negocio.asignacion_unidad a
                WHERE (a.organizacion_id, a.sucursal_id, a.vehiculo_id) = (veh.organizacion_id, veh.sucursal_id, veh.id)
                ORDER BY a.revision DESC LIMIT 1) op ON true
            LEFT JOIN seguridad.persona p ON (p.organizacion_id, p.id) = (r.organizacion_id, op.persona_id)
            LEFT JOIN LATERAL (
                SELECT count(DISTINCT pa.id) AS paradas, count(DISTINCT pc.cliente_id) AS clientes,
                       sum(coalesce(pc.banos_planeados, 0)) AS banos
                FROM negocio.parada pa
                JOIN LATERAL (SELECT y.id FROM negocio.parada_version y
                    WHERE (y.organizacion_id, y.sucursal_id, y.parada_id) = (pa.organizacion_id, pa.sucursal_id, pa.id)
                    ORDER BY y.revision DESC LIMIT 1) pv ON true
                JOIN negocio.parada_cliente pc ON (pc.organizacion_id, pc.sucursal_id, pc.version_id) = (pa.organizacion_id, pa.sucursal_id, pv.id)
                WHERE (pa.organizacion_id, pa.sucursal_id, pa.ruta_id) = (r.organizacion_id, r.sucursal_id, r.id)) st ON true
            WHERE r.organizacion_id = $1
            ORDER BY v.nombre, r.id
            """, organizacion);
        var lista = new List<RutaResumen>();
        await using (var r = await cmd.ExecuteReaderAsync(ct))
        {
            while (await r.ReadAsync(ct))
                lista.Add(new RutaResumen(
                    r.GetGuid(0), r.GetGuid(1), r.GetInt32(2), r.GetString(3), DiasSemana.Lista(r.GetInt16(4)),
                    new UnidadResumen(r.GetGuid(5), r.IsDBNull(6) ? null : r.GetString(6), r.IsDBNull(7) ? null : r.GetDecimal(7)),
                    r.IsDBNull(8) ? null : new OperadorResumen(r.GetGuid(8), r.IsDBNull(9) ? "" : r.GetString(9)),
                    r.GetInt32(10), r.GetInt32(11), r.GetInt32(12)));
        }
        await tx.ConfirmarAsync(ct);
        return lista;
    }

    public async Task<IReadOnlyList<ParadaDetalle>?> ParadasAsync(Guid organizacion, Guid acceso, Guid ruta, CancellationToken ct)
    {
        await using var tx = await TransaccionContexto.AbrirAsync(datos, organizacion, acceso, ct);
        if (await SucursalDeRutaAsync(tx, organizacion, ruta, ct) is not { } sucursal) return null;

        var filas = new List<(Guid Id, int Posicion, int Revision, Guid Version, SitioDetalle Sitio)>();
        await using (var cmd = tx.Comando($"""
            SELECT pa.id, pa.posicion, pv.revision, pv.id, s.id, fs.nombre, {Direccion}, fs.referencia_localizacion
            FROM negocio.parada pa
            JOIN LATERAL (SELECT y.id, y.revision, y.sitio_id FROM negocio.parada_version y
                WHERE (y.organizacion_id, y.sucursal_id, y.parada_id) = (pa.organizacion_id, pa.sucursal_id, pa.id)
                ORDER BY y.revision DESC LIMIT 1) pv ON true
            JOIN negocio.sitio s ON (s.organizacion_id, s.sucursal_id, s.id) = (pa.organizacion_id, pa.sucursal_id, pv.sitio_id)
            {FichaSitio}
            WHERE pa.organizacion_id = $1 AND pa.sucursal_id = $2 AND pa.ruta_id = $3
            ORDER BY pa.posicion
            """, organizacion, sucursal, ruta))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
        {
            while (await r.ReadAsync(ct))
                filas.Add((r.GetGuid(0), r.GetInt32(1), r.GetInt32(2), r.GetGuid(3), new SitioDetalle(
                    r.GetGuid(4), r.IsDBNull(5) ? null : r.GetString(5), r.IsDBNull(6) ? null : r.GetString(6), r.IsDBNull(7) ? null : r.GetString(7))));
        }

        var clientes = new Dictionary<Guid, List<ClienteParada>>();
        await using (var cmd = tx.Comando($"""
            SELECT pc.version_id, c.id, {NombreCliente} AS nombre, pc.banos_planeados, pc.fosas_planeadas
            FROM negocio.parada_cliente pc
            JOIN negocio.cliente c ON (c.organizacion_id, c.sucursal_id, c.id) = (pc.organizacion_id, pc.sucursal_id, pc.cliente_id)
            WHERE pc.organizacion_id = $1 AND pc.sucursal_id = $2 AND pc.version_id = ANY($3)
            ORDER BY nombre, c.id
            """, organizacion, sucursal, filas.Select(f => f.Version).ToArray()))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
        {
            while (await r.ReadAsync(ct))
            {
                if (!clientes.TryGetValue(r.GetGuid(0), out var l)) clientes[r.GetGuid(0)] = l = [];
                l.Add(new ClienteParada(r.GetGuid(1), r.GetString(2), r.IsDBNull(3) ? null : r.GetInt32(3), r.IsDBNull(4) ? null : r.GetInt32(4)));
            }
        }
        await tx.ConfirmarAsync(ct);

        return filas.Select(f =>
        {
            var lista = clientes.GetValueOrDefault(f.Version) ?? [];
            return new ParadaDetalle(f.Id, f.Posicion, f.Revision, f.Sitio, lista, lista.Sum(c => c.Banos ?? 0));
        }).ToList();
    }

    public async Task<OpcionesRuta> OpcionesAsync(Guid organizacion, Guid acceso, Guid sucursal, CancellationToken ct)
    {
        await using var tx = await TransaccionContexto.AbrirAsync(datos, organizacion, acceso, ct);

        var unidades = new List<UnidadOpcion>();
        await using (var cmd = tx.Comando("""
            SELECT veh.id, veh.numero_economico, cap.valor_m3, op.persona_id, p.nombre_completo
            FROM negocio.vehiculo veh
            LEFT JOIN LATERAL (SELECT f.capacidad_id FROM negocio.ficha_vehiculo f
                WHERE (f.organizacion_id, f.sucursal_id, f.vehiculo_id) = (veh.organizacion_id, veh.sucursal_id, veh.id)
                ORDER BY f.revision DESC LIMIT 1) fv ON true
            LEFT JOIN catalogo.capacidad_vehiculo cap ON cap.id = fv.capacidad_id
            LEFT JOIN LATERAL (SELECT a.persona_id FROM negocio.asignacion_unidad a
                WHERE (a.organizacion_id, a.sucursal_id, a.vehiculo_id) = (veh.organizacion_id, veh.sucursal_id, veh.id)
                ORDER BY a.revision DESC LIMIT 1) op ON true
            LEFT JOIN seguridad.persona p ON (p.organizacion_id, p.id) = (veh.organizacion_id, op.persona_id)
            WHERE veh.organizacion_id = $1 AND veh.sucursal_id = $2
            ORDER BY veh.numero_economico NULLS LAST, veh.id
            """, organizacion, sucursal))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
        {
            while (await r.ReadAsync(ct))
                unidades.Add(new UnidadOpcion(r.GetGuid(0), r.IsDBNull(1) ? null : r.GetString(1), r.IsDBNull(2) ? null : r.GetDecimal(2),
                    r.IsDBNull(3) ? null : r.GetGuid(3), r.IsDBNull(4) ? null : r.GetString(4)));
        }

        var operadores = new List<OperadorOpcion>();
        await using (var cmd = tx.Comando("SELECT persona_id, nombre_completo FROM seguridad.operadores_para_ruta($1, $2)", organizacion, sucursal))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
        {
            while (await r.ReadAsync(ct)) operadores.Add(new OperadorOpcion(r.GetGuid(0), r.GetString(1)));
        }

        var clientes = new List<ClienteOpcion>();
        await using (var cmd = tx.Comando($"""
            SELECT c.id, {NombreCliente} AS nombre FROM negocio.cliente c
            WHERE c.organizacion_id = $1 AND c.sucursal_id = $2
            ORDER BY nombre, c.id
            """, organizacion, sucursal))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
        {
            while (await r.ReadAsync(ct)) clientes.Add(new ClienteOpcion(r.GetGuid(0), r.GetString(1)));
        }

        // Solo puntos de servicio: las PTAR no están en negocio.punto_servicio.
        var sitiosCliente = await SitiosCliente.LeerAsync(tx, organizacion, Guid.Empty, ct);
        var puntos = new List<PuntoServicioOpcion>();
        await using (var cmd = tx.Comando($"""
            SELECT s.id, coalesce(nullif(btrim(fs.nombre), ''), s.referencia) AS nombre, {Direccion}
            FROM negocio.punto_servicio ps
            JOIN negocio.sitio s ON (s.organizacion_id, s.sucursal_id, s.id) = (ps.organizacion_id, ps.sucursal_id, ps.sitio_id)
            {FichaSitio}
            WHERE ps.organizacion_id = $1 AND ps.sucursal_id = $2
            ORDER BY nombre NULLS LAST, s.id
            """, organizacion, sucursal))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
        {
            while (await r.ReadAsync(ct))
            {
                var sitioId = r.GetGuid(0);
                var asociados = sitiosCliente.Where(x=>x.Key.Sucursal==sucursal)
                    .SelectMany(x=>x.Value.Where(s=>s.SitioId==sitioId).Select(s=>new ClienteSitioOpcion(x.Key.Cliente,s.Banos,s.Fosas))).ToArray();
                if (asociados.Length==0) continue;
                puntos.Add(new PuntoServicioOpcion(sitioId, r.IsDBNull(1) ? null : r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2), asociados));
            }
        }

        await tx.ConfirmarAsync(ct);
        return new OpcionesRuta(unidades, operadores, clientes, puntos);
    }

    // ---------------- Escritura de rutas ----------------

    public Task<ResultadoEscritura> CrearAsync(Guid organizacion, Guid acceso, Guid sucursal, DatosRuta d, CancellationToken ct) =>
        ConConflictoAsync(MensajeRutaModificada, async () =>
        {
            await using var tx = await TransaccionContexto.AbrirAsync(datos, organizacion, acceso, ct);
            if (!await PuedeEscribirAsync(tx, organizacion, sucursal, ct)) return ResultadoEscritura.SinPermiso;
            if (await ValidarRutaAsync(tx, organizacion, sucursal, d, ct) is { } invalida) return invalida;

            Guid id;
            await using (var cmd = tx.Comando("INSERT INTO negocio.ruta (organizacion_id, sucursal_id) VALUES ($1, $2) RETURNING id", organizacion, sucursal))
                id = (Guid)(await cmd.ExecuteScalarAsync(ct))!;
            await InsertarVersionRutaAsync(tx, organizacion, sucursal, id, 1, d, ct);
            await AsignarOperadorAsync(tx, organizacion, sucursal, d.UnidadId, d.OperadorId, ct);
            await tx.ConfirmarAsync(ct);
            return ResultadoEscritura.Correcta(id, 1);
        });

    public Task<ResultadoEscritura> EditarAsync(Guid organizacion, Guid acceso, Guid ruta, DatosRuta d, int revision, CancellationToken ct) =>
        ConConflictoAsync(MensajeRutaModificada, async () =>
        {
            await using var tx = await TransaccionContexto.AbrirAsync(datos, organizacion, acceso, ct);
            if (await SucursalDeRutaAsync(tx, organizacion, ruta, ct) is not { } sucursal) return ResultadoEscritura.NoEncontrada;
            if (!await PuedeEscribirAsync(tx, organizacion, sucursal, ct)) return ResultadoEscritura.SinPermiso;
            await BloquearAsync(tx, "ruta", ruta, ct);

            int actual; string nombre; short dias; Guid unidad;
            await using (var cmd = tx.Comando("""
                SELECT revision, nombre, dias, vehiculo_id FROM negocio.ruta_version
                WHERE organizacion_id = $1 AND sucursal_id = $2 AND ruta_id = $3
                ORDER BY revision DESC LIMIT 1
                """, organizacion, sucursal, ruta))
            await using (var r = await cmd.ExecuteReaderAsync(ct))
            {
                if (!await r.ReadAsync(ct)) return ResultadoEscritura.NoEncontrada;
                (actual, nombre, dias, unidad) = (r.GetInt32(0), r.GetString(1), r.GetInt16(2), r.GetGuid(3));
            }
            if (actual != revision) return ResultadoEscritura.Conflicto(MensajeRutaModificada);
            if (await ValidarRutaAsync(tx, organizacion, sucursal, d, ct) is { } invalida) return invalida;

            // El operador vive en asignacion_unidad, pero cambiarlo también es una revisión de la ruta:
            // así quien editó sobre la revisión anterior recibe 409 y no revierte el cambio sin aviso.
            var operadorCambio = await AsignarOperadorAsync(tx, organizacion, sucursal, d.UnidadId, d.OperadorId, ct);
            var nueva = actual;
            if (operadorCambio || nombre != d.Nombre || dias != d.Dias || unidad != d.UnidadId)
            {
                nueva = actual + 1;
                await InsertarVersionRutaAsync(tx, organizacion, sucursal, ruta, nueva, d, ct);
            }
            await tx.ConfirmarAsync(ct);
            return ResultadoEscritura.Correcta(ruta, nueva);
        });

    private static async Task<ResultadoEscritura?> ValidarRutaAsync(TransaccionContexto tx, Guid org, Guid suc, DatosRuta d, CancellationToken ct)
    {
        await using (var cmd = tx.Comando("SELECT EXISTS (SELECT 1 FROM negocio.vehiculo WHERE organizacion_id = $1 AND sucursal_id = $2 AND id = $3)", org, suc, d.UnidadId))
            if (!(bool)(await cmd.ExecuteScalarAsync(ct))!) return ResultadoEscritura.Invalida(MensajeUnidadInvalida);
        if (d.OperadorId is { } op)
        {
            await using var cmd = tx.Comando("SELECT EXISTS (SELECT 1 FROM seguridad.operadores_para_ruta($1, $2) WHERE persona_id = $3)", org, suc, op);
            if (!(bool)(await cmd.ExecuteScalarAsync(ct))!) return ResultadoEscritura.Invalida(MensajeOperadorInvalido);
        }
        return null;
    }

    private static async Task InsertarVersionRutaAsync(TransaccionContexto tx, Guid org, Guid suc, Guid ruta, int revision, DatosRuta d, CancellationToken ct)
    {
        await using var cmd = tx.Comando("""
            INSERT INTO negocio.ruta_version (organizacion_id, sucursal_id, ruta_id, revision, nombre, dias, vehiculo_id)
            VALUES ($1, $2, $3, $4, $5, $6, $7)
            """, org, suc, ruta, revision, d.Nombre, d.Dias, d.UnidadId);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// Operador de la unidad (afecta a todas sus rutas). Sin operador en la solicitud no cambia nada;
    /// si es el mismo que el actual tampoco se registra revisión. Devuelve si insertó una asignación.
    /// </summary>
    private static async Task<bool> AsignarOperadorAsync(TransaccionContexto tx, Guid org, Guid suc, Guid unidad, Guid? operador, CancellationToken ct)
    {
        if (operador is not { } persona) return false;
        await BloquearAsync(tx, "unidad", unidad, ct);
        int ultima = 0; Guid? actual = null;
        await using (var cmd = tx.Comando("""
            SELECT revision, persona_id FROM negocio.asignacion_unidad
            WHERE organizacion_id = $1 AND sucursal_id = $2 AND vehiculo_id = $3
            ORDER BY revision DESC LIMIT 1
            """, org, suc, unidad))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
        {
            if (await r.ReadAsync(ct)) (ultima, actual) = (r.GetInt32(0), r.IsDBNull(1) ? null : r.GetGuid(1));
        }
        if (actual == persona) return false;
        await using var ins = tx.Comando("""
            INSERT INTO negocio.asignacion_unidad (organizacion_id, sucursal_id, vehiculo_id, revision, persona_id)
            VALUES ($1, $2, $3, $4, $5)
            """, org, suc, unidad, ultima + 1, persona);
        await ins.ExecuteNonQueryAsync(ct);
        return true;
    }

    // ---------------- Escritura de paradas ----------------

    public Task<ResultadoEscritura> AgregarParadaAsync(Guid organizacion, Guid acceso, Guid ruta, DatosParada d, CancellationToken ct) =>
        ConConflictoAsync(MensajeParadasCambiaron, async () =>
        {
            await using var tx = await TransaccionContexto.AbrirAsync(datos, organizacion, acceso, ct);
            if (await SucursalDeRutaAsync(tx, organizacion, ruta, ct) is not { } sucursal) return ResultadoEscritura.NoEncontrada;
            if (!await PuedeEscribirAsync(tx, organizacion, sucursal, ct)) return ResultadoEscritura.SinPermiso;
            await BloquearAsync(tx, "ruta", ruta, ct);
            if (await ValidarClientesAsync(tx, organizacion, sucursal, d.Clientes, ct) is { } invalida) return invalida;
            var (sitio, error) = await ResolverSitioAsync(tx, organizacion, sucursal, d, ct);
            if (error is not null) return error;
            if (!await SitioDeClientesAsync(tx, organizacion, sucursal, sitio, d.Clientes, ct))
                return ResultadoEscritura.Invalida("La dirección elegida no está registrada para uno de los clientes. Revísala en Clientes.");

            int posicion;
            await using (var cmd = tx.Comando(
                "SELECT coalesce(max(posicion), 0) + 1 FROM negocio.parada WHERE organizacion_id = $1 AND sucursal_id = $2 AND ruta_id = $3",
                organizacion, sucursal, ruta))
                posicion = (int)(await cmd.ExecuteScalarAsync(ct))!;
            Guid id;
            await using (var cmd = tx.Comando(
                "INSERT INTO negocio.parada (organizacion_id, sucursal_id, ruta_id, posicion) VALUES ($1, $2, $3, $4) RETURNING id",
                organizacion, sucursal, ruta, posicion))
                id = (Guid)(await cmd.ExecuteScalarAsync(ct))!;
            await InsertarVersionParadaAsync(tx, organizacion, sucursal, id, 1, sitio, d.Clientes, ct);
            await tx.ConfirmarAsync(ct);
            return ResultadoEscritura.Correcta(id, 1);
        });

    public Task<ResultadoEscritura> EditarParadaAsync(Guid organizacion, Guid acceso, Guid ruta, Guid parada, DatosParada d, int revision, CancellationToken ct) =>
        ConConflictoAsync(MensajeParadaModificada, async () =>
        {
            await using var tx = await TransaccionContexto.AbrirAsync(datos, organizacion, acceso, ct);
            if (await SucursalDeRutaAsync(tx, organizacion, ruta, ct) is not { } sucursal) return ResultadoEscritura.NoEncontrada;
            if (!await PuedeEscribirAsync(tx, organizacion, sucursal, ct)) return ResultadoEscritura.SinPermiso;
            await BloquearAsync(tx, "ruta", ruta, ct);

            int actual; Guid version, sitioActual;
            await using (var cmd = tx.Comando("""
                SELECT v.revision, v.id, v.sitio_id FROM negocio.parada p
                JOIN negocio.parada_version v ON (v.organizacion_id, v.sucursal_id, v.parada_id) = (p.organizacion_id, p.sucursal_id, p.id)
                WHERE p.organizacion_id = $1 AND p.sucursal_id = $2 AND p.ruta_id = $3 AND p.id = $4
                ORDER BY v.revision DESC LIMIT 1
                """, organizacion, sucursal, ruta, parada))
            await using (var r = await cmd.ExecuteReaderAsync(ct))
            {
                if (!await r.ReadAsync(ct)) return ResultadoEscritura.NoEncontrada;
                (actual, version, sitioActual) = (r.GetInt32(0), r.GetGuid(1), r.GetGuid(2));
            }
            if (actual != revision) return ResultadoEscritura.Conflicto(MensajeParadaModificada);
            if (await ValidarClientesAsync(tx, organizacion, sucursal, d.Clientes, ct) is { } invalida) return invalida;

            // Mismo sitio y mismos clientes/baños: no hay revisión nueva.
            if (d.SitioId == sitioActual)
            {
                var previos = new HashSet<(Guid, int?, int?)>();
                await using (var cmd = tx.Comando(
                    "SELECT cliente_id, banos_planeados, fosas_planeadas FROM negocio.parada_cliente WHERE organizacion_id = $1 AND sucursal_id = $2 AND version_id = $3",
                    organizacion, sucursal, version))
                await using (var r = await cmd.ExecuteReaderAsync(ct))
                {
                    while (await r.ReadAsync(ct)) previos.Add((r.GetGuid(0), r.IsDBNull(1) ? null : r.GetInt32(1), r.IsDBNull(2) ? null : r.GetInt32(2)));
                }
                if (previos.SetEquals(d.Clientes.Select(c => (c.ClienteId, c.Banos, c.Fosas))))
                {
                    await tx.ConfirmarAsync(ct);
                    return ResultadoEscritura.Correcta(parada, actual);
                }
            }

            var (sitio, error) = await ResolverSitioAsync(tx, organizacion, sucursal, d, ct);
            if (error is not null) return error;
            if (!await SitioDeClientesAsync(tx, organizacion, sucursal, sitio, d.Clientes, ct))
                return ResultadoEscritura.Invalida("La dirección elegida no está registrada para uno de los clientes. Revísala en Clientes.");
            await InsertarVersionParadaAsync(tx, organizacion, sucursal, parada, actual + 1, sitio, d.Clientes, ct);
            await tx.ConfirmarAsync(ct);
            return ResultadoEscritura.Correcta(parada, actual + 1);
        });

    public Task<ResultadoEscritura> OrdenarAsync(Guid organizacion, Guid acceso, Guid ruta, IReadOnlyList<Guid> paradas, CancellationToken ct) =>
        ConConflictoAsync(MensajeParadasCambiaron, async () =>
        {
            await using var tx = await TransaccionContexto.AbrirAsync(datos, organizacion, acceso, ct);
            if (await SucursalDeRutaAsync(tx, organizacion, ruta, ct) is not { } sucursal) return ResultadoEscritura.NoEncontrada;
            if (!await PuedeEscribirAsync(tx, organizacion, sucursal, ct)) return ResultadoEscritura.SinPermiso;
            await BloquearAsync(tx, "ruta", ruta, ct);

            var existentes = new HashSet<Guid>();
            await using (var cmd = tx.Comando("SELECT id FROM negocio.parada WHERE organizacion_id = $1 AND sucursal_id = $2 AND ruta_id = $3",
                             organizacion, sucursal, ruta))
            await using (var r = await cmd.ExecuteReaderAsync(ct))
            {
                while (await r.ReadAsync(ct)) existentes.Add(r.GetGuid(0));
            }
            if (paradas.Count != existentes.Count || !existentes.SetEquals(paradas))
                return ResultadoEscritura.Conflicto(MensajeParadasCambiaron);

            // La unicidad de posición se comprueba al confirmar, así que los intercambios son válidos.
            await using (var cmd = tx.Comando("""
                UPDATE negocio.parada p SET posicion = t.pos
                FROM unnest($4::uuid[]) WITH ORDINALITY AS t(id, pos)
                WHERE p.organizacion_id = $1 AND p.sucursal_id = $2 AND p.ruta_id = $3 AND p.id = t.id AND p.posicion <> t.pos
                """, organizacion, sucursal, ruta, paradas.ToArray()))
                await cmd.ExecuteNonQueryAsync(ct);
            await tx.ConfirmarAsync(ct);
            return ResultadoEscritura.Correcta(ruta, 0);
        });

    private static async Task<ResultadoEscritura?> ValidarClientesAsync(
        TransaccionContexto tx, Guid org, Guid suc, IReadOnlyList<DatosClienteParada> clientes, CancellationToken ct)
    {
        await using var cmd = tx.Comando("SELECT count(*)::int FROM negocio.cliente WHERE organizacion_id = $1 AND sucursal_id = $2 AND id = ANY($3)",
            org, suc, clientes.Select(c => c.ClienteId).ToArray());
        return (int)(await cmd.ExecuteScalarAsync(ct))! == clientes.Count ? null : ResultadoEscritura.Invalida(MensajeClienteInvalido);
    }

    /// <summary>Punto de servicio existente de la sucursal o alta de uno nuevo (sitio + ficha revisión 1 + punto_servicio).</summary>
    private static async Task<bool> SitioDeClientesAsync(TransaccionContexto tx, Guid org, Guid suc, Guid sitio,
        IReadOnlyList<DatosClienteParada> clientes, CancellationToken ct)
    {
        await using var cmd=tx.Comando("""
            SELECT count(*)::int FROM (SELECT DISTINCT ON (cliente_id) activo
                FROM negocio.cliente_sitio WHERE organizacion_id=$1 AND sucursal_id=$2 AND sitio_id=$3 AND cliente_id=ANY($4)
                ORDER BY cliente_id,revision DESC) cs WHERE activo
            """,org,suc,sitio,clientes.Select(c=>c.ClienteId).ToArray());
        return (int)(await cmd.ExecuteScalarAsync(ct))! == clientes.Count;
    }

    private static async Task<(Guid Sitio, ResultadoEscritura? Error)> ResolverSitioAsync(
        TransaccionContexto tx, Guid org, Guid suc, DatosParada d, CancellationToken ct)
    {
        if (d.SitioId is { } existente)
        {
            await using (var candado=tx.Comando("SELECT pg_advisory_xact_lock(hashtextextended('sitio:' || $1::text || ':' || $2::text,0))",org,existente))
                await candado.ExecuteNonQueryAsync(ct);
            await using var cmd = tx.Comando(
                "SELECT EXISTS (SELECT 1 FROM negocio.punto_servicio WHERE organizacion_id = $1 AND sucursal_id = $2 AND sitio_id = $3)", org, suc, existente);
            return (bool)(await cmd.ExecuteScalarAsync(ct))!
                ? (existente, null)
                : (default, ResultadoEscritura.Invalida(MensajeNoEsPuntoServicio));
        }

        var n = d.NuevoSitio!;
        Guid sitio;
        await using (var cmd = tx.Comando("INSERT INTO negocio.sitio (organizacion_id, sucursal_id, referencia) VALUES ($1, $2, $3) RETURNING id", org, suc, n.Nombre))
            sitio = (Guid)(await cmd.ExecuteScalarAsync(ct))!;
        await using (var cmd = tx.Comando("""
            INSERT INTO negocio.ficha_sitio (organizacion_id, sucursal_id, sitio_id, revision, nombre, calle, colonia, municipio, estado, codigo_postal, referencia_localizacion)
            VALUES ($1, $2, $3, 1, $4, $5, $6, $7, $8, $9, $10)
            """, org, suc, sitio, n.Nombre, Nulo(n.Calle), Nulo(n.Colonia), Nulo(n.Municipio), Nulo(n.Estado), Nulo(n.CodigoPostal), Nulo(n.Referencia)))
            await cmd.ExecuteNonQueryAsync(ct);
        await using (var cmd = tx.Comando("INSERT INTO negocio.punto_servicio (organizacion_id, sucursal_id, sitio_id) VALUES ($1, $2, $3)", org, suc, sitio))
            await cmd.ExecuteNonQueryAsync(ct);
        // Compatibilidad con alta desde Rutas: el nuevo sitio queda registrado para sus clientes.
        await using (var cmd=tx.Comando("""
            INSERT INTO negocio.cliente_sitio (organizacion_id,sucursal_id,cliente_id,sitio_id,revision)
            SELECT $1,$2,c,$3,1 FROM unnest($4::uuid[]) c
            """,org,suc,sitio,d.Clientes.Select(c=>c.ClienteId).ToArray()))
            await cmd.ExecuteNonQueryAsync(ct);
        return (sitio, null);
    }

    private static async Task InsertarVersionParadaAsync(TransaccionContexto tx, Guid org, Guid suc, Guid parada, int revision, Guid sitio,
        IReadOnlyList<DatosClienteParada> clientes, CancellationToken ct)
    {
        Guid version;
        await using (var cmd = tx.Comando("""
            INSERT INTO negocio.parada_version (organizacion_id, sucursal_id, parada_id, revision, sitio_id)
            VALUES ($1, $2, $3, $4, $5) RETURNING id
            """, org, suc, parada, revision, sitio))
            version = (Guid)(await cmd.ExecuteScalarAsync(ct))!;
        await using (var cmd = tx.Comando("""
            INSERT INTO negocio.parada_cliente (organizacion_id, sucursal_id, version_id, cliente_id, banos_planeados, fosas_planeadas)
            SELECT $1, $2, $3, t.cliente, t.banos, t.fosas FROM unnest($4::uuid[], $5::int[], $6::int[]) AS t(cliente, banos, fosas)
            """, org, suc, version, clientes.Select(c => c.ClienteId).ToArray(), clientes.Select(c => c.Banos).ToArray(), clientes.Select(c => c.Fosas).ToArray()))
            await cmd.ExecuteNonQueryAsync(ct);
    }

    // ---------------- Comunes ----------------

    private static object Nulo(string? valor) => (object?)valor ?? DBNull.Value;

    private static async Task<Guid?> SucursalDeRutaAsync(TransaccionContexto tx, Guid org, Guid ruta, CancellationToken ct)
    {
        await using var cmd = tx.Comando("SELECT sucursal_id FROM negocio.ruta WHERE organizacion_id = $1 AND id = $2", org, ruta);
        return await cmd.ExecuteScalarAsync(ct) is Guid s ? s : null;
    }

    private static async Task<bool> PuedeEscribirAsync(TransaccionContexto tx, Guid org, Guid suc, CancellationToken ct)
    {
        await using var cmd = tx.Comando("SELECT coalesce(seguridad.puede($1, $2, 'datos.escribir'), false)", org, suc);
        return (bool)(await cmd.ExecuteScalarAsync(ct))!;
    }

    /// <summary>Serializa las escrituras sobre la misma ruta o unidad hasta el fin de la transacción.</summary>
    private static async Task BloquearAsync(TransaccionContexto tx, string tipo, Guid id, CancellationToken ct)
    {
        await using var cmd = tx.Comando("SELECT pg_advisory_xact_lock(hashtextextended($1, 0))", $"{tipo}:{id}");
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Una revisión o posición duplicada (23505) por una escritura simultánea se informa como conflicto.</summary>
    private static async Task<ResultadoEscritura> ConConflictoAsync(string mensaje, Func<Task<ResultadoEscritura>> accion)
    {
        try { return await accion(); }
        catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.UniqueViolation) { return ResultadoEscritura.Conflicto(mensaje); }
    }
}
