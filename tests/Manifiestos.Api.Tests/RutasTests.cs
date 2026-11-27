using System.Net;
using System.Net.Http.Json;
using Manifiestos.Api.Acceso;
using Manifiestos.Api.Ordenes;
using Manifiestos.Api.Rutas;
using Manifiestos.Api.Tests.Infraestructura;

namespace Manifiestos.Api.Tests;

/// <summary>
/// Rutas recurrentes y paradas (0008, 0027) contra PostgreSQL real: permisos por sucursal, revisiones, servicio de la ruta
/// (sanitarios | fosas según la clasificación de la unidad), nombre generado (número económico + terciado), parada = sitio con
/// clientes uno por uno, reasignación con confirmación, baja de paradas, carga de la unidad y conflictos. Datos ficticios.
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
    private static readonly Guid Obra2 = Guid.Parse("0f000000-0000-4000-8000-0000000000f3");
    private static readonly Guid Obra3 = Guid.Parse("0f000000-0000-4000-8000-0000000000f4");
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
            INSERT INTO negocio.sitio VALUES ('{Org}', '{Suc}', '{Obra}', 'Obra'), ('{Org}', '{Suc}', '{Ptar}', 'PTAR'),
                ('{Org}', '{Suc}', '{Obra2}', 'Obra 2'), ('{Org}', '{Suc}', '{Obra3}', 'Obra 3') ON CONFLICT DO NOTHING;
            INSERT INTO negocio.ficha_sitio(organizacion_id, sucursal_id, sitio_id, revision, nombre, calle, numero_exterior, colonia, municipio, codigo_postal, referencia_localizacion) VALUES
                ('{Org}', '{Suc}', '{Obra}', 1, 'Obra Ficticia Norte', 'Calle Falsa', '123', 'Centro', 'Municipio Ficticio', '00000', 'Portón azul'),
                ('{Org}', '{Suc}', '{Ptar}', 1, 'PTAR Ficticia', NULL, NULL, NULL, NULL, NULL, NULL),
                ('{Org}', '{Suc}', '{Obra2}', 1, 'Obra Ficticia Dos', NULL, NULL, NULL, NULL, NULL, NULL),
                ('{Org}', '{Suc}', '{Obra3}', 1, 'Obra Ficticia Tres', NULL, NULL, NULL, NULL, NULL, NULL) ON CONFLICT DO NOTHING;
            INSERT INTO negocio.punto_servicio(organizacion_id, sucursal_id, sitio_id) VALUES
                ('{Org}', '{Suc}', '{Obra}'), ('{Org}', '{Suc}', '{Obra2}'), ('{Org}', '{Suc}', '{Obra3}') ON CONFLICT DO NOTHING;
            INSERT INTO negocio.cliente_sitio(organizacion_id,sucursal_id,cliente_id,sitio_id,revision)
            VALUES ('{Org}','{Suc}','{ClienteAlfa}','{Obra}',1),('{Org}','{Suc}','{ClienteBeta}','{Obra}',1),
                   ('{Org}','{Suc}','{ClienteAlfa}','{Obra2}',1),('{Org}','{Suc}','{ClienteBeta}','{Obra2}',1),
                   ('{Org}','{Suc}','{ClienteAlfa}','{Obra3}',1),('{Org}','{Suc}','{ClienteBeta}','{Obra3}',1) ON CONFLICT DO NOTHING;
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

    /// <summary>Número económico de las unidades de prueba (el nombre de la ruta es este número + el terciado).</summary>
    private static string Num(Guid unidad) => $"U-{unidad.ToString("N")[..6]}";

    /// <summary>
    /// Unidad propia de cada prueba. Por omisión de sanitarios (1 m³, capacidad 4); "fosas" = 18 m³; null = sin clasificar (18 m³).
    /// </summary>
    private async Task<Guid> NuevaUnidadAsync(Guid? operador = null, string? clasificacion = "sanitarios", bool conFicha = true)
    {
        var id = Guid.NewGuid();
        var capacidad = clasificacion == "sanitarios" ? 4 : 3;
        await _bd.SqlComoDuenoAsync($"""
            INSERT INTO negocio.vehiculo VALUES ('{Org}', '{Suc}', '{id}', '{Num(id)}');
            {(conFicha ? $"INSERT INTO negocio.ficha_vehiculo(organizacion_id, sucursal_id, vehiculo_id, revision, capacidad_id, numero_economico, clasificacion) VALUES ('{Org}', '{Suc}', '{id}', 1, {capacidad}, '{Num(id)}', {(clasificacion is null ? "NULL" : $"'{clasificacion}'")});" : "")}
            {(operador is { } p ? $"INSERT INTO negocio.asignacion_unidad(organizacion_id, sucursal_id, vehiculo_id, revision, persona_id) VALUES ('{Org}', '{Suc}', '{id}', 1, '{p}');" : "")}
            """);
        return id;
    }

    /// <summary>Cliente propio de la prueba; con autorización de generador deja de ser cliente general.</summary>
    private async Task<Guid> NuevoClienteAsync(bool autorizado = false)
    {
        var (empresa, cliente) = (Guid.NewGuid(), Guid.NewGuid());
        await _bd.SqlComoDuenoAsync($"""
            INSERT INTO negocio.empresa VALUES ('{Org}', '{empresa}', 'E-{empresa.ToString("N")[..8]}');
            INSERT INTO negocio.ficha_empresa(organizacion_id, empresa_id, revision, nombre_fiscal, nombre_comercial) VALUES ('{Org}', '{empresa}', 1, 'Cliente {cliente.ToString("N")[..6]} SA', NULL);
            INSERT INTO negocio.empresa_sucursal VALUES ('{Org}', '{Suc}', '{empresa}');
            INSERT INTO negocio.cliente VALUES ('{Org}', '{Suc}', '{cliente}', '{empresa}', 'C-{cliente.ToString("N")[..8]}');
            {(autorizado ? $"INSERT INTO negocio.autorizacion_empresa(organizacion_id, empresa_id, autoridad, finalidad, numero) VALUES ('{Org}', '{empresa}', 'Medio ambiente', 'Generador', 'NL-FICT-{cliente.ToString("N")[..6]}');" : "")}
            """);
        return cliente;
    }

    /// <summary>Punto de servicio propio con los clientes dados registrados (baños y/o fosas registrados y propiedad de la fosa).</summary>
    private async Task<Guid> NuevoSitioAsync(Guid[] clientes, int? banos = 5, int? fosas = null, bool propia = false)
    {
        var sitio = Guid.NewGuid();
        await _bd.SqlComoDuenoAsync($"""
            INSERT INTO negocio.sitio VALUES ('{Org}', '{Suc}', '{sitio}', 'S-{sitio.ToString("N")[..8]}');
            INSERT INTO negocio.ficha_sitio(organizacion_id, sucursal_id, sitio_id, revision, nombre, calle, municipio) VALUES ('{Org}', '{Suc}', '{sitio}', 1, 'Sitio {sitio.ToString("N")[..6]}', 'Calle Ficticia', 'Municipio Ficticio');
            INSERT INTO negocio.punto_servicio(organizacion_id, sucursal_id, sitio_id) VALUES ('{Org}', '{Suc}', '{sitio}');
            -- Solo los clientes AUTORIZADOS tienen direcciones propias; los generales no se ligan a sitios (12-nov-2026).
            {string.Join("\n", clientes.Select(c => $"INSERT INTO negocio.cliente_sitio(organizacion_id,sucursal_id,cliente_id,sitio_id,revision,banos_registrados,fosas_registradas,fosa_propia) SELECT '{Org}','{Suc}',c.id,'{sitio}',1,{(banos?.ToString() ?? "NULL")},{(fosas?.ToString() ?? "NULL")},{propia.ToString().ToLowerInvariant()} FROM negocio.cliente c WHERE c.id='{c}' AND EXISTS (SELECT 1 FROM negocio.autorizacion_empresa a WHERE a.empresa_id=c.empresa_id AND a.autoridad='Medio ambiente' AND a.finalidad='Generador');"))}
            """);
        return sitio;
    }

    private static async Task<HttpResponseMessage> EnviarAsync(HttpClient nav, HttpMethod metodo, string url, object? cuerpo, bool conCsrf = true)
    {
        var req = new HttpRequestMessage(metodo, url) { Content = cuerpo is null ? null : JsonContent.Create(cuerpo) };
        if (conCsrf) req.Headers.Add(ServiciosAcceso.CabeceraCsrf, await nav.CsrfAsync());
        return await nav.SendAsync(req);
    }

    /// <summary>Cuerpo de alta de ruta: el nombre ya no se envía; "nombre" solo se manda en la prueba que comprueba que se ignora.</summary>
    private static object Ruta(Guid unidad, int[]? dias = null, Guid? operador = null, Guid? sucursal = null, string? nombre = null) =>
        new { sucursalId = sucursal ?? Suc, nombre, dias = dias ?? [1, 3, 5], unidadId = unidad, operadorId = operador };

    private static object Parada(Guid? sitio, params (Guid Cliente, int Banos)[] clientes) =>
        new { sitioId = sitio, clientes = clientes.Select(c => new { clienteId = c.Cliente, banos = c.Banos }).ToArray() };

    private static object ParadaFosas(Guid? sitio, params (Guid Cliente, int Fosas)[] clientes) =>
        new { sitioId = sitio, clientes = clientes.Select(c => new { clienteId = c.Cliente, banos = 0, fosas = c.Fosas }).ToArray() };

    private static async Task<RevisionGuardada> CrearRutaAsync(HttpClient nav, Guid unidad, Guid? operador = null, int[]? dias = null)
    {
        var r = await EnviarAsync(nav, HttpMethod.Post, "/api/rutas", Ruta(unidad, dias, operador));
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        return (await r.Content.ReadFromJsonAsync<RevisionGuardada>())!;
    }

    private static async Task<RevisionGuardada> AgregarParadaAsync(HttpClient nav, Guid ruta, object cuerpo)
    {
        var r = await EnviarAsync(nav, HttpMethod.Post, $"/api/rutas/{ruta}/paradas", cuerpo);
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        return (await r.Content.ReadFromJsonAsync<RevisionGuardada>())!;
    }

    private static async Task<RevisionGuardada> AgregarSitioAsync(HttpClient nav, Guid ruta, Guid sitio, bool reasignar = false)
    {
        var r = await EnviarAsync(nav, HttpMethod.Post, $"/api/rutas/{ruta}/paradas", new { sitioId = sitio, reasignar });
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        return (await r.Content.ReadFromJsonAsync<RevisionGuardada>())!;
    }

    private static Task<HttpResponseMessage> AgregarClienteAsync(HttpClient nav, Guid ruta, Guid parada, Guid cliente, int revision, int? banos = null, int? ordenes = null, bool reasignar = false) =>
        EnviarAsync(nav, HttpMethod.Post, $"/api/rutas/{ruta}/paradas/{parada}/clientes", new { clienteId = cliente, banos, ordenes, reasignar, revision });

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

    /// <summary>Error de negocio con código estable: 422 (regla) o 409 (conflicto) con {"error":"codigo","mensaje":"es"}.</summary>
    private static async Task<ErrorNegocio> AfirmarCodigoAsync(HttpResponseMessage r, HttpStatusCode estado, string codigo)
    {
        Assert.Equal(estado, r.StatusCode);
        var cuerpo = (await r.Content.ReadFromJsonAsync<ErrorNegocio>())!;
        Assert.Equal(codigo, cuerpo.Error);
        Assert.False(string.IsNullOrWhiteSpace(cuerpo.Mensaje));
        return cuerpo;
    }

    /// <summary>Huella de todo lo escrito para una ruta: revisiones, paradas con posición y clientes, y operador de su unidad.</summary>
    private Task<string> HuellaAsync(Guid ruta) => _bd.ConsultarAsync($"""
        SELECT (SELECT count(*) FROM negocio.ruta_version WHERE ruta_id = '{ruta}') || '|' ||
               (SELECT coalesce(string_agg(p.id || ':' || p.posicion, ',' ORDER BY p.id), '') FROM negocio.parada p WHERE p.ruta_id = '{ruta}') || '|' ||
               (SELECT count(*) FROM negocio.parada_version v JOIN negocio.parada p ON p.id = v.parada_id WHERE p.ruta_id = '{ruta}') || '|' ||
               (SELECT count(*) FROM negocio.parada_baja b JOIN negocio.parada p ON p.id = b.parada_id WHERE p.ruta_id = '{ruta}') || '|' ||
               (SELECT count(*) FROM negocio.asignacion_unidad a JOIN negocio.ruta_version v ON v.vehiculo_id = a.vehiculo_id WHERE v.ruta_id = '{ruta}') || '|' ||
               (SELECT count(*) FROM negocio.sitio)
        """.ReplaceLineEndings(" "));

    // ---------------- Lectura ----------------

    [Fact]
    public async Task Sin_sesion_no_lista_ni_crea()
    {
        var nav = _api.NuevoNavegador();
        Assert.Equal(HttpStatusCode.Unauthorized, (await nav.GetAsync("/api/rutas")).StatusCode);
        var antes = await _bd.ConsultarAsync("SELECT count(*) FROM negocio.ruta");
        Assert.Equal(HttpStatusCode.Unauthorized, (await EnviarAsync(nav, HttpMethod.Post, "/api/rutas", Ruta(UnidadOtraSucursal))).StatusCode);
        Assert.Equal(antes, await _bd.ConsultarAsync("SELECT count(*) FROM negocio.ruta"));
    }

    [Fact]
    public async Task Opciones_ofrecen_unidades_clientes_y_solo_puntos_de_servicio()
    {
        var unidad = await NuevaUnidadAsync(Operador);
        var nav = await SesionAsync(escribir: true);
        var o = (await nav.GetFromJsonAsync<OpcionesRuta>($"/api/rutas/opciones?sucursalId={Suc}"))!;

        var u = Assert.Single(o.Unidades, x => x.Id == unidad);
        Assert.Equal((1m, "sanitarios"), (decimal.Round(u.CapacidadM3!.Value), u.Clasificacion));
        Assert.Equal((Operador, NombreOperador), (u.OperadorId!.Value, u.OperadorNombre));
        Assert.DoesNotContain(o.Unidades, x => x.Id == UnidadOtraSucursal);
        Assert.Contains(new OperadorOpcion(Operador2, NombreOperador2), o.Operadores);
        Assert.DoesNotContain(o.Operadores, x => x.Id == PersonaOtroGrupo);
        // Nombre comercial, si no fiscal; solo clientes de la sucursal.
        Assert.Contains(new ClienteOpcion(ClienteBeta, "Beta Ficticia SA"), o.Clientes);
        Assert.Contains(new ClienteOpcion(ClienteAlfa, "Constructora Ficticia Alfa"), o.Clientes);
        Assert.DoesNotContain(o.Clientes, x => x.Id == ClienteOtraSucursal);
        var obra = Assert.Single(o.PuntosServicio, x => x.Id == Obra);
        Assert.Equal((Obra,"Obra Ficticia Norte","Calle Falsa 123, Centro, Municipio Ficticio, 00000"),(obra.Id,obra.Nombre,obra.Direccion));
        Assert.True(obra.Libre); // sitio normal: los clientes generales ya no se ligan a sitios
        Assert.DoesNotContain(o.PuntosServicio, x => x.Id == Ptar);

        var lectura = await SesionAsync(escribir: false);
        Assert.Empty((await lectura.GetFromJsonAsync<OpcionesRuta>($"/api/rutas/opciones?sucursalId={Suc}"))!.Operadores);
    }

    [Fact]
    public async Task Opciones_por_servicio_filtran_unidades_y_dicen_en_que_ruta_esta_cada_sitio_y_cliente_general()
    {
        var nav = await SesionAsync(escribir: true);
        var sanitarios = await NuevaUnidadAsync();
        var fosas = await NuevaUnidadAsync(clasificacion: "fosas");
        var sinClasificar = await NuevaUnidadAsync(clasificacion: null);
        var general = await NuevoClienteAsync();
        var autorizado = await NuevoClienteAsync(autorizado: true);
        var sitioLibre = await NuevoSitioAsync([]);
        var sitioDeAutorizado = await NuevoSitioAsync([autorizado], banos: null, fosas: 1);
        var ruta = await CrearRutaAsync(nav, sanitarios);
        await AgregarSitioAsync(nav, ruta.Id, sitioLibre);
        var rutaNombre = $"{Num(sanitarios)}-LMV";

        var s = (await nav.GetFromJsonAsync<OpcionesRuta>($"/api/rutas/opciones?sucursalId={Suc}&servicio=sanitarios"))!;
        Assert.Contains(s.Unidades, x => x.Id == sanitarios);
        Assert.DoesNotContain(s.Unidades, x => x.Id == fosas || x.Id == sinClasificar);
        // Sanitarios: solo sitios normales (sin dueño) y dice en qué ruta está cada uno; el de un autorizado no se ofrece.
        var libre = Assert.Single(s.Sitios!, x => x.Id == sitioLibre);
        Assert.Null(libre.Propietario);
        Assert.Equal(new RutaReferencia(ruta.Id, rutaNombre), libre.RutaActual);
        Assert.DoesNotContain(s.Sitios!, x => x.Id == sitioDeAutorizado);
        // Los generales se ofrecen todos (ya no dependen del sitio); el autorizado no es general.
        Assert.Contains(s.ClientesGenerales!, x => x.Id == general);
        Assert.DoesNotContain(s.ClientesGenerales!, x => x.Id == autorizado);
        // PTAR nunca.
        Assert.DoesNotContain(s.Sitios!, x => x.Id == Ptar);

        // Fosas: sitios normales y los de clientes autorizados, estos con su dueño.
        var f = (await nav.GetFromJsonAsync<OpcionesRuta>($"/api/rutas/opciones?sucursalId={Suc}&servicio=fosas"))!;
        Assert.Contains(f.Unidades, x => x.Id == fosas);
        Assert.DoesNotContain(f.Unidades, x => x.Id == sanitarios);
        Assert.Equal(autorizado, Assert.Single(f.Sitios!, x => x.Id == sitioDeAutorizado).Propietario!.Id);
        Assert.Null(Assert.Single(f.Sitios!, x => x.Id == sitioLibre).Propietario);        // Sin servicio se conserva el contrato anterior (todas las unidades).
        var todas = (await nav.GetFromJsonAsync<OpcionesRuta>($"/api/rutas/opciones?sucursalId={Suc}"))!;
        Assert.All(new[] { sanitarios, fosas, sinClasificar }, id => Assert.Contains(todas.Unidades, x => x.Id == id));
        Assert.Equal(HttpStatusCode.BadRequest, (await nav.GetAsync($"/api/rutas/opciones?sucursalId={Suc}&servicio=otro")).StatusCode);
    }

    // ---------------- Rutas ----------------

    [Fact]
    public async Task Crea_con_nombre_generado_sin_cambiar_operador_y_lista_sin_paradas()
    {
        var unidad = await NuevaUnidadAsync(Operador);
        var nav = await SesionAsync(escribir: true);
        // El nombre ya no se acepta libre: si llega, se ignora.
        var r = await EnviarAsync(nav, HttpMethod.Post, "/api/rutas", Ruta(unidad, dias: [5, 1, 3, 1], nombre: "  R-01 Norte  "));
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        var creada = (await r.Content.ReadFromJsonAsync<RevisionGuardada>())!;
        Assert.Equal(1, creada.Revision);

        var ruta = await ResumenAsync(nav, creada.Id);
        Assert.Equal((Suc, 1, $"{Num(unidad)}-LMV"), (ruta.SucursalId, ruta.Revision, ruta.Nombre));
        Assert.Equal([1, 3, 5], ruta.Dias);
        Assert.Equal(("sanitarios", "LMV"), (ruta.Servicio, ruta.Terciado));
        Assert.Equal(new UnidadResumen(unidad, Num(unidad), 1m, "sanitarios"), ruta.Unidad with { CapacidadM3 = decimal.Round(ruta.Unidad.CapacidadM3!.Value) });
        Assert.Equal(new OperadorResumen(Operador, NombreOperador), ruta.Operador);
        Assert.Equal((0, 0, 0), (ruta.Paradas, ruta.Clientes, ruta.Banos));
        Assert.Null(ruta.Ordenes);
        Assert.Null(ruta.VolumenEstimadoM3);
        Assert.Empty(ruta.ClientesNombres!);
        // Máscara lunes|miércoles|viernes, autor tomado de la sesión y sin nueva asignación de unidad.
        Assert.Equal($"21|true|1|{Num(unidad)}-LMV", await _bd.ConsultarAsync(
            $"SELECT v.dias || '|' || (r.creada_por_acceso_id IS NOT NULL) || '|' || (SELECT count(*) FROM negocio.asignacion_unidad WHERE vehiculo_id = '{unidad}') || '|' || v.nombre FROM negocio.ruta r JOIN negocio.ruta_version v ON v.ruta_id = r.id WHERE r.id = '{creada.Id}'"));

        // El mismo operador que ya tiene la unidad tampoco genera revisión. La segunda ruta de la unidad es el otro terciado.
        var segunda = await CrearRutaAsync(nav, unidad, Operador, dias: [2, 4, 6]);
        Assert.Equal(("MJS", $"{Num(unidad)}-MJS"), ((await ResumenAsync(nav, segunda.Id)).Terciado, (await ResumenAsync(nav, segunda.Id)).Nombre));
        Assert.Equal("1", await _bd.ConsultarAsync($"SELECT count(*) FROM negocio.asignacion_unidad WHERE vehiculo_id = '{unidad}'"));
    }

    [Fact]
    public async Task Martes_y_miercoles_se_escriben_Ma_y_Mi_no_son_ruta_duplicada_y_el_nombre_guardado_es_el_de_la_vista_previa()
    {
        var unidad = await NuevaUnidadAsync();
        var nav = await SesionAsync(escribir: true);
        var martes = await CrearRutaAsync(nav, unidad, dias: [2]);
        var miercoles = await CrearRutaAsync(nav, unidad, dias: [3]);
        Assert.Equal($"{Num(unidad)}-Ma", (await ResumenAsync(nav, martes.Id)).Nombre);   // igual que la vista previa del panel (formato.ts)
        Assert.Equal($"{Num(unidad)}-Mi", (await ResumenAsync(nav, miercoles.Id)).Nombre);
        // Los mismos días sí siguen siendo duplicado.
        await AfirmarCodigoAsync(await EnviarAsync(nav, HttpMethod.Post, "/api/rutas", Ruta(unidad, dias: [3])), HttpStatusCode.Conflict, "ruta_duplicada");
    }

    [Fact]
    public async Task Otros_dias_se_aceptan_con_iniciales_y_la_misma_unidad_con_el_mismo_terciado_es_ruta_duplicada()
    {
        var unidad = await NuevaUnidadAsync();
        var nav = await SesionAsync(escribir: true);
        // Otro conjunto de días no se rechaza: el nombre se deriva de las iniciales y terciado queda vacío.
        var lunesJueves = await CrearRutaAsync(nav, unidad, dias: [4, 1]);
        var r = await ResumenAsync(nav, lunesJueves.Id);
        Assert.Equal($"{Num(unidad)}-LJ", r.Nombre);
        Assert.Null(r.Terciado);
        Assert.Equal([1, 4], r.Dias);

        // Una unidad no puede estar en dos rutas el mismo día: L-M-V choca con L-J en el lunes.
        var antes = await _bd.ConsultarAsync("SELECT count(*) FROM negocio.ruta");
        var duplicada = await AfirmarCodigoAsync(await EnviarAsync(nav, HttpMethod.Post, "/api/rutas", Ruta(unidad, dias: [1, 3, 5])), HttpStatusCode.Conflict, "ruta_duplicada");
        Assert.Equal(new RutaReferencia(lunesJueves.Id, $"{Num(unidad)}-LJ"), duplicada.RutaActual);
        Assert.Equal(antes, await _bd.ConsultarAsync("SELECT count(*) FROM negocio.ruta"));

        // Con días libres sí se crea; editarla para pisar un día de la otra ruta es conflicto; no con ella misma.
        var miVie = await CrearRutaAsync(nav, unidad, dias: [3, 5]);
        await AfirmarCodigoAsync(await EnviarAsync(nav, HttpMethod.Put, $"/api/rutas/{miVie.Id}",
            new { dias = new[] { 1, 3, 5 }, unidadId = unidad, revision = 1 }), HttpStatusCode.Conflict, "ruta_duplicada");
        var mismaRuta = await EnviarAsync(nav, HttpMethod.Put, $"/api/rutas/{miVie.Id}", new { dias = new[] { 5, 3 }, unidadId = unidad, revision = 1 });
        Assert.Equal(HttpStatusCode.OK, mismaRuta.StatusCode);
        Assert.Equal(1, (await mismaRuta.Content.ReadFromJsonAsync<RevisionGuardada>())!.Revision);
        // Otra unidad sí puede tener ese terciado.
        await CrearRutaAsync(nav, await NuevaUnidadAsync());
    }

    [Fact]
    public async Task Unidad_sin_ficha_o_sin_clasificar_no_puede_tener_ruta()
    {
        var nav = await SesionAsync(escribir: true);
        var antes = await _bd.ConsultarAsync("SELECT count(*) FROM negocio.ruta");
        await AfirmarCodigoAsync(await EnviarAsync(nav, HttpMethod.Post, "/api/rutas", Ruta(await NuevaUnidadAsync(clasificacion: null))), HttpStatusCode.UnprocessableEntity, "unidad_sin_clasificar");
        await AfirmarCodigoAsync(await EnviarAsync(nav, HttpMethod.Post, "/api/rutas", Ruta(await NuevaUnidadAsync(conFicha: false))), HttpStatusCode.UnprocessableEntity, "unidad_sin_clasificar");
        Assert.Equal(antes, await _bd.ConsultarAsync("SELECT count(*) FROM negocio.ruta"));

        // Tampoco al editar una ruta existente hacia una unidad sin clasificar.
        var ruta = await CrearRutaAsync(nav, await NuevaUnidadAsync());
        await AfirmarCodigoAsync(await EnviarAsync(nav, HttpMethod.Put, $"/api/rutas/{ruta.Id}",
            new { dias = new[] { 1, 3, 5 }, unidadId = await NuevaUnidadAsync(clasificacion: null), revision = 1 }), HttpStatusCode.UnprocessableEntity, "unidad_sin_clasificar");
    }

    [Fact]
    public async Task Crea_con_cambio_de_operador_de_la_unidad()
    {
        var unidad = await NuevaUnidadAsync(Operador);
        var nav = await SesionAsync(escribir: true);
        var otra = await CrearRutaAsync(nav, unidad);
        var creada = await CrearRutaAsync(nav, unidad, Operador2, dias: [2, 4, 6]);

        Assert.Equal($"2|{Operador2}", await _bd.ConsultarAsync(
            $"SELECT revision || '|' || persona_id FROM negocio.asignacion_unidad WHERE vehiculo_id = '{unidad}' ORDER BY revision DESC LIMIT 1"));
        // El operador es de la unidad: cambia en todas sus rutas.
        Assert.Equal(new OperadorResumen(Operador2, NombreOperador2), (await ResumenAsync(nav, creada.Id)).Operador);
        Assert.Equal(new OperadorResumen(Operador2, NombreOperador2), (await ResumenAsync(nav, otra.Id)).Operador);
    }

    [Fact]
    public async Task Edita_con_revision_vigente_recalcula_el_nombre_y_rechaza_revision_vieja()
    {
        var unidad = await NuevaUnidadAsync(Operador);
        var otraUnidad = await NuevaUnidadAsync();
        var nav = await SesionAsync(escribir: true);
        var ruta = await CrearRutaAsync(nav, unidad);

        var r = await EnviarAsync(nav, HttpMethod.Put, $"/api/rutas/{ruta.Id}",
            new { dias = new[] { 2, 4, 6 }, unidadId = otraUnidad, operadorId = Operador2, revision = 1 });
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal(new RevisionGuardada(ruta.Id, 2), await r.Content.ReadFromJsonAsync<RevisionGuardada>());
        var resumen = await ResumenAsync(nav, ruta.Id);
        Assert.Equal((2, $"{Num(otraUnidad)}-MJS", otraUnidad), (resumen.Revision, resumen.Nombre, resumen.Unidad.Id));
        Assert.Equal([2, 4, 6], resumen.Dias);
        Assert.Equal(new OperadorResumen(Operador2, NombreOperador2), resumen.Operador);

        // Alguien más ya guardó la revisión 2: editar sobre la 1 es conflicto y no escribe.
        await AfirmarErrorAsync(await EnviarAsync(nav, HttpMethod.Put, $"/api/rutas/{ruta.Id}",
            new { dias = new[] { 1 }, unidadId = unidad, revision = 1 }),
            HttpStatusCode.Conflict, RepositorioRutas.MensajeRutaModificada);

        // Sin cambios: responde la revisión vigente sin insertar (el nombre ya coincide con el generado).
        r = await EnviarAsync(nav, HttpMethod.Put, $"/api/rutas/{ruta.Id}",
            new { dias = new[] { 6, 2, 4 }, unidadId = otraUnidad, revision = 2 });
        Assert.Equal(new RevisionGuardada(ruta.Id, 2), await r.Content.ReadFromJsonAsync<RevisionGuardada>());
        Assert.Equal("2", await _bd.ConsultarAsync($"SELECT count(*) FROM negocio.ruta_version WHERE ruta_id = '{ruta.Id}'"));
    }

    [Fact]
    public async Task Cambiar_solo_el_operador_crea_revision_y_la_edicion_vieja_da_409()
    {
        var unidad = await NuevaUnidadAsync(Operador);
        var nav = await SesionAsync(escribir: true);
        var ruta = await CrearRutaAsync(nav, unidad);
        var original = await ResumenAsync(nav, ruta.Id);

        // A cambia solo el operador sobre la revisión 1.
        var r = await EnviarAsync(nav, HttpMethod.Put, $"/api/rutas/{ruta.Id}",
            new { dias = original.Dias, unidadId = unidad, operadorId = Operador2, revision = 1 });
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal(new RevisionGuardada(ruta.Id, 2), await r.Content.ReadFromJsonAsync<RevisionGuardada>());

        // B editó sobre la revisión 1 con el operador que cargó: conflicto, no revierte el cambio de A.
        await AfirmarErrorAsync(await EnviarAsync(nav, HttpMethod.Put, $"/api/rutas/{ruta.Id}",
            new { dias = new[] { 2, 4 }, unidadId = unidad, operadorId = Operador, revision = 1 }),
            HttpStatusCode.Conflict, RepositorioRutas.MensajeRutaModificada);
        var resumen = await ResumenAsync(nav, ruta.Id);
        Assert.Equal((2, original.Nombre), (resumen.Revision, resumen.Nombre));
        Assert.Equal(new OperadorResumen(Operador2, NombreOperador2), resumen.Operador);
    }

    [Fact]
    public async Task Una_ruta_con_paradas_no_cambia_a_una_unidad_de_otro_servicio()
    {
        var nav = await SesionAsync(escribir: true);
        var ruta = await CrearRutaAsync(nav, await NuevaUnidadAsync());
        await AgregarParadaAsync(nav, ruta.Id, Parada(Obra, (ClienteAlfa, 4)));
        var antes = await HuellaAsync(ruta.Id);
        await AfirmarCodigoAsync(await EnviarAsync(nav, HttpMethod.Put, $"/api/rutas/{ruta.Id}",
            new { dias = new[] { 1, 3, 5 }, unidadId = await NuevaUnidadAsync(clasificacion: "fosas"), revision = 1 }), HttpStatusCode.UnprocessableEntity, "servicio_distinto");
        Assert.Equal(antes, await HuellaAsync(ruta.Id));
        // Sin paradas sí se puede (es una corrección del plan).
        var vacia = await CrearRutaAsync(nav, await NuevaUnidadAsync());
        var cambio = await EnviarAsync(nav, HttpMethod.Put, $"/api/rutas/{vacia.Id}", new { dias = new[] { 1, 3, 5 }, unidadId = await NuevaUnidadAsync(clasificacion: "fosas"), revision = 1 });
        Assert.Equal(HttpStatusCode.OK, cambio.StatusCode);
        Assert.Equal("fosas", (await ResumenAsync(nav, vacia.Id)).Servicio);
    }

    [Fact]
    public async Task Ruta_invalida_responde_400_sin_crear()
    {
        var nav = await SesionAsync(escribir: true);
        var unidad = await NuevaUnidadAsync();
        var casos = new (string Caso, object Cuerpo)[]
        {
            ("sin días", Ruta(unidad, dias: [])),
            ("día fuera de la semana", Ruta(unidad, dias: [1, 8])),
            ("unidad de otra sucursal", Ruta(UnidadOtraSucursal)),
            ("operador inexistente", Ruta(unidad, operador: Guid.NewGuid())),
            ("sin unidad", new { sucursalId = Suc, dias = new[] { 1 } }),
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
        await AfirmarErrorAsync(await EnviarAsync(nav, HttpMethod.Post, "/api/rutas", Ruta(unidad, operador: PersonaOtroGrupo)),
            HttpStatusCode.BadRequest, RepositorioRutas.MensajeOperadorInvalido);
        Assert.Equal("1", await _bd.ConsultarAsync($"SELECT count(*) FROM negocio.asignacion_unidad WHERE vehiculo_id = '{unidad}'"));
    }

    [Fact]
    public async Task Sucursal_sin_permiso_responde_403()
    {
        var nav = await SesionAsync(escribir: true);
        var antes = await _bd.ConsultarAsync($"SELECT count(*) FROM negocio.ruta WHERE sucursal_id = '{Suc2}'");
        var r = await EnviarAsync(nav, HttpMethod.Post, "/api/rutas", Ruta(UnidadOtraSucursal, sucursal: Suc2));
        await AfirmarErrorAsync(r, HttpStatusCode.Forbidden, EndpointsRutas.MensajeSinPermiso);
        Assert.Equal(antes, await _bd.ConsultarAsync($"SELECT count(*) FROM negocio.ruta WHERE sucursal_id = '{Suc2}'"));
    }

    // ---------------- Lista por servicio, terciado, carga y órdenes ----------------

    [Fact]
    public async Task Lista_por_servicio_con_carga_en_m3_en_sanitarios_y_ordenes_con_volumen_estimado_en_fosas()
    {
        var nav = await SesionAsync(escribir: true);
        var general = await NuevoClienteAsync();
        var duenoFosa = await NuevoClienteAsync(autorizado: true);
        var sitioBanos = await NuevoSitioAsync([general], banos: 12);
        var sitioFija = await NuevoSitioAsync([duenoFosa], banos: 0, fosas: 2);
        var sitioPropia = await NuevoSitioAsync([general], banos: 0, fosas: 1, propia: true);

        var unidadSan = await NuevaUnidadAsync();
        var lmv = await CrearRutaAsync(nav, unidadSan);
        var mjs = await CrearRutaAsync(nav, unidadSan, dias: [2, 4, 6]);
        var pSan = await AgregarSitioAsync(nav, lmv.Id, sitioBanos);
        Assert.Equal(HttpStatusCode.Created, (await AgregarClienteAsync(nav, lmv.Id, pSan.Id, general, 1, banos: 12)).StatusCode);

        var unidadFos = await NuevaUnidadAsync(clasificacion: "fosas");
        var fosas = await CrearRutaAsync(nav, unidadFos, dias: [2, 4, 6]);
        await AgregarParadaAsync(nav, fosas.Id, ParadaFosas(sitioFija, (duenoFosa, 2)));
        await AgregarParadaAsync(nav, fosas.Id, ParadaFosas(sitioPropia, (general, 1)));

        var s = await ResumenAsync(nav, lmv.Id);
        Assert.Equal(("sanitarios", "LMV", 1, 1, 12), (s.Servicio, s.Terciado, s.Paradas, s.Clientes, s.Banos));
        Assert.Null(s.Ordenes);
        Assert.Null(s.VolumenEstimadoM3);
        var cn = Assert.Single(s.ClientesNombres!);
        Assert.StartsWith("Cliente ", cn);
        // Carga de la UNIDAD (no de la ruta): m³ estimados contra su capacidad de 1 m³. Aún sin succiones registradas: cero.
        Assert.NotNull(s.Carga);
        Assert.Equal((unidadSan, 1m, 0, 0m, 0m), (s.Carga!.UnidadId, s.Carga.CapacidadM3, s.Carga.Banos, s.Carga.M3, s.Carga.Porcentaje));
        Assert.Null(s.Carga.UltimaTiradaEn);
        Assert.Empty(s.Carga.Desglose);
        // Dos rutas de la misma unidad ven exactamente la misma carga.
        var hermana = (await ResumenAsync(nav, mjs.Id)).Carga!;
        Assert.Equal((s.Carga.UnidadId, s.Carga.CapacidadM3, s.Carga.Banos, s.Carga.M3, s.Carga.Porcentaje),
            (hermana.UnidadId, hermana.CapacidadM3, hermana.Banos, hermana.M3, hermana.Porcentaje));
        Assert.Equal("MJS", (await ResumenAsync(nav, mjs.Id)).Terciado);

        var f = await ResumenAsync(nav, fosas.Id);
        Assert.Equal(("fosas", "MJS", 2, 0), (f.Servicio, f.Terciado, f.Paradas, f.Banos));
        Assert.Null(f.Carga);
        // 3 órdenes en total, pero el volumen estimado («unidad llena») cuenta solo la fosa FIJA (cliente autorizado): 2 × 18 m³.
        // La fosa de un cliente general es fosa PROPIA (pendiente PG-24) y no suma volumen.
        Assert.Equal(3, f.Ordenes);
        Assert.Equal(36m, f.VolumenEstimadoM3);
        var tipos = (await ParadasAsync(nav, fosas.Id)).Select(x => x.Tipo).ToList();
        Assert.Equal(["fosa_fija", "fosa_propia"], tipos);

        var soloSan = (await nav.GetFromJsonAsync<List<RutaResumen>>($"/api/rutas?servicio=sanitarios"))!;
        Assert.Contains(soloSan, x => x.Id == lmv.Id);
        Assert.DoesNotContain(soloSan, x => x.Id == fosas.Id);
        var soloFos = (await nav.GetFromJsonAsync<List<RutaResumen>>($"/api/rutas?servicio=fosas&sucursalId={Suc}"))!;
        Assert.Contains(soloFos, x => x.Id == fosas.Id);
        Assert.DoesNotContain(soloFos, x => x.Id == lmv.Id);
        Assert.Empty((await nav.GetFromJsonAsync<List<RutaResumen>>($"/api/rutas?sucursalId={Suc2}"))!);
        Assert.Equal(HttpStatusCode.BadRequest, (await nav.GetAsync("/api/rutas?servicio=otro")).StatusCode);
    }

    // ---------------- Paradas: contrato anterior (parada completa con clientes) ----------------

    [Fact]
    public async Task Baja_y_alta_de_parada_concurrentes_no_dejan_direccion_eliminada_en_uso()
    {
        using var nav=await SesionAsync(escribir:true);
        var alta=await EnviarAsync(nav,HttpMethod.Post,"/api/clientes",new {sucursalId=Suc,nombreComercial="Cliente Concurrente Ficticio",autorizacion="AMB-FICT-CONC",telefono="5550000099",correo="fixture@example.invalid",sitios=new[]{new {nombre="Sitio Concurrente Ficticio",calle="Calle Ficticia",banos=5,fosas=0,latitud=25.6m,longitud=-100.3m}}});
        Assert.Equal(HttpStatusCode.Created,alta.StatusCode);
        var id=(await alta.Content.ReadFromJsonAsync<RevisionGuardada>())!.Id;
        var cliente=Assert.Single((await nav.GetFromJsonAsync<List<Manifiestos.Api.Clientes.ClienteResumen>>("/api/clientes"))!,c=>c.Id==id);
        var sitio=Assert.Single(cliente.Sitios!);
        var ruta=await CrearRutaAsync(nav,await NuevaUnidadAsync(clasificacion:"fosas"));
        var tareas=await Task.WhenAll(
            EnviarAsync(nav,HttpMethod.Post,$"/api/rutas/{ruta.Id}/paradas",new {sitioId=sitio.SitioId,clientes=new[]{new {clienteId=id,banos=0,fosas=3}}}),
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
        object Sitio(string nombre,string municipio,int banos,int fosas)=>new {nombre,calle="Calle de Servicio Ficticia",numeroExterior="7",municipio,banos,fosas,latitud=25.6m,longitud=-100.3m};
        var creado=await EnviarAsync(nav,HttpMethod.Post,"/api/clientes",new {sucursalId=Suc,nombreComercial="Cliente Multisitio Ficticio",autorizacion="AMB-FICT-MULTI",telefono="5550000099",correo="fixture@example.invalid",calle="Domicilio Fiscal Ficticio",sitios=new[]{Sitio("Baños Norte","Municipio Norte",10,0),Sitio("Fosa Sur","Municipio Sur",0,2)}});
        Assert.Equal(HttpStatusCode.Created,creado.StatusCode);
        var id=(await creado.Content.ReadFromJsonAsync<RevisionGuardada>())!.Id;
        var cliente=Assert.Single((await nav.GetFromJsonAsync<List<Manifiestos.Api.Clientes.ClienteResumen>>("/api/clientes"))!,c=>c.Id==id);
        Assert.Equal("Domicilio Fiscal Ficticio",cliente.Calle);
        Assert.Equal(2,cliente.Sitios!.Count);
        var fosa=Assert.Single(cliente.Sitios,s=>s.Nombre=="Fosa Sur");
        var opciones=(await nav.GetFromJsonAsync<OpcionesRuta>($"/api/rutas/opciones?sucursalId={Suc}"))!;
        Assert.Single(opciones.PuntosServicio.Single(s=>s.Id==fosa.SitioId).Clientes!,c=>c.ClienteId==id);
        // Una ruta de fosas: solo órdenes (fosas planeadas), nunca baños.
        var ruta=await CrearRutaAsync(nav,await NuevaUnidadAsync(clasificacion:"fosas"));
        await AgregarParadaAsync(nav,ruta.Id,new {sitioId=fosa.SitioId,clientes=new[]{new {clienteId=id,banos=0,fosas=1}}});
        var ajena=await EnviarAsync(nav,HttpMethod.Post,$"/api/rutas/{ruta.Id}/paradas",new {sitioId=fosa.SitioId,clientes=new[]{new {clienteId=ClienteAlfa,fosas=1}}});
        Assert.Equal(HttpStatusCode.BadRequest,ajena.StatusCode);

        var cambio=fosa with { Calle="Calle de Servicio Corregida",Fosas=3 };
        var guardar=await EnviarAsync(nav,HttpMethod.Put,$"/api/clientes/{id}",new {autorizacion="AMB-FICT-MULTI",telefono="5550000099",correo="fixture@example.invalid",nombreComercial=cliente.NombreComercial,calle=cliente.Calle,revision=cliente.Revision,sitios=new[]{cambio}});
        Assert.Equal(HttpStatusCode.OK,guardar.StatusCode);
        var vieja=await EnviarAsync(nav,HttpMethod.Put,$"/api/clientes/{id}",new {autorizacion="AMB-FICT-MULTI",telefono="5550000099",correo="fixture@example.invalid",nombreComercial=cliente.NombreComercial,calle=cliente.Calle,revision=cliente.Revision,sitios=new[]{fosa}});
        Assert.Equal(HttpStatusCode.Conflict,vieja.StatusCode);
        var actual=Assert.Single((await nav.GetFromJsonAsync<List<Manifiestos.Api.Clientes.ClienteResumen>>("/api/clientes"))!,c=>c.Id==id);
        Assert.Equal(2,actual.Sitios!.Count); // Omitir la otra dirección no la borra.
        Assert.Equal(3,actual.Sitios.Single(s=>s.SitioId==fosa.SitioId).Fosas);
        Assert.Equal("2",await _bd.ConsultarAsync($"SELECT fosas_registradas FROM negocio.cliente_sitio WHERE cliente_id='{id}' AND sitio_id='{fosa.SitioId}' AND revision=1"));
        Assert.Single(await ParadasAsync(nav,ruta.Id));
        object Baja(Manifiestos.Api.Clientes.SitioCliente s)=>new {s.SitioId,s.Revision,s.RevisionFicha,eliminar=true};
        var usada=actual.Sitios.Single(s=>s.SitioId==fosa.SitioId);
        var noBorrar=await EnviarAsync(nav,HttpMethod.Put,$"/api/clientes/{id}",new {autorizacion="AMB-FICT-MULTI",telefono="5550000099",correo="fixture@example.invalid",nombreComercial="No debe guardarse",revision=actual.Revision,sitios=new[]{Baja(usada)}});
        Assert.Equal(HttpStatusCode.BadRequest,noBorrar.StatusCode);
        var despues=Assert.Single((await nav.GetFromJsonAsync<List<Manifiestos.Api.Clientes.ClienteResumen>>("/api/clientes"))!,c=>c.Id==id);
        Assert.Equal(actual.NombreComercial,despues.NombreComercial); // Todo el envío se revierte.
        var libre=actual.Sitios.Single(s=>s.SitioId!=fosa.SitioId);
        var borrar=await EnviarAsync(nav,HttpMethod.Put,$"/api/clientes/{id}",new {autorizacion="AMB-FICT-MULTI",telefono="5550000099",correo="fixture@example.invalid",nombreComercial=actual.NombreComercial,calle=actual.Calle,revision=actual.Revision,sitios=new[]{Baja(libre)}});
        Assert.Equal(HttpStatusCode.OK,borrar.StatusCode);
        despues=Assert.Single((await nav.GetFromJsonAsync<List<Manifiestos.Api.Clientes.ClienteResumen>>("/api/clientes"))!,c=>c.Id==id);
        Assert.Equal(fosa.SitioId,Assert.Single(despues.Sitios!).SitioId);
        Assert.Equal("false",await _bd.ConsultarAsync($"SELECT activo::text FROM negocio.cliente_sitio WHERE cliente_id='{id}' AND sitio_id='{libre.SitioId}' ORDER BY revision DESC LIMIT 1"));
        Assert.Equal("1",await _bd.ConsultarAsync($"SELECT count(*) FROM negocio.cliente_sitio WHERE cliente_id='{id}' AND sitio_id='{libre.SitioId}' AND revision=1 AND activo"));
        var reutilizar=await EnviarAsync(nav,HttpMethod.Post,$"/api/rutas/{ruta.Id}/paradas",new {sitioId=libre.SitioId,clientes=new[]{new {clienteId=id,fosas=1}}});
        // 12-nov-2026 (0025): al desactivarse su dirección el sitio vuelve a ser normal y un autorizado puede ir en él.
        Assert.Equal(HttpStatusCode.Created,reutilizar.StatusCode);
    }

    [Fact]
    public async Task Una_ruta_es_solo_de_sanitarios_o_solo_de_fosas_y_en_sanitarios_solo_clientes_generales()
    {
        using var nav = await SesionAsync(escribir: true);
        var general = await NuevoClienteAsync();
        var autorizado = await NuevoClienteAsync(autorizado: true);
        var sitioMixto = await NuevoSitioAsync([]); // sitio normal: va cualquier cliente general
        var sanitarios = await CrearRutaAsync(nav, await NuevaUnidadAsync());
        var fosas = await CrearRutaAsync(nav, await NuevaUnidadAsync(clasificacion: "fosas"));
        var sitioFosa = await NuevoSitioAsync([autorizado], banos: 0, fosas: 1);
        var antesSan = await HuellaAsync(sanitarios.Id);
        var antesFos = await HuellaAsync(fosas.Id);

        await AfirmarCodigoAsync(await EnviarAsync(nav, HttpMethod.Post, $"/api/rutas/{sanitarios.Id}/paradas", ParadaFosas(sitioMixto, (general, 1))), HttpStatusCode.UnprocessableEntity, "fosas_en_sanitarios");
        await AfirmarCodigoAsync(await EnviarAsync(nav, HttpMethod.Post, $"/api/rutas/{sanitarios.Id}/paradas", Parada(sitioMixto, (autorizado, 4))), HttpStatusCode.UnprocessableEntity, "cliente_autorizado_en_sanitarios");
        await AfirmarCodigoAsync(await EnviarAsync(nav, HttpMethod.Post, $"/api/rutas/{fosas.Id}/paradas", Parada(sitioMixto, (general, 4))), HttpStatusCode.UnprocessableEntity, "banos_en_fosas");
        Assert.Equal((antesSan, antesFos), (await HuellaAsync(sanitarios.Id), await HuellaAsync(fosas.Id)));

        // Lo permitido: banos de un cliente general en sanitarios; órdenes (fosas planeadas) de cualquiera en fosas.
        await AgregarParadaAsync(nav, sanitarios.Id, Parada(sitioMixto, (general, 4)));
        var pf = await AgregarParadaAsync(nav, fosas.Id, ParadaFosas(sitioFosa, (autorizado, 2)));
        // La edición completa de la parada (contrato anterior) valida el servicio igual.
        await AfirmarCodigoAsync(await EnviarAsync(nav, HttpMethod.Put, $"/api/rutas/{fosas.Id}/paradas/{pf.Id}",
            new { sitioId = sitioFosa, revision = 1, clientes = new[] { new { clienteId = autorizado, banos = 3 } } }), HttpStatusCode.UnprocessableEntity, "banos_en_fosas");
    }

    [Fact]
    public async Task Agrega_paradas_con_punto_existente_y_nuevo_y_las_detalla()
    {
        var nav = await SesionAsync(escribir: true);
        var ruta = await CrearRutaAsync(nav, await NuevaUnidadAsync(Operador));

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
        Assert.Equal((19, "sanitarios", 0), (primera.Banos, primera.Tipo, primera.Ordenes));
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
        var ruta = await CrearRutaAsync(nav, await NuevaUnidadAsync());
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
        var ruta = await CrearRutaAsync(nav, await NuevaUnidadAsync());
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
    public async Task Un_sitio_no_puede_tener_dos_paradas_vigentes_en_la_misma_ruta()
    {
        var nav = await SesionAsync(escribir: true);
        var ruta = await CrearRutaAsync(nav, await NuevaUnidadAsync());
        var a = await AgregarParadaAsync(nav, ruta.Id, Parada(Obra, (ClienteAlfa, 1)));
        var b = await AgregarParadaAsync(nav, ruta.Id, Parada(Obra2, (ClienteAlfa, 1)));
        var antes = await HuellaAsync(ruta.Id);

        var duplicada = await AfirmarCodigoAsync(await EnviarAsync(nav, HttpMethod.Post, $"/api/rutas/{ruta.Id}/paradas", Parada(Obra, (ClienteBeta, 2))), HttpStatusCode.Conflict, "sitio_ya_en_ruta");
        Assert.Equal(ruta.Id, duplicada.RutaActual!.Id);
        // Corregir una parada para llevarla a un sitio que otra ya ocupa tampoco se permite.
        await AfirmarCodigoAsync(await EnviarAsync(nav, HttpMethod.Put, $"/api/rutas/{ruta.Id}/paradas/{b.Id}",
            new { sitioId = Obra, revision = 1, clientes = new[] { new { clienteId = ClienteAlfa, banos = 1 } } }), HttpStatusCode.Conflict, "sitio_ya_en_ruta");
        Assert.Equal(antes, await HuellaAsync(ruta.Id));
        Assert.Equal(a.Id, (await ParadasAsync(nav, ruta.Id)).First().Id);
    }

    [Fact]
    public async Task Reordena_paradas_y_rechaza_una_lista_distinta()
    {
        var nav = await SesionAsync(escribir: true);
        var ruta = await CrearRutaAsync(nav, await NuevaUnidadAsync());
        var a = await AgregarParadaAsync(nav, ruta.Id, Parada(Obra, (ClienteAlfa, 1)));
        var b = await AgregarParadaAsync(nav, ruta.Id, Parada(Obra2, (ClienteBeta, 2)));
        var c = await AgregarParadaAsync(nav, ruta.Id, Parada(Obra3, (ClienteAlfa, 3)));
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

    // ---------------- Parada = sitio; clientes uno por uno ----------------

    [Fact]
    public async Task Agrega_un_sitio_como_parada_sin_clientes_y_despues_sus_clientes_generales_uno_por_uno()
    {
        var nav = await SesionAsync(escribir: true);
        var uno = await NuevoClienteAsync();
        var dos = await NuevoClienteAsync();
        var sitio = await NuevoSitioAsync([uno, dos]);
        var ruta = await CrearRutaAsync(nav, await NuevaUnidadAsync());

        var parada = await AgregarSitioAsync(nav, ruta.Id, sitio);
        Assert.Equal(1, parada.Revision);
        var vacia = Assert.Single(await ParadasAsync(nav, ruta.Id));
        Assert.Equal((parada.Id, 1, "sanitarios", 0), (vacia.Id, vacia.Revision, vacia.Tipo, vacia.Clientes.Count));
        Assert.Equal((1, 0, 0), ((await ResumenAsync(nav, ruta.Id)).Paradas, (await ResumenAsync(nav, ruta.Id)).Clientes, (await ResumenAsync(nav, ruta.Id)).Banos));

        // Cada cliente crea una revisión nueva con el conjunto completo.
        var r1 = await AgregarClienteAsync(nav, ruta.Id, parada.Id, uno, revision: 1, banos: 12);
        Assert.Equal(HttpStatusCode.Created, r1.StatusCode);
        Assert.Equal(new RevisionGuardada(parada.Id, 2), await r1.Content.ReadFromJsonAsync<RevisionGuardada>());
        var r2 = await AgregarClienteAsync(nav, ruta.Id, parada.Id, dos, revision: 2, banos: 11);
        Assert.Equal(3, (await r2.Content.ReadFromJsonAsync<RevisionGuardada>())!.Revision);
        var p = Assert.Single(await ParadasAsync(nav, ruta.Id));
        Assert.Equal((3, 23, 2), (p.Revision, p.Banos, p.Clientes.Count));
        Assert.All(p.Clientes, c => Assert.False(c.Autorizado));
        Assert.Equal((1, 2, 23), ((await ResumenAsync(nav, ruta.Id)).Paradas, (await ResumenAsync(nav, ruta.Id)).Clientes, (await ResumenAsync(nav, ruta.Id)).Banos));
        // La historia se conserva: las revisiones 1 y 2 siguen como estaban.
        Assert.Equal("0|1|2", await _bd.ConsultarAsync($"""
            SELECT (SELECT count(*) FROM negocio.parada_cliente c JOIN negocio.parada_version v ON v.id = c.version_id WHERE v.parada_id = '{parada.Id}' AND v.revision = 1) || '|' ||
                   (SELECT count(*) FROM negocio.parada_cliente c JOIN negocio.parada_version v ON v.id = c.version_id WHERE v.parada_id = '{parada.Id}' AND v.revision = 2) || '|' ||
                   (SELECT count(*) FROM negocio.parada_cliente c JOIN negocio.parada_version v ON v.id = c.version_id WHERE v.parada_id = '{parada.Id}' AND v.revision = 3)
            """.ReplaceLineEndings(" ")));

        // Editar los baños de un cliente (el lápiz de sanitarios): revisión nueva; el mismo valor no crea otra.
        var url = $"/api/rutas/{ruta.Id}/paradas/{parada.Id}/clientes/{uno}";
        var edita = await EnviarAsync(nav, HttpMethod.Put, url, new { banos = 15, revision = 3 });
        Assert.Equal(new RevisionGuardada(parada.Id, 4), await edita.Content.ReadFromJsonAsync<RevisionGuardada>());
        var igual = await EnviarAsync(nav, HttpMethod.Put, url, new { banos = 15, revision = 4 });
        Assert.Equal(4, (await igual.Content.ReadFromJsonAsync<RevisionGuardada>())!.Revision);
        Assert.Equal(26, Assert.Single(await ParadasAsync(nav, ruta.Id)).Banos);
        await AfirmarCodigoAsync(await EnviarAsync(nav, HttpMethod.Put, url, new { banos = 20, revision = 3 }), HttpStatusCode.Conflict, "revision_desactualizada");
        await AfirmarCodigoAsync(await EnviarAsync(nav, HttpMethod.Put, url, new { banos = 0, revision = 4 }), HttpStatusCode.UnprocessableEntity, "banos_invalidos");
        await AfirmarCodigoAsync(await EnviarAsync(nav, HttpMethod.Put, url, new { ordenes = 2, revision = 4 }), HttpStatusCode.UnprocessableEntity, "ordenes_en_sanitarios");

        // Quitar clientes: una revisión nueva sin ese cliente; en sanitarios la parada puede quedar sin clientes.
        var quita = await EnviarAsync(nav, HttpMethod.Delete, $"{url}?revision=4", null);
        Assert.Equal(new RevisionGuardada(parada.Id, 5), await quita.Content.ReadFromJsonAsync<RevisionGuardada>());
        await AfirmarCodigoAsync(await EnviarAsync(nav, HttpMethod.Delete, $"/api/rutas/{ruta.Id}/paradas/{parada.Id}/clientes/{dos}?revision=4", null), HttpStatusCode.Conflict, "revision_desactualizada");
        var quitaOtro = await EnviarAsync(nav, HttpMethod.Delete, $"/api/rutas/{ruta.Id}/paradas/{parada.Id}/clientes/{dos}?revision=5", null);
        Assert.Equal(6, (await quitaOtro.Content.ReadFromJsonAsync<RevisionGuardada>())!.Revision);
        Assert.Empty(Assert.Single(await ParadasAsync(nav, ruta.Id)).Clientes);
        Assert.Equal(HttpStatusCode.NotFound, (await EnviarAsync(nav, HttpMethod.Delete, $"{url}?revision=6", null)).StatusCode);
    }

    [Fact]
    public async Task Al_agregar_clientes_exige_cliente_general_y_baños_de_sanitarios()
    {
        var nav = await SesionAsync(escribir: true);
        var general = await NuevoClienteAsync();
        var autorizado = await NuevoClienteAsync(autorizado: true);
        var otroGeneral = await NuevoClienteAsync();
        var sitio = await NuevoSitioAsync([]);
        var ruta = await CrearRutaAsync(nav, await NuevaUnidadAsync());
        var parada = await AgregarSitioAsync(nav, ruta.Id, sitio);
        var antes = await HuellaAsync(ruta.Id);

        await AfirmarCodigoAsync(await AgregarClienteAsync(nav, ruta.Id, parada.Id, autorizado, 1, banos: 4), HttpStatusCode.UnprocessableEntity, "cliente_autorizado_en_sanitarios");
        await AfirmarCodigoAsync(await AgregarClienteAsync(nav, ruta.Id, parada.Id, general, 1, banos: 0), HttpStatusCode.UnprocessableEntity, "banos_invalidos");
        await AfirmarCodigoAsync(await AgregarClienteAsync(nav, ruta.Id, parada.Id, general, 1), HttpStatusCode.UnprocessableEntity, "banos_invalidos");
        await AfirmarCodigoAsync(await AgregarClienteAsync(nav, ruta.Id, parada.Id, general, 1, ordenes: 2), HttpStatusCode.UnprocessableEntity, "ordenes_en_sanitarios");
        await AfirmarCodigoAsync(await AgregarClienteAsync(nav, ruta.Id, parada.Id, general, 9, banos: 4), HttpStatusCode.Conflict, "revision_desactualizada");
        await AfirmarErrorAsync(await AgregarClienteAsync(nav, ruta.Id, parada.Id, Guid.NewGuid(), 1, banos: 4), HttpStatusCode.BadRequest, RepositorioRutas.MensajeClienteInvalido);
        Assert.Equal(HttpStatusCode.BadRequest, (await AgregarClienteAsync(nav, ruta.Id, parada.Id, general, 1, banos: EndpointsRutas.MaximoBanos + 1)).StatusCode);
        Assert.Equal(antes, await HuellaAsync(ruta.Id));

        Assert.Equal(HttpStatusCode.Created, (await AgregarClienteAsync(nav, ruta.Id, parada.Id, general, 1, banos: 4)).StatusCode);
        await AfirmarCodigoAsync(await AgregarClienteAsync(nav, ruta.Id, parada.Id, general, 2, banos: 9), HttpStatusCode.Conflict, "cliente_ya_en_parada");
        // Los generales ya no se ligan a sitios: cualquier cliente general va en la parada de un sitio normal.
        Assert.Equal(HttpStatusCode.Created, (await AgregarClienteAsync(nav, ruta.Id, parada.Id, otroGeneral, 2, banos: 3)).StatusCode);
    }

    [Fact]
    public async Task El_mismo_sitio_y_cliente_pueden_estar_en_dos_rutas_a_la_vez_y_reasignar_solo_mueve_si_se_pide()
    {
        var nav = await SesionAsync(escribir: true);
        var cliente = await NuevoClienteAsync();
        var sitio = await NuevoSitioAsync([cliente]);
        var otroSitio = await NuevoSitioAsync([cliente]);
        var origen = await CrearRutaAsync(nav, await NuevaUnidadAsync());
        var destino = await CrearRutaAsync(nav, await NuevaUnidadAsync());
        var paradaOrigen = await AgregarSitioAsync(nav, origen.Id, sitio);
        await AgregarClienteAsync(nav, origen.Id, paradaOrigen.Id, cliente, 1, banos: 7);

        // Reagendas o emergencias: por omisión el sitio se agrega en la otra ruta sin tocar la primera (sin 409).
        var paralela = await AgregarSitioAsync(nav, destino.Id, sitio);
        Assert.Single(await ParadasAsync(nav, origen.Id));
        Assert.Single(await ParadasAsync(nav, destino.Id));
        Assert.Equal("0", await _bd.ConsultarAsync($"SELECT count(*) FROM negocio.parada_baja WHERE parada_id = '{paradaOrigen.Id}'"));
        // El mismo cliente también va en las dos rutas.
        Assert.Equal(HttpStatusCode.Created, (await AgregarClienteAsync(nav, destino.Id, paralela.Id, cliente, 1, banos: 4)).StatusCode);
        Assert.Equal(7, Assert.Single(await ParadasAsync(nav, origen.Id)).Banos);
        Assert.Equal(4, Assert.Single(await ParadasAsync(nav, destino.Id)).Banos);
        // Sigue prohibido el mismo sitio dos veces en la MISMA ruta.
        await AfirmarCodigoAsync(await EnviarAsync(nav, HttpMethod.Post, $"/api/rutas/{destino.Id}/paradas", new { sitioId = sitio }), HttpStatusCode.Conflict, "sitio_ya_en_ruta");

        // Mover explícito (reasignar=true): da de baja la parada de la otra ruta y crea la nueva en una sola operación.
        var tercera = await CrearRutaAsync(nav, await NuevaUnidadAsync());
        await AgregarSitioAsync(nav, tercera.Id, sitio, reasignar: true);
        Assert.Empty(await ParadasAsync(nav, origen.Id));
        Assert.Empty(await ParadasAsync(nav, destino.Id));
        Assert.Single(await ParadasAsync(nav, tercera.Id));
        Assert.Equal("1", await _bd.ConsultarAsync($"SELECT count(*) FROM negocio.parada_baja WHERE parada_id = '{paradaOrigen.Id}'"));

        // Cliente: reasignar=true lo quita de las paradas de la otra ruta (nueva revisión) y lo pone en la nueva.
        var enTercera = Assert.Single(await ParadasAsync(nav, tercera.Id));
        Assert.Equal(HttpStatusCode.Created, (await AgregarClienteAsync(nav, tercera.Id, enTercera.Id, cliente, 1, banos: 7)).StatusCode);
        var paradaOtro = await AgregarSitioAsync(nav, origen.Id, otroSitio);
        Assert.Equal(HttpStatusCode.Created, (await AgregarClienteAsync(nav, origen.Id, paradaOtro.Id, cliente, 1, banos: 3, reasignar: true)).StatusCode);
        Assert.Empty(Assert.Single(await ParadasAsync(nav, tercera.Id)).Clientes);
        Assert.Equal(3, Assert.Single(await ParadasAsync(nav, origen.Id)).Banos);
        // La parada de la que salió el cliente conserva su historia: revisión 1 vacía, 2 con el cliente y 3 otra vez vacía.
        Assert.Equal("3", await _bd.ConsultarAsync($"SELECT max(revision) FROM negocio.parada_version WHERE parada_id = '{enTercera.Id}'"));
    }

    [Fact]
    public async Task Fosas_la_parada_es_el_sitio_y_se_edita_en_ordenes_no_en_banos()
    {
        var nav = await SesionAsync(escribir: true);
        // Generales: siguen el proceso de la fosa fija (cada orden es una vuelta con la unidad llena) y van en cualquier sitio normal.
        var cliente = await NuevoClienteAsync();
        var otro = await NuevoClienteAsync();
        var sitio = await NuevoSitioAsync([]);
        var ruta = await CrearRutaAsync(nav, await NuevaUnidadAsync(clasificacion: "fosas"));

        // La parada es el sitio: se crea sin clientes (también en fosas) y los clientes se agregan después.
        var parada = await AgregarSitioAsync(nav, ruta.Id, sitio);
        Assert.Equal(HttpStatusCode.Created, (await AgregarClienteAsync(nav, ruta.Id, parada.Id, cliente, 1, ordenes: 2)).StatusCode);
        var p = Assert.Single(await ParadasAsync(nav, ruta.Id));
        Assert.Equal(("fosa_propia", 2, 0), (p.Tipo, p.Ordenes, p.Banos));   // la fosa de un cliente general es PROPIA
        Assert.Equal((2, false), (p.Clientes[0].Ordenes, p.Clientes[0].Autorizado));

        var url = $"/api/rutas/{ruta.Id}/paradas/{parada.Id}/clientes";
        await AfirmarCodigoAsync(await AgregarClienteAsync(nav, ruta.Id, parada.Id, otro, 2, banos: 3), HttpStatusCode.UnprocessableEntity, "banos_en_fosas");
        await AfirmarCodigoAsync(await AgregarClienteAsync(nav, ruta.Id, parada.Id, otro, 2, ordenes: 0), HttpStatusCode.UnprocessableEntity, "ordenes_invalidas");
        // En fosas generales puede haber varios clientes en la misma parada.
        Assert.Equal(HttpStatusCode.Created, (await AgregarClienteAsync(nav, ruta.Id, parada.Id, otro, 2, ordenes: 3)).StatusCode);
        // El lápiz de la parada de fosas edita las órdenes del cliente.
        var edita = await EnviarAsync(nav, HttpMethod.Put, $"{url}/{cliente}", new { ordenes = 4, revision = 3 });
        Assert.Equal(4, (await edita.Content.ReadFromJsonAsync<RevisionGuardada>())!.Revision);
        await AfirmarCodigoAsync(await EnviarAsync(nav, HttpMethod.Put, $"{url}/{cliente}", new { banos = 4, revision = 4 }), HttpStatusCode.UnprocessableEntity, "banos_en_fosas");
        var vigente = Assert.Single(await ParadasAsync(nav, ruta.Id));
        Assert.Equal((7, 0), (vigente.Ordenes, vigente.Banos));
        Assert.Equal(7, (await ResumenAsync(nav, ruta.Id)).Ordenes);

        // La parada puede quedar sin clientes: es el sitio, y se sigue armando la ruta.
        Assert.Equal(HttpStatusCode.OK, (await EnviarAsync(nav, HttpMethod.Delete, $"{url}/{otro}?revision=4", null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await EnviarAsync(nav, HttpMethod.Delete, $"{url}/{cliente}?revision=5", null)).StatusCode);
        Assert.Empty(Assert.Single(await ParadasAsync(nav, ruta.Id)).Clientes);
    }

    [Fact]
    public async Task Autorizado_va_en_su_direccion_o_en_un_sitio_normal_el_general_solo_en_normal_y_una_parada_con_autorizado_no_admite_otros()
    {
        var nav = await SesionAsync(escribir: true);
        var dueno = await NuevoClienteAsync(autorizado: true);
        var otroAutorizado = await NuevoClienteAsync(autorizado: true);
        var general = await NuevoClienteAsync();
        var sitioFija = await NuevoSitioAsync([dueno, otroAutorizado], banos: null, fosas: 1);
        var sitioNormal = await NuevoSitioAsync([]);
        var ruta = await CrearRutaAsync(nav, await NuevaUnidadAsync(clasificacion: "fosas"));
        var pFija = await AgregarSitioAsync(nav, ruta.Id, sitioFija);
        var pNormal = await AgregarSitioAsync(nav, ruta.Id, sitioNormal);

        // La dirección de un cliente autorizado es suya: un general no va ahí ni otro autorizado que no sea dueño.
        await AfirmarCodigoAsync(await AgregarClienteAsync(nav, ruta.Id, pFija.Id, general, 1, ordenes: 1), HttpStatusCode.UnprocessableEntity, "cliente_no_va_en_sitio");
        var sitioAjeno = await NuevoSitioAsync([otroAutorizado], banos: null, fosas: 1);
        var pAjena = await AgregarSitioAsync(nav, ruta.Id, sitioAjeno);
        await AfirmarCodigoAsync(await AgregarClienteAsync(nav, ruta.Id, pAjena.Id, dueno, 1, ordenes: 1), HttpStatusCode.UnprocessableEntity, "cliente_no_va_en_sitio");
        // Un autorizado SÍ puede ir en un sitio normal (emergencia o reagenda); entonces la parada ya no admite a nadie más.
        Assert.Equal(HttpStatusCode.Created, (await AgregarClienteAsync(nav, ruta.Id, pNormal.Id, dueno, 1, ordenes: 1)).StatusCode);
        await AfirmarCodigoAsync(await AgregarClienteAsync(nav, ruta.Id, pNormal.Id, general, 2, ordenes: 1), HttpStatusCode.UnprocessableEntity, "fosa_fija_un_cliente");
        await AfirmarCodigoAsync(await AgregarClienteAsync(nav, ruta.Id, pNormal.Id, otroAutorizado, 2, ordenes: 1), HttpStatusCode.UnprocessableEntity, "fosa_fija_un_cliente");
        // Un general sigue yendo en un sitio normal.
        var pNormal2 = await AgregarSitioAsync(nav, ruta.Id, await NuevoSitioAsync([]));
        Assert.Equal(HttpStatusCode.Created, (await AgregarClienteAsync(nav, ruta.Id, pNormal2.Id, general, 1, ordenes: 1)).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await AgregarClienteAsync(nav, ruta.Id, pFija.Id, dueno, 1, ordenes: 2)).StatusCode);
        // Fosa fija: una sola fosa, un solo cliente por parada.
        await AfirmarCodigoAsync(await AgregarClienteAsync(nav, ruta.Id, pFija.Id, otroAutorizado, 2, ordenes: 1), HttpStatusCode.UnprocessableEntity, "fosa_fija_un_cliente");
        Assert.Equal("fosa_fija", Assert.Single(await ParadasAsync(nav, ruta.Id), x => x.Id == pFija.Id).Tipo);

        // Los sitios de autorizados no van en rutas de sanitarios.
        var sanitarios = await CrearRutaAsync(nav, await NuevaUnidadAsync());
        await AfirmarCodigoAsync(await EnviarAsync(nav, HttpMethod.Post, $"/api/rutas/{sanitarios.Id}/paradas", new { sitioId = sitioFija }), HttpStatusCode.UnprocessableEntity, "sitio_de_cliente_autorizado");
    }
    // ---------------- Baja de paradas ----------------

    [Fact]
    public async Task Eliminar_una_parada_la_da_de_baja_sin_borrar_historia_y_libera_al_cliente_y_al_sitio()
    {
        var nav = await SesionAsync(escribir: true);
        var cliente = await NuevoClienteAsync();
        var sitio = await NuevoSitioAsync([cliente]);
        var otro = await NuevoSitioAsync([cliente]);
        var ruta = await CrearRutaAsync(nav, await NuevaUnidadAsync());
        var parada = await AgregarParadaAsync(nav, ruta.Id, Parada(sitio, (cliente, 6)));
        var segunda = await AgregarParadaAsync(nav, ruta.Id, Parada(otro, (cliente, 3)));

        await AfirmarCodigoAsync(await EnviarAsync(nav, HttpMethod.Delete, $"/api/rutas/{ruta.Id}/paradas/{parada.Id}?revision=9", null), HttpStatusCode.Conflict, "revision_desactualizada");
        Assert.Equal(HttpStatusCode.NotFound, (await EnviarAsync(nav, HttpMethod.Delete, $"/api/rutas/{ruta.Id}/paradas/{Guid.NewGuid()}", null)).StatusCode);
        var baja = await EnviarAsync(nav, HttpMethod.Delete, $"/api/rutas/{ruta.Id}/paradas/{parada.Id}?revision=1", null);
        Assert.Equal(HttpStatusCode.NoContent, baja.StatusCode);

        Assert.Equal([segunda.Id], (await ParadasAsync(nav, ruta.Id)).Select(p => p.Id));
        var resumen = await ResumenAsync(nav, ruta.Id);
        Assert.Equal((1, 1, 3), (resumen.Paradas, resumen.Clientes, resumen.Banos));
        // La parada y su historia se conservan; solo se registra la baja con quién y cuándo.
        Assert.Equal("1|1|true", await _bd.ConsultarAsync($"""
            SELECT (SELECT count(*) FROM negocio.parada WHERE id = '{parada.Id}') || '|' ||
                   (SELECT count(*) FROM negocio.parada_version WHERE parada_id = '{parada.Id}') || '|' ||
                   (SELECT acceso_id IS NOT NULL FROM negocio.parada_baja WHERE parada_id = '{parada.Id}')
            """.ReplaceLineEndings(" ")));
        // Ya dada de baja no se vuelve a eliminar ni se edita.
        Assert.Equal(HttpStatusCode.NotFound, (await EnviarAsync(nav, HttpMethod.Delete, $"/api/rutas/{ruta.Id}/paradas/{parada.Id}", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await AgregarClienteAsync(nav, ruta.Id, parada.Id, cliente, 1, banos: 2)).StatusCode);

        // El cliente y el sitio quedan sin ruta: Clientes ya no lo marca en uso y Órdenes no ve la parada.
        var sitios = (await nav.GetFromJsonAsync<List<Manifiestos.Api.Sitios.SitioResumen>>("/api/sitios"))!;
        Assert.Empty(sitios.Single(s => s.Id == sitio).Rutas);
        var ordenes = Assert.Single((await nav.GetFromJsonAsync<List<RutaDeOrdenes>>("/api/ordenes"))!, r => r.Id == ruta.Id);
        Assert.Equal([segunda.Id], ordenes.Paradas.Select(p => p.Id));
        // El sitio puede volver a agregarse en la misma ruta.
        await AgregarParadaAsync(nav, ruta.Id, Parada(sitio, (cliente, 5)));

        // El orden visual ignora las paradas dadas de baja y las conserva al final sin chocar con las posiciones vigentes.
        var vigentes = (await ParadasAsync(nav, ruta.Id)).Select(p => p.Id).Reverse().ToArray();
        Assert.Equal(HttpStatusCode.NoContent, (await EnviarAsync(nav, HttpMethod.Put, $"/api/rutas/{ruta.Id}/orden", new { paradas = vigentes })).StatusCode);
        Assert.Equal(vigentes, (await ParadasAsync(nav, ruta.Id)).Select(p => p.Id));
        await AfirmarErrorAsync(await EnviarAsync(nav, HttpMethod.Put, $"/api/rutas/{ruta.Id}/orden", new { paradas = vigentes.Append(parada.Id).ToArray() }),
            HttpStatusCode.Conflict, RepositorioRutas.MensajeParadasCambiaron);
    }

    [Fact]
    public async Task No_se_elimina_una_parada_mientras_la_ruta_tiene_una_jornada_publicada()
    {
        var nav = await SesionAsync(escribir: true);
        var cliente = await NuevoClienteAsync();
        var sitio = await NuevoSitioAsync([cliente]);
        var ruta = await CrearRutaAsync(nav, await NuevaUnidadAsync(Operador));
        var parada = await AgregarParadaAsync(nav, ruta.Id, Parada(sitio, (cliente, 6)));
        var jornada = Guid.NewGuid();
        await _bd.SqlComoDuenoAsync($"""
            INSERT INTO negocio.jornada(organizacion_id, sucursal_id, id, ruta_id, fecha) VALUES ('{Org}', '{Suc}', '{jornada}', '{ruta.Id}', (now() AT TIME ZONE 'UTC')::date + 1);
            INSERT INTO negocio.estado_jornada(organizacion_id, sucursal_id, jornada_id, revision, estado) VALUES ('{Org}', '{Suc}', '{jornada}', 1, 'programada');
            """);
        var antes = await HuellaAsync(ruta.Id);
        await AfirmarCodigoAsync(await EnviarAsync(nav, HttpMethod.Delete, $"/api/rutas/{ruta.Id}/paradas/{parada.Id}", null), HttpStatusCode.Conflict, "ruta_vigente");
        Assert.Equal(antes, await HuellaAsync(ruta.Id));
        // Retirada la jornada (cancelada), la parada ya puede eliminarse.
        await _bd.SqlComoDuenoAsync($"INSERT INTO negocio.estado_jornada(organizacion_id, sucursal_id, jornada_id, revision, estado) VALUES ('{Org}', '{Suc}', '{jornada}', 2, 'cancelada');");
        Assert.Equal(HttpStatusCode.NoContent, (await EnviarAsync(nav, HttpMethod.Delete, $"/api/rutas/{ruta.Id}/paradas/{parada.Id}", null)).StatusCode);
    }

    [Fact]
    public async Task No_se_reasigna_un_sitio_ni_un_cliente_desde_una_ruta_con_jornada_publicada()
    {
        var nav = await SesionAsync(escribir: true);
        var cliente = await NuevoClienteAsync();
        var sitio = await NuevoSitioAsync([cliente]);
        var origen = await CrearRutaAsync(nav, await NuevaUnidadAsync());
        var destino = await CrearRutaAsync(nav, await NuevaUnidadAsync());
        var paradaOrigen = await AgregarSitioAsync(nav, origen.Id, sitio);
        var jornada = Guid.NewGuid();
        await _bd.SqlComoDuenoAsync($"""
            INSERT INTO negocio.jornada(organizacion_id, sucursal_id, id, ruta_id, fecha) VALUES ('{Org}', '{Suc}', '{jornada}', '{origen.Id}', (now() AT TIME ZONE 'UTC')::date + 1);
            INSERT INTO negocio.estado_jornada(organizacion_id, sucursal_id, jornada_id, revision, estado) VALUES ('{Org}', '{Suc}', '{jornada}', 1, 'programada');
            """);
        var antes = await HuellaAsync(origen.Id);
        var rechazo = await AfirmarCodigoAsync(await EnviarAsync(nav, HttpMethod.Post, $"/api/rutas/{destino.Id}/paradas", new { sitioId = sitio, reasignar = true }),
            HttpStatusCode.Conflict, "ruta_vigente");
        Assert.Equal(origen.Id, rechazo.RutaActual!.Id);
        Assert.Equal(antes, await HuellaAsync(origen.Id));
        Assert.Equal("0", await _bd.ConsultarAsync($"SELECT count(*) FROM negocio.parada_baja WHERE parada_id = '{paradaOrigen.Id}'"));
        Assert.Empty(await ParadasAsync(nav, destino.Id));
    }

    // ---------------- Permisos, CSRF y no encontrados ----------------

    private static IEnumerable<(HttpMethod Metodo, string Url, object? Cuerpo)> Escrituras(Guid ruta, Guid parada, Guid unidad) =>
    [
        (HttpMethod.Post, "/api/rutas", Ruta(unidad, operador: Operador2)),
        (HttpMethod.Put, $"/api/rutas/{ruta}", new { dias = new[] { 7 }, unidadId = unidad, operadorId = Operador2, revision = 1 }),
        (HttpMethod.Post, $"/api/rutas/{ruta}/paradas", Parada(Obra, (ClienteBeta, 9))),
        (HttpMethod.Post, $"/api/rutas/{ruta}/paradas", new { nuevoSitio = new { nombre = "Sitio que no debe existir" }, clientes = new[] { new { clienteId = ClienteBeta, banos = 9 } } }),
        (HttpMethod.Post, $"/api/rutas/{ruta}/paradas", new { sitioId = Obra2, reasignar = false }),
        (HttpMethod.Put, $"/api/rutas/{ruta}/paradas/{parada}", new { sitioId = Obra, revision = 1, clientes = new[] { new { clienteId = ClienteBeta, banos = 9 } } }),
        (HttpMethod.Post, $"/api/rutas/{ruta}/paradas/{parada}/clientes", new { clienteId = ClienteBeta, banos = 9, reasignar = false, revision = 1 }),
        (HttpMethod.Put, $"/api/rutas/{ruta}/paradas/{parada}/clientes/{ClienteAlfa}", new { banos = 9, revision = 1 }),
        (HttpMethod.Delete, $"/api/rutas/{ruta}/paradas/{parada}/clientes/{ClienteAlfa}?revision=1", null),
        (HttpMethod.Delete, $"/api/rutas/{ruta}/paradas/{parada}?revision=1", null),
        (HttpMethod.Put, $"/api/rutas/{ruta}/orden", new { paradas = new[] { parada } }),
    ];

    private async Task<(Guid Ruta, Guid Parada, Guid Unidad)> RutaConParadaAsync()
    {
        var nav = await SesionAsync(escribir: true);
        var unidad = await NuevaUnidadAsync(Operador);
        var ruta = await CrearRutaAsync(nav, unidad);
        var p = await AgregarParadaAsync(nav, ruta.Id, Parada(Obra, (ClienteAlfa, 2)));
        return (ruta.Id, p.Id, unidad);
    }

    [Fact]
    public async Task Solo_lectura_recibe_403_en_cada_escritura_y_no_cambia_nada()
    {
        var (ruta, parada, unidad) = await RutaConParadaAsync();
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
        var (ruta, parada, unidad) = await RutaConParadaAsync();
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
        var (ruta, _, unidad) = await RutaConParadaAsync();
        var nav = await SesionAsync(escribir: true);
        var falsa = Guid.NewGuid();

        Assert.Equal(HttpStatusCode.NotFound, (await nav.GetAsync($"/api/rutas/{falsa}/paradas")).StatusCode);
        foreach (var (metodo, url, cuerpo) in Escrituras(falsa, Guid.NewGuid(), unidad).Skip(1))
            await AfirmarErrorAsync(await EnviarAsync(nav, metodo, url, cuerpo), HttpStatusCode.NotFound);
        // Parada que no existe dentro de una ruta que sí existe.
        await AfirmarErrorAsync(await EnviarAsync(nav, HttpMethod.Put, $"/api/rutas/{ruta}/paradas/{Guid.NewGuid()}",
            new { sitioId = Obra, revision = 1, clientes = new[] { new { clienteId = ClienteAlfa, banos = 1 } } }), HttpStatusCode.NotFound);
        Assert.Equal(HttpStatusCode.NotFound, (await AgregarClienteAsync(nav, ruta, Guid.NewGuid(), ClienteAlfa, 1, banos: 2)).StatusCode);
    }
}
