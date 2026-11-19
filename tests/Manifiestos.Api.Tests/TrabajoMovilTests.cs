using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Manifiestos.Api.Acceso;
using Manifiestos.Api.Tests.Infraestructura;

namespace Manifiestos.Api.Tests;

/// <summary>Trabajo del día para el canal móvil (0023): solo lo publicado, solo de la persona de la sesión. Datos ficticios.</summary>
[Trait("Categoria", "Integracion")]
public sealed class TrabajoMovilTests(BaseDatosPrueba bd) : IClassFixture<BaseDatosPrueba>, IAsyncLifetime
{
    private static readonly Guid Org = BaseDatosPrueba.Organizacion, Suc = BaseDatosPrueba.Sucursal;
    private ApiPrueba _api = null!;
    public Task InitializeAsync() { _api = new ApiPrueba(bd); return Task.CompletedTask; }
    public async Task DisposeAsync() => await _api.DisposeAsync();

    private sealed record Tokens(string TokenType, string AccessToken, int ExpiresIn, string RefreshToken);
    private sealed record Equipo(string Correo, string Sujeto, Guid Persona, Guid Instalacion, Guid Dispositivo);
    private static string Hoy => DateOnly.FromDateTime(DateTime.UtcNow).ToString("yyyy-MM-dd");

