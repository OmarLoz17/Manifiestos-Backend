using System.Net;
using System.Net.Http.Json;
using Manifiestos.Api.Acceso;
using Manifiestos.Api.Rutas;
using Manifiestos.Api.Tests.Infraestructura;

namespace Manifiestos.Api.Tests;

/// <summary>
/// Rutas recurrentes y paradas (0008) contra PostgreSQL real: permisos por sucursal, revisiones,
/// puntos de servicio (las PTAR no son paradas), orden visual y conflictos. Datos ficticios.
/// </summary>
[Trait("Categoria", "Integracion")]
public sealed class RutasTests : IClassFixture<BaseDatosPrueba>, IAsyncLifetime
{
    private static readonly Guid Org = BaseDatosPrueba.Organizacion;
    private static readonly Guid Suc = BaseDatosPrueba.Sucursal;
    private const string Empresa = "0f000000-0000-4000-8000-000000000002";
    private const string OtraOrg = "0f000000-0000-4000-8000-0000000000a9";
    private static readonly Guid Suc2 = Guid.Parse("0f000000-0000-4000-8000-0000000000a2");
    private static readonly Guid EmpresaAlfa = Guid.Parse("0f000000-0000-4000-8000-0000000000e1");
    private static readonly Guid EmpresaBeta = Guid.Parse("0f000000-0000-4000-8000-0000000000e2");
    private static readonly Guid ClienteAlfa = Guid.Parse("0f000000-0000-4000-8000-0000000000d1");
    private static readonly Guid ClienteBeta = Guid.Parse("0f000000-0000-4000-8000-0000000000d2");
    private static readonly Guid ClienteOtraSucursal = Guid.Parse("0f000000-0000-4000-8000-0000000000d3");
    private static readonly Guid Operador = Guid.Parse("0f000000-0000-4000-8000-0000000000b1");
    private static readonly Guid Operador2 = Guid.Parse("0f000000-0000-4000-8000-0000000000b3");
    private static readonly Guid PersonaOtroGrupo = Guid.Parse("0f000000-0000-4000-8000-0000000000b2");
    private static readonly Guid UnidadOtraSucursal = Guid.Parse("0f000000-0000-4000-8000-0000000000c4");
    private static readonly Guid Obra = Guid.Parse("0f000000-0000-4000-8000-0000000000f1");
    private static readonly Guid Ptar = Guid.Parse("0f000000-0000-4000-8000-0000000000f2");
    private const string NombreOperador = "Operador Ficticio Uno";
    private const string NombreOperador2 = "Operador Ficticio Dos";

    private readonly BaseDatosPrueba _bd;
    private ApiPrueba _api = null!;

    public RutasTests(BaseDatosPrueba bd) => _bd = bd;

    public async Task InitializeAsync()
    {
        _api = new ApiPrueba(_bd);
        // Catálogo ficticio; idempotente porque la base se comparte entre las pruebas de la clase.
        await _bd.SqlComoDuenoAsync($"""
            INSERT INTO negocio.organizacion VALUES ('{OtraOrg}', 'Otro grupo ficticio') ON CONFLICT DO NOTHING;
            INSERT INTO negocio.sucursal(organizacion_id, id, empresa_id, codigo, nombre)
            VALUES ('{Org}', '{Suc2}', '{Empresa}', 'PRUEBA2', 'Otra sucursal ficticia') ON CONFLICT DO NOTHING;
            INSERT INTO negocio.empresa VALUES ('{Org}', '{EmpresaAlfa}', 'CLI-ALFA'), ('{Org}', '{EmpresaBeta}', 'CLI-BETA') ON CONFLICT DO NOTHING;
            INSERT INTO negocio.ficha_empresa(organizacion_id, empresa_id, revision, nombre_fiscal, nombre_comercial) VALUES
                ('{Org}', '{EmpresaAlfa}', 1, 'Alfa Ficticia SA', 'Constructora Ficticia Alfa'),
                ('{Org}', '{EmpresaBeta}', 1, 'Beta Ficticia SA', NULL) ON CONFLICT DO NOTHING;
            INSERT INTO negocio.empresa_sucursal VALUES ('{Org}', '{Suc}', '{EmpresaAlfa}'), ('{Org}', '{Suc}', '{EmpresaBeta}'),
                ('{Org}', '{Suc2}', '{EmpresaAlfa}') ON CONFLICT DO NOTHING;
            INSERT INTO negocio.cliente VALUES ('{Org}', '{Suc}', '{ClienteAlfa}', '{EmpresaAlfa}', 'C-ALFA'),
                ('{Org}', '{Suc}', '{ClienteBeta}', '{EmpresaBeta}', 'C-BETA'),
                ('{Org}', '{Suc2}', '{ClienteOtraSucursal}', '{EmpresaAlfa}', 'C-OTRA') ON CONFLICT DO NOTHING;
            INSERT INTO seguridad.persona VALUES ('{Org}', '{Operador}', '{NombreOperador}'), ('{Org}', '{Operador2}', '{NombreOperador2}'),
                ('{OtraOrg}', '{PersonaOtroGrupo}', 'Persona de otro grupo') ON CONFLICT DO NOTHING;
            INSERT INTO negocio.vehiculo VALUES ('{Org}', '{Suc2}', '{UnidadOtraSucursal}', 'U-OTRA') ON CONFLICT DO NOTHING;
            INSERT INTO negocio.sitio VALUES ('{Org}', '{Suc}', '{Obra}', 'Obra'), ('{Org}', '{Suc}', '{Ptar}', 'PTAR') ON CONFLICT DO NOTHING;
            INSERT INTO negocio.ficha_sitio(organizacion_id, sucursal_id, sitio_id, revision, nombre, calle, numero_exterior, colonia, municipio, codigo_postal, referencia_localizacion) VALUES
                ('{Org}', '{Suc}', '{Obra}', 1, 'Obra Ficticia Norte', 'Calle Falsa', '123', 'Centro', 'Municipio Ficticio', '00000', 'Portón azul'),
                ('{Org}', '{Suc}', '{Ptar}', 1, 'PTAR Ficticia', NULL, NULL, NULL, NULL, NULL, NULL) ON CONFLICT DO NOTHING;
            INSERT INTO negocio.punto_servicio(organizacion_id, sucursal_id, sitio_id) VALUES ('{Org}', '{Suc}', '{Obra}') ON CONFLICT DO NOTHING;
            INSERT INTO negocio.cliente_sitio(organizacion_id,sucursal_id,cliente_id,sitio_id,revision)
            VALUES ('{Org}','{Suc}','{ClienteAlfa}','{Obra}',1),('{Org}','{Suc}','{ClienteBeta}','{Obra}',1) ON CONFLICT DO NOTHING;
            """);
    }

