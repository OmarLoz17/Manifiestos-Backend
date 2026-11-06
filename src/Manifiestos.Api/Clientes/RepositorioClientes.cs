using Manifiestos.Api.Datos;
using Manifiestos.Api.Rutas;
using Npgsql;

namespace Manifiestos.Api.Clientes;

public interface IRepositorioClientes
{
    Task<ResultadoEscritura> GuardarSitiosAsync(Guid organizacion, Guid acceso, Guid cliente, SolicitudSitioCliente[] sitios, CancellationToken ct);
    Task<IReadOnlyList<ClienteResumen>> ListarAsync(Guid organizacion, Guid acceso, CancellationToken ct);
    Task<ResultadoEscritura> CrearAsync(Guid organizacion, Guid acceso, Guid sucursal, DatosCliente datos, CancellationToken ct);
    Task<ResultadoEscritura> EditarAsync(Guid organizacion, Guid acceso, Guid cliente, DatosCliente datos, int revision,
        string? autorizacionVista, CancellationToken ct);
}

/// <summary>
/// Clientes (migración 0009) con el usuario manifiestos_api. Toda consulta corre en una
/// TransaccionContexto: RLS limita a las sucursales con datos.leer. Las altas y correcciones pasan por
/// negocio.alta_cliente / negocio.corregir_cliente, que exigen datos.escribir en la sucursal
/// y crean fichas y autorizaciones nuevas en lugar de sobrescribir.
/// </summary>
public sealed class RepositorioClientes(NpgsqlDataSource datos) : IRepositorioClientes
{
    public const string MensajeClienteModificado = "Alguien más modificó este cliente. Recarga para ver los cambios.";
    public const string MensajeQuitarAutorizacion = "No se puede quitar la autorización de un cliente desde aquí.";
    public const string MensajeSinNombre = "Escribe el nombre fiscal o el nombre comercial del cliente.";
    public const string MensajeSinFicha = "Este cliente no tiene ficha de datos registrada; todavía no se puede corregir desde aquí.";

    // Cliente autorizado: su empresa tiene autorización de medio ambiente como generador (la última registrada).
    private const string Autoridad = "Medio ambiente";
    private const string Finalidad = "Generador";

    // ---------------- Lectura ----------------

    public async Task<IReadOnlyList<ClienteResumen>> ListarAsync(Guid organizacion, Guid acceso, CancellationToken ct)
    {
        await using var tx = await TransaccionContexto.AbrirAsync(datos, organizacion, acceso, ct);

        var sitios = await SitiosCliente.LeerAsync(tx, organizacion, Guid.Empty, ct);

        // Paradas cuya última revisión incluye al cliente; nombre de la última revisión de la ruta.
        var asignaciones = new Dictionary<(Guid Sucursal, Guid Cliente), List<AsignacionCliente>>();
        await using (var cmd = tx.Comando("""
            SELECT pa.sucursal_id, pc.cliente_id, pa.ruta_id, rv.nombre, pa.id, pa.posicion, pc.banos_planeados, pc.fosas_planeadas, pv.sitio_id
            FROM negocio.parada pa
            JOIN LATERAL (SELECT y.id, y.sitio_id FROM negocio.parada_version y
                WHERE (y.organizacion_id, y.sucursal_id, y.parada_id) = (pa.organizacion_id, pa.sucursal_id, pa.id)
                ORDER BY y.revision DESC LIMIT 1) pv ON true
            JOIN negocio.parada_cliente pc ON (pc.organizacion_id, pc.sucursal_id, pc.version_id) = (pa.organizacion_id, pa.sucursal_id, pv.id)
            JOIN LATERAL (SELECT x.nombre FROM negocio.ruta_version x
                WHERE (x.organizacion_id, x.sucursal_id, x.ruta_id) = (pa.organizacion_id, pa.sucursal_id, pa.ruta_id)
                ORDER BY x.revision DESC LIMIT 1) rv ON true
            WHERE pa.organizacion_id = $1
            ORDER BY rv.nombre, pa.posicion, pa.id
            """, organizacion))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
        {
            while (await r.ReadAsync(ct))
            {
                var clave = (r.GetGuid(0), r.GetGuid(1));
                if (!asignaciones.TryGetValue(clave, out var l)) asignaciones[clave] = l = [];
                l.Add(new AsignacionCliente(r.GetGuid(2), r.GetString(3), r.GetGuid(4), r.GetInt32(5), r.IsDBNull(6) ? null : r.GetInt32(6), r.IsDBNull(7) ? null : r.GetInt32(7), r.GetGuid(8)));
            }
        }

        var lista = new List<ClienteResumen>();
        await using (var cmd = tx.Comando("""
            SELECT c.id, c.sucursal_id, coalesce(f.revision, 0),
                   coalesce(nullif(btrim(f.nombre_comercial), ''), nullif(btrim(f.nombre_fiscal), ''),
                            nullif(btrim(c.codigo_comercial), ''), 'Cliente sin nombre') AS nombre,
                   f.nombre_fiscal, f.nombre_comercial, f.rfc, f.calle, f.numero_exterior, f.colonia,
                   f.codigo_postal, f.municipio, f.estado, f.telefono, f.correo, au.numero
            FROM negocio.cliente c
            LEFT JOIN LATERAL (SELECT * FROM negocio.ficha_empresa x
                WHERE (x.organizacion_id, x.empresa_id) = (c.organizacion_id, c.empresa_id)
                ORDER BY x.revision DESC LIMIT 1) f ON true
            LEFT JOIN LATERAL (SELECT a.numero FROM negocio.autorizacion_empresa a
                WHERE (a.organizacion_id, a.empresa_id) = (c.organizacion_id, c.empresa_id)
                  AND a.autoridad = $2 AND a.finalidad = $3
                ORDER BY a.registrada_en DESC, a.id DESC LIMIT 1) au ON true
            WHERE c.organizacion_id = $1
            ORDER BY nombre, c.id
            """, organizacion, Autoridad, Finalidad))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
        {
            while (await r.ReadAsync(ct))
            {
                string? T(int i) => r.IsDBNull(i) ? null : r.GetString(i);
                var propias = asignaciones.GetValueOrDefault((r.GetGuid(1), r.GetGuid(0))) ?? [];
                lista.Add(new ClienteResumen(
                    r.GetGuid(0), r.GetGuid(1), r.GetInt32(2), r.GetString(3),
                    T(4), T(5), T(6), T(7), T(8), T(9), T(10), T(11), T(12), T(13), T(14), T(15),
                    propias, propias.Sum(a => a.Banos ?? 0), propias.Sum(a => a.Fosas ?? 0), sitios.GetValueOrDefault((r.GetGuid(1), r.GetGuid(0))) ?? []));
            }
        }
        await tx.ConfirmarAsync(ct);
        return lista;
    }

