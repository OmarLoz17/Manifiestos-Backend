using Manifiestos.Api.Acceso;
using Manifiestos.Api.Datos;
using Manifiestos.Api.Rutas;
using Npgsql;

namespace Manifiestos.Api.Ordenes;

// ---- Respuestas (JSON camelCase) ----

public sealed record UnidadDeOrden(Guid Id, string? NumeroEconomico, decimal? CapacidadM3, string? Clasificacion);

/// <summary>
/// Cliente atendido en una parada. Autorizado = tiene autorización de medio ambiente como generador:
/// él es el generador y firma; si no, el generador es nuestra empresa y no firma (requisitos 1.4).
/// Fosas = órdenes (vueltas) planeadas en la parada. FosaPropia = la fosa es nuestra, rentada: se deduce del cliente (decisión del
/// 9-nov-2026): un cliente autorizado tiene fosa propia SUYA (fija); uno general nos la renta (fosa nuestra). No se pregunta.
/// </summary>
public sealed record ClienteDeOrden(Guid ClienteId, string Nombre, bool Autorizado, int Banos, int Fosas, bool FosaPropia,
    IReadOnlyList<OrdenDeCliente>? Ordenes = null, bool AjustadoHoy = false, int PlanBanos = 0, int PlanFosas = 0);
/// <summary>
/// Una orden (vuelta) de fosa en el día pedido y qué firmas lleva ya. Transportista = el operador guardó el servicio; Cliente = firmó el
/// cliente autorizado (en un cliente general la firma es nuestra: va marcada); AguaYDrenaje = quien recibe en la planta firmó la descarga.
/// </summary>
/// <summary>Imagen de una firma de una orden (archivo de evidencia) y cuándo se hizo; se ve con GET /api/archivos/{id}.</summary>
public sealed record FirmaDeOrden(Guid ArchivoId, DateTimeOffset En);
public sealed record OrdenDeCliente(Guid? Id, int Numero, bool Transportista, bool Cliente, bool AguaYDrenaje, DateTimeOffset? ServicioEn = null, Guid? FotoId = null,
    FirmaDeOrden? FirmaTransportista = null, FirmaDeOrden? FirmaCliente = null, FirmaDeOrden? FirmaPlanta = null);
/// <summary>
/// Atención de una parada de SANITARIOS en el día pedido (se calcula de las capturas): baños succionados, cuándo se capturó la primera
/// succión y si ya se tiraron en la planta (la captura entró en una tirada). Null = sin captura todavía.
/// </summary>
/// <param name="BanosCubiertos">Suma de los baños asignados que ya cubrieron las capturas (cada captura guarda lo asignado a su orden): si Operaciones agrega baños después de atender la parada, lo asignado hoy supera lo cubierto y la parada vuelve a tener trabajo pendiente.</param>
/// <summary>Una captura de la parada (una por orden): su foto, cuándo se tomó, cuántos baños succionó y cuántos asignados cubrió. EsFosa = orden de fosa propia (sin baños).</summary>
public sealed record CapturaSanitarios(Guid? FotoId, DateTimeOffset CapturadaEn, int BanosSuccionados, int BanosCubiertos, bool EsFosa, bool Tirada = false);
public sealed record AtencionSanitarios(int BanosSuccionados, DateTimeOffset CapturadaEn, bool Tirada, Guid? FotoId = null, int? BanosCubiertos = null, IReadOnlyList<CapturaSanitarios>? Capturas = null);
public sealed record ParadaDeOrden(Guid Id, int Posicion, string? Sitio, string? Direccion, IReadOnlyList<ClienteDeOrden> Clientes,
    decimal? Latitud = null, decimal? Longitud = null, AtencionSanitarios? Atencion = null, bool NoSeHara = false, bool Terminada = false, int NoNecesarias = 0, bool Reportada = false, IReadOnlyList<Guid>? OrdenesNoNecesarias = null);
/// <summary>Parada extraordinaria: se agregó a la ruta de UN día (cliente no planeado que pidió succión); no se repite.</summary>
public sealed record ParadaExtraordinaria(
    Guid Id, DateOnly Fecha, string? Sitio, string? Direccion, IReadOnlyList<ClienteDeOrden> Clientes, DateTimeOffset CreadaEn,
    decimal? Latitud = null, decimal? Longitud = null, AtencionSanitarios? Atencion = null, Guid? FusionadaEn = null, bool NoSeHara = false, bool Terminada = false, int NoNecesarias = 0, bool Reportada = false, IReadOnlyList<Guid>? OrdenesNoNecesarias = null);
public sealed record RutaDeOrdenes(
    Guid Id, Guid SucursalId, string Nombre, IReadOnlyList<int> Dias, UnidadDeOrden Unidad, OperadorResumen? Operador,
    IReadOnlyList<ParadaDeOrden> Paradas, IReadOnlyList<ParadaExtraordinaria>? Extraordinarias = null, bool CambioDelDia = false,
    string? Empresa = null, bool NoSeHara = false);

/// <summary>Cambio de los baños de un cliente solo para ese día (se toma como extraordinario; el plan fijo no cambia).</summary>
public sealed record SolicitudAjusteBanos(Guid? RutaId, string? Fecha, Guid? ParadaId, Guid? ClienteId, int? Banos);

/// <summary>Cambio de las órdenes (fosas) de un cliente solo para ese día; las que pasan del plan son extraordinarias.</summary>
public sealed record SolicitudAjusteFosas(Guid? RutaId, string? Fecha, Guid? ParadaId, Guid? ClienteId, int? Fosas);

/// <summary>«No se hará hoy» (decisión 0031): toda la ruta (sin ParadaId) o una parada de HOY o de un día próximo; NoSeHara=false lo deshace. Nunca reagenda ni toca lo capturado.</summary>
public sealed record SolicitudNoSeHara(Guid? RutaId, string? Fecha, Guid? ParadaId, bool? NoSeHara);

/// <summary>Cambio de unidad y/o de operador de la ruta solo para ese día (extraordinario). PersonaId vacío = el operador asignado a esa unidad.</summary>
public sealed record SolicitudCambioUnidad(Guid? RutaId, string? Fecha, Guid? VehiculoId, Guid? PersonaId);

/// <summary>ClaveSolicitud: identifica el envío (el panel la genera al abrir el formulario); un reintento con la misma clave no duplica la parada.</summary>
public sealed record SolicitudExtraordinaria(Guid? RutaId, string? Fecha, Guid? SitioId, SolicitudClienteParada[]? Clientes, Guid? ClaveSolicitud = null);
/// <summary>Parada extraordinaria creada. Aviso: el sitio ya está o estuvo en la ruta (se permite en extraordinarias, pero Operaciones debe saberlo).</summary>
public sealed record ExtraordinariaCreada(Guid Id, int Revision, string? Aviso);

public interface IRepositorioOrdenes
{
    /// <summary>
    /// Con fecha, cada ruta trae además las paradas extraordinarias de ese día y lo atendido (firmas por orden, capturas de baños).
    /// Con fecha y hasta (un rango), solo las extraordinarias de esos días: cada una lleva su Fecha (para los reportes).
    /// </summary>
    Task<IReadOnlyList<RutaDeOrdenes>> ListarAsync(Guid organizacion, Guid acceso, DateOnly? fecha, DateOnly? hasta, CancellationToken ct);
    Task<ResultadoEscritura> AgregarExtraordinariaAsync(Guid organizacion, Guid acceso, Guid ruta, DateOnly fecha, Guid sitio, IReadOnlyList<DatosClienteParada> clientes, Guid? claveSolicitud, CancellationToken ct);
    Task<ResultadoEscritura> CambiarUnidadAsync(Guid organizacion, Guid acceso, Guid ruta, DateOnly fecha, Guid vehiculo, Guid? persona, CancellationToken ct);
    Task<ResultadoEscritura> MarcarNoSeHaraAsync(Guid organizacion, Guid acceso, Guid ruta, DateOnly fecha, Guid? parada, bool activo, CancellationToken ct);
    Task<ResultadoEscritura> AjustarBanosAsync(Guid organizacion, Guid acceso, Guid ruta, DateOnly fecha, Guid parada, Guid cliente, int banos, CancellationToken ct);
    Task<ResultadoEscritura> AjustarFosasAsync(Guid organizacion, Guid acceso, Guid ruta, DateOnly fecha, Guid parada, Guid cliente, int fosas, CancellationToken ct);
}