    public async Task DisposeAsync() => await _api.DisposeAsync();

    // ---------------- Apoyos ----------------

    private async Task<HttpClient> SesionAsync(bool escribir)
    {
        var correo = $"rutas-{Guid.NewGuid():N}@example.invalid";
        await _bd.AutorizarAsync(correo, escribir: escribir);
        var nav = _api.NuevoNavegador();
        Assert.Equal(HttpStatusCode.NoContent, (await nav.EntrarAsync(ValidadorFicticio.Token($"s-{correo}", correo))).StatusCode);
        return nav;
    }

    /// <summary>Unidad propia de cada prueba (capacidad 18 m³) para que los cambios de operador no se crucen.</summary>
    private async Task<Guid> NuevaUnidadAsync(Guid? operador = null)
    {
        var id = Guid.NewGuid();
        await _bd.SqlComoDuenoAsync($"""
            INSERT INTO negocio.vehiculo VALUES ('{Org}', '{Suc}', '{id}', 'U-{id.ToString("N")[..6]}');
            INSERT INTO negocio.ficha_vehiculo(organizacion_id, sucursal_id, vehiculo_id, revision, capacidad_id) VALUES ('{Org}', '{Suc}', '{id}', 1, 3);
            {(operador is { } p ? $"INSERT INTO negocio.asignacion_unidad(organizacion_id, sucursal_id, vehiculo_id, revision, persona_id) VALUES ('{Org}', '{Suc}', '{id}', 1, '{p}');" : "")}
            """);
        return id;
    }

    private static async Task<HttpResponseMessage> EnviarAsync(HttpClient nav, HttpMethod metodo, string url, object cuerpo, bool conCsrf = true)
    {
        var req = new HttpRequestMessage(metodo, url) { Content = JsonContent.Create(cuerpo) };
        if (conCsrf) req.Headers.Add(ServiciosAcceso.CabeceraCsrf, await nav.CsrfAsync());
        return await nav.SendAsync(req);
    }

    private static object Ruta(string nombre, Guid unidad, int[]? dias = null, Guid? operador = null, Guid? sucursal = null) =>
        new { sucursalId = sucursal ?? Suc, nombre, dias = dias ?? [1, 3, 5], unidadId = unidad, operadorId = operador };

    private static object Parada(Guid? sitio, params (Guid Cliente, int Banos)[] clientes) =>
        new { sitioId = sitio, clientes = clientes.Select(c => new { clienteId = c.Cliente, banos = c.Banos }).ToArray() };

    private static async Task<RevisionGuardada> CrearRutaAsync(HttpClient nav, string nombre, Guid unidad, Guid? operador = null)
    {
        var r = await EnviarAsync(nav, HttpMethod.Post, "/api/rutas", Ruta(nombre, unidad, operador: operador));
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        return (await r.Content.ReadFromJsonAsync<RevisionGuardada>())!;
    }

    private static async Task<RevisionGuardada> AgregarParadaAsync(HttpClient nav, Guid ruta, object cuerpo)
    {
        var r = await EnviarAsync(nav, HttpMethod.Post, $"/api/rutas/{ruta}/paradas", cuerpo);
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        return (await r.Content.ReadFromJsonAsync<RevisionGuardada>())!;
    }

    private static async Task<RutaResumen> ResumenAsync(HttpClient nav, Guid ruta) =>
        Assert.Single((await nav.GetFromJsonAsync<List<RutaResumen>>("/api/rutas"))!, x => x.Id == ruta);

    private static async Task<List<ParadaDetalle>> ParadasAsync(HttpClient nav, Guid ruta) =>
        (await nav.GetFromJsonAsync<List<ParadaDetalle>>($"/api/rutas/{ruta}/paradas"))!;

    private static async Task AfirmarErrorAsync(HttpResponseMessage r, HttpStatusCode codigo, string? mensaje = null)
    {
        Assert.Equal(codigo, r.StatusCode);
        var cuerpo = (await r.Content.ReadFromJsonAsync<MensajeError>())!;
        if (mensaje is null) Assert.False(string.IsNullOrWhiteSpace(cuerpo.Mensaje));
        else Assert.Equal(mensaje, cuerpo.Mensaje);
    }

    /// <summary>Huella de todo lo escrito para una ruta: revisiones, paradas con posición y clientes, y operador de su unidad.</summary>
    private Task<string> HuellaAsync(Guid ruta) => _bd.ConsultarAsync($"""
        SELECT (SELECT count(*) FROM negocio.ruta_version WHERE ruta_id = '{ruta}') || '|' ||
               (SELECT coalesce(string_agg(p.id || ':' || p.posicion, ',' ORDER BY p.id), '') FROM negocio.parada p WHERE p.ruta_id = '{ruta}') || '|' ||
               (SELECT count(*) FROM negocio.parada_version v JOIN negocio.parada p ON p.id = v.parada_id WHERE p.ruta_id = '{ruta}') || '|' ||
               (SELECT count(*) FROM negocio.asignacion_unidad a JOIN negocio.ruta_version v ON v.vehiculo_id = a.vehiculo_id WHERE v.ruta_id = '{ruta}') || '|' ||
               (SELECT count(*) FROM negocio.sitio)
        """.ReplaceLineEndings(" "));

    // ---------------- Lectura ----------------

    [Fact]
    public async Task Sin_sesion_no_lista_ni_crea()
    {
        var nav = _api.NuevoNavegador();
        Assert.Equal(HttpStatusCode.Unauthorized, (await nav.GetAsync("/api/rutas")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await EnviarAsync(nav, HttpMethod.Post, "/api/rutas", Ruta("R-ANON", UnidadOtraSucursal))).StatusCode);
        Assert.Equal("0", await _bd.ConsultarAsync("SELECT count(*) FROM negocio.ruta_version WHERE nombre = 'R-ANON'"));
    }

