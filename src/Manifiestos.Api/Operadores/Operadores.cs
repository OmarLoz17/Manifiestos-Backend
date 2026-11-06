using Manifiestos.Api.Acceso;
using Manifiestos.Api.Datos;
using Manifiestos.Api.Rutas;
using Npgsql;

namespace Manifiestos.Api.Operadores;

// ---- Respuestas (JSON camelCase) ----

public sealed record UnidadDeOperador(Guid Id, string? NumeroEconomico);

/// <summary>Persona conocida en una sucursal. Revisión 0 = todavía sin ficha (solo es operador de una unidad).</summary>
public sealed record OperadorFicha(
    Guid Id, Guid SucursalId, int Revision, string Nombre, string? Cargo, IReadOnlyList<UnidadDeOperador> Unidades);

// ---- Solicitudes ----

public sealed record SolicitudNuevoOperador(Guid? SucursalId, string? Nombre, string? Cargo);
public sealed record SolicitudEditarOperador(Guid? SucursalId, string? Nombre, string? Cargo, int? Revision);

public interface IRepositorioOperadores
{
    Task<IReadOnlyList<OperadorFicha>> ListarAsync(Guid organizacion, Guid acceso, CancellationToken ct);
    Task<ResultadoEscritura> CrearAsync(Guid organizacion, Guid acceso, Guid sucursal, string nombre, string? cargo, CancellationToken ct);
    Task<ResultadoEscritura> EditarAsync(Guid organizacion, Guid acceso, Guid sucursal, Guid persona, string nombre, string? cargo, int revision, CancellationToken ct);
}

/// <summary>
/// Operadores (migración 0013) con el usuario manifiestos_api. El alta y la corrección pasan por
/// negocio.alta_operador / negocio.corregir_operador, que exigen datos.escribir en la sucursal y crean
/// fichas nuevas en lugar de sobrescribir. No se guardan ni muestran correos ni cuentas Google (PG-20, PG-22).
/// </summary>
public sealed class RepositorioOperadores(NpgsqlDataSource datos) : IRepositorioOperadores
{
    public const string MensajeModificado = "Alguien más modificó este operador. Recarga para ver los cambios.";
    public const string MensajeSinNombre = "Escribe el nombre completo del operador.";

