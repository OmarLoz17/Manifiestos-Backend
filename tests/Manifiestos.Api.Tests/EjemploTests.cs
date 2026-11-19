using System.Net;
using System.Net.Http.Json;
using Manifiestos.Api.Clientes;
using Manifiestos.Api.Documentos;
using Manifiestos.Api.Incidencias;
using Manifiestos.Api.Operadores;
using Manifiestos.Api.Ordenes;
using Manifiestos.Api.Rutas;
using Manifiestos.Api.Tests.Infraestructura;
using Manifiestos.Api.Unidades;

namespace Manifiestos.Api.Tests;

/// <summary>
/// Los datos de ejemplo (database/demo/datos_ejemplo.sql, ficticios con apariencia real) se cargan y la API los
/// sirve en todos los módulos con las reglas confirmadas: generador según autorización, unidad llena, cuentas de
/// teléfono, incidencias con historial y los tres tipos de servicio en Órdenes.
/// </summary>
[Trait("Categoria", "Integracion")]
public sealed class EjemploTests : IClassFixture<BaseDatosPrueba>, IAsyncLifetime
{
    private static readonly Guid Org = Guid.Parse("e1000000-0000-4000-8000-000000000001");
    private static readonly Guid Suc = Guid.Parse("e1000000-0000-4000-8000-000000000003");
    private const string Correo = "revision-ejemplo@example.com";

    private readonly BaseDatosPrueba _bd;
    private ApiPrueba _api = null!;
    private HttpClient _nav = null!;

    public EjemploTests(BaseDatosPrueba bd) => _bd = bd;

    public async Task InitializeAsync()
    {
        var archivo = Path.Combine(BuscarBackend(), "database", "demo", "datos_ejemplo.sql");
        if (await _bd.ConsultarAsync($"SELECT count(*) FROM negocio.organizacion WHERE id = '{Org}'") == "0")
        {
            await _bd.SqlComoDuenoAsync(await File.ReadAllTextAsync(archivo));
            await _bd.SqlComoDuenoAsync($"""
                INSERT INTO seguridad.acceso_autorizado(organizacion_id, correo_permitido, activo, rol_codigo) VALUES ('{Org}', '{Correo}', true, 'operaciones');
                INSERT INTO seguridad.permiso_sucursal SELECT '{Org}', '{Suc}', a.id, 'datos.leer' FROM seguridad.acceso_autorizado a WHERE a.correo_permitido = '{Correo}';
                """);
        }
        _api = new ApiPrueba(_bd) { Organizacion = Org };
        _nav = _api.NuevoNavegador();
        Assert.Equal(HttpStatusCode.NoContent, (await _nav.EntrarAsync(ValidadorFicticio.Token("s-ejemplo", Correo))).StatusCode);
    }

    public async Task DisposeAsync()
    {
        _nav.Dispose();
        await _api.DisposeAsync();
    }