    [Fact]
    public async Task Opciones_ofrecen_unidades_clientes_y_solo_puntos_de_servicio()
    {
        var unidad = await NuevaUnidadAsync(Operador);
        var nav = await SesionAsync(escribir: true);
        var o = (await nav.GetFromJsonAsync<OpcionesRuta>($"/api/rutas/opciones?sucursalId={Suc}"))!;

        var u = Assert.Single(o.Unidades, x => x.Id == unidad);
        Assert.Equal(18m, decimal.Round(u.CapacidadM3!.Value));
        Assert.Equal((Operador, NombreOperador), (u.OperadorId!.Value, u.OperadorNombre));
        Assert.DoesNotContain(o.Unidades, x => x.Id == UnidadOtraSucursal);
        Assert.Contains(new OperadorOpcion(Operador2, NombreOperador2), o.Operadores);
        Assert.DoesNotContain(o.Operadores, x => x.Id == PersonaOtroGrupo);
        // Nombre comercial, si no fiscal; solo clientes de la sucursal.
        Assert.Equal([new ClienteOpcion(ClienteBeta, "Beta Ficticia SA"), new ClienteOpcion(ClienteAlfa, "Constructora Ficticia Alfa")], o.Clientes);
        var obra = Assert.Single(o.PuntosServicio, x => x.Id == Obra);
        Assert.Equal((Obra,"Obra Ficticia Norte","Calle Falsa 123, Centro, Municipio Ficticio, 00000"),(obra.Id,obra.Nombre,obra.Direccion));
        Assert.Contains(obra.Clientes!,c=>c.ClienteId==ClienteAlfa);
        Assert.DoesNotContain(o.PuntosServicio, x => x.Id == Ptar);

        var lectura = await SesionAsync(escribir: false);
        Assert.Empty((await lectura.GetFromJsonAsync<OpcionesRuta>($"/api/rutas/opciones?sucursalId={Suc}"))!.Operadores);
    }

    // ---------------- Rutas ----------------

    [Fact]
    public async Task Crea_sin_cambiar_operador_y_lista_sin_paradas()
    {
        var unidad = await NuevaUnidadAsync(Operador);
        var nav = await SesionAsync(escribir: true);
        var r = await EnviarAsync(nav, HttpMethod.Post, "/api/rutas", Ruta("  R-01 Norte  ", unidad, dias: [5, 1, 3, 1]));
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        var creada = (await r.Content.ReadFromJsonAsync<RevisionGuardada>())!;
        Assert.Equal(1, creada.Revision);

        var ruta = await ResumenAsync(nav, creada.Id);
        Assert.Equal((Suc, 1, "R-01 Norte"), (ruta.SucursalId, ruta.Revision, ruta.Nombre));
        Assert.Equal([1, 3, 5], ruta.Dias);
        Assert.Equal(unidad, ruta.Unidad.Id);
        Assert.Equal(18m, decimal.Round(ruta.Unidad.CapacidadM3!.Value));
        Assert.Equal(new OperadorResumen(Operador, NombreOperador), ruta.Operador);
        Assert.Equal((0, 0, 0), (ruta.Paradas, ruta.Clientes, ruta.Banos));
        // Máscara lunes|miércoles|viernes, autor tomado de la sesión y sin nueva asignación de unidad.
        Assert.Equal("21|true|1", await _bd.ConsultarAsync(
            $"SELECT v.dias || '|' || (r.creada_por_acceso_id IS NOT NULL) || '|' || (SELECT count(*) FROM negocio.asignacion_unidad WHERE vehiculo_id = '{unidad}') FROM negocio.ruta r JOIN negocio.ruta_version v ON v.ruta_id = r.id WHERE r.id = '{creada.Id}'"));

        // El mismo operador que ya tiene la unidad tampoco genera revisión.
        await CrearRutaAsync(nav, "R-01 Mismo operador", unidad, Operador);
        Assert.Equal("1", await _bd.ConsultarAsync($"SELECT count(*) FROM negocio.asignacion_unidad WHERE vehiculo_id = '{unidad}'"));
    }

    [Fact]
    public async Task Crea_con_cambio_de_operador_de_la_unidad()
    {
        var unidad = await NuevaUnidadAsync(Operador);
        var nav = await SesionAsync(escribir: true);
        var otra = await CrearRutaAsync(nav, "R-02 Previa", unidad);
        var creada = await CrearRutaAsync(nav, "R-02 Sur", unidad, Operador2);

        Assert.Equal($"2|{Operador2}", await _bd.ConsultarAsync(
            $"SELECT revision || '|' || persona_id FROM negocio.asignacion_unidad WHERE vehiculo_id = '{unidad}' ORDER BY revision DESC LIMIT 1"));
        // El operador es de la unidad: cambia en todas sus rutas.
        Assert.Equal(new OperadorResumen(Operador2, NombreOperador2), (await ResumenAsync(nav, creada.Id)).Operador);
        Assert.Equal(new OperadorResumen(Operador2, NombreOperador2), (await ResumenAsync(nav, otra.Id)).Operador);
    }