    public async Task<IReadOnlyList<OperadorFicha>> ListarAsync(Guid organizacion, Guid acceso, CancellationToken ct)
    {
        await using var tx = await TransaccionContexto.AbrirAsync(datos, organizacion, acceso, ct);

        // Unidades cuyo operador vigente es la persona.
        var unidades = new Dictionary<(Guid Sucursal, Guid Persona), List<UnidadDeOperador>>();
        await using (var cmd = tx.Comando("""
            SELECT veh.sucursal_id, op.persona_id, veh.id, coalesce(f.numero_economico, veh.numero_economico)
            FROM negocio.vehiculo veh
            JOIN LATERAL (SELECT a.persona_id FROM negocio.asignacion_unidad a
                WHERE (a.organizacion_id, a.sucursal_id, a.vehiculo_id) = (veh.organizacion_id, veh.sucursal_id, veh.id)
                ORDER BY a.revision DESC LIMIT 1) op ON op.persona_id IS NOT NULL
            LEFT JOIN LATERAL (SELECT x.numero_economico FROM negocio.ficha_vehiculo x
                WHERE (x.organizacion_id, x.sucursal_id, x.vehiculo_id) = (veh.organizacion_id, veh.sucursal_id, veh.id)
                ORDER BY x.revision DESC LIMIT 1) f ON true
            WHERE veh.organizacion_id = $1
            ORDER BY 4 NULLS LAST, veh.id
            """, organizacion))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct))
            {
                var clave = (r.GetGuid(0), r.GetGuid(1));
                if (!unidades.TryGetValue(clave, out var l)) unidades[clave] = l = [];
                l.Add(new UnidadDeOperador(r.GetGuid(2), r.IsDBNull(3) ? null : r.GetString(3)));
            }

        // Una fila por persona y sucursal donde es conocida: con ficha o como operador (histórico) de una unidad.
        var lista = new List<OperadorFicha>();
        await using (var cmd = tx.Comando("""
            SELECT p.id, k.sucursal_id, coalesce(f.revision, 0), coalesce(f.nombre_completo, p.nombre_completo), f.cargo
            FROM seguridad.persona p
            JOIN LATERAL (
                SELECT x.sucursal_id FROM negocio.operador_ficha x WHERE x.organizacion_id = p.organizacion_id AND x.persona_id = p.id
                UNION
                SELECT a.sucursal_id FROM negocio.asignacion_unidad a WHERE a.organizacion_id = p.organizacion_id AND a.persona_id = p.id
            ) k ON true
            LEFT JOIN LATERAL (SELECT x.* FROM negocio.operador_ficha x
                WHERE (x.organizacion_id, x.sucursal_id, x.persona_id) = (p.organizacion_id, k.sucursal_id, p.id)
                ORDER BY x.revision DESC LIMIT 1) f ON true
            WHERE p.organizacion_id = $1
            ORDER BY 4, p.id
            """, organizacion))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct))
            {
                var (persona, sucursal) = (r.GetGuid(0), r.GetGuid(1));
                lista.Add(new OperadorFicha(persona, sucursal, r.GetInt32(2), r.GetString(3), r.IsDBNull(4) ? null : r.GetString(4),
                    unidades.GetValueOrDefault((sucursal, persona)) ?? []));
            }
        await tx.ConfirmarAsync(ct);
        return lista;
    }

    public async Task<ResultadoEscritura> CrearAsync(Guid organizacion, Guid acceso, Guid sucursal, string nombre, string? cargo, CancellationToken ct)
    {
        await using var tx = await TransaccionContexto.AbrirAsync(datos, organizacion, acceso, ct);
        try
        {
            Guid id;
            await using (var cmd = tx.Comando("SELECT negocio.alta_operador($1, $2, $3::text, $4::text)", organizacion, sucursal, nombre, (object?)cargo ?? DBNull.Value))
                id = (Guid)(await cmd.ExecuteScalarAsync(ct))!;
            await tx.ConfirmarAsync(ct);
            return ResultadoEscritura.Correcta(id, 1);
        }
        catch (PostgresException e) when (Traducir(e) is { } r) { return r; }
    }

    public async Task<ResultadoEscritura> EditarAsync(Guid organizacion, Guid acceso, Guid sucursal, Guid persona, string nombre, string? cargo, int revision, CancellationToken ct)
    {
        await using var tx = await TransaccionContexto.AbrirAsync(datos, organizacion, acceso, ct);
        try
        {
            int vigente;
            await using (var cmd = tx.Comando("SELECT negocio.corregir_operador($1, $2, $3, $4, $5::text, $6::text)",
                organizacion, sucursal, persona, revision, nombre, (object?)cargo ?? DBNull.Value))
                vigente = (int)(await cmd.ExecuteScalarAsync(ct))!;
            await tx.ConfirmarAsync(ct);
            return ResultadoEscritura.Correcta(persona, vigente);
        }
        catch (PostgresException e) when (Traducir(e) is { } r) { return r; }
    }

    private static ResultadoEscritura? Traducir(PostgresException e) => e.SqlState switch
    {
        PostgresErrorCodes.InsufficientPrivilege => ResultadoEscritura.SinPermiso,
        PostgresErrorCodes.InvalidParameterValue => ResultadoEscritura.Invalida(MensajeSinNombre),
        PostgresErrorCodes.SerializationFailure or PostgresErrorCodes.UniqueViolation => ResultadoEscritura.Conflicto(MensajeModificado),
        PostgresErrorCodes.NoDataFound => ResultadoEscritura.NoEncontrada,
        PostgresErrorCodes.ForeignKeyViolation => ResultadoEscritura.Invalida("La sucursal no existe."),
        _ => null,
    };
}

/// <summary>
/// Pantalla "Operadores" del panel: nombre y cargo de las personas que operan las unidades.
/// No administra cuentas Google ni teléfonos (PG-20, PG-22). No se borran operadores.
/// </summary>
public static class EndpointsOperadores
{
    public const int LargoMaximoCampo = 200;
    public const string MensajeSinPermiso = "No tienes permiso para modificar operadores en esta sucursal.";
    public const string MensajeNoEncontrado = "No se encontró el operador.";

