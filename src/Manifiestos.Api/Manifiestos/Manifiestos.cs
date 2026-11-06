using Manifiestos.Api.Acceso;
using Manifiestos.Api.Datos;
using Npgsql;

namespace Manifiestos.Api.Documentos;

// ---- Respuestas (JSON camelCase) ----

public sealed record UnidadDeManifiesto(string? NumeroEconomico, decimal? CapacidadM3);
public sealed record DescargaDeManifiesto(DateTimeOffset? Fecha, string? Planta);
public sealed record ParticipanteManifiesto(string Rol, string Nombre, string? Rfc, string? Autorizacion);
public sealed record RenglonManifiesto(
    int Renglon, string? Descripcion, string? Contenedor, decimal? CapacidadContenedor, string? UnidadCapacidad, int? NumeroContenedores,
    decimal? Cantidad, string? UnidadCantidad, string? Naturaleza, string? Metodo);

/// <summary>
/// Última versión de un manifiesto con lo que existe en el modelo. «Descarga» vacía = todavía no se ha
/// descargado en la planta. El folio es texto documental y puede faltar: no se asignan folios (PG-03, PG-23).
/// </summary>
public sealed record ManifiestoResumen(
    Guid Id, Guid SucursalId, Guid ClienteId, string Cliente, int Revision, string? Folio, DateTimeOffset Fecha,
    UnidadDeManifiesto? Unidad, DescargaDeManifiesto? Descarga, IReadOnlyList<ParticipanteManifiesto> Participantes,
    IReadOnlyList<RenglonManifiesto> Renglones, string? InstruccionesSeguridad, string? ObservacionesRecepcion, int Documentos);

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

        var lista = new List<ManifiestoResumen>();
        await using (var cmd = tx.Comando("""
            SELECT m.id, m.sucursal_id, m.cliente_id,
                   coalesce(nullif(btrim(fc.nombre_comercial), ''), nullif(btrim(fc.nombre_fiscal), ''), nullif(btrim(c.codigo_comercial), ''), 'Cliente sin nombre'),
                   v.id, v.revision, v.numero_manifiesto, pri.creada_en,
                   coalesce(fvh.numero_economico, veh.numero_economico), cap.valor_m3, fvh.id IS NOT NULL,
                   d.id IS NOT NULL, d.ocurrida_en, coalesce(nullif(btrim(fs.nombre), ''), s.referencia),
                   v.instrucciones_seguridad, v.observaciones_recepcion,
                   (SELECT count(*) FROM negocio.documento_manifiesto dm
                    WHERE (dm.organizacion_id, dm.sucursal_id, dm.version_id) = (v.organizacion_id, v.sucursal_id, v.id))
            FROM negocio.manifiesto m
            JOIN negocio.cliente c ON (c.organizacion_id, c.sucursal_id, c.id) = (m.organizacion_id, m.sucursal_id, m.cliente_id)
            LEFT JOIN LATERAL (SELECT x.nombre_fiscal, x.nombre_comercial FROM negocio.ficha_empresa x
                WHERE (x.organizacion_id, x.empresa_id) = (c.organizacion_id, c.empresa_id)
                ORDER BY x.revision DESC LIMIT 1) fc ON true
            JOIN LATERAL (SELECT x.* FROM negocio.version_manifiesto x
                WHERE (x.organizacion_id, x.sucursal_id, x.manifiesto_id) = (m.organizacion_id, m.sucursal_id, m.id)
                ORDER BY x.revision DESC LIMIT 1) v ON true
            JOIN LATERAL (SELECT min(x.creada_en) AS creada_en FROM negocio.version_manifiesto x
                WHERE (x.organizacion_id, x.sucursal_id, x.manifiesto_id) = (m.organizacion_id, m.sucursal_id, m.id)) pri ON true
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
                lista.Add(new ManifiestoResumen(
                    r.GetGuid(0), r.GetGuid(1), r.GetGuid(2), r.GetString(3), r.GetInt32(5), T(r, 6), fecha,
                    r.GetBoolean(10) ? new UnidadDeManifiesto(T(r, 8), D(r, 9)) : null,
                    r.GetBoolean(11) ? new DescargaDeManifiesto(r.IsDBNull(12) ? null : new DateTimeOffset(DateTime.SpecifyKind(r.GetDateTime(12), DateTimeKind.Utc)), T(r, 13)) : null,
                    participantes.GetValueOrDefault(version) ?? [], renglones.GetValueOrDefault(version) ?? [],
                    T(r, 14), T(r, 15), (int)r.GetInt64(16)));
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