    [Fact]
    public async Task Edita_con_revision_vigente_y_rechaza_revision_vieja()
    {
        var unidad = await NuevaUnidadAsync(Operador);
        var otraUnidad = await NuevaUnidadAsync();
        var nav = await SesionAsync(escribir: true);
        var ruta = await CrearRutaAsync(nav, "R-03 Original", unidad);

        var r = await EnviarAsync(nav, HttpMethod.Put, $"/api/rutas/{ruta.Id}",
            new { nombre = "R-03 Corregida", dias = new[] { 2, 4, 6 }, unidadId = otraUnidad, operadorId = Operador2, revision = 1 });
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal(new RevisionGuardada(ruta.Id, 2), await r.Content.ReadFromJsonAsync<RevisionGuardada>());
        var resumen = await ResumenAsync(nav, ruta.Id);
        Assert.Equal((2, "R-03 Corregida", otraUnidad), (resumen.Revision, resumen.Nombre, resumen.Unidad.Id));
        Assert.Equal([2, 4, 6], resumen.Dias);
        Assert.Equal(new OperadorResumen(Operador2, NombreOperador2), resumen.Operador);

        // Alguien más ya guardó la revisión 2: editar sobre la 1 es conflicto y no escribe.
        await AfirmarErrorAsync(await EnviarAsync(nav, HttpMethod.Put, $"/api/rutas/{ruta.Id}",
            new { nombre = "R-03 Vieja", dias = new[] { 1 }, unidadId = unidad, revision = 1 }),
            HttpStatusCode.Conflict, RepositorioRutas.MensajeRutaModificada);

        // Sin cambios: responde la revisión vigente sin insertar.
        r = await EnviarAsync(nav, HttpMethod.Put, $"/api/rutas/{ruta.Id}",
            new { nombre = "R-03 Corregida", dias = new[] { 6, 2, 4 }, unidadId = otraUnidad, revision = 2 });
        Assert.Equal(new RevisionGuardada(ruta.Id, 2), await r.Content.ReadFromJsonAsync<RevisionGuardada>());
        Assert.Equal("2", await _bd.ConsultarAsync($"SELECT count(*) FROM negocio.ruta_version WHERE ruta_id = '{ruta.Id}'"));
    }

    [Fact]
    public async Task Cambiar_solo_el_operador_crea_revision_y_la_edicion_vieja_da_409()
    {
        var unidad = await NuevaUnidadAsync(Operador);
        var nav = await SesionAsync(escribir: true);
        var ruta = await CrearRutaAsync(nav, "R-08 Operador", unidad);
        var original = await ResumenAsync(nav, ruta.Id);

        // A cambia solo el operador sobre la revisión 1.
        var r = await EnviarAsync(nav, HttpMethod.Put, $"/api/rutas/{ruta.Id}",
            new { nombre = original.Nombre, dias = original.Dias, unidadId = unidad, operadorId = Operador2, revision = 1 });
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal(new RevisionGuardada(ruta.Id, 2), await r.Content.ReadFromJsonAsync<RevisionGuardada>());

        // B editó sobre la revisión 1 con el operador que cargó: conflicto, no revierte el cambio de A.
        await AfirmarErrorAsync(await EnviarAsync(nav, HttpMethod.Put, $"/api/rutas/{ruta.Id}",
            new { nombre = "R-08 Renombrada", dias = original.Dias, unidadId = unidad, operadorId = Operador, revision = 1 }),
            HttpStatusCode.Conflict, RepositorioRutas.MensajeRutaModificada);
        var resumen = await ResumenAsync(nav, ruta.Id);
        Assert.Equal((2, original.Nombre), (resumen.Revision, resumen.Nombre));
        Assert.Equal(new OperadorResumen(Operador2, NombreOperador2), resumen.Operador);
    }

    [Fact]
    public async Task Ruta_invalida_responde_400_sin_crear()
    {
        var nav = await SesionAsync(escribir: true);
        var unidad = await NuevaUnidadAsync();
        var casos = new (string Caso, object Cuerpo)[]
        {
            ("nombre vacío", Ruta("   ", unidad)),
            ("nombre largo", Ruta(new string('x', EndpointsRutas.LargoMaximoNombre + 1), unidad)),
            ("sin días", Ruta("R-INV", unidad, dias: [])),
            ("día fuera de la semana", Ruta("R-INV", unidad, dias: [1, 8])),
            ("unidad de otra sucursal", Ruta("R-INV", UnidadOtraSucursal)),
            ("operador inexistente", Ruta("R-INV", unidad, operador: Guid.NewGuid())),
        };
        var antes = await _bd.ConsultarAsync("SELECT count(*) FROM negocio.ruta");
        foreach (var (caso, cuerpo) in casos)
        {
            var r = await EnviarAsync(nav, HttpMethod.Post, "/api/rutas", cuerpo);
            Assert.True(r.StatusCode == HttpStatusCode.BadRequest, caso);
            await AfirmarErrorAsync(r, HttpStatusCode.BadRequest);
        }
        Assert.Equal(antes, await _bd.ConsultarAsync("SELECT count(*) FROM negocio.ruta"));
    }

    [Fact]
    public async Task Operador_de_otro_grupo_responde_400()
    {
        var unidad = await NuevaUnidadAsync(Operador);
        var nav = await SesionAsync(escribir: true);
        await AfirmarErrorAsync(await EnviarAsync(nav, HttpMethod.Post, "/api/rutas", Ruta("R-OP-AJENO", unidad, operador: PersonaOtroGrupo)),
            HttpStatusCode.BadRequest, RepositorioRutas.MensajeOperadorInvalido);
        Assert.Equal("1", await _bd.ConsultarAsync($"SELECT count(*) FROM negocio.asignacion_unidad WHERE vehiculo_id = '{unidad}'"));
    }

    [Fact]
    public async Task Sucursal_sin_permiso_responde_403()
    {
        var nav = await SesionAsync(escribir: true);
        var r = await EnviarAsync(nav, HttpMethod.Post, "/api/rutas", Ruta("R-OTRA", UnidadOtraSucursal, sucursal: Suc2));
        await AfirmarErrorAsync(r, HttpStatusCode.Forbidden, EndpointsRutas.MensajeSinPermiso);
        Assert.Equal("0", await _bd.ConsultarAsync("SELECT count(*) FROM negocio.ruta_version WHERE nombre = 'R-OTRA'"));
    }

    // ---------------- Paradas ----------------

