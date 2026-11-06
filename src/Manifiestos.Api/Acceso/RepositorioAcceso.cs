using Manifiestos.Api.Datos;
using Npgsql;

namespace Manifiestos.Api.Acceso;

public sealed record ResultadoInicio(string Resultado, Guid? AccesoId)
{
    public bool Aceptado => Resultado == "aceptado" && AccesoId is not null;
}

public sealed record SesionVigente(string Correo, string RolCodigo, string RolNombre);

public sealed record SucursalVisible(Guid Id, string Nombre);

public interface IRepositorioAcceso
{
    Task<ResultadoInicio> IniciarSesionWebAsync(Guid organizacion, IdentidadGoogle identidad, CancellationToken ct);
    Task<SesionVigente?> ObtenerSesionAsync(Guid organizacion, Guid acceso, CancellationToken ct);
    Task<IReadOnlyList<SucursalVisible>> ListarSucursalesAsync(Guid organizacion, Guid acceso, CancellationToken ct);
}

/// <summary>
/// Acceso a PostgreSQL con el usuario manifiestos_api. La lógica de vínculo y
/// revalidación vive en las funciones de la migración 0006.
/// </summary>
public sealed class RepositorioAcceso(NpgsqlDataSource datos) : IRepositorioAcceso
{
    public async Task<ResultadoInicio> IniciarSesionWebAsync(Guid organizacion, IdentidadGoogle identidad, CancellationToken ct)
    {
        await using var cmd = datos.CreateCommand(
            "SELECT resultado_login, acceso FROM seguridad.iniciar_sesion_web($1, $2, $3, $4)");
        cmd.Parameters.AddWithValue(organizacion);
        cmd.Parameters.AddWithValue(identidad.Emisor);
        cmd.Parameters.AddWithValue(identidad.Sujeto);
        cmd.Parameters.AddWithValue(identidad.Correo);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        await r.ReadAsync(ct);
        return new ResultadoInicio(r.GetString(0), r.IsDBNull(1) ? null : r.GetGuid(1));
    }

    public async Task<SesionVigente?> ObtenerSesionAsync(Guid organizacion, Guid acceso, CancellationToken ct)
    {
        await using var cmd = datos.CreateCommand(
            "SELECT correo, rol_codigo, rol_nombre FROM seguridad.sesion_web($1, $2)");
        cmd.Parameters.AddWithValue(organizacion);
        cmd.Parameters.AddWithValue(acceso);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? new SesionVigente(r.GetString(0), r.GetString(1), r.GetString(2)) : null;
    }

    public async Task<IReadOnlyList<SucursalVisible>> ListarSucursalesAsync(Guid organizacion, Guid acceso, CancellationToken ct)
    {
        await using var tx = await TransaccionContexto.AbrirAsync(datos, organizacion, acceso, ct);
        var lista = new List<SucursalVisible>();
        await using (var cmd = tx.Comando("SELECT id, nombre FROM negocio.sucursal ORDER BY nombre"))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
        {
            while (await r.ReadAsync(ct)) lista.Add(new SucursalVisible(r.GetGuid(0), r.GetString(1)));
        }
        await tx.ConfirmarAsync(ct);
        return lista;
    }
}
