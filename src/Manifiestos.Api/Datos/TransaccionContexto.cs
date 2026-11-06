using Npgsql;

namespace Manifiestos.Api.Datos;

/// <summary>
/// Conexión y transacción con el contexto de la sesión fijado con set_config local:
/// las políticas RLS filtran por organización/acceso y el contexto desaparece al terminar.
/// </summary>
public sealed class TransaccionContexto : IAsyncDisposable
{
    private readonly NpgsqlConnection _conexion;
    private readonly NpgsqlTransaction _transaccion;

    private TransaccionContexto(NpgsqlConnection conexion, NpgsqlTransaction transaccion)
    {
        _conexion = conexion;
        _transaccion = transaccion;
    }

    public static async Task<TransaccionContexto> AbrirAsync(NpgsqlDataSource datos, Guid organizacion, Guid acceso, CancellationToken ct)
    {
        var con = await datos.OpenConnectionAsync(ct);
        try
        {
            var tx = await con.BeginTransactionAsync(ct);
            await using var cmd = new NpgsqlCommand(
                "SELECT set_config('app.organizacion_id', $1, true), set_config('app.acceso_id', $2, true)", con, tx);
            cmd.Parameters.AddWithValue(organizacion.ToString());
            cmd.Parameters.AddWithValue(acceso.ToString());
            await cmd.ExecuteNonQueryAsync(ct);
            return new TransaccionContexto(con, tx);
        }
        catch
        {
            await con.DisposeAsync();
            throw;
        }
    }

    public NpgsqlCommand Comando(string sql, params object[] parametros)
    {
        var cmd = new NpgsqlCommand(sql, _conexion, _transaccion);
        foreach (var p in parametros) cmd.Parameters.AddWithValue(p);
        return cmd;
    }

    public Task ConfirmarAsync(CancellationToken ct) => _transaccion.CommitAsync(ct);

    public async ValueTask DisposeAsync()
    {
        await _transaccion.DisposeAsync();
        await _conexion.DisposeAsync();
    }
}
