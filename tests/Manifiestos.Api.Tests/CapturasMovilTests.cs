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
            Assert.Equal("0", await ManifiestosDeAsync(o.Ordenes[0]));      // faltan firma y nombre en la planta
        }
        var ultima = await PasoAsync(s, o, 0, "planta");
        Assert.Equal(HttpStatusCode.OK, ultima.StatusCode);
        Assert.DoesNotContain("manifiesto", (await ultima.Content.ReadAsStringAsync()).ToLowerInvariant());
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
        foreach (var paso in new[] { "planta", "servicio", "cliente" }) Assert.Equal(HttpStatusCode.OK, (await PasoAsync(s, o, 1, paso)).StatusCode);
        Assert.Equal("1", await ManifiestosDeAsync(o.Ordenes[1]));
        var m2 = await bd.ConsultarAsync($"SELECT manifiesto_id FROM negocio.orden_manifiesto WHERE orden_derivada_id='{o.Ordenes[1]}'");
        Assert.NotEqual(m, m2);
        // Folio consecutivo: el de la segunda orden es el siguiente al de la primera.
        var folio2 = await bd.ConsultarAsync($"SELECT numero_manifiesto FROM negocio.version_manifiesto WHERE manifiesto_id='{m2}'");
        Assert.Equal(int.Parse(folio1[..3]) + 1, int.Parse(folio2[..3]));
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
    public async Task Cliente_general_no_pide_firma_del_cliente_y_arma_con_servicio_y_planta()
    {
        var s = await SesionAsync(); var o = await OrdenesAsync(s, fosas: 1, autorizado: false);
        Assert.Equal(HttpStatusCode.OK, (await PasoAsync(s, o, 0, "servicio", requiereCliente: false)).StatusCode);
        Assert.Equal("0", await ManifiestosDeAsync(o.Ordenes[0]));
        Assert.Equal(HttpStatusCode.OK, (await PasoAsync(s, o, 0, "planta", requiereCliente: false)).StatusCode);
        Assert.Equal("1", await ManifiestosDeAsync(o.Ordenes[0]));
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

    [Fact]
    public async Task Un_problema_no_cuenta_como_paso_ni_arma_manifiesto()
    {
        var s = await SesionAsync(); var o = await OrdenesAsync(s, fosas: 1, autorizado: false);
        var p = Sobre(s, Guid.NewGuid(), "PROBLEMA", evidencias: [Foto(Guid.NewGuid())], problema: new { motivo = "sin_acceso", comentario = "" },
            orden: o.Ordenes[0], jornada: o.Jornada, requiereCliente: false);
        Assert.Equal(HttpStatusCode.OK, (await EnviarAsync(s.Http, p)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await PasoAsync(s, o, 0, "planta", requiereCliente: false)).StatusCode);
        Assert.Equal("0", await ManifiestosDeAsync(o.Ordenes[0]));        // falta el servicio: el problema no lo reemplaza
    }
}
