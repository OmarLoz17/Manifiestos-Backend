using Microsoft.Extensions.DependencyInjection;
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
        bool aceptado = true, object? planta = null, string? nombre = null, object? problema = null, int version = 1,
        Guid? orden = null, Guid? jornada = null, bool requiereCliente = true)
    {
        var o = s.Yo.Operador;
        return JsonSerializer.Serialize(new
        {
            version, id,
            contexto = new { ambitoId = "ambito", personaId = persona ?? o.PersonaId, identidadId = o.IdentidadId, instalacionId = o.InstalacionId,
                asignacionEquipoId = o.AsignacionEquipoId, revisionEquipo = o.RevisionEquipo.ToString() },
            trabajo = new { ordenId = orden ?? Guid.NewGuid(), jornadaId = jornada ?? Guid.NewGuid(), asignacionTrabajoId = "a-1", revisionTrabajo = "1",
                requiereFirmaCliente = requiereCliente, versionDatos = "1", datosOrdenJson = "{\"dato\":\"sintetico\"}" },
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

    // ---- Fosa fija: una orden = un manifiesto, armado por el servidor al llegar todos los pasos ----

    private static string Hoy => DateOnly.FromDateTime(DateTime.UtcNow).ToString("yyyy-MM-dd");
    private sealed record Orden(Guid Jornada, Guid[] Ordenes, Guid Cliente);

    /// <summary>Ruta publicada hoy con un cliente y N fosas planeadas; devuelve las órdenes derivadas tal como las descarga el teléfono.</summary>
    private async Task<Orden> OrdenesAsync(Sesion s, int fosas, bool autorizado)
    {
        var (ruta, unidad, parada, sitio, version, empresa, cliente, jornada) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        await bd.SqlComoDuenoAsync($"""
            BEGIN;
            INSERT INTO negocio.ficha_empresa(organizacion_id, empresa_id, revision, nombre_fiscal, nombre_comercial)
            SELECT '{Org}', '0f000000-0000-4000-8000-000000000002', 1, 'Transportes de Prueba SA de CV', 'Transportes de Prueba'
            WHERE NOT EXISTS (SELECT 1 FROM negocio.ficha_empresa WHERE empresa_id = '0f000000-0000-4000-8000-000000000002');
            INSERT INTO negocio.vehiculo VALUES ('{Org}', '{Suc}', '{unidad}', 'FF-{unidad.ToString("N")[..5]}');
            INSERT INTO negocio.ficha_vehiculo(organizacion_id, sucursal_id, vehiculo_id, revision, capacidad_id, clasificacion) VALUES ('{Org}', '{Suc}', '{unidad}', 1, 2, 'fosas');
            INSERT INTO negocio.ruta(organizacion_id, sucursal_id, id) VALUES ('{Org}', '{Suc}', '{ruta}');
            INSERT INTO negocio.ruta_version(organizacion_id, sucursal_id, ruta_id, revision, nombre, dias, vehiculo_id) VALUES ('{Org}', '{Suc}', '{ruta}', 1, 'Ruta Fosa Fija', 127, '{unidad}');
            INSERT INTO negocio.asignacion_unidad(organizacion_id, sucursal_id, vehiculo_id, revision, persona_id) VALUES ('{Org}', '{Suc}', '{unidad}', 1, '{s.Equipo.Persona}');
            INSERT INTO negocio.empresa VALUES ('{Org}', '{empresa}', 'CLI-{empresa:N}');
            INSERT INTO negocio.ficha_empresa(organizacion_id, empresa_id, revision, nombre_comercial) VALUES ('{Org}', '{empresa}', 1, 'Cliente Fosa Fija');
            INSERT INTO negocio.empresa_sucursal VALUES ('{Org}', '{Suc}', '{empresa}');
            INSERT INTO negocio.cliente VALUES ('{Org}', '{Suc}', '{cliente}', '{empresa}', 'F-{cliente:N}');
            INSERT INTO negocio.sitio VALUES ('{Org}', '{Suc}', '{sitio}', 'Sitio fosa fija');
            INSERT INTO negocio.punto_servicio VALUES ('{Org}', '{Suc}', '{sitio}');
            INSERT INTO negocio.parada(organizacion_id, sucursal_id, id, ruta_id, posicion) VALUES ('{Org}', '{Suc}', '{parada}', '{ruta}', 1);
            INSERT INTO negocio.parada_version(organizacion_id, sucursal_id, id, parada_id, revision, sitio_id) VALUES ('{Org}', '{Suc}', '{version}', '{parada}', 1, '{sitio}');
            INSERT INTO negocio.parada_cliente(organizacion_id, sucursal_id, version_id, cliente_id, fosas_planeadas) VALUES ('{Org}', '{Suc}', '{version}', '{cliente}', {fosas});
            {(autorizado ? $"INSERT INTO negocio.autorizacion_empresa(organizacion_id, empresa_id, autoridad, finalidad, numero) VALUES ('{Org}', '{empresa}', 'Medio ambiente', 'Generador', 'AUT-FF');" : "")}
            INSERT INTO negocio.jornada(organizacion_id, sucursal_id, id, ruta_id, fecha) VALUES ('{Org}', '{Suc}', '{jornada}', '{ruta}', '{Hoy}');
            INSERT INTO negocio.estado_jornada(organizacion_id, sucursal_id, jornada_id, revision, estado) VALUES ('{Org}', '{Suc}', '{jornada}', 1, 'programada');
            COMMIT;
            """);
        var t = JsonDocument.Parse(await (await s.Http.GetAsync($"/api/movil/trabajo?fecha={Hoy}")).Content.ReadAsStringAsync()).RootElement;
        var j = t.GetProperty("jornadas").EnumerateArray().Single(x => x.GetProperty("jornadaId").GetGuid() == jornada);
        var ordenes = j.GetProperty("paradas")[0].GetProperty("clientes")[0].GetProperty("ordenes").EnumerateArray().Select(o => o.GetProperty("ordenId").GetGuid()).ToArray();
        Assert.Equal(fosas, ordenes.Length);
        return new Orden(jornada, ordenes, cliente);
    }

    private Task<HttpResponseMessage> PasoAsync(Sesion s, Orden o, int cual, string paso, bool requiereCliente = true) => EnviarAsync(s.Http, paso switch
    {
        "servicio" => Sobre(s, Guid.NewGuid(), "SERVICIO_OPERADOR", orden: o.Ordenes[cual], jornada: o.Jornada, requiereCliente: requiereCliente),
        "cliente" => Sobre(s, Guid.NewGuid(), "FIRMA_CLIENTE", evidencias: [Firma(Guid.NewGuid(), "FIRMA_CLIENTE")], nombre: "Cliente Ficticio",
            orden: o.Ordenes[cual], jornada: o.Jornada, requiereCliente: requiereCliente),
        _ => Sobre(s, Guid.NewGuid(), "RECEPCION_PLANTA", evidencias: [Firma(Guid.NewGuid(), "FIRMA_RECEPCION")], nombre: "Quien recibe",
            planta: new { id = "pta-1", nombre = "PTAR Ficticia" }, orden: o.Ordenes[cual], jornada: o.Jornada, requiereCliente: requiereCliente),
    });

    /// <summary>Simula la llegada de los archivos de una orden: da por recibida toda su evidencia pendiente (la subida HTTP se prueba aparte). El manifiesto solo se arma con todos los archivos recibidos.</summary>
    private async Task RecibirArchivosAsync(Sesion s, Guid orden)
    {
        var ids = (await bd.ConsultarAsync($"SELECT coalesce(string_agg(e.id::text, ','), '') FROM negocio.evidencia_captura e JOIN negocio.captura_trabajo t ON (t.organizacion_id,t.sucursal_id,t.captura_id)=(e.organizacion_id,e.sucursal_id,e.captura_id) WHERE t.orden_derivada_id='{orden}' AND e.archivo_id IS NULL"))
            .Split(',', StringSplitOptions.RemoveEmptyEntries);
        foreach (var id in ids)
            await bd.SqlComoDuenoAsync($"SELECT sync.registrar_archivo('{Org}', (SELECT id FROM seguridad.acceso_autorizado WHERE correo_permitido='{s.Equipo.Correo}'), '{s.Equipo.Instalacion}', '{id}', 'image/png', 'prueba/{id}')");
    }

    private async Task<string> ManifiestosDeAsync(Guid orden) =>
        await bd.ConsultarAsync($"SELECT count(*) FROM negocio.orden_manifiesto WHERE orden_derivada_id='{orden}'");

    [Fact]
    public async Task Cliente_autorizado_arma_un_manifiesto_por_orden_solo_al_llegar_los_tres_pasos_y_sin_exponerlo_al_operador()
    {
        var s = await SesionAsync(); var o = await OrdenesAsync(s, fosas: 2, autorizado: true);
        foreach (var paso in new[] { "servicio", "cliente" })
        {
            var r = await PasoAsync(s, o, 0, paso);
            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
            Assert.DoesNotContain("manifiesto", (await r.Content.ReadAsStringAsync()).ToLowerInvariant());
            await RecibirArchivosAsync(s, o.Ordenes[0]);
            Assert.Equal("0", await ManifiestosDeAsync(o.Ordenes[0]));      // faltan firma y nombre en la planta
        }
        var ultima = await PasoAsync(s, o, 0, "planta");
        Assert.Equal(HttpStatusCode.OK, ultima.StatusCode);
        Assert.DoesNotContain("manifiesto", (await ultima.Content.ReadAsStringAsync()).ToLowerInvariant());
        Assert.Equal("0", await ManifiestosDeAsync(o.Ordenes[0]));          // H03: con el sobre no basta; faltan los archivos de la planta
        await RecibirArchivosAsync(s, o.Ordenes[0]);
        Assert.Equal("1", await ManifiestosDeAsync(o.Ordenes[0]));
        Assert.Equal("0", await ManifiestosDeAsync(o.Ordenes[1]));          // la segunda vuelta es otra orden y otro manifiesto
        // Un envío repetido de la planta (otro UUID) no duplica el manifiesto.
        Assert.Equal(HttpStatusCode.OK, (await PasoAsync(s, o, 0, "planta")).StatusCode);
        Assert.Equal("1", await ManifiestosDeAsync(o.Ordenes[0]));
        // Cliente autorizado = generador; la cantidad se estima por regla y el folio queda sin asignar (pendiente).
        var m = await bd.ConsultarAsync($"SELECT manifiesto_id FROM negocio.orden_manifiesto WHERE orden_derivada_id='{o.Ordenes[0]}'");
        Assert.Equal("2", await bd.ConsultarAsync($"SELECT count(*) FROM negocio.participante_manifiesto p JOIN negocio.version_manifiesto v ON v.id=p.version_id WHERE v.manifiesto_id='{m}'"));
        Assert.Equal("estimada", await bd.ConsultarAsync($"SELECT d.naturaleza_cantidad FROM negocio.detalle_manifiesto d JOIN negocio.version_manifiesto v ON v.id=d.version_id WHERE v.manifiesto_id='{m}'"));
        var folio1 = await bd.ConsultarAsync($"SELECT numero_manifiesto FROM negocio.version_manifiesto WHERE manifiesto_id='{m}'");
        Assert.Matches(@"^\d{3}/" + DateTime.UtcNow.Year + "$", folio1);
        // Segunda orden: completa sus propios pasos y obtiene su propio manifiesto, en otro orden de llegada.
        foreach (var paso in new[] { "planta", "servicio", "cliente" }) { Assert.Equal(HttpStatusCode.OK, (await PasoAsync(s, o, 1, paso)).StatusCode); await RecibirArchivosAsync(s, o.Ordenes[1]); }
        Assert.Equal("1", await ManifiestosDeAsync(o.Ordenes[1]));
        var m2 = await bd.ConsultarAsync($"SELECT manifiesto_id FROM negocio.orden_manifiesto WHERE orden_derivada_id='{o.Ordenes[1]}'");
        Assert.NotEqual(m, m2);
        // Folio consecutivo: el de la segunda orden es el siguiente al de la primera.
        var folio2 = await bd.ConsultarAsync($"SELECT numero_manifiesto FROM negocio.version_manifiesto WHERE manifiesto_id='{m2}'");
        Assert.Equal(int.Parse(folio1[..3]) + 1, int.Parse(folio2[..3]));
    }

    [Fact]
    public async Task Fosa_fija_no_se_arma_sin_la_firma_del_cliente_aunque_todo_lo_demas_este_recibido()
    {
        var s = await SesionAsync(); var o = await OrdenesAsync(s, fosas: 1, autorizado: true);
        foreach (var paso in new[] { "servicio", "planta" }) { Assert.Equal(HttpStatusCode.OK, (await PasoAsync(s, o, 0, paso)).StatusCode); await RecibirArchivosAsync(s, o.Ordenes[0]); }
        Assert.Equal("0", await ManifiestosDeAsync(o.Ordenes[0]));          // el cliente autorizado es el generador: su firma es requisito
        Assert.Equal(HttpStatusCode.OK, (await PasoAsync(s, o, 0, "cliente")).StatusCode);
        Assert.Equal("0", await ManifiestosDeAsync(o.Ordenes[0]));          // la firma llegó en el sobre, pero su archivo todavía no
        await RecibirArchivosAsync(s, o.Ordenes[0]);
        Assert.Equal("1", await ManifiestosDeAsync(o.Ordenes[0]));
    }

    [Theory]
    [InlineData(true)]   // la fosa propia (cliente general) ya no es una orden: viaja como parada de sanitarios (decisión 0033)
    public async Task Ordenes_muestra_por_orden_si_firmaron_el_transportista_el_cliente_y_agua_y_drenaje(bool autorizado)
    {
        var s = await SesionAsync(); var o = await OrdenesAsync(s, fosas: 2, autorizado: autorizado);
        var correo = $"firmas-{Guid.NewGuid():N}@example.invalid";
        await bd.AutorizarAsync(correo);
        var web = _api.NuevoNavegador();
        Assert.Equal(HttpStatusCode.NoContent, (await web.EntrarAsync(ValidadorFicticio.Token($"s-{correo}", correo))).StatusCode);
        var ruta = Guid.Parse(await bd.ConsultarAsync($"SELECT ruta_id FROM negocio.jornada WHERE id='{o.Jornada}'"));
        async Task<(bool Transportista, bool Cliente, bool Agua)[]> FirmasAsync()
        {
            var cliente = JsonDocument.Parse(await web.GetStringAsync($"/api/ordenes?fecha={Hoy}")).RootElement.EnumerateArray()
                .Single(r => r.GetProperty("id").GetGuid() == ruta).GetProperty("paradas")[0].GetProperty("clientes")[0];
            return cliente.GetProperty("ordenes").EnumerateArray()
                .Select(x => (x.GetProperty("transportista").GetBoolean(), x.GetProperty("cliente").GetBoolean(), x.GetProperty("aguaYDrenaje").GetBoolean())).ToArray();
        }
        // Sin capturas: nada firmado; en un cliente general la firma del cliente es la nuestra y ya viene marcada.
        Assert.Equal([(false, !autorizado, false), (false, !autorizado, false)], await FirmasAsync());
        Assert.Equal(HttpStatusCode.OK, (await PasoAsync(s, o, 0, "servicio", requiereCliente: autorizado)).StatusCode);
        Assert.Equal([(true, !autorizado, false), (false, !autorizado, false)], await FirmasAsync());
        if (autorizado)
        {
            Assert.Equal(HttpStatusCode.OK, (await PasoAsync(s, o, 0, "cliente")).StatusCode);
            Assert.Equal([(true, true, false), (false, false, false)], await FirmasAsync());
        }
        Assert.Equal(HttpStatusCode.OK, (await PasoAsync(s, o, 0, "planta", requiereCliente: autorizado)).StatusCode);
        Assert.Equal([(true, true, true), (false, !autorizado, false)], await FirmasAsync());
    }

    [Fact]
    public async Task Carga_por_tirar_de_fosas_cuenta_ordenes_succionadas_sin_recepcion_en_planta_y_baja_al_descargar()
    {
        var s = await SesionAsync(); var o = await OrdenesAsync(s, fosas: 2, autorizado: true);   // fosa fija (convenio): cada orden es un viaje con la unidad llena
        var correo = $"carga-{Guid.NewGuid():N}@example.invalid";
        await bd.AutorizarAsync(correo);
        var web = _api.NuevoNavegador();
        Assert.Equal(HttpStatusCode.NoContent, (await web.EntrarAsync(ValidadorFicticio.Token($"s-{correo}", correo))).StatusCode);
        var ruta = Guid.Parse(await bd.ConsultarAsync($"SELECT ruta_id FROM negocio.jornada WHERE id='{o.Jornada}'"));
        async Task<JsonElement> CargaAsync() =>
            JsonDocument.Parse(await web.GetStringAsync("/api/rutas")).RootElement.EnumerateArray()
                .Single(r => r.GetProperty("id").GetGuid() == ruta).GetProperty("cargaFosas");

        // Sin capturas: la unidad (15 m3) viene vacía.
        var vacia = await CargaAsync();
        Assert.Equal((0, 0m, 15m, 0m), (vacia.GetProperty("ordenes").GetInt32(), vacia.GetProperty("m3").GetDecimal(), vacia.GetProperty("capacidadM3").GetDecimal(), vacia.GetProperty("porcentaje").GetDecimal()));
        Assert.Equal(JsonValueKind.Null, vacia.GetProperty("ultimaDescargaEn").ValueKind);

        // Dos órdenes succionadas (llenan la unidad dos veces): 2 x 15 m3 estimados, 200 %, sin importar la fecha de hoy.
        // Un servicio de una orden inexistente va a revisión y no cuenta; un problema tampoco.
        foreach (var i in new[] { 0, 1 }) Assert.Equal(HttpStatusCode.OK, (await PasoAsync(s, o, i, "servicio")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await EnviarAsync(s.Http, Sobre(s, Guid.NewGuid(), "SERVICIO_OPERADOR", orden: Guid.NewGuid(), jornada: o.Jornada))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await EnviarAsync(s.Http, Sobre(s, Guid.NewGuid(), "SERVICIO_OPERADOR", orden: o.Ordenes[0], jornada: o.Jornada))).StatusCode);   // reenvío con otro UUID: misma orden
        var llena = await CargaAsync();
        Assert.Equal((2, 30m, 200m), (llena.GetProperty("ordenes").GetInt32(), llena.GetProperty("m3").GetDecimal(), llena.GetProperty("porcentaje").GetDecimal()));
        var dia = Assert.Single(llena.GetProperty("desglose").EnumerateArray());
        Assert.Equal((Hoy, 2), (dia.GetProperty("fecha").GetString(), dia.GetProperty("ordenes").GetInt32()));

        // La descarga en planta de la primera orden la saca de la carga y fija la última descarga.
        Assert.Equal(HttpStatusCode.OK, (await PasoAsync(s, o, 0, "planta")).StatusCode);
        var parcial = await CargaAsync();
        Assert.Equal((1, 15m, 100m), (parcial.GetProperty("ordenes").GetInt32(), parcial.GetProperty("m3").GetDecimal(), parcial.GetProperty("porcentaje").GetDecimal()));
        Assert.NotEqual(JsonValueKind.Null, parcial.GetProperty("ultimaDescargaEn").ValueKind);
        Assert.Equal(1, Assert.Single(parcial.GetProperty("desglose").EnumerateArray()).GetProperty("ordenes").GetInt32());
        Assert.Equal(HttpStatusCode.OK, (await PasoAsync(s, o, 1, "planta")).StatusCode);
        Assert.Equal(0, (await CargaAsync()).GetProperty("ordenes").GetInt32());
    }

    [Fact]
    public async Task El_folio_es_consecutivo_por_anio_y_no_desperdicia_numeros_al_revertir()
    {
        // Subtransacción revertida: el número no se consume, el siguiente intento recibe el mismo. Año ficticio propio.
        await bd.SqlComoDuenoAsync($"""
            DO $$ DECLARE a text; b text; c text; BEGIN
                BEGIN a := negocio.siguiente_folio('{Org}', '{Suc}', 2098); RAISE EXCEPTION 'revertir' USING ERRCODE = 'P0001';
                EXCEPTION WHEN SQLSTATE 'P0001' THEN NULL; END;
                b := negocio.siguiente_folio('{Org}', '{Suc}', 2098);
                c := negocio.siguiente_folio('{Org}', '{Suc}', 2098);
                IF b <> '001/2098' OR c <> '002/2098' THEN RAISE EXCEPTION 'Se esperaba 001/2098 y 002/2098, no % y %', b, c; END IF;
                IF negocio.siguiente_folio('{Org}', '{Suc}', 2099) <> '001/2099' THEN RAISE EXCEPTION 'Cada año reinicia en 001'; END IF;
            END $$;
            """);
    }

    [Fact]
    public async Task Orden_inexistente_o_regla_de_firma_distinta_se_guarda_para_revision_sin_armar_manifiesto()
    {
        var s = await SesionAsync(); var o = await OrdenesAsync(s, fosas: 1, autorizado: true);
        var inventada = Guid.NewGuid(); var id1 = Guid.NewGuid();
        // El hecho se recibe (no se pierde el trabajo del teléfono) pero queda marcado.
        var r = await EnviarAsync(s.Http, Sobre(s, id1, orden: inventada, jornada: o.Jornada));
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal("orden_no_resuelta", await bd.ConsultarAsync($"SELECT motivo FROM negocio.captura_por_revisar WHERE captura_id='{id1}'"));
        // La regla de firma del sobre contradice al servidor (cliente autorizado sí firma).
        var id2 = Guid.NewGuid();
        Assert.Equal(HttpStatusCode.OK, (await EnviarAsync(s.Http, Sobre(s, id2, orden: o.Ordenes[0], jornada: o.Jornada, requiereCliente: false))).StatusCode);
        Assert.Equal("regla_firma_distinta", await bd.ConsultarAsync($"SELECT motivo FROM negocio.captura_por_revisar WHERE captura_id='{id2}'"));
        Assert.Equal("0", await ManifiestosDeAsync(o.Ordenes[0]));
        Assert.Equal("0", await ManifiestosDeAsync(inventada));
    }

    // ---- Archivos de evidencia (0026) ----

    private static string ShaDe(byte[] b) => Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(b));
    private static object FotoReal(Guid id, byte[] bytes) => new
    {
        id, rol = "FOTO", sha256 = ShaDe(bytes), tamano = bytes.Length,
        ubicacion = new { latitud = 25.6866, longitud = -100.3161, precisionMetros = 8.5, fechaEpochMs = 1_790_000_000_000L - 2000 },
    };
    private static object FirmaReal(Guid id, string rol, byte[] bytes) => new { id, rol, sha256 = ShaDe(bytes), tamano = bytes.Length, ubicacion = (object?)null };

    private static Task<HttpResponseMessage> SubirAsync(HttpClient http, Guid id, byte[] bytes, string tipo = "image/jpeg")
    {
        var contenido = new ByteArrayContent(bytes); contenido.Headers.ContentType = new MediaTypeHeaderValue(tipo);
        return http.PutAsync($"/api/movil/archivos/{id}", contenido);
    }

    [Fact]
    public async Task Varias_subidas_simultaneas_de_la_misma_evidencia_responden_bien_y_dejan_un_solo_archivo()
    {
        var s = await SesionAsync(); var foto = Guid.NewGuid();
        var bytes = ImagenesDePrueba.Jpeg(7, relleno: 1_900_000);   // grande (bajo el tope): amplía la ventana de la carrera
        Assert.Equal(HttpStatusCode.OK, (await EnviarAsync(s.Http, Sobre(s, Guid.NewGuid(), evidencias: [FotoReal(foto, bytes)]))).StatusCode);
        var respuestas = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => SubirAsync(s.Http, foto, bytes)));
        Assert.All(respuestas, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));   // antes, el perdedor de la carrera recibía 500
        Assert.Equal("1", await bd.ConsultarAsync($"SELECT count(*) FROM negocio.archivo WHERE id='{foto}'"));
        Assert.Equal("1", await bd.ConsultarAsync($"SELECT count(*) FROM sync.tarea WHERE clave='drive:{foto}'"));
        var archivos = Directory.GetFiles(_api.RutaArchivos, "*", SearchOption.AllDirectories).Where(a => a.Contains(foto.ToString("N"))).ToArray();
        Assert.Single(archivos);                       // sin copias parciales sobrantes ni sobrescrituras
        Assert.Equal(ShaDe(bytes), ShaDe(await File.ReadAllBytesAsync(archivos[0])));
    }

    [Fact]
    public async Task Drive_en_segundo_plano_archiva_lo_recibido_reintenta_si_falla_y_no_duplica()
    {
        var s = await SesionAsync(); var foto = Guid.NewGuid(); byte[] bytes = ImagenesDePrueba.Jpeg(4);
        Assert.Equal(HttpStatusCode.OK, (await EnviarAsync(s.Http, Sobre(s, Guid.NewGuid(), evidencias: [FotoReal(foto, bytes)]))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await SubirAsync(s.Http, foto, bytes)).StatusCode);   // el operador ya terminó: no esperó a Drive
        var proceso = _api.Services.GetRequiredService<Manifiestos.Api.Archivos.ProcesoSubidaDrive>();

        // Drive caído: la evidencia sigue recibida en el VPS, la tarea se reprograma y nada se marca como archivado.
        _api.Drive.Fallar = true;
        while (await bd.ConsultarAsync($"SELECT count(*) FROM sync.tarea WHERE clave='drive:{foto}' AND intentos=0") == "1") await proceso.ProcesarUnoAsync(default);
        Assert.Equal("0", await bd.ConsultarAsync($"SELECT count(*) FROM negocio.ubicacion_archivo WHERE archivo_id='{foto}' AND proveedor='drive'"));
        Assert.Equal("1", await bd.ConsultarAsync($"SELECT count(*) FROM sync.tarea WHERE clave='drive:{foto}' AND completada_en IS NULL AND disponible_en>now()"));

        // Drive vuelve: se vence la espera y se archiva con su identificador, una sola vez.
        _api.Drive.Fallar = false;
        await bd.SqlComoDuenoAsync($"UPDATE sync.tarea SET disponible_en=now()-interval '1 second' WHERE clave='drive:{foto}'");
        while (await proceso.ProcesarUnoAsync(default)) { }
        Assert.Equal("drive-" + foto.ToString("N"), await bd.ConsultarAsync($"SELECT clave_externa FROM negocio.ubicacion_archivo WHERE archivo_id='{foto}' AND proveedor='drive'"));
        Assert.Equal("1", await bd.ConsultarAsync($"SELECT count(*) FROM sync.tarea WHERE clave='drive:{foto}' AND completada_en IS NOT NULL"));
        Assert.False(await proceso.ProcesarUnoAsync(default));   // nada pendiente: no se vuelve a subir
        Assert.Single(_api.Drive.Subidos, x => x.Archivo == foto);
        Assert.True(File.Exists(_api.Drive.Subidos.Single(x => x.Archivo == foto).Ruta));   // la copia del VPS se conserva
    }

    [Fact]
    public async Task Rechaza_un_archivo_que_no_es_imagen_aunque_declare_serlo_y_uno_demasiado_grande()
    {
        var s = await SesionAsync(); var id = Guid.NewGuid();
        // El encabezado dice JPEG y el tamaño y la huella coinciden con lo declarado, pero el contenido no es una imagen: se rechaza sin guardar nada.
        var falsa = Guid.NewGuid(); byte[] texto = System.Text.Encoding.UTF8.GetBytes("<script>alert(1)</script>");
        // Un PNG declarado como JPEG (y al revés) tampoco pasa: la firma debe corresponder al tipo.
        var png = Guid.NewGuid(); byte[] bytesPng = ImagenesDePrueba.Png(1);
        // Más grande que el tope de una foto (2 MB), con firma válida.
        var grande = Guid.NewGuid(); var enorme = ImagenesDePrueba.Jpeg(3, relleno: 2_200_000);
        Assert.Equal(HttpStatusCode.OK, (await EnviarAsync(s.Http, Sobre(s, id, evidencias: [FotoReal(falsa, texto), FirmaReal(png, "FIRMA_OPERADOR", bytesPng), FotoReal(grande, enorme)]))).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await SubirAsync(s.Http, falsa, texto)).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await SubirAsync(s.Http, png, bytesPng, "image/jpeg")).StatusCode);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await SubirAsync(s.Http, grande, enorme)).StatusCode);
        Assert.Equal("0", await bd.ConsultarAsync($"SELECT count(*) FROM negocio.archivo WHERE id IN ('{falsa}','{png}','{grande}')"));
        Assert.Empty(Directory.Exists(_api.RutaArchivos) ? Directory.GetFiles(_api.RutaArchivos, "*", SearchOption.AllDirectories) : []);
        // H21: una imagen truncada, un PNG con la suma de verificación mala y un PNG enviado como foto (tipo que no corresponde al rol) también se rechazan.
        var truncada = Guid.NewGuid(); var jpegTruncado = ImagenesDePrueba.Jpeg(11)[..^2];
        var crcMalo = Guid.NewGuid(); var pngMalo = ImagenesDePrueba.Png(12, crcMalo: true);
        var pngComoFoto = Guid.NewGuid(); var pngBueno = ImagenesDePrueba.Png(13);
        Assert.Equal(HttpStatusCode.OK, (await EnviarAsync(s.Http, Sobre(s, Guid.NewGuid(), evidencias: [FotoReal(truncada, jpegTruncado), FirmaReal(crcMalo, "FIRMA_OPERADOR", pngMalo), FotoReal(pngComoFoto, pngBueno)]))).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await SubirAsync(s.Http, truncada, jpegTruncado)).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await SubirAsync(s.Http, crcMalo, pngMalo, "image/png")).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await SubirAsync(s.Http, pngComoFoto, pngBueno, "image/png")).StatusCode);
        // La firma PNG correcta sí se acepta con su tipo.
        Assert.Equal(HttpStatusCode.OK, (await SubirAsync(s.Http, png, bytesPng, "image/png")).StatusCode);
    }

    [Fact]
    public async Task Sube_el_archivo_verificado_lo_guarda_en_el_vps_encola_drive_y_es_idempotente()
    {
        var s = await SesionAsync(); var foto = Guid.NewGuid(); var firma = Guid.NewGuid();
        var bytesFoto = ImagenesDePrueba.Jpeg(5); var bytesFirma = ImagenesDePrueba.Png(9);
        var id = Guid.NewGuid();
        Assert.Equal(HttpStatusCode.OK, (await EnviarAsync(s.Http, Sobre(s, id, evidencias: [FotoReal(foto, bytesFoto), FirmaReal(firma, "FIRMA_OPERADOR", bytesFirma)]))).StatusCode);

        // Contenido distinto con el mismo tamaño, o más grande: se rechaza sin guardar ni registrar nada.
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await SubirAsync(s.Http, foto, [8, 7, 6, 5, 4, 3, 2, 1])).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await SubirAsync(s.Http, foto, [1, 2, 3, 4, 5, 6, 7, 8, 9])).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await SubirAsync(s.Http, foto, [1, 2, 3])).StatusCode);
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, (await SubirAsync(s.Http, foto, bytesFoto, "application/pdf")).StatusCode);
        Assert.Equal("0", await bd.ConsultarAsync($"SELECT count(*) FROM negocio.archivo WHERE id='{foto}'"));
        Assert.Empty(Directory.Exists(_api.RutaArchivos) ? Directory.GetFiles(_api.RutaArchivos, "*", SearchOption.AllDirectories) : []);

        Assert.Equal(HttpStatusCode.OK, (await SubirAsync(s.Http, foto, bytesFoto)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await SubirAsync(s.Http, firma, bytesFirma, "image/png")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await SubirAsync(s.Http, foto, bytesFoto)).StatusCode);   // reintento: sin duplicar
        Assert.Equal(2, Directory.GetFiles(_api.RutaArchivos, "*", SearchOption.AllDirectories).Length);
        var guardado = Directory.GetFiles(_api.RutaArchivos, $"{foto:N}*", SearchOption.AllDirectories).Single();
        Assert.Equal(bytesFoto, await File.ReadAllBytesAsync(guardado));
        Assert.Equal("1", await bd.ConsultarAsync($"SELECT count(*) FROM negocio.archivo WHERE id='{foto}'"));
        Assert.Equal("vps", await bd.ConsultarAsync($"SELECT proveedor FROM negocio.ubicacion_archivo WHERE archivo_id='{foto}'"));
        Assert.Equal("1", await bd.ConsultarAsync($"SELECT count(*) FROM sync.tarea WHERE clave='drive:{foto}' AND tipo='subir_drive' AND completada_en IS NULL"));
        // La consulta de la captura refleja lo recibido.
        var estado = JsonDocument.Parse(await (await s.Http.GetAsync($"/api/movil/capturas/{id}")).Content.ReadAsStringAsync()).RootElement;
        Assert.All(estado.GetProperty("evidencias").EnumerateArray(), e => Assert.Equal("recibida", e.GetProperty("estado").GetString()));
    }

    [Fact]
    public async Task Otro_operador_no_puede_subir_ni_ver_las_evidencias_ajenas()
    {
        var a = await SesionAsync(); var b = await SesionAsync(); var foto = Guid.NewGuid(); byte[] bytes = ImagenesDePrueba.Jpeg(0);
        Assert.Equal(HttpStatusCode.OK, (await EnviarAsync(a.Http, Sobre(a, Guid.NewGuid(), evidencias: [FotoReal(foto, bytes), FirmaReal(Guid.NewGuid(), "FIRMA_OPERADOR", [4])]))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await SubirAsync(b.Http, foto, bytes)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await SubirAsync(a.Http, Guid.NewGuid(), bytes)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await SubirAsync(_api.NuevoNavegador(), foto, bytes)).StatusCode);
        Assert.Equal("0", await bd.ConsultarAsync($"SELECT count(*) FROM negocio.archivo WHERE id='{foto}'"));
    }

    [Fact]
    public async Task La_foto_de_un_problema_crea_un_aviso_de_incidencia_una_sola_vez()
    {
        var s = await SesionAsync(); var o = await OrdenesAsync(s, fosas: 1, autorizado: true);
        var foto = Guid.NewGuid(); byte[] bytes = ImagenesDePrueba.Jpeg(7); var captura = Guid.NewGuid();
        var sobre = Sobre(s, captura, "PROBLEMA", evidencias: [FotoReal(foto, bytes)], problema: new { motivo = "cliente_ausente", comentario = "Nadie abrió" },
            orden: o.Ordenes[0], jornada: o.Jornada);
        Assert.Equal(HttpStatusCode.OK, (await EnviarAsync(s.Http, sobre)).StatusCode);
        Assert.Equal("0", await bd.ConsultarAsync($"SELECT count(*) FROM negocio.incidencia WHERE evidencia_archivo_id='{foto}'"));   // aún sin archivo
        Assert.Equal(HttpStatusCode.OK, (await SubirAsync(s.Http, foto, bytes)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await SubirAsync(s.Http, foto, bytes)).StatusCode);
        Assert.Equal("1", await bd.ConsultarAsync($"SELECT count(*) FROM negocio.incidencia WHERE evidencia_archivo_id='{foto}'"));
        Assert.Equal("cliente_ausente", await bd.ConsultarAsync($"SELECT motivo FROM negocio.incidencia WHERE evidencia_archivo_id='{foto}'"));
        Assert.Equal(s.Equipo.Persona.ToString(), await bd.ConsultarAsync($"SELECT persona_id FROM negocio.incidencia WHERE evidencia_archivo_id='{foto}'"));
        Assert.Equal(o.Cliente.ToString(), await bd.ConsultarAsync($"SELECT cliente_id FROM negocio.incidencia WHERE evidencia_archivo_id='{foto}'"));
        Assert.Equal("0", await ManifiestosDeAsync(o.Ordenes[0]));   // un aviso no completa ni cancela la orden
    }

    [Fact]
    public async Task Las_plantas_son_los_sitios_que_no_son_punto_de_servicio_y_usan_etag()
    {
        var s = await SesionAsync(); var ptar = Guid.NewGuid();
        await bd.SqlComoDuenoAsync($"""
            INSERT INTO negocio.sitio VALUES ('{Org}', '{Suc}', '{ptar}', 'PTAR Ficticia Plantas');
            """);
        var r = await s.Http.GetAsync("/api/movil/plantas");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var lista = JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement.EnumerateArray().ToList();
        Assert.Contains(lista, p => p.GetProperty("id").GetGuid() == ptar && p.GetProperty("nombre").GetString() == "PTAR Ficticia Plantas");
        var req = new HttpRequestMessage(HttpMethod.Get, "/api/movil/plantas"); req.Headers.TryAddWithoutValidation("If-None-Match", r.Headers.ETag!.ToString());
        Assert.Equal(HttpStatusCode.NotModified, (await s.Http.SendAsync(req)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _api.NuevoNavegador().GetAsync("/api/movil/plantas")).StatusCode);
    }

    [Fact]
    public async Task Un_problema_no_cuenta_como_paso_ni_arma_manifiesto()
    {
        var s = await SesionAsync(); var o = await OrdenesAsync(s, fosas: 1, autorizado: true);
        var p = Sobre(s, Guid.NewGuid(), "PROBLEMA", evidencias: [Foto(Guid.NewGuid())], problema: new { motivo = "sin_acceso", comentario = "" },
            orden: o.Ordenes[0], jornada: o.Jornada);
        Assert.Equal(HttpStatusCode.OK, (await EnviarAsync(s.Http, p)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await PasoAsync(s, o, 0, "planta")).StatusCode);
        Assert.Equal("0", await ManifiestosDeAsync(o.Ordenes[0]));        // falta el servicio: el problema no lo reemplaza
    }
}
