using System.Collections.Concurrent;
using System.Text;
using Manifiestos.Api.Acceso;

namespace Manifiestos.Api.Registro;

/// <summary>
/// Registro del backend en archivos de texto, uno por día (`manifiestos-AAAA-MM-DD.log`, hora UTC), con retención de 30 días: al arrancar y al cambiar de día
/// se borran los archivos más viejos. Sin datos personales ni secretos: solo nivel, categoría, mensaje y excepción. Ruta en `Registro:Ruta`
/// (por omisión, `logs` junto a la aplicación; en desarrollo, `backend/.local/logs`).
/// </summary>
public sealed class ProveedorRegistroArchivo : ILoggerProvider
{
    public const int DiasDeRetencion = 30;
    private readonly string _ruta;
    private readonly Func<DateTime> _ahora;
    private readonly object _candado = new();
    private DateOnly _diaLimpiado;
    private readonly ConcurrentDictionary<string, ILogger> _registradores = new();

    public ProveedorRegistroArchivo(string ruta, Func<DateTime>? ahora = null)
    {
        _ruta = ruta;
        _ahora = ahora ?? (() => DateTime.UtcNow);
        Directory.CreateDirectory(_ruta);
        Limpiar(DateOnly.FromDateTime(_ahora()));
    }

    public static string ArchivoDe(DateOnly dia) => $"manifiestos-{dia:yyyy-MM-dd}.log";

    /// <summary>Borra los archivos de registro de hace más de 30 días (por la fecha de su nombre). No toca archivos que no sean de este registro.</summary>
    public void Limpiar(DateOnly hoy)
    {
        lock (_candado)
        {
            _diaLimpiado = hoy;
            foreach (var archivo in Directory.EnumerateFiles(_ruta, "manifiestos-*.log"))
            {
                var nombre = Path.GetFileNameWithoutExtension(archivo)["manifiestos-".Length..];
                if (DateOnly.TryParseExact(nombre, "yyyy-MM-dd", out var dia) && dia < hoy.AddDays(-DiasDeRetencion))
                {
                    try { File.Delete(archivo); } catch (IOException) { /* en uso: se borra en la siguiente limpieza */ }
                }
            }
        }
    }

    public ILogger CreateLogger(string categoria) => _registradores.GetOrAdd(categoria, c => new RegistradorArchivo(this, c));
    public void Dispose() { }

    private void Escribir(string categoria, LogLevel nivel, string mensaje, Exception? excepcion)
    {
        var ahora = _ahora();
        var dia = DateOnly.FromDateTime(ahora);
        var sb = new StringBuilder().Append(ahora.ToString("yyyy-MM-ddTHH:mm:ss.fffZ")).Append(" [").Append(nivel).Append("] ").Append(categoria).Append(": ").AppendLine(mensaje);
        if (excepcion is not null) sb.AppendLine(excepcion.ToString());
        lock (_candado)
        {
            if (dia != _diaLimpiado) Limpiar(dia);
            try { File.AppendAllText(Path.Combine(_ruta, ArchivoDe(dia)), sb.ToString(), Encoding.UTF8); }
            catch (IOException) { /* el registro nunca tumba una petición */ }
        }
    }

    private sealed class RegistradorArchivo(ProveedorRegistroArchivo padre, string categoria) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState estado) where TState : notnull => null;
        public bool IsEnabled(LogLevel nivel) => nivel != LogLevel.None;
        public void Log<TState>(LogLevel nivel, EventId id, TState estado, Exception? excepcion, Func<TState, Exception?, string> formato)
        {
            if (!IsEnabled(nivel)) return;
            padre.Escribir(categoria, nivel, formato(estado, excepcion), excepcion);
        }
    }
}

public sealed record ErrorDelCliente(string? Mensaje, string? Pila, string? Ruta);

public static class ServiciosRegistro
{
    /// <summary>Agrega el registro en archivo (con retención de 30 días) a los proveedores de registro de la aplicación.</summary>
    public static void AgregarRegistroEnArchivo(this WebApplicationBuilder builder)
    {
        var ruta = builder.Configuration["Registro:Ruta"]
            ?? (builder.Environment.IsDevelopment() ? Path.GetFullPath(Path.Combine(builder.Environment.ContentRootPath, "..", "..", ".local", "logs")) : Path.Combine(builder.Environment.ContentRootPath, "logs"));
        builder.Logging.AddProvider(new ProveedorRegistroArchivo(ruta));
    }

    /// <summary>
    /// POST /api/errores-cliente: la pantalla de error del panel avisa aquí lo que falló para que quede en el registro. Sin sesión (el error puede ocurrir antes de entrar),
    /// con límite por IP y con textos acotados; solo se escribe el mensaje, la pila y la ruta de la pantalla.
    /// </summary>
    public static void MapearErroresDelCliente(this IEndpointRouteBuilder app) =>
        app.MapPost("/api/errores-cliente", (ErrorDelCliente e, ILoggerFactory logs) =>
        {
            static string Linea(string? t, int max) { t ??= ""; t = t.Replace('\r', ' ').Replace('\n', ' '); return t.Length > max ? t[..max] : t; }
            // La pila conserva sus saltos pero cada línea va sangrada: nadie puede inventar entradas del registro con un texto enviado desde fuera.
            static string Pila(string? t) { t ??= ""; t = t.Length > 4000 ? t[..4000] : t; return string.Join("\n", t.Replace("\r", "").Split('\n').Select(l => "  " + l)); }
            logs.CreateLogger("Panel").LogError("Error en el panel (pantalla {Ruta}): {Mensaje}\n{Pila}", Linea(e.Ruta, 200), Linea(e.Mensaje, 500), Pila(e.Pila));
            return Results.NoContent();
        }).AllowAnonymous().RequireRateLimiting(ServiciosAcceso.PoliticaValidacion);
}