    public static IServiceCollection AgregarOperadores(this IServiceCollection services) =>
        services.AddSingleton<IRepositorioOperadores, RepositorioOperadores>();

    public static void MapearOperadores(this IEndpointRouteBuilder app)
    {
        // Sin AllowAnonymous: la política por defecto exige sesión revalidada.
        var g = app.MapGroup("/api/operadores");
        g.MapGet("", ListarAsync);
        g.MapPost("", CrearAsync).AddEndpointFilter(EndpointsAcceso.ValidarCsrf);
        g.MapPut("/{id:guid}", EditarAsync).AddEndpointFilter(EndpointsAcceso.ValidarCsrf);
    }

    private static async Task<IResult> ListarAsync(HttpContext http, IRepositorioOperadores operadores)
    {
        if (!ServiciosAcceso.LeerIds(http.User, out var org, out var acceso)) return Results.Unauthorized();
        http.Response.Headers.CacheControl = "no-store";
        return Results.Ok(await operadores.ListarAsync(org, acceso, http.RequestAborted));
    }

    private static async Task<IResult> CrearAsync(SolicitudNuevoOperador s, HttpContext http, IRepositorioOperadores operadores)
    {
        if (!ServiciosAcceso.LeerIds(http.User, out var org, out var acceso)) return Results.Unauthorized();
        if (s.SucursalId is not { } sucursal || sucursal == Guid.Empty) return Error("Falta la sucursal.");
        if (Validar(s.Nombre, s.Cargo, out var nombre, out var cargo) is { } error) return error;
        var r = await operadores.CrearAsync(org, acceso, sucursal, nombre!, cargo, http.RequestAborted);
        return Responder(r, creado: true, $"/api/operadores/{r.Id}");
    }

    private static async Task<IResult> EditarAsync(Guid id, SolicitudEditarOperador s, HttpContext http, IRepositorioOperadores operadores)
    {
        if (!ServiciosAcceso.LeerIds(http.User, out var org, out var acceso)) return Results.Unauthorized();
        if (s.SucursalId is not { } sucursal || sucursal == Guid.Empty) return Error("Falta la sucursal.");
        if (Validar(s.Nombre, s.Cargo, out var nombre, out var cargo) is { } error) return error;
        // 0 = la persona todavía no tenía ficha en el listado; la primera se crea con revisión 1.
        if (s.Revision is not >= 0) return Error("Falta la revisión que se editó.");
        return Responder(await operadores.EditarAsync(org, acceso, sucursal, id, nombre!, cargo, s.Revision.Value, http.RequestAborted), creado: false, null);
    }

    private static IResult? Validar(string? nombre, string? cargo, out string? n, out string? c)
    {
        n = string.IsNullOrWhiteSpace(nombre) ? null : nombre.Trim();
        c = string.IsNullOrWhiteSpace(cargo) ? null : cargo.Trim();
        if (n is null) return Error(RepositorioOperadores.MensajeSinNombre);
        if (n.Length > LargoMaximoCampo || c?.Length > LargoMaximoCampo) return Error($"Cada dato del operador admite hasta {LargoMaximoCampo} caracteres.");
        return null;
    }

    private static IResult Responder(ResultadoEscritura r, bool creado, string? ubicacion) => r.Estado switch
    {
        EstadoEscritura.Correcta when creado => Results.Created(ubicacion, new RevisionGuardada(r.Id, r.Revision)),
        EstadoEscritura.Correcta => Results.Ok(new RevisionGuardada(r.Id, r.Revision)),
        EstadoEscritura.NoEncontrada => Results.Json(new MensajeError(MensajeNoEncontrado), statusCode: StatusCodes.Status404NotFound),
        EstadoEscritura.SinPermiso => Results.Json(new MensajeError(MensajeSinPermiso), statusCode: StatusCodes.Status403Forbidden),
        EstadoEscritura.Conflicto => Results.Json(new MensajeError(r.Mensaje!), statusCode: StatusCodes.Status409Conflict),
        _ => Error(r.Mensaje!),
    };

    private static IResult Error(string mensaje) => Results.BadRequest(new MensajeError(mensaje));
}
