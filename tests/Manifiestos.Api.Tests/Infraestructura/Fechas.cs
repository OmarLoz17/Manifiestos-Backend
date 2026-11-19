namespace Manifiestos.Api.Tests.Infraestructura;

/// <summary>Fechas FUTURAS para las pruebas de extraordinarias y ajustes (nunca se aceptan en el pasado): el próximo lunes, martes y miércoles.</summary>
public static class Fechas
{
    public static readonly DateOnly ProximoLunes = Calcular();
    public static string Lunes => ProximoLunes.ToString("yyyy-MM-dd");
    public static string Martes => ProximoLunes.AddDays(1).ToString("yyyy-MM-dd");
    public static string Miercoles => ProximoLunes.AddDays(2).ToString("yyyy-MM-dd");

    private static DateOnly Calcular()
    {
        // Mañana o después, que sea lunes (a prueba del reloj local/UTC: siempre queda al menos un día adelante).
        var d = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(2);
        while (d.DayOfWeek != DayOfWeek.Monday) d = d.AddDays(1);
        return d;
    }
}
