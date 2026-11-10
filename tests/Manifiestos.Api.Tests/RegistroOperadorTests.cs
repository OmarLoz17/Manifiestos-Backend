using System.Diagnostics;
using Manifiestos.Api.Tests.Infraestructura;

namespace Manifiestos.Api.Tests;

[Trait("Categoria","Integracion")]
public sealed class RegistroOperadorTests(BaseDatosPrueba bd) : IClassFixture<BaseDatosPrueba>
{
    private async Task<int> RegistrarAsync(string correo,Guid operaciones,Guid instalacion,string nombre="Operador Ficticio D'Ávila")
    {
        var raiz=new DirectoryInfo(AppContext.BaseDirectory);
        while (raiz is not null && !File.Exists(Path.Combine(raiz.FullName,"compose.yaml"))) raiz=raiz.Parent;
        Assert.NotNull(raiz);
        var psi=new ProcessStartInfo("powershell.exe") {UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};
        foreach(var arg in new[]{"-NoProfile","-ExecutionPolicy","Bypass","-File",Path.Combine(raiz!.FullName,"database","Add-AccesoOperador.ps1"),
            "-Correo",correo,"-OrganizacionId",BaseDatosPrueba.Organizacion.ToString(),"-Database",bd.Nombre,"-SucursalId",BaseDatosPrueba.Sucursal.ToString(),
            "-AccesoOperacionesId",operaciones.ToString(),"-Nombre",nombre,"-InstalacionId",instalacion.ToString()}) psi.ArgumentList.Add(arg);
        psi.Environment.Remove("PSModulePath");
        using var p=Process.Start(psi)!; var salida=p.StandardOutput.ReadToEndAsync(); var error=p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync(); await salida; await error;
        return p.ExitCode;
    }
    [Fact] public async Task Alta_repetida_conserva_persona_y_entrega_sin_dar_permisos_de_panel()
    {
        var ops=$"ops-{Guid.NewGuid():N}@example.invalid"; var correo=$"registro-{Guid.NewGuid():N}@example.invalid";
        await bd.AutorizarAsync(ops,escribir:true);
        var acceso=Guid.Parse(await bd.ConsultarAsync($"SELECT id FROM seguridad.acceso_autorizado WHERE correo_permitido='{ops}'"));
        var instalacion=Guid.NewGuid();
        Assert.Equal(0,await RegistrarAsync(correo,acceso,instalacion));
        Assert.Equal(0,await RegistrarAsync(correo,acceso,instalacion));
        Assert.Equal("1",await bd.ConsultarAsync($"SELECT count(*) FROM negocio.operador_ficha WHERE correo='{correo}'"));
        Assert.Equal("1",await bd.ConsultarAsync($"SELECT count(*) FROM seguridad.asignacion_equipo e JOIN seguridad.instalacion_app i ON i.dispositivo_id=e.dispositivo_id AND i.organizacion_id=e.organizacion_id WHERE i.id='{instalacion}'"));
        Assert.Equal("0",await bd.ConsultarAsync($"SELECT count(*) FROM seguridad.permiso_sucursal p JOIN seguridad.acceso_autorizado a ON (a.organizacion_id,a.id)=(p.organizacion_id,p.acceso_id) WHERE a.correo_permitido='{correo}'"));
        Assert.NotEqual(0,await RegistrarAsync(correo,acceso,instalacion,"Otro nombre"));
        Assert.Equal("1",await bd.ConsultarAsync($"SELECT count(*) FROM negocio.operador_ficha WHERE correo='{correo}'"));
        Assert.NotEqual(0,await RegistrarAsync(ops,acceso,Guid.NewGuid()));
        Assert.Equal("operaciones",await bd.ConsultarAsync($"SELECT rol_codigo FROM seguridad.acceso_autorizado WHERE correo_permitido='{ops}'"));
    }
}
