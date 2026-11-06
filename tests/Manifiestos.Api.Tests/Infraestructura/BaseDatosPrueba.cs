using System.Diagnostics;

namespace Manifiestos.Api.Tests.Infraestructura;

/// <summary>
/// Base temporal manifiestos_test_&lt;guid&gt; en el PostgreSQL del compose del proyecto,
/// con todas las migraciones. Se crea por clase de prueba y se elimina al terminar;
/// nunca toca la base "manifiestos". La API se conecta como manifiestos_api.
/// Requiere Docker Desktop con el servicio postgres en marcha y Windows PowerShell.
/// </summary>
public sealed class BaseDatosPrueba : IAsyncLifetime
{
    public static readonly Guid Organizacion = Guid.Parse("0f000000-0000-4000-8000-000000000001");
    public static readonly Guid Sucursal = Guid.Parse("0f000000-0000-4000-8000-000000000003");
    public const string NombreSucursal = "Sucursal ficticia de prueba";

    private readonly string _raiz = BuscarRaiz();
    public string Nombre { get; } = "manifiestos_test_" + Guid.NewGuid().ToString("N");
    public string CadenaConexion { get; private set; } = "";
    private bool _creada;

    public async Task InitializeAsync()
    {
        await DockerAsync($"exec -T postgres createdb -U manifiestos_owner {Nombre}");
        _creada = true;
        await EjecutarAsync("powershell.exe",
            $"-NoProfile -ExecutionPolicy Bypass -File \"{Path.Combine(_raiz, "database", "Migrate.ps1")}\" -Database {Nombre}");
        var clave = Path.Combine(_raiz, ".local", "api_password");
        if (!File.Exists(clave))
            await EjecutarAsync("powershell.exe",
                $"-NoProfile -ExecutionPolicy Bypass -File \"{Path.Combine(_raiz, "database", "Initialize-ApiRole.ps1")}\"");
        CadenaConexion = $"Host=127.0.0.1;Port=55432;Database={Nombre};Username=manifiestos_api;" +
                         $"Password={File.ReadAllText(clave).Trim()};Pooling=false";
        await SqlComoDuenoAsync($"""
            INSERT INTO negocio.organizacion VALUES ('{Organizacion}', 'Grupo Ficticio de Prueba');
            INSERT INTO negocio.empresa VALUES ('{Organizacion}', '0f000000-0000-4000-8000-000000000002', 'EMP-PRUEBA');
            INSERT INTO negocio.sucursal(organizacion_id, id, empresa_id, codigo, nombre)
            VALUES ('{Organizacion}', '{Sucursal}', '0f000000-0000-4000-8000-000000000002', 'PRUEBA', '{NombreSucursal}');
            """);
    }

    public async Task DisposeAsync()
    {
        if (_creada) await DockerAsync($"exec -T postgres dropdb --force -U manifiestos_owner {Nombre}");
    }

    /// <summary>Alta de una entrada de la lista, como lo haría Administración.</summary>
    public Task AutorizarAsync(string correo, string? rol = "operaciones", bool activo = true, bool conSucursal = true, bool escribir = false) =>
        SqlComoDuenoAsync($"""
            WITH a AS (
                INSERT INTO seguridad.acceso_autorizado(organizacion_id, correo_permitido, activo, rol_codigo)
                VALUES ('{Organizacion}', '{correo}', {activo}, {(rol is null ? "NULL" : $"'{rol}'")}) RETURNING id)
            INSERT INTO seguridad.permiso_sucursal
            SELECT '{Organizacion}', '{Sucursal}', a.id, p FROM a,
                unnest(CASE WHEN {escribir} THEN ARRAY['datos.leer','datos.escribir'] ELSE ARRAY['datos.leer'] END) p
            WHERE {conSucursal};
            """);

    public Task DesactivarAsync(string correo) =>
        SqlComoDuenoAsync($"UPDATE seguridad.acceso_autorizado SET activo = false WHERE correo_permitido = '{correo}';");

    public async Task<string> ConsultarAsync(string sql) =>
        (await DockerAsync($"exec -T postgres psql -X -At -v ON_ERROR_STOP=1 -U manifiestos_owner -d {Nombre} -c \"{sql}\"")).Trim();

    public Task SqlComoDuenoAsync(string sql) =>
        DockerAsync($"exec -T postgres psql -X -q -v ON_ERROR_STOP=1 -U manifiestos_owner -d {Nombre}", sql);

    private Task<string> DockerAsync(string args, string? entrada = null) =>
        EjecutarAsync("docker", $"compose -f \"{Path.Combine(_raiz, "compose.yaml")}\" {args}", entrada);

    private static async Task<string> EjecutarAsync(string programa, string args, string? entrada = null)
    {
        var psi = new ProcessStartInfo(programa, args)
        {
            RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = entrada is not null,
            UseShellExecute = false, CreateNoWindow = true,
            // psql espera UTF-8; sin esto Windows usa la página de códigos de la consola (acentos ficticios fallan).
            StandardInputEncoding = entrada is null ? null : new System.Text.UTF8Encoding(false),
            StandardOutputEncoding = System.Text.Encoding.UTF8,
        };
        // Windows PowerShell 5.1 debe resolver sus propios módulos, aunque el proceso padre
        // se haya iniciado desde PowerShell 7 con una ruta de módulos incompatible.
        if (programa.Equals("powershell.exe", StringComparison.OrdinalIgnoreCase))
            psi.Environment.Remove("PSModulePath");
        using var p = Process.Start(psi) ?? throw new InvalidOperationException($"No se pudo iniciar {programa}");
        if (entrada is not null)
        {
            await p.StandardInput.WriteAsync(entrada);
            p.StandardInput.Close();
        }
        var salida = p.StandardOutput.ReadToEndAsync();
        var error = p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync();
        if (p.ExitCode != 0)
            throw new InvalidOperationException($"{programa} terminó con {p.ExitCode}: {await error}");
        return await salida;
    }

    private static string BuscarRaiz()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "compose.yaml"))) return d.FullName;
        throw new InvalidOperationException("No se encontró compose.yaml en las carpetas superiores.");
    }
}
