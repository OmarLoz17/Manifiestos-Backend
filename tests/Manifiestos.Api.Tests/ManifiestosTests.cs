using System.Net;
using System.Net.Http.Json;
using Manifiestos.Api.Documentos;
using Manifiestos.Api.Tests.Infraestructura;

namespace Manifiestos.Api.Tests;

/// <summary>
/// Manifiestos (0004) en solo lectura contra PostgreSQL real: última versión, participantes, renglones con la
/// cantidad estimada por unidad llena, descarga opcional y aislamiento por sucursal. Datos ficticios.
/// </summary>
[Trait("Categoria", "Integracion")]
public sealed class ManifiestosTests : IClassFixture<BaseDatosPrueba>, IAsyncLifetime
{
    private static readonly Guid Org = BaseDatosPrueba.Organizacion;
    private static readonly Guid Suc = BaseDatosPrueba.Sucursal;
    private const string Empresa = "0f000000-0000-4000-8000-000000000002";
    private static readonly Guid Suc2 = Guid.Parse("0f000000-0000-4000-8000-0000000000a2");

    private readonly BaseDatosPrueba _bd;
    private ApiPrueba _api = null!;

    public ManifiestosTests(BaseDatosPrueba bd) => _bd = bd;

    public async Task InitializeAsync()
    {
        _api = new ApiPrueba(_bd);
        await _bd.SqlComoDuenoAsync($"""
            INSERT INTO negocio.sucursal(organizacion_id, id, empresa_id, codigo, nombre)
            VALUES ('{Org}', '{Suc2}', '{Empresa}', 'PRUEBA2', 'Otra sucursal ficticia') ON CONFLICT DO NOTHING;
            """);
    }

    public async Task DisposeAsync() => await _api.DisposeAsync();

    private async Task<HttpClient> SesionAsync(bool escribir = false)
    {
        var correo = $"manifiestos-{Guid.NewGuid():N}@example.invalid";
        await _bd.AutorizarAsync(correo, escribir: escribir);
        var nav = _api.NuevoNavegador();
        Assert.Equal(HttpStatusCode.NoContent, (await nav.EntrarAsync(ValidadorFicticio.Token($"s-{correo}", correo))).StatusCode);
        return nav;
    }

    private record Sembrado(Guid Manifiesto, string Cliente, string Unidad, string? Folio);

