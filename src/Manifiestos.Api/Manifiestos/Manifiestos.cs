using Manifiestos.Api.Acceso;
using Manifiestos.Api.Datos;
using System.Text.Json;
using Npgsql;

namespace Manifiestos.Api.Documentos;

// ---- Respuestas (JSON camelCase) ----

public sealed record UnidadDeManifiesto(string? NumeroEconomico, decimal? CapacidadM3, Guid? Id = null);
public sealed record DescargaDeManifiesto(DateTimeOffset? Fecha, string? Planta);
public sealed record ParticipanteManifiesto(string Rol, string Nombre, string? Rfc, string? Autorizacion);
public sealed record RenglonManifiesto(
    int Renglon, string? Descripcion, string? Contenedor, decimal? CapacidadContenedor, string? UnidadCapacidad, int? NumeroContenedores,
    decimal? Cantidad, string? UnidadCantidad, string? Naturaleza, string? Metodo);

// ---- Sanitarios (0028): manifiesto sin cliente, armado al hacer la tirada. Volúmenes en m3 estimados, nunca litros. ----
public sealed record FirmaGeneradorSanitarios(bool Provisional, string? Nombre);
public sealed record FirmaPersonaSanitarios(string? Nombre, bool Firmado);
public sealed record FirmasSanitarios(FirmaGeneradorSanitarios Generador, FirmaPersonaSanitarios Transportista, FirmaPersonaSanitarios Destinatario);
public sealed record PeriodoSanitarios(DateOnly Desde, DateOnly Hasta);
public sealed record ParadaSanitarios(DateOnly Fecha, string? Sitio, int Clientes, IReadOnlyList<string> NombresClientes, decimal? Latitud, decimal? Longitud, decimal? PrecisionM, int Banos, decimal? VolumenM3 = null, Guid? FotoArchivoId = null, bool EsFosa = false);
public sealed record DatosSanitarios(
    string? Planta, string? Recibe, PeriodoSanitarios? Periodo, int TotalBanos, decimal TotalM3Estimado, string? Metodo,
    FirmasSanitarios Firmas, IReadOnlyList<ParadaSanitarios> Paradas);

/// <summary>
/// Última versión de un manifiesto con lo que existe en el modelo. «Descarga» vacía = todavía no se ha
/// descargado en la planta. El folio es texto documental: lo asigna el servidor al armar el manifiesto (NNN/AAAA, contador compartido; alcance y zona horaria pendientes en PG-18 y PG-27).
/// </summary>
public sealed record ManifiestoResumen(
    Guid Id, Guid SucursalId, Guid? ClienteId, string? Cliente, int Revision, string? Folio, DateTimeOffset Fecha,
    UnidadDeManifiesto? Unidad, DescargaDeManifiesto? Descarga, IReadOnlyList<ParticipanteManifiesto> Participantes,
    IReadOnlyList<RenglonManifiesto> Renglones, string? InstruccionesSeguridad, string? ObservacionesRecepcion, int Documentos,
    string Tipo = "fosa_fija", DatosSanitarios? Sanitarios = null, IReadOnlyList<EvidenciaManifiesto>? Evidencias = null, FirmaPrecargadaManifiesto? GeneradorPrecargado = null);

/// <summary>Archivo de evidencia de un manifiesto (foto o firma); se ve en el panel con GET /api/archivos/{id}.</summary>
public sealed record EvidenciaManifiesto(Guid ArchivoId, string Rol, double? Latitud = null, double? Longitud = null, double? PrecisionM = null, string? Nombre = null);

/// <summary>Firma precargada del generador (nuestra empresa) para clientes sin autorización; sin imagen mientras sea provisional.</summary>
public sealed record FirmaPrecargadaManifiesto(string Nombre, bool Provisional);

public interface IRepositorioManifiestos
{
    Task<IReadOnlyList<ManifiestoResumen>> ListarAsync(Guid organizacion, Guid acceso, CancellationToken ct);
}

