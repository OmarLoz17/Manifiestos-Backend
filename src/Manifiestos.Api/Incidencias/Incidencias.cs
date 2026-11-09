using Manifiestos.Api.Acceso;
using Manifiestos.Api.Datos;
using Manifiestos.Api.Rutas;
using Npgsql;

namespace Manifiestos.Api.Incidencias;

// ---- Respuestas (JSON camelCase) ----

public sealed record Referencia(Guid Id, string Nombre);
public sealed record ParadaDeIncidencia(Guid Id, int Posicion, string? Sitio);
public sealed record UbicacionIncidencia(decimal Latitud, decimal Longitud, decimal? PrecisionM);
public sealed record SeguimientoIncidencia(int Revision, string Estatus, string? Comentario, DateTimeOffset RegistradoEn);

public sealed record IncidenciaResumen(
    Guid Id, Guid SucursalId, string Folio, string Servicio, string Motivo, string? Descripcion, DateTimeOffset OcurridaEn,
    Referencia? Ruta, ParadaDeIncidencia? Parada, Referencia? Cliente, Referencia? Unidad, Referencia? Operador,
    int? BanosAsignados, int? BanosAtendidos, bool TieneEvidencia, UbicacionIncidencia? Ubicacion,
    string Estatus, int Revision, IReadOnlyList<SeguimientoIncidencia> Historial, bool FosaPropia);

public sealed record SolicitudSeguimiento(string? Estatus, string? Comentario, int? Revision);

public interface IRepositorioIncidencias
{
    Task<IReadOnlyList<IncidenciaResumen>> ListarAsync(Guid organizacion, Guid acceso, CancellationToken ct);
    Task<ResultadoEscritura> SeguirAsync(Guid organizacion, Guid acceso, Guid incidencia, string estatus, string? comentario, int revision, CancellationToken ct);
}

/// <summary>
/// Incidencias (migración 0016). Nacen en la app con estatus «abierta»; el panel registra su seguimiento con
/// revisiones (historial inmutable) y 409 si alguien más lo cambió.
/// </summary>
public sealed class RepositorioIncidencias(NpgsqlDataSource datos) : IRepositorioIncidencias
{
    public const string MensajeModificada = "Alguien más actualizó esta incidencia. Recarga para ver los cambios.";
    public static readonly string[] Estatus = ["abierta", "en_proceso", "cerrada"];

