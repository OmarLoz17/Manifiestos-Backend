using Microsoft.Extensions.DependencyInjection;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Manifiestos.Api.Acceso;
using Manifiestos.Api.Documentos;
using Manifiestos.Api.Tests.Infraestructura;

namespace Manifiestos.Api.Tests;

/// <summary>
/// Captura de baños y tirada de sanitarios (0028) de punta a punta por HTTP: foto -> baños -> tirada -> barra a 0 -> manifiesto
/// con folio consecutivo. El operador nunca ve folio ni manifiesto. Datos ficticios; volúmenes en m3 estimados.
/// </summary>
[Trait("Categoria", "Integracion")]
public sealed partial class SanitariosMovilTests(BaseDatosPrueba bd) : IClassFixture<BaseDatosPrueba>, IAsyncLifetime
{
    private static readonly Guid Org = BaseDatosPrueba.Organizacion, Suc = BaseDatosPrueba.Sucursal;
    private const string EmpresaNuestra = "0f000000-0000-4000-8000-000000000002";
    private static string Hoy => DateOnly.FromDateTime(DateTime.UtcNow).ToString("yyyy-MM-dd");
    private ApiPrueba _api = null!;
    public Task InitializeAsync() { _api = new ApiPrueba(bd); return Task.CompletedTask; }
    public async Task DisposeAsync() => await _api.DisposeAsync();

    private sealed record Tokens(string TokenType, string AccessToken, int ExpiresIn, string RefreshToken);
    private sealed record Sesion(HttpClient Http, Guid Persona, UsuarioMovil Yo);
    /// <summary>Escenario: una unidad de 1 m3 con dos rutas (LMV y MJS), tres paradas, un cliente general, una planta y la jornada de hoy.</summary>
    private sealed record Escenario(Guid Unidad, Guid Jornada, Guid Jornada2, Guid ParadaA, Guid ParadaB, Guid ParadaC, Guid Planta, Guid Cliente);

    private async Task<(string Correo, Guid Persona, Guid Instalacion)> AltaOperadorAsync()
    {
        var correo = $"san-{Guid.NewGuid():N}@example.invalid"; var persona = Guid.NewGuid(); var instalacion = Guid.NewGuid(); var dispositivo = Guid.NewGuid();
        await bd.AutorizarAsync(correo, "telefono_operador", conSucursal: false);
        await bd.SqlComoDuenoAsync($"""
            INSERT INTO seguridad.persona(organizacion_id,id,nombre_completo) VALUES('{Org}','{persona}','Operador sintético');
            INSERT INTO negocio.operador_ficha(organizacion_id,sucursal_id,persona_id,revision,nombre_completo,correo) VALUES('{Org}','{Suc}','{persona}',1,'Operador sintético','{correo}');
            INSERT INTO seguridad.dispositivo(organizacion_id,id) VALUES('{Org}','{dispositivo}');
            INSERT INTO seguridad.instalacion_app(organizacion_id,id,dispositivo_id,habilitada) VALUES('{Org}','{instalacion}','{dispositivo}',true);
            INSERT INTO seguridad.asignacion_equipo(organizacion_id,sucursal_id,dispositivo_id,acceso_id,persona_id,revision,desde)
            SELECT organizacion_id,'{Suc}','{dispositivo}',id,'{persona}',1,now()-interval '1 minute' FROM seguridad.acceso_autorizado WHERE correo_permitido='{correo}';
            """);
        return (correo, persona, instalacion);
    }

    private async Task<Sesion> SesionAsync()
    {
        var (correo, persona, instalacion) = await AltaOperadorAsync();
        var http = _api.NuevoNavegador();
        var d = (await (await http.PostAsJsonAsync("/api/movil/auth/desafio", new { instalacionId = instalacion })).Content.ReadFromJsonAsync<DesafioMovil>())!;
        var r = await http.PostAsJsonAsync("/api/movil/auth/google", new SolicitudGoogleMovil(ValidadorFicticio.Token($"s-{Guid.NewGuid():N}", correo, true, nonce: d.Nonce), instalacion, d.Id));
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", (await r.Content.ReadFromJsonAsync<Tokens>())!.AccessToken);
        return new Sesion(http, persona, (await http.GetFromJsonAsync<UsuarioMovil>("/api/movil/auth/me"))!);
    }

    private Task<Escenario> EscenarioAsync(Sesion s) => EscenarioAsync(s.Persona);

    private async Task<Escenario> EscenarioAsync(Guid persona)
    {
        var (unidad, r1, r2, pa, pb, pc, sa, sb, sc, planta, emp, cli, j1, j2) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        string N(Guid g) => g.ToString("N")[..8];
        await bd.SqlComoDuenoAsync($"""
            BEGIN;
            INSERT INTO negocio.ficha_empresa(organizacion_id, empresa_id, revision, nombre_fiscal, nombre_comercial)
            SELECT '{Org}', '{EmpresaNuestra}', 1, 'Transportes de Prueba SA de CV', 'Transportes de Prueba'
            WHERE NOT EXISTS (SELECT 1 FROM negocio.ficha_empresa WHERE empresa_id = '{EmpresaNuestra}');
            INSERT INTO negocio.vehiculo VALUES ('{Org}', '{Suc}', '{unidad}', 'SN-{N(unidad)}');
            INSERT INTO negocio.ficha_vehiculo(organizacion_id, sucursal_id, vehiculo_id, revision, capacidad_id, clasificacion, numero_economico) VALUES ('{Org}', '{Suc}', '{unidad}', 1, 4, 'sanitarios', 'SN-{N(unidad)}');
            INSERT INTO negocio.asignacion_unidad(organizacion_id, sucursal_id, vehiculo_id, revision, persona_id) VALUES ('{Org}', '{Suc}', '{unidad}', 1, '{persona}');
            INSERT INTO negocio.ruta(organizacion_id, sucursal_id, id) VALUES ('{Org}', '{Suc}', '{r1}'), ('{Org}', '{Suc}', '{r2}');
            INSERT INTO negocio.ruta_version(organizacion_id, sucursal_id, ruta_id, revision, nombre, dias, vehiculo_id) VALUES
                ('{Org}', '{Suc}', '{r1}', 1, 'SN-{N(unidad)}-LMV', 21, '{unidad}'), ('{Org}', '{Suc}', '{r2}', 1, 'SN-{N(unidad)}-MJS', 42, '{unidad}');
            INSERT INTO negocio.empresa VALUES ('{Org}', '{emp}', 'CG-{emp:N}');
            INSERT INTO negocio.ficha_empresa(organizacion_id, empresa_id, revision, nombre_comercial) VALUES ('{Org}', '{emp}', 1, 'Cliente General Sanitarios');
            INSERT INTO negocio.empresa_sucursal VALUES ('{Org}', '{Suc}', '{emp}');
            INSERT INTO negocio.cliente VALUES ('{Org}', '{Suc}', '{cli}', '{emp}', 'G-{cli:N}');
            INSERT INTO negocio.sitio VALUES ('{Org}', '{Suc}', '{sa}', 'Sitio SA'), ('{Org}', '{Suc}', '{sb}', 'Sitio SB'), ('{Org}', '{Suc}', '{sc}', 'Sitio SC'), ('{Org}', '{Suc}', '{planta}', 'PTAR ficticia');
            INSERT INTO negocio.ficha_sitio(organizacion_id, sucursal_id, sitio_id, revision, nombre) VALUES ('{Org}', '{Suc}', '{sa}', 1, 'Bodega Central'), ('{Org}', '{Suc}', '{planta}', 1, 'PTAR NORTE');
            INSERT INTO negocio.punto_servicio VALUES ('{Org}', '{Suc}', '{sa}'), ('{Org}', '{Suc}', '{sb}'), ('{Org}', '{Suc}', '{sc}');
            INSERT INTO negocio.parada(organizacion_id, sucursal_id, id, ruta_id, posicion) VALUES ('{Org}', '{Suc}', '{pa}', '{r1}', 1), ('{Org}', '{Suc}', '{pb}', '{r1}', 2), ('{Org}', '{Suc}', '{pc}', '{r2}', 1);
            INSERT INTO negocio.parada_version(organizacion_id, sucursal_id, id, parada_id, revision, sitio_id) VALUES
                ('{Org}', '{Suc}', '{pa}', '{pa}', 1, '{sa}'), ('{Org}', '{Suc}', '{pb}', '{pb}', 1, '{sb}'), ('{Org}', '{Suc}', '{pc}', '{pc}', 1, '{sc}');
            INSERT INTO negocio.parada_cliente(organizacion_id, sucursal_id, version_id, cliente_id, banos_planeados) VALUES
                ('{Org}', '{Suc}', '{pa}', '{cli}', 12), ('{Org}', '{Suc}', '{pb}', '{cli}', 20), ('{Org}', '{Suc}', '{pc}', '{cli}', 8);
            INSERT INTO negocio.jornada(organizacion_id, sucursal_id, id, ruta_id, fecha) VALUES ('{Org}', '{Suc}', '{j1}', '{r1}', '{Hoy}'), ('{Org}', '{Suc}', '{j2}', '{r2}', '{Hoy}');
            INSERT INTO negocio.estado_jornada(organizacion_id, sucursal_id, jornada_id, revision, estado) VALUES ('{Org}', '{Suc}', '{j1}', 1, 'programada'), ('{Org}', '{Suc}', '{j2}', 1, 'programada');
            COMMIT;
            """);
        return new Escenario(unidad, j1, j2, pa, pb, pc, planta, cli);
    }