/// <summary>Órdenes por ruta y parada (solo lectura) a partir del plan vigente de Rutas y los sitios de cada cliente.</summary>
public sealed class RepositorioOrdenes(NpgsqlDataSource datos) : IRepositorioOrdenes
{
    public async Task<IReadOnlyList<RutaDeOrdenes>> ListarAsync(Guid organizacion, Guid acceso, DateOnly? fecha, DateOnly? hasta, CancellationToken ct)
    {
        var rango = fecha is { } desdeR && hasta is { } hastaR && hastaR != desdeR;
        await using var tx = await TransaccionContexto.AbrirAsync(datos, organizacion, acceso, ct);
        string? T(NpgsqlDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);

        // Clientes de la última revisión de cada parada.
        var clientes = new Dictionary<Guid, List<ClienteDeOrden>>();
        await using (var cmd = tx.Comando("""
            SELECT pa.id, pc.cliente_id,
                   coalesce(nullif(btrim(fe.nombre_comercial), ''), nullif(btrim(fe.nombre_fiscal), ''), nullif(btrim(c.codigo_comercial), ''), 'Cliente sin nombre'),
                   EXISTS (SELECT 1 FROM negocio.autorizacion_empresa a WHERE (a.organizacion_id, a.empresa_id) = (c.organizacion_id, c.empresa_id)
                           AND a.autoridad = 'Medio ambiente' AND a.finalidad = 'Generador'),
                   coalesce((SELECT aj.banos FROM negocio.ajuste_banos_dia aj WHERE (aj.organizacion_id, aj.sucursal_id, aj.parada_id, aj.cliente_id, aj.fecha) = (pa.organizacion_id, pa.sucursal_id, pa.id, pc.cliente_id, $2::date) ORDER BY aj.revision DESC LIMIT 1), pc.banos_planeados, 0),
                   coalesce((SELECT af.fosas FROM negocio.ajuste_fosas_dia af WHERE (af.organizacion_id, af.sucursal_id, af.parada_id, af.cliente_id, af.fecha) = (pa.organizacion_id, pa.sucursal_id, pa.id, pc.cliente_id, $2::date) ORDER BY af.revision DESC LIMIT 1), pc.fosas_planeadas, 0),
                   NOT EXISTS (SELECT 1 FROM negocio.autorizacion_empresa a WHERE (a.organizacion_id, a.empresa_id) = (c.organizacion_id, c.empresa_id) AND a.autoridad = 'Medio ambiente' AND a.finalidad = 'Generador'),
                   EXISTS (SELECT 1 FROM negocio.ajuste_banos_dia aj WHERE (aj.organizacion_id, aj.sucursal_id, aj.parada_id, aj.cliente_id, aj.fecha) = (pa.organizacion_id, pa.sucursal_id, pa.id, pc.cliente_id, $2::date))
                   OR EXISTS (SELECT 1 FROM negocio.ajuste_fosas_dia af WHERE (af.organizacion_id, af.sucursal_id, af.parada_id, af.cliente_id, af.fecha) = (pa.organizacion_id, pa.sucursal_id, pa.id, pc.cliente_id, $2::date)),
                   coalesce(pc.banos_planeados, 0), coalesce(pc.fosas_planeadas, 0)
            FROM negocio.parada pa
            JOIN LATERAL (SELECT x.id, x.sitio_id FROM negocio.parada_version x
                WHERE (x.organizacion_id, x.sucursal_id, x.parada_id) = (pa.organizacion_id, pa.sucursal_id, pa.id)
                ORDER BY x.revision DESC LIMIT 1) pv ON true
            JOIN negocio.parada_cliente pc ON (pc.organizacion_id, pc.sucursal_id, pc.version_id) = (pa.organizacion_id, pa.sucursal_id, pv.id)
            JOIN negocio.cliente c ON (c.organizacion_id, c.sucursal_id, c.id) = (pa.organizacion_id, pa.sucursal_id, pc.cliente_id)
            LEFT JOIN LATERAL (SELECT x.nombre_fiscal, x.nombre_comercial FROM negocio.ficha_empresa x
                WHERE (x.organizacion_id, x.empresa_id) = (c.organizacion_id, c.empresa_id) ORDER BY x.revision DESC LIMIT 1) fe ON true
            WHERE pa.organizacion_id = $1 AND NOT EXISTS (SELECT 1 FROM negocio.parada_baja pb WHERE (pb.organizacion_id, pb.sucursal_id, pb.parada_id) = (pa.organizacion_id, pa.sucursal_id, pa.id))
            ORDER BY pa.id, 3
            """, organizacion, (object?)(rango ? null : fecha) ?? DBNull.Value))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct))
            {
                if (!clientes.TryGetValue(r.GetGuid(0), out var l)) clientes[r.GetGuid(0)] = l = [];
                l.Add(new ClienteDeOrden(r.GetGuid(1), r.GetString(2), r.GetBoolean(3), r.GetInt32(4), r.GetInt32(5), r.GetBoolean(6), AjustadoHoy: r.GetBoolean(7), PlanBanos: r.GetInt32(8), PlanFosas: r.GetInt32(9)));
            }

        var paradas = new Dictionary<Guid, List<ParadaDeOrden>>();
        await using (var cmd = tx.Comando("""
            SELECT pa.ruta_id, pa.id, pa.posicion, coalesce(nullif(btrim(fs.nombre), ''), s.referencia),
                   nullif(concat_ws(', ', nullif(btrim(concat_ws(' ', fs.calle, fs.numero_exterior)), ''), fs.colonia, fs.municipio), ''),
                   fs.latitud, fs.longitud
            FROM negocio.parada pa
            JOIN LATERAL (SELECT x.sitio_id FROM negocio.parada_version x
                WHERE (x.organizacion_id, x.sucursal_id, x.parada_id) = (pa.organizacion_id, pa.sucursal_id, pa.id)
                ORDER BY x.revision DESC LIMIT 1) pv ON true
            JOIN negocio.sitio s ON (s.organizacion_id, s.sucursal_id, s.id) = (pa.organizacion_id, pa.sucursal_id, pv.sitio_id)
            LEFT JOIN LATERAL (SELECT x.* FROM negocio.ficha_sitio x
                WHERE (x.organizacion_id, x.sucursal_id, x.sitio_id) = (s.organizacion_id, s.sucursal_id, s.id)
                ORDER BY x.revision DESC LIMIT 1) fs ON true
            WHERE pa.organizacion_id = $1 AND NOT EXISTS (SELECT 1 FROM negocio.parada_baja pb WHERE (pb.organizacion_id, pb.sucursal_id, pb.parada_id) = (pa.organizacion_id, pa.sucursal_id, pa.id))
            ORDER BY pa.ruta_id, pa.posicion
            """, organizacion))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct))
            {
                if (!paradas.TryGetValue(r.GetGuid(0), out var l)) paradas[r.GetGuid(0)] = l = [];
                l.Add(new ParadaDeOrden(r.GetGuid(1), r.GetInt32(2), T(r, 3), T(r, 4), clientes.GetValueOrDefault(r.GetGuid(1)) ?? [],
                    r.IsDBNull(5) ? null : r.GetDecimal(5), r.IsDBNull(6) ? null : r.GetDecimal(6)));
            }

        // Paradas extraordinarias del día pedido, por ruta.
        var extras = new Dictionary<Guid, List<ParadaExtraordinaria>>();
        if (fecha is { } dia)
        {
            var porParada = new Dictionary<Guid, (Guid Ruta, string? Sitio, string? Dir, decimal? Lat, decimal? Lon, DateTimeOffset Creada, List<ClienteDeOrden> Clientes, DateOnly Fecha, Guid? Fusionada)>();
            await using (var cmd = tx.Comando("""
                SELECT pe.ruta_id, pe.id, coalesce(nullif(btrim(fs.nombre), ''), s.referencia),
                       nullif(concat_ws(', ', nullif(btrim(concat_ws(' ', fs.calle, fs.numero_exterior)), ''), fs.colonia, fs.municipio), ''),
                       fs.latitud, fs.longitud, pe.creada_en,
                       pc.cliente_id,
                       coalesce(nullif(btrim(fe.nombre_comercial), ''), nullif(btrim(fe.nombre_fiscal), ''), nullif(btrim(c.codigo_comercial), ''), 'Cliente sin nombre'),
                       EXISTS (SELECT 1 FROM negocio.autorizacion_empresa a WHERE (a.organizacion_id, a.empresa_id) = (c.organizacion_id, c.empresa_id)
                               AND a.autoridad = 'Medio ambiente' AND a.finalidad = 'Generador'),
                       coalesce(pc.banos_planeados, 0), coalesce(pc.fosas_planeadas, 0), NOT EXISTS (SELECT 1 FROM negocio.autorizacion_empresa a WHERE (a.organizacion_id, a.empresa_id) = (c.organizacion_id, c.empresa_id) AND a.autoridad = 'Medio ambiente' AND a.finalidad = 'Generador'), pe.fecha, pe.fusionada_en
                FROM negocio.parada_extraordinaria pe
                JOIN negocio.sitio s ON (s.organizacion_id, s.sucursal_id, s.id) = (pe.organizacion_id, pe.sucursal_id, pe.sitio_id)
                LEFT JOIN LATERAL (SELECT x.* FROM negocio.ficha_sitio x
                    WHERE (x.organizacion_id, x.sucursal_id, x.sitio_id) = (s.organizacion_id, s.sucursal_id, s.id)
                    ORDER BY x.revision DESC LIMIT 1) fs ON true
                JOIN negocio.parada_extraordinaria_cliente pc ON (pc.organizacion_id, pc.sucursal_id, pc.parada_extraordinaria_id) = (pe.organizacion_id, pe.sucursal_id, pe.id)
                JOIN negocio.cliente c ON (c.organizacion_id, c.sucursal_id, c.id) = (pe.organizacion_id, pe.sucursal_id, pc.cliente_id)
                LEFT JOIN LATERAL (SELECT x.nombre_fiscal, x.nombre_comercial FROM negocio.ficha_empresa x
                    WHERE (x.organizacion_id, x.empresa_id) = (c.organizacion_id, c.empresa_id) ORDER BY x.revision DESC LIMIT 1) fe ON true
                WHERE pe.organizacion_id = $1 AND pe.fecha BETWEEN $2::date AND $3::date
                ORDER BY pe.fecha, pe.creada_en, pe.id, 9
                """, organizacion, dia, hasta ?? dia))
            await using (var r = await cmd.ExecuteReaderAsync(ct))
                while (await r.ReadAsync(ct))
                {
                    var id = r.GetGuid(1);
                    if (!porParada.TryGetValue(id, out var e))
                        porParada[id] = e = (r.GetGuid(0), T(r, 2), T(r, 3), r.IsDBNull(4) ? null : r.GetDecimal(4), r.IsDBNull(5) ? null : r.GetDecimal(5),
                            new DateTimeOffset(DateTime.SpecifyKind(r.GetDateTime(6), DateTimeKind.Utc)), [], DateOnly.FromDateTime(r.GetDateTime(13)), r.IsDBNull(14) ? null : r.GetGuid(14));
                    e.Clientes.Add(new ClienteDeOrden(r.GetGuid(7), r.GetString(8), r.GetBoolean(9), r.GetInt32(10), r.GetInt32(11), r.GetBoolean(12)));
                }
            foreach (var (id, e) in porParada.OrderBy(x => x.Value.Creada))
            {
                if (!extras.TryGetValue(e.Ruta, out var lista)) extras[e.Ruta] = lista = [];
                lista.Add(new ParadaExtraordinaria(id, e.Fecha, e.Sitio, e.Dir, e.Clientes, e.Creada, e.Lat, e.Lon, FusionadaEn: e.Fusionada));
            }
        }

        // Con fecha: firmas de cada orden de fosa del día (la orden es el hash jornada:parada:cliente:vuelta, igual que en la base).
        var jornadaDeRuta = new Dictionary<Guid, Guid>();
        var firmas = new HashSet<(Guid Orden, string Tipo)>();
        var servicioEn = new Dictionary<Guid, DateTimeOffset>();
        var fotoDe = new Dictionary<Guid, Guid>();   // archivo de la foto de la captura del servicio más antigua de cada orden
        var firmaDe = new Dictionary<(Guid Orden, string Tipo), FirmaDeOrden>();   // imagen de la firma de cada paso (la más antigua) y su hora
        if (fecha is { } dia2 && !rango)
        {
            await using (var cmd = tx.Comando("SELECT id, ruta_id FROM negocio.jornada WHERE organizacion_id = $1 AND fecha = $2::date", organizacion, dia2))
            await using (var r = await cmd.ExecuteReaderAsync(ct))
                while (await r.ReadAsync(ct)) jornadaDeRuta[r.GetGuid(1)] = r.GetGuid(0);
            await using (var cmd = tx.Comando("""
                SELECT ct.orden_derivada_id, ct.tipo, k.ocurrida_en,
                       (SELECT e.archivo_id FROM negocio.evidencia_captura e WHERE (e.organizacion_id, e.sucursal_id, e.captura_id) = (ct.organizacion_id, ct.sucursal_id, ct.captura_id) AND e.rol = 'FOTO' AND e.archivo_id IS NOT NULL LIMIT 1),
                       (SELECT e.archivo_id FROM negocio.evidencia_captura e WHERE (e.organizacion_id, e.sucursal_id, e.captura_id) = (ct.organizacion_id, ct.sucursal_id, ct.captura_id) AND e.rol IN ('FIRMA_OPERADOR', 'FIRMA_CLIENTE', 'FIRMA_RECEPCION') AND e.archivo_id IS NOT NULL LIMIT 1)
                FROM negocio.captura_trabajo_efectiva ct JOIN negocio.captura k ON (k.organizacion_id, k.sucursal_id, k.id) = (ct.organizacion_id, ct.sucursal_id, ct.captura_id)
                JOIN negocio.jornada j ON (j.organizacion_id, j.sucursal_id, j.id) = (ct.organizacion_id, ct.sucursal_id, ct.jornada_id)
                WHERE ct.organizacion_id = $1 AND j.fecha = $2::date AND ct.tipo IN ('SERVICIO_OPERADOR', 'FIRMA_CLIENTE', 'RECEPCION_PLANTA')
                  AND negocio.captura_aplicable(ct.organizacion_id, ct.sucursal_id, ct.captura_id)
                """, organizacion, dia2))
            await using (var r = await cmd.ExecuteReaderAsync(ct))
                while (await r.ReadAsync(ct))
                {
                    firmas.Add((r.GetGuid(0), r.GetString(1)));
                    if (!r.IsDBNull(4))
                    {
                        var hora = new DateTimeOffset(DateTime.SpecifyKind(r.GetDateTime(2), DateTimeKind.Utc));
                        if (!firmaDe.TryGetValue((r.GetGuid(0), r.GetString(1)), out var anterior) || hora < anterior.En) firmaDe[(r.GetGuid(0), r.GetString(1))] = new FirmaDeOrden(r.GetGuid(4), hora);
                    }
                    if (r.GetString(1) == "SERVICIO_OPERADOR")
                    {
                        var en = new DateTimeOffset(DateTime.SpecifyKind(r.GetDateTime(2), DateTimeKind.Utc));
                        if (!servicioEn.TryGetValue(r.GetGuid(0), out var previa) || en < previa)
                        {
                            servicioEn[r.GetGuid(0)] = en;
                            if (!r.IsDBNull(3)) fotoDe[r.GetGuid(0)] = r.GetGuid(3); else fotoDe.Remove(r.GetGuid(0));
                        }
                    }
                }
        }
        // Paradas con un reporte de problema del operador abierto hoy (no cuenta el aviso automático de baños incompletos): se ven en gris.
        var paradasReportadas = new HashSet<Guid>();
        if (fecha is { } diaR && !rango)
        {
            await using var cmd = tx.Comando("""
                SELECT DISTINCT i.parada_id FROM negocio.incidencia i
                WHERE i.organizacion_id = $1 AND i.parada_id IS NOT NULL AND i.ocurrida_en >= $2::date AND i.ocurrida_en < $2::date + 1
                  AND NOT i.automatica AND coalesce(i.descripcion, '') NOT LIKE 'Aviso automático%' AND NOT (i.motivo = 'servicio_parcial' AND coalesce(i.descripcion, '') LIKE 'Se atendieron % de % baños.' AND i.ocurrida_en < timestamptz '2026-11-20 00:00-06')
                  AND coalesce((SELECT s.estatus FROM negocio.seguimiento_incidencia s WHERE (s.organizacion_id, s.sucursal_id, s.incidencia_id) = (i.organizacion_id, i.sucursal_id, i.id)
                                ORDER BY s.revision DESC LIMIT 1), 'abierta') <> 'cerrada'
                """, organizacion, diaR);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) paradasReportadas.Add(r.GetGuid(0));
        }
        // «Terminar parada» del teléfono (migración 0056): paradas del día que el operador cerró con las órdenes que necesitó. Vale la última revisión.
        var paradasTerminadas = new Dictionary<Guid, int>();   // parada -> órdenes que no se necesitaron
        var ordenesNoNecesarias = new Dictionary<Guid, IReadOnlyList<Guid>>();   // parada -> sus identidades (si el teléfono las mandó)
        if (fecha is { } diaT && !rango)
        {
            await using var cmd = tx.Comando("""
                SELECT DISTINCT ON (t.jornada_id, t.parada_id) t.parada_id, t.terminada, t.no_necesarias, t.ordenes_no_necesarias
                FROM negocio.parada_terminada_dia t JOIN negocio.jornada j ON (j.organizacion_id, j.sucursal_id, j.id) = (t.organizacion_id, t.sucursal_id, t.jornada_id)
                WHERE t.organizacion_id = $1 AND j.fecha = $2::date
                ORDER BY t.jornada_id, t.parada_id, t.revision DESC
                """, organizacion, diaT);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) if (r.GetBoolean(1)) { paradasTerminadas[r.GetGuid(0)] = r.GetInt32(2); var ids = r.GetFieldValue<Guid[]>(3); if (ids.Length > 0) ordenesNoNecesarias[r.GetGuid(0)] = ids; }
        }
        // Sanitarios: succiones capturadas del día por parada (del plan o extraordinaria); Tirada = TODAS ya entraron en una tirada.
        var atencionSanitarios = new Dictionary<Guid, AtencionSanitarios>();
        if (fecha is { } dia3 && !rango)
        {
            await using var cmd = tx.Comando("""
                SELECT sp.parada_id, sp.banos_succionados, sp.fecha_captura,
                       EXISTS (SELECT 1 FROM negocio.tirada_parada tp WHERE (tp.organizacion_id, tp.sucursal_id, tp.captura_id) = (sp.organizacion_id, sp.sucursal_id, sp.captura_id)),
                       (SELECT e.archivo_id FROM negocio.evidencia_captura e WHERE (e.organizacion_id, e.sucursal_id, e.captura_id) = (sp.organizacion_id, sp.sucursal_id, sp.captura_id) AND e.rol = 'FOTO' AND e.archivo_id IS NOT NULL LIMIT 1),
                       sp.banos_asignados, sp.es_fosa
                FROM negocio.succion_parada sp JOIN negocio.jornada j ON (j.organizacion_id, j.sucursal_id, j.id) = (sp.organizacion_id, sp.sucursal_id, sp.jornada_id)
                WHERE sp.organizacion_id = $1 AND j.fecha = $2::date
                """, organizacion, dia3);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            var porParada = new Dictionary<Guid, List<(CapturaSanitarios Captura, bool Tirada)>>();
            while (await r.ReadAsync(ct))
            {
                var en = new DateTimeOffset(DateTime.SpecifyKind(r.GetDateTime(2), DateTimeKind.Utc));
                if (!porParada.TryGetValue(r.GetGuid(0), out var lista)) porParada[r.GetGuid(0)] = lista = [];
                lista.Add((new CapturaSanitarios(r.IsDBNull(4) ? null : r.GetGuid(4), en, r.GetInt32(1), r.GetInt32(5), !r.IsDBNull(6) && r.GetBoolean(6), r.GetBoolean(3)), r.GetBoolean(3)));
            }
            foreach (var (parada, lista) in porParada)
            {
                var ordenadas = lista.OrderBy(x => x.Captura.CapturadaEn).ToList();
                var primera = ordenadas[0].Captura;
                atencionSanitarios[parada] = new AtencionSanitarios(ordenadas.Sum(x => x.Captura.BanosSuccionados), primera.CapturadaEn, ordenadas.All(x => x.Tirada), primera.FotoId,
                    ordenadas.Sum(x => x.Captura.BanosCubiertos), ordenadas.Select(x => x.Captura).ToList());
            }
        }
        // Fosa propia: el operador la atiende con una captura de la parada (foto con ubicación) por orden, no con firmas por orden. Las órdenes de la
        // parada se reparten en el orden de las capturas: la n-ésima orden propia (contando los clientes en su orden) toma la n-ésima captura.
        List<ClienteDeOrden> ConFirmasDe(Guid ruta, Guid parada, IReadOnlyList<ClienteDeOrden> clientes)
        {
            var fosas = (atencionSanitarios.GetValueOrDefault(parada)?.Capturas ?? []).Where(x => x.EsFosa).OrderBy(x => x.CapturadaEn).ToList();
            var antes = 0; var lista = new List<ClienteDeOrden>();
            foreach (var c in clientes) { lista.Add(ConFirmas(ruta, parada, c, antes, fosas)); if (c.FosaPropia) antes += Math.Max(c.Fosas, 0); }
            return lista;
        }
        ClienteDeOrden ConFirmas(Guid ruta, Guid parada, ClienteDeOrden c, int antes, IReadOnlyList<CapturaSanitarios> fosas)
        {
            if (fecha is null || rango || c.Fosas <= 0) return c;
            var jornada = jornadaDeRuta.GetValueOrDefault(ruta);
            return c with
            {
                Ordenes = Enumerable.Range(1, c.Fosas).Select(n =>
                {
                    if (c.FosaPropia && antes + n - 1 < fosas.Count)
                    {
                        var k = fosas[antes + n - 1];
                        return new OrdenDeCliente(null, n, true, true, k.Tirada, k.CapturadaEn, k.FotoId);   // atendida; Agua y Drenaje = ya se descargó en la planta
                    }
                    var orden = jornada == Guid.Empty ? Guid.Empty : IdOrden(jornada, parada, c.ClienteId, n);
                    var firmaPropia = !c.Autorizado || c.FosaPropia;   // el generador es nuestra empresa: no firma el cliente
                    return new OrdenDeCliente(orden == Guid.Empty ? null : orden, n, firmas.Contains((orden, "SERVICIO_OPERADOR")), firmaPropia || firmas.Contains((orden, "FIRMA_CLIENTE")), firmas.Contains((orden, "RECEPCION_PLANTA")), servicioEn.TryGetValue(orden, out var hora) ? hora : null, fotoDe.TryGetValue(orden, out var foto) ? foto : null,
                        firmaDe.GetValueOrDefault((orden, "SERVICIO_OPERADOR")), firmaDe.GetValueOrDefault((orden, "FIRMA_CLIENTE")), firmaDe.GetValueOrDefault((orden, "RECEPCION_PLANTA")));
                }).ToList(),
            };
        }

        var rutas = new List<RutaDeOrdenes>();
        await using (var cmd = tx.Comando("""
            SELECT ru.id, ru.sucursal_id, rv.nombre, rv.dias, veh.id, coalesce(fv.numero_economico, veh.numero_economico), cap.valor_m3, fv.clasificacion,
                   op.persona_id, coalesce(ofi.nombre_completo, pe.nombre_completo), cu.vehiculo_id IS NOT NULL
            FROM negocio.ruta ru
            JOIN LATERAL (SELECT x.nombre, x.dias, x.vehiculo_id FROM negocio.ruta_version x
                WHERE (x.organizacion_id, x.sucursal_id, x.ruta_id) = (ru.organizacion_id, ru.sucursal_id, ru.id)
                ORDER BY x.revision DESC LIMIT 1) rv ON true
            LEFT JOIN LATERAL negocio.cambio_unidad_en(ru.organizacion_id, ru.sucursal_id, ru.id, $2::date) cu ON true
            JOIN negocio.vehiculo veh ON (veh.organizacion_id, veh.sucursal_id, veh.id) = (ru.organizacion_id, ru.sucursal_id, coalesce(cu.vehiculo_id, rv.vehiculo_id))
            LEFT JOIN LATERAL (SELECT x.* FROM negocio.ficha_vehiculo x
                WHERE (x.organizacion_id, x.sucursal_id, x.vehiculo_id) = (veh.organizacion_id, veh.sucursal_id, veh.id)
                ORDER BY x.revision DESC LIMIT 1) fv ON true
            LEFT JOIN catalogo.capacidad_vehiculo cap ON cap.id = fv.capacidad_id
            LEFT JOIN LATERAL (SELECT coalesce(cu.persona_id, (SELECT a.persona_id FROM negocio.asignacion_unidad a
                WHERE (a.organizacion_id, a.sucursal_id, a.vehiculo_id) = (veh.organizacion_id, veh.sucursal_id, veh.id)
                ORDER BY a.revision DESC LIMIT 1)) AS persona_id) op ON true
            LEFT JOIN seguridad.persona pe ON (pe.organizacion_id, pe.id) = (ru.organizacion_id, op.persona_id)
            LEFT JOIN LATERAL (SELECT x.nombre_completo FROM negocio.operador_ficha x
                WHERE (x.organizacion_id, x.sucursal_id, x.persona_id) = (ru.organizacion_id, ru.sucursal_id, op.persona_id)
                ORDER BY x.revision DESC LIMIT 1) ofi ON true
            WHERE ru.organizacion_id = $1
            ORDER BY rv.nombre, ru.id
            """, organizacion, (object?)(rango ? null : fecha) ?? DBNull.Value))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct))
                rutas.Add(new RutaDeOrdenes(
                    r.GetGuid(0), r.GetGuid(1), r.GetString(2), DiasSemana.Lista(r.GetInt16(3)),
                    new UnidadDeOrden(r.GetGuid(4), T(r, 5), r.IsDBNull(6) ? null : r.GetDecimal(6), T(r, 7)),
                    r.IsDBNull(8) ? null : new OperadorResumen(r.GetGuid(8), T(r, 9) ?? "Operador sin nombre"),
                    (paradas.GetValueOrDefault(r.GetGuid(0)) ?? []).Select(p => p with { Clientes = ConFirmasDe(r.GetGuid(0), p.Id, p.Clientes), Atencion = atencionSanitarios.GetValueOrDefault(p.Id), Terminada = paradasTerminadas.ContainsKey(p.Id), NoNecesarias = paradasTerminadas.GetValueOrDefault(p.Id), Reportada = paradasReportadas.Contains(p.Id), OrdenesNoNecesarias = ordenesNoNecesarias.GetValueOrDefault(p.Id) }).ToList(),
                    (extras.GetValueOrDefault(r.GetGuid(0)) ?? []).Select(p => p with { Clientes = ConFirmasDe(r.GetGuid(0), p.Id, p.Clientes), Atencion = atencionSanitarios.GetValueOrDefault(p.Id), Terminada = paradasTerminadas.ContainsKey(p.Id), NoNecesarias = paradasTerminadas.GetValueOrDefault(p.Id), Reportada = paradasReportadas.Contains(p.Id), OrdenesNoNecesarias = ordenesNoNecesarias.GetValueOrDefault(p.Id) }).ToList(),
                    r.GetBoolean(10)));
        // «No se hará hoy» vigente del día pedido (ruta y paradas); en rangos no aplica.
        if (fecha is { } diaNsh && !rango)
        {
            var marcadas = new Dictionary<(Guid Ruta, Guid Objetivo), bool>();
            await using (var cmd = tx.Comando("""
                SELECT DISTINCT ON (n.ruta_id, n.objetivo) n.ruta_id, n.objetivo, n.activo FROM negocio.no_se_hara_dia n
                WHERE n.organizacion_id = $1 AND n.fecha = $2::date ORDER BY n.ruta_id, n.objetivo, n.revision DESC
                """, organizacion, diaNsh))
            await using (var r = await cmd.ExecuteReaderAsync(ct))
                while (await r.ReadAsync(ct)) marcadas[(r.GetGuid(0), r.GetGuid(1))] = r.GetBoolean(2);
            bool Marcada(Guid ruta, Guid objetivo) => marcadas.TryGetValue((ruta, objetivo), out var v) && v;
            for (var i = 0; i < rutas.Count; i++)
            {
                var ru = rutas[i];
                rutas[i] = ru with
                {
                    NoSeHara = Marcada(ru.Id, ru.Id),
                    Paradas = ru.Paradas.Select(p => p with { NoSeHara = Marcada(ru.Id, p.Id) }).ToList(),
                    Extraordinarias = ru.Extraordinarias?.Select(p => p with { NoSeHara = Marcada(ru.Id, p.Id) }).ToList(),
                };
            }
        }
        // Nuestra empresa (nombre fiscal; si falta, el comercial): es el generador de los clientes generales en los manifiestos.
        var empresas = new Dictionary<Guid, string>();
        await using (var cmd = tx.Comando("""
            SELECT s.id, coalesce(nullif(btrim(fe.nombre_fiscal), ''), nullif(btrim(fe.nombre_comercial), ''))
            FROM negocio.sucursal s
            JOIN LATERAL (SELECT x.nombre_fiscal, x.nombre_comercial FROM negocio.ficha_empresa x
                WHERE (x.organizacion_id, x.empresa_id) = (s.organizacion_id, s.empresa_id) ORDER BY x.revision DESC LIMIT 1) fe ON true
            WHERE s.organizacion_id = $1
            """, organizacion))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct)) if (!r.IsDBNull(1)) empresas[r.GetGuid(0)] = r.GetString(1);
        for (var i = 0; i < rutas.Count; i++) rutas[i] = rutas[i] with { Empresa = empresas.GetValueOrDefault(rutas[i].SucursalId) };
        await tx.ConfirmarAsync(ct);
        return rutas;
    }

    /// <summary>Id de la orden derivada: md5(jornada:parada:cliente:vuelta) como uuid, igual que negocio.resolver_orden_derivada.</summary>
    internal static Guid IdOrden(Guid jornada, Guid parada, Guid cliente, int vuelta) =>
        Guid.Parse(Convert.ToHexString(System.Security.Cryptography.MD5.HashData(System.Text.Encoding.UTF8.GetBytes($"{jornada}:{parada}:{cliente}:{vuelta}"))));

    /// <summary>Cambia la unidad y/o el operador de la ruta SOLO para esa fecha (historial; el plan y las asignaciones no se tocan).</summary>
    public async Task<ResultadoEscritura> CambiarUnidadAsync(Guid organizacion, Guid acceso, Guid ruta, DateOnly fecha, Guid vehiculo, Guid? persona, CancellationToken ct)
    {
        await using var tx = await TransaccionContexto.AbrirAsync(datos, organizacion, acceso, ct);
        Guid sucursal; short dias;
        await using (var cmd = tx.Comando("""
            SELECT ru.sucursal_id, rv.dias FROM negocio.ruta ru
            JOIN LATERAL (SELECT x.dias FROM negocio.ruta_version x
                WHERE (x.organizacion_id, x.sucursal_id, x.ruta_id) = (ru.organizacion_id, ru.sucursal_id, ru.id)
                ORDER BY x.revision DESC LIMIT 1) rv ON true
            WHERE ru.organizacion_id = $1 AND ru.id = $2
            """, organizacion, ruta))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
        {
            if (!await r.ReadAsync(ct)) return ResultadoEscritura.NoEncontrada;
            (sucursal, dias) = (r.GetGuid(0), r.GetInt16(1));
        }
        await using (var cmd = tx.Comando("SELECT coalesce(seguridad.puede($1, $2, 'datos.escribir'), false)", organizacion, sucursal))
            if (!(bool)(await cmd.ExecuteScalarAsync(ct))!) return ResultadoEscritura.SinPermiso;
        if ((dias & (1 << (((int)fecha.DayOfWeek + 6) % 7))) == 0) return ResultadoEscritura.Invalida(EndpointsOrdenes.MensajeDia);
        await using (var cmd = tx.Comando("SELECT EXISTS (SELECT 1 FROM negocio.vehiculo WHERE organizacion_id = $1 AND sucursal_id = $2 AND id = $3)", organizacion, sucursal, vehiculo))
            if (!(bool)(await cmd.ExecuteScalarAsync(ct))!) return ResultadoEscritura.Invalida("No se encontró la unidad.");
        if (persona is { } p)
            await using (var cmd = tx.Comando("SELECT EXISTS (SELECT 1 FROM negocio.operador_ficha WHERE organizacion_id = $1 AND sucursal_id = $2 AND persona_id = $3)", organizacion, sucursal, p))
                if (!(bool)(await cmd.ExecuteScalarAsync(ct))!) return ResultadoEscritura.Invalida("No se encontró el operador.");
        await using (var cmd = tx.Comando("SELECT pg_advisory_xact_lock(hashtextextended($1, 0))", $"cambio:{organizacion}:{ruta}:{fecha:yyyy-MM-dd}"))
            await cmd.ExecuteNonQueryAsync(ct);
        int revision;
        await using (var cmd = tx.Comando("SELECT coalesce(max(revision), 0) + 1 FROM negocio.cambio_unidad_dia WHERE (organizacion_id, sucursal_id, ruta_id, fecha) = ($1, $2, $3, $4::date)", organizacion, sucursal, ruta, fecha))
            revision = Convert.ToInt32(await cmd.ExecuteScalarAsync(ct));
        await using (var cmd = tx.Comando("INSERT INTO negocio.cambio_unidad_dia (organizacion_id, sucursal_id, ruta_id, fecha, revision, vehiculo_id, persona_id) VALUES ($1, $2, $3, $4::date, $5, $6, $7::uuid)",
            organizacion, sucursal, ruta, fecha, revision, vehiculo, (object?)persona ?? DBNull.Value))
            await cmd.ExecuteNonQueryAsync(ct);
        await tx.ConfirmarAsync(ct);
        return new ResultadoEscritura(EstadoEscritura.Correcta, ruta, revision, null);
    }

    /// <summary>Marca (o deshace) «no se hará hoy» para una ruta o una de sus paradas, solo ese día (historial; nunca borra ni reagenda).</summary>
    public async Task<ResultadoEscritura> MarcarNoSeHaraAsync(Guid organizacion, Guid acceso, Guid ruta, DateOnly fecha, Guid? parada, bool activo, CancellationToken ct)
    {
        await using var tx = await TransaccionContexto.AbrirAsync(datos, organizacion, acceso, ct);
        Guid sucursal; short dias;
        await using (var cmd = tx.Comando("""
            SELECT ru.sucursal_id, rv.dias FROM negocio.ruta ru
            JOIN LATERAL (SELECT x.dias FROM negocio.ruta_version x
                WHERE (x.organizacion_id, x.sucursal_id, x.ruta_id) = (ru.organizacion_id, ru.sucursal_id, ru.id)
                ORDER BY x.revision DESC LIMIT 1) rv ON true
            WHERE ru.organizacion_id = $1 AND ru.id = $2
            """, organizacion, ruta))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
        {
            if (!await r.ReadAsync(ct)) return ResultadoEscritura.NoEncontrada;
            (sucursal, dias) = (r.GetGuid(0), r.GetInt16(1));
        }
        await using (var cmd = tx.Comando("SELECT coalesce(seguridad.puede($1, $2, 'datos.escribir'), false)", organizacion, sucursal))
            if (!(bool)(await cmd.ExecuteScalarAsync(ct))!) return ResultadoEscritura.SinPermiso;
        if ((dias & (1 << (((int)fecha.DayOfWeek + 6) % 7))) == 0) return ResultadoEscritura.Invalida(EndpointsOrdenes.MensajeDia);
        if (parada is { } p)
            await using (var cmd = tx.Comando("""
                SELECT EXISTS (SELECT 1 FROM negocio.parada WHERE (organizacion_id, sucursal_id, ruta_id, id) = ($1, $2, $3, $4))
                    OR EXISTS (SELECT 1 FROM negocio.parada_extraordinaria WHERE (organizacion_id, sucursal_id, ruta_id, id) = ($1, $2, $3, $4) AND fecha = $5::date)
                """, organizacion, sucursal, ruta, p, fecha))
                if (!(bool)(await cmd.ExecuteScalarAsync(ct))!) return ResultadoEscritura.Invalida("No se encontró la parada.");
        var objetivo = parada ?? ruta;
        await using (var cmd = tx.Comando("SELECT pg_advisory_xact_lock(hashtextextended($1, 0))", $"nsh:{organizacion}:{ruta}:{fecha:yyyy-MM-dd}"))
            await cmd.ExecuteNonQueryAsync(ct);
        int revision;
        await using (var cmd = tx.Comando("SELECT coalesce(max(revision), 0) + 1 FROM negocio.no_se_hara_dia WHERE (organizacion_id, sucursal_id, ruta_id, objetivo, fecha) = ($1, $2, $3, $4, $5::date)", organizacion, sucursal, ruta, objetivo, fecha))
            revision = Convert.ToInt32(await cmd.ExecuteScalarAsync(ct));
        await using (var cmd = tx.Comando("INSERT INTO negocio.no_se_hara_dia (organizacion_id, sucursal_id, ruta_id, objetivo, fecha, revision, es_ruta, activo) VALUES ($1, $2, $3, $4, $5::date, $6, $7, $8)",
            organizacion, sucursal, ruta, objetivo, fecha, revision, parada is null, activo))
            await cmd.ExecuteNonQueryAsync(ct);
        await tx.ConfirmarAsync(ct);
        return new ResultadoEscritura(EstadoEscritura.Correcta, objetivo, revision, null);
    }

    /// <summary>Cambia los baños de un cliente en una parada del plan SOLO para esa fecha (historial; el plan fijo no se toca).</summary>
    public Task<ResultadoEscritura> AjustarBanosAsync(Guid organizacion, Guid acceso, Guid ruta, DateOnly fecha, Guid parada, Guid cliente, int banos, CancellationToken ct) =>
        AjustarAsync(organizacion, acceso, ruta, fecha, parada, cliente, banos, false, ct);

    /// <summary>Cambia las órdenes (fosas) de un cliente en una parada del plan SOLO para esa fecha (historial; el plan fijo no se toca).</summary>
    public Task<ResultadoEscritura> AjustarFosasAsync(Guid organizacion, Guid acceso, Guid ruta, DateOnly fecha, Guid parada, Guid cliente, int fosas, CancellationToken ct) =>
        AjustarAsync(organizacion, acceso, ruta, fecha, parada, cliente, fosas, true, ct);

    private async Task<ResultadoEscritura> AjustarAsync(Guid organizacion, Guid acceso, Guid ruta, DateOnly fecha, Guid parada, Guid cliente, int cantidad, bool fosas, CancellationToken ct)
    {
        await using var tx = await TransaccionContexto.AbrirAsync(datos, organizacion, acceso, ct);
        Guid sucursal; short dias;
        await using (var cmd = tx.Comando("""
            SELECT ru.sucursal_id, rv.dias FROM negocio.ruta ru
            JOIN LATERAL (SELECT x.dias FROM negocio.ruta_version x
                WHERE (x.organizacion_id, x.sucursal_id, x.ruta_id) = (ru.organizacion_id, ru.sucursal_id, ru.id)
                ORDER BY x.revision DESC LIMIT 1) rv ON true
            WHERE ru.organizacion_id = $1 AND ru.id = $2
            """, organizacion, ruta))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
        {
            if (!await r.ReadAsync(ct)) return ResultadoEscritura.NoEncontrada;
            (sucursal, dias) = (r.GetGuid(0), r.GetInt16(1));
        }
        await using (var cmd = tx.Comando("SELECT coalesce(seguridad.puede($1, $2, 'datos.escribir'), false)", organizacion, sucursal))
            if (!(bool)(await cmd.ExecuteScalarAsync(ct))!) return ResultadoEscritura.SinPermiso;
        if ((dias & (1 << (((int)fecha.DayOfWeek + 6) % 7))) == 0) return ResultadoEscritura.Invalida(EndpointsOrdenes.MensajeDia);
        // El cliente tiene baños u órdenes (según el ajuste) planeados en esa parada de la ruta (vigente, sin baja).
        await using (var cmd = tx.Comando($"""
            SELECT EXISTS (SELECT 1 FROM negocio.parada pa
                JOIN LATERAL (SELECT x.id FROM negocio.parada_version x
                    WHERE (x.organizacion_id, x.sucursal_id, x.parada_id) = (pa.organizacion_id, pa.sucursal_id, pa.id) ORDER BY x.revision DESC LIMIT 1) pv ON true
                JOIN negocio.parada_cliente pc ON (pc.organizacion_id, pc.sucursal_id, pc.version_id) = (pa.organizacion_id, pa.sucursal_id, pv.id)
                WHERE (pa.organizacion_id, pa.sucursal_id, pa.id, pa.ruta_id) = ($1, $2, $3, $4) AND pc.cliente_id = $5 AND {(fosas ? "coalesce(pc.fosas_planeadas, 0)" : "coalesce(pc.banos_planeados, 0)")} > 0
                  AND NOT EXISTS (SELECT 1 FROM negocio.parada_baja pb WHERE (pb.organizacion_id, pb.sucursal_id, pb.parada_id) = (pa.organizacion_id, pa.sucursal_id, pa.id)))
            """, organizacion, sucursal, parada, ruta, cliente))
            if (!(bool)(await cmd.ExecuteScalarAsync(ct))!) return ResultadoEscritura.Invalida(fosas ? EndpointsOrdenes.MensajeAjusteFosas : EndpointsOrdenes.MensajeAjusteBanos);
        // R2-03: las órdenes ya atendidas (con captura) no pueden quedar fuera: no se baja de la última vuelta capturada ese día.
        if (fosas)
        {
            Guid? jornada = null;
            await using (var cmd = tx.Comando("SELECT id FROM negocio.jornada WHERE (organizacion_id, sucursal_id, ruta_id, fecha) = ($1, $2, $3, $4::date)", organizacion, sucursal, ruta, fecha))
                jornada = await cmd.ExecuteScalarAsync(ct) as Guid?;
            if (jornada is { } j)
            {
                int ultima;
                await using (var cmd = tx.Comando("""
                    SELECT coalesce(max(g), 0) FROM generate_series(1, 500) g
                    WHERE EXISTS (SELECT 1 FROM negocio.captura_trabajo_efectiva ct WHERE ct.organizacion_id = $1 AND ct.sucursal_id = $2
                                  AND ct.orden_derivada_id = md5($3::text || ':' || $4::text || ':' || $5::text || ':' || g::text)::uuid)
                    """, organizacion, sucursal, j, parada, cliente))
                    ultima = Convert.ToInt32(await cmd.ExecuteScalarAsync(ct));
                if (cantidad < ultima) return ResultadoEscritura.Invalida($"Ya hay órdenes atendidas hasta la número {ultima}: no se puede bajar de {ultima}.");
            }
        }
        // R2-08: dos cambios simultáneos de lo mismo se turnan (si no, leerían la misma revisión y uno chocaría con la clave).
        await using (var cmd = tx.Comando("SELECT pg_advisory_xact_lock(hashtextextended($1, 0))", $"ajuste:{(fosas ? "f" : "b")}:{organizacion}:{parada}:{cliente}:{fecha:yyyy-MM-dd}"))
            await cmd.ExecuteNonQueryAsync(ct);
        int revision;
        var tabla = fosas ? "ajuste_fosas_dia" : "ajuste_banos_dia";
        // Sin cambio real (la cantidad vigente de ese día ya es esa): no se guarda otra revisión ni se avisa al operador (Revision = 0).
        int vigente;
        await using (var cmd = tx.Comando($"""
            SELECT coalesce(
                (SELECT {(fosas ? "fosas" : "banos")} FROM negocio.{tabla} WHERE (organizacion_id, sucursal_id, parada_id, cliente_id, fecha) = ($1, $2, $3, $5, $6::date) ORDER BY revision DESC LIMIT 1),
                (SELECT coalesce(pc.{(fosas ? "fosas_planeadas" : "banos_planeados")}, 0) FROM negocio.parada pa
                    JOIN LATERAL (SELECT x.id FROM negocio.parada_version x
                        WHERE (x.organizacion_id, x.sucursal_id, x.parada_id) = (pa.organizacion_id, pa.sucursal_id, pa.id) ORDER BY x.revision DESC LIMIT 1) pv ON true
                    JOIN negocio.parada_cliente pc ON (pc.organizacion_id, pc.sucursal_id, pc.version_id) = (pa.organizacion_id, pa.sucursal_id, pv.id)
                    WHERE (pa.organizacion_id, pa.sucursal_id, pa.id, pa.ruta_id) = ($1, $2, $3, $4) AND pc.cliente_id = $5 LIMIT 1), 0)
            """, organizacion, sucursal, parada, ruta, cliente, fecha))
            vigente = Convert.ToInt32(await cmd.ExecuteScalarAsync(ct));
        if (vigente == cantidad) return new ResultadoEscritura(EstadoEscritura.Correcta, parada, 0, null);
        await using (var cmd = tx.Comando($"SELECT coalesce(max(revision), 0) + 1 FROM negocio.{tabla} WHERE (organizacion_id, sucursal_id, parada_id, cliente_id, fecha) = ($1, $2, $3, $4, $5::date)", organizacion, sucursal, parada, cliente, fecha))
            revision = Convert.ToInt32(await cmd.ExecuteScalarAsync(ct));
        await using (var cmd = tx.Comando($"INSERT INTO negocio.{tabla} (organizacion_id, sucursal_id, parada_id, cliente_id, fecha, revision, {(fosas ? "fosas" : "banos")}) VALUES ($1, $2, $3, $4, $5::date, $6, $7)", organizacion, sucursal, parada, cliente, fecha, revision, cantidad))
            await cmd.ExecuteNonQueryAsync(ct);
        await tx.ConfirmarAsync(ct);
        return new ResultadoEscritura(EstadoEscritura.Correcta, parada, revision, null);
    }

    public async Task<ResultadoEscritura> AgregarExtraordinariaAsync(Guid organizacion, Guid acceso, Guid ruta, DateOnly fecha, Guid sitio, IReadOnlyList<DatosClienteParada> clientes, Guid? claveSolicitud, CancellationToken ct)
    {
        await using var tx = await TransaccionContexto.AbrirAsync(datos, organizacion, acceso, ct);
        Guid sucursal; short dias;
        await using (var cmd = tx.Comando("""
            SELECT ru.sucursal_id, rv.dias FROM negocio.ruta ru
            JOIN LATERAL (SELECT x.dias FROM negocio.ruta_version x
                WHERE (x.organizacion_id, x.sucursal_id, x.ruta_id) = (ru.organizacion_id, ru.sucursal_id, ru.id)
                ORDER BY x.revision DESC LIMIT 1) rv ON true
            WHERE ru.organizacion_id = $1 AND ru.id = $2
            """, organizacion, ruta))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
        {
            if (!await r.ReadAsync(ct)) return ResultadoEscritura.NoEncontrada;
            (sucursal, dias) = (r.GetGuid(0), r.GetInt16(1));
        }
        await using (var cmd = tx.Comando("SELECT coalesce(seguridad.puede($1, $2, 'datos.escribir'), false)", organizacion, sucursal))
            if (!(bool)(await cmd.ExecuteScalarAsync(ct))!) return ResultadoEscritura.SinPermiso;
        // La parada extraordinaria va en una salida que existe: la ruta sale ese día de la semana.
        if ((dias & (1 << (((int)fecha.DayOfWeek + 6) % 7))) == 0) return ResultadoEscritura.Invalida(EndpointsOrdenes.MensajeDia);
        // Sanitarios solo lleva baños y fosas solo lleva fosas (cada cliente, una de las dos cantidades).
        // El servicio lo define la unidad que la ruta tiene ESE día: si Operaciones le cambió la unidad del día, vale la del cambio, no la del plan.
        bool esSanitarios;
        await using (var cmd = tx.Comando("""
            SELECT EXISTS (SELECT 1 FROM negocio.ruta_version rv
                LEFT JOIN LATERAL negocio.cambio_unidad_en(rv.organizacion_id, rv.sucursal_id, rv.ruta_id, $4::date) cu ON true
                JOIN LATERAL (SELECT x.clasificacion FROM negocio.ficha_vehiculo x
                    WHERE (x.organizacion_id, x.sucursal_id, x.vehiculo_id) = (rv.organizacion_id, rv.sucursal_id, coalesce(cu.vehiculo_id, rv.vehiculo_id))
                    ORDER BY x.revision DESC LIMIT 1) f ON f.clasificacion = 'sanitarios'
                WHERE (rv.organizacion_id, rv.sucursal_id, rv.ruta_id) = ($1, $2, $3)
                  AND rv.revision = (SELECT max(y.revision) FROM negocio.ruta_version y WHERE (y.organizacion_id, y.sucursal_id, y.ruta_id) = ($1, $2, $3)))
            """, organizacion, sucursal, ruta, fecha))
            esSanitarios = (bool)(await cmd.ExecuteScalarAsync(ct))!;
        // Decisión 0031: una unidad de sanitarios (1 m³) no hace fosas; una unidad de fosas SÍ puede hacer baños si Operaciones lo decide (parada por parada).
        if (esSanitarios && clientes.Any(c => c.Fosas is > 0)) return ResultadoEscritura.Invalida(EndpointsOrdenes.MensajeSoloBanos);
        await using (var cmd = tx.Comando("SELECT EXISTS (SELECT 1 FROM negocio.sitio WHERE organizacion_id = $1 AND sucursal_id = $2 AND id = $3)", organizacion, sucursal, sitio))
            if (!(bool)(await cmd.ExecuteScalarAsync(ct))!) return ResultadoEscritura.Invalida("No se encontró el sitio.");
        // Reintento de la misma solicitud: se devuelve la parada ya creada.
        if (claveSolicitud is { } clave)
        {
            await using var existente = tx.Comando("SELECT id FROM negocio.parada_extraordinaria WHERE organizacion_id = $1 AND sucursal_id = $2 AND clave_solicitud = $3", organizacion, sucursal, clave);
            if (await existente.ExecuteScalarAsync(ct) is Guid previa) return ResultadoEscritura.Correcta(previa, 1);
        }
        // PG-37: si el sitio YA está en la ruta de ese día (parada del plan vigente o una extraordinaria anterior del día) no se crea una parada
        // repetida para el operador: la orden/servicio extraordinario se FUSIONA con esa parada. Un sitio que solo estuvo (parada dada de baja) sí
        // es una parada extraordinaria nueva.
        Guid? fusionadaEn = null;
        await using (var cmd = tx.Comando("""
            SELECT coalesce(
                (SELECT pa.id FROM negocio.parada pa
                    JOIN LATERAL (SELECT y.sitio_id FROM negocio.parada_version y
                        WHERE (y.organizacion_id, y.sucursal_id, y.parada_id) = (pa.organizacion_id, pa.sucursal_id, pa.id)
                        ORDER BY y.revision DESC LIMIT 1) pv ON true
                    WHERE pa.organizacion_id = $1 AND pa.sucursal_id = $2 AND pa.ruta_id = $3 AND pv.sitio_id = $4
                      AND NOT EXISTS (SELECT 1 FROM negocio.parada_baja pb WHERE (pb.organizacion_id, pb.sucursal_id, pb.parada_id) = (pa.organizacion_id, pa.sucursal_id, pa.id))
                    ORDER BY pa.posicion LIMIT 1),
                (SELECT x.id FROM negocio.parada_extraordinaria x
                    WHERE x.organizacion_id = $1 AND x.sucursal_id = $2 AND x.ruta_id = $3 AND x.sitio_id = $4 AND x.fecha = $5::date AND x.fusionada_en IS NULL
                    ORDER BY x.creada_en LIMIT 1))
            """, organizacion, sucursal, ruta, sitio, fecha))
            fusionadaEn = await cmd.ExecuteScalarAsync(ct) as Guid?;
        var autorizados = await RepositorioRutas.ClientesAutorizadosAsync(tx, organizacion, sucursal, clientes.Select(c => c.ClienteId), ct);
        // Un cliente autorizado (convenio) no se mezcla con baños: ni en sanitarios ni en una parada de baños de una unidad de fosas.
        if ((esSanitarios || clientes.Any(c => c.Banos is > 0)) && autorizados.Count > 0) return ResultadoEscritura.Invalida(EndpointsOrdenes.MensajeAutorizadoEnSanitarios);
        if (fusionadaEn is null)
        {
            // Parada nueva: autorizado va en su dirección activa o un sitio normal; general solo en un sitio normal. En fosas un autorizado va solo.
            if (!await ReglasSitio.ClientesValidosAsync(tx, organizacion, sucursal, sitio, clientes.Select(c => c.ClienteId).ToList(), ct))
                return ResultadoEscritura.Invalida(EndpointsOrdenes.MensajeSitio);
            if (!esSanitarios && clientes.Count > 1 && autorizados.Count > 0) return ResultadoEscritura.Invalida(EndpointsOrdenes.MensajeAutorizadoSolo);
        }
        // Fusionada: la parada ya existe en ese sitio, así que se acepta aunque sea de otro cliente (incluso en la parada de un autorizado:
        // Operaciones ya habrá avisado al operador); el panel y la app la muestran separada por cliente y marcada como extraordinaria.
        string? aviso = fusionadaEn is null ? null : EndpointsOrdenes.AvisoFusionada;
        var id = Guid.NewGuid();
        try
        {
            await using var cmd = tx.Comando("INSERT INTO negocio.parada_extraordinaria (organizacion_id, sucursal_id, id, ruta_id, fecha, sitio_id, clave_solicitud, fusionada_en) VALUES ($1, $2, $3, $4, $5::date, $6, $7::uuid, $8::uuid)",
                organizacion, sucursal, id, ruta, fecha, sitio, (object?)claveSolicitud ?? DBNull.Value, (object?)fusionadaEn ?? DBNull.Value);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.UniqueViolation && claveSolicitud is not null)
        {
            // Dos envíos simultáneos con la misma clave: gana el primero; este devuelve su parada.
            await using var tx2 = await TransaccionContexto.AbrirAsync(datos, organizacion, acceso, ct);
            await using var buscar = tx2.Comando("SELECT id FROM negocio.parada_extraordinaria WHERE organizacion_id = $1 AND sucursal_id = $2 AND clave_solicitud = $3", organizacion, sucursal, claveSolicitud);
            return await buscar.ExecuteScalarAsync(ct) is Guid ganadora ? ResultadoEscritura.Correcta(ganadora, 1) : ResultadoEscritura.Invalida("No se pudo agregar la parada. Reintenta.");
        }
        foreach (var c in clientes)
            await using (var cmd = tx.Comando("INSERT INTO negocio.parada_extraordinaria_cliente (organizacion_id, sucursal_id, parada_extraordinaria_id, cliente_id, banos_planeados, fosas_planeadas) VALUES ($1, $2, $3, $4, $5::int, $6::int)",
                organizacion, sucursal, id, c.ClienteId, (object?)c.Banos ?? DBNull.Value, (object?)c.Fosas ?? DBNull.Value))
                await cmd.ExecuteNonQueryAsync(ct);
        await tx.ConfirmarAsync(ct);
        return new ResultadoEscritura(EstadoEscritura.Correcta, id, 1, aviso);
    }
}

