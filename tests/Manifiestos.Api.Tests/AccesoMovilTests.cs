using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Manifiestos.Api.Acceso;
using Manifiestos.Api.Tests.Infraestructura;
using Microsoft.AspNetCore.Authentication.BearerToken;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Manifiestos.Api.Tests;

[Trait("Categoria","Integracion")]
public sealed class AccesoMovilTests(BaseDatosPrueba bd) : IClassFixture<BaseDatosPrueba>,IAsyncLifetime
{
    private ApiPrueba _api=null!;
    public Task InitializeAsync() { _api=new ApiPrueba(bd); return Task.CompletedTask; }
    public async Task DisposeAsync() => await _api.DisposeAsync();
    private sealed record Tokens(string TokenType,string AccessToken,int ExpiresIn,string RefreshToken);
    private sealed record Equipo(string Correo,string Sujeto,Guid Persona,Guid Instalacion,Guid Dispositivo);
    private async Task<Equipo> PrepararAsync(string rol="telefono_operador",bool habilitada=true)
    {
        var e=new Equipo($"movil-{Guid.NewGuid():N}@example.invalid",$"s-{Guid.NewGuid():N}",Guid.NewGuid(),Guid.NewGuid(),Guid.NewGuid());
        await bd.AutorizarAsync(e.Correo,rol,conSucursal:rol=="operaciones");
        await bd.SqlComoDuenoAsync($"""
            INSERT INTO seguridad.persona(organizacion_id,id,nombre_completo) VALUES('{BaseDatosPrueba.Organizacion}','{e.Persona}','Operador sintético');
            INSERT INTO negocio.operador_ficha(organizacion_id,sucursal_id,persona_id,revision,nombre_completo,correo)
            VALUES('{BaseDatosPrueba.Organizacion}','{BaseDatosPrueba.Sucursal}','{e.Persona}',1,'Operador sintético','{e.Correo}');
            INSERT INTO seguridad.dispositivo(organizacion_id,id) VALUES('{BaseDatosPrueba.Organizacion}','{e.Dispositivo}');
            INSERT INTO seguridad.instalacion_app(organizacion_id,id,dispositivo_id,habilitada)
            VALUES('{BaseDatosPrueba.Organizacion}','{e.Instalacion}','{e.Dispositivo}',{habilitada});
            INSERT INTO seguridad.asignacion_equipo(organizacion_id,sucursal_id,dispositivo_id,acceso_id,persona_id,revision,desde)
            SELECT organizacion_id,'{BaseDatosPrueba.Sucursal}','{e.Dispositivo}',id,'{e.Persona}',1,now()-interval '1 minute'
            FROM seguridad.acceso_autorizado WHERE correo_permitido='{e.Correo}';
            """);
        return e;
    }
    private async Task<DesafioMovil> DesafioAsync(HttpClient cliente,Equipo e) =>
        (await (await cliente.PostAsJsonAsync("/api/movil/auth/desafio",new {instalacionId=e.Instalacion})).Content.ReadFromJsonAsync<DesafioMovil>())!;
    private async Task<HttpResponseMessage> EntrarAsync(HttpClient cliente,Equipo e,bool verificado=true,string? nonceExtra=null)
    {
        var d=await DesafioAsync(cliente,e);
        return await cliente.PostAsJsonAsync("/api/movil/auth/google",new SolicitudGoogleMovil(
            ValidadorFicticio.Token(e.Sujeto,e.Correo,verificado,nonce:nonceExtra??d.Nonce),e.Instalacion,d.Id));
    }
    private async Task<(HttpClient Cliente,Tokens Tokens)> SesionAsync(Equipo e)
    {
        var cliente=_api.NuevoNavegador(); var r=await EntrarAsync(cliente,e);
        Assert.Equal(HttpStatusCode.OK,r.StatusCode);
        Assert.False(r.Headers.Contains("Set-Cookie"));
        var tokens=(await r.Content.ReadFromJsonAsync<Tokens>())!;
        cliente.DefaultRequestHeaders.Authorization=new AuthenticationHeaderValue("Bearer",tokens.AccessToken);
        return(cliente,tokens);
    }
    [Fact] public async Task Operador_entra_solo_a_movil_sin_permisos_de_panel()
    {
        var e=await PrepararAsync(); var (cliente,tokens)=await SesionAsync(e);
        Assert.Equal("Bearer",tokens.TokenType); Assert.Equal(900,tokens.ExpiresIn);
        var yo=(await cliente.GetFromJsonAsync<UsuarioMovil>("/api/movil/auth/me"))!;
        Assert.Equal(e.Persona,yo.Operador.PersonaId); Assert.Equal(e.Instalacion,yo.Operador.InstalacionId);
        Assert.Equal("Operador sintético",yo.Operador.Nombre); Assert.Equal("telefono_operador",yo.Rol);
        foreach (var ruta in new[]{"/api/auth/me","/api/clientes","/api/ordenes","/api/operadores","/api/jornadas","/api/rutas"})
            Assert.Equal(HttpStatusCode.Unauthorized,(await cliente.GetAsync(ruta)).StatusCode);
        Assert.Equal("0",await bd.ConsultarAsync($"SELECT count(*) FROM seguridad.permiso_sucursal p JOIN seguridad.acceso_autorizado a ON a.id=p.acceso_id AND a.organizacion_id=p.organizacion_id WHERE a.correo_permitido='{e.Correo}'"));
        Assert.Equal(HttpStatusCode.Forbidden,(await _api.NuevoNavegador().EntrarAsync(ValidadorFicticio.Token(e.Sujeto,e.Correo))).StatusCode);
    }
    [Fact] public async Task Operaciones_entra_al_panel_pero_no_a_movil()
    {
        var e=await PrepararAsync("operaciones"); var cliente=_api.NuevoNavegador();
        Assert.Equal(HttpStatusCode.Forbidden,(await EntrarAsync(cliente,e)).StatusCode);
        Assert.Equal("t",await bd.ConsultarAsync($"SELECT identidad_id IS NULL FROM seguridad.acceso_autorizado WHERE correo_permitido='{e.Correo}'"));
        Assert.Equal(HttpStatusCode.NoContent,(await cliente.EntrarAsync(ValidadorFicticio.Token(e.Sujeto,e.Correo))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,(await cliente.GetAsync("/api/movil/auth/me")).StatusCode);
    }
    [Fact] public async Task Nonce_unico_instalacion_y_verificacion_son_obligatorios()
    {
        var e=await PrepararAsync(); var cliente=_api.NuevoNavegador();
        Assert.Equal(HttpStatusCode.Forbidden,(await EntrarAsync(cliente,e,verificado:false)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,(await EntrarAsync(cliente,e,nonceExtra:"incorrecto")).StatusCode);
        var d=await DesafioAsync(cliente,e);
        var solicitud=new SolicitudGoogleMovil(ValidadorFicticio.Token(e.Sujeto,e.Correo,nonce:d.Nonce),e.Instalacion,d.Id);
        Assert.Equal(HttpStatusCode.OK,(await cliente.PostAsJsonAsync("/api/movil/auth/google",solicitud)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,(await cliente.PostAsJsonAsync("/api/movil/auth/google",solicitud)).StatusCode);
        var d2=await DesafioAsync(cliente,e);
        Assert.Equal(HttpStatusCode.Forbidden,(await cliente.PostAsJsonAsync("/api/movil/auth/google",solicitud with {DesafioId=d2.Id})).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,(await cliente.PostAsJsonAsync("/api/movil/auth/desafio",new{instalacionId=Guid.NewGuid()})).StatusCode);
    }
    [Fact] public async Task Instalacion_deshabilitada_o_asignacion_ajena_no_autorizan()
    {
        var deshabilitada=await PrepararAsync(habilitada:false); var cliente=_api.NuevoNavegador();
        Assert.Equal(HttpStatusCode.Forbidden,(await cliente.PostAsJsonAsync("/api/movil/auth/desafio",new{instalacionId=deshabilitada.Instalacion})).StatusCode);
        var a=await PrepararAsync(); var b=await PrepararAsync(); var d=await DesafioAsync(cliente,a);
        Assert.Equal(HttpStatusCode.Forbidden,(await cliente.PostAsJsonAsync("/api/movil/auth/google",new SolicitudGoogleMovil(
            ValidadorFicticio.Token(b.Sujeto,b.Correo,nonce:d.Nonce),a.Instalacion,d.Id))).StatusCode);
        Assert.Equal("t",await bd.ConsultarAsync($"SELECT identidad_id IS NULL FROM seguridad.acceso_autorizado WHERE correo_permitido='{b.Correo}'"));
    }
    [Fact] public async Task Cerrar_sesion_revoca_access_y_refresh_en_servidor()
    {
        var (cliente,tokens)=await SesionAsync(await PrepararAsync());
        Assert.Equal(HttpStatusCode.OK,(await cliente.PostAsJsonAsync("/api/movil/auth/refresh",new{tokens.RefreshToken})).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent,(await cliente.PostAsync("/api/movil/auth/logout",null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,(await cliente.GetAsync("/api/movil/auth/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,(await cliente.PostAsJsonAsync("/api/movil/auth/refresh",new{tokens.RefreshToken})).StatusCode);
    }
    [Theory] [InlineData("baja")] [InlineData("rol")] [InlineData("equipo")] [InlineData("asignacion")] [InlineData("persona")]
    public async Task Revalida_cada_peticion_y_renovacion(string cambio)
    {
        var e=await PrepararAsync(); var (cliente,tokens)=await SesionAsync(e);
        var org=BaseDatosPrueba.Organizacion;
        await bd.SqlComoDuenoAsync(cambio switch {
            "baja"=>$"UPDATE seguridad.acceso_autorizado SET activo=false WHERE correo_permitido='{e.Correo}'",
            "rol"=>$"UPDATE seguridad.acceso_autorizado SET rol_codigo='operaciones' WHERE correo_permitido='{e.Correo}'",
            "equipo"=>$"UPDATE seguridad.instalacion_app SET habilitada=false WHERE id='{e.Instalacion}'",
            "asignacion"=>$"INSERT INTO seguridad.asignacion_equipo(organizacion_id,sucursal_id,dispositivo_id,acceso_id,persona_id,revision,desde) SELECT organizacion_id,'{BaseDatosPrueba.Sucursal}',dispositivo_id,acceso_id,persona_id,2,now() FROM seguridad.asignacion_equipo WHERE dispositivo_id='{e.Dispositivo}'",
            _=>$"INSERT INTO negocio.operador_ficha(organizacion_id,sucursal_id,persona_id,revision,nombre_completo,correo) VALUES('{org}','{BaseDatosPrueba.Sucursal}','{e.Persona}',2,'Otro titular',NULL)"
        });
        Assert.Equal(HttpStatusCode.Forbidden,(await cliente.GetAsync("/api/movil/auth/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,(await cliente.PostAsJsonAsync("/api/movil/auth/refresh",new{tokens.RefreshToken})).StatusCode);
    }
    [Fact] public async Task Desafio_expirado_token_invalido_y_refresh_alterado_no_entran()
    {
        var e=await PrepararAsync(); var cliente=_api.NuevoNavegador(); var d=await DesafioAsync(cliente,e);
        await bd.SqlComoDuenoAsync($"UPDATE seguridad.desafio_movil SET vence_en=now()-interval '1 second' WHERE id='{d.Id}'");
        Assert.Equal(HttpStatusCode.Forbidden,(await cliente.PostAsJsonAsync("/api/movil/auth/google",new SolicitudGoogleMovil(
            ValidadorFicticio.Token(e.Sujeto,e.Correo,nonce:d.Nonce),e.Instalacion,d.Id))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,(await cliente.PostAsJsonAsync("/api/movil/auth/google",new SolicitudGoogleMovil("invalido",e.Instalacion,d.Id))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,(await cliente.PostAsJsonAsync("/api/movil/auth/refresh",new{refreshToken="alterado"})).StatusCode);
        using var http=_api.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions{BaseAddress=new Uri("http://localhost")});
        Assert.Equal(HttpStatusCode.BadRequest,(await http.PostAsJsonAsync("/api/movil/auth/desafio",new{instalacionId=e.Instalacion})).StatusCode);
    }
    [Fact] public async Task Access_expirado_y_tokens_de_otro_tipo_no_se_aceptan()
    {
        var (cliente,tokens)=await SesionAsync(await PrepararAsync());
        var o=_api.Services.GetRequiredService<IOptionsMonitor<BearerTokenOptions>>().Get(AccesoMovil.Esquema);
        var ticket=o.BearerTokenProtector.Unprotect(tokens.AccessToken)!;
        ticket.Properties.ExpiresUtc=DateTimeOffset.UtcNow.AddSeconds(-1);
        cliente.DefaultRequestHeaders.Authorization=new("Bearer",o.BearerTokenProtector.Protect(ticket));
        Assert.Equal(HttpStatusCode.Unauthorized,(await cliente.GetAsync("/api/movil/auth/me")).StatusCode);
        cliente.DefaultRequestHeaders.Authorization=new("Bearer",tokens.RefreshToken);
        Assert.Equal(HttpStatusCode.Unauthorized,(await cliente.GetAsync("/api/movil/auth/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,(await cliente.PostAsJsonAsync("/api/movil/auth/refresh",new{refreshToken=tokens.AccessToken})).StatusCode);
    }
    [Fact] public async Task Un_desafio_concurrente_solo_crea_una_sesion()
    {
        var e=await PrepararAsync(); var cliente=_api.NuevoNavegador(); var d=await DesafioAsync(cliente,e);
        var s=new SolicitudGoogleMovil(ValidadorFicticio.Token(e.Sujeto,e.Correo,nonce:d.Nonce),e.Instalacion,d.Id);
        var respuestas=await Task.WhenAll(Enumerable.Range(0,4).Select(_=>cliente.PostAsJsonAsync("/api/movil/auth/google",s)));
        Assert.Single(respuestas,r=>r.StatusCode==HttpStatusCode.OK);
        Assert.Equal(3,respuestas.Count(r=>r.StatusCode==HttpStatusCode.Forbidden));
        Assert.Equal("1",await bd.ConsultarAsync($"SELECT count(*) FROM seguridad.sesion_movil WHERE instalacion_id='{e.Instalacion}'"));
    }
    [Fact] public async Task Renovacion_prolongada_conserva_sesion_y_persona_sin_otro_login()
    {
        var e=await PrepararAsync(); var (cliente,tokens)=await SesionAsync(e);
        var o=_api.Services.GetRequiredService<IOptionsMonitor<BearerTokenOptions>>().Get(AccesoMovil.Esquema);
        Assert.True(o.RefreshTokenProtector.Unprotect(tokens.RefreshToken)!.Properties.ExpiresUtc>DateTimeOffset.UtcNow.AddDays(29));
        // Simula una sesión creada hace semanas, aún dentro de su ventana de renovación.
        await bd.SqlComoDuenoAsync($"UPDATE seguridad.sesion_movil SET creada_en=now()-interval '40 days', vence_en=now()+interval '1 day' WHERE instalacion_id='{e.Instalacion}'");
        var respuesta=await cliente.PostAsJsonAsync("/api/movil/auth/refresh",new{tokens.RefreshToken});
        Assert.Equal(HttpStatusCode.OK,respuesta.StatusCode);
        var nuevos=(await respuesta.Content.ReadFromJsonAsync<Tokens>())!;
        cliente.DefaultRequestHeaders.Authorization=new("Bearer",nuevos.AccessToken);
        Assert.Equal(e.Persona,(await cliente.GetFromJsonAsync<UsuarioMovil>("/api/movil/auth/me"))!.Operador.PersonaId);
        Assert.Equal("1",await bd.ConsultarAsync($"SELECT count(*) FROM seguridad.sesion_movil WHERE instalacion_id='{e.Instalacion}' AND vence_en>now()+interval '29 days'"));
        Assert.Equal("1",await bd.ConsultarAsync($"SELECT count(*) FROM seguridad.inicio_sesion i JOIN seguridad.acceso_autorizado a ON a.id=i.acceso_id WHERE a.correo_permitido='{e.Correo}' AND i.canal='movil' AND i.resultado='aceptado'"));
        Assert.Equal(HttpStatusCode.NoContent,(await cliente.PostAsync("/api/movil/auth/logout",null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,(await cliente.PostAsJsonAsync("/api/movil/auth/refresh",new{nuevos.RefreshToken})).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,(await cliente.PostAsJsonAsync("/api/movil/auth/refresh",new{tokens.RefreshToken})).StatusCode);
    }
    [Fact] public async Task Renovar_no_resucita_sesion_caducada_ni_refresh_vencido()
    {
        var e=await PrepararAsync(); var (cliente,tokens)=await SesionAsync(e);
        var o=_api.Services.GetRequiredService<IOptionsMonitor<BearerTokenOptions>>().Get(AccesoMovil.Esquema);
        var ticket=o.RefreshTokenProtector.Unprotect(tokens.RefreshToken)!;
        ticket.Properties.ExpiresUtc=DateTimeOffset.UtcNow.AddSeconds(-1);
        Assert.Equal(HttpStatusCode.Unauthorized,(await cliente.PostAsJsonAsync("/api/movil/auth/refresh",new{refreshToken=o.RefreshTokenProtector.Protect(ticket)})).StatusCode);
        await bd.SqlComoDuenoAsync($"UPDATE seguridad.sesion_movil SET vence_en=now()-interval '1 second' WHERE instalacion_id='{e.Instalacion}'");
        Assert.Equal(HttpStatusCode.Unauthorized,(await cliente.PostAsJsonAsync("/api/movil/auth/refresh",new{tokens.RefreshToken})).StatusCode);
        Assert.Equal("t",await bd.ConsultarAsync($"SELECT vence_en<now() FROM seguridad.sesion_movil WHERE instalacion_id='{e.Instalacion}'"));
    }
    [Fact] public async Task Una_rafaga_de_renovaciones_y_consultas_de_un_teléfono_no_recibe_429()
    {
        // Carga corta: un teléfono que reintenta sin parar (envíos y descargas seguidos) no debe toparse con el límite de acceso.
        var (cliente,tokens)=await SesionAsync(await PrepararAsync());
        var respuestas=new List<HttpResponseMessage>();
        for(var lote=0;lote<15;lote++) respuestas.AddRange(await Task.WhenAll(Enumerable.Range(0,8).Select(_=>cliente.PostAsJsonAsync("/api/movil/auth/refresh",new{tokens.RefreshToken}))));
        Assert.DoesNotContain(respuestas,r=>r.StatusCode==HttpStatusCode.TooManyRequests);
        for(var i=0;i<150;i++) Assert.NotEqual(HttpStatusCode.TooManyRequests,(await cliente.PostAsJsonAsync("/api/movil/auth/refresh",new{tokens.RefreshToken})).StatusCode);
    }
    [Fact] public async Task Renovaciones_concurrentes_y_logout_no_reactivan_sesion()
    {
        var (cliente,tokens)=await SesionAsync(await PrepararAsync());
        await Task.WhenAll(Enumerable.Range(0,4).Select(_=>cliente.PostAsJsonAsync("/api/movil/auth/refresh",new{tokens.RefreshToken}))
            .Append(cliente.PostAsync("/api/movil/auth/logout",null)));
        Assert.Equal(HttpStatusCode.Forbidden,(await cliente.PostAsJsonAsync("/api/movil/auth/refresh",new{tokens.RefreshToken})).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,(await cliente.GetAsync("/api/movil/auth/me")).StatusCode);
    }
}