    private static long Ahora(int minutos = 0) => DateTimeOffset.UtcNow.AddMinutes(minutos).ToUnixTimeMilliseconds();
    private static string Sha(char c) => new(c, 64);

    private static string Sobre(Sesion s, Guid id, string tipo, Guid orden, Guid jornada, string datosOrden, object[] evidencias, object? sanitarios,
        object? planta = null, string? nombre = null, object? problema = null, long? fecha = null)
    {
        var o = s.Yo.Operador;
        return JsonSerializer.Serialize(new
        {
            version = 1, id,
            contexto = new { ambitoId = "ambito", personaId = o.PersonaId, identidadId = o.IdentidadId, instalacionId = o.InstalacionId, asignacionEquipoId = o.AsignacionEquipoId, revisionEquipo = o.RevisionEquipo.ToString() },
            trabajo = new { ordenId = orden, jornadaId = jornada, asignacionTrabajoId = "a-1", revisionTrabajo = "1", requiereFirmaCliente = false, versionDatos = "1", datosOrdenJson = datosOrden },
            tipo, fechaEpochMs = fecha ?? Ahora(),
            evidencias,
            aceptado = tipo == "TIRADA_SANITARIOS" ? new { version = "fixture-1", textoExacto = "Texto ficticio" } : null,
            nombreFirmante = nombre, planta, problema, sanitarios,
        });
    }

    private static object Foto(Guid id, bool ubicacion = true, long? ms = null) => new
    {
        id, rol = "FOTO", sha256 = Sha('a'), tamano = 1234,
        ubicacion = ubicacion ? new { latitud = 25.6702, longitud = -100.3091, precisionMetros = 8.0, fechaEpochMs = ms ?? Ahora() } : null,
    };
    private static object Firma(Guid id, string rol) => new { id, rol, sha256 = Sha('b'), tamano = 900, ubicacion = (object?)null };

    private static string Succion(Sesion s, Escenario e, Guid captura, Guid parada, Guid jornada, object? banos, int asignados = 12, object[]? evidencias = null, long? fecha = null) =>
        Sobre(s, captura, "SERVICIO_SANITARIOS", parada, jornada, "{\"dato\":\"sintetico\"}", evidencias ?? [Foto(Guid.NewGuid(), true, fecha)], new { banos, banosAsignados = asignados }, fecha: fecha);

    private static string Tirada(Sesion s, Escenario e, Guid captura, Guid tirada, Guid[] incluidas, bool conLista = true, string? datos = null) =>
        Sobre(s, captura, "TIRADA_SANITARIOS", tirada, e.Jornada2, datos ?? $"{{\"unidadId\":\"{e.Unidad}\"}}",
            [Firma(Guid.NewGuid(), "FIRMA_OPERADOR"), Firma(Guid.NewGuid(), "FIRMA_RECEPCION")],
            conLista ? new { capturasIncluidas = incluidas } : null, planta: new { id = e.Planta, nombre = "PTAR del teléfono" }, nombre: "Quien Recibe");

    private static Task<HttpResponseMessage> EnviarAsync(HttpClient http, string sobre) =>
        http.PostAsync("/api/movil/capturas", new StringContent(sobre, Encoding.UTF8, "application/json"));

    private Task<string> CargaAsync(Guid unidad) => bd.ConsultarAsync($"SELECT banos||'|'||m3||'|'||porcentaje FROM negocio.carga_unidad_interna('{Org}','{Suc}','{unidad}')");
    private Task<string> ManifiestosDeTiradaAsync(Guid tirada) => bd.ConsultarAsync($"SELECT count(*) FROM negocio.tirada_manifiesto WHERE tirada_id='{tirada}'");
    private Task<string> FolioDeAsync(Guid tirada) => bd.ConsultarAsync($"SELECT v.numero_manifiesto FROM negocio.version_manifiesto v JOIN negocio.tirada_manifiesto tm ON tm.manifiesto_id=v.manifiesto_id WHERE tm.tirada_id='{tirada}'");

    private async Task<Guid> CargarFirmaEmpresaAsync()
    {
        var archivo = Guid.NewGuid();
        await bd.SqlComoDuenoAsync($"""
            INSERT INTO negocio.archivo (organizacion_id, sucursal_id, id, sha256, tamano_bytes, mime)
            VALUES ('{Org}', '{Suc}', '{archivo}', decode(repeat('ef', 32), 'hex'), 100, 'image/png');
            INSERT INTO negocio.firma_generador_precargada (organizacion_id, sucursal_id, empresa_id, revision, nombre_firmante, archivo_id, es_provisional)
            SELECT '{Org}', '{Suc}', '{EmpresaNuestra}', coalesce(max(revision), 0) + 1, 'Firma de empresa ficticia', '{archivo}', false
            FROM negocio.firma_generador_precargada WHERE sucursal_id = '{Suc}' AND empresa_id = '{EmpresaNuestra}';
            """);
        return archivo;
    }

    private async Task<CodigoValidacion> ImprimirTiradaAsync(Sesion movil, Guid captura, Guid manifiesto, HttpClient panel)
    {
        using var incompleta = await panel.PrepararImpresionAsync(manifiesto);
        Assert.Equal(HttpStatusCode.Conflict, incompleta.StatusCode);
        var op = movil.Yo.Operador;
        await bd.SqlComoDuenoAsync($"""
            SELECT sync.registrar_archivo('{Org}', '{op.AccesoId}', '{op.InstalacionId}', e.id, 'image/png', 'prueba/' || e.id)
            FROM negocio.evidencia_captura e WHERE e.captura_id = '{captura}';
            """);
        var firma = await CargarFirmaEmpresaAsync();
        using var preparada = await panel.PrepararImpresionAsync(manifiesto);
        Assert.Equal(HttpStatusCode.OK, preparada.StatusCode);
        var documento = (await preparada.Content.ReadFromJsonAsync<CodigoValidacion>())!;
        Assert.Equal(firma, documento.Impreso!.GeneradorPrecargado!.ArchivoId);
        Assert.True(documento.Impreso.GeneradorEsTransportista);
        return documento;
    }

