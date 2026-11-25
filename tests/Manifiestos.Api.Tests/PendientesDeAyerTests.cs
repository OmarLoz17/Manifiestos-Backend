using System.Net.Http.Json;
using System.Text.Json;
using Manifiestos.Api.Acceso;
using Manifiestos.Api.Tests.Infraestructura;

namespace Manifiestos.Api.Tests;

/// <summary>Pendientes de ayer (decisión 0045, migración 0075): solo la lectura de la función de la base. Datos ficticios.</summary>
[Trait("Categoria", "Integracion")]
public sealed class PendientesDeAyerTests(BaseDatosPrueba bd) : IClassFixture<BaseDatosPrueba>, IAsyncLifetime
{
    private static readonly Guid Org = BaseDatosPrueba.Organizacion, Suc = BaseDatosPrueba.Sucursal;
    private ApiPrueba _api = null!;
    public Task InitializeAsync() { _api = new ApiPrueba(bd); return Task.CompletedTask; }
    public async Task DisposeAsync() => await _api.DisposeAsync();

    // El «hoy» del negocio es el de Monterrey (zona provisional, PG-18): por la tarde ya es otro día en UTC.
    private static DateOnly Hoy => DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeBySystemTimeZoneId(DateTime.UtcNow, "America/Monterrey"));
    private static string F(DateOnly d) => d.ToString("yyyy-MM-dd");

    private sealed record Operador(string Correo, Guid Persona, Guid Instalacion, Guid Acceso, HttpClient Http, UsuarioMovil Yo);
    private sealed record Ruta(Guid Id, Guid Unidad, Guid Jornada, Guid Parada, Guid Cliente);

    private async Task<Operador> OperadorAsync()
    {
        var (correo, sujeto, persona, instalacion, dispositivo) = ($"ayer-{Guid.NewGuid():N}@example.invalid", $"s-{Guid.NewGuid():N}", Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        await bd.AutorizarAsync(correo, "telefono_operador", conSucursal: false);
        await bd.SqlComoDuenoAsync($"""
            INSERT INTO seguridad.persona(organizacion_id,id,nombre_completo) VALUES('{Org}','{persona}','Operador sintético');
            INSERT INTO negocio.operador_ficha(organizacion_id,sucursal_id,persona_id,revision,nombre_completo,correo)
            VALUES('{Org}','{Suc}','{persona}',1,'Operador sintético','{correo}');
            INSERT INTO seguridad.dispositivo(organizacion_id,id) VALUES('{Org}','{dispositivo}');
            INSERT INTO seguridad.instalacion_app(organizacion_id,id,dispositivo_id,habilitada) VALUES('{Org}','{instalacion}','{dispositivo}',true);
            INSERT INTO seguridad.asignacion_equipo(organizacion_id,sucursal_id,dispositivo_id,acceso_id,persona_id,revision,desde)
            SELECT organizacion_id,'{Suc}','{dispositivo}',id,'{persona}',1,now()-interval '1 minute'
            FROM seguridad.acceso_autorizado WHERE correo_permitido='{correo}';
            """);
        // El inicio de sesión vincula la identidad de Google a la cuenta (la usa el contexto del operador).
        var cliente = _api.NuevoNavegador();
        var d = (await (await cliente.PostAsJsonAsync("/api/movil/auth/desafio", new { instalacionId = instalacion })).Content.ReadFromJsonAsync<DesafioMovil>())!;
        var r = await cliente.PostAsJsonAsync("/api/movil/auth/google", new SolicitudGoogleMovil(ValidadorFicticio.Token(sujeto, correo, true, nonce: d.Nonce), instalacion, d.Id));
        r.EnsureSuccessStatusCode();
        var token = JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement.GetProperty("accessToken").GetString();
        cliente.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        var datos = (await cliente.GetFromJsonAsync<UsuarioMovil>("/api/movil/auth/me"))!;
        var acceso = Guid.Parse(await bd.ConsultarAsync($"SELECT id FROM seguridad.acceso_autorizado WHERE correo_permitido='{correo}'"));
        return new Operador(correo, persona, instalacion, acceso, cliente, datos);
    }

    /// <summary>Ruta de todos los días con una parada; la jornada de la fecha dada solo se crea si <paramref name="estado"/> no es null.</summary>
    private async Task<Ruta> RutaAsync(Guid persona, int fosas, string? estado, DateOnly fecha, string servicio = "fosas", int banos = 0)
    {
        var r = new Ruta(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var (sitio, version, empresa) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        await bd.SqlComoDuenoAsync($"""
            BEGIN;
            INSERT INTO negocio.vehiculo VALUES ('{Org}', '{Suc}', '{r.Unidad}', 'AYR-{r.Unidad.ToString("N")[..5]}');
            INSERT INTO negocio.ficha_vehiculo(organizacion_id, sucursal_id, vehiculo_id, revision, capacidad_id, clasificacion) VALUES ('{Org}', '{Suc}', '{r.Unidad}', 1, {(servicio == "sanitarios" ? 4 : 2)}, '{servicio}');
            INSERT INTO negocio.ruta(organizacion_id, sucursal_id, id) VALUES ('{Org}', '{Suc}', '{r.Id}');
            INSERT INTO negocio.ruta_version(organizacion_id, sucursal_id, ruta_id, revision, nombre, dias, vehiculo_id, registrada_en) VALUES ('{Org}', '{Suc}', '{r.Id}', 1, 'Ruta Ayer Ficticia', 127, '{r.Unidad}', now() - interval '3 days');
            INSERT INTO negocio.asignacion_unidad(organizacion_id, sucursal_id, vehiculo_id, revision, persona_id, registrada_en) VALUES ('{Org}', '{Suc}', '{r.Unidad}', 1, '{persona}', now() - interval '3 days');
            INSERT INTO negocio.empresa VALUES ('{Org}', '{empresa}', 'CLI-{empresa:N}');
            INSERT INTO negocio.ficha_empresa(organizacion_id, empresa_id, revision, nombre_comercial) VALUES ('{Org}', '{empresa}', 1, 'Cliente Ayer Ficticio');
            INSERT INTO negocio.empresa_sucursal VALUES ('{Org}', '{Suc}', '{empresa}');
            INSERT INTO negocio.cliente VALUES ('{Org}', '{Suc}', '{r.Cliente}', '{empresa}', 'A-{r.Cliente:N}');
            INSERT INTO negocio.sitio VALUES ('{Org}', '{Suc}', '{sitio}', 'Sitio ayer');
            INSERT INTO negocio.punto_servicio VALUES ('{Org}', '{Suc}', '{sitio}');
            INSERT INTO negocio.parada(organizacion_id, sucursal_id, id, ruta_id, posicion, creada_en) VALUES ('{Org}', '{Suc}', '{r.Parada}', '{r.Id}', 1, now() - interval '3 days');
            INSERT INTO negocio.parada_version(organizacion_id, sucursal_id, id, parada_id, revision, sitio_id, registrada_en) VALUES ('{Org}', '{Suc}', '{version}', '{r.Parada}', 1, '{sitio}', now() - interval '3 days');
            INSERT INTO negocio.parada_cliente(organizacion_id, sucursal_id, version_id, cliente_id, banos_planeados, fosas_planeadas) VALUES ('{Org}', '{Suc}', '{version}', '{r.Cliente}', {(banos > 0 ? banos.ToString() : "NULL")}, {fosas});
            {(servicio == "fosas" ? $"INSERT INTO negocio.autorizacion_empresa(organizacion_id, empresa_id, autoridad, finalidad, numero) VALUES ('{Org}', '{empresa}', 'Medio ambiente', 'Generador', 'AUT-FICTICIA');" : "")}
            {(estado is null ? "" : $"""
            INSERT INTO negocio.jornada(organizacion_id, sucursal_id, id, ruta_id, fecha) VALUES ('{Org}', '{Suc}', '{r.Jornada}', '{r.Id}', '{F(fecha)}');
            INSERT INTO negocio.estado_jornada(organizacion_id, sucursal_id, jornada_id, revision, estado) VALUES ('{Org}', '{Suc}', '{r.Jornada}', 1, '{estado}');
            """)}
            COMMIT;
            """);
        return r;
    }

    private async Task<List<JsonElement>> PendientesAsync(Operador o)
    {
        var json = await bd.ConsultarAsync($"SELECT seguridad.pendientes_de_ayer('{Org}','{o.Acceso}','{o.Instalacion}','{F(Hoy)}')::text");
        var raiz = JsonDocument.Parse(json).RootElement.Clone();
        Assert.Equal(F(Hoy.AddDays(-1)), raiz.GetProperty("fecha").GetString());
        return raiz.GetProperty("jornadas").EnumerateArray().ToList();
    }

    private static List<JsonElement> Ordenes(JsonElement jornada) =>
        jornada.GetProperty("paradas")[0].GetProperty("clientes")[0].GetProperty("ordenes").EnumerateArray().ToList();

    /// <summary>Captura de servicio ya recibida de una orden o parada (solo lo que la lectura de pendientes mira).</summary>
    private Task CapturaAsync(Operador o, Guid jornada, Guid ordenOParada, string tipo) => bd.SqlComoDuenoAsync($"""
        WITH eq AS (SELECT id, dispositivo_id, acceso_id FROM seguridad.asignacion_equipo WHERE persona_id = '{o.Persona}' LIMIT 1),
             ins AS (INSERT INTO negocio.captura(organizacion_id,sucursal_id,id,asignacion_equipo_id,dispositivo_id,instalacion_app_id,ocurrida_en,resultado_declarado)
                     SELECT '{Org}','{Suc}',gen_random_uuid(), eq.id, eq.dispositivo_id, '{o.Instalacion}', now() - interval '1 day', '{tipo}' FROM eq RETURNING id)
        INSERT INTO negocio.captura_trabajo(organizacion_id,sucursal_id,captura_id,tipo,persona_id,identidad_id,orden_derivada_id,jornada_id,
            asignacion_trabajo,revision_trabajo,version_datos,requiere_firma_cliente,datos_orden,banos,banos_asignados)
        SELECT '{Org}','{Suc}',ins.id,'{tipo}','{o.Persona}', a.identidad_id, '{ordenOParada}','{jornada}','a','1','1',false,convert_to('d','UTF8'),
               CASE WHEN '{tipo}' = 'SERVICIO_SANITARIOS' THEN 1 END, CASE WHEN '{tipo}' = 'SERVICIO_SANITARIOS' THEN 1 END
        FROM ins, seguridad.acceso_autorizado a WHERE a.id = '{o.Acceso}';
        """);

    [Fact]
    public async Task Quedan_las_ordenes_sin_hacer_de_ayer_y_nada_de_anteayer()
    {
        var yo = await OperadorAsync();
        var ruta = await RutaAsync(yo.Persona, fosas: 3, "programada", Hoy.AddDays(-1));
        await RutaAsync(yo.Persona, fosas: 1, "programada", Hoy.AddDays(-2));   // anteayer no cuenta (su jornada de ayer sí se genera, ver abajo)
        var antes = (await PendientesAsync(yo)).Single(j => j.GetProperty("jornadaId").GetGuid() == ruta.Jornada);
        Assert.Equal(3, Ordenes(antes).Count);

        // Hechas 2 de 3: queda 1.
        foreach (var orden in Ordenes(antes).Take(2))
            await CapturaAsync(yo, ruta.Jornada, orden.GetProperty("ordenId").GetGuid(), "SERVICIO_OPERADOR");
        var despues = (await PendientesAsync(yo)).Single(j => j.GetProperty("jornadaId").GetGuid() == ruta.Jornada);
        var queda = Assert.Single(Ordenes(despues));
        Assert.Equal(3, queda.GetProperty("vuelta").GetInt32());
        Assert.Equal(1, despues.GetProperty("paradas")[0].GetProperty("clientes")[0].GetProperty("fosas").GetInt32());

        // Hecha la última, la ruta desaparece de los pendientes.
        await CapturaAsync(yo, ruta.Jornada, queda.GetProperty("ordenId").GetGuid(), "SERVICIO_OPERADOR");
        Assert.DoesNotContain(await PendientesAsync(yo), j => j.GetProperty("jornadaId").GetGuid() == ruta.Jornada);
        Assert.DoesNotContain(await PendientesAsync(yo), j => j.GetProperty("jornadaId").GetGuid() != ruta.Jornada && j.GetProperty("paradas").GetArrayLength() == 0);
    }

    [Fact]
    public async Task Cuenta_lo_cancelado_y_lo_que_nadie_abrio_pero_no_las_extraordinarias_ni_lo_de_otro_operador()
    {
        var yo = await OperadorAsync(); var otro = await OperadorAsync();
        var cancelada = await RutaAsync(yo.Persona, 1, "cancelada", Hoy.AddDays(-1));
        var sinJornada = await RutaAsync(yo.Persona, 1, null, Hoy.AddDays(-1));
        var ajena = await RutaAsync(otro.Persona, 1, "programada", Hoy.AddDays(-1));
        var extra = Guid.NewGuid();
        await bd.SqlComoDuenoAsync($"""
            BEGIN;
            INSERT INTO negocio.parada_extraordinaria(organizacion_id,sucursal_id,id,ruta_id,fecha,sitio_id)
            SELECT '{Org}','{Suc}','{extra}','{cancelada.Id}','{F(Hoy.AddDays(-1))}', sitio_id FROM negocio.parada_version WHERE parada_id = '{cancelada.Parada}';
            INSERT INTO negocio.parada_extraordinaria_cliente(organizacion_id,sucursal_id,parada_extraordinaria_id,cliente_id,fosas_planeadas)
            VALUES('{Org}','{Suc}','{extra}','{cancelada.Cliente}',2);
            COMMIT;
            """);

        var pendientes = await PendientesAsync(yo);
        var j = pendientes.Single(x => x.GetProperty("rutaId").GetGuid() == cancelada.Id);
        Assert.Equal(cancelada.Parada, Assert.Single(j.GetProperty("paradas").EnumerateArray()).GetProperty("paradaId").GetGuid());
        Assert.Contains(pendientes, x => x.GetProperty("rutaId").GetGuid() == sinJornada.Id);
        Assert.DoesNotContain(pendientes, x => x.GetProperty("rutaId").GetGuid() == ajena.Id);
    }

    [Fact]
    public async Task Cambiar_hoy_el_operador_de_la_unidad_no_traspasa_lo_de_ayer()
    {
        var yo = await OperadorAsync(); var nuevo = await OperadorAsync();
        var ruta = await RutaAsync(yo.Persona, 1, "programada", Hoy.AddDays(-1));
        await bd.SqlComoDuenoAsync($"INSERT INTO negocio.asignacion_unidad(organizacion_id,sucursal_id,vehiculo_id,revision,persona_id) VALUES('{Org}','{Suc}','{ruta.Unidad}',2,'{nuevo.Persona}');");
        Assert.Empty(await PendientesAsync(yo));   // sin ninguna unidad hoy no hay dónde contar la carga
        Assert.DoesNotContain(await PendientesAsync(nuevo), j => j.GetProperty("rutaId").GetGuid() == ruta.Id);
        var hoy = Guid.NewGuid();
        await bd.SqlComoDuenoAsync($"""
            INSERT INTO negocio.vehiculo VALUES ('{Org}', '{Suc}', '{hoy}', 'NVA-{hoy.ToString("N")[..5]}');
            INSERT INTO negocio.ficha_vehiculo(organizacion_id, sucursal_id, vehiculo_id, revision, capacidad_id, clasificacion) VALUES ('{Org}', '{Suc}', '{hoy}', 1, 3, 'fosas');
            INSERT INTO negocio.asignacion_unidad(organizacion_id, sucursal_id, vehiculo_id, revision, persona_id) VALUES ('{Org}', '{Suc}', '{hoy}', 1, '{yo.Persona}');
            """);
        var j = (await PendientesAsync(yo)).Single(x => x.GetProperty("rutaId").GetGuid() == ruta.Id);
        Assert.Equal(hoy, j.GetProperty("unidad").GetProperty("id").GetGuid());           // la unidad de HOY, no la de ayer
        Assert.Equal(18m, j.GetProperty("unidad").GetProperty("capacidadM3").GetDecimal());
    }

    [Fact]
    public async Task Lo_que_hoy_se_cambia_en_rutas_no_aparece_como_pendiente_de_ayer_ni_se_borra()
    {
        var yo = await OperadorAsync();
        var ruta = await RutaAsync(yo.Persona, 2, "programada", Hoy.AddDays(-1));
        // Hoy: se sube de 2 a 5 órdenes y se agrega otra parada en la misma ruta.
        var (nuevaVersion, nuevaParada, versionNueva, otroSitio) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        await bd.SqlComoDuenoAsync($"""
            BEGIN;
            INSERT INTO negocio.parada_version(organizacion_id,sucursal_id,id,parada_id,revision,sitio_id)
            SELECT '{Org}','{Suc}','{nuevaVersion}','{ruta.Parada}',2,sitio_id FROM negocio.parada_version WHERE parada_id='{ruta.Parada}' AND revision=1;
            INSERT INTO negocio.parada_cliente(organizacion_id,sucursal_id,version_id,cliente_id,fosas_planeadas) VALUES('{Org}','{Suc}','{nuevaVersion}','{ruta.Cliente}',5);
            INSERT INTO negocio.sitio VALUES('{Org}','{Suc}','{otroSitio}','Sitio de hoy');
            INSERT INTO negocio.punto_servicio VALUES('{Org}','{Suc}','{otroSitio}');
            INSERT INTO negocio.parada(organizacion_id,sucursal_id,id,ruta_id,posicion) VALUES('{Org}','{Suc}','{nuevaParada}','{ruta.Id}',2);
            INSERT INTO negocio.parada_version(organizacion_id,sucursal_id,id,parada_id,revision,sitio_id) VALUES('{Org}','{Suc}','{versionNueva}','{nuevaParada}',1,'{otroSitio}');
            INSERT INTO negocio.parada_cliente(organizacion_id,sucursal_id,version_id,cliente_id,fosas_planeadas) VALUES('{Org}','{Suc}','{versionNueva}','{ruta.Cliente}',1);
            COMMIT;
            """);
        var j = (await PendientesAsync(yo)).Single(x => x.GetProperty("jornadaId").GetGuid() == ruta.Jornada);
        Assert.Equal(ruta.Parada, Assert.Single(j.GetProperty("paradas").EnumerateArray()).GetProperty("paradaId").GetGuid());
        Assert.Equal(2, Ordenes(j).Count);

        // Dar de baja hoy la parada no borra lo que ayer quedó sin hacer.
        await bd.SqlComoDuenoAsync($"INSERT INTO negocio.parada_baja(organizacion_id,sucursal_id,parada_id) VALUES('{Org}','{Suc}','{ruta.Parada}');");
        Assert.Contains(await PendientesAsync(yo), x => x.GetProperty("jornadaId").GetGuid() == ruta.Jornada);
    }

    [Fact]
    public async Task Una_parada_de_sanitarios_con_captura_ya_no_esta_pendiente()
    {
        var yo = await OperadorAsync();
        var ruta = await RutaAsync(yo.Persona, 0, "programada", Hoy.AddDays(-1), "sanitarios", banos: 4);
        var j = (await PendientesAsync(yo)).Single(x => x.GetProperty("rutaId").GetGuid() == ruta.Id);
        Assert.Equal("sanitarios", j.GetProperty("servicio").GetString());
        await CapturaAsync(yo, ruta.Jornada, ruta.Parada, "SERVICIO_SANITARIOS");
        Assert.DoesNotContain(await PendientesAsync(yo), x => x.GetProperty("rutaId").GetGuid() == ruta.Id);
    }

    [Fact]
    public async Task Encendido_por_omision_se_apaga_en_general_y_el_ajuste_de_la_cuenta_manda()
    {
        var yo = await OperadorAsync();
        await RutaAsync(yo.Persona, 1, "programada", Hoy.AddDays(-1));
        Assert.NotEmpty(await PendientesAsync(yo));
        Task Ajuste(string acceso, bool activo, int revision) => bd.SqlComoDuenoAsync(
            $"INSERT INTO seguridad.pendientes_ayer_ajuste(organizacion_id,acceso_id,revision,activo) VALUES('{Org}',{acceso},{revision},{activo});");
        try
        {
            await Ajuste("NULL", false, 1);
            Assert.Empty(await PendientesAsync(yo));
            await Ajuste($"'{yo.Acceso}'", true, 1);
            Assert.NotEmpty(await PendientesAsync(yo));
            await Ajuste($"'{yo.Acceso}'", false, 2);
            Assert.Empty(await PendientesAsync(yo));
        }
        finally { await Ajuste("NULL", true, 2); }
        // El historial no se edita ni se borra.
        await Assert.ThrowsAsync<InvalidOperationException>(() => bd.SqlComoDuenoAsync("DELETE FROM seguridad.pendientes_ayer_ajuste;"));
    }

    private static string Sha(char c) => new(c, 64);

    /// <summary>Captura hecha AHORA y enviada por la API, como el teléfono.</summary>
    private static async Task<Guid> EnviarAsync(Operador o, string tipo, Guid jornada, Guid objetivo, object? sanitarios = null)
    {
        var id = Guid.NewGuid(); var ahora = DateTimeOffset.UtcNow.AddMinutes(-1).ToUnixTimeMilliseconds(); var yo = o.Yo.Operador;
        var foto = new { id = Guid.NewGuid(), rol = "FOTO", sha256 = Sha('a'), tamano = 1234, ubicacion = new { latitud = 25.6866, longitud = -100.3161, precisionMetros = 8.5, fechaEpochMs = ahora - 2000 } };
        object[] evidencias = tipo == "SERVICIO_OPERADOR" ? [foto, new { id = Guid.NewGuid(), rol = "FIRMA_OPERADOR", sha256 = Sha('b'), tamano = 900, ubicacion = (object?)null }] : [foto];
        var sobre = JsonSerializer.Serialize(new
        {
            version = 1, id,
            contexto = new { ambitoId = "ambito", personaId = yo.PersonaId, identidadId = yo.IdentidadId, instalacionId = yo.InstalacionId,
                asignacionEquipoId = yo.AsignacionEquipoId, revisionEquipo = yo.RevisionEquipo.ToString() },
            trabajo = new { ordenId = objetivo, jornadaId = jornada, asignacionTrabajoId = "a-1", revisionTrabajo = "1", requiereFirmaCliente = true, versionDatos = "1", datosOrdenJson = "{}" },
            tipo, fechaEpochMs = ahora, evidencias, sanitarios,
            aceptado = tipo == "SERVICIO_OPERADOR" ? new { version = "fixture-1", textoExacto = "Texto ficticio" } : null,
        });
        var r = await o.Http.PostAsync("/api/movil/capturas", new StringContent(sobre, System.Text.Encoding.UTF8, "application/json"));
        Assert.True(r.IsSuccessStatusCode, await r.Content.ReadAsStringAsync());
        return id;
    }

    /// <summary>Hoy la unidad de ayer pasa a otra persona y el operador recibe una unidad nueva del mismo servicio.</summary>
    private async Task<Guid> CambiarDeUnidadHoyAsync(Operador yo, Operador otro, Ruta ayer, string servicio)
    {
        var nueva = Guid.NewGuid();
        await bd.SqlComoDuenoAsync($"""
            INSERT INTO negocio.vehiculo VALUES ('{Org}', '{Suc}', '{nueva}', 'HOY-{nueva.ToString("N")[..5]}');
            INSERT INTO negocio.ficha_vehiculo(organizacion_id, sucursal_id, vehiculo_id, revision, capacidad_id, clasificacion) VALUES ('{Org}', '{Suc}', '{nueva}', 1, {(servicio == "sanitarios" ? 4 : 3)}, '{servicio}');
            INSERT INTO negocio.asignacion_unidad(organizacion_id, sucursal_id, vehiculo_id, revision, persona_id) VALUES ('{Org}', '{Suc}', '{ayer.Unidad}', 2, '{otro.Persona}');
            INSERT INTO negocio.asignacion_unidad(organizacion_id, sucursal_id, vehiculo_id, revision, persona_id) VALUES ('{Org}', '{Suc}', '{nueva}', 1, '{yo.Persona}');
            """);
        return nueva;
    }

    private Task<string> PorRevisarAsync(Guid captura) => bd.ConsultarAsync($"SELECT coalesce(string_agg(motivo, ','), '') FROM negocio.captura_por_revisar WHERE captura_id='{captura}'");

    [Fact]
    public async Task La_succion_de_sanitarios_de_ayer_hecha_hoy_se_carga_en_la_unidad_de_hoy()
    {
        var yo = await OperadorAsync(); var otro = await OperadorAsync();
        var ruta = await RutaAsync(yo.Persona, 0, "programada", Hoy.AddDays(-1), "sanitarios", banos: 4);
        var nueva = await CambiarDeUnidadHoyAsync(yo, otro, ruta, "sanitarios");

        var captura = await EnviarAsync(yo, "SERVICIO_SANITARIOS", ruta.Jornada, ruta.Parada, new { banos = 4, banosAsignados = 4 });
        Assert.Equal("", await PorRevisarAsync(captura));
        Assert.Equal(nueva.ToString(), await bd.ConsultarAsync($"SELECT vehiculo_id FROM negocio.succion_parada WHERE captura_id='{captura}'"));
        Assert.DoesNotContain(await PendientesAsync(yo), j => j.GetProperty("jornadaId").GetGuid() == ruta.Jornada);
    }

    [Fact]
    public async Task La_orden_de_fosa_de_ayer_hecha_hoy_se_resuelve_en_la_unidad_de_hoy_tambien_al_descargar_despues()
    {
        var yo = await OperadorAsync(); var otro = await OperadorAsync();
        var ruta = await RutaAsync(yo.Persona, 2, "programada", Hoy.AddDays(-1));
        var orden = Ordenes((await PendientesAsync(yo)).Single(j => j.GetProperty("jornadaId").GetGuid() == ruta.Jornada))[0].GetProperty("ordenId").GetGuid();
        var nueva = await CambiarDeUnidadHoyAsync(yo, otro, ruta, "fosas");

        var captura = await EnviarAsync(yo, "SERVICIO_OPERADOR", ruta.Jornada, orden);
        Assert.Equal("", await PorRevisarAsync(captura));
        string Resuelve(string instante) => $"SELECT vehiculo_id FROM negocio.resolver_orden_derivada('{Org}','{Suc}','{yo.Persona}','{ruta.Jornada}','{orden}',{instante})";
        Assert.Equal(nueva.ToString(), await bd.ConsultarAsync(Resuelve("now()")));
        // La descarga en planta puede ser otro día: manda el momento de la succión.
        Assert.Equal(nueva.ToString(), await bd.ConsultarAsync(Resuelve("now() + interval '1 day'")));
        Assert.Single(Ordenes((await PendientesAsync(yo)).Single(j => j.GetProperty("jornadaId").GetGuid() == ruta.Jornada)));
    }

    [Fact]
    public async Task Lo_de_anteayer_no_se_puede_hacer_como_pendiente()
    {
        var yo = await OperadorAsync(); var otro = await OperadorAsync();
        var ruta = await RutaAsync(yo.Persona, 0, "programada", Hoy.AddDays(-2), "sanitarios", banos: 4);
        await CambiarDeUnidadHoyAsync(yo, otro, ruta, "sanitarios");
        var captura = await EnviarAsync(yo, "SERVICIO_SANITARIOS", ruta.Jornada, ruta.Parada, new { banos = 4, banosAsignados = 4 });
        Assert.Equal("parada_no_resuelta", await PorRevisarAsync(captura));
    }

    [Fact]
    public async Task Operaciones_quita_hoy_un_pendiente_y_lo_puede_devolver_y_la_app_lo_lee_por_su_endpoint()
    {
        var yo = await OperadorAsync();
        var ruta = await RutaAsync(yo.Persona, 1, "programada", Hoy.AddDays(-1));
        async Task<List<JsonElement>> PorApiAsync()
        {
            var r = await yo.Http.GetAsync($"/api/movil/trabajo/pendientes?fecha={F(Hoy)}");
            Assert.True(r.IsSuccessStatusCode, await r.Content.ReadAsStringAsync());
            return JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement.GetProperty("jornadas").EnumerateArray().Select(x => x.Clone()).ToList();
        }
        Assert.Contains(await PorApiAsync(), j => j.GetProperty("jornadaId").GetGuid() == ruta.Jornada);
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, (await yo.Http.GetAsync($"/api/movil/trabajo/pendientes?fecha={F(Hoy.AddDays(-5))}")).StatusCode);

        var correo = $"ops-{Guid.NewGuid():N}@example.invalid";
        await bd.AutorizarAsync(correo, "operaciones", escribir: true);
        var panel = _api.NuevoNavegador();
        Assert.Equal(System.Net.HttpStatusCode.NoContent, (await panel.EntrarAsync(ValidadorFicticio.Token($"s-{correo}", correo))).StatusCode);
        async Task<System.Net.HttpStatusCode> MarcarAsync(Guid parada, bool noSeHara)
        {
            var req = new HttpRequestMessage(HttpMethod.Put, "/api/ordenes/pendientes-ayer/no-se-hara") { Content = JsonContent.Create(new { rutaId = ruta.Id, paradaId = parada, noSeHara }) };
            req.Headers.Add(ServiciosAcceso.CabeceraCsrf, await panel.CsrfAsync());
            return (await panel.SendAsync(req)).StatusCode;
        }
        Assert.Equal(System.Net.HttpStatusCode.NotFound, await MarcarAsync(Guid.NewGuid(), true));
        Assert.Equal(System.Net.HttpStatusCode.NoContent, await MarcarAsync(ruta.Parada, true));
        Assert.Equal(System.Net.HttpStatusCode.NoContent, await MarcarAsync(ruta.Parada, true));   // repetir no agrega revisiones
        Assert.DoesNotContain(await PorApiAsync(), j => j.GetProperty("jornadaId").GetGuid() == ruta.Jornada);
        Assert.Equal(System.Net.HttpStatusCode.NoContent, await MarcarAsync(ruta.Parada, false));
        Assert.Contains(await PorApiAsync(), j => j.GetProperty("jornadaId").GetGuid() == ruta.Jornada);
        Assert.Equal("2", await bd.ConsultarAsync($"SELECT count(*) FROM negocio.pendiente_descartado WHERE jornada_id='{ruta.Jornada}'"));
    }

    [Fact]
    public async Task Administracion_apaga_y_enciende_para_todos_o_para_uno_con_confirmacion_y_solo_el_administrador()
    {
        var yo = await OperadorAsync(); var otro = await OperadorAsync();
        await RutaAsync(yo.Persona, 1, "programada", Hoy.AddDays(-1));
        async Task<(HttpClient Nav, string Correo)> EntrarAsync(string rol)
        {
            var correo = $"adm-{Guid.NewGuid():N}@example.invalid";
            await bd.AutorizarAsync(correo, rol, escribir: true);
            var nav = _api.NuevoNavegador();
            Assert.Equal(System.Net.HttpStatusCode.NoContent, (await nav.EntrarAsync(ValidadorFicticio.Token($"s-{correo}", correo))).StatusCode);
            return (nav, correo);
        }
        async Task<System.Net.HttpStatusCode> EnviarAdminAsync(HttpClient nav, HttpMethod metodo, string url, object cuerpo)
        {
            var req = new HttpRequestMessage(metodo, url) { Content = JsonContent.Create(cuerpo) };
            req.Headers.Add(ServiciosAcceso.CabeceraCsrf, await nav.CsrfAsync());
            return (await nav.SendAsync(req)).StatusCode;
        }
        var (ops, _) = await EntrarAsync("operaciones");
        Assert.Equal(System.Net.HttpStatusCode.Forbidden, (await ops.GetAsync("/api/admin/pendientes-ayer")).StatusCode);
        var (admin, correoAdmin) = await EntrarAsync("administrador");
        // Sin confirmar con Google no se escribe.
        Assert.Equal(System.Net.HttpStatusCode.Forbidden, await EnviarAdminAsync(admin, HttpMethod.Put, "/api/admin/pendientes-ayer", new { activo = false }));
        Assert.Equal(System.Net.HttpStatusCode.NoContent, await EnviarAdminAsync(admin, HttpMethod.Post, "/api/admin/confirmar", new { credential = ValidadorFicticio.Token($"s-{correoAdmin}", correoAdmin) }));
        async Task<(bool General, bool Yo, bool Otro)> LeerAsync()
        {
            var j = JsonDocument.Parse(await admin.GetStringAsync("/api/admin/pendientes-ayer")).RootElement;
            bool De(Operador o) => j.GetProperty("cuentas").EnumerateArray().Single(c => c.GetProperty("cuentaId").GetGuid() == o.Acceso).GetProperty("activo").GetBoolean();
            return (j.GetProperty("general").GetBoolean(), De(yo), De(otro));
        }
        Task<System.Net.HttpStatusCode> GuardarAsync(Guid? cuenta, bool activo) => EnviarAdminAsync(admin, HttpMethod.Put, "/api/admin/pendientes-ayer", new { cuentaId = cuenta, activo });
        try
        {
            Assert.Equal((true, true, true), await LeerAsync());
            Assert.Equal(System.Net.HttpStatusCode.NoContent, await GuardarAsync(null, false));
            Assert.Equal((false, false, false), await LeerAsync());
            Assert.Empty(await PendientesAsync(yo));
            Assert.Equal(System.Net.HttpStatusCode.NoContent, await GuardarAsync(yo.Acceso, true));       // solo para uno
            Assert.Equal((false, true, false), await LeerAsync());
            Assert.NotEmpty(await PendientesAsync(yo));
            Assert.Equal(System.Net.HttpStatusCode.NoContent, await GuardarAsync(null, false));           // «todos» vuelve a igualar
            Assert.Equal((false, false, false), await LeerAsync());
            Assert.Equal(System.Net.HttpStatusCode.NotFound, await GuardarAsync(Guid.NewGuid(), true));
        }
        finally { await GuardarAsync(null, true); }
        Assert.Equal((true, true, true), await LeerAsync());
    }
}
