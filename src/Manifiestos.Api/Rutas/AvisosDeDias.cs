namespace Manifiestos.Api.Rutas;

/// <summary>
/// Avisos (no bloqueos) cuando dos rutas de la misma unidad, o de un mismo operador en distintas unidades,
/// coinciden en algún día de la semana. Si eso es un error o un caso válido (turnos, visitas extraordinarias)
/// depende de PG-10, así que el sistema avisa pero no impide guardar.
/// </summary>
public static class AvisosDeDias
{
    private static readonly string[] Nombres = ["Lun", "Mar", "Mié", "Jue", "Vie", "Sáb", "Dom"];

    public static string Texto(short mascara) =>
        string.Join(", ", Enumerable.Range(0, 7).Where(d => (mascara & (1 << d)) != 0).Select(d => Nombres[d]));

    /// <summary>Pares de rutas con días en común: «Ruta A y Ruta B coinciden en Lun, Mié».</summary>
    public static IReadOnlyList<string> Coincidencias(IReadOnlyList<(string Ruta, short Dias, string? Unidad)> rutas, bool soloDeUnidadesDistintas = false)
    {
        var avisos = new List<string>();
        for (var i = 0; i < rutas.Count; i++)
            for (var j = i + 1; j < rutas.Count; j++)
            {
                if (soloDeUnidadesDistintas && rutas[i].Unidad == rutas[j].Unidad) continue;
                var comun = (short)(rutas[i].Dias & rutas[j].Dias);
                if (comun == 0) continue;
                string Nombre((string Ruta, short Dias, string? Unidad) r) => soloDeUnidadesDistintas && r.Unidad is not null ? $"{r.Ruta} ({r.Unidad})" : r.Ruta;
                avisos.Add($"{Nombre(rutas[i])} y {Nombre(rutas[j])} coinciden en {Texto(comun)}.");
            }
        return avisos;
    }
}