    [Fact]
    public async Task Baja_y_alta_de_parada_concurrentes_no_dejan_direccion_eliminada_en_uso()
    {
        using var nav=await SesionAsync(escribir:true);
        var alta=await EnviarAsync(nav,HttpMethod.Post,"/api/clientes",new {sucursalId=Suc,nombreComercial="Cliente Concurrente Ficticio",sitios=new[]{new {nombre="Sitio Concurrente Ficticio",calle="Calle Ficticia",banos=5,fosas=0}}});
        Assert.Equal(HttpStatusCode.Created,alta.StatusCode);
        var id=(await alta.Content.ReadFromJsonAsync<RevisionGuardada>())!.Id;
        var cliente=Assert.Single((await nav.GetFromJsonAsync<List<Manifiestos.Api.Clientes.ClienteResumen>>("/api/clientes"))!,c=>c.Id==id);
        var sitio=Assert.Single(cliente.Sitios!);
        var ruta=await CrearRutaAsync(nav,"Ruta concurrente de sitio",await NuevaUnidadAsync());
        var tareas=await Task.WhenAll(
            EnviarAsync(nav,HttpMethod.Post,$"/api/rutas/{ruta.Id}/paradas",new {sitioId=sitio.SitioId,clientes=new[]{new {clienteId=id,banos=3}}}),
            EnviarAsync(nav,HttpMethod.Put,$"/api/clientes/{id}",new {nombreComercial=cliente.NombreComercial,revision=cliente.Revision,sitios=new[]{new {sitio.SitioId,sitio.Revision,sitio.RevisionFicha,eliminar=true}}}));
        var actual=Assert.Single((await nav.GetFromJsonAsync<List<Manifiestos.Api.Clientes.ClienteResumen>>("/api/clientes"))!,c=>c.Id==id);
        if (tareas[0].StatusCode==HttpStatusCode.Created)
        {
            Assert.Equal(HttpStatusCode.BadRequest,tareas[1].StatusCode);
            Assert.True(Assert.Single(actual.Sitios!).EnRuta);
        }
        else
        {
            Assert.Equal(HttpStatusCode.BadRequest,tareas[0].StatusCode);
            Assert.Equal(HttpStatusCode.OK,tareas[1].StatusCode);
            Assert.Empty(actual.Sitios!);
            Assert.Empty(await ParadasAsync(nav,ruta.Id));
        }
    }

    [Fact]
    public async Task Registra_varias_direcciones_y_sugiere_inventario_sin_confundir_domicilio_fiscal()
    {
        using var nav=await SesionAsync(escribir:true);
        object Sitio(string nombre,string municipio,int banos,int fosas)=>new {nombre,calle="Calle de Servicio Ficticia",numeroExterior="7",municipio,banos,fosas};
        var creado=await EnviarAsync(nav,HttpMethod.Post,"/api/clientes",new {sucursalId=Suc,nombreComercial="Cliente Multisitio Ficticio",calle="Domicilio Fiscal Ficticio",sitios=new[]{Sitio("Baños Norte","Municipio Norte",10,0),Sitio("Fosa Sur","Municipio Sur",0,2)}});
        Assert.Equal(HttpStatusCode.Created,creado.StatusCode);
        var id=(await creado.Content.ReadFromJsonAsync<RevisionGuardada>())!.Id;
        var cliente=Assert.Single((await nav.GetFromJsonAsync<List<Manifiestos.Api.Clientes.ClienteResumen>>("/api/clientes"))!,c=>c.Id==id);
        Assert.Equal("Domicilio Fiscal Ficticio",cliente.Calle);
        Assert.Equal(2,cliente.Sitios!.Count);
        var fosa=Assert.Single(cliente.Sitios,s=>s.Nombre=="Fosa Sur");
        var opciones=(await nav.GetFromJsonAsync<OpcionesRuta>($"/api/rutas/opciones?sucursalId={Suc}"))!;
        Assert.Equal(2,Assert.Single(opciones.PuntosServicio.Single(s=>s.Id==fosa.SitioId).Clientes!,c=>c.ClienteId==id).Fosas);
        var ruta=await CrearRutaAsync(nav,"Ruta con direcciones ficticias",await NuevaUnidadAsync());
        await AgregarParadaAsync(nav,ruta.Id,new {sitioId=fosa.SitioId,clientes=new[]{new {clienteId=id,banos=0,fosas=1}}});
        var ajena=await EnviarAsync(nav,HttpMethod.Post,$"/api/rutas/{ruta.Id}/paradas",new {sitioId=fosa.SitioId,clientes=new[]{new {clienteId=ClienteAlfa,banos=1}}});
        Assert.Equal(HttpStatusCode.BadRequest,ajena.StatusCode);

        var cambio=fosa with { Calle="Calle de Servicio Corregida",Fosas=3 };
        var guardar=await EnviarAsync(nav,HttpMethod.Put,$"/api/clientes/{id}",new {nombreComercial=cliente.NombreComercial,calle=cliente.Calle,revision=cliente.Revision,sitios=new[]{cambio}});
        Assert.Equal(HttpStatusCode.OK,guardar.StatusCode);
        var vieja=await EnviarAsync(nav,HttpMethod.Put,$"/api/clientes/{id}",new {nombreComercial=cliente.NombreComercial,calle=cliente.Calle,revision=cliente.Revision,sitios=new[]{fosa}});
        Assert.Equal(HttpStatusCode.Conflict,vieja.StatusCode);
        var actual=Assert.Single((await nav.GetFromJsonAsync<List<Manifiestos.Api.Clientes.ClienteResumen>>("/api/clientes"))!,c=>c.Id==id);
        Assert.Equal(2,actual.Sitios!.Count); // Omitir la otra dirección no la borra.
        Assert.Equal(3,actual.Sitios.Single(s=>s.SitioId==fosa.SitioId).Fosas);
        Assert.Equal("2",await _bd.ConsultarAsync($"SELECT fosas_registradas FROM negocio.cliente_sitio WHERE cliente_id='{id}' AND sitio_id='{fosa.SitioId}' AND revision=1"));
        Assert.Single(await ParadasAsync(nav,ruta.Id));
        object Baja(Manifiestos.Api.Clientes.SitioCliente s)=>new {s.SitioId,s.Revision,s.RevisionFicha,eliminar=true};
        var usada=actual.Sitios.Single(s=>s.SitioId==fosa.SitioId);
        var noBorrar=await EnviarAsync(nav,HttpMethod.Put,$"/api/clientes/{id}",new {nombreComercial="No debe guardarse",revision=actual.Revision,sitios=new[]{Baja(usada)}});
        Assert.Equal(HttpStatusCode.BadRequest,noBorrar.StatusCode);
        var despues=Assert.Single((await nav.GetFromJsonAsync<List<Manifiestos.Api.Clientes.ClienteResumen>>("/api/clientes"))!,c=>c.Id==id);
        Assert.Equal(actual.NombreComercial,despues.NombreComercial); // Todo el envío se revierte.
        var libre=actual.Sitios.Single(s=>s.SitioId!=fosa.SitioId);
        var borrar=await EnviarAsync(nav,HttpMethod.Put,$"/api/clientes/{id}",new {nombreComercial=actual.NombreComercial,calle=actual.Calle,revision=actual.Revision,sitios=new[]{Baja(libre)}});
        Assert.Equal(HttpStatusCode.OK,borrar.StatusCode);
        despues=Assert.Single((await nav.GetFromJsonAsync<List<Manifiestos.Api.Clientes.ClienteResumen>>("/api/clientes"))!,c=>c.Id==id);
        Assert.Equal(fosa.SitioId,Assert.Single(despues.Sitios!).SitioId);
        Assert.Equal("false",await _bd.ConsultarAsync($"SELECT activo::text FROM negocio.cliente_sitio WHERE cliente_id='{id}' AND sitio_id='{libre.SitioId}' ORDER BY revision DESC LIMIT 1"));
        Assert.Equal("1",await _bd.ConsultarAsync($"SELECT count(*) FROM negocio.cliente_sitio WHERE cliente_id='{id}' AND sitio_id='{libre.SitioId}' AND revision=1 AND activo"));
        var reutilizar=await EnviarAsync(nav,HttpMethod.Post,$"/api/rutas/{ruta.Id}/paradas",new {sitioId=libre.SitioId,clientes=new[]{new {clienteId=id,banos=1}}});
        Assert.Equal(HttpStatusCode.BadRequest,reutilizar.StatusCode);
    }