    // ---------------- Escritura ----------------

    public async Task<ResultadoEscritura> GuardarSitiosAsync(Guid organizacion, Guid acceso, Guid cliente, SolicitudSitioCliente[] sitios, CancellationToken ct)
    {
        await using var tx=await TransaccionContexto.AbrirAsync(datos,organizacion,acceso,ct);
        Guid sucursal;
        await using(var cmd=tx.Comando("SELECT sucursal_id FROM negocio.cliente WHERE organizacion_id=$1 AND id=$2 LIMIT 1",organizacion,cliente))
        {
            if(await cmd.ExecuteScalarAsync(ct) is not Guid id)return ResultadoEscritura.NoEncontrada;
            sucursal=id;
        }
        await using(var permiso=tx.Comando("SELECT seguridad.puede($1,$2,'datos.escribir')",organizacion,sucursal))
            if(await permiso.ExecuteScalarAsync(ct) is not true)return ResultadoEscritura.SinPermiso;
        try
        {
            if(await SitiosCliente.GuardarAsync(tx,organizacion,sucursal,cliente,sitios,ct) is {} error)return error;
            await tx.ConfirmarAsync(ct);
            return ResultadoEscritura.Correcta(cliente,0);
        }
        catch(PostgresException e)when(Traducir(e) is {} r){return r;}
    }

    public async Task<ResultadoEscritura> CrearAsync(Guid organizacion, Guid acceso, Guid sucursal, DatosCliente d, CancellationToken ct)
    {
        await using var tx = await TransaccionContexto.AbrirAsync(datos, organizacion, acceso, ct);
        try
        {
            Guid id;
            await using (var cmd = tx.Comando("""
                SELECT negocio.alta_cliente($1, $2, $3::text, $4::text, $5::text, $6::text, $7::text, $8::text,
                                            $9::text, $10::text, $11::text, $12::text, $13::text, $14::text)
                """, [organizacion, sucursal, .. Parametros(d)]))
                id = (Guid)(await cmd.ExecuteScalarAsync(ct))!;
            if (await SitiosCliente.GuardarAsync(tx, organizacion, sucursal, id, d.Sitios, ct) is {} errorSitios) return errorSitios;
            await tx.ConfirmarAsync(ct);
            return ResultadoEscritura.Correcta(id, 1);
        }
        catch (PostgresException e) when (Traducir(e) is { } r) { return r; }
    }