    [Fact]
    public async Task Succion_con_foto_y_ubicacion_suma_a_la_carga_de_la_unidad_y_el_trabajo_trae_servicio_y_carga()
    {
        var s = await SesionAsync(); var e = await EscenarioAsync(s);
        Assert.Equal("0|0.000|0.0", await CargaAsync(e.Unidad));
        Assert.Equal(HttpStatusCode.OK, (await EnviarAsync(s.Http, Succion(s, e, Guid.NewGuid(), e.ParadaA, e.Jornada, 10))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await EnviarAsync(s.Http, Succion(s, e, Guid.NewGuid(), e.ParadaC, e.Jornada2, 40, 8))).StatusCode);   // otra ruta, misma unidad
        Assert.Equal("50|0.800|80.0", await CargaAsync(e.Unidad));
        var t = JsonDocument.Parse(await (await s.Http.GetAsync($"/api/movil/trabajo?fecha={Hoy}")).Content.ReadAsStringAsync()).RootElement;
        var j = t.GetProperty("jornadas").EnumerateArray().Single(x => x.GetProperty("jornadaId").GetGuid() == e.Jornada);
        Assert.Equal("sanitarios", j.GetProperty("servicio").GetString());
        Assert.Equal(0.8m, j.GetProperty("unidad").GetProperty("carga").GetProperty("m3").GetDecimal());
        Assert.Equal(50, j.GetProperty("unidad").GetProperty("carga").GetProperty("banos").GetInt32());
        var pa = j.GetProperty("paradas").EnumerateArray().Single(p => p.GetProperty("paradaId").GetGuid() == e.ParadaA);
        Assert.Equal(12, pa.GetProperty("banosAsignados").GetInt32());
        Assert.All(pa.GetProperty("clientes").EnumerateArray(), c => { Assert.Equal(0, c.GetProperty("fosas").GetInt32()); Assert.Empty(c.GetProperty("ordenes").EnumerateArray()); });
        Assert.DoesNotContain("litros", t.GetRawText().ToLowerInvariant());
    }

    [Fact]
    public async Task Aviso_push_llega_al_telefono_del_operador_de_la_ruta_de_hoy_y_un_token_muerto_se_olvida()
    {
        var s = await SesionAsync(); var e = await EscenarioAsync(s);
        var ruta = Guid.Parse(await bd.ConsultarAsync($"SELECT ruta_id FROM negocio.jornada WHERE id = '{e.Jornada}'"));
        var avisos = _api.Services.GetRequiredService<Manifiestos.Api.Avisos.AvisosTrabajo>();
        var hoy = Manifiestos.Api.Avisos.AvisosTrabajo.HoyLocal();
        // Sin token registrado no hay a quién avisar.
        await avisos.AvisarAsync(ruta, hoy, "Se agregó una parada a tu ruta de hoy.");
        Assert.Empty(_api.Emisor.Enviados);
        // Token inválido: 400; válido: 204 (y registrarlo otra vez lo actualiza, no lo duplica).
        Assert.Equal(HttpStatusCode.BadRequest, (await s.Http.PostAsJsonAsync("/api/movil/auth/token-aviso", new { token = "corto" })).StatusCode);
        var token = "fcm-token-ficticio-" + Guid.NewGuid().ToString("N");
        Assert.Equal(HttpStatusCode.NoContent, (await s.Http.PostAsJsonAsync("/api/movil/auth/token-aviso", new { token })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await s.Http.PostAsJsonAsync("/api/movil/auth/token-aviso", new { token })).StatusCode);
        Assert.Equal("1", await bd.ConsultarAsync($"SELECT count(*) FROM seguridad.token_push WHERE token = '{token}'"));
        // Solo avisa si la fecha es hoy.
        await avisos.AvisarAsync(ruta, hoy.AddDays(1), "mañana");
        Assert.Empty(_api.Emisor.Enviados);
        await avisos.AvisarAsync(ruta, hoy, "Se agregó una parada a tu ruta de hoy.");
        var (enviado, aviso) = Assert.Single(_api.Emisor.Enviados);
        Assert.Equal(token, enviado);
        Assert.Equal("Se agregó una parada a tu ruta de hoy.", aviso.Texto);
        // FCM dice que el token ya no existe: se retira de la base.
        _api.Emisor.Muertos.Add(token);
        await avisos.AvisarAsync(ruta, hoy, "otro");
        Assert.Equal("0", await bd.ConsultarAsync($"SELECT count(*) FROM seguridad.token_push WHERE token = '{token}'"));
    }

    [Fact]
    public async Task Extraordinaria_fusionada_sale_dentro_de_la_parada_existente_marcada_y_sin_parada_repetida()
    {
        var s = await SesionAsync(); var e = await EscenarioAsync(s);
        var (extra, otroEmp, otro) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        await bd.SqlComoDuenoAsync($"""
            BEGIN;
            INSERT INTO negocio.empresa VALUES ('{Org}', '{otroEmp}', 'CG-{otroEmp:N}');
            INSERT INTO negocio.ficha_empresa(organizacion_id, empresa_id, revision, nombre_comercial) VALUES ('{Org}', '{otroEmp}', 1, 'Cliente General Extra');
            INSERT INTO negocio.empresa_sucursal VALUES ('{Org}', '{Suc}', '{otroEmp}');
            INSERT INTO negocio.cliente VALUES ('{Org}', '{Suc}', '{otro}', '{otroEmp}', 'G-{otro:N}');
            INSERT INTO negocio.parada_extraordinaria(organizacion_id, sucursal_id, id, ruta_id, fecha, sitio_id, fusionada_en)
            SELECT '{Org}', '{Suc}', '{extra}', ruta_id, '{Hoy}', sitio_id, '{e.ParadaA}' FROM negocio.parada p JOIN negocio.parada_version v ON v.parada_id = p.id WHERE p.id = '{e.ParadaA}';
            INSERT INTO negocio.parada_extraordinaria_cliente VALUES ('{Org}', '{Suc}', '{extra}', '{otro}', 5, NULL);
            COMMIT;
            """);
        var t = JsonDocument.Parse(await (await s.Http.GetAsync($"/api/movil/trabajo?fecha={Hoy}")).Content.ReadAsStringAsync()).RootElement;
        var j = t.GetProperty("jornadas").EnumerateArray().Single(x => x.GetProperty("jornadaId").GetGuid() == e.Jornada);
        var paradas = j.GetProperty("paradas").EnumerateArray().ToList();
        Assert.Equal(2, paradas.Count); // A (con la orden fusionada) y B: no hay una parada extraordinaria aparte
        var a = paradas.Single(p => p.GetProperty("paradaId").GetGuid() == e.ParadaA);
        Assert.Equal("plan", a.GetProperty("tipo").GetString());
        var clientes = a.GetProperty("clientes").EnumerateArray().ToList();
        Assert.Equal([false, true], clientes.Select(c => c.GetProperty("extraordinaria").GetBoolean()).ToArray());
        Assert.Equal(17, a.GetProperty("banosAsignados").GetInt32());
        // La captura contra la parada de origen de la orden se resuelve igual que antes.
        Assert.Equal(HttpStatusCode.OK, (await EnviarAsync(s.Http, Succion(s, e, Guid.NewGuid(), extra, e.Jornada, 5, 5))).StatusCode);
    }

