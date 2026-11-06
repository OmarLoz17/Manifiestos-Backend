using System.Net;
using Microsoft.AspNetCore.HttpOverrides;

namespace Manifiestos.Api;

/// <summary>
/// Detrás de Cloudflare Tunnel + Nginx la API ve la IP y el esquema del proxy, no los del cliente.
/// Sin esto el límite de intentos agrupa a todos los usuarios bajo una sola IP y el esquema llega como http.
/// Solo se aceptan cabeceras X-Forwarded-For/Proto de proxies declarados: por defecto el propio equipo
/// (loopback); otros se declaran en <c>Manifiestos:ProxyConfiable:Redes</c> como IP o CIDR (p. ej. "172.18.0.0/16").
/// Una entrada inválida detiene el arranque en lugar de ignorarse.
/// </summary>
public static class ProxyConfiable
{
    public const string Seccion = "Manifiestos:ProxyConfiable:Redes";

    public static void Configurar(ForwardedHeadersOptions o, IEnumerable<string> redes)
    {
        o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
        o.ForwardLimit = 1;
        // Los valores por defecto ya confían en loopback; solo se suman las redes declaradas.
        foreach (var texto in redes.Select(r => r.Trim()).Where(r => r.Length > 0))
        {
            var partes = texto.Split('/');
            if (partes.Length > 2 || !IPAddress.TryParse(partes[0], out var ip))
                throw new InvalidOperationException($"Red de proxy no válida en {Seccion}: '{texto}'.");
            if (partes.Length == 1)
            {
                o.KnownProxies.Add(ip);
                continue;
            }
            var max = ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? 32 : 128;
            if (!int.TryParse(partes[1], out var prefijo) || prefijo < 0 || prefijo > max)
                throw new InvalidOperationException($"Prefijo de red de proxy no válido en {Seccion}: '{texto}'.");
            o.KnownIPNetworks.Add(new System.Net.IPNetwork(ip, prefijo));
        }
    }

    public static IServiceCollection AgregarProxyConfiable(this IServiceCollection servicios, IConfiguration configuracion)
    {
        var redes = configuracion.GetSection(Seccion).Get<string[]>() ?? [];
        servicios.Configure<ForwardedHeadersOptions>(o => Configurar(o, redes));
        return servicios;
    }
}
