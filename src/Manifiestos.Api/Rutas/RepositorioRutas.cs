using Manifiestos.Api.Datos;
using Manifiestos.Api.Clientes;
using Npgsql;

namespace Manifiestos.Api.Rutas;

public interface IRepositorioRutas
{
    Task<IReadOnlyList<RutaResumen>> ListarAsync(Guid organizacion, Guid acceso, Guid? sucursal, string? servicio, CancellationToken ct);
    Task<IReadOnlyList<ParadaDetalle>?> ParadasAsync(Guid organizacion, Guid acceso, Guid ruta, CancellationToken ct);
    Task<OpcionesRuta> OpcionesAsync(Guid organizacion, Guid acceso, Guid sucursal, string? servicio, CancellationToken ct);
    Task<ResultadoEscritura> CrearAsync(Guid organizacion, Guid acceso, Guid sucursal, DatosRuta datos, CancellationToken ct);
    Task<ResultadoEscritura> EditarAsync(Guid organizacion, Guid acceso, Guid ruta, DatosRuta datos, int revision, CancellationToken ct);
    Task<ResultadoEscritura> AgregarParadaAsync(Guid organizacion, Guid acceso, Guid ruta, DatosParada datos, CancellationToken ct);
    Task<ResultadoEscritura> AgregarParadaSitioAsync(Guid organizacion, Guid acceso, Guid ruta, Guid sitio, bool reasignar, CancellationToken ct);
    Task<ResultadoEscritura> EditarParadaAsync(Guid organizacion, Guid acceso, Guid ruta, Guid parada, DatosParada datos, int revision, CancellationToken ct);
    Task<ResultadoEscritura> EliminarParadaAsync(Guid organizacion, Guid acceso, Guid ruta, Guid parada, int? revision, CancellationToken ct);
    Task<ResultadoEscritura> AgregarClienteAsync(Guid organizacion, Guid acceso, Guid ruta, Guid parada, Guid cliente, int? banos, int? ordenes, bool reasignar, int revision, CancellationToken ct);
    Task<ResultadoEscritura> EditarClienteAsync(Guid organizacion, Guid acceso, Guid ruta, Guid parada, Guid cliente, int? banos, int? ordenes, int revision, CancellationToken ct);
    Task<ResultadoEscritura> QuitarClienteAsync(Guid organizacion, Guid acceso, Guid ruta, Guid parada, Guid cliente, int revision, CancellationToken ct);
    Task<ResultadoEscritura> OrdenarAsync(Guid organizacion, Guid acceso, Guid ruta, IReadOnlyList<Guid> paradas, CancellationToken ct);
}