    public async Task<IReadOnlyList<IncidenciaResumen>> ListarAsync(Guid organizacion, Guid acceso, CancellationToken ct)
    {
        await using var tx = await TransaccionContexto.AbrirAsync(datos, organizacion, acceso, ct);
        static DateTimeOffset Utc(DateTime d) => new(DateTime.SpecifyKind(d, DateTimeKind.Utc));

        var historial = new Dictionary<Guid, List<SeguimientoIncidencia>>();
        await using (var cmd = tx.Comando("""
            SELECT incidencia_id, revision, estatus, comentario, registrado_en FROM negocio.seguimiento_incidencia
            WHERE organizacion_id = $1 ORDER BY incidencia_id, revision
            """, organizacion))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct))
            {
                if (!historial.TryGetValue(r.GetGuid(0), out var l)) historial[r.GetGuid(0)] = l = [];
                l.Add(new SeguimientoIncidencia(r.GetInt32(1), r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3), Utc(r.GetDateTime(4))));
            }

        var lista = new List<IncidenciaResumen>();
        await using (var cmd = tx.Comando("""
            SELECT i.id, i.sucursal_id, i.numero, i.servicio, i.motivo, i.descripcion, i.ocurrida_en,
                   i.ruta_id, rv.nombre, i.parada_id, pa.posicion, coalesce(nullif(btrim(fs.nombre), ''), si.referencia),
                   i.cliente_id, coalesce(nullif(btrim(fe.nombre_comercial), ''), nullif(btrim(fe.nombre_fiscal), ''), nullif(btrim(c.codigo_comercial), ''), 'Cliente sin nombre'),
                   i.vehiculo_id, coalesce(fv.numero_economico, veh.numero_economico),
                   i.persona_id, coalesce(op.nombre_completo, pe.nombre_completo),
                   i.banos_asignados, i.banos_atendidos, i.evidencia_archivo_id IS NOT NULL, i.latitud, i.longitud, i.precision_m,
                   NOT EXISTS (SELECT 1 FROM negocio.autorizacion_empresa a WHERE (a.organizacion_id, a.empresa_id) = (c.organizacion_id, c.empresa_id) AND a.autoridad = 'Medio ambiente' AND a.finalidad = 'Generador')
            FROM negocio.incidencia i
            LEFT JOIN LATERAL (SELECT x.nombre FROM negocio.ruta_version x
                WHERE (x.organizacion_id, x.sucursal_id, x.ruta_id) = (i.organizacion_id, i.sucursal_id, i.ruta_id)
                ORDER BY x.revision DESC LIMIT 1) rv ON true
            LEFT JOIN negocio.parada pa ON (pa.organizacion_id, pa.sucursal_id, pa.id) = (i.organizacion_id, i.sucursal_id, i.parada_id)
            LEFT JOIN LATERAL (SELECT x.sitio_id FROM negocio.parada_version x
                WHERE (x.organizacion_id, x.sucursal_id, x.parada_id) = (pa.organizacion_id, pa.sucursal_id, pa.id)
                ORDER BY x.revision DESC LIMIT 1) pv ON true
            LEFT JOIN negocio.sitio si ON (si.organizacion_id, si.sucursal_id, si.id) = (i.organizacion_id, i.sucursal_id, pv.sitio_id)
            LEFT JOIN LATERAL (SELECT x.nombre FROM negocio.ficha_sitio x
                WHERE (x.organizacion_id, x.sucursal_id, x.sitio_id) = (si.organizacion_id, si.sucursal_id, si.id)
                ORDER BY x.revision DESC LIMIT 1) fs ON true
            LEFT JOIN LATERAL (SELECT x.fosa_propia FROM negocio.cliente_sitio x
                WHERE (x.organizacion_id, x.sucursal_id, x.cliente_id, x.sitio_id) = (i.organizacion_id, i.sucursal_id, i.cliente_id, pv.sitio_id)
                ORDER BY x.revision DESC LIMIT 1) cs ON true
            LEFT JOIN negocio.cliente c ON (c.organizacion_id, c.sucursal_id, c.id) = (i.organizacion_id, i.sucursal_id, i.cliente_id)
            LEFT JOIN LATERAL (SELECT x.nombre_fiscal, x.nombre_comercial FROM negocio.ficha_empresa x
                WHERE (x.organizacion_id, x.empresa_id) = (c.organizacion_id, c.empresa_id)
                ORDER BY x.revision DESC LIMIT 1) fe ON true
            LEFT JOIN negocio.vehiculo veh ON (veh.organizacion_id, veh.sucursal_id, veh.id) = (i.organizacion_id, i.sucursal_id, i.vehiculo_id)
            LEFT JOIN LATERAL (SELECT x.numero_economico FROM negocio.ficha_vehiculo x
                WHERE (x.organizacion_id, x.sucursal_id, x.vehiculo_id) = (veh.organizacion_id, veh.sucursal_id, veh.id)
                ORDER BY x.revision DESC LIMIT 1) fv ON true
            LEFT JOIN seguridad.persona pe ON (pe.organizacion_id, pe.id) = (i.organizacion_id, i.persona_id)
            LEFT JOIN LATERAL (SELECT x.nombre_completo FROM negocio.operador_ficha x
                WHERE (x.organizacion_id, x.sucursal_id, x.persona_id) = (i.organizacion_id, i.sucursal_id, i.persona_id)
                ORDER BY x.revision DESC LIMIT 1) op ON true
            WHERE i.organizacion_id = $1
            ORDER BY i.ocurrida_en DESC, i.numero DESC
            """, organizacion))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct))
            {
                string? T(int n) => r.IsDBNull(n) ? null : r.GetString(n);
                Referencia? Ref(int id, int nombre, string vacio) => r.IsDBNull(id) ? null : new Referencia(r.GetGuid(id), T(nombre) ?? vacio);
                var id = r.GetGuid(0);
                var h = historial.GetValueOrDefault(id) ?? [];
                var ultimo = h.LastOrDefault();
                lista.Add(new IncidenciaResumen(
                    id, r.GetGuid(1), $"INC-{r.GetInt64(2):000000}", r.GetString(3), r.GetString(4), T(5), Utc(r.GetDateTime(6)),
                    Ref(7, 8, "Ruta sin nombre"),
                    r.IsDBNull(9) ? null : new ParadaDeIncidencia(r.GetGuid(9), r.GetInt32(10), T(11)),
                    Ref(12, 13, "Cliente sin nombre"), Ref(14, 15, "Unidad sin número"), Ref(16, 17, "Operador sin nombre"),
                    r.IsDBNull(18) ? null : r.GetInt32(18), r.IsDBNull(19) ? null : r.GetInt32(19), r.GetBoolean(20),
                    r.IsDBNull(21) ? null : new UbicacionIncidencia(r.GetDecimal(21), r.GetDecimal(22), r.IsDBNull(23) ? null : r.GetDecimal(23)),
                    ultimo?.Estatus ?? "abierta", ultimo?.Revision ?? 0, h, r.GetBoolean(24)));
            }
        await tx.ConfirmarAsync(ct);
        return lista;
    }

    public async Task<ResultadoEscritura> SeguirAsync(Guid organizacion, Guid acceso, Guid incidencia, string estatus, string? comentario, int revision, CancellationToken ct)
    {
        try
        {
            await using var tx = await TransaccionContexto.AbrirAsync(datos, organizacion, acceso, ct);
            Guid sucursal;
            await using (var cmd = tx.Comando("SELECT sucursal_id FROM negocio.incidencia WHERE organizacion_id = $1 AND id = $2", organizacion, incidencia))
            {
                if (await cmd.ExecuteScalarAsync(ct) is not Guid s) return ResultadoEscritura.NoEncontrada;
                sucursal = s;
            }
            await using (var cmd = tx.Comando("SELECT coalesce(seguridad.puede($1, $2, 'datos.escribir'), false)", organizacion, sucursal))
                if (!(bool)(await cmd.ExecuteScalarAsync(ct))!) return ResultadoEscritura.SinPermiso;
            await using (var cmd = tx.Comando("SELECT pg_advisory_xact_lock(hashtextextended($1, 0))", $"incidencia:{incidencia}"))
                await cmd.ExecuteNonQueryAsync(ct);
            int actual = 0; string? estatusActual = null;
            await using (var cmd = tx.Comando("""
                SELECT revision, estatus FROM negocio.seguimiento_incidencia
                WHERE organizacion_id = $1 AND sucursal_id = $2 AND incidencia_id = $3 ORDER BY revision DESC LIMIT 1
                """, organizacion, sucursal, incidencia))
            await using (var r = await cmd.ExecuteReaderAsync(ct))
                if (await r.ReadAsync(ct)) (actual, estatusActual) = (r.GetInt32(0), r.GetString(1));
            if (actual != revision) return ResultadoEscritura.Conflicto(MensajeModificada);
            // Mismo estatus sin comentario: nada que registrar.
            if (estatus == estatusActual && comentario is null) return ResultadoEscritura.Correcta(incidencia, actual);
            await using (var cmd = tx.Comando("""
                INSERT INTO negocio.seguimiento_incidencia (organizacion_id, sucursal_id, incidencia_id, revision, estatus, comentario)
                VALUES ($1, $2, $3, $4, $5, $6)
                """, organizacion, sucursal, incidencia, actual + 1, estatus, (object?)comentario ?? DBNull.Value))
                await cmd.ExecuteNonQueryAsync(ct);
            await tx.ConfirmarAsync(ct);
            return ResultadoEscritura.Correcta(incidencia, actual + 1);
        }
        catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.UniqueViolation) { return ResultadoEscritura.Conflicto(MensajeModificada); }
    }
}

