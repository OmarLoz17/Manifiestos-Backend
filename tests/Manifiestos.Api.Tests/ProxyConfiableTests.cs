using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.HttpOverrides;

namespace Manifiestos.Api.Tests;

public class ProxyConfiableTests
{
    [Fact]
    public void Acepta_ip_y_cidr_declarados_y_solo_lee_for_y_proto()
    {
        var o = new ForwardedHeadersOptions();
        ProxyConfiable.Configurar(o, ["10.1.2.3", " 172.18.0.0/16 ", ""]);

        Assert.Equal(ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto, o.ForwardedHeaders);
        Assert.Equal(1, o.ForwardLimit);
        Assert.Contains(IPAddress.Parse("10.1.2.3"), o.KnownProxies);
        Assert.Contains(o.KnownIPNetworks, n => n.Contains(IPAddress.Parse("172.18.5.9")));
        Assert.DoesNotContain(o.KnownIPNetworks, n => n.Contains(IPAddress.Parse("8.8.8.8")));
    }

    [Theory]
    [InlineData("no-es-ip")]
    [InlineData("10.0.0.0/33")]
    [InlineData("10.0.0.0/x")]
    [InlineData("10.0.0.0/8/8")]
    public void Una_red_invalida_detiene_el_arranque(string red)
    {
        Assert.Throws<InvalidOperationException>(() => ProxyConfiable.Configurar(new ForwardedHeadersOptions(), [red]));
    }
}