    [Fact]
    public async Task Reenvio_idempotente_y_otro_contenido_con_el_mismo_id_se_rechaza_y_sin_tope_de_banos()
    {
        var s = await SesionAsync(); var e = await EscenarioAsync(s); var id = Guid.NewGuid();
        var sobre = Succion(s, e, id, e.ParadaA, e.Jornada, 5000);   // sin tope: la unidad rebasa su capacidad
        Assert.Equal(HttpStatusCode.OK, (await EnviarAsync(s.Http, sobre)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await EnviarAsync(s.Http, sobre)).StatusCode);
        Assert.Equal("1", await bd.ConsultarAsync($"SELECT count(*) FROM negocio.succion_parada WHERE captura_id='{id}'"));
        Assert.Equal("5000|80.000|8000.0", await CargaAsync(e.Unidad));
        Assert.Equal(HttpStatusCode.Conflict, (await EnviarAsync(s.Http, sobre.Replace("a-1", "a-2"))).StatusCode);
    }

    [Fact]
    public async Task Sin_ubicacion_con_dos_fotos_o_con_baños_invalidos_es_422_y_no_deja_nada()
    {
        var s = await SesionAsync(); var e = await EscenarioAsync(s);
        var casos = new[]
        {
            Succion(s, e, Guid.NewGuid(), e.ParadaA, e.Jornada, 3, evidencias: [Foto(Guid.NewGuid(), false)]),
            Succion(s, e, Guid.NewGuid(), e.ParadaA, e.Jornada, 3, evidencias: [Foto(Guid.NewGuid()), Foto(Guid.NewGuid())]),
            Succion(s, e, Guid.NewGuid(), e.ParadaA, e.Jornada, -1),
            Succion(s, e, Guid.NewGuid(), e.ParadaA, e.Jornada, "tres"),
            Succion(s, e, Guid.NewGuid(), e.ParadaA, e.Jornada, 2.5m),
            Tirada(s, e, Guid.NewGuid(), Guid.NewGuid(), [], conLista: false),
            Tirada(s, e, Guid.NewGuid(), Guid.NewGuid(), [Guid.NewGuid()], datos: "no-es-json"),   // 422, no 500
        };
        foreach (var c in casos)
        {
            var id = JsonDocument.Parse(c).RootElement.GetProperty("id").GetGuid();
            Assert.Equal(HttpStatusCode.UnprocessableEntity, (await EnviarAsync(s.Http, c)).StatusCode);
            Assert.Equal("0", await bd.ConsultarAsync($"SELECT count(*) FROM negocio.captura WHERE id='{id}'"));
        }
        Assert.Equal("0|0.000|0.0", await CargaAsync(e.Unidad));
    }

    [Fact]
    public async Task Parada_ajena_a_la_jornada_se_recibe_y_queda_por_revisar_sin_sumar_a_la_carga()
    {
        var s = await SesionAsync(); var e = await EscenarioAsync(s); var id = Guid.NewGuid();
        Assert.Equal(HttpStatusCode.OK, (await EnviarAsync(s.Http, Succion(s, e, id, e.ParadaC, e.Jornada, 9))).StatusCode);   // parada de la otra ruta
        Assert.Equal("parada_no_resuelta", await bd.ConsultarAsync($"SELECT motivo FROM negocio.captura_por_revisar WHERE captura_id='{id}'"));
        Assert.Equal("0|0.000|0.0", await CargaAsync(e.Unidad));
        var estado = JsonDocument.Parse(await (await s.Http.GetAsync($"/api/movil/capturas/{id}")).Content.ReadAsStringAsync()).RootElement;
        Assert.True(estado.GetProperty("enRevision").GetBoolean());
    }

    [Fact]
    public async Task Foto_baños_tirada_barra_a_cero_y_manifiesto_con_folio_consecutivo_sin_exponerlo_al_operador()
    {
        var s = await SesionAsync(); var e = await EscenarioAsync(s);
        var (c1, c2, tirada, capTirada) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        Assert.Equal(HttpStatusCode.OK, (await EnviarAsync(s.Http, Succion(s, e, c1, e.ParadaA, e.Jornada, 10, fecha: Ahora(-120)))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await EnviarAsync(s.Http, Succion(s, e, c2, e.ParadaC, e.Jornada2, 40, 8, fecha: Ahora(-60)))).StatusCode);
        var folioAntes = int.Parse((await bd.ConsultarAsync($"SELECT coalesce(max(ultimo),0) FROM negocio.contador_folio WHERE organizacion_id='{Org}' AND sucursal_id='{Suc}' AND anio={DateTime.UtcNow.Year}")));
        var sobreTirada = Tirada(s, e, capTirada, tirada, [c1, c2]);
        var r = await EnviarAsync(s.Http, sobreTirada);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var cuerpo = (await r.Content.ReadAsStringAsync()).ToLowerInvariant();
        Assert.DoesNotContain("folio", cuerpo); Assert.DoesNotContain("manifiesto", cuerpo);
        Assert.Equal("1", await ManifiestosDeTiradaAsync(tirada));
        Assert.Equal($"{folioAntes + 1:000}/{DateTime.UtcNow.Year}", await FolioDeAsync(tirada));   // contador compartido con fosa fija
        Assert.Equal("0|0.000|0.0", await CargaAsync(e.Unidad));                                       // la barra reinicia en las dos rutas de la unidad
        Assert.Equal("PTAR NORTE", await bd.ConsultarAsync($"SELECT planta_nombre FROM negocio.tirada WHERE id='{tirada}'"));   // de la base, no del teléfono
        // El estado de la captura dice si el manifiesto ya se armó, sin folio ni datos.
        var estado = (await (await s.Http.GetAsync($"/api/movil/capturas/{capTirada}")).Content.ReadAsStringAsync());
        Assert.True(JsonDocument.Parse(estado).RootElement.GetProperty("manifiestoArmado").GetBoolean());
        Assert.DoesNotContain("folio", estado.ToLowerInvariant());
        var estadoSuccion = JsonDocument.Parse(await (await s.Http.GetAsync($"/api/movil/capturas/{c1}")).Content.ReadAsStringAsync()).RootElement;
        Assert.True(estadoSuccion.GetProperty("incluidaEnTirada").GetBoolean());
        // Reenvío de la tirada: no crea otro manifiesto ni consume folio.
        Assert.Equal(HttpStatusCode.OK, (await EnviarAsync(s.Http, sobreTirada)).StatusCode);
        Assert.Equal("1", await ManifiestosDeTiradaAsync(tirada));
        Assert.Equal(folioAntes + 1, int.Parse(await bd.ConsultarAsync($"SELECT max(ultimo) FROM negocio.contador_folio WHERE organizacion_id='{Org}' AND sucursal_id='{Suc}' AND anio={DateTime.UtcNow.Year}")));
        // Una succión posterior vuelve a sumar desde 0.
        Assert.Equal(HttpStatusCode.OK, (await EnviarAsync(s.Http, Succion(s, e, Guid.NewGuid(), e.ParadaB, e.Jornada, 20, 20))).StatusCode);
        Assert.Equal("20|0.320|32.0", await CargaAsync(e.Unidad));

        // El panel lo ve: tipo sanitarios, sin cliente, planta, totales en m3 y paradas con coordenadas; sin litros.
        var correo = $"san-web-{Guid.NewGuid():N}@example.invalid";
        await bd.AutorizarAsync(correo);
        var nav = _api.NuevoNavegador();
        Assert.Equal(HttpStatusCode.NoContent, (await nav.EntrarAsync(ValidadorFicticio.Token($"s-{correo}", correo))).StatusCode);
        var texto = await nav.GetStringAsync("/api/manifiestos");
        var m = JsonDocument.Parse(texto).RootElement.EnumerateArray().Single(x => x.GetProperty("folio").GetString() == $"{folioAntes + 1:000}/{DateTime.UtcNow.Year}" && x.GetProperty("tipo").GetString() == "sanitarios");
        Assert.Equal(JsonValueKind.Null, m.GetProperty("cliente").ValueKind);
        // La fecha del manifiesto es la de la tirada (cuando firma quien recibe), no la del momento en que el servidor lo armó.
        var fechaTirada = DateTimeOffset.FromUnixTimeMilliseconds(long.Parse(await bd.ConsultarAsync($"SELECT floor(extract(epoch FROM fecha_captura) * 1000)::bigint FROM negocio.tirada WHERE id='{tirada}'")));
        Assert.True(Math.Abs((fechaTirada - m.GetProperty("fecha").GetDateTimeOffset()).TotalMilliseconds) < 1);
        var san = m.GetProperty("sanitarios");
        Assert.Equal("PTAR NORTE", san.GetProperty("planta").GetString());
        Assert.Equal("Quien Recibe", san.GetProperty("recibe").GetString());
        Assert.Equal(50, san.GetProperty("totalBanos").GetInt32());
        Assert.Equal(1m, san.GetProperty("totalM3Estimado").GetDecimal());   // capacidad de la unidad: siempre se tira llena (no baños x 16 L)
        Assert.Equal("capacidad_unidad_llena", san.GetProperty("metodo").GetString());
        var paradas = san.GetProperty("paradas").EnumerateArray().ToList();
        Assert.Equal(2, paradas.Count);
        Assert.Equal("Bodega Central", paradas[0].GetProperty("sitio").GetString());   // cronológico: la primera succión
        Assert.Equal(25.6702m, paradas[0].GetProperty("latitud").GetDecimal());
        Assert.DoesNotContain("litros", m.GetRawText().ToLowerInvariant());
        var id = m.GetProperty("id").GetGuid();
        var documento = await ImprimirTiradaAsync(s, capTirada, id, nav);
        Assert.Equal(2, documento.Impreso!.Sanitarios!.Paradas.Count);
        Assert.Equal(25.6702m, documento.Impreso.Sanitarios.Paradas[0].Latitud);
        await CargarFirmaEmpresaAsync();
        using var repetir = await nav.PrepararImpresionAsync(id);
        Assert.Equal(HttpStatusCode.OK, repetir.StatusCode);
        var reimpreso = (await repetir.Content.ReadFromJsonAsync<CodigoValidacion>())!;
        Assert.Equal(documento.Codigo, reimpreso.Codigo);
        Assert.Equal(documento.Impreso.GeneradorPrecargado, reimpreso.Impreso!.GeneradorPrecargado);
    }