/// <summary>Pantalla "Incidencias" del panel: consulta y seguimiento de estatus. Las incidencias las crea la app.</summary>
public static class EndpointsIncidencias
{
    public const int LargoMaximoComentario = 1000;
    public const string MensajeSinPermiso = "No tienes permiso para dar seguimiento a incidencias en esta sucursal.";
    public const string MensajeNoEncontrada = "No se encontró la incidencia.";
    public const string MensajeEstatus = "El estatus debe ser abierta, en proceso o cerrada.";

    public static IServiceCollection AgregarIncidencias(this IServiceCollection services) =>
        services.AddSingleton<IRepositorioIncidencias, RepositorioIncidencias>();

    public static void MapearIncidencias(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/incidencias");
        g.MapGet("", async (HttpContext http, IRepositorioIncidencias incidencias) =>
        {
            if (!ServiciosAcceso.LeerIds(http.User, out var org, out var acceso)) return Results.Unauthorized();
            http.Response.Headers.CacheControl = "no-store";
            return Results.Ok(await incidencias.ListarAsync(org, acceso, http.RequestAborted));
        });
        g.MapPut("/{id:guid}/seguimiento", SeguirAsync).AddEndpointFilter(EndpointsAcceso.ValidarCsrf);
    }

    private static async Task<IResult> SeguirAsync(Guid id, SolicitudSeguimiento s, HttpContext http, IRepositorioIncidencias incidencias)
    {
        if (!ServiciosAcceso.LeerIds(http.User, out var org, out var acceso)) return Results.Unauthorized();
        if (s.Estatus is not { } estatus || !RepositorioIncidencias.Estatus.Contains(estatus)) return Error(MensajeEstatus);
        var comentario = string.IsNullOrWhiteSpace(s.Comentario) ? null : s.Comentario.Trim();
        if (comentario?.Length > LargoMaximoComentario) return Error($"El comentario admite hasta {LargoMaximoComentario} caracteres.");
        if (s.Revision is not >= 0) return Error("Falta la revisión que se vio.");
        var r = await incidencias.SeguirAsync(org, acceso, id, estatus, comentario, s.Revision.Value, http.RequestAborted);
        return r.Estado switch
        {
            EstadoEscritura.Correcta => Results.Ok(new RevisionGuardada(r.Id, r.Revision)),
            EstadoEscritura.NoEncontrada => Results.Json(new MensajeError(MensajeNoEncontrada), statusCode: StatusCodes.Status404NotFound),
            EstadoEscritura.SinPermiso => Results.Json(new MensajeError(MensajeSinPermiso), statusCode: StatusCodes.Status403Forbidden),
            EstadoEscritura.Conflicto => Results.Json(new MensajeError(r.Mensaje!), statusCode: StatusCodes.Status409Conflict),
            _ => Error(r.Mensaje!),
        };
    }

    private static IResult Error(string mensaje) => Results.BadRequest(new MensajeError(mensaje));
}