    private async Task<Equipo> EquipoAsync()
    {
        var e = new Equipo($"trabajo-{Guid.NewGuid():N}@example.invalid", $"s-{Guid.NewGuid():N}", Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        await bd.AutorizarAsync(e.Correo, "telefono_operador", conSucursal: false);
        await bd.SqlComoDuenoAsync($"""
            INSERT INTO seguridad.persona(organizacion_id,id,nombre_completo) VALUES('{Org}','{e.Persona}','Operador sintético');
            INSERT INTO negocio.operador_ficha(organizacion_id,sucursal_id,persona_id,revision,nombre_completo,correo)
            VALUES('{Org}','{Suc}','{e.Persona}',1,'Operador sintético','{e.Correo}');
            INSERT INTO seguridad.dispositivo(organizacion_id,id) VALUES('{Org}','{e.Dispositivo}');
            INSERT INTO seguridad.instalacion_app(organizacion_id,id,dispositivo_id,habilitada) VALUES('{Org}','{e.Instalacion}','{e.Dispositivo}',true);
            INSERT INTO seguridad.asignacion_equipo(organizacion_id,sucursal_id,dispositivo_id,acceso_id,persona_id,revision,desde)
            SELECT organizacion_id,'{Suc}','{e.Dispositivo}',id,'{e.Persona}',1,now()-interval '1 minute'
            FROM seguridad.acceso_autorizado WHERE correo_permitido='{e.Correo}';
            """);
        return e;
    }

    private async Task<HttpClient> SesionAsync(Equipo e)
    {
        var cliente = _api.NuevoNavegador();
        var d = (await (await cliente.PostAsJsonAsync("/api/movil/auth/desafio", new { instalacionId = e.Instalacion })).Content.ReadFromJsonAsync<DesafioMovil>())!;
        var r = await cliente.PostAsJsonAsync("/api/movil/auth/google", new SolicitudGoogleMovil(ValidadorFicticio.Token(e.Sujeto, e.Correo, true, nonce: d.Nonce), e.Instalacion, d.Id));
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var t = (await r.Content.ReadFromJsonAsync<Tokens>())!;
        cliente.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", t.AccessToken);
        return cliente;
    }

    /// <summary>Ruta con una parada y un cliente; la unidad queda asignada a <paramref name="operador"/> y la jornada con el estado dado.</summary>
    private async Task<(Guid Jornada, Guid Parada, Guid Cliente)> RutaAsync(Guid operador, int fosas, bool autorizado, string? estado, string? fecha = null, int banos = 0)
    {
        var (ruta, unidad, parada, sitio, version, empresa, cliente, jornada) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        await bd.SqlComoDuenoAsync($"""
            BEGIN;
            INSERT INTO negocio.vehiculo VALUES ('{Org}', '{Suc}', '{unidad}', 'TRB-{unidad.ToString("N")[..5]}');
            INSERT INTO negocio.ficha_vehiculo(organizacion_id, sucursal_id, vehiculo_id, revision, capacidad_id, clasificacion) VALUES ('{Org}', '{Suc}', '{unidad}', 1, 2, 'fosas');
            INSERT INTO negocio.ruta(organizacion_id, sucursal_id, id) VALUES ('{Org}', '{Suc}', '{ruta}');
            INSERT INTO negocio.ruta_version(organizacion_id, sucursal_id, ruta_id, revision, nombre, dias, vehiculo_id) VALUES ('{Org}', '{Suc}', '{ruta}', 1, 'Ruta Trabajo Ficticia', 127, '{unidad}');
            INSERT INTO negocio.asignacion_unidad(organizacion_id, sucursal_id, vehiculo_id, revision, persona_id) VALUES ('{Org}', '{Suc}', '{unidad}', 1, '{operador}');
            INSERT INTO negocio.empresa VALUES ('{Org}', '{empresa}', 'CLI-{empresa:N}');
            INSERT INTO negocio.ficha_empresa(organizacion_id, empresa_id, revision, nombre_comercial) VALUES ('{Org}', '{empresa}', 1, 'Cliente Trabajo Ficticio');
            INSERT INTO negocio.empresa_sucursal VALUES ('{Org}', '{Suc}', '{empresa}');
            INSERT INTO negocio.cliente VALUES ('{Org}', '{Suc}', '{cliente}', '{empresa}', 'T-{cliente:N}');
            INSERT INTO negocio.sitio VALUES ('{Org}', '{Suc}', '{sitio}', 'Sitio trabajo');
            INSERT INTO negocio.punto_servicio VALUES ('{Org}', '{Suc}', '{sitio}');
            INSERT INTO negocio.parada(organizacion_id, sucursal_id, id, ruta_id, posicion) VALUES ('{Org}', '{Suc}', '{parada}', '{ruta}', 1);
            INSERT INTO negocio.parada_version(organizacion_id, sucursal_id, id, parada_id, revision, sitio_id) VALUES ('{Org}', '{Suc}', '{version}', '{parada}', 1, '{sitio}');
            INSERT INTO negocio.parada_cliente(organizacion_id, sucursal_id, version_id, cliente_id, banos_planeados, fosas_planeadas) VALUES ('{Org}', '{Suc}', '{version}', '{cliente}', {(banos > 0 ? banos.ToString() : "NULL")}, {fosas});
            {(autorizado ? $"INSERT INTO negocio.autorizacion_empresa(organizacion_id, empresa_id, autoridad, finalidad, numero) VALUES ('{Org}', '{empresa}', 'Medio ambiente', 'Generador', 'AUT-FICTICIA');" : "")}
            {(estado is null ? "" : $"""
            INSERT INTO negocio.jornada(organizacion_id, sucursal_id, id, ruta_id, fecha) VALUES ('{Org}', '{Suc}', '{jornada}', '{ruta}', '{fecha ?? Hoy}');
            INSERT INTO negocio.estado_jornada(organizacion_id, sucursal_id, jornada_id, revision, estado) VALUES ('{Org}', '{Suc}', '{jornada}', 1, '{estado}');
            """)}
            COMMIT;
            """);
        return (jornada, parada, cliente);
    }

    private static async Task<JsonElement> TrabajoAsync(HttpClient c, string fecha)
    {
        var r = await c.GetAsync($"/api/movil/trabajo?fecha={fecha}");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Contains("no-store", r.Headers.CacheControl!.ToString());
        return JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement.Clone();
    }

    [Fact]
    public async Task En_una_ruta_con_banos_la_fosa_de_un_cliente_general_viaja_como_parada_de_sanitarios_y_la_de_convenio_sigue_siendo_orden()
    {
        var yo = await EquipoAsync();
        // Un cliente general con baños Y fosa en una ruta de fosas: la ruta es «mixta»; su fosa ya no es una orden, va en la entrada de sanitarios.
        var (jornada, parada, cliente) = await RutaAsync(yo.Persona, fosas: 1, autorizado: false, "programada", banos: 4);
        var c = await SesionAsync(yo);
        var t = await TrabajoAsync(c, Hoy);
        var jornadas = t.GetProperty("jornadas").EnumerateArray().ToList();
        Assert.Equal(2, jornadas.Count);
        var fosas = jornadas.Single(x => x.GetProperty("servicio").GetString() == "fosas");
        var banos = jornadas.Single(x => x.GetProperty("servicio").GetString() == "sanitarios");
        Assert.Empty(fosas.GetProperty("paradas").EnumerateArray());                          // sin órdenes de fosa propia
        Assert.Equal(jornada, banos.GetProperty("jornadaId").GetGuid());                      // la misma jornada real
        Assert.Equal(jornada + ":sanitarios", banos.GetProperty("claveRuta").GetString());    // identidad propia en el teléfono
        var p = Assert.Single(banos.GetProperty("paradas").EnumerateArray());
        Assert.Equal(parada, p.GetProperty("paradaId").GetGuid());
        Assert.Equal(4, p.GetProperty("banosAsignados").GetInt32());
        Assert.False(p.GetProperty("fosa").GetBoolean());                                     // tiene baños: se captura con su total
        // Un cliente con convenio (autorizado) con baños de datos antiguos NO crea la entrada de sanitarios ni deja de ser orden.
        var otro = await EquipoAsync();
        await RutaAsync(otro.Persona, fosas: 2, autorizado: true, "programada", banos: 3);
        var t2 = await TrabajoAsync(await SesionAsync(otro), Hoy);
        var unico = Assert.Single(t2.GetProperty("jornadas").EnumerateArray());
        Assert.Equal("fosas", unico.GetProperty("servicio").GetString());
    }

    [Fact]
    public async Task Entrega_solo_la_jornada_programada_de_hoy_de_su_persona_con_ordenes_derivadas()
    {
        var yo = await EquipoAsync(); var otro = await EquipoAsync();
        var (programada, parada, cliente) = await RutaAsync(yo.Persona, fosas: 2, autorizado: true, "programada", banos: 3);
        var borrador = (await RutaAsync(yo.Persona, 1, true, "borrador")).Item1;      // fila antigua en borrador: no se ve
        var cancelada = (await RutaAsync(yo.Persona, 1, true, "cancelada")).Item1;    // cancelada: no se ve
        await RutaAsync(yo.Persona, 1, true, null);                                    // sin fila: ahora SÍ se ve (programada por omisión, 0049)
        await RutaAsync(otro.Persona, 1, true, "programada");                          // de otro operador: no se ve
        var c = await SesionAsync(yo);

        var t = await TrabajoAsync(c, Hoy);
        var jornadas = t.GetProperty("jornadas").EnumerateArray().ToList();
        var ids = jornadas.Select(x => x.GetProperty("jornadaId").GetGuid()).ToList();
        Assert.Contains(programada, ids);
        Assert.DoesNotContain(borrador, ids);
        Assert.DoesNotContain(cancelada, ids);
        Assert.All(jornadas, x => Assert.Equal("programada", x.GetProperty("estado").GetString()));
        var j = jornadas.Single(x => x.GetProperty("jornadaId").GetGuid() == programada);
        Assert.Equal("programada", j.GetProperty("estado").GetString());
        Assert.Equal(1, j.GetProperty("unidad").GetProperty("capacidadM3").GetDecimal() > 0 ? 1 : 0);
        var p = Assert.Single(j.GetProperty("paradas").EnumerateArray());
        Assert.Equal(parada, p.GetProperty("paradaId").GetGuid());
        Assert.Equal("plan", p.GetProperty("tipo").GetString());
        var k = Assert.Single(p.GetProperty("clientes").EnumerateArray());
        Assert.Equal(cliente, k.GetProperty("clienteId").GetGuid());
        Assert.True(k.GetProperty("autorizado").GetBoolean()); Assert.False(k.GetProperty("fosaPropia").GetBoolean());
        Assert.Equal(3, k.GetProperty("banos").GetInt32());
        var ordenes = k.GetProperty("ordenes").EnumerateArray().ToList();
        Assert.Equal(2, ordenes.Count);                      // una por fosa planeada; los baños no generan órdenes
        Assert.Equal([1, 2], ordenes.Select(o => o.GetProperty("vuelta").GetInt32()));
        Assert.Equal(2, ordenes.Select(o => o.GetProperty("ordenId").GetGuid()).Distinct().Count());

        // Determinista: la misma descarga repetida conserva las mismas identidades.
        var otraVez = await TrabajoAsync(c, Hoy);
        Assert.Equal(t.GetRawText(), otraVez.GetRawText());
        // No incluye capturas, firmas ni manifiestos.
        var texto = t.GetRawText().ToLowerInvariant();
        Assert.DoesNotContain("firma", texto); Assert.DoesNotContain("manifiesto", texto); Assert.DoesNotContain("captura", texto);
    }

    [Fact]
    public async Task Cliente_general_marca_fosa_propia_y_sin_firma_de_cliente()
    {
        var yo = await EquipoAsync();
        await RutaAsync(yo.Persona, fosas: 1, autorizado: false, "programada");
        var t = await TrabajoAsync(await SesionAsync(yo), Hoy);
        // Decisión 0033: la fosa propia (cliente general) SIEMPRE viaja como parada de sanitarios (un baño muy grande: foto con ubicación, sin cantidad).
        var jornadas = t.GetProperty("jornadas").EnumerateArray().ToList();
        Assert.Empty(jornadas.Single(x => x.GetProperty("servicio").GetString() == "fosas").GetProperty("paradas").EnumerateArray());   // sin órdenes de fosa
        var j = jornadas.Single(x => x.GetProperty("servicio").GetString() == "sanitarios");
        var p = Assert.Single(j.GetProperty("paradas").EnumerateArray());
        Assert.True(p.GetProperty("fosa").GetBoolean());
        Assert.False(p.GetProperty("clientes")[0].GetProperty("autorizado").GetBoolean());
    }

    [Fact]
    public async Task Sin_trabajo_hoy_devuelve_lista_vacia_y_etag_permite_304()
    {
        var yo = await EquipoAsync(); var c = await SesionAsync(yo);
        var vacio = await TrabajoAsync(c, Hoy);
        Assert.Empty(vacio.GetProperty("jornadas").EnumerateArray());
        var r = await c.GetAsync($"/api/movil/trabajo?fecha={Hoy}");
        var etag = r.Headers.ETag!.ToString();
        var req = new HttpRequestMessage(HttpMethod.Get, $"/api/movil/trabajo?fecha={Hoy}");
        req.Headers.TryAddWithoutValidation("If-None-Match", etag);
        Assert.Equal(HttpStatusCode.NotModified, (await c.SendAsync(req)).StatusCode);
        await RutaAsync(yo.Persona, 1, true, "programada");            // al publicar cambia la huella
        var nueva = await c.GetAsync($"/api/movil/trabajo?fecha={Hoy}");
        Assert.NotEqual(etag, nueva.Headers.ETag!.ToString());
    }

    [Fact]
    public async Task Fecha_invalida_o_fuera_de_rango_y_acceso_sin_sesion_se_rechazan()
    {
        var yo = await EquipoAsync(); var c = await SesionAsync(yo);
        Assert.Equal(HttpStatusCode.BadRequest, (await c.GetAsync("/api/movil/trabajo")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await c.GetAsync("/api/movil/trabajo?fecha=ayer")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await c.GetAsync("/api/movil/trabajo?fecha=2026-01-01")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await c.GetAsync($"/api/movil/trabajo?fecha={DateOnly.FromDateTime(DateTime.UtcNow).AddDays(5):yyyy-MM-dd}")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _api.NuevoNavegador().GetAsync($"/api/movil/trabajo?fecha={Hoy}")).StatusCode);
    }

    [Fact]
    public async Task Baja_de_equipo_o_instalacion_retira_el_acceso_al_trabajo()
    {
        var yo = await EquipoAsync(); await RutaAsync(yo.Persona, 1, true, "programada");
        var c = await SesionAsync(yo);
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync($"/api/movil/trabajo?fecha={Hoy}")).StatusCode);
        await bd.SqlComoDuenoAsync($"UPDATE seguridad.instalacion_app SET habilitada=false WHERE organizacion_id='{Org}' AND id='{yo.Instalacion}'");
        Assert.NotEqual(HttpStatusCode.OK, (await c.GetAsync($"/api/movil/trabajo?fecha={Hoy}")).StatusCode);
    }
}