    [Fact]
    public async Task Ordenes_informa_la_atencion_real_de_cada_parada_de_sanitarios_capturada_y_tirada()
    {
        var s = await SesionAsync(); var e = await EscenarioAsync(s);
        var correo = $"san-web-{Guid.NewGuid():N}@example.invalid";
        await bd.AutorizarAsync(correo);
        var web = _api.NuevoNavegador();
        Assert.Equal(HttpStatusCode.NoContent, (await web.EntrarAsync(ValidadorFicticio.Token($"s-{correo}", correo))).StatusCode);
        var ruta = Guid.Parse(await bd.ConsultarAsync($"SELECT ruta_id FROM negocio.jornada WHERE id='{e.Jornada}'"));
        async Task<System.Text.Json.JsonElement> ParadaAAsync()
        {
            var rutas = JsonDocument.Parse(await web.GetStringAsync($"/api/ordenes?fecha={Hoy}")).RootElement;
            return rutas.EnumerateArray().Single(r => r.GetProperty("id").GetGuid() == ruta).GetProperty("paradas").EnumerateArray().Single(p => p.GetProperty("id").GetGuid() == e.ParadaA);
        }
        // Sin captura: sin atención.
        Assert.False((await ParadaAAsync()).TryGetProperty("atencion", out var vacia) && vacia.ValueKind != JsonValueKind.Null);
        // Capturada la succión: baños, hora y todavía sin tirar.
        var (c1, c2, tirada, capTirada) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        Assert.Equal(HttpStatusCode.OK, (await EnviarAsync(s.Http, Succion(s, e, c1, e.ParadaA, e.Jornada, 10, fecha: Ahora(-120)))).StatusCode);
        var atendida = (await ParadaAAsync()).GetProperty("atencion");
        Assert.Equal((10, false), (atendida.GetProperty("banosSuccionados").GetInt32(), atendida.GetProperty("tirada").GetBoolean()));
        Assert.Equal(JsonValueKind.String, atendida.GetProperty("capturadaEn").ValueKind);
        // Lo asignado que cubrió la captura viaja con la atención: el panel lo compara con lo asignado hoy para saber si se agregaron baños después.
        Assert.True(atendida.GetProperty("banosCubiertos").GetInt32() > 0);
        // Cada captura lleva dónde se tomó su foto (punto del mapa de Monitoreo).
        var captura = Assert.Single(atendida.GetProperty("capturas").EnumerateArray());
        Assert.Equal((25.6702m, -100.3091m), (captura.GetProperty("latitud").GetDecimal(), captura.GetProperty("longitud").GetDecimal()));
        Assert.Equal(8m, captura.GetProperty("precision").GetDecimal());
        // Tirada en la planta: la parada pasa a tirada.
        Assert.Equal(HttpStatusCode.OK, (await EnviarAsync(s.Http, Succion(s, e, c2, e.ParadaC, e.Jornada2, 40, 8, fecha: Ahora(-60)))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await EnviarAsync(s.Http, Tirada(s, e, capTirada, tirada, [c1, c2]))).StatusCode);
        Assert.True((await ParadaAAsync()).GetProperty("atencion").GetProperty("tirada").GetBoolean());
    }