/// <summary>Manifiestos (migración 0004), solo lectura: RLS limita a las sucursales con datos.leer.</summary>
public sealed class RepositorioManifiestos(NpgsqlDataSource datos) : IRepositorioManifiestos
{
    public async Task<IReadOnlyList<ManifiestoResumen>> ListarAsync(Guid organizacion, Guid acceso, CancellationToken ct)
    {
        await using var tx = await TransaccionContexto.AbrirAsync(datos, organizacion, acceso, ct);
        string? T(NpgsqlDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);
        decimal? D(NpgsqlDataReader r, int i) => r.IsDBNull(i) ? null : r.GetDecimal(i);

        var participantes = new Dictionary<Guid, List<ParticipanteManifiesto>>();
        await using (var cmd = tx.Comando("""
            SELECT p.version_id, p.rol, coalesce(nullif(btrim(fe.nombre_comercial), ''), nullif(btrim(fe.nombre_fiscal), ''), 'Sin nombre'),
                   fe.rfc, au.numero
            FROM negocio.participante_manifiesto p
            JOIN negocio.ficha_empresa fe ON (fe.organizacion_id, fe.empresa_id, fe.id) = (p.organizacion_id, p.empresa_id, p.ficha_empresa_id)
            LEFT JOIN negocio.autorizacion_empresa au ON (au.organizacion_id, au.empresa_id, au.id) = (p.organizacion_id, p.empresa_id, p.autorizacion_id)
            WHERE p.organizacion_id = $1
            ORDER BY p.version_id, CASE p.rol WHEN 'generador' THEN 1 WHEN 'transportista' THEN 2 ELSE 3 END
            """, organizacion))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct))
            {
                if (!participantes.TryGetValue(r.GetGuid(0), out var l)) participantes[r.GetGuid(0)] = l = [];
                l.Add(new ParticipanteManifiesto(r.GetString(1), r.GetString(2), T(r, 3), T(r, 4)));
            }

        var renglones = new Dictionary<Guid, List<RenglonManifiesto>>();
        await using (var cmd = tx.Comando("""
            SELECT version_id, renglon, descripcion_residuo, tipo_contenedor, capacidad_contenedor, unidad_capacidad,
                   numero_contenedores, cantidad, unidad_cantidad, naturaleza_cantidad, metodo_cuantificacion
            FROM negocio.detalle_manifiesto WHERE organizacion_id = $1 ORDER BY version_id, renglon
            """, organizacion))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct))
            {
                if (!renglones.TryGetValue(r.GetGuid(0), out var l)) renglones[r.GetGuid(0)] = l = [];
                l.Add(new RenglonManifiesto(r.GetInt32(1), T(r, 2), T(r, 3), D(r, 4), T(r, 5), r.IsDBNull(6) ? null : r.GetInt32(6),
                    D(r, 7), T(r, 8), T(r, 9), T(r, 10)));
            }

        // Sanitarios: recepción (planta, quien recibe, firmas) y paradas con la coordenada de su foto.
        var recepciones = new Dictionary<Guid, (string? Planta, string? Recibe, string? Operador, string? Generador, bool GenProvisional, bool FirmaOp, bool FirmaRec)>();
        await using (var cmd = tx.Comando("""
            SELECT rm.version_id, rm.planta_nombre, rm.nombre_recibe, p.nombre_completo, fg.nombre_firmante, coalesce(fg.es_provisional, true),
                   EXISTS (SELECT 1 FROM negocio.evidencia_captura e WHERE (e.organizacion_id, e.sucursal_id, e.captura_id) = (rm.organizacion_id, rm.sucursal_id, rm.captura_id)
                           AND e.rol = 'FIRMA_OPERADOR' AND e.archivo_id IS NOT NULL),
                   EXISTS (SELECT 1 FROM negocio.evidencia_captura e WHERE (e.organizacion_id, e.sucursal_id, e.captura_id) = (rm.organizacion_id, rm.sucursal_id, rm.captura_id)
                           AND e.rol = 'FIRMA_RECEPCION' AND e.archivo_id IS NOT NULL)
            FROM negocio.recepcion_manifiesto rm
            JOIN negocio.tirada t ON (t.organizacion_id, t.sucursal_id, t.captura_id) = (rm.organizacion_id, rm.sucursal_id, rm.captura_id)
            LEFT JOIN seguridad.persona p ON (p.organizacion_id, p.id) = (t.organizacion_id, t.persona_id)
            LEFT JOIN negocio.firma_generador_precargada fg ON (fg.organizacion_id, fg.sucursal_id, fg.id) = (rm.organizacion_id, rm.sucursal_id, rm.firma_generador_id)
            WHERE rm.organizacion_id = $1
            """, organizacion))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct))
                recepciones[r.GetGuid(0)] = (T(r, 1), T(r, 2), T(r, 3), T(r, 4), r.GetBoolean(5), r.GetBoolean(6), r.GetBoolean(7));

        var paradasSan = new Dictionary<Guid, List<ParadaSanitarios>>();
        await using (var cmd = tx.Comando("""
            SELECT mp.version_id, mp.fecha, coalesce(nullif(btrim(fs.nombre), ''), s.referencia), mp.banos, mp.latitud, mp.longitud, mp.precision_m, mp.clientes::text, mp.volumen_m3,
                   (SELECT e.archivo_id FROM negocio.evidencia_captura e WHERE (e.organizacion_id, e.sucursal_id, e.id) = (mp.organizacion_id, mp.sucursal_id, mp.evidencia_foto_id)),
                   mp.es_fosa
            FROM negocio.manifiesto_parada mp
            JOIN negocio.sitio s ON (s.organizacion_id, s.sucursal_id, s.id) = (mp.organizacion_id, mp.sucursal_id, mp.sitio_id)
            LEFT JOIN negocio.ficha_sitio fs ON (fs.organizacion_id, fs.sucursal_id, fs.id) = (mp.organizacion_id, mp.sucursal_id, mp.ficha_sitio_id)
            WHERE mp.organizacion_id = $1 ORDER BY mp.version_id, mp.renglon
            """, organizacion))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct))
            {
                var nombres = new List<string>();
                using (var doc = JsonDocument.Parse(r.GetString(7)))
                    foreach (var c in doc.RootElement.EnumerateArray()) nombres.Add(c.GetProperty("nombre").GetString() ?? "");
                var f = DateTime.SpecifyKind(r.GetDateTime(1), DateTimeKind.Utc);
                if (!paradasSan.TryGetValue(r.GetGuid(0), out var l)) paradasSan[r.GetGuid(0)] = l = [];
                l.Add(new ParadaSanitarios(DateOnly.FromDateTime(f), T(r, 2), nombres.Count, nombres, D(r, 4), D(r, 5), D(r, 6), r.GetInt32(3), D(r, 8), r.IsDBNull(9) ? null : r.GetGuid(9), r.GetBoolean(10)));
            }

        // Evidencias (fotos y firmas) de las capturas que sustentan cada manifiesto: orden (fosa fija), tirada y paradas (sanitarios).
        var evidencias = new Dictionary<Guid, List<EvidenciaManifiesto>>();
        await using (var cmd = tx.Comando("""
            WITH caps AS (
                SELECT om.organizacion_id, om.sucursal_id, om.manifiesto_id, ct.captura_id FROM negocio.orden_manifiesto om
                JOIN negocio.captura_trabajo ct ON (ct.organizacion_id, ct.sucursal_id, ct.orden_derivada_id) = (om.organizacion_id, om.sucursal_id, om.orden_derivada_id) AND ct.tipo <> 'PROBLEMA'
                -- La tirada tiene su propio id: las firmas (operador y quien recibe) cuelgan de la CAPTURA de la tirada.
                UNION SELECT tm.organizacion_id, tm.sucursal_id, tm.manifiesto_id, ti.captura_id FROM negocio.tirada_manifiesto tm
                JOIN negocio.tirada ti ON (ti.organizacion_id, ti.sucursal_id, ti.id) = (tm.organizacion_id, tm.sucursal_id, tm.tirada_id)
                UNION SELECT v.organizacion_id, v.sucursal_id, v.manifiesto_id, mp.captura_id FROM negocio.manifiesto_parada mp
                JOIN negocio.version_manifiesto v ON (v.organizacion_id, v.sucursal_id, v.id) = (mp.organizacion_id, mp.sucursal_id, mp.version_id))
            SELECT c.manifiesto_id, e.archivo_id, e.rol, e.latitud, e.longitud, e.precision_metros,
                   CASE e.rol WHEN 'FIRMA_OPERADOR' THEN p.nombre_completo ELSE ct.nombre_firmante END FROM caps c
            JOIN negocio.evidencia_captura e ON (e.organizacion_id, e.sucursal_id, e.captura_id) = (c.organizacion_id, c.sucursal_id, c.captura_id)
            LEFT JOIN negocio.captura_trabajo ct ON (ct.organizacion_id, ct.sucursal_id, ct.captura_id) = (c.organizacion_id, c.sucursal_id, c.captura_id)
            LEFT JOIN seguridad.persona p ON (p.organizacion_id, p.id) = (ct.organizacion_id, ct.persona_id)
            WHERE c.organizacion_id = $1 AND e.archivo_id IS NOT NULL ORDER BY c.manifiesto_id, e.recibida_en
            """, organizacion))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct))
            {
                if (!evidencias.TryGetValue(r.GetGuid(0), out var l)) evidencias[r.GetGuid(0)] = l = [];
                l.Add(new EvidenciaManifiesto(r.GetGuid(1), r.GetString(2), r.IsDBNull(3) ? null : Convert.ToDouble(r.GetValue(3)), r.IsDBNull(4) ? null : Convert.ToDouble(r.GetValue(4)), r.IsDBNull(5) ? null : Convert.ToDouble(r.GetValue(5)), T(r, 6)));
            }

        var precargadas = new Dictionary<Guid, FirmaPrecargadaManifiesto>();
        await using (var cmd = tx.Comando("""
            SELECT DISTINCT ON (sucursal_id) sucursal_id, nombre_firmante, es_provisional FROM negocio.firma_generador_precargada
            WHERE organizacion_id = $1 ORDER BY sucursal_id, vigente_desde DESC
            """, organizacion))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct)) precargadas[r.GetGuid(0)] = new FirmaPrecargadaManifiesto(r.GetString(1), r.GetBoolean(2));

        // Fosa fija: la orden queda descargada cuando quien recibe en la planta ya firmó (captura RECEPCION_PLANTA de la orden); no hay que esperar otro registro.
        var plantaFosaFija = new Dictionary<Guid, DescargaDeManifiesto>();
        await using (var cmd = tx.Comando("""
            SELECT om.manifiesto_id, ct.planta_nombre, kt.ocurrida_en FROM negocio.orden_manifiesto om
            JOIN negocio.captura_trabajo ct ON (ct.organizacion_id, ct.sucursal_id, ct.orden_derivada_id) = (om.organizacion_id, om.sucursal_id, om.orden_derivada_id) AND ct.tipo = 'RECEPCION_PLANTA'
            LEFT JOIN negocio.captura kt ON (kt.organizacion_id, kt.sucursal_id, kt.id) = (ct.organizacion_id, ct.sucursal_id, ct.captura_id)
            WHERE om.organizacion_id = $1 ORDER BY kt.ocurrida_en
            """, organizacion))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct))
                plantaFosaFija[r.GetGuid(0)] = new DescargaDeManifiesto(r.IsDBNull(2) ? null : new DateTimeOffset(DateTime.SpecifyKind(r.GetDateTime(2), DateTimeKind.Utc)), r.IsDBNull(1) ? null : r.GetString(1));

        var lista = new List<ManifiestoResumen>();
        await using (var cmd = tx.Comando("""
            SELECT m.id, m.sucursal_id, m.cliente_id,
                   CASE WHEN c.id IS NULL THEN NULL ELSE coalesce(nullif(btrim(fc.nombre_comercial), ''), nullif(btrim(fc.nombre_fiscal), ''), nullif(btrim(c.codigo_comercial), ''), 'Cliente sin nombre') END,
                   v.id, v.revision, v.numero_manifiesto,
                   CASE WHEN m.tipo = 'sanitarios' THEN coalesce(kt.ocurrida_en, pri.creada_en) ELSE pri.creada_en END,   -- sanitarios: fecha de la tirada (cuando firma quien recibe)
                   coalesce(fvh.numero_economico, veh.numero_economico), cap.valor_m3, fvh.id IS NOT NULL,
                   d.id IS NOT NULL, d.ocurrida_en, coalesce(nullif(btrim(fs.nombre), ''), s.referencia),
                   v.instrucciones_seguridad, v.observaciones_recepcion,
                   (SELECT count(*) FROM negocio.documento_manifiesto dm
                    WHERE (dm.organizacion_id, dm.sucursal_id, dm.version_id) = (v.organizacion_id, v.sucursal_id, v.id)),
                   m.tipo, fvh.vehiculo_id
            FROM negocio.manifiesto m
            LEFT JOIN negocio.cliente c ON (c.organizacion_id, c.sucursal_id, c.id) = (m.organizacion_id, m.sucursal_id, m.cliente_id)
            LEFT JOIN LATERAL (SELECT x.nombre_fiscal, x.nombre_comercial FROM negocio.ficha_empresa x
                WHERE (x.organizacion_id, x.empresa_id) = (c.organizacion_id, c.empresa_id)
                ORDER BY x.revision DESC LIMIT 1) fc ON true
            JOIN LATERAL (SELECT x.* FROM negocio.version_manifiesto x
                WHERE (x.organizacion_id, x.sucursal_id, x.manifiesto_id) = (m.organizacion_id, m.sucursal_id, m.id)
                ORDER BY x.revision DESC LIMIT 1) v ON true
            JOIN LATERAL (SELECT min(x.creada_en) AS creada_en FROM negocio.version_manifiesto x
                WHERE (x.organizacion_id, x.sucursal_id, x.manifiesto_id) = (m.organizacion_id, m.sucursal_id, m.id)) pri ON true
            LEFT JOIN negocio.recepcion_manifiesto rmt ON (rmt.organizacion_id, rmt.sucursal_id, rmt.version_id) = (v.organizacion_id, v.sucursal_id, v.id)
            LEFT JOIN negocio.captura kt ON (kt.organizacion_id, kt.sucursal_id, kt.id) = (rmt.organizacion_id, rmt.sucursal_id, rmt.captura_id)
            LEFT JOIN negocio.ficha_vehiculo fvh ON (fvh.organizacion_id, fvh.sucursal_id, fvh.id) = (v.organizacion_id, v.sucursal_id, v.ficha_vehiculo_id)
            LEFT JOIN negocio.vehiculo veh ON (veh.organizacion_id, veh.sucursal_id, veh.id) = (fvh.organizacion_id, fvh.sucursal_id, fvh.vehiculo_id)
            LEFT JOIN catalogo.capacidad_vehiculo cap ON cap.id = fvh.capacidad_id
            LEFT JOIN negocio.descarga d ON (d.organizacion_id, d.sucursal_id, d.id) = (m.organizacion_id, m.sucursal_id, m.descarga_id)
            LEFT JOIN negocio.ficha_sitio fs ON (fs.organizacion_id, fs.sucursal_id, fs.id) = (d.organizacion_id, d.sucursal_id, d.ficha_sitio_ptar_id)
            LEFT JOIN negocio.sitio s ON (s.organizacion_id, s.sucursal_id, s.id) = (fs.organizacion_id, fs.sucursal_id, fs.sitio_id)
            WHERE m.organizacion_id = $1
            ORDER BY pri.creada_en DESC, m.id
            """, organizacion))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct))
            {
                var version = r.GetGuid(4);
                var fecha = new DateTimeOffset(DateTime.SpecifyKind(r.GetDateTime(7), DateTimeKind.Utc));
                var renglonesM = renglones.GetValueOrDefault(version) ?? [];
                DatosSanitarios? san = null;
                if (T(r, 17) == "sanitarios")
                {
                    var paradas = paradasSan.GetValueOrDefault(version) ?? [];
                    var rec = recepciones.GetValueOrDefault(version);
                    var primero = renglonesM.FirstOrDefault();
                    san = new DatosSanitarios(rec.Planta, rec.Recibe,
                        paradas.Count == 0 ? null : new PeriodoSanitarios(paradas.Min(p => p.Fecha), paradas.Max(p => p.Fecha)),
                        paradas.Sum(p => p.Banos), primero?.Cantidad ?? 0m, primero?.Metodo,
                        new FirmasSanitarios(new FirmaGeneradorSanitarios(rec.GenProvisional, rec.Generador),
                            new FirmaPersonaSanitarios(rec.Operador, rec.FirmaOp), new FirmaPersonaSanitarios(rec.Recibe, rec.FirmaRec)),
                        paradas);
                }
                lista.Add(new ManifiestoResumen(
                    r.GetGuid(0), r.GetGuid(1), r.IsDBNull(2) ? null : r.GetGuid(2), T(r, 3), r.GetInt32(5), T(r, 6), fecha,
                    r.GetBoolean(10) ? new UnidadDeManifiesto(T(r, 8), D(r, 9), r.IsDBNull(18) ? null : r.GetGuid(18)) : null,
                    r.GetBoolean(11) ? new DescargaDeManifiesto(r.IsDBNull(12) ? null : new DateTimeOffset(DateTime.SpecifyKind(r.GetDateTime(12), DateTimeKind.Utc)), T(r, 13)) : plantaFosaFija.GetValueOrDefault(r.GetGuid(0)),
                    participantes.GetValueOrDefault(version) ?? [], renglonesM,
                    T(r, 14), T(r, 15), (int)r.GetInt64(16), T(r, 17) ?? "fosa_fija", san, evidencias.GetValueOrDefault(r.GetGuid(0)) ?? [], precargadas.GetValueOrDefault(r.GetGuid(1))));
            }
        await tx.ConfirmarAsync(ct);
        return lista;
    }
}

/// <summary>Pantalla "Manifiestos" del panel: consulta. No emite, numera ni genera PDF todavía.</summary>
public static class EndpointsManifiestos
{
    public static IServiceCollection AgregarManifiestos(this IServiceCollection services) =>
        services.AddSingleton<IRepositorioManifiestos, RepositorioManifiestos>();

    public static void MapearManifiestos(this IEndpointRouteBuilder app)
    {
        // Sin AllowAnonymous: la política por defecto exige sesión revalidada.
        app.MapGet("/api/manifiestos", async (HttpContext http, IRepositorioManifiestos manifiestos) =>
        {
            if (!ServiciosAcceso.LeerIds(http.User, out var org, out var acceso)) return Results.Unauthorized();
            http.Response.Headers.CacheControl = "no-store";
            return Results.Ok(await manifiestos.ListarAsync(org, acceso, http.RequestAborted));
        });
    }
}
