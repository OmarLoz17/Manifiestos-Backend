using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Manifiestos.Api.Acceso;
using Manifiestos.Api.Tests.Infraestructura;

namespace Manifiestos.Api.Tests;

/// <summary>Recepción de capturas (0024): idempotente, atribuida al autor de la sesión, atómica y sin generar manifiestos. Datos ficticios.</summary>
[Trait("Categoria", "Integracion")]
public sealed class CapturasMovilTests(BaseDatosPrueba bd) : IClassFixture<BaseDatosPrueba>, IAsyncLifetime
{
    private static readonly Guid Org = BaseDatosPrueba.Organizacion, Suc = BaseDatosPrueba.Sucursal;
    private ApiPrueba _api = null!;
    public Task InitializeAsync() { _api = new ApiPrueba(bd); return Task.CompletedTask; }
    public async Task DisposeAsync() => await _api.DisposeAsync();

    private sealed record Tokens(string TokenType, string AccessToken, int ExpiresIn, string RefreshToken);
    private sealed record Equipo(string Correo, string Sujeto, Guid Persona, Guid Instalacion, Guid Dispositivo);
    private sealed record Sesion(HttpClient Http, Equipo Equipo, UsuarioMovil Yo);

    private async Task<Sesion> SesionAsync()
    {
        var e = new Equipo($"cap-{Guid.NewGuid():N}@example.invalid", $"s-{Guid.NewGuid():N}", Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        await bd.AutorizarAsync(e.Correo, "telefono_operador", conSucursal: false);
        await bd.SqlComoDuenoAsync($"""
            INSERT INTO seguridad.persona(organizacion_id,id,nombre_completo) VALUES('{Org}','{e.Persona}','Operador sintético');
            INSERT INTO negocio.operador_ficha(organizacion_id,sucursal_id,persona_id,revision,nombre_completo,correo) VALUES('{Org}','{Suc}','{e.Persona}',1,'Operador sintético','{e.Correo}');
            INSERT INTO seguridad.dispositivo(organizacion_id,id) VALUES('{Org}','{e.Dispositivo}');
            INSERT INTO seguridad.instalacion_app(organizacion_id,id,dispositivo_id,habilitada) VALUES('{Org}','{e.Instalacion}','{e.Dispositivo}',true);
            INSERT INTO seguridad.asignacion_equipo(organizacion_id,sucursal_id,dispositivo_id,acceso_id,persona_id,revision,desde)
            SELECT organizacion_id,'{Suc}','{e.Dispositivo}',id,'{e.Persona}',1,now()-interval '1 minute' FROM seguridad.acceso_autorizado WHERE correo_permitido='{e.Correo}';
            """);
        var http = _api.NuevoNavegador();
        var d = (await (await http.PostAsJsonAsync("/api/movil/auth/desafio", new { instalacionId = e.Instalacion })).Content.ReadFromJsonAsync<DesafioMovil>())!;
        var r = await http.PostAsJsonAsync("/api/movil/auth/google", new SolicitudGoogleMovil(ValidadorFicticio.Token(e.Sujeto, e.Correo, true, nonce: d.Nonce), e.Instalacion, d.Id));
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", (await r.Content.ReadFromJsonAsync<Tokens>())!.AccessToken);
        return new Sesion(http, e, (await http.GetFromJsonAsync<UsuarioMovil>("/api/movil/auth/me"))!);
    }

    private static string Sha(char c) => new(c, 64);
    private static object Foto(Guid id, bool conUbicacion = true) => new
    {
        id, rol = "FOTO", sha256 = Sha('a'), tamano = 1234,
        ubicacion = conUbicacion ? new { latitud = 25.6866, longitud = -100.3161, precisionMetros = 8.5, fechaEpochMs = 1_790_000_000_000L - 2000 } : null,
    };
    private static object Firma(Guid id, string rol) => new { id, rol, sha256 = Sha('b'), tamano = 900, ubicacion = (object?)null };

    private static string Sobre(Sesion s, Guid id, string tipo = "SERVICIO_OPERADOR", Guid? persona = null, object[]? evidencias = null,
        bool aceptado = true, object? planta = null, string? nombre = null, object? problema = null, int version = 1)
    {
        var o = s.Yo.Operador;
        return JsonSerializer.Serialize(new
        {
            version, id,
            contexto = new { ambitoId = "ambito", personaId = persona ?? o.PersonaId, identidadId = o.IdentidadId, instalacionId = o.InstalacionId,
                asignacionEquipoId = o.AsignacionEquipoId, revisionEquipo = o.RevisionEquipo.ToString() },
            trabajo = new { ordenId = Guid.NewGuid(), jornadaId = Guid.NewGuid(), asignacionTrabajoId = "a-1", revisionTrabajo = "1",
                requiereFirmaCliente = true, versionDatos = "1", datosOrdenJson = "{\"dato\":\"sintetico\"}" },
            tipo, fechaEpochMs = 1_790_000_000_000L,
            evidencias = evidencias ?? [Foto(Guid.NewGuid()), Firma(Guid.NewGuid(), "FIRMA_OPERADOR")],
            aceptado = aceptado && tipo != "PROBLEMA" ? new { version = "fixture-1", textoExacto = "Texto ficticio  exacto" } : null,
            nombreFirmante = nombre, planta, problema,
        });
    }

    private static Task<HttpResponseMessage> EnviarAsync(HttpClient http, string sobre) =>
        http.PostAsync("/api/movil/capturas", new StringContent(sobre, Encoding.UTF8, "application/json"));

    private Task<string> ContarAsync(string tabla, Guid captura) =>
        bd.ConsultarAsync($"SELECT count(*) FROM {tabla} WHERE captura_id='{captura}'");

    [Fact]
    public async Task Recibe_servicio_con_autor_contexto_posicion_y_evidencias_pendientes_sin_generar_manifiesto()
    {
        var s = await SesionAsync(); var id = Guid.NewGuid();
        var manifiestosAntes = await bd.ConsultarAsync("SELECT count(*) FROM negocio.manifiesto");
        var incidenciasAntes = await bd.ConsultarAsync("SELECT count(*) FROM negocio.incidencia");
        var r = await EnviarAsync(s.Http, Sobre(s, id));
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var res = JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("recibida", res.GetProperty("estado").GetString());
        Assert.Equal(id, res.GetProperty("capturaId").GetGuid());
        Assert.All(res.GetProperty("evidencias").EnumerateArray(), e => Assert.Equal("pendiente", e.GetProperty("estado").GetString()));
        Assert.Equal("1", await bd.ConsultarAsync($"SELECT count(*) FROM negocio.captura WHERE id='{id}'"));
        Assert.Equal(s.Equipo.Persona.ToString(), await bd.ConsultarAsync($"SELECT persona_id FROM negocio.captura_trabajo WHERE captura_id='{id}'"));
        Assert.Equal("1", await ContarAsync("negocio.posicion_captura", id));
        Assert.Equal("2", await ContarAsync("negocio.evidencia_captura", id));
        Assert.Equal("1", await bd.ConsultarAsync($"SELECT count(*) FROM negocio.contenido_aceptado WHERE captura_id='{id}'"));
        // Texto aceptado exacto, con su doble espacio, tal como llegó.
        Assert.Equal("Texto ficticio  exacto", await bd.ConsultarAsync($"SELECT convert_from(contenido,'UTF8') FROM negocio.contenido_aceptado WHERE captura_id='{id}'"));
        Assert.Equal("1", await bd.ConsultarAsync($"SELECT count(*) FROM sync.comando WHERE id='{id}'"));
        // La recepción solo registra hechos: ni manifiestos ni incidencias de panel.
        Assert.Equal(manifiestosAntes, await bd.ConsultarAsync("SELECT count(*) FROM negocio.manifiesto"));
        Assert.Equal(incidenciasAntes, await bd.ConsultarAsync("SELECT count(*) FROM negocio.incidencia"));
    }

    [Fact]
    public async Task Reintento_identico_devuelve_el_mismo_resultado_sin_duplicar_y_otro_contenido_se_rechaza()
    {
        var s = await SesionAsync(); var id = Guid.NewGuid(); var sobre = Sobre(s, id);
        var a = await (await EnviarAsync(s.Http, sobre)).Content.ReadAsStringAsync();
        var r2 = await EnviarAsync(s.Http, sobre);
        Assert.Equal(HttpStatusCode.OK, r2.StatusCode);
        Assert.Equal(a, await r2.Content.ReadAsStringAsync());
        Assert.Equal("1", await bd.ConsultarAsync($"SELECT count(*) FROM negocio.captura WHERE id='{id}'"));
        Assert.Equal("2", await ContarAsync("negocio.evidencia_captura", id));
        // Mismo UUID con contenido distinto (aunque solo cambie un espacio): conflicto, sin tocar lo guardado.
        var alterado = Sobre(s, id).Replace("a-1", "a-2");
        Assert.Equal(HttpStatusCode.Conflict, (await EnviarAsync(s.Http, alterado)).StatusCode);
        Assert.Equal("1", await bd.ConsultarAsync($"SELECT count(*) FROM negocio.captura WHERE id='{id}'"));
    }

    [Fact]
    public async Task Sobre_de_otra_persona_o_version_o_foto_sin_ubicacion_se_rechaza_sin_dejar_nada()
    {
        var s = await SesionAsync();
        var ajena = Guid.NewGuid();
        Assert.Equal(HttpStatusCode.Forbidden, (await EnviarAsync(s.Http, Sobre(s, ajena, persona: Guid.NewGuid()))).StatusCode);
        var v = Guid.NewGuid();
        Assert.Equal(HttpStatusCode.BadRequest, (await EnviarAsync(s.Http, Sobre(s, v, version: 2))).StatusCode);
        var sinGeo = Guid.NewGuid();
        Assert.Equal(HttpStatusCode.BadRequest, (await EnviarAsync(s.Http, Sobre(s, sinGeo, evidencias: [Foto(Guid.NewGuid(), false), Firma(Guid.NewGuid(), "FIRMA_OPERADOR")]))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await EnviarAsync(s.Http, "{no es json")).StatusCode);
        foreach (var id in new[] { ajena, v, sinGeo })
        {
            Assert.Equal("0", await bd.ConsultarAsync($"SELECT count(*) FROM negocio.captura WHERE id='{id}'"));
            Assert.Equal("0", await bd.ConsultarAsync($"SELECT count(*) FROM sync.comando WHERE id='{id}'"));
        }
    }

    [Fact]
    public async Task Problema_requiere_foto_con_ubicacion_y_no_lleva_texto_aceptado_y_la_planta_exige_su_dato()
    {
        var s = await SesionAsync();
        var p = Guid.NewGuid();
        var ok = Sobre(s, p, "PROBLEMA", evidencias: [Foto(Guid.NewGuid())], problema: new { motivo = "sin_acceso", comentario = "" });
        Assert.Equal(HttpStatusCode.OK, (await EnviarAsync(s.Http, ok)).StatusCode);
        Assert.Equal("sin_acceso", await bd.ConsultarAsync($"SELECT problema_motivo FROM negocio.captura_trabajo WHERE captura_id='{p}'"));
        Assert.Equal("0", await bd.ConsultarAsync($"SELECT count(*) FROM negocio.contenido_aceptado WHERE captura_id='{p}'"));
        // Un problema con texto aceptado, o una recepción sin planta, son inconsistentes.
        var conTexto = Sobre(s, Guid.NewGuid(), "PROBLEMA", evidencias: [Foto(Guid.NewGuid())], aceptado: true, problema: new { motivo = "otro", comentario = "" })
            .Replace("\"aceptado\":null", "\"aceptado\":{\"version\":\"x\",\"textoExacto\":\"y\"}");
        Assert.Equal(HttpStatusCode.BadRequest, (await EnviarAsync(s.Http, conTexto)).StatusCode);
        var sinPlanta = Sobre(s, Guid.NewGuid(), "RECEPCION_PLANTA", evidencias: [Firma(Guid.NewGuid(), "FIRMA_RECEPCION")], nombre: "Quien recibe");
        Assert.Equal(HttpStatusCode.BadRequest, (await EnviarAsync(s.Http, sinPlanta)).StatusCode);
        var planta = Guid.NewGuid();
        var conPlanta = Sobre(s, planta, "RECEPCION_PLANTA", evidencias: [Firma(Guid.NewGuid(), "FIRMA_RECEPCION")], nombre: "Quien recibe", planta: new { id = "pta-1", nombre = "PTAR Ficticia" });
        Assert.Equal(HttpStatusCode.OK, (await EnviarAsync(s.Http, conPlanta)).StatusCode);
        Assert.Equal("PTAR Ficticia", await bd.ConsultarAsync($"SELECT planta_nombre FROM negocio.captura_trabajo WHERE captura_id='{planta}'"));
    }

    [Fact]
    public async Task Consulta_solo_las_capturas_de_su_propia_instalacion_y_sin_sesion_no_entra()
    {
        var a = await SesionAsync(); var b = await SesionAsync(); var id = Guid.NewGuid();
        Assert.Equal(HttpStatusCode.OK, (await EnviarAsync(a.Http, Sobre(a, id))).StatusCode);
        var propia = await a.Http.GetAsync($"/api/movil/capturas/{id}");
        Assert.Equal(HttpStatusCode.OK, propia.StatusCode);
        Assert.Equal("recibida", JsonDocument.Parse(await propia.Content.ReadAsStringAsync()).RootElement.GetProperty("estado").GetString());
        Assert.Equal(HttpStatusCode.NotFound, (await b.Http.GetAsync($"/api/movil/capturas/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _api.NuevoNavegador().GetAsync($"/api/movil/capturas/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await EnviarAsync(_api.NuevoNavegador(), Sobre(a, Guid.NewGuid()))).StatusCode);
    }
}