/// <summary>
/// Rutas recurrentes y paradas (migraciones 0008 y 0027) con el usuario manifiestos_api.
/// Toda consulta corre en una TransaccionContexto: RLS limita a las sucursales con datos.leer / datos.escribir.
/// Los cambios son revisiones nuevas; el único UPDATE es parada.posicion; las paradas se dan de baja (parada_baja), no se borran.
/// El servicio de una ruta (sanitarios | fosas) es la clasificación de su unidad.
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
    public const string MensajeUnidadSinClasificar = "Clasifica la unidad como sanitarios o fosas antes de usarla en una ruta.";
    public const string MensajeCapacidadIncompatible = "La capacidad de la unidad no corresponde a su clasificación.";
    public const string MensajeRutaDuplicada = "Esta unidad ya tiene una ruta en alguno de esos días: una unidad no puede estar en dos rutas el mismo día.";
    public const string MensajeServicioDistinto = "La unidad es de otro servicio y la ruta ya tiene paradas; crea una ruta nueva.";

    // Nombre visible del cliente: última ficha_empresa (comercial, si no fiscal), si no código comercial.
    private const string NombreCliente = """
        coalesce((SELECT coalesce(nullif(btrim(f.nombre_comercial), ''), nullif(btrim(f.nombre_fiscal), ''))
                  FROM negocio.ficha_empresa f
                  WHERE f.organizacion_id = c.organizacion_id AND f.empresa_id = c.empresa_id
                  ORDER BY f.revision DESC LIMIT 1),
                 nullif(btrim(c.codigo_comercial), ''), 'Cliente sin nombre')
        """;

    // Cliente con autorización de Medio ambiente como generador (alias c). Sin ella es cliente GENERAL.
    internal const string Autorizado = """
        EXISTS (SELECT 1 FROM negocio.autorizacion_empresa au
                WHERE (au.organizacion_id, au.empresa_id) = (c.organizacion_id, c.empresa_id)
                  AND au.autoridad = 'Medio ambiente' AND au.finalidad = 'Generador')
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

    // La parada pa (alias) no tiene baja.
    private const string SinBaja = """
        NOT EXISTS (SELECT 1 FROM negocio.parada_baja pb
                    WHERE (pb.organizacion_id, pb.sucursal_id, pb.parada_id) = (pa.organizacion_id, pa.sucursal_id, pa.id))
        """;

    // Última revisión de la parada pa (alias pv: id, revision, sitio_id).
    private const string UltimaVersionParada = """
        JOIN LATERAL (SELECT y.id, y.revision, y.sitio_id FROM negocio.parada_version y
            WHERE (y.organizacion_id, y.sucursal_id, y.parada_id) = (pa.organizacion_id, pa.sucursal_id, pa.id)
            ORDER BY y.revision DESC LIMIT 1) pv ON true
        """;

    private const string RutaDeParada = """
        JOIN LATERAL (SELECT x.nombre, x.vehiculo_id FROM negocio.ruta_version x
            WHERE (x.organizacion_id, x.sucursal_id, x.ruta_id) = (pa.organizacion_id, pa.sucursal_id, pa.ruta_id)
            ORDER BY x.revision DESC LIMIT 1) rv ON true
        LEFT JOIN LATERAL (SELECT x.clasificacion FROM negocio.ficha_vehiculo x
            WHERE (x.organizacion_id, x.sucursal_id, x.vehiculo_id) = (pa.organizacion_id, pa.sucursal_id, rv.vehiculo_id)
            ORDER BY x.revision DESC LIMIT 1) fr ON true
        """;

    // ---------------- Lectura ----------------

    public async Task<IReadOnlyList<RutaResumen>> ListarAsync(Guid organizacion, Guid acceso, Guid? sucursal, string? servicio, CancellationToken ct)
    {
        await using var tx = await TransaccionContexto.AbrirAsync(datos, organizacion, acceso, ct);
        await using var cmd = tx.Comando($"""
            SELECT r.id, r.sucursal_id, v.revision, v.nombre, v.dias,
                   veh.id, coalesce(fv.numero_economico, veh.numero_economico), cap.valor_m3, fv.clasificacion,
                   op.persona_id, p.nombre_completo,
                   coalesce(st.paradas, 0)::int, coalesce(st.clientes, 0)::int, coalesce(st.banos, 0)::int,
                   coalesce(st.ordenes, 0)::int, coalesce(st.ordenes_fija, 0)::int, st.nombres,
                   cg.banos, cg.m3, cg.capacidad_m3, cg.porcentaje, cg.ultima_tirada_en, cg.desglose::text,
                   cf.ordenes, cf.m3, cf.capacidad_m3, cf.porcentaje, cf.ultima_descarga_en, cf.desglose::text
            FROM negocio.ruta r
            JOIN LATERAL (SELECT x.revision, x.nombre, x.dias, x.vehiculo_id FROM negocio.ruta_version x
                WHERE (x.organizacion_id, x.sucursal_id, x.ruta_id) = (r.organizacion_id, r.sucursal_id, r.id)
                ORDER BY x.revision DESC LIMIT 1) v ON true
            JOIN negocio.vehiculo veh ON (veh.organizacion_id, veh.sucursal_id, veh.id) = (r.organizacion_id, r.sucursal_id, v.vehiculo_id)
            LEFT JOIN LATERAL (SELECT f.capacidad_id, f.numero_economico, f.clasificacion FROM negocio.ficha_vehiculo f
                WHERE (f.organizacion_id, f.sucursal_id, f.vehiculo_id) = (veh.organizacion_id, veh.sucursal_id, veh.id)
                ORDER BY f.revision DESC LIMIT 1) fv ON true
            LEFT JOIN catalogo.capacidad_vehiculo cap ON cap.id = fv.capacidad_id
            LEFT JOIN LATERAL (SELECT a.persona_id FROM negocio.asignacion_unidad a
                WHERE (a.organizacion_id, a.sucursal_id, a.vehiculo_id) = (veh.organizacion_id, veh.sucursal_id, veh.id)
                ORDER BY a.revision DESC LIMIT 1) op ON true
            LEFT JOIN seguridad.persona p ON (p.organizacion_id, p.id) = (r.organizacion_id, op.persona_id)
            LEFT JOIN LATERAL (
                SELECT count(DISTINCT pa.id) AS paradas, count(DISTINCT pc.cliente_id) AS clientes,
                       sum(coalesce(pc.banos_planeados, 0)) AS banos,
                       sum(coalesce(pc.fosas_planeadas, 0)) AS ordenes,
                       sum(CASE WHEN {Autorizado} THEN coalesce(pc.fosas_planeadas, 0) ELSE 0 END) AS ordenes_fija,
                       array_agg(DISTINCT {NombreCliente}) FILTER (WHERE pc.cliente_id IS NOT NULL) AS nombres
                FROM negocio.parada pa
                {UltimaVersionParada}
                LEFT JOIN negocio.parada_cliente pc ON (pc.organizacion_id, pc.sucursal_id, pc.version_id) = (pa.organizacion_id, pa.sucursal_id, pv.id)
                LEFT JOIN negocio.cliente c ON (c.organizacion_id, c.sucursal_id, c.id) = (pc.organizacion_id, pc.sucursal_id, pc.cliente_id)
                WHERE (pa.organizacion_id, pa.sucursal_id, pa.ruta_id) = (r.organizacion_id, r.sucursal_id, r.id) AND {SinBaja}) st ON true
            LEFT JOIN LATERAL (SELECT k.* FROM seguridad.carga_unidad_web(r.organizacion_id, $2, r.sucursal_id, veh.id) k) cg ON true
            LEFT JOIN LATERAL (SELECT k.* FROM seguridad.carga_fosas_web(r.organizacion_id, $2, r.sucursal_id, veh.id) k
                WHERE fv.clasificacion = 'fosas') cf ON true
            WHERE r.organizacion_id = $1 AND ($3::uuid IS NULL OR r.sucursal_id = $3) AND ($4::text IS NULL OR fv.clasificacion = $4)
            ORDER BY v.nombre, r.id
            """, organizacion, acceso, (object?)sucursal ?? DBNull.Value, (object?)servicio ?? DBNull.Value);
        var lista = new List<RutaResumen>();
        await using (var r = await cmd.ExecuteReaderAsync(ct))
        {
            while (await r.ReadAsync(ct))
            {
                var unidadId = r.GetGuid(5);
                var clasificacion = r.IsDBNull(8) ? null : r.GetString(8);
                var capacidad = r.IsDBNull(7) ? (decimal?)null : r.GetDecimal(7);
                var dias = r.GetInt16(4);
                var banos = r.GetInt32(13);
                var ordenes = r.GetInt32(14);
                var fija = r.GetInt32(15);
                var nombres = r.IsDBNull(16) ? [] : r.GetFieldValue<string[]>(16);
                CargaUnidad? carga = null;
                if (clasificacion == "sanitarios" && !r.IsDBNull(17))
                    carga = new CargaUnidad(unidadId, r.GetDecimal(19), r.GetInt32(17), r.GetDecimal(18), r.IsDBNull(20) ? 0m : r.GetDecimal(20),
                        r.IsDBNull(21) ? null : r.GetFieldValue<DateTimeOffset>(21), LeerDesglose(r.IsDBNull(22) ? null : r.GetString(22)));
                CargaFosas? cargaFosas = null;
                if (clasificacion == "fosas" && !r.IsDBNull(23) && !r.IsDBNull(25))
                {
                    // Baños de esta unidad que aún no se tiran (una unidad de fosas puede hacer baños): suman a la carga de la barra.
                    var banosPorTirar = r.IsDBNull(17) ? 0 : r.GetInt32(17);
                    var m3Banos = banosPorTirar > 0 && !r.IsDBNull(18) ? r.GetDecimal(18) : 0m;
                    var pctBanos = banosPorTirar > 0 && !r.IsDBNull(20) ? r.GetDecimal(20) : 0m;
                    cargaFosas = new CargaFosas(unidadId, r.GetDecimal(25), r.GetInt32(23), r.GetDecimal(24) + m3Banos, (r.IsDBNull(26) ? 0m : r.GetDecimal(26)) + pctBanos,
                        r.IsDBNull(27) ? null : r.GetFieldValue<DateTimeOffset>(27), LeerDesgloseFosas(r.IsDBNull(28) ? null : r.GetString(28)), banosPorTirar);
                }
                lista.Add(new RutaResumen(
                    r.GetGuid(0), r.GetGuid(1), r.GetInt32(2), r.GetString(3), DiasSemana.Lista(dias),
                    new UnidadResumen(unidadId, r.IsDBNull(6) ? null : r.GetString(6), capacidad, clasificacion),
                    r.IsDBNull(9) ? null : new OperadorResumen(r.GetGuid(9), r.IsDBNull(10) ? "" : r.GetString(10)),
                    r.GetInt32(11), r.GetInt32(12), clasificacion == "fosas" ? 0 : banos,
                    clasificacion, dias switch { 21 => "LMV", 42 => "MJS", _ => null },
                    clasificacion == "fosas" ? ordenes : null, carga,
                    clasificacion == "fosas" && capacidad is { } c ? fija * c : null, nombres, cargaFosas));
            }
        }
        await tx.ConfirmarAsync(ct);
        return lista;
    }

    internal static IReadOnlyList<DesgloseFosas> LeerDesgloseFosas(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        return doc.RootElement.EnumerateArray()
            .Select(e => new DesgloseFosas(e.GetProperty("fecha").GetString() ?? "", e.GetProperty("ordenes").GetInt32())).ToList();
    }

    internal static IReadOnlyList<DesgloseCarga> LeerDesglose(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        return doc.RootElement.EnumerateArray()
            .Select(e => new DesgloseCarga(e.GetProperty("fecha").GetString() ?? "", e.GetProperty("banos").GetInt32())).ToList();
    }

    public async Task<IReadOnlyList<ParadaDetalle>?> ParadasAsync(Guid organizacion, Guid acceso, Guid ruta, CancellationToken ct)
    {
        await using var tx = await TransaccionContexto.AbrirAsync(datos, organizacion, acceso, ct);
        if (await SucursalDeRutaAsync(tx, organizacion, ruta, ct) is not { } sucursal) return null;
        var servicio = await ServicioDeRutaAsync(tx, organizacion, sucursal, ruta, ct);

        var filas = new List<(Guid Id, int Posicion, int Revision, Guid Version, SitioDetalle Sitio)>();
        await using (var cmd = tx.Comando($"""
            SELECT pa.id, pa.posicion, pv.revision, pv.id, s.id, fs.nombre, {Direccion}, fs.referencia_localizacion
            FROM negocio.parada pa
            {UltimaVersionParada}
            JOIN negocio.sitio s ON (s.organizacion_id, s.sucursal_id, s.id) = (pa.organizacion_id, pa.sucursal_id, pv.sitio_id)
            {FichaSitio}
            WHERE pa.organizacion_id = $1 AND pa.sucursal_id = $2 AND pa.ruta_id = $3 AND {SinBaja}
            ORDER BY pa.posicion
            """, organizacion, sucursal, ruta))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
        {
            while (await r.ReadAsync(ct))
                filas.Add((r.GetGuid(0), r.GetInt32(1), r.GetInt32(2), r.GetGuid(3), new SitioDetalle(
                    r.GetGuid(4), r.IsDBNull(5) ? null : r.GetString(5), r.IsDBNull(6) ? null : r.GetString(6), r.IsDBNull(7) ? null : r.GetString(7))));
        }

        var clientes = new Dictionary<Guid, List<(ClienteParada Cliente, bool Propia)>>();
        await using (var cmd = tx.Comando($"""
            SELECT pc.version_id, c.id, {NombreCliente} AS nombre, pc.banos_planeados, pc.fosas_planeadas,
                   {Autorizado} AS autorizado, NOT ({Autorizado})
            FROM negocio.parada_cliente pc
            JOIN negocio.parada_version v ON (v.organizacion_id, v.sucursal_id, v.id) = (pc.organizacion_id, pc.sucursal_id, pc.version_id)
            JOIN negocio.cliente c ON (c.organizacion_id, c.sucursal_id, c.id) = (pc.organizacion_id, pc.sucursal_id, pc.cliente_id)
            WHERE pc.organizacion_id = $1 AND pc.sucursal_id = $2 AND pc.version_id = ANY($3)
            ORDER BY nombre, c.id
            """, organizacion, sucursal, filas.Select(f => f.Version).ToArray()))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
        {
            while (await r.ReadAsync(ct))
            {
                if (!clientes.TryGetValue(r.GetGuid(0), out var l)) clientes[r.GetGuid(0)] = l = [];
                var fosas = r.IsDBNull(4) ? (int?)null : r.GetInt32(4);
                l.Add((new ClienteParada(r.GetGuid(1), r.GetString(2), r.IsDBNull(3) ? null : r.GetInt32(3), fosas, r.GetBoolean(5), fosas ?? 0), r.GetBoolean(6)));
            }
        }
        await tx.ConfirmarAsync(ct);

        return filas.Select(f =>
        {
            var lista = clientes.GetValueOrDefault(f.Version) ?? [];
            var tipo = servicio switch
            {
                "sanitarios" => "sanitarios",
                "fosas" => lista.Any(c => c.Propia) ? "fosa_propia" : "fosa_fija",
                _ => null,
            };
            return new ParadaDetalle(f.Id, f.Posicion, f.Revision, f.Sitio, lista.Select(c => c.Cliente).ToList(),
                lista.Sum(c => c.Cliente.Banos ?? 0), tipo, lista.Sum(c => c.Cliente.Fosas ?? 0));
        }).ToList();
    }

    public async Task<OpcionesRuta> OpcionesAsync(Guid organizacion, Guid acceso, Guid sucursal, string? servicio, CancellationToken ct)
    {
        await using var tx = await TransaccionContexto.AbrirAsync(datos, organizacion, acceso, ct);

        var unidades = new List<UnidadOpcion>();
        await using (var cmd = tx.Comando("""
            SELECT veh.id, coalesce(fv.numero_economico, veh.numero_economico), cap.valor_m3, op.persona_id, p.nombre_completo, fv.clasificacion
            FROM negocio.vehiculo veh
            LEFT JOIN LATERAL (SELECT f.capacidad_id, f.numero_economico, f.clasificacion FROM negocio.ficha_vehiculo f
                WHERE (f.organizacion_id, f.sucursal_id, f.vehiculo_id) = (veh.organizacion_id, veh.sucursal_id, veh.id)
                ORDER BY f.revision DESC LIMIT 1) fv ON true
            LEFT JOIN catalogo.capacidad_vehiculo cap ON cap.id = fv.capacidad_id
            LEFT JOIN LATERAL (SELECT a.persona_id FROM negocio.asignacion_unidad a
                WHERE (a.organizacion_id, a.sucursal_id, a.vehiculo_id) = (veh.organizacion_id, veh.sucursal_id, veh.id)
                ORDER BY a.revision DESC LIMIT 1) op ON true
            LEFT JOIN seguridad.persona p ON (p.organizacion_id, p.id) = (veh.organizacion_id, op.persona_id)
            WHERE veh.organizacion_id = $1 AND veh.sucursal_id = $2 AND ($3::text IS NULL OR fv.clasificacion = $3)
            ORDER BY coalesce(fv.numero_economico, veh.numero_economico) NULLS LAST, veh.id
            """, organizacion, sucursal, (object?)servicio ?? DBNull.Value))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
        {
            while (await r.ReadAsync(ct))
                unidades.Add(new UnidadOpcion(r.GetGuid(0), r.IsDBNull(1) ? null : r.GetString(1), r.IsDBNull(2) ? null : r.GetDecimal(2),
                    r.IsDBNull(3) ? null : r.GetGuid(3), r.IsDBNull(4) ? null : r.GetString(4), r.IsDBNull(5) ? null : r.GetString(5)));
        }

        var operadores = new List<OperadorOpcion>();
        await using (var cmd = tx.Comando("SELECT persona_id, nombre_completo FROM seguridad.operadores_para_ruta($1, $2)", organizacion, sucursal))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
        {
            while (await r.ReadAsync(ct)) operadores.Add(new OperadorOpcion(r.GetGuid(0), r.GetString(1)));
        }

        var clientes = new List<ClienteOpcion>();
        await using (var cmd = tx.Comando($"""
            SELECT c.id, {NombreCliente} AS nombre, {Autorizado} AS autorizado FROM negocio.cliente c
            WHERE c.organizacion_id = $1 AND c.sucursal_id = $2
            ORDER BY nombre, c.id
            """, organizacion, sucursal))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
        {
            while (await r.ReadAsync(ct)) clientes.Add(new ClienteOpcion(r.GetGuid(0), r.GetString(1), r.GetBoolean(2)));
        }

        // Dónde está hoy cada sitio y cada cliente: paradas vigentes (sin baja) de rutas del servicio pedido.
        var rutaDeSitio = new Dictionary<Guid, RutaReferencia>();
        var rutasDeSitio = new Dictionary<Guid, List<RutaReferencia>>();
        var rutaDeCliente = new Dictionary<Guid, RutaReferencia>();
        await using (var cmd = tx.Comando($"""
            SELECT pv.sitio_id, pc.cliente_id, pa.ruta_id, rv.nombre
            FROM negocio.parada pa
            {UltimaVersionParada}
            {RutaDeParada}
            LEFT JOIN negocio.parada_cliente pc ON (pc.organizacion_id, pc.sucursal_id, pc.version_id) = (pa.organizacion_id, pa.sucursal_id, pv.id)
            WHERE pa.organizacion_id = $1 AND pa.sucursal_id = $2 AND {SinBaja} AND ($3::text IS NULL OR fr.clasificacion = $3)
            ORDER BY rv.nombre, pa.ruta_id
            """, organizacion, sucursal, (object?)servicio ?? DBNull.Value))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
        {
            while (await r.ReadAsync(ct))
            {
                var referencia = new RutaReferencia(r.GetGuid(2), r.GetString(3));
                rutaDeSitio.TryAdd(r.GetGuid(0), referencia);
                if (!rutasDeSitio.TryGetValue(r.GetGuid(0), out var todas)) rutasDeSitio[r.GetGuid(0)] = todas = [];
                if (!todas.Any(x => x.Id == referencia.Id)) todas.Add(referencia);
                if (!r.IsDBNull(1)) rutaDeCliente.TryAdd(r.GetGuid(1), referencia);
            }
        }

        // Sitios de la sucursal sin baja. Los de un cliente autorizado son suyos (solo fosas); los demás son normales (para
        // cualquier cliente general). Las PTAR no están en negocio.punto_servicio.
        var autorizados = clientes.Where(c => c.Autorizado).ToDictionary(c => c.Id);
        var duenos = (await SitiosCliente.LeerAsync(tx, organizacion, Guid.Empty, ct))
            .Where(x => x.Key.Sucursal == sucursal && autorizados.ContainsKey(x.Key.Cliente))
            .SelectMany(x => x.Value.Select(s => (s.SitioId, Cliente: autorizados[x.Key.Cliente])))
            .GroupBy(x => x.SitioId).ToDictionary(g => g.Key, g => g.Select(x => x.Cliente).ToList());
        var puntos = new List<PuntoServicioOpcion>();
        var sitios = new List<SitioOpcion>();
        await using (var cmd = tx.Comando($"""
            SELECT s.id, coalesce(nullif(btrim(fs.nombre), ''), s.referencia) AS nombre, {Direccion}, {ReglasSitio.DeAutorizado} AS propio
            FROM negocio.punto_servicio ps
            JOIN negocio.sitio s ON (s.organizacion_id, s.sucursal_id, s.id) = (ps.organizacion_id, ps.sucursal_id, ps.sitio_id)
            {FichaSitio}
            WHERE ps.organizacion_id = $1 AND ps.sucursal_id = $2 AND {ReglasSitio.SinBaja}
            ORDER BY nombre NULLS LAST, s.id
            """, organizacion, sucursal))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
        {
            while (await r.ReadAsync(ct))
            {
                var sitioId = r.GetGuid(0);
                var nombre = r.IsDBNull(1) ? null : r.GetString(1);
                var direccion = r.IsDBNull(2) ? null : r.GetString(2);
                if (!r.GetBoolean(3))
                {
                    puntos.Add(new PuntoServicioOpcion(sitioId, nombre, direccion, [], Libre: true));
                    sitios.Add(new SitioOpcion(sitioId, nombre, direccion, null, rutaDeSitio.GetValueOrDefault(sitioId), rutasDeSitio.GetValueOrDefault(sitioId) ?? []));
                    continue;
                }
                // Sitio de un cliente autorizado: sin dueño activo no se ofrece; solo va en rutas de fosas.
                if (!duenos.TryGetValue(sitioId, out var propietarios)) continue;
                puntos.Add(new PuntoServicioOpcion(sitioId, nombre, direccion, propietarios.Select(c => new ClienteSitioOpcion(c.Id, null, null)).ToArray()));
                if (servicio != "sanitarios")
                    sitios.Add(new SitioOpcion(sitioId, nombre, direccion, propietarios[0], rutaDeSitio.GetValueOrDefault(sitioId), rutasDeSitio.GetValueOrDefault(sitioId) ?? []));
            }
        }

        // Clientes generales (sin autorización de generador): van en cualquier parada de sitio normal.
        var generales = clientes.Where(c => !c.Autorizado)
            .Select(c => new ClienteGeneralOpcion(c.Id, c.Nombre, rutaDeCliente.GetValueOrDefault(c.Id))).ToList();

        await tx.ConfirmarAsync(ct);
        return new OpcionesRuta(unidades, operadores, clientes, puntos, sitios, generales);
    }

    // ---------------- Escritura de rutas ----------------

    public Task<ResultadoEscritura> CrearAsync(Guid organizacion, Guid acceso, Guid sucursal, DatosRuta d, CancellationToken ct) =>
        ConConflictoAsync(MensajeRutaModificada, async () =>
        {
            await using var tx = await TransaccionContexto.AbrirAsync(datos, organizacion, acceso, ct);
            if (!await PuedeEscribirAsync(tx, organizacion, sucursal, ct)) return ResultadoEscritura.SinPermiso;
            // Serializa las altas de rutas de la misma unidad: el terciado no puede repetirse.
            await BloquearAsync(tx, "unidad", d.UnidadId, ct);
            var (numero, error) = await ValidarRutaAsync(tx, organizacion, sucursal, d, null, ct);
            if (error is not null) return error;

            Guid id;
            await using (var cmd = tx.Comando("INSERT INTO negocio.ruta (organizacion_id, sucursal_id) VALUES ($1, $2) RETURNING id", organizacion, sucursal))
                id = (Guid)(await cmd.ExecuteScalarAsync(ct))!;
            await InsertarVersionRutaAsync(tx, organizacion, sucursal, id, 1, numero!, d, ct);
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
            await BloquearAsync(tx, "unidad", d.UnidadId, ct);

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
            var (numero, error) = await ValidarRutaAsync(tx, organizacion, sucursal, d, ruta, ct);
            if (error is not null) return error;

            // Una ruta es de un solo servicio: con paradas no puede pasar a una unidad de otro servicio.
            var servicioActual = await ServicioDeRutaAsync(tx, organizacion, sucursal, ruta, ct);
            if (servicioActual is not null && servicioActual != await ClasificacionDeUnidadAsync(tx, organizacion, sucursal, d.UnidadId, ct)
                && await ContarParadasVigentesAsync(tx, organizacion, sucursal, ruta, ct) > 0)
                return ResultadoEscritura.Regla("servicio_distinto", MensajeServicioDistinto);

            // El operador vive en asignacion_unidad, pero cambiarlo también es una revisión de la ruta:
            // así quien editó sobre la revisión anterior recibe 409 y no revierte el cambio sin aviso.
            var operadorCambio = await AsignarOperadorAsync(tx, organizacion, sucursal, d.UnidadId, d.OperadorId, ct);
            var nombreNuevo = numero!;
            var nueva = actual;
            if (operadorCambio || nombre != nombreNuevo || dias != d.Dias || unidad != d.UnidadId)
            {
                nueva = actual + 1;
                await InsertarVersionRutaAsync(tx, organizacion, sucursal, ruta, nueva, nombreNuevo, d, ct);
            }
            await tx.ConfirmarAsync(ct);
            return ResultadoEscritura.Correcta(ruta, nueva);
        });

    /// <summary>
    /// Unidad de la sucursal con clasificación y capacidad coherentes (sanitarios = 1 m³; fosas = 10/15/18 m³), operador válido
    /// y sin otra ruta de la misma unidad con los mismos días (se compara la máscara, no las iniciales: martes y miércoles
    /// son ambos «M»). Devuelve el NOMBRE de la ruta (número económico + terciado), con los días sin ambigüedad entre paréntesis
    /// solo si otra ruta de la unidad ya usa ese mismo nombre.
    /// </summary>
    private static async Task<(string? Numero, ResultadoEscritura? Error)> ValidarRutaAsync(
        TransaccionContexto tx, Guid org, Guid suc, DatosRuta d, Guid? rutaActual, CancellationToken ct)
    {
        string? numero = null, clasificacion = null; short? capacidad = null;
        await using (var cmd = tx.Comando("""
            SELECT coalesce(f.numero_economico, veh.numero_economico), f.clasificacion, f.capacidad_id
            FROM negocio.vehiculo veh
            LEFT JOIN LATERAL (SELECT x.numero_economico, x.clasificacion, x.capacidad_id FROM negocio.ficha_vehiculo x
                WHERE (x.organizacion_id, x.sucursal_id, x.vehiculo_id) = (veh.organizacion_id, veh.sucursal_id, veh.id)
                ORDER BY x.revision DESC LIMIT 1) f ON true
            WHERE veh.organizacion_id = $1 AND veh.sucursal_id = $2 AND veh.id = $3
            """, org, suc, d.UnidadId))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
        {
            if (!await r.ReadAsync(ct)) return (null, ResultadoEscritura.Invalida(MensajeUnidadInvalida));
            numero = r.IsDBNull(0) ? null : r.GetString(0);
            clasificacion = r.IsDBNull(1) ? null : r.GetString(1);
            capacidad = r.IsDBNull(2) ? null : r.GetInt16(2);
        }
        if (d.OperadorId is { } op)
        {
            await using var cmd = tx.Comando("SELECT EXISTS (SELECT 1 FROM seguridad.operadores_para_ruta($1, $2) WHERE persona_id = $3)", org, suc, op);
            if (!(bool)(await cmd.ExecuteScalarAsync(ct))!) return (null, ResultadoEscritura.Invalida(MensajeOperadorInvalido));
        }
        if (clasificacion is null) return (null, ResultadoEscritura.Regla("unidad_sin_clasificar", MensajeUnidadSinClasificar));
        if (clasificacion == "sanitarios" ? capacidad != 4 : capacidad is not (1 or 2 or 3))
            return (null, ResultadoEscritura.Regla("capacidad_incompatible", MensajeCapacidadIncompatible));
        if (string.IsNullOrWhiteSpace(numero)) return (null, ResultadoEscritura.Regla("unidad_sin_numero", "La unidad no tiene número económico."));

        var usados = new HashSet<string>();
        // Misma unidad y mismos días en otra ruta de la sucursal; además se recogen los nombres ya usados por la unidad.
        await using (var cmd = tx.Comando("""
            SELECT ru.id, rv.nombre, rv.dias FROM negocio.ruta ru
            JOIN LATERAL (SELECT x.nombre, x.dias, x.vehiculo_id FROM negocio.ruta_version x
                WHERE (x.organizacion_id, x.sucursal_id, x.ruta_id) = (ru.organizacion_id, ru.sucursal_id, ru.id)
                ORDER BY x.revision DESC LIMIT 1) rv ON true
            WHERE ru.organizacion_id = $1 AND ru.sucursal_id = $2 AND rv.vehiculo_id = $3 AND ($4::uuid IS NULL OR ru.id <> $4)
            """, org, suc, d.UnidadId, (object?)rutaActual ?? DBNull.Value))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
        {
            while (await r.ReadAsync(ct))
            {
                if ((r.GetInt16(2) & d.Dias) != 0)
                    return (null, ResultadoEscritura.Conflicto(MensajeRutaDuplicada, "ruta_duplicada", new RutaReferencia(r.GetGuid(0), r.GetString(1))));
                usados.Add(r.GetString(1));
            }
        }
        var nombre = DiasSemana.NombreRuta(numero, d.Dias);
        if (usados.Contains(nombre)) nombre = $"{nombre} ({DiasSemana.Desambiguado(d.Dias)})";
        return (nombre, null);
    }

    private static async Task InsertarVersionRutaAsync(TransaccionContexto tx, Guid org, Guid suc, Guid ruta, int revision, string nombre, DatosRuta d, CancellationToken ct)
    {
        await using var cmd = tx.Comando("""
            INSERT INTO negocio.ruta_version (organizacion_id, sucursal_id, ruta_id, revision, nombre, dias, vehiculo_id)
            VALUES ($1, $2, $3, $4, $5, $6, $7)
            """, org, suc, ruta, revision, nombre, d.Dias, d.UnidadId);
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

    // ---------------- Escritura de paradas: contrato anterior (parada completa con clientes) ----------------

    public Task<ResultadoEscritura> AgregarParadaAsync(Guid organizacion, Guid acceso, Guid ruta, DatosParada d, CancellationToken ct) =>
        ConConflictoAsync(MensajeParadasCambiaron, async () =>
        {
            await using var tx = await TransaccionContexto.AbrirAsync(datos, organizacion, acceso, ct);
            if (await SucursalDeRutaAsync(tx, organizacion, ruta, ct) is not { } sucursal) return ResultadoEscritura.NoEncontrada;
            if (!await PuedeEscribirAsync(tx, organizacion, sucursal, ct)) return ResultadoEscritura.SinPermiso;
            await BloquearAsync(tx, "ruta", ruta, ct);
            if (await ValidarClientesAsync(tx, organizacion, sucursal, d.Clientes, ct) is { } invalida) return invalida;
            var servicio = await ServicioDeRutaAsync(tx, organizacion, sucursal, ruta, ct);
            if (await ValidarServicioClientesAsync(tx, organizacion, sucursal, servicio, d.Clientes, ct) is { } regla) return regla;
            var (sitio, error) = await ResolverSitioAsync(tx, organizacion, sucursal, d, ct);
            if (error is not null) return error;
            if (!await SitioDeClientesAsync(tx, organizacion, sucursal, sitio, d.Clientes, ct))
                return ResultadoEscritura.Invalida("El sitio elegido no es válido para uno de los clientes: un autorizado va en su dirección activa o en un sitio normal; un general, solo en un sitio normal.");
            if (await SitioEnRutaAsync(tx, organizacion, sucursal, ruta, sitio, null, ct))
                return ResultadoEscritura.Conflicto(MensajeSitioYaEnRuta, "sitio_ya_en_ruta", new RutaReferencia(ruta, await NombreRutaAsync(tx, organizacion, sucursal, ruta, ct)));

            var (id, _) = await InsertarParadaAsync(tx, organizacion, sucursal, ruta, sitio, d.Clientes, ct);
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

            if (await LeerParadaAsync(tx, organizacion, sucursal, ruta, parada, ct) is not { } vigente) return ResultadoEscritura.NoEncontrada;
            if (vigente.Revision != revision) return ResultadoEscritura.Conflicto(MensajeParadaModificada);
            if (await ValidarClientesAsync(tx, organizacion, sucursal, d.Clientes, ct) is { } invalida) return invalida;
            var servicio = await ServicioDeRutaAsync(tx, organizacion, sucursal, ruta, ct);
            if (await ValidarServicioClientesAsync(tx, organizacion, sucursal, servicio, d.Clientes, ct) is { } regla) return regla;

            // Mismo sitio y mismos clientes/baños: no hay revisión nueva.
            if (d.SitioId == vigente.Sitio &&
                vigente.Clientes.Select(c => (c.Cliente, c.Banos, c.Fosas)).ToHashSet()
                    .SetEquals(d.Clientes.Select(c => (c.ClienteId, c.Banos, c.Fosas))))
            {
                await tx.ConfirmarAsync(ct);
                return ResultadoEscritura.Correcta(parada, vigente.Revision);
            }

            var (sitio, error) = await ResolverSitioAsync(tx, organizacion, sucursal, d, ct);
            if (error is not null) return error;
            if (!await SitioDeClientesAsync(tx, organizacion, sucursal, sitio, d.Clientes, ct))
                return ResultadoEscritura.Invalida("El sitio elegido no es válido para uno de los clientes: un autorizado va en su dirección activa o en un sitio normal; un general, solo en un sitio normal.");
            if (sitio != vigente.Sitio && await SitioEnRutaAsync(tx, organizacion, sucursal, ruta, sitio, parada, ct))
                return ResultadoEscritura.Conflicto(MensajeSitioYaEnRuta, "sitio_ya_en_ruta", new RutaReferencia(ruta, await NombreRutaAsync(tx, organizacion, sucursal, ruta, ct)));
            await InsertarVersionParadaAsync(tx, organizacion, sucursal, parada, vigente.Revision + 1, sitio, d.Clientes, ct);
            await tx.ConfirmarAsync(ct);
            return ResultadoEscritura.Correcta(parada, vigente.Revision + 1);
        });

    // ---------------- Escritura de paradas: parada = sitio y clientes uno por uno ----------------

    private const string MensajeJornadaPublicada = "La ruta tiene una jornada publicada que incluye esta parada. Retira la jornada antes de eliminarla o reasignarla.";

    /// <summary>Jornada programada o en curso de hoy en adelante: el operador la vería cambiar o desaparecer.</summary>
    private static async Task<bool> TieneJornadaPublicadaAsync(TransaccionContexto tx, Guid org, Guid suc, Guid ruta, CancellationToken ct)
    {
        await using var cmd = tx.Comando("""
            SELECT EXISTS (SELECT 1 FROM negocio.jornada j
                JOIN LATERAL (SELECT x.estado FROM negocio.estado_jornada x
                    WHERE (x.organizacion_id, x.sucursal_id, x.jornada_id) = (j.organizacion_id, j.sucursal_id, j.id)
                    ORDER BY x.revision DESC LIMIT 1) e ON e.estado IN ('programada', 'en_curso')
                WHERE (j.organizacion_id, j.sucursal_id, j.ruta_id) = ($1, $2, $3) AND j.fecha >= (now() AT TIME ZONE 'UTC')::date)
            """, org, suc, ruta);
        return (bool)(await cmd.ExecuteScalarAsync(ct))!;
    }

    public Task<ResultadoEscritura> AgregarParadaSitioAsync(Guid organizacion, Guid acceso, Guid ruta, Guid sitio, bool reasignar, CancellationToken ct) =>
        ConConflictoAsync(MensajeParadasCambiaron, async () =>
        {
            await using var tx = await TransaccionContexto.AbrirAsync(datos, organizacion, acceso, ct);
            if (await SucursalDeRutaAsync(tx, organizacion, ruta, ct) is not { } sucursal) return ResultadoEscritura.NoEncontrada;
            if (!await PuedeEscribirAsync(tx, organizacion, sucursal, ct)) return ResultadoEscritura.SinPermiso;
            var servicio = await ServicioDeRutaAsync(tx, organizacion, sucursal, ruta, ct);

            // Se bloquean juntas (en orden) la ruta y las que hoy tienen el sitio, para que reasignar no provoque bloqueos mutuos.
            List<Ocupacion> previas = !reasignar || servicio is null ? new() : await OcupacionesSitioAsync(tx, organizacion, sucursal, ruta, sitio, servicio, ct);
            await BloquearRutasAsync(tx, previas.Select(o => o.Ruta).Append(ruta), ct);

            if (servicio is null) return ResultadoEscritura.Regla("unidad_sin_clasificar", MensajeUnidadSinClasificar);
            var (_, error) = await ResolverSitioAsync(tx, organizacion, sucursal, new DatosParada(sitio, null, []), ct);
            if (error is not null) return error;
            if (await SitioEnRutaAsync(tx, organizacion, sucursal, ruta, sitio, null, ct))
                return ResultadoEscritura.Conflicto(MensajeSitioYaEnRuta, "sitio_ya_en_ruta", new RutaReferencia(ruta, await NombreRutaAsync(tx, organizacion, sucursal, ruta, ct)));
            // Sanitarios solo va en sitios normales; los sitios de un cliente autorizado son de su fosa (rutas de fosas).
            if (servicio == "sanitarios" && !await ReglasSitio.EsNormalAsync(tx, organizacion, sucursal, sitio, ct))
                return ResultadoEscritura.Regla("sitio_de_cliente_autorizado", "Ese sitio es de un cliente autorizado: solo va en rutas de fosas.");

            // Un mismo sitio puede estar en dos rutas a la vez (reagendas, emergencias); solo se mueve si se pide reasignar.
            var otras = reasignar ? await OcupacionesSitioAsync(tx, organizacion, sucursal, ruta, sitio, servicio, ct) : [];
            if (otras.Count > 0)
            {
                foreach (var o in otras)
                    if (await TieneJornadaPublicadaAsync(tx, organizacion, sucursal, o.Ruta, ct))
                        return ResultadoEscritura.Conflicto(MensajeJornadaPublicada, "ruta_vigente", new RutaReferencia(o.Ruta, o.RutaNombre));
                foreach (var o in otras) await DarDeBajaAsync(tx, organizacion, sucursal, o.Parada, "Reasignada a otra ruta", ct);
            }

            var (id, _) = await InsertarParadaAsync(tx, organizacion, sucursal, ruta, sitio, [], ct);
            await tx.ConfirmarAsync(ct);
            return ResultadoEscritura.Correcta(id, 1);
        });

    public Task<ResultadoEscritura> AgregarClienteAsync(Guid organizacion, Guid acceso, Guid ruta, Guid parada, Guid cliente,
        int? banos, int? ordenes, bool reasignar, int revision, CancellationToken ct) =>
        ConConflictoAsync(MensajeParadaModificada, async () =>
        {
            await using var tx = await TransaccionContexto.AbrirAsync(datos, organizacion, acceso, ct);
            if (await SucursalDeRutaAsync(tx, organizacion, ruta, ct) is not { } sucursal) return ResultadoEscritura.NoEncontrada;
            if (!await PuedeEscribirAsync(tx, organizacion, sucursal, ct)) return ResultadoEscritura.SinPermiso;
            var servicio = await ServicioDeRutaAsync(tx, organizacion, sucursal, ruta, ct);

            // Primera lectura sin bloqueo solo para saber qué rutas hay que bloquear; todo se vuelve a leer con los bloqueos tomados.
            var sitioPrevio = (await LeerParadaAsync(tx, organizacion, sucursal, ruta, parada, ct))?.Sitio;
            List<Ocupacion> previas = !reasignar || servicio is null || sitioPrevio is null ? new()
                : await OcupacionesClienteAsync(tx, organizacion, sucursal, ruta, cliente, servicio == "fosas" ? sitioPrevio : null, servicio, ct);
            await BloquearRutasAsync(tx, previas.Select(o => o.Ruta).Append(ruta), ct);

            if (await LeerParadaAsync(tx, organizacion, sucursal, ruta, parada, ct) is not { } actual) return ResultadoEscritura.NoEncontrada;
            if (actual.Revision != revision) return ResultadoEscritura.Conflicto(MensajeParadaModificada, "revision_desactualizada");
            if (servicio is null) return ResultadoEscritura.Regla("unidad_sin_clasificar", MensajeUnidadSinClasificar);

            var datosCliente = new DatosClienteParada(cliente, banos, ordenes);
            if (await ValidarClientesAsync(tx, organizacion, sucursal, [datosCliente], ct) is { } invalida) return invalida;
            if (CantidadInvalida(servicio, banos, ordenes) is { } cantidad) return cantidad;
            if (servicio == "sanitarios" && (await ClientesAutorizadosAsync(tx, organizacion, sucursal, [cliente], ct)).Count > 0)
                return ResultadoEscritura.Regla("cliente_autorizado_en_sanitarios", "Solo los clientes generales (sin autorización de generador) van en rutas de sanitarios.");
            if (actual.Clientes.Any(c => c.Cliente == cliente))
                return ResultadoEscritura.Conflicto("Ese cliente ya está en la parada.", "cliente_ya_en_parada");
            if (!await SitioDeClientesAsync(tx, organizacion, sucursal, actual.Sitio, [datosCliente], ct))
                return ResultadoEscritura.Regla("cliente_no_va_en_sitio", "Un cliente autorizado va en su dirección activa o en un sitio normal; un cliente general, solo en un sitio normal.");
            // Fosa fija (sitio de un cliente autorizado): una sola fosa y un solo cliente por parada.
            if (servicio == "fosas" && actual.Clientes.Count > 0 && (await ClientesAutorizadosAsync(tx, organizacion, sucursal, [cliente, .. actual.Clientes.Select(c => c.Cliente)], ct)).Count > 0)
                return ResultadoEscritura.Regla("fosa_fija_un_cliente", "La parada de un cliente autorizado lleva solo a ese cliente.");

            // Un mismo cliente puede estar en dos rutas a la vez; solo se mueve si se pide reasignar.
            var otras = reasignar ? await OcupacionesClienteAsync(tx, organizacion, sucursal, ruta, cliente, servicio == "fosas" ? actual.Sitio : null, servicio, ct) : [];
            if (otras.Count > 0)
            {
                foreach (var o in otras)
                    if (await TieneJornadaPublicadaAsync(tx, organizacion, sucursal, o.Ruta, ct))
                        return ResultadoEscritura.Conflicto(MensajeJornadaPublicada, "ruta_vigente", new RutaReferencia(o.Ruta, o.RutaNombre));
                foreach (var o in otras) await QuitarClienteDeParadaAsync(tx, organizacion, sucursal, o.Parada, cliente, servicio, ct);
            }

            var conjunto = actual.Clientes.Select(c => new DatosClienteParada(c.Cliente, c.Banos, c.Fosas)).Append(
                new DatosClienteParada(cliente, servicio == "sanitarios" ? banos : null, servicio == "fosas" ? ordenes : null)).ToList();
            await InsertarVersionParadaAsync(tx, organizacion, sucursal, parada, actual.Revision + 1, actual.Sitio, conjunto, ct);
            await tx.ConfirmarAsync(ct);
            return ResultadoEscritura.Correcta(parada, actual.Revision + 1);
        });

    public Task<ResultadoEscritura> EditarClienteAsync(Guid organizacion, Guid acceso, Guid ruta, Guid parada, Guid cliente,
        int? banos, int? ordenes, int revision, CancellationToken ct) =>
        ConConflictoAsync(MensajeParadaModificada, async () =>
        {
            await using var tx = await TransaccionContexto.AbrirAsync(datos, organizacion, acceso, ct);
            if (await SucursalDeRutaAsync(tx, organizacion, ruta, ct) is not { } sucursal) return ResultadoEscritura.NoEncontrada;
            if (!await PuedeEscribirAsync(tx, organizacion, sucursal, ct)) return ResultadoEscritura.SinPermiso;
            await BloquearAsync(tx, "ruta", ruta, ct);
            if (await LeerParadaAsync(tx, organizacion, sucursal, ruta, parada, ct) is not { } actual) return ResultadoEscritura.NoEncontrada;
            if (actual.Clientes.All(c => c.Cliente != cliente)) return ResultadoEscritura.NoEncontrada;
            if (actual.Revision != revision) return ResultadoEscritura.Conflicto(MensajeParadaModificada, "revision_desactualizada");
            var servicio = await ServicioDeRutaAsync(tx, organizacion, sucursal, ruta, ct);
            if (servicio is null) return ResultadoEscritura.Regla("unidad_sin_clasificar", MensajeUnidadSinClasificar);
            if (CantidadInvalida(servicio, banos, ordenes) is { } cantidad) return cantidad;

            var nuevo = actual.Clientes.Select(c => c.Cliente != cliente ? new DatosClienteParada(c.Cliente, c.Banos, c.Fosas)
                : new DatosClienteParada(c.Cliente, servicio == "sanitarios" ? banos : c.Banos, servicio == "fosas" ? ordenes : c.Fosas)).ToList();
            if (nuevo.Select(c => (c.ClienteId, c.Banos, c.Fosas)).ToHashSet()
                .SetEquals(actual.Clientes.Select(c => (c.Cliente, c.Banos, c.Fosas))))
            {
                await tx.ConfirmarAsync(ct);
                return ResultadoEscritura.Correcta(parada, actual.Revision);
            }
            await InsertarVersionParadaAsync(tx, organizacion, sucursal, parada, actual.Revision + 1, actual.Sitio, nuevo, ct);
            await tx.ConfirmarAsync(ct);
            return ResultadoEscritura.Correcta(parada, actual.Revision + 1);
        });

    public Task<ResultadoEscritura> QuitarClienteAsync(Guid organizacion, Guid acceso, Guid ruta, Guid parada, Guid cliente, int revision, CancellationToken ct) =>
        ConConflictoAsync(MensajeParadaModificada, async () =>
        {
            await using var tx = await TransaccionContexto.AbrirAsync(datos, organizacion, acceso, ct);
            if (await SucursalDeRutaAsync(tx, organizacion, ruta, ct) is not { } sucursal) return ResultadoEscritura.NoEncontrada;
            if (!await PuedeEscribirAsync(tx, organizacion, sucursal, ct)) return ResultadoEscritura.SinPermiso;
            await BloquearAsync(tx, "ruta", ruta, ct);
            if (await LeerParadaAsync(tx, organizacion, sucursal, ruta, parada, ct) is not { } actual) return ResultadoEscritura.NoEncontrada;
            if (actual.Clientes.All(c => c.Cliente != cliente)) return ResultadoEscritura.NoEncontrada;
            if (actual.Revision != revision) return ResultadoEscritura.Conflicto(MensajeParadaModificada, "revision_desactualizada");
            var restantes = actual.Clientes.Where(c => c.Cliente != cliente).Select(c => new DatosClienteParada(c.Cliente, c.Banos, c.Fosas)).ToList();
            // La parada es el sitio: puede quedar sin clientes (también en fosas).
            await InsertarVersionParadaAsync(tx, organizacion, sucursal, parada, actual.Revision + 1, actual.Sitio, restantes, ct);
            await tx.ConfirmarAsync(ct);
            return ResultadoEscritura.Correcta(parada, actual.Revision + 1);
        });

    /// <summary>
    /// Da de baja la parada (registro nuevo en parada_baja; la parada y su historia se conservan). No se permite mientras la ruta
    /// tenga una jornada publicada (programada o en curso) de hoy en adelante: el operador la vería desaparecer.
    /// </summary>
    public Task<ResultadoEscritura> EliminarParadaAsync(Guid organizacion, Guid acceso, Guid ruta, Guid parada, int? revision, CancellationToken ct) =>
        ConConflictoAsync(MensajeParadasCambiaron, async () =>
        {
            await using var tx = await TransaccionContexto.AbrirAsync(datos, organizacion, acceso, ct);
            if (await SucursalDeRutaAsync(tx, organizacion, ruta, ct) is not { } sucursal) return ResultadoEscritura.NoEncontrada;
            if (!await PuedeEscribirAsync(tx, organizacion, sucursal, ct)) return ResultadoEscritura.SinPermiso;
            await BloquearAsync(tx, "ruta", ruta, ct);
            if (await LeerParadaAsync(tx, organizacion, sucursal, ruta, parada, ct) is not { } actual) return ResultadoEscritura.NoEncontrada;
            if (revision is { } vista && vista != actual.Revision) return ResultadoEscritura.Conflicto(MensajeParadaModificada, "revision_desactualizada");

            if (await TieneJornadaPublicadaAsync(tx, organizacion, sucursal, ruta, ct))
                return ResultadoEscritura.Conflicto(MensajeJornadaPublicada, "ruta_vigente");

            await DarDeBajaAsync(tx, organizacion, sucursal, parada, null, ct);
            await tx.ConfirmarAsync(ct);
            return ResultadoEscritura.Correcta(parada, actual.Revision);
        });

    public Task<ResultadoEscritura> OrdenarAsync(Guid organizacion, Guid acceso, Guid ruta, IReadOnlyList<Guid> paradas, CancellationToken ct) =>
        ConConflictoAsync(MensajeParadasCambiaron, async () =>
        {
            await using var tx = await TransaccionContexto.AbrirAsync(datos, organizacion, acceso, ct);
            if (await SucursalDeRutaAsync(tx, organizacion, ruta, ct) is not { } sucursal) return ResultadoEscritura.NoEncontrada;
            if (!await PuedeEscribirAsync(tx, organizacion, sucursal, ct)) return ResultadoEscritura.SinPermiso;
            await BloquearAsync(tx, "ruta", ruta, ct);

            var existentes = new HashSet<Guid>();
            var bajas = new List<Guid>();
            await using (var cmd = tx.Comando($"""
                SELECT pa.id, NOT ({SinBaja}) AS baja FROM negocio.parada pa
                WHERE pa.organizacion_id = $1 AND pa.sucursal_id = $2 AND pa.ruta_id = $3
                ORDER BY pa.posicion
                """, organizacion, sucursal, ruta))
            await using (var r = await cmd.ExecuteReaderAsync(ct))
            {
                while (await r.ReadAsync(ct))
                    if (r.GetBoolean(1)) bajas.Add(r.GetGuid(0)); else existentes.Add(r.GetGuid(0));
            }
            if (paradas.Count != existentes.Count || !existentes.SetEquals(paradas))
                return ResultadoEscritura.Conflicto(MensajeParadasCambiaron);

            // La unicidad de posición se comprueba al confirmar, así que los intercambios son válidos. Las paradas dadas de baja
            // pasan al final para no chocar con las posiciones de las vigentes.
            await using (var cmd = tx.Comando("""
                UPDATE negocio.parada p SET posicion = t.pos
                FROM unnest($4::uuid[]) WITH ORDINALITY AS t(id, pos)
                WHERE p.organizacion_id = $1 AND p.sucursal_id = $2 AND p.ruta_id = $3 AND p.id = t.id AND p.posicion <> t.pos
                """, organizacion, sucursal, ruta, paradas.Concat(bajas).ToArray()))
                await cmd.ExecuteNonQueryAsync(ct);
            await tx.ConfirmarAsync(ct);
            return ResultadoEscritura.Correcta(ruta, 0);
        });

    // ---------------- Reglas de servicio ----------------

    private const string MensajeSitioYaEnRuta = "Ese sitio ya tiene una parada en esta ruta.";

    /// <summary>Servicio de la ruta (clasificación de la unidad de su última revisión); null = sin clasificar.</summary>
    private static async Task<string?> ServicioDeRutaAsync(TransaccionContexto tx, Guid org, Guid suc, Guid ruta, CancellationToken ct)
    {
        await using var cmd = tx.Comando("""
            SELECT f.clasificacion FROM negocio.ruta_version v
            LEFT JOIN LATERAL (SELECT x.clasificacion FROM negocio.ficha_vehiculo x
                WHERE (x.organizacion_id, x.sucursal_id, x.vehiculo_id) = (v.organizacion_id, v.sucursal_id, v.vehiculo_id)
                ORDER BY x.revision DESC LIMIT 1) f ON true
            WHERE (v.organizacion_id, v.sucursal_id, v.ruta_id) = ($1, $2, $3)
            ORDER BY v.revision DESC LIMIT 1
            """, org, suc, ruta);
        return await cmd.ExecuteScalarAsync(ct) as string;
    }

    private static async Task<string?> ClasificacionDeUnidadAsync(TransaccionContexto tx, Guid org, Guid suc, Guid unidad, CancellationToken ct)
    {
        await using var cmd = tx.Comando("""
            SELECT clasificacion FROM negocio.ficha_vehiculo
            WHERE organizacion_id = $1 AND sucursal_id = $2 AND vehiculo_id = $3 ORDER BY revision DESC LIMIT 1
            """, org, suc, unidad);
        return await cmd.ExecuteScalarAsync(ct) as string;
    }

    private static async Task<string> NombreRutaAsync(TransaccionContexto tx, Guid org, Guid suc, Guid ruta, CancellationToken ct)
    {
        await using var cmd = tx.Comando("""
            SELECT nombre FROM negocio.ruta_version WHERE organizacion_id = $1 AND sucursal_id = $2 AND ruta_id = $3 ORDER BY revision DESC LIMIT 1
            """, org, suc, ruta);
        return await cmd.ExecuteScalarAsync(ct) as string ?? "";
    }

    private static async Task<int> ContarParadasVigentesAsync(TransaccionContexto tx, Guid org, Guid suc, Guid ruta, CancellationToken ct)
    {
        await using var cmd = tx.Comando($"""
            SELECT count(*)::int FROM negocio.parada pa
            WHERE pa.organizacion_id = $1 AND pa.sucursal_id = $2 AND pa.ruta_id = $3 AND {SinBaja}
            """, org, suc, ruta);
        return (int)(await cmd.ExecuteScalarAsync(ct))!;
    }

    internal static async Task<HashSet<Guid>> ClientesAutorizadosAsync(TransaccionContexto tx, Guid org, Guid suc, IEnumerable<Guid> clientes, CancellationToken ct)
    {
        var lista = new HashSet<Guid>();
        await using var cmd = tx.Comando($"""
            SELECT c.id FROM negocio.cliente c
            WHERE c.organizacion_id = $1 AND c.sucursal_id = $2 AND c.id = ANY($3) AND {Autorizado}
            """, org, suc, clientes.ToArray());
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) lista.Add(r.GetGuid(0));
        return lista;
    }

    /// <summary>
    /// Una ruta es solo de sanitarios o solo de fosas: en sanitarios solo clientes generales y baños; en fosas solo órdenes
    /// (fosas planeadas). Las rutas sin clasificar (anteriores a esta regla) conservan el comportamiento libre.
    /// </summary>
    private static async Task<ResultadoEscritura?> ValidarServicioClientesAsync(
        TransaccionContexto tx, Guid org, Guid suc, string? servicio, IReadOnlyList<DatosClienteParada> clientes, CancellationToken ct)
    {
        if (servicio == "sanitarios")
        {
            if (clientes.Any(c => (c.Fosas ?? 0) > 0))
                return ResultadoEscritura.Regla("fosas_en_sanitarios", "Una ruta de sanitarios no lleva fosas.");
            if ((await ClientesAutorizadosAsync(tx, org, suc, clientes.Select(c => c.ClienteId), ct)).Count > 0)
                return ResultadoEscritura.Regla("cliente_autorizado_en_sanitarios", "Solo los clientes generales (sin autorización de generador) van en rutas de sanitarios.");
        }
        else if (servicio == "fosas")
        {
            if (clientes.Any(c => (c.Banos ?? 0) > 0))
                return ResultadoEscritura.Regla("banos_en_fosas", "Una ruta de fosas no lleva baños.");
            // Fosa fija: un cliente autorizado va solo en su parada (también en POST/PUT de la parada completa, no solo al agregar un cliente).
            if (clientes.Select(c => c.ClienteId).Distinct().Count() > 1
                && (await ClientesAutorizadosAsync(tx, org, suc, clientes.Select(c => c.ClienteId), ct)).Count > 0)
                return ResultadoEscritura.Regla("fosa_fija_un_cliente", "La parada de un cliente autorizado lleva solo a ese cliente.");
        }
        return null;
    }

    /// <summary>Sanitarios: baños &gt; 0 y sin órdenes. Fosas: órdenes &gt; 0 y sin baños.</summary>
    private static ResultadoEscritura? CantidadInvalida(string servicio, int? banos, int? ordenes)
    {
        if (servicio == "sanitarios")
        {
            if (ordenes is not null) return ResultadoEscritura.Regla("ordenes_en_sanitarios", "En sanitarios se indican baños, no órdenes.");
            if (banos is not > 0) return ResultadoEscritura.Regla("banos_invalidos", "Indica cuántos baños se atienden (más de cero).");
        }
        else
        {
            if (banos is not null) return ResultadoEscritura.Regla("banos_en_fosas", "En fosas se indican órdenes, no baños.");
            if (ordenes is not > 0) return ResultadoEscritura.Regla("ordenes_invalidas", "Indica cuántas órdenes se hacen (más de cero).");
        }
        return null;
    }

    private sealed record Ocupacion(Guid Ruta, string RutaNombre, Guid Parada);

    /// <summary>Paradas vigentes del sitio en OTRAS rutas del mismo servicio.</summary>
    private static async Task<List<Ocupacion>> OcupacionesSitioAsync(TransaccionContexto tx, Guid org, Guid suc, Guid ruta, Guid sitio, string servicio, CancellationToken ct)
    {
        var lista = new List<Ocupacion>();
        await using var cmd = tx.Comando($"""
            SELECT pa.ruta_id, rv.nombre, pa.id FROM negocio.parada pa
            {UltimaVersionParada}
            {RutaDeParada}
            WHERE pa.organizacion_id = $1 AND pa.sucursal_id = $2 AND pa.ruta_id <> $3 AND pv.sitio_id = $4
              AND fr.clasificacion = $5 AND {SinBaja}
            ORDER BY rv.nombre, pa.id
            """, org, suc, ruta, sitio, servicio);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) lista.Add(new Ocupacion(r.GetGuid(0), r.GetString(1), r.GetGuid(2)));
        return lista;
    }

    /// <summary>Paradas vigentes del cliente (opcionalmente en ese sitio) en OTRAS rutas del mismo servicio.</summary>
    private static async Task<List<Ocupacion>> OcupacionesClienteAsync(TransaccionContexto tx, Guid org, Guid suc, Guid ruta, Guid cliente, Guid? sitio, string servicio, CancellationToken ct)
    {
        var lista = new List<Ocupacion>();
        await using var cmd = tx.Comando($"""
            SELECT pa.ruta_id, rv.nombre, pa.id FROM negocio.parada pa
            {UltimaVersionParada}
            {RutaDeParada}
            JOIN negocio.parada_cliente pc ON (pc.organizacion_id, pc.sucursal_id, pc.version_id) = (pa.organizacion_id, pa.sucursal_id, pv.id)
            WHERE pa.organizacion_id = $1 AND pa.sucursal_id = $2 AND pa.ruta_id <> $3 AND pc.cliente_id = $4
              AND ($5::uuid IS NULL OR pv.sitio_id = $5) AND fr.clasificacion = $6 AND {SinBaja}
            ORDER BY rv.nombre, pa.id
            """, org, suc, ruta, cliente, (object?)sitio ?? DBNull.Value, servicio);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) lista.Add(new Ocupacion(r.GetGuid(0), r.GetString(1), r.GetGuid(2)));
        return lista;
    }

    /// <summary>El sitio ya tiene una parada vigente en esa ruta (se ignora la parada indicada, que es la que se edita).</summary>
    private static async Task<bool> SitioEnRutaAsync(TransaccionContexto tx, Guid org, Guid suc, Guid ruta, Guid sitio, Guid? exceptoParada, CancellationToken ct)
    {
        await using var cmd = tx.Comando($"""
            SELECT EXISTS (SELECT 1 FROM negocio.parada pa
                {UltimaVersionParada}
                WHERE pa.organizacion_id = $1 AND pa.sucursal_id = $2 AND pa.ruta_id = $3 AND pv.sitio_id = $4
                  AND ($5::uuid IS NULL OR pa.id <> $5) AND {SinBaja})
            """, org, suc, ruta, sitio, (object?)exceptoParada ?? DBNull.Value);
        return (bool)(await cmd.ExecuteScalarAsync(ct))!;
    }

    private sealed record ParadaActual(int Revision, Guid Version, Guid Sitio, List<(Guid Cliente, int? Banos, int? Fosas)> Clientes);

    /// <summary>Última revisión de una parada vigente (sin baja) de la ruta, con sus clientes; null si no existe o está dada de baja.</summary>
    private static async Task<ParadaActual?> LeerParadaAsync(TransaccionContexto tx, Guid org, Guid suc, Guid ruta, Guid parada, CancellationToken ct)
    {
        int revision; Guid version, sitio;
        await using (var cmd = tx.Comando($"""
            SELECT pv.revision, pv.id, pv.sitio_id FROM negocio.parada pa
            {UltimaVersionParada}
            WHERE pa.organizacion_id = $1 AND pa.sucursal_id = $2 AND pa.ruta_id = $3 AND pa.id = $4 AND {SinBaja}
            """, org, suc, ruta, parada))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
        {
            if (!await r.ReadAsync(ct)) return null;
            (revision, version, sitio) = (r.GetInt32(0), r.GetGuid(1), r.GetGuid(2));
        }
        var clientes = new List<(Guid, int?, int?)>();
        await using (var cmd = tx.Comando("""
            SELECT cliente_id, banos_planeados, fosas_planeadas FROM negocio.parada_cliente
            WHERE organizacion_id = $1 AND sucursal_id = $2 AND version_id = $3 ORDER BY cliente_id
            """, org, suc, version))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
        {
            while (await r.ReadAsync(ct)) clientes.Add((r.GetGuid(0), r.IsDBNull(1) ? null : r.GetInt32(1), r.IsDBNull(2) ? null : r.GetInt32(2)));
        }
        return new ParadaActual(revision, version, sitio, clientes);
    }

    /// <summary>Saca al cliente de una parada de otra ruta (reasignación). En fosas, si queda vacía se da de baja.</summary>
    private static async Task QuitarClienteDeParadaAsync(TransaccionContexto tx, Guid org, Guid suc, Guid parada, Guid cliente, string servicio, CancellationToken ct)
    {
        Guid ruta;
        await using (var cmd = tx.Comando("SELECT ruta_id FROM negocio.parada WHERE organizacion_id = $1 AND sucursal_id = $2 AND id = $3", org, suc, parada))
            ruta = (Guid)(await cmd.ExecuteScalarAsync(ct))!;
        if (await LeerParadaAsync(tx, org, suc, ruta, parada, ct) is not { } otra) return;
        var restantes = otra.Clientes.Where(c => c.Cliente != cliente).Select(c => new DatosClienteParada(c.Cliente, c.Banos, c.Fosas)).ToList();
        if (restantes.Count == 0 && servicio != "sanitarios")
            await DarDeBajaAsync(tx, org, suc, parada, "Reasignada a otra ruta", ct);
        else
            await InsertarVersionParadaAsync(tx, org, suc, parada, otra.Revision + 1, otra.Sitio, restantes, ct);
    }

    private static async Task DarDeBajaAsync(TransaccionContexto tx, Guid org, Guid suc, Guid parada, string? motivo, CancellationToken ct)
    {
        await using var cmd = tx.Comando(
            "INSERT INTO negocio.parada_baja (organizacion_id, sucursal_id, parada_id, motivo) VALUES ($1, $2, $3, $4)",
            org, suc, parada, Nulo(motivo));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task<(Guid Id, int Posicion)> InsertarParadaAsync(TransaccionContexto tx, Guid org, Guid suc, Guid ruta, Guid sitio,
        IReadOnlyList<DatosClienteParada> clientes, CancellationToken ct)
    {
        int posicion;
        await using (var cmd = tx.Comando(
            "SELECT coalesce(max(posicion), 0) + 1 FROM negocio.parada WHERE organizacion_id = $1 AND sucursal_id = $2 AND ruta_id = $3",
            org, suc, ruta))
            posicion = (int)(await cmd.ExecuteScalarAsync(ct))!;
        Guid id;
        await using (var cmd = tx.Comando(
            "INSERT INTO negocio.parada (organizacion_id, sucursal_id, ruta_id, posicion) VALUES ($1, $2, $3, $4) RETURNING id",
            org, suc, ruta, posicion))
            id = (Guid)(await cmd.ExecuteScalarAsync(ct))!;
        await InsertarVersionParadaAsync(tx, org, suc, id, 1, sitio, clientes, ct);
        return (id, posicion);
    }

    private static async Task<ResultadoEscritura?> ValidarClientesAsync(
        TransaccionContexto tx, Guid org, Guid suc, IReadOnlyList<DatosClienteParada> clientes, CancellationToken ct)
    {
        await using var cmd = tx.Comando("SELECT count(*)::int FROM negocio.cliente WHERE organizacion_id = $1 AND sucursal_id = $2 AND id = ANY($3)",
            org, suc, clientes.Select(c => c.ClienteId).ToArray());
        return (int)(await cmd.ExecuteScalarAsync(ct))! == clientes.Count ? null : ResultadoEscritura.Invalida(MensajeClienteInvalido);
    }

    /// <summary>Todos los clientes pueden ir en el sitio (autorizado: su dirección activa o un sitio normal; general: sitio normal).</summary>
    private static Task<bool> SitioDeClientesAsync(TransaccionContexto tx, Guid org, Guid suc, Guid sitio,
        IReadOnlyList<DatosClienteParada> clientes, CancellationToken ct) =>
        ReglasSitio.ClientesValidosAsync(tx, org, suc, sitio, clientes.Select(c => c.ClienteId).ToList(), ct);

    /// <summary>Punto de servicio existente de la sucursal o alta de uno nuevo (sitio + ficha revisión 1 + punto_servicio).</summary>
    private static async Task<(Guid Sitio, ResultadoEscritura? Error)> ResolverSitioAsync(
        TransaccionContexto tx, Guid org, Guid suc, DatosParada d, CancellationToken ct)
    {
        if (d.SitioId is { } existente)
        {
            await using (var candado=tx.Comando("SELECT pg_advisory_xact_lock(hashtextextended('sitio:' || $1::text || ':' || $2::text,0))",org,existente))
                await candado.ExecuteNonQueryAsync(ct);
            await using var cmd = tx.Comando(
                """
                SELECT EXISTS (SELECT 1 FROM negocio.punto_servicio ps
                    JOIN negocio.sitio s ON (s.organizacion_id, s.sucursal_id, s.id) = (ps.organizacion_id, ps.sucursal_id, ps.sitio_id)
                    WHERE ps.organizacion_id = $1 AND ps.sucursal_id = $2 AND ps.sitio_id = $3 AND
                """ + " " + ReglasSitio.SinBaja + ")", org, suc, existente);
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
        // Compatibilidad con alta desde Rutas: solo los clientes autorizados tienen direcciones propias; los generales no se ligan.
        await using (var cmd=tx.Comando($"""
            INSERT INTO negocio.cliente_sitio (organizacion_id,sucursal_id,cliente_id,sitio_id,revision)
            SELECT $1,$2,c.id,$3,1 FROM negocio.cliente c
            WHERE c.organizacion_id=$1 AND c.sucursal_id=$2 AND c.id=ANY($4::uuid[]) AND {Autorizado}
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
        if (clientes.Count == 0) return;
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

    /// <summary>Bloquea varias rutas siempre en el mismo orden (por id) para que dos reasignaciones cruzadas no se bloqueen entre sí.</summary>
    private static async Task BloquearRutasAsync(TransaccionContexto tx, IEnumerable<Guid> rutas, CancellationToken ct)
    {
        foreach (var ruta in rutas.Distinct().OrderBy(x => x)) await BloquearAsync(tx, "ruta", ruta, ct);
    }

    /// <summary>Una revisión o posición duplicada (23505) o un bloqueo mutuo por una escritura simultánea se informan como conflicto.</summary>
    private static async Task<ResultadoEscritura> ConConflictoAsync(string mensaje, Func<Task<ResultadoEscritura>> accion)
    {
        try { return await accion(); }
        catch (PostgresException e) when (e.SqlState is PostgresErrorCodes.UniqueViolation or PostgresErrorCodes.DeadlockDetected)
        { return ResultadoEscritura.Conflicto(mensaje); }
    }
}
