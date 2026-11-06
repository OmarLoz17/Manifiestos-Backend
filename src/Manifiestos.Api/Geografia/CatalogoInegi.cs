using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Caching.Memory;

namespace Manifiestos.Api.Geografia;

public sealed record OpcionGeografica(string Clave, string Nombre);

/// <summary>Catálogos públicos en memoria; nunca copia el catálogo a PostgreSQL.</summary>
public sealed class CatalogoInegi(HttpClient http, IMemoryCache cache)
{
    public async Task<OpcionGeografica[]> ConsultarAsync(string catalogo, string? estado, string? municipio, CancellationToken ct)
    {
        var ruta = Ruta(catalogo, estado, municipio);
        if (cache.TryGetValue<OpcionGeografica[]>(ruta, out var guardado)) return guardado!;
        using var respuesta = await http.GetAsync(ruta, ct);
        respuesta.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await respuesta.Content.ReadAsStringAsync(ct));
        if (!json.RootElement.TryGetProperty("datos", out var datos) || datos.ValueKind != JsonValueKind.Array)
            throw new JsonException("Respuesta de catálogo INEGI inesperada.");
        var campo = catalogo switch { "colonias" => "nom_asen", "calles" => "nomvial", _ => "nomgeo" };
        var opciones = datos.EnumerateArray().Select(d =>
        {
            var nombre = d.GetProperty(campo).GetString()?.Trim() ?? "";
            if (catalogo == "calles" && d.TryGetProperty("tipovial", out var tipo))
            {
                var prefijo = tipo.GetString()?.Trim();
                if (!string.IsNullOrEmpty(prefijo) && !prefijo.Equals("NINGUNO", StringComparison.OrdinalIgnoreCase))
                    nombre = $"{prefijo} {nombre}";
            }
            var clave = catalogo switch
            {
                "estados" => d.GetProperty("cve_ent").GetString()!,
                "municipios" => d.GetProperty("cve_mun").GetString()!,
                _ => nombre,
            };
            return new OpcionGeografica(clave, nombre);
        }).Where(o => o.Nombre.Length > 0).DistinctBy(o => o.Clave)
          .OrderBy(o => o.Nombre, StringComparer.Create(new System.Globalization.CultureInfo("es-MX"), true)).ToArray();
        cache.Set(ruta, opciones, TimeSpan.FromHours(6));
        return opciones;
    }

    public static string Ruta(string catalogo, string? estado, string? municipio)
    {
        if (catalogo == "estados") return "mgee";
        if (estado is null || !Regex.IsMatch(estado, "^(0[1-9]|[12][0-9]|3[0-2])$"))
            throw new ArgumentException("Elige un estado válido.");
        if (catalogo == "municipios") return $"mgem/{estado}";
        if (municipio is null || !Regex.IsMatch(municipio, "^[0-9]{3}$") || municipio == "000")
            throw new ArgumentException("Elige un municipio válido.");
        return catalogo switch
        {
            "colonias" => $"asentamientos/{estado}/{municipio}",
            "calles" => $"vialidades/{estado}/{municipio}",
            _ => throw new ArgumentException("Catálogo no permitido."),
        };
    }
}

public static class EndpointsGeografia
{
    public static IServiceCollection AgregarGeografia(this IServiceCollection servicios)
    {
        servicios.AddMemoryCache();
        servicios.AddSingleton<CatalogoPostal>();
        servicios.AddHttpClient("sepomex",http=>{http.Timeout=TimeSpan.FromSeconds(45);http.MaxResponseContentBufferSize=20*1024*1024;});
        servicios.AddHttpClient<CatalogoInegi>(http =>
        {
            http.BaseAddress = new Uri("https://gaia.inegi.org.mx/wscatgeo/v2/");
            http.Timeout = TimeSpan.FromSeconds(15);
        });
        return servicios;
    }

    public static void MapearGeografia(this WebApplication app)
    {
        app.MapGet("/api/geografia/codigos-postales/{cp}",async Task<IResult>(string cp,CatalogoPostal postal,CancellationToken ct)=>
        {
            try
            {
                var datos=await postal.ConsultarAsync(cp,ct);
                return datos.Length>0?Results.Ok(datos):Results.NotFound(new {mensaje="No se encontró ese código postal. Revísalo antes de continuar."});
            }
            catch(ArgumentException e){return Results.BadRequest(new {mensaje=e.Message});}
            catch(Exception e)when(e is HttpRequestException or IOException || e is OperationCanceledException&&!ct.IsCancellationRequested)
            {return Results.Json(new {mensaje="No se pudo consultar el catálogo postal. Reintenta la consulta para desbloquear la dirección."},statusCode:503);}
        }).RequireAuthorization();
        app.MapGet("/api/geografia/{catalogo}", async (string catalogo, string? estado, string? municipio,
            CatalogoInegi inegi, CancellationToken ct) =>
        {
            try { return Results.Ok(await inegi.ConsultarAsync(catalogo, estado, municipio, ct)); }
            catch (ArgumentException e) { return Results.BadRequest(new { mensaje = e.Message }); }
            catch (Exception e) when (e is HttpRequestException or JsonException or KeyNotFoundException || e is OperationCanceledException && !ct.IsCancellationRequested)
            {
                return Results.Json(new { mensaje = "INEGI no está disponible. Puedes conservar o escribir la dirección y volver a intentar la consulta." }, statusCode: 503);
            }
        }).RequireAuthorization();
    }
}