    public async Task<ResultadoEscritura> EditarAsync(Guid organizacion, Guid acceso, Guid cliente, DatosCliente d, int revision,
        string? autorizacionVista, CancellationToken ct)
    {
        await using var tx = await TransaccionContexto.AbrirAsync(datos, organizacion, acceso, ct);
        Guid sucursal, empresa;
        await using (var cmd = tx.Comando("SELECT sucursal_id, empresa_id FROM negocio.cliente WHERE organizacion_id = $1 AND id = $2 LIMIT 1", organizacion, cliente))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
        {
            if (!await r.ReadAsync(ct)) return ResultadoEscritura.NoEncontrada;
            (sucursal, empresa) = (r.GetGuid(0), r.GetGuid(1));
        }
        try
        {
            // Mismo candado que negocio.corregir_cliente (reentrante en la transacción): las comprobaciones
            // de abajo y la corrección ven el mismo estado de la empresa.
            await using (var cmd = tx.Comando("SELECT pg_advisory_xact_lock(hashtextextended('empresa:' || $1::text || ':' || $2::text, 0))",
                             organizacion, empresa))
                await cmd.ExecuteNonQueryAsync(ct);

            // La ficha y la autorización son de la empresa, y una empresa puede estar ligada a varias sucursales:
            // corregirla cambia lo que ven todas. Se exige datos.escribir en cada sucursal ligada que RLS deja ver.
            // Limitación: una sucursal ligada sin datos.leer no es visible para manifiestos_api; cerrarlo del
            // todo requiere que corregir_cliente lo compruebe (migración nueva, pendiente).
            await using (var cmd = tx.Comando("""
                SELECT coalesce(bool_and(coalesce(seguridad.puede(x.organizacion_id, x.sucursal_id, 'datos.escribir'), false)), true)
                FROM negocio.empresa_sucursal x WHERE x.organizacion_id = $1 AND x.empresa_id = $2
                """, organizacion, empresa))
                if (await cmd.ExecuteScalarAsync(ct) is false) return ResultadoEscritura.SinPermiso;

            // Un cliente sin ficha (p. ej. importado solo con código comercial) no se puede corregir con
            // corregir_cliente: no hay ficha de la que partir. (El permiso de su propia sucursal ya se comprobó
            // arriba: el cliente siempre está ligado a ella.)
            await using (var cmd = tx.Comando("SELECT EXISTS (SELECT 1 FROM negocio.ficha_empresa WHERE organizacion_id = $1 AND empresa_id = $2)",
                             organizacion, empresa))
                if (await cmd.ExecuteScalarAsync(ct) is false) return ResultadoEscritura.Invalida(MensajeSinFicha);

            // Cambiar solo el número de autorización no sube la revisión de la ficha: se compara además
            // con la autorización vigente que vio quien edita.
            if (autorizacionVista is not null)
            {
                await using var cmd = tx.Comando("""
                    SELECT a.numero FROM negocio.autorizacion_empresa a
                    WHERE a.organizacion_id = $1 AND a.empresa_id = $2 AND a.autoridad = $3 AND a.finalidad = $4
                    ORDER BY a.registrada_en DESC, a.id DESC LIMIT 1
                    """, organizacion, empresa, Autoridad, Finalidad);
                var vigente = await cmd.ExecuteScalarAsync(ct) as string;
                if (Normalizar(vigente) != Normalizar(autorizacionVista)) return ResultadoEscritura.Conflicto(MensajeClienteModificado);
            }

            int nueva;
            await using (var cmd = tx.Comando("""
                SELECT negocio.corregir_cliente($1, $2, $3, $4, $5::text, $6::text, $7::text, $8::text, $9::text, $10::text,
                                                $11::text, $12::text, $13::text, $14::text, $15::text, $16::text)
                """, [organizacion, sucursal, cliente, revision, .. Parametros(d)]))
                nueva = (int)(await cmd.ExecuteScalarAsync(ct))!;
            if (await SitiosCliente.GuardarAsync(tx, organizacion, sucursal, cliente, d.Sitios, ct) is {} errorSitios) return errorSitios;
            await tx.ConfirmarAsync(ct);
            return ResultadoEscritura.Correcta(cliente, nueva);
        }
        catch (PostgresException e) when (Traducir(e) is { } r) { return r; }
    }

    // ---------------- Comunes ----------------

    private static object[] Parametros(DatosCliente d) =>
    [
        Nulo(d.NombreFiscal), Nulo(d.NombreComercial), Nulo(d.Rfc), Nulo(d.Calle), Nulo(d.NumeroExterior), Nulo(d.Colonia),
        Nulo(d.CodigoPostal), Nulo(d.Municipio), Nulo(d.Estado), Nulo(d.Telefono), Nulo(d.Correo), Nulo(d.Autorizacion),
    ];

    private static object Nulo(string? valor) => (object?)valor ?? DBNull.Value;

    /// <summary>Igual que infra.texto: recortado; vacío = null.</summary>
    private static string? Normalizar(string? valor) => string.IsNullOrWhiteSpace(valor) ? null : valor.Trim();

    /// <summary>
    /// SQLSTATE de las funciones de 0009: 42501 sin permiso, 22023 dato inválido, 40001 revisión vieja,
    /// P0002 cliente no encontrado. 23505 (revisión duplicada por escritura simultánea) también es conflicto.
    /// </summary>
    private static ResultadoEscritura? Traducir(PostgresException e) => e.SqlState switch
    {
        PostgresErrorCodes.InsufficientPrivilege => ResultadoEscritura.SinPermiso,
        PostgresErrorCodes.InvalidParameterValue => ResultadoEscritura.Invalida(
            e.MessageText.Contains("autorización", StringComparison.OrdinalIgnoreCase) ? MensajeQuitarAutorizacion : MensajeSinNombre),
        PostgresErrorCodes.SerializationFailure or PostgresErrorCodes.UniqueViolation => ResultadoEscritura.Conflicto(MensajeClienteModificado),
        PostgresErrorCodes.NoDataFound => ResultadoEscritura.NoEncontrada,
        _ => null,
    };
}
