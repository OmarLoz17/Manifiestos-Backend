using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Caching.Memory;

namespace Manifiestos.Api.Geografia;

public sealed record UbicacionPostal(string EstadoId,string Estado,string MunicipioId,string Municipio,string Colonia);

/// <summary>Descarga oficial gratuita de SEPOMEX. Catálogo solo en memoria, sin tablas ni archivos de aplicación.</summary>
public sealed class CatalogoPostal(IHttpClientFactory clientes,IMemoryCache cache)
{
    public const string Fuente="https://www.correosdemexico.gob.mx/sslservicios/consultacp/CodigoPostal_Exportar.aspx";
    private readonly SemaphoreSlim descarga=new(1,1);
    private const string ClaveCache="sepomex:catalogo";

    public async Task<UbicacionPostal[]> ConsultarAsync(string cp,CancellationToken ct)
    {
        if(!Regex.IsMatch(cp,"^[0-9]{5}$"))throw new ArgumentException("Escribe un código postal de 5 dígitos.");
        if(cache.TryGetValue<Dictionary<string,UbicacionPostal[]>>(ClaveCache,out var vigente))return vigente!.GetValueOrDefault(cp)??[];
        await descarga.WaitAsync(ct);
        try
        {
            if(cache.TryGetValue<Dictionary<string,UbicacionPostal[]>>(ClaveCache,out vigente))return vigente!.GetValueOrDefault(cp)??[];
            using var http=clientes.CreateClient("sepomex");
            var html=await http.GetStringAsync(Fuente,ct);
            using var formulario=new FormUrlEncodedContent(Formulario(html));
            using var respuesta=await http.PostAsync(Fuente,formulario,ct);
            respuesta.EnsureSuccessStatusCode();
            await using var contenido=await respuesta.Content.ReadAsStreamAsync(ct);
            using var zip=new ZipArchive(contenido,ZipArchiveMode.Read);
            var entrada=zip.Entries.SingleOrDefault(e=>e.Name.Equals("CPdescarga.txt",StringComparison.OrdinalIgnoreCase))
                ??throw new InvalidDataException("No se recibió el catálogo postal TXT esperado.");
            if(entrada.Length>64*1024*1024)throw new InvalidDataException("Catálogo postal fuera del tamaño permitido.");
            using var lector=new StreamReader(entrada.Open(),Encoding.Latin1,detectEncodingFromByteOrderMarks:true);
            var catalogo=await LeerAsync(lector,ct);
            cache.Set(ClaveCache,catalogo,TimeSpan.FromHours(24));
            return catalogo.GetValueOrDefault(cp)??[];
        }
        finally{descarga.Release();}
    }

    public static Dictionary<string,string> Formulario(string html)
    {
        var valores=new Dictionary<string,string>();
        foreach(Match input in Regex.Matches(html,"<input\\b[^>]*>",RegexOptions.IgnoreCase))
        {
            if(!Regex.IsMatch(input.Value,"type=\"hidden\"",RegexOptions.IgnoreCase))continue;
            var nombre=Regex.Match(input.Value,"name=\"([^\"]+)\"",RegexOptions.IgnoreCase).Groups[1].Value;
            var valor=Regex.Match(input.Value,"value=\"([^\"]*)\"",RegexOptions.IgnoreCase).Groups[1].Value;
            if(nombre.Length>0)valores[nombre]=WebUtility.HtmlDecode(valor);
        }
        if(!valores.ContainsKey("__VIEWSTATE"))throw new InvalidDataException("Cambió el formulario de descarga postal.");
        valores["cboEdo"]="00";valores["rblTipo"]="txt";
        valores["btnDescarga.x"]="10";valores["btnDescarga.y"]="10";
        return valores;
    }

    public static async Task<Dictionary<string,UbicacionPostal[]>> LeerAsync(TextReader lector,CancellationToken ct)
    {
        string? linea;
        string[]? encabezado=null;
        while((linea=await lector.ReadLineAsync(ct)) is not null)
            if(linea.StartsWith("d_codigo|",StringComparison.OrdinalIgnoreCase)){encabezado=linea.Split('|');break;}
        if(encabezado is null)throw new InvalidDataException("Falta el encabezado del catálogo postal.");
        int I(string campo){var i=Array.FindIndex(encabezado,c=>c.Equals(campo,StringComparison.OrdinalIgnoreCase));return i>=0?i:throw new InvalidDataException("Falta un campo postal esperado.");}
        var cp=I("d_codigo");var colonia=I("d_asenta");var estado=I("d_estado");var municipio=I("D_mnpio");var eid=I("c_estado");var mid=I("c_mnpio");
        var grupos=new Dictionary<string,HashSet<UbicacionPostal>>();
        while((linea=await lector.ReadLineAsync(ct)) is not null)
        {
            var c=linea.Split('|');
            if(c.Length!=encabezado.Length||!Regex.IsMatch(c[cp],"^[0-9]{5}$"))continue;
            var dato=new UbicacionPostal(c[eid].PadLeft(2,'0'),c[estado],c[mid].PadLeft(3,'0'),c[municipio],c[colonia]);
            if(!grupos.TryGetValue(c[cp],out var lista))grupos[c[cp]]=lista=[];
            lista.Add(dato);
        }
        if(grupos.Count==0)throw new InvalidDataException("El catálogo postal está vacío.");
        return grupos.ToDictionary(p=>p.Key,p=>p.Value.OrderBy(v=>v.Colonia,StringComparer.Create(new System.Globalization.CultureInfo("es-MX"),true)).ToArray());
    }
}