    [Fact]
    public async Task Un_cliente_puede_tener_banos_y_fosas_en_distintos_sitios_y_en_el_mismo()
    {
        using var nav = await SesionAsync(escribir: true);
        var ruta = await CrearRutaAsync(nav, "Servicios mixtos ficticios", await NuevaUnidadAsync());
        var primera = await AgregarParadaAsync(nav, ruta.Id, new { sitioId = Obra, clientes = new[] { new { clienteId = ClienteAlfa, banos = 10, fosas = 0 } } });
        var segunda = await AgregarParadaAsync(nav, ruta.Id, new { nuevoSitio = new { nombre = "Fosa ficticia en otro municipio", municipio = "Municipio Ficticio Sur" }, clientes = new[] { new { clienteId = ClienteAlfa, banos = 0, fosas = 1 } } });
        var paradas = await ParadasAsync(nav, ruta.Id);
        Assert.Equal(0, paradas.Single(p => p.Id == segunda.Id).Banos);
        Assert.Equal(1, Assert.Single(paradas.Single(p => p.Id == segunda.Id).Clientes).Fosas);

        var cambiar = await EnviarAsync(nav, HttpMethod.Put, $"/api/rutas/{ruta.Id}/paradas/{primera.Id}",
            new { sitioId = Obra, revision = 1, clientes = new[] { new { clienteId = ClienteAlfa, banos = 10, fosas = 2 } } });
        Assert.Equal(HttpStatusCode.OK, cambiar.StatusCode);
        Assert.Equal(2, (await cambiar.Content.ReadFromJsonAsync<RevisionGuardada>())!.Revision);
        var clientes = await nav.GetFromJsonAsync<List<Manifiestos.Api.Clientes.ClienteResumen>>("/api/clientes");
        var asignaciones = Assert.Single(clientes!, c => c.Id == ClienteAlfa).Asignaciones.Where(a => a.RutaId == ruta.Id).ToArray();
        Assert.Equal(2, asignaciones.Length);
        Assert.Equal(3, asignaciones.Sum(a => a.Fosas));
        Assert.Equal("0", await _bd.ConsultarAsync($"SELECT coalesce(fosas_planeadas::text, 'NULL') FROM negocio.parada_cliente c JOIN negocio.parada_version v ON v.id = c.version_id WHERE v.parada_id = '{primera.Id}' AND v.revision = 1 AND c.cliente_id = '{ClienteAlfa}'"));
    }

    [Fact]
    public async Task Agrega_paradas_con_punto_existente_y_nuevo_y_las_detalla()
    {
        var nav = await SesionAsync(escribir: true);
        var ruta = await CrearRutaAsync(nav, "R-04 Paradas", await NuevaUnidadAsync(Operador));

        var p1 = await AgregarParadaAsync(nav, ruta.Id, Parada(Obra, (ClienteAlfa, 15), (ClienteBeta, 4)));
        Assert.Equal(1, p1.Revision);
        var p2 = await AgregarParadaAsync(nav, ruta.Id, new
        {
            nuevoSitio = new { nombre = "  Fosa Ficticia Sur ", calle = "Avenida Inventada 9", colonia = "", municipio = "Municipio Ficticio", referencia = "Junto al tanque" },
            clientes = new[] { new { clienteId = ClienteAlfa, banos = 10 } },
        });

        // El punto nuevo queda como sitio + ficha revisión 1 + punto de servicio, sin datos inventados.
        Assert.Equal("Fosa Ficticia Sur|1|Fosa Ficticia Sur|Avenida Inventada 9|true|true|Junto al tanque|1", await _bd.ConsultarAsync($"""
            SELECT s.referencia || '|' || f.revision || '|' || f.nombre || '|' || f.calle || '|' || (f.colonia IS NULL) || '|' || (f.pais IS NULL)
                   || '|' || f.referencia_localizacion || '|' || (SELECT count(*) FROM negocio.punto_servicio ps WHERE ps.sitio_id = s.id)
            FROM negocio.parada_version v JOIN negocio.sitio s ON s.id = v.sitio_id JOIN negocio.ficha_sitio f ON f.sitio_id = s.id
            WHERE v.parada_id = '{p2.Id}'
            """.ReplaceLineEndings(" ")));

        var paradas = await ParadasAsync(nav, ruta.Id);
        Assert.Equal([(p1.Id, 1), (p2.Id, 2)], paradas.Select(p => (p.Id, p.Posicion)));
        var primera = paradas[0];
        Assert.Equal(new SitioDetalle(Obra, "Obra Ficticia Norte", "Calle Falsa 123, Centro, Municipio Ficticio, 00000", "Portón azul"), primera.Sitio);
        Assert.Equal([new ClienteParada(ClienteBeta, "Beta Ficticia SA", 4), new ClienteParada(ClienteAlfa, "Constructora Ficticia Alfa", 15)], primera.Clientes);
        Assert.Equal(19, primera.Banos);
        Assert.Equal(("Fosa Ficticia Sur", "Avenida Inventada 9, Municipio Ficticio"), (paradas[1].Sitio.Nombre, paradas[1].Sitio.Direccion));
        Assert.Equal(10, paradas[1].Banos);

        var resumen = await ResumenAsync(nav, ruta.Id);
        Assert.Equal((2, 2, 29), (resumen.Paradas, resumen.Clientes, resumen.Banos));

        // El punto nuevo ya se ofrece como opción.
        Assert.Contains((await nav.GetFromJsonAsync<OpcionesRuta>($"/api/rutas/opciones?sucursalId={Suc}"))!.PuntosServicio,
            x => x.Id == paradas[1].Sitio.Id);
    }