    private static string BuscarBackend()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "database", "demo", "datos_ejemplo.sql"))) return d.FullName;
        throw new InvalidOperationException("No se encontró database/demo/datos_ejemplo.sql.");
    }

    [Fact]
    public async Task Clientes_unidades_operadores_y_rutas_tienen_datos_con_apariencia_real()
    {
        var clientes = (await _nav.GetFromJsonAsync<List<ClienteResumen>>("/api/clientes"))!;
        Assert.True(clientes.Count >= 20);
        Assert.Contains(clientes, c => c.Autorizacion is not null);
        Assert.Contains(clientes, c => c.Autorizacion is null);
        Assert.All(clientes, c => Assert.DoesNotContain("Demo", c.Nombre));
        Assert.Contains(clientes.SelectMany(c => c.Sitios ?? []), s => s.FosaPropia);

        var unidades = (await _nav.GetFromJsonAsync<ListaUnidades>("/api/unidades"))!.Unidades;
        Assert.True(unidades.Count >= 6);
        Assert.Contains(unidades, u => u.Clasificacion == "sanitarios");
        Assert.Contains(unidades, u => u.Operador is null);

        var operadores = (await _nav.GetFromJsonAsync<List<OperadorFicha>>("/api/operadores"))!;
        Assert.True(operadores.Count >= 6);
        Assert.All(operadores.Where(o => o.Correo is not null), o => Assert.EndsWith("@example.com", o.Correo));

        var rutas = (await _nav.GetFromJsonAsync<List<RutaResumen>>("/api/rutas"))!;
        Assert.True(rutas.Count >= 6);
        Assert.All(rutas, r => Assert.True(r.Paradas > 0));
    }

    [Fact]
    public async Task Ordenes_manifiestos_e_incidencias_siguen_las_reglas_confirmadas()
    {
        var ordenes = (await _nav.GetFromJsonAsync<List<RutaDeOrdenes>>("/api/ordenes"))!;
        Assert.All(ordenes, r => Assert.False(string.IsNullOrWhiteSpace(r.Empresa))); // generador de los clientes generales: nuestra empresa, con su nombre
        var clientes = ordenes.SelectMany(r => r.Paradas).SelectMany(p => p.Clientes).ToList();
        Assert.Contains(clientes, c => c.Banos > 0);
        Assert.Contains(clientes, c => c.Fosas > 0 && !c.FosaPropia);
        Assert.Contains(clientes, c => c.Fosas > 0 && c.FosaPropia);

        var manifiestos = (await _nav.GetFromJsonAsync<List<ManifiestoResumen>>("/api/manifiestos"))!;
        Assert.True(manifiestos.Count >= 10);
        Assert.All(manifiestos, m => Assert.NotNull(m.Evidencias)); // fotos y firmas por manifiesto (se ven con /api/archivos/{id})
        // Fosa fija: unidad llena (capacidad de la unidad, estimada). Sanitarios: capacidad de la unidad (1 m3) estimada, sin cliente (tirada).
        Assert.All(manifiestos.Where(m => m.Tipo == "fosa_fija"), m => Assert.Equal((m.Unidad?.CapacidadM3, "estimada"), (m.Renglones[0].Cantidad, m.Renglones[0].Naturaleza)));
        var san = manifiestos.Single(m => m.Tipo == "sanitarios");
        Assert.Null(san.ClienteId);
        Assert.Equal("014/2026", san.Folio);
        Assert.Equal("PTAR NORTE", san.Sanitarios!.Planta);
        Assert.Equal((66, 1m, "capacidad_unidad_llena"), (san.Sanitarios.TotalBanos, san.Sanitarios.TotalM3Estimado, san.Sanitarios.Metodo));
        Assert.Equal(3, san.Sanitarios.Paradas.Count);
        Assert.True(san.Sanitarios.Firmas.Generador.Provisional);
        Assert.Equal("AR Multiservicios", san.Participantes.Single(p => p.Rol == "generador").Nombre);
        var ruta410 = (await _nav.GetFromJsonAsync<List<RutaResumen>>("/api/rutas"))!.Single(r => r.Nombre == "ARM-410-LMV");
        Assert.Equal((50, 0.8m, 80.0m), (ruta410.Carga!.Banos, ruta410.Carga.M3, ruta410.Carga.Porcentaje));
        // Un cliente general lleva a nuestra empresa como generador; uno autorizado, a sí mismo.
        Assert.Contains(manifiestos, m => m.Participantes.Single(p => p.Rol == "generador").Nombre == "AR Multiservicios");
        Assert.Contains(manifiestos, m => m.Participantes.Single(p => p.Rol == "generador").Nombre == m.Cliente);
        Assert.Contains(manifiestos, m => m.Descarga is null);

        var incidencias = (await _nav.GetFromJsonAsync<List<IncidenciaResumen>>("/api/incidencias"))!;
        Assert.Contains(incidencias, i => i.Servicio == "fosa");
        Assert.Contains(incidencias, i => i.Servicio == "sanitario" && i.BanosAsignados > i.BanosAtendidos);
        Assert.Contains(incidencias, i => i.Estatus == "cerrada" && i.Historial.Count == 3);
        Assert.All(incidencias, i => Assert.NotNull(i.Operador));
    }
}
