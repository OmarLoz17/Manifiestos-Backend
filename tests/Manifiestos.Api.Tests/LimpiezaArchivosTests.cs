using System.Net;
using Manifiestos.Api.Archivos;
using Manifiestos.Api.Tests.Infraestructura;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Manifiestos.Api.Tests;

/// <summary>Limpieza por retención (0069): solo retira del VPS lo que ya tiene copia verificada en Drive hace N días. Datos ficticios.</summary>
[Trait("Categoria", "Integracion")]
public sealed class LimpiezaArchivosTests(BaseDatosPrueba bd) : IClassFixture<BaseDatosPrueba>, IAsyncLifetime
{
    private static readonly Guid Org = BaseDatosPrueba.Organizacion, Suc = BaseDatosPrueba.Sucursal;
    private ApiPrueba _api = null!;
    public Task InitializeAsync() { _api = new ApiPrueba(bd); return Task.CompletedTask; }
    public async Task DisposeAsync() => await _api.DisposeAsync();

    /// <summary>Un archivo con copia en el VPS (y en disco) y, opcionalmente, en Drive verificada hace <paramref name="diasEnDrive"/> días.</summary>
    private async Task<(Guid Id, string Ruta)> SembrarAsync(int? diasEnDrive, int bytes = 64, int bytesEnDisco = 64)
    {
        var id = Guid.NewGuid(); var clave = $"limpieza/{id:N}.jpg";
        var ruta = Path.Combine(_api.RutaArchivos, clave); Directory.CreateDirectory(Path.GetDirectoryName(ruta)!);
        await File.WriteAllBytesAsync(ruta, new byte[bytesEnDisco]);
        var drive = diasEnDrive is null ? "" : $"INSERT INTO negocio.ubicacion_archivo(organizacion_id,sucursal_id,archivo_id,proveedor,clave_externa,verificada_en) VALUES('{Org}','{Suc}','{id}','drive','drive-{id:N}', now() - interval '{diasEnDrive} days');";
        await bd.SqlComoDuenoAsync($"""
            INSERT INTO negocio.archivo(organizacion_id,sucursal_id,id,sha256,tamano_bytes,mime) VALUES('{Org}','{Suc}','{id}',decode(repeat('00',32),'hex'),{bytes},'image/jpeg');
            INSERT INTO negocio.ubicacion_archivo(organizacion_id,sucursal_id,archivo_id,proveedor,clave_externa,verificada_en) VALUES('{Org}','{Suc}','{id}','vps','{clave}', now());
            {drive}
            """);
        return (id, ruta);
    }

    /// <summary>Con el rol limitado de la API (el mismo que usa el servidor).</summary>
    private async Task SqlComoApiAsync(string sql)
    {
        await using var cmd = _api.Services.GetRequiredService<NpgsqlDataSource>().CreateCommand(sql);
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync()) { }
    }

    private LimpiezaArchivosLocales Servicio() => _api.Services.GetRequiredService<LimpiezaArchivosLocales>();

    [Fact]
    public async Task Retira_solo_lo_que_ya_esta_en_Drive_hace_mas_de_N_dias_y_no_repite()
    {
        var viejo = await SembrarAsync(diasEnDrive: 30);
        var reciente = await SembrarAsync(diasEnDrive: 3);
        var sinDrive = await SembrarAsync(diasEnDrive: null);
        var otroTamano = await SembrarAsync(diasEnDrive: 30, bytes: 64, bytesEnDisco: 10);

        var (archivos, bytes) = await Servicio().LimpiarAsync(14, default);
        Assert.True(archivos >= 1 && bytes >= 64);
        Assert.False(File.Exists(viejo.Ruta));                  // archivado hace 30 días: retirado del VPS
        Assert.True(File.Exists(reciente.Ruta));                // solo 3 días en Drive: se conserva
        Assert.True(File.Exists(sinDrive.Ruta));                // sin copia en Drive: nunca se toca
        Assert.True(File.Exists(otroTamano.Ruta));              // el disco no coincide con la base: se omite sin borrar
        Assert.Equal("1", await bd.ConsultarAsync($"SELECT count(*) FROM sync.archivo_local_limpiado WHERE archivo_id='{viejo.Id}'"));
        Assert.Equal("0", await bd.ConsultarAsync($"SELECT count(*) FROM sync.archivo_local_limpiado WHERE archivo_id IN ('{reciente.Id}','{sinDrive.Id}','{otroTamano.Id}')"));
        // La ubicación del VPS no se borra de la base (es inmutable): el historial de dónde estuvo se conserva.
        Assert.Equal("1", await bd.ConsultarAsync($"SELECT count(*) FROM negocio.ubicacion_archivo WHERE archivo_id='{viejo.Id}' AND proveedor='vps'"));

        Assert.Equal(0, (await Servicio().LimpiarAsync(14, default)).Archivos);   // ya no se vuelve a proponer
    }

    [Fact]
    public async Task La_retencion_minima_es_de_7_dias_y_solo_se_marca_con_copia_verificada()
    {
        var sinDrive = await SembrarAsync(diasEnDrive: null);
        var ex = await Assert.ThrowsAsync<PostgresException>(() => SqlComoApiAsync("SELECT * FROM sync.archivos_para_limpieza(3)"));
        Assert.Equal("22023", ex.SqlState);
        var ex2 = await Assert.ThrowsAsync<PostgresException>(() => SqlComoApiAsync($"SELECT sync.registrar_archivo_local_limpiado('{Org}','{Suc}','{sinDrive.Id}')"));
        Assert.Equal("22023", ex2.SqlState);
    }

    [Fact]
    public async Task El_panel_sigue_sirviendo_el_archivo_retirado_desde_Drive()
    {
        var a = await SembrarAsync(diasEnDrive: 30);
        _api.Drive.Contenidos["drive-" + a.Id.ToString("N")] = [1, 2, 3, 4];
        var correo = $"limpieza-{Guid.NewGuid():N}@example.invalid";
        await bd.AutorizarAsync(correo);
        var nav = _api.NuevoNavegador();
        Assert.Equal(HttpStatusCode.NoContent, (await nav.EntrarAsync(ValidadorFicticio.Token($"s-{correo}", correo))).StatusCode);

        await Servicio().LimpiarAsync(14, default);
        Assert.False(File.Exists(a.Ruta));
        var r = await nav.GetAsync($"/api/archivos/{a.Id}");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal([1, 2, 3, 4], await r.Content.ReadAsByteArrayAsync());
    }
}