public static class EndpointsOrdenes
{
    public const string MensajeAjusteFosas = "Solo se pueden cambiar las órdenes de un cliente de fosas en una parada de la ruta, para hoy o un día próximo.";
    public const string MensajeAjusteBanos = "Solo se pueden cambiar los baños de un cliente de sanitarios en una parada de la ruta, para hoy o un día próximo.";
    public const string MensajeSinPermiso = "No tienes permiso para agregar paradas en esta sucursal.";
    public const string MensajeDia = "La ruta no sale ese día de la semana.";
    public const string MensajeSoloBanos = "Las rutas de sanitarios solo llevan baños.";
    public const string MensajeAutorizadoEnSanitarios = "Solo los clientes generales (sin autorización de generador) van en rutas de sanitarios.";
    public const string AvisoFusionada = "Este sitio ya está en la ruta de hoy: la orden se agregó a su parada como extraordinaria.";
    public const string MensajeAutorizadoSolo = "Una parada de un cliente autorizado lleva solo a ese cliente.";
    public const string MensajeSoloFosas = "Las rutas de fosas solo llevan fosas.";
    public const string MensajeSitio = "El sitio no es válido para ese cliente: un autorizado va en su dirección activa o en un sitio normal; un general, solo en un sitio normal.";

    public static IServiceCollection AgregarOrdenes(this IServiceCollection services) =>
        services.AddSingleton<IRepositorioOrdenes, RepositorioOrdenes>();