    [Fact]
    public async Task Una_planta_con_una_captura_por_revisar_no_se_elimina_y_sin_pendientes_se_oculta_conservando_su_historial()
    {
        var s = await SesionAsync(); var e = await EscenarioAsync(s);
        var correo = $"san-admin-{Guid.NewGuid():N}@example.invalid";
        await bd.AutorizarAsync(correo, rol: "administrador", escribir: true);
        var web = _api.NuevoNavegador();
        Assert.Equal(HttpStatusCode.NoContent, (await web.EntrarAsync(ValidadorFicticio.Token($"s-{correo}", correo))).StatusCode);
        async Task<HttpResponseMessage> EliminarAsync()
        {
            var req = new HttpRequestMessage(HttpMethod.Delete, $"/api/plantas/{e.Planta}");
            req.Headers.Add(ServiciosAcceso.CabeceraCsrf, await web.CsrfAsync());
            return await web.SendAsync(req);
        }
        // Una tirada a esta planta que quedó por revisar es un proceso pendiente: la planta no se puede eliminar.
        var capVacia = Guid.NewGuid();
        Assert.Equal(HttpStatusCode.OK, (await EnviarAsync(s.Http, Tirada(s, e, capVacia, Guid.NewGuid(), []))).StatusCode);
        Assert.Equal("tirada_sin_carga", await bd.ConsultarAsync($"SELECT motivo FROM negocio.captura_por_revisar WHERE captura_id='{capVacia}'"));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await EliminarAsync()).StatusCode);
        Assert.Equal("0", await bd.ConsultarAsync($"SELECT count(*) FROM negocio.sitio_baja WHERE sitio_id='{e.Planta}'"));
        // Con una descarga válida (ya en el historial) y sin nada pendiente de ella se oculta: la baja no borra nada.
        var plantaLimpia = Guid.NewGuid();
        await bd.SqlComoDuenoAsync($"INSERT INTO negocio.sitio VALUES ('{Org}', '{Suc}', '{plantaLimpia}', 'Planta sin pendientes'); INSERT INTO negocio.ficha_sitio(organizacion_id, sucursal_id, sitio_id, revision, nombre) VALUES ('{Org}', '{Suc}', '{plantaLimpia}', 1, 'Planta sin pendientes');");
        var req2 = new HttpRequestMessage(HttpMethod.Delete, $"/api/plantas/{plantaLimpia}");
        req2.Headers.Add(ServiciosAcceso.CabeceraCsrf, await web.CsrfAsync());
        Assert.Equal(HttpStatusCode.NoContent, (await web.SendAsync(req2)).StatusCode);
        Assert.Equal("1", await bd.ConsultarAsync($"SELECT count(*) FROM negocio.sitio WHERE id='{plantaLimpia}'"));   // oculta, no borrada
        Assert.Equal("1", await bd.ConsultarAsync($"SELECT count(*) FROM negocio.sitio_baja WHERE sitio_id='{plantaLimpia}'"));
    }

    [Fact]
    public async Task Manifiesto_reparte_la_capacidad_por_baños_y_varia_3_por_ciento_entre_paradas_con_los_mismos_baños()
    {
        var s = await SesionAsync(); var e = await EscenarioAsync(s);
        var (c1, c2, c3, tirada, capTirada) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        Assert.Equal(HttpStatusCode.OK, (await EnviarAsync(s.Http, Succion(s, e, c1, e.ParadaA, e.Jornada, 10, fecha: Ahora(-30)))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await EnviarAsync(s.Http, Succion(s, e, c2, e.ParadaC, e.Jornada2, 10, fecha: Ahora(-20)))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await EnviarAsync(s.Http, Succion(s, e, c3, e.ParadaB, e.Jornada, 20, 20, fecha: Ahora(-10)))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await EnviarAsync(s.Http, Tirada(s, e, capTirada, tirada, [c1, c2, c3]))).StatusCode);
        Assert.Equal("1", await ManifiestosDeTiradaAsync(tirada));
        // Unidad de 1 m³ y 40 baños: 10 baños = 0.250 m³ en cada parada igual, una +3 % y la otra −3 %; los 20 baños = 0.500 m³ sin variación.
        Assert.Equal("0.258|0.242|0.500", await bd.ConsultarAsync($"SELECT string_agg(volumen_m3::text, '|' ORDER BY renglon) FROM negocio.manifiesto_parada WHERE captura_id IN ('{c1}','{c2}','{c3}')"));
        // El total repartido es EXACTAMENTE la capacidad de la unidad (la milésima que sobra se reparte, no se pierde).
        Assert.Equal("1.000", await bd.ConsultarAsync($"SELECT sum(volumen_m3)::text FROM negocio.manifiesto_parada WHERE captura_id IN ('{c1}','{c2}','{c3}')"));
    }

    [Fact]
    public async Task Tirada_mezclada_de_fosas_propias_y_banos_un_folio_16_litros_por_bano_y_el_resto_entre_las_fosas()
    {
        var s = await SesionAsync(); var e = await EscenarioAsync(s);
        var (c1, c2, c3, tirada, capTirada) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        string Fosa(Guid captura, Guid parada, Guid jornada, long fecha) =>
            Sobre(s, captura, "SERVICIO_SANITARIOS", parada, jornada, "{\"dato\":\"sintetico\"}", [Foto(Guid.NewGuid(), true, fecha)], new { banos = 0, banosAsignados = 0, fosa = true }, fecha: fecha);
        // Una fosa propia no lleva cantidad de baños (422 y no deja nada).
        var invalida = Sobre(s, Guid.NewGuid(), "SERVICIO_SANITARIOS", e.ParadaA, e.Jornada, "{\"dato\":\"sintetico\"}", [Foto(Guid.NewGuid(), true, Ahora(-40))], new { banos = 3, banosAsignados = 0, fosa = true }, fecha: Ahora(-40));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await EnviarAsync(s.Http, invalida)).StatusCode);
        // 10 baños + 2 fosas propias, unidad de 1 m³ (62.5 baños equivalentes a 16 L).
        Assert.Equal(HttpStatusCode.OK, (await EnviarAsync(s.Http, Succion(s, e, c1, e.ParadaA, e.Jornada, 10, fecha: Ahora(-30)))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await EnviarAsync(s.Http, Fosa(c2, e.ParadaC, e.Jornada2, Ahora(-20)))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await EnviarAsync(s.Http, Fosa(c3, e.ParadaB, e.Jornada, Ahora(-10)))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await EnviarAsync(s.Http, Tirada(s, e, capTirada, tirada, [c1, c2, c3]))).StatusCode);
        Assert.Equal("1", await ManifiestosDeTiradaAsync(tirada));
        Assert.Matches(@"^[0-9]{3}/[0-9]{4}$", await FolioDeAsync(tirada));   // un solo folio por tirada
        // Baños a 16 L (10 = 0.160 m³); el resto (0.840) entre las dos fosas con variación ±3 %: no son iguales y suman exacto.
        Assert.Equal("0.160|0.433|0.407", await bd.ConsultarAsync($"SELECT string_agg(volumen_m3::text, '|' ORDER BY renglon) FROM negocio.manifiesto_parada WHERE captura_id IN ('{c1}','{c2}','{c3}')"));
        Assert.Equal("1.000", await bd.ConsultarAsync($"SELECT sum(volumen_m3)::text FROM negocio.manifiesto_parada WHERE captura_id IN ('{c1}','{c2}','{c3}')"));
        Assert.Equal("false|true|true", await bd.ConsultarAsync($"SELECT string_agg(es_fosa::text, '|' ORDER BY renglon) FROM negocio.manifiesto_parada WHERE captura_id IN ('{c1}','{c2}','{c3}')"));
        // Un solo renglón: «Excreta humana», sanitarios y fosa, 1 m³ (la capacidad de la unidad), 12 contenedores (10 baños + 2 fosas).
        Assert.Equal("Excreta humana|sanitario y fosa|12|1.000000", await bd.ConsultarAsync($"SELECT d.descripcion_residuo||'|'||d.tipo_contenedor||'|'||d.numero_contenedores||'|'||d.cantidad FROM negocio.detalle_manifiesto d JOIN negocio.version_manifiesto v ON v.id=d.version_id JOIN negocio.tirada_manifiesto tm ON tm.manifiesto_id=v.manifiesto_id WHERE tm.tirada_id='{tirada}'"));
        var correo = $"imprimir-mixta-{Guid.NewGuid():N}@example.invalid";
        await bd.AutorizarAsync(correo);
        using var panel = _api.NuevoNavegador();
        Assert.Equal(HttpStatusCode.NoContent, (await panel.EntrarAsync(ValidadorFicticio.Token($"s-{correo}", correo))).StatusCode);
        var id = Guid.Parse(await bd.ConsultarAsync($"SELECT manifiesto_id FROM negocio.tirada_manifiesto WHERE tirada_id='{tirada}'"));
        var documento = await ImprimirTiradaAsync(s, capTirada, id, panel);
        Assert.Equal(await FolioDeAsync(tirada), documento.Impreso!.Folio);
        Assert.Equal([false, true, true], documento.Impreso.Sanitarios!.Paradas.Select(p => p.EsFosa));
        Assert.Equal(1m, documento.Impreso.Sanitarios.Paradas.Sum(p => p.VolumenM3));
        using var publico = _api.NuevoNavegador();
        var verificado = (await publico.GetFromJsonAsync<ResultadoValidacion>($"/api/validacion/{documento.Codigo}"))!;
        Assert.Equal("PTAR NORTE", verificado.Datos!.Destinatario);
        Assert.Equal(3, verificado.Datos.Firmas);
    }

    [Fact]
    public async Task Tirada_solo_de_fosas_propias_tambien_arma_manifiesto_con_descripcion_de_excreta_humana()
    {
        var s = await SesionAsync(); var e = await EscenarioAsync(s);
        var (c1, c2, tirada, capTirada) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        string Fosa(Guid captura, Guid parada, Guid jornada, long fecha) =>
            Sobre(s, captura, "SERVICIO_SANITARIOS", parada, jornada, "{\"dato\":\"sintetico\"}", [Foto(Guid.NewGuid(), true, fecha)], new { banos = 0, banosAsignados = 0, fosa = true }, fecha: fecha);
        Assert.Equal(HttpStatusCode.OK, (await EnviarAsync(s.Http, Fosa(c1, e.ParadaA, e.Jornada, Ahora(-20)))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await EnviarAsync(s.Http, Fosa(c2, e.ParadaB, e.Jornada, Ahora(-10)))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await EnviarAsync(s.Http, Tirada(s, e, capTirada, tirada, [c1, c2]))).StatusCode);
        Assert.Equal("1", await ManifiestosDeTiradaAsync(tirada));
        Assert.Equal("0.515|0.485", await bd.ConsultarAsync($"SELECT string_agg(volumen_m3::text, '|' ORDER BY renglon) FROM negocio.manifiesto_parada WHERE captura_id IN ('{c1}','{c2}')"));
        Assert.Equal("Excreta humana|fosa", await bd.ConsultarAsync($"SELECT d.descripcion_residuo||'|'||d.tipo_contenedor FROM negocio.detalle_manifiesto d JOIN negocio.version_manifiesto v ON v.id=d.version_id JOIN negocio.tirada_manifiesto tm ON tm.manifiesto_id=v.manifiesto_id WHERE tm.tirada_id='{tirada}'"));
    }

    [Fact]
    public async Task Cambio_de_operador_del_dia_permite_capturar_al_suplente_aunque_la_unidad_sea_de_otro_y_le_llega_su_trabajo()
    {
        var s = await SesionAsync(); var e = await EscenarioAsync(s);
        var titular = Guid.NewGuid();
        await bd.SqlComoDuenoAsync($"""
            INSERT INTO seguridad.persona(organizacion_id,id,nombre_completo) VALUES('{Org}','{titular}','Titular ficticio');
            INSERT INTO negocio.operador_ficha(organizacion_id,sucursal_id,persona_id,revision,nombre_completo,correo) VALUES('{Org}','{Suc}','{titular}',1,'Titular ficticio',NULL);
            INSERT INTO negocio.asignacion_unidad(organizacion_id, sucursal_id, vehiculo_id, revision, persona_id) VALUES ('{Org}', '{Suc}', '{e.Unidad}', 2, '{titular}');
            """);
        // El teléfono ya no es el de la persona asignada a la unidad: no ve el trabajo y su captura no se resuelve.
        var trabajoAntes = await s.Http.GetStringAsync($"/api/movil/trabajo?fecha={Hoy}");
        Assert.DoesNotContain(e.Jornada.ToString(), trabajoAntes);
        var c0 = Guid.NewGuid();
        Assert.Equal(HttpStatusCode.OK, (await EnviarAsync(s.Http, Succion(s, e, c0, e.ParadaA, e.Jornada, 10))).StatusCode);
        Assert.Equal("1", await bd.ConsultarAsync($"SELECT count(*) FROM negocio.captura_por_revisar WHERE captura_id='{c0}'"));
        // Operaciones lo pone de operador solo por hoy (misma unidad): ve el trabajo y su captura se resuelve.
        await bd.SqlComoDuenoAsync($"""
            INSERT INTO negocio.cambio_unidad_dia(organizacion_id, sucursal_id, ruta_id, fecha, revision, vehiculo_id, persona_id)
            SELECT '{Org}', '{Suc}', ruta_id, '{Hoy}'::date, 1, '{e.Unidad}', '{s.Persona}' FROM negocio.jornada WHERE id = '{e.Jornada}';
            """);
        Assert.Contains(e.Jornada.ToString(), await s.Http.GetStringAsync($"/api/movil/trabajo?fecha={Hoy}"));
        var c1 = Guid.NewGuid();
        Assert.Equal(HttpStatusCode.OK, (await EnviarAsync(s.Http, Succion(s, e, c1, e.ParadaA, e.Jornada, 10))).StatusCode);
        Assert.Equal("0", await bd.ConsultarAsync($"SELECT count(*) FROM negocio.captura_por_revisar WHERE captura_id='{c1}'"));
        Assert.Equal("1", await bd.ConsultarAsync($"SELECT count(*) FROM negocio.succion_parada WHERE captura_id='{c1}'"));
    }

    [Fact]
    public async Task Una_captura_hecha_antes_del_cambio_de_unidad_se_queda_con_la_unidad_que_tenia_aunque_llegue_despues()
    {
        var s = await SesionAsync(); var e = await EscenarioAsync(s);
        var u2 = Guid.NewGuid();
        await bd.SqlComoDuenoAsync($"""
            INSERT INTO negocio.vehiculo VALUES ('{Org}', '{Suc}', '{u2}', 'CU2-{u2.ToString("N")[..5]}');
            INSERT INTO negocio.ficha_vehiculo(organizacion_id, sucursal_id, vehiculo_id, revision, capacidad_id, clasificacion) VALUES ('{Org}', '{Suc}', '{u2}', 1, 4, 'sanitarios');
            """);
        var antes = Guid.NewGuid(); var despues = Guid.NewGuid();
        var sobreAnterior = Succion(s, e, antes, e.ParadaA, e.Jornada, 10, fecha: Ahora(-60));
        // La captura se HIZO hace una hora; Operaciones cambia la unidad de la ruta ahora; la captura llega después del cambio.
        var cambio = bd.SqlComoDuenoAsync($"""
            INSERT INTO negocio.cambio_unidad_dia(organizacion_id, sucursal_id, ruta_id, fecha, revision, vehiculo_id, persona_id)
            SELECT '{Org}', '{Suc}', ruta_id, '{Hoy}'::date, 1, '{u2}', NULL FROM negocio.jornada WHERE id = '{e.Jornada}';
            """);
        await cambio;
        using var primera = await EnviarAsync(s.Http, sobreAnterior);
        using var repetida = await EnviarAsync(s.Http, sobreAnterior);
        Assert.Equal(HttpStatusCode.OK, primera.StatusCode);
        Assert.Equal(HttpStatusCode.OK, repetida.StatusCode);
        Assert.Equal(await primera.Content.ReadAsStringAsync(), await repetida.Content.ReadAsStringAsync());
        // Lo que se hizo antes del cambio se queda con la unidad del plan y no cae a revisión.
        Assert.Equal("0", await bd.ConsultarAsync($"SELECT count(*) FROM negocio.captura_por_revisar WHERE captura_id='{antes}'"));
        Assert.Equal($"{e.Unidad}|{s.Persona}", await bd.ConsultarAsync($"SELECT vehiculo_id||'|'||persona_id FROM negocio.succion_parada WHERE captura_id='{antes}'"));
        Assert.Equal("1", await bd.ConsultarAsync($"SELECT count(*) FROM negocio.succion_parada WHERE captura_id='{antes}'"));
        Assert.Equal("1", await bd.ConsultarAsync($"SELECT count(*) FROM negocio.evidencia_captura WHERE captura_id='{antes}'"));
        // Lo que se hace después del cambio es de la otra unidad: este teléfono no la opera, así que no se resuelve sin más.
        Assert.Equal(HttpStatusCode.OK, (await EnviarAsync(s.Http, Succion(s, e, despues, e.ParadaA, e.Jornada, 5, fecha: Ahora(5)))).StatusCode);
        Assert.Equal("0", await bd.ConsultarAsync($"SELECT count(*) FROM negocio.succion_parada WHERE captura_id='{despues}'"));
        Assert.Equal("parada_no_resuelta", await bd.ConsultarAsync($"SELECT motivo FROM negocio.captura_por_revisar WHERE captura_id='{despues}'"));
    }

    [Fact]
    public async Task Tirada_que_llega_antes_que_su_succion_espera_y_se_arma_al_llegar()
    {
        var s = await SesionAsync(); var e = await EscenarioAsync(s);
        var (c1, c2, tirada, capTirada) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        Assert.Equal(HttpStatusCode.OK, (await EnviarAsync(s.Http, Succion(s, e, c1, e.ParadaA, e.Jornada, 10, fecha: Ahora(-30)))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await EnviarAsync(s.Http, Tirada(s, e, capTirada, tirada, [c1, c2]))).StatusCode);
        Assert.Equal("0", await ManifiestosDeTiradaAsync(tirada));
        Assert.Equal("esperando|1", await bd.ConsultarAsync($"SELECT estado||'|'||faltantes FROM negocio.estado_tirada WHERE tirada_id='{tirada}'"));
        var espera = JsonDocument.Parse(await (await s.Http.GetAsync($"/api/movil/capturas/{capTirada}")).Content.ReadAsStringAsync()).RootElement;
        Assert.False(espera.GetProperty("manifiestoArmado").GetBoolean());
        Assert.Equal(c2, espera.GetProperty("capturasFaltantes")[0].GetGuid());
        Assert.Equal("10|0.160|16.0", await CargaAsync(e.Unidad));   // esperando no reinicia la barra
        Assert.Equal(HttpStatusCode.OK, (await EnviarAsync(s.Http, Succion(s, e, c2, e.ParadaC, e.Jornada2, 5, fecha: Ahora(-20)))).StatusCode);
        Assert.Equal("1", await ManifiestosDeTiradaAsync(tirada));
        Assert.Equal("0|0.000|0.0", await CargaAsync(e.Unidad));
        // La coordenada de la foto de la succión tardía ya estaba guardada cuando se armó.
        Assert.Equal("2|0", await bd.ConsultarAsync($"SELECT count(*)||'|'||count(*) FILTER (WHERE latitud IS NULL) FROM negocio.manifiesto_parada WHERE captura_id IN ('{c1}','{c2}')"));
    }

    [Fact]
    public async Task Doble_tirada_con_las_mismas_capturas_no_duplica_manifiesto_ni_consume_folio_y_la_succion_tardia_queda_en_la_carga()
    {
        var s = await SesionAsync(); var e = await EscenarioAsync(s);
        var (c1, t1, t2, cap1, cap2) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        Assert.Equal(HttpStatusCode.OK, (await EnviarAsync(s.Http, Succion(s, e, c1, e.ParadaA, e.Jornada, 10, fecha: Ahora(-10)))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await EnviarAsync(s.Http, Tirada(s, e, cap1, t1, [c1]))).StatusCode);
        var folio = await FolioDeAsync(t1);
        var ultimo = await bd.ConsultarAsync($"SELECT max(ultimo) FROM negocio.contador_folio WHERE organizacion_id='{Org}' AND sucursal_id='{Suc}' AND anio={DateTime.UtcNow.Year}");
        Assert.Equal(HttpStatusCode.OK, (await EnviarAsync(s.Http, Tirada(s, e, cap2, t2, [c1]))).StatusCode);   // doble toque con otro id
        Assert.Equal("tirada_capturas_ya_incluidas", await bd.ConsultarAsync($"SELECT motivo FROM negocio.captura_por_revisar WHERE captura_id='{cap2}'"));
        Assert.Equal("0", await ManifiestosDeTiradaAsync(t2));
        Assert.Equal(ultimo, await bd.ConsultarAsync($"SELECT max(ultimo) FROM negocio.contador_folio WHERE organizacion_id='{Org}' AND sucursal_id='{Suc}' AND anio={DateTime.UtcNow.Year}"));
        Assert.Equal(folio, await FolioDeAsync(t1));
        // Succión tardía: fecha anterior a la tirada ya armada, llega después; no estaba declarada, así que queda en la carga actual.
        var tardia = Guid.NewGuid();
        Assert.Equal(HttpStatusCode.OK, (await EnviarAsync(s.Http, Succion(s, e, tardia, e.ParadaB, e.Jornada, 7, fecha: Ahora(-300)))).StatusCode);
        Assert.Equal("7|0.112|11.2", await CargaAsync(e.Unidad));
        Assert.Equal("1", await ManifiestosDeTiradaAsync(t1));
        // Tirada sin capturas incluidas: por revisar, no manifiesto.
        var capVacia = Guid.NewGuid();
        Assert.Equal(HttpStatusCode.OK, (await EnviarAsync(s.Http, Tirada(s, e, capVacia, Guid.NewGuid(), []))).StatusCode);
        Assert.Equal("tirada_sin_carga", await bd.ConsultarAsync($"SELECT motivo FROM negocio.captura_por_revisar WHERE captura_id='{capVacia}'"));
    }

    [Fact]
    public async Task Problema_por_parada_se_acepta_sin_cerrar_nada_y_su_foto_crea_incidencia_de_sanitario()
    {
        var s = await SesionAsync(); var e = await EscenarioAsync(s);
        var foto = Guid.NewGuid(); byte[] bytes = ImagenesDePrueba.Jpeg(3); var captura = Guid.NewGuid();
        var sobre = Sobre(s, captura, "PROBLEMA", e.ParadaA, e.Jornada, "{\"dato\":\"sintetico\"}",
            [new { id = foto, rol = "FOTO", sha256 = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes)), tamano = bytes.Length,
                ubicacion = new { latitud = 25.6, longitud = -100.3, precisionMetros = 9.0, fechaEpochMs = Ahora() } }],
            null, problema: new { motivo = "sin_acceso", comentario = "Portón cerrado" });
        Assert.Equal(HttpStatusCode.OK, (await EnviarAsync(s.Http, sobre)).StatusCode);
        Assert.Equal("0", await bd.ConsultarAsync($"SELECT count(*) FROM negocio.captura_por_revisar WHERE captura_id='{captura}'"));
        var contenido = new ByteArrayContent(bytes); contenido.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
        Assert.Equal(HttpStatusCode.OK, (await s.Http.PutAsync($"/api/movil/archivos/{foto}", contenido)).StatusCode);
        Assert.Equal("sanitario|sin_acceso|12", await bd.ConsultarAsync($"SELECT servicio||'|'||motivo||'|'||banos_asignados FROM negocio.incidencia WHERE evidencia_archivo_id='{foto}'"));
        Assert.Equal("0|0.000|0.0", await CargaAsync(e.Unidad));
    }
}