    [Fact]
    public async Task Paradas_invalidas_responden_400_sin_escribir()
    {
        var nav = await SesionAsync(escribir: true);
        var ruta = await CrearRutaAsync(nav, "R-05 Invalidas", await NuevaUnidadAsync());
        var antes = await HuellaAsync(ruta.Id);
        var url = $"/api/rutas/{ruta.Id}/paradas";

        // Una PTAR es sitio de descarga, no punto de servicio.
        await AfirmarErrorAsync(await EnviarAsync(nav, HttpMethod.Post, url, Parada(Ptar, (ClienteAlfa, 5))),
            HttpStatusCode.BadRequest, RepositorioRutas.MensajeNoEsPuntoServicio);
        await AfirmarErrorAsync(await EnviarAsync(nav, HttpMethod.Post, url, Parada(Obra, (ClienteOtraSucursal, 5))),
            HttpStatusCode.BadRequest, RepositorioRutas.MensajeClienteInvalido);
        await AfirmarErrorAsync(await EnviarAsync(nav, HttpMethod.Post, url, Parada(Obra, (ClienteAlfa, 5), (ClienteAlfa, 3))), HttpStatusCode.BadRequest);
        await AfirmarErrorAsync(await EnviarAsync(nav, HttpMethod.Post, url, Parada(Obra, (ClienteAlfa, 0))), HttpStatusCode.BadRequest);
        await AfirmarErrorAsync(await EnviarAsync(nav, HttpMethod.Post, url, Parada(Obra, (ClienteAlfa, EndpointsRutas.MaximoBanos + 1))), HttpStatusCode.BadRequest);
        await AfirmarErrorAsync(await EnviarAsync(nav, HttpMethod.Post, url, Parada(Obra)), HttpStatusCode.BadRequest);
        await AfirmarErrorAsync(await EnviarAsync(nav, HttpMethod.Post, url, new { clientes = new[] { new { clienteId = ClienteAlfa } } }), HttpStatusCode.BadRequest);
        await AfirmarErrorAsync(await EnviarAsync(nav, HttpMethod.Post, url,
            new { sitioId = Obra, nuevoSitio = new { nombre = "Doble" }, clientes = new[] { new { clienteId = ClienteAlfa, banos = 1 } } }), HttpStatusCode.BadRequest);

        Assert.Equal(antes, await HuellaAsync(ruta.Id));
    }

    [Fact]
    public async Task Edita_parada_con_revision_vigente_y_rechaza_revision_vieja()
    {
        var nav = await SesionAsync(escribir: true);
        var ruta = await CrearRutaAsync(nav, "R-06 Correccion", await NuevaUnidadAsync());
        var p = await AgregarParadaAsync(nav, ruta.Id, Parada(Obra, (ClienteAlfa, 15)));
        var url = $"/api/rutas/{ruta.Id}/paradas/{p.Id}";

        // Corrección de operaciones: otros baños y otro cliente.
        var r = await EnviarAsync(nav, HttpMethod.Put, url, new
        {
            sitioId = Obra, revision = 1,
            clientes = new[] { new { clienteId = ClienteAlfa, banos = 12 }, new { clienteId = ClienteBeta, banos = 3 } },
        });
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal(new RevisionGuardada(p.Id, 2), await r.Content.ReadFromJsonAsync<RevisionGuardada>());
        var parada = Assert.Single(await ParadasAsync(nav, ruta.Id));
        Assert.Equal((2, 15), (parada.Revision, parada.Banos));
        Assert.Equal([ClienteBeta, ClienteAlfa], parada.Clientes.Select(c => c.ClienteId));

        await AfirmarErrorAsync(await EnviarAsync(nav, HttpMethod.Put, url, new { sitioId = Obra, revision = 1, clientes = new[] { new { clienteId = ClienteAlfa, banos = 1 } } }),
            HttpStatusCode.Conflict, RepositorioRutas.MensajeParadaModificada);
        Assert.Equal("2", await _bd.ConsultarAsync($"SELECT count(*) FROM negocio.parada_version WHERE parada_id = '{p.Id}'"));
        // La revisión 1 se conserva intacta.
        Assert.Equal("15", await _bd.ConsultarAsync(
            $"SELECT sum(c.banos_planeados) FROM negocio.parada_cliente c JOIN negocio.parada_version v ON v.id = c.version_id WHERE v.parada_id = '{p.Id}' AND v.revision = 1"));
    }

