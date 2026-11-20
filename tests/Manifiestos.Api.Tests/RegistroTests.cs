using Manifiestos.Api.Registro;
using Microsoft.Extensions.Logging;

namespace Manifiestos.Api.Tests;

public sealed class RegistroTests : IDisposable
{
    private readonly string _ruta = Path.Combine(Path.GetTempPath(), "registro-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { if (Directory.Exists(_ruta)) Directory.Delete(_ruta, true); }

    private void Crear(DateOnly dia) { Directory.CreateDirectory(_ruta); File.WriteAllText(Path.Combine(_ruta, ProveedorRegistroArchivo.ArchivoDe(dia)), "viejo"); }

    [Fact]
    public void Borra_los_archivos_de_mas_de_30_dias_al_arrancar_y_conserva_el_resto()
    {
        var hoy = new DateOnly(2026, 11, 20);
        Crear(hoy.AddDays(-31)); Crear(hoy.AddDays(-30)); Crear(hoy.AddDays(-5));
        File.WriteAllText(Path.Combine(_ruta, "otro.txt"), "ajeno");
        using var proveedor = new ProveedorRegistroArchivo(_ruta, () => new DateTime(2026, 11, 20, 12, 0, 0, DateTimeKind.Utc));
        var nombres = Directory.GetFiles(_ruta).Select(Path.GetFileName).Order().ToArray();
        Assert.DoesNotContain(ProveedorRegistroArchivo.ArchivoDe(hoy.AddDays(-31)), nombres);
        Assert.Contains(ProveedorRegistroArchivo.ArchivoDe(hoy.AddDays(-30)), nombres);   // exactamente 30 días todavía se conserva
        Assert.Contains(ProveedorRegistroArchivo.ArchivoDe(hoy.AddDays(-5)), nombres);
        Assert.Contains("otro.txt", nombres);                                              // nunca toca lo que no es de este registro
    }

    [Fact]
    public void Escribe_un_archivo_por_dia_con_nivel_categoria_y_excepcion_y_limpia_al_cambiar_de_dia()
    {
        var ahora = new DateTime(2026, 11, 20, 23, 59, 0, DateTimeKind.Utc);
        Crear(new DateOnly(2026, 10, 25));   // 26 días antes de hoy: se conserva hoy, pero al día siguiente (31 días) se borra
        using var proveedor = new ProveedorRegistroArchivo(_ruta, () => ahora);
        var log = proveedor.CreateLogger("Prueba");
        log.LogError(new InvalidOperationException("falló algo"), "Algo falló {Dato}", 42);
        var hoy = File.ReadAllText(Path.Combine(_ruta, "manifiestos-2026-11-20.log"));
        Assert.Contains("[Error] Prueba: Algo falló 42", hoy);
        Assert.Contains("InvalidOperationException: falló algo", hoy);
        Assert.True(File.Exists(Path.Combine(_ruta, "manifiestos-2026-10-25.log")));
        ahora = new DateTime(2026, 11, 25, 0, 1, 0, DateTimeKind.Utc);   // otro día: archivo nuevo y limpieza
        log.LogInformation("Nuevo día");
        Assert.True(File.Exists(Path.Combine(_ruta, "manifiestos-2026-11-25.log")));
        Assert.False(File.Exists(Path.Combine(_ruta, "manifiestos-2026-10-25.log")));
    }
}