    /// <summary>Un manifiesto completo (una sola transacción: los participantes y renglones lo exigen).</summary>
    private async Task<Sembrado> SembrarAsync(Guid sucursal, string fecha, bool conDescarga, string? folio, short capacidad = 2)
    {
        var (cliente, empCli, empTrans, unidad, fichaVeh, ptar, fichaPtar, descarga, manifiesto, version) =
            (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var nombreCliente = $"Cliente Ficticio {cliente:N}"[..28];
        var numero = $"MA-{unidad:N}"[..9];
        var folioSql = folio is null ? "NULL" : $"'{folio}'";
        var descargaSql = conDescarga ? $"'{descarga}'" : "NULL";
        await _bd.SqlComoDuenoAsync($"""
            BEGIN;
            INSERT INTO negocio.empresa VALUES ('{Org}', '{empCli}', 'CLI-{empCli:N}'), ('{Org}', '{empTrans}', 'TRA-{empTrans:N}');
            INSERT INTO negocio.ficha_empresa(organizacion_id, empresa_id, revision, nombre_fiscal, nombre_comercial, rfc) VALUES
                ('{Org}', '{empCli}', 1, 'Fiscal {nombreCliente} SA', '{nombreCliente}', 'RFC-FICTICIO'),
                ('{Org}', '{empTrans}', 1, 'Transportes Ficticios SA', NULL, NULL);
            INSERT INTO negocio.empresa_sucursal VALUES ('{Org}', '{sucursal}', '{empCli}'), ('{Org}', '{sucursal}', '{empTrans}');
            INSERT INTO negocio.autorizacion_empresa(organizacion_id, empresa_id, autoridad, finalidad, numero)
            VALUES ('{Org}', '{empCli}', 'Medio ambiente', 'Generador', 'FI-TEST-{cliente:N}');
            INSERT INTO negocio.cliente VALUES ('{Org}', '{sucursal}', '{cliente}', '{empCli}', 'C-{cliente:N}');
            INSERT INTO negocio.vehiculo VALUES ('{Org}', '{sucursal}', '{unidad}', '{numero}');
            INSERT INTO negocio.ficha_vehiculo(organizacion_id, sucursal_id, id, vehiculo_id, revision, capacidad_id)
            VALUES ('{Org}', '{sucursal}', '{fichaVeh}', '{unidad}', 1, {capacidad});
            INSERT INTO negocio.sitio VALUES ('{Org}', '{sucursal}', '{ptar}', 'PTAR ficticia');
            INSERT INTO negocio.ficha_sitio(organizacion_id, sucursal_id, id, sitio_id, revision, nombre)
            VALUES ('{Org}', '{sucursal}', '{fichaPtar}', '{ptar}', 1, 'PTAR Ficticia Norte');
            INSERT INTO negocio.descarga(organizacion_id, sucursal_id, id, cliente_id, ficha_vehiculo_id, ficha_sitio_ptar_id, ocurrida_en)
            SELECT '{Org}', '{sucursal}', '{descarga}', '{cliente}', '{fichaVeh}', '{fichaPtar}', '{fecha}'::timestamptz + interval '2 hours' WHERE {conDescarga};
            INSERT INTO negocio.manifiesto(organizacion_id, sucursal_id, id, cliente_id, descarga_id)
            VALUES ('{Org}', '{sucursal}', '{manifiesto}', '{cliente}', {descargaSql});
            INSERT INTO negocio.version_manifiesto(organizacion_id, sucursal_id, id, manifiesto_id, revision, numero_manifiesto, ficha_vehiculo_id, creada_en)
            VALUES ('{Org}', '{sucursal}', '{version}', '{manifiesto}', 1, {folioSql}, '{fichaVeh}', '{fecha}'::timestamptz);
            INSERT INTO negocio.participante_manifiesto(organizacion_id, sucursal_id, version_id, rol, empresa_id, ficha_empresa_id, autorizacion_id, origen_domicilio)
            SELECT '{Org}', '{sucursal}', '{version}', 'generador', '{empCli}',
                (SELECT id FROM negocio.ficha_empresa WHERE empresa_id = '{empCli}'),
                (SELECT id FROM negocio.autorizacion_empresa WHERE empresa_id = '{empCli}'), 'fiscal';
            INSERT INTO negocio.participante_manifiesto(organizacion_id, sucursal_id, version_id, rol, empresa_id, ficha_empresa_id, origen_domicilio)
            SELECT '{Org}', '{sucursal}', '{version}', 'transportista', '{empTrans}',
                (SELECT id FROM negocio.ficha_empresa WHERE empresa_id = '{empTrans}'), 'fiscal';
            INSERT INTO negocio.detalle_manifiesto(organizacion_id, sucursal_id, version_id, renglon, descripcion_residuo, tipo_contenedor,
                capacidad_contenedor, unidad_capacidad, numero_contenedores, cantidad, unidad_cantidad, naturaleza_cantidad, metodo_cuantificacion)
            SELECT '{Org}', '{sucursal}', '{version}', 1, 'Agua residual ficticia', 'Fosa del cliente', c.valor_m3, 'm3', 1, c.valor_m3, 'm3', 'estimada', 'capacidad_unidad_llena'
            FROM catalogo.capacidad_vehiculo c WHERE c.id = {capacidad};
            COMMIT;
            """);
        return new Sembrado(manifiesto, nombreCliente, numero, folio);
    }

    private static async Task<List<ManifiestoResumen>> ListarAsync(HttpClient nav) =>
        (await nav.GetFromJsonAsync<List<ManifiestoResumen>>("/api/manifiestos"))!;

    [Fact]
    public async Task Lista_la_ultima_version_con_unidad_participantes_y_cantidad_estimada_por_unidad_llena()
    {
        var m = await SembrarAsync(Suc, "2026-11-04T09:00:00-06:00", conDescarga: true, folio: "MAN-FICTICIO-1");
        using var nav = await SesionAsync();
        var x = Assert.Single(await ListarAsync(nav), i => i.Id == m.Manifiesto);
        Assert.Equal((m.Cliente, "MAN-FICTICIO-1", 1), (x.Cliente, x.Folio, x.Revision));
        Assert.Equal((m.Unidad, 15m), (x.Unidad?.NumeroEconomico, x.Unidad?.CapacidadM3));
        Assert.Equal("PTAR Ficticia Norte", x.Descarga?.Planta);
        Assert.NotNull(x.Descarga?.Fecha);
        Assert.Equal(DateTimeOffset.Parse("2026-11-04T15:00:00Z"), x.Fecha);
        // Participantes en orden: cliente con su autorización y transportista.
        Assert.Equal(["generador", "transportista"], x.Participantes.Select(p => p.Rol));
        Assert.Equal(m.Cliente, x.Participantes[0].Nombre);
        Assert.StartsWith("FI-TEST-", x.Participantes[0].Autorizacion);
        Assert.Equal("Transportes Ficticios SA", x.Participantes[1].Nombre);
        // Regla de fosa fija: la unidad va llena, estimada por regla y nunca medida.
        var r = Assert.Single(x.Renglones);
        Assert.Equal((15m, "m3", "estimada", "capacidad_unidad_llena"), (r.Cantidad, r.UnidadCantidad, r.Naturaleza, r.Metodo));
        Assert.Equal(0, x.Documentos);
    }

    [Fact]
    public async Task Un_manifiesto_sin_descarga_ni_folio_se_lista_sin_inventar_datos_y_el_orden_es_por_fecha()
    {
        var antiguo = await SembrarAsync(Suc, "2026-11-01T08:00:00-06:00", conDescarga: false, folio: null, capacidad: 3);
        var reciente = await SembrarAsync(Suc, "2026-11-05T08:00:00-06:00", conDescarga: true, folio: "MAN-FICTICIO-2", capacidad: 1);
        using var nav = await SesionAsync();
        var lista = await ListarAsync(nav);
        var x = Assert.Single(lista, i => i.Id == antiguo.Manifiesto);
        Assert.Null(x.Folio);
        Assert.Null(x.Descarga);
        Assert.Equal(18m, x.Unidad?.CapacidadM3);
        Assert.Equal(18m, Assert.Single(x.Renglones).Cantidad);
        Assert.True(lista.FindIndex(i => i.Id == reciente.Manifiesto) < lista.FindIndex(i => i.Id == antiguo.Manifiesto));
    }

    [Fact]
    public async Task Solo_ve_los_manifiestos_de_sus_sucursales_y_exige_sesion()
    {
        var propio = await SembrarAsync(Suc, "2026-11-04T09:00:00-06:00", conDescarga: false, folio: "MAN-FICTICIO-3");
        var ajeno = await SembrarAsync(Suc2, "2026-11-04T09:00:00-06:00", conDescarga: false, folio: "MAN-FICTICIO-4");
        using var nav = await SesionAsync();
        var lista = await ListarAsync(nav);
        Assert.Contains(lista, i => i.Id == propio.Manifiesto);
        Assert.DoesNotContain(lista, i => i.Id == ajeno.Manifiesto);
        using var anonimo = _api.NuevoNavegador();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonimo.GetAsync("/api/manifiestos")).StatusCode);
        // Es solo lectura: no hay rutas para escribir manifiestos.
        Assert.NotEqual(HttpStatusCode.OK, (await nav.PostAsJsonAsync("/api/manifiestos", new { })).StatusCode);
    }

    /// <summary>Genera con la función de 0015 en el contexto de una cuenta (como lo hará la API al recibir la orden).</summary>
    private async Task<(Guid Manifiesto, bool GeneradorEsCliente)> GenerarAsync(string correo, Guid cliente, Guid unidad)
    {
        var acceso = await _bd.ConsultarAsync($"SELECT id FROM seguridad.acceso_autorizado WHERE correo_permitido = '{correo}'");
        var r = await _bd.ConsultarAsync(
            $"SELECT set_config('app.organizacion_id', '{Org}', true), set_config('app.acceso_id', '{acceso}', true); " +
            $"SELECT manifiesto, generador_es_cliente FROM negocio.generar_manifiesto_fosa_fija('{Org}', '{Suc}', '{cliente}', '{unidad}');");
        var partes = r.Split('\n').Last().Split('|');
        return (Guid.Parse(partes[0]), partes[1] == "t");
    }

    private async Task<Guid> ClienteAsync(string nombre, bool autorizado)
    {
        var (cliente, empresa) = (Guid.NewGuid(), Guid.NewGuid());
        await _bd.SqlComoDuenoAsync($"""
            BEGIN;
            INSERT INTO negocio.empresa VALUES ('{Org}', '{empresa}', 'CLI-{empresa:N}');
            INSERT INTO negocio.ficha_empresa(organizacion_id, empresa_id, revision, nombre_fiscal, nombre_comercial) VALUES ('{Org}', '{empresa}', 1, '{nombre} SA', '{nombre}');
            INSERT INTO negocio.empresa_sucursal VALUES ('{Org}', '{Suc}', '{empresa}');
            INSERT INTO negocio.autorizacion_empresa(organizacion_id, empresa_id, autoridad, finalidad, numero)
            SELECT '{Org}', '{empresa}', 'Medio ambiente', 'Generador', 'FI-GEN-{empresa:N}' WHERE {autorizado};
            INSERT INTO negocio.cliente VALUES ('{Org}', '{Suc}', '{cliente}', '{empresa}', 'C-{cliente:N}');
            COMMIT;
            """);
        return cliente;
    }

    [Fact]
    public async Task El_generador_es_el_cliente_autorizado_o_nuestra_empresa_para_un_cliente_general()
    {
        // Nuestra empresa (la de la sucursal) con su ficha y su autorización de transportista.
        await _bd.SqlComoDuenoAsync($"""
            INSERT INTO negocio.ficha_empresa(organizacion_id, empresa_id, revision, nombre_fiscal, nombre_comercial)
            SELECT '{Org}', '{Empresa}', 1, 'Transportes de Prueba SA de CV', 'Transportes de Prueba'
            WHERE NOT EXISTS (SELECT 1 FROM negocio.ficha_empresa WHERE empresa_id = '{Empresa}');
            INSERT INTO negocio.autorizacion_empresa(organizacion_id, empresa_id, autoridad, finalidad, numero)
            SELECT '{Org}', '{Empresa}', 'Medio ambiente', 'Transportista', 'FI-TRA-PRUEBA'
            WHERE NOT EXISTS (SELECT 1 FROM negocio.autorizacion_empresa WHERE empresa_id = '{Empresa}' AND finalidad = 'Transportista');
            """);
        var nombreEmpresa = await _bd.ConsultarAsync($"SELECT coalesce(nullif(btrim(nombre_comercial), ''), nombre_fiscal) FROM negocio.ficha_empresa WHERE empresa_id = '{Empresa}' ORDER BY revision DESC LIMIT 1");
        var autorizado = await ClienteAsync($"Autorizado {Guid.NewGuid():N}"[..20], autorizado: true);
        var general = await ClienteAsync($"General {Guid.NewGuid():N}"[..17], autorizado: false);
        var unidad = Guid.NewGuid();
        await _bd.SqlComoDuenoAsync($"""
            INSERT INTO negocio.vehiculo VALUES ('{Org}', '{Suc}', '{unidad}', 'GEN-{unidad.ToString("N")[..5]}');
            INSERT INTO negocio.ficha_vehiculo(organizacion_id, sucursal_id, vehiculo_id, revision, capacidad_id) VALUES ('{Org}', '{Suc}', '{unidad}', 1, 2);
            """);
        var correo = $"generar-{Guid.NewGuid():N}@example.invalid";
        await _bd.AutorizarAsync(correo, escribir: true);

        var (mAut, autEsCliente) = await GenerarAsync(correo, autorizado, unidad);
        var (mGen, genEsCliente) = await GenerarAsync(correo, general, unidad);
        Assert.True(autEsCliente);   // el cliente firma
        Assert.False(genEsCliente);  // no se pide firma del cliente

        using var nav = _api.NuevoNavegador();
        Assert.Equal(HttpStatusCode.NoContent, (await nav.EntrarAsync(ValidadorFicticio.Token($"s-{correo}", correo))).StatusCode);
        var lista = await ListarAsync(nav);
        var a = Assert.Single(lista, x => x.Id == mAut);
        var g = Assert.Single(lista, x => x.Id == mGen);
        Assert.StartsWith("Autorizado", a.Participantes.Single(p => p.Rol == "generador").Nombre);
        Assert.StartsWith("FI-GEN-", a.Participantes.Single(p => p.Rol == "generador").Autorizacion);
        Assert.Equal(nombreEmpresa, g.Participantes.Single(p => p.Rol == "generador").Nombre);
        Assert.All([a, g], m => Assert.Equal(nombreEmpresa, m.Participantes.Single(p => p.Rol == "transportista").Nombre));
        Assert.All([a, g], m => Assert.Equal((15m, "estimada", "capacidad_unidad_llena"), (m.Renglones[0].Cantidad, m.Renglones[0].Naturaleza, m.Renglones[0].Metodo)));
        Assert.All([a, g], m => Assert.Null(m.Folio));
        Assert.All([a, g], m => Assert.DoesNotContain(m.Participantes, p => p.Rol == "destinatario"));
    }

    [Fact]
    public async Task Generar_exige_permiso_de_escritura_y_no_deja_nada_a_medias()
    {
        var cliente = await ClienteAsync($"Sin permiso {Guid.NewGuid():N}"[..21], autorizado: true);
        var correo = $"lector-{Guid.NewGuid():N}@example.invalid";
        await _bd.AutorizarAsync(correo, escribir: false);
        var antes = await _bd.ConsultarAsync("SELECT count(*) FROM negocio.manifiesto");
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => GenerarAsync(correo, cliente, Guid.NewGuid()));
        Assert.Contains("Sin permiso", error.Message);
        Assert.Equal(antes, await _bd.ConsultarAsync("SELECT count(*) FROM negocio.manifiesto"));
    }
}