    [Fact]
    public async Task Reordena_paradas_y_rechaza_una_lista_distinta()
    {
        var nav = await SesionAsync(escribir: true);
        var ruta = await CrearRutaAsync(nav, "R-07 Orden", await NuevaUnidadAsync());
        var a = await AgregarParadaAsync(nav, ruta.Id, Parada(Obra, (ClienteAlfa, 1)));
        var b = await AgregarParadaAsync(nav, ruta.Id, Parada(Obra, (ClienteBeta, 2)));
        var c = await AgregarParadaAsync(nav, ruta.Id, Parada(Obra, (ClienteAlfa, 3)));
        var url = $"/api/rutas/{ruta.Id}/orden";

        var r = await EnviarAsync(nav, HttpMethod.Put, url, new { paradas = new[] { c.Id, a.Id, b.Id } });
        Assert.Equal(HttpStatusCode.NoContent, r.StatusCode);
        Assert.Equal([(c.Id, 1), (a.Id, 2), (b.Id, 3)], (await ParadasAsync(nav, ruta.Id)).Select(p => (p.Id, p.Posicion)));
        // El orden no crea revisiones.
        Assert.Equal("3", await _bd.ConsultarAsync(
            $"SELECT count(*) FROM negocio.parada_version v JOIN negocio.parada p ON p.id = v.parada_id WHERE p.ruta_id = '{ruta.Id}'"));

        var antes = await HuellaAsync(ruta.Id);
        foreach (var lista in new Guid[][] { [a.Id, b.Id], [a.Id, b.Id, c.Id, Guid.NewGuid()], [a.Id, a.Id, b.Id], [a.Id, b.Id, Guid.NewGuid()] })
            await AfirmarErrorAsync(await EnviarAsync(nav, HttpMethod.Put, url, new { paradas = lista }),
                HttpStatusCode.Conflict, RepositorioRutas.MensajeParadasCambiaron);
        Assert.Equal(antes, await HuellaAsync(ruta.Id));
    }

    // ---------------- Permisos, CSRF y no encontrados ----------------

    private static IEnumerable<(HttpMethod Metodo, string Url, object Cuerpo)> Escrituras(Guid ruta, Guid parada, Guid unidad) =>
    [
        (HttpMethod.Post, "/api/rutas", Ruta("R-NO-ESCRIBE", unidad, operador: Operador2)),
        (HttpMethod.Put, $"/api/rutas/{ruta}", new { nombre = "R-NO-ESCRIBE", dias = new[] { 7 }, unidadId = unidad, operadorId = Operador2, revision = 1 }),
        (HttpMethod.Post, $"/api/rutas/{ruta}/paradas", Parada(Obra, (ClienteBeta, 9))),
        (HttpMethod.Post, $"/api/rutas/{ruta}/paradas", new { nuevoSitio = new { nombre = "Sitio que no debe existir" }, clientes = new[] { new { clienteId = ClienteBeta, banos = 9 } } }),
        (HttpMethod.Put, $"/api/rutas/{ruta}/paradas/{parada}", new { sitioId = Obra, revision = 1, clientes = new[] { new { clienteId = ClienteBeta, banos = 9 } } }),
        (HttpMethod.Put, $"/api/rutas/{ruta}/orden", new { paradas = new[] { parada } }),
    ];

    private async Task<(Guid Ruta, Guid Parada, Guid Unidad)> RutaConParadaAsync(string nombre)
    {
        var nav = await SesionAsync(escribir: true);
        var unidad = await NuevaUnidadAsync(Operador);
        var ruta = await CrearRutaAsync(nav, nombre, unidad);
        var p = await AgregarParadaAsync(nav, ruta.Id, Parada(Obra, (ClienteAlfa, 2)));
        return (ruta.Id, p.Id, unidad);
    }

    [Fact]
    public async Task Solo_lectura_recibe_403_en_cada_escritura_y_no_cambia_nada()
    {
        var (ruta, parada, unidad) = await RutaConParadaAsync("R-08 Lectura");
        var nav = await SesionAsync(escribir: false);
        Assert.Single(await ParadasAsync(nav, ruta));
        var antes = await HuellaAsync(ruta);
        var rutasAntes = await _bd.ConsultarAsync("SELECT count(*) FROM negocio.ruta");

        foreach (var (metodo, url, cuerpo) in Escrituras(ruta, parada, unidad))
            await AfirmarErrorAsync(await EnviarAsync(nav, metodo, url, cuerpo), HttpStatusCode.Forbidden, EndpointsRutas.MensajeSinPermiso);

        Assert.Equal(antes, await HuellaAsync(ruta));
        Assert.Equal(rutasAntes, await _bd.ConsultarAsync("SELECT count(*) FROM negocio.ruta"));
    }

    [Fact]
    public async Task Sin_token_CSRF_no_escribe()
    {
        var (ruta, parada, unidad) = await RutaConParadaAsync("R-09 CSRF");
        var nav = await SesionAsync(escribir: true);
        var antes = await HuellaAsync(ruta);
        var rutasAntes = await _bd.ConsultarAsync("SELECT count(*) FROM negocio.ruta");

        foreach (var (metodo, url, cuerpo) in Escrituras(ruta, parada, unidad))
            Assert.Equal(HttpStatusCode.BadRequest, (await EnviarAsync(nav, metodo, url, cuerpo, conCsrf: false)).StatusCode);

        Assert.Equal(antes, await HuellaAsync(ruta));
        Assert.Equal(rutasAntes, await _bd.ConsultarAsync("SELECT count(*) FROM negocio.ruta"));
    }

    [Fact]
    public async Task Ruta_o_parada_inexistente_responde_404()
    {
        var (ruta, _, unidad) = await RutaConParadaAsync("R-10 Existe");
        var nav = await SesionAsync(escribir: true);
        var falsa = Guid.NewGuid();

        Assert.Equal(HttpStatusCode.NotFound, (await nav.GetAsync($"/api/rutas/{falsa}/paradas")).StatusCode);
        foreach (var (metodo, url, cuerpo) in Escrituras(falsa, Guid.NewGuid(), unidad).Skip(1))
            await AfirmarErrorAsync(await EnviarAsync(nav, metodo, url, cuerpo), HttpStatusCode.NotFound);
        // Parada que no existe dentro de una ruta que sí existe.
        await AfirmarErrorAsync(await EnviarAsync(nav, HttpMethod.Put, $"/api/rutas/{ruta}/paradas/{Guid.NewGuid()}",
            new { sitioId = Obra, revision = 1, clientes = new[] { new { clienteId = ClienteAlfa, banos = 1 } } }), HttpStatusCode.NotFound);
    }
}