    public static void MapearOrdenes(this IEndpointRouteBuilder app)
    {
        // ?fecha=AAAA-MM-DD añade las paradas extraordinarias de ese día.
        // ?desde=AAAA-MM-DD&hasta=AAAA-MM-DD (hasta 62 días) añade las extraordinarias de ese rango, cada una con su fecha (reportes).
        app.MapGet("/api/ordenes", async (string? fecha, string? desde, string? hasta, HttpContext http, IRepositorioOrdenes ordenes) =>
        {
            if (!ServiciosAcceso.LeerIds(http.User, out var org, out var acceso)) return Results.Unauthorized();
            DateOnly? dia = null, fin = null;
            if (fecha is not null)
            {
                if (!DateOnly.TryParseExact(fecha, "yyyy-MM-dd", out var d)) return Results.BadRequest(new MensajeError("Indica la fecha como AAAA-MM-DD."));
                dia = d;
            }
            else if (desde is not null || hasta is not null)
            {
                if (!DateOnly.TryParseExact(desde, "yyyy-MM-dd", out var d) || !DateOnly.TryParseExact(hasta, "yyyy-MM-dd", out var h))
                    return Results.BadRequest(new MensajeError("Indica el rango como desde=AAAA-MM-DD&hasta=AAAA-MM-DD."));
                if (h < d || h.DayNumber - d.DayNumber > 62) return Results.BadRequest(new MensajeError("El rango va de uno a 62 días."));
                (dia, fin) = (d, h);
            }
            http.Response.Headers.CacheControl = "no-store";
            return Results.Ok(await ordenes.ListarAsync(org, acceso, dia, fin, http.RequestAborted));
        });

        // Parada extraordinaria: solo de ese día, no se repite en la ruta fija. El aviso al operador queda pendiente (por_avisar).
        app.MapPost("/api/ordenes/extraordinarias", async (SolicitudExtraordinaria s, HttpContext http, IRepositorioOrdenes ordenes, Avisos.AvisosTrabajo avisos) =>
        {
            if (!ServiciosAcceso.LeerIds(http.User, out var org, out var acceso)) return Results.Unauthorized();
            if (s.RutaId is not { } ruta || ruta == Guid.Empty) return Results.BadRequest(new MensajeError("Elige la ruta."));
            if (!DateOnly.TryParseExact(s.Fecha, "yyyy-MM-dd", out var fecha)) return Results.BadRequest(new MensajeError("Indica la fecha como AAAA-MM-DD."));
            if (fecha < Avisos.AvisosTrabajo.HoyLocal()) return Results.BadRequest(new MensajeError("Las extraordinarias son de hoy o de un día próximo, no del pasado."));
            if (s.SitioId is not { } sitio || sitio == Guid.Empty) return Results.BadRequest(new MensajeError("Elige el sitio del cliente."));
            var clientes = s.Clientes ?? [];
            if (clientes.Length is 0 or > 50) return Results.BadRequest(new MensajeError("Agrega al menos un cliente."));
            var lista = new List<DatosClienteParada>();
            foreach (var c in clientes)
            {
                if (c?.ClienteId is not { } cliente || cliente == Guid.Empty) return Results.BadRequest(new MensajeError("Elige el cliente."));
                if (c.Banos is < 0 or > 10_000 || c.Fosas is < 0 or > 10_000 || (c.Banos ?? 0) + (c.Fosas ?? 0) == 0 || (c.Banos ?? 0) > 0 && (c.Fosas ?? 0) > 0)
                    return Results.BadRequest(new MensajeError("Indica baños o fosas (solo una de las dos), de 1 a 10000."));
                lista.Add(new DatosClienteParada(cliente, c.Banos is > 0 ? c.Banos : null, c.Fosas));
            }
            if (lista.Select(c => c.ClienteId).Distinct().Count() != lista.Count) return Results.BadRequest(new MensajeError("Un cliente aparece más de una vez."));
            var r = await ordenes.AgregarExtraordinariaAsync(org, acceso, ruta, fecha, sitio, lista, s.ClaveSolicitud, http.RequestAborted);
            IResult AvisarYCrear()
            {
                // PG-37: toda extraordinaria de HOY avisa al operador (push); fusionada = orden nueva en una parada que ya tenía.
                var texto = r.Mensaje is null ? "Se agregó una parada a tu ruta de hoy." : "Se agregó una orden a una parada de tu ruta de hoy.";
                _ = Task.Run(() => avisos.AvisarAsync(ruta, fecha, texto));
                return Results.Created($"/api/ordenes/extraordinarias/{r.Id}", new ExtraordinariaCreada(r.Id, r.Revision, r.Mensaje));
            }
            return r.Estado switch
            {
                EstadoEscritura.Correcta => AvisarYCrear(),
                EstadoEscritura.NoEncontrada => Results.Json(new MensajeError("No se encontró la ruta."), statusCode: StatusCodes.Status404NotFound),
                EstadoEscritura.SinPermiso => Results.Json(new MensajeError(MensajeSinPermiso), statusCode: StatusCodes.Status403Forbidden),
                _ => Results.BadRequest(new MensajeError(r.Mensaje!)),
            };
        }).AddEndpointFilter(EndpointsAcceso.ValidarCsrf);

        // Cambio de unidad y/o operador solo para ese día (se toma como extraordinario). Avisa al operador del día si es hoy.
        app.MapPut("/api/ordenes/cambios-unidad", async (SolicitudCambioUnidad s, HttpContext http, IRepositorioOrdenes ordenes, Avisos.AvisosTrabajo avisos) =>
        {
            if (!ServiciosAcceso.LeerIds(http.User, out var org, out var acceso)) return Results.Unauthorized();
            if (s.RutaId is not { } ruta || ruta == Guid.Empty || s.VehiculoId is not { } vehiculo || vehiculo == Guid.Empty) return Results.BadRequest(new MensajeError("Elige la ruta y la unidad."));
            if (!DateOnly.TryParseExact(s.Fecha, "yyyy-MM-dd", out var fecha)) return Results.BadRequest(new MensajeError("Indica la fecha como AAAA-MM-DD."));
            if (fecha < Avisos.AvisosTrabajo.HoyLocal()) return Results.BadRequest(new MensajeError("El cambio es para hoy o un día próximo."));
            var persona = s.PersonaId is { } p && p != Guid.Empty ? p : (Guid?)null;
            var r = await ordenes.CambiarUnidadAsync(org, acceso, ruta, fecha, vehiculo, persona, http.RequestAborted);
            return r.Estado switch
            {
                EstadoEscritura.Correcta => AvisarCambio(),
                EstadoEscritura.NoEncontrada => Results.Json(new MensajeError("No se encontró la ruta."), statusCode: StatusCodes.Status404NotFound),
                EstadoEscritura.SinPermiso => Results.Json(new MensajeError(MensajeSinPermiso), statusCode: StatusCodes.Status403Forbidden),
                _ => Results.BadRequest(new MensajeError(r.Mensaje!)),
            };
            IResult AvisarCambio()
            {
                _ = Task.Run(() => avisos.AvisarAsync(ruta, fecha, "Operaciones te asignó una ruta para hoy."));
                return Results.NoContent();
            }
        }).AddEndpointFilter(EndpointsAcceso.ValidarCsrf);

        // «No se hará hoy» (ruta o parada) y su deshacer. Avisa al operador del día con el texto genérico si es hoy.
        app.MapPut("/api/ordenes/no-se-hara", async (SolicitudNoSeHara s, HttpContext http, IRepositorioOrdenes ordenes, Avisos.AvisosTrabajo avisos) =>
        {
            if (!ServiciosAcceso.LeerIds(http.User, out var org, out var acceso)) return Results.Unauthorized();
            if (s.RutaId is not { } ruta || ruta == Guid.Empty) return Results.BadRequest(new MensajeError("Elige la ruta."));
            if (s.ParadaId is { } pp && pp == Guid.Empty) return Results.BadRequest(new MensajeError("Elige la parada."));
            if (s.NoSeHara is not { } activo) return Results.BadRequest(new MensajeError("Indica si no se hará o sí se hará."));
            if (!DateOnly.TryParseExact(s.Fecha, "yyyy-MM-dd", out var fecha)) return Results.BadRequest(new MensajeError("Indica la fecha como AAAA-MM-DD."));
            if (fecha < Avisos.AvisosTrabajo.HoyLocal()) return Results.BadRequest(new MensajeError("Solo se marca hoy o un día próximo."));
            var r = await ordenes.MarcarNoSeHaraAsync(org, acceso, ruta, fecha, s.ParadaId, activo, http.RequestAborted);
            return r.Estado switch
            {
                EstadoEscritura.Correcta => Avisar(),
                EstadoEscritura.NoEncontrada => Results.Json(new MensajeError("No se encontró la ruta."), statusCode: StatusCodes.Status404NotFound),
                EstadoEscritura.SinPermiso => Results.Json(new MensajeError(MensajeSinPermiso), statusCode: StatusCodes.Status403Forbidden),
                _ => Results.BadRequest(new MensajeError(r.Mensaje!)),
            };
            IResult Avisar()
            {
                _ = Task.Run(() => avisos.AvisarAsync(ruta, fecha, "Operaciones actualizó tu trabajo de hoy."));
                return Results.NoContent();
            }
        }).AddEndpointFilter(EndpointsAcceso.ValidarCsrf);

        // Cambio de baños solo para ese día (se toma como extraordinario). Avisa al operador si es hoy.
        app.MapPut("/api/ordenes/ajustes-banos", async (SolicitudAjusteBanos s, HttpContext http, IRepositorioOrdenes ordenes, Avisos.AvisosTrabajo avisos) =>
        {
            if (!ServiciosAcceso.LeerIds(http.User, out var org, out var acceso)) return Results.Unauthorized();
            if (s.RutaId is not { } ruta || ruta == Guid.Empty || s.ParadaId is not { } parada || parada == Guid.Empty || s.ClienteId is not { } cliente || cliente == Guid.Empty)
                return Results.BadRequest(new MensajeError("Elige la ruta, la parada y el cliente."));
            if (!DateOnly.TryParseExact(s.Fecha, "yyyy-MM-dd", out var fecha)) return Results.BadRequest(new MensajeError("Indica la fecha como AAAA-MM-DD."));
            if (fecha < Avisos.AvisosTrabajo.HoyLocal()) return Results.BadRequest(new MensajeError(MensajeAjusteBanos));
            if (s.Banos is not (>= 0 and <= 10_000)) return Results.BadRequest(new MensajeError("Indica los baños, de 0 a 10000."));
            var r = await ordenes.AjustarBanosAsync(org, acceso, ruta, fecha, parada, cliente, s.Banos.Value, http.RequestAborted);
            return r.Estado switch
            {
                EstadoEscritura.Correcta => AvisarAjuste(r.Revision != 0),
                EstadoEscritura.NoEncontrada => Results.Json(new MensajeError("No se encontró la ruta."), statusCode: StatusCodes.Status404NotFound),
                EstadoEscritura.SinPermiso => Results.Json(new MensajeError(MensajeSinPermiso), statusCode: StatusCodes.Status403Forbidden),
                _ => Results.BadRequest(new MensajeError(r.Mensaje!)),
            };
            IResult AvisarAjuste(bool huboCambio)
            {
                if (!huboCambio) return Results.NoContent();
                _ = Task.Run(() => avisos.AvisarAsync(ruta, fecha, "Operaciones cambió los baños de una parada de tu ruta de hoy."));
                return Results.NoContent();
            }
        }).AddEndpointFilter(EndpointsAcceso.ValidarCsrf);

        // Cambio de las órdenes (fosas) solo para ese día; las que pasan del plan son extraordinarias. Avisa al operador si es hoy.
        app.MapPut("/api/ordenes/ajustes-fosas", async (SolicitudAjusteFosas s, HttpContext http, IRepositorioOrdenes ordenes, Avisos.AvisosTrabajo avisos) =>
        {
            if (!ServiciosAcceso.LeerIds(http.User, out var org, out var acceso)) return Results.Unauthorized();
            if (s.RutaId is not { } ruta || ruta == Guid.Empty || s.ParadaId is not { } parada || parada == Guid.Empty || s.ClienteId is not { } cliente || cliente == Guid.Empty)
                return Results.BadRequest(new MensajeError("Elige la ruta, la parada y el cliente."));
            if (!DateOnly.TryParseExact(s.Fecha, "yyyy-MM-dd", out var fecha)) return Results.BadRequest(new MensajeError("Indica la fecha como AAAA-MM-DD."));
            if (fecha < Avisos.AvisosTrabajo.HoyLocal()) return Results.BadRequest(new MensajeError(MensajeAjusteFosas));
            if (s.Fosas is not (>= 0 and <= 10_000)) return Results.BadRequest(new MensajeError("Indica las órdenes, de 0 a 10000."));
            var r = await ordenes.AjustarFosasAsync(org, acceso, ruta, fecha, parada, cliente, s.Fosas.Value, http.RequestAborted);
            return r.Estado switch
            {
                EstadoEscritura.Correcta => AvisarAjuste(r.Revision != 0),
                EstadoEscritura.NoEncontrada => Results.Json(new MensajeError("No se encontró la ruta."), statusCode: StatusCodes.Status404NotFound),
                EstadoEscritura.SinPermiso => Results.Json(new MensajeError(MensajeSinPermiso), statusCode: StatusCodes.Status403Forbidden),
                _ => Results.BadRequest(new MensajeError(r.Mensaje!)),
            };
            IResult AvisarAjuste(bool huboCambio)
            {
                if (!huboCambio) return Results.NoContent();
                _ = Task.Run(() => avisos.AvisarAsync(ruta, fecha, "Operaciones cambió las órdenes de una parada de tu ruta de hoy."));
                return Results.NoContent();
            }
        }).AddEndpointFilter(EndpointsAcceso.ValidarCsrf);
    }
}
