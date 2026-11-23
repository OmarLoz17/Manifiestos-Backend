using System.Diagnostics;
using System.Net;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;

namespace Manifiestos.Api.Tests;

public sealed partial class SanitariosMovilTests
{
    public sealed class EmuladorFactAttribute : FactAttribute
    {
        public EmuladorFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("MANIFIESTOS_ADB_EMULADOR")))
                Skip = "Requiere ADB y APK .pruebas en el emulador aislado emulator-5578.";
        }
    }

    [EmuladorFact]
    public async Task Android_emulador_sincroniza_por_https_tras_perder_respuesta_y_cambiar_unidad()
    {
        const string emulador = "emulator-5578";
        var adb = Environment.GetEnvironmentVariable("MANIFIESTOS_ADB_EMULADOR")!;
        var (correo, persona, instalacion) = await AltaOperadorAsync();
        var e = await EscenarioAsync(persona);
        var unidadNueva = Guid.NewGuid();
        await bd.SqlComoDuenoAsync($"""
            INSERT INTO negocio.vehiculo VALUES ('{Org}', '{Suc}', '{unidadNueva}', 'NVA-{unidadNueva.ToString("N")[..8]}');
            INSERT INTO negocio.ficha_vehiculo(organizacion_id,sucursal_id,vehiculo_id,revision,capacidad_id,clasificacion)
            VALUES ('{Org}','{Suc}','{unidadNueva}',1,4,'sanitarios');
            INSERT INTO negocio.asignacion_unidad(organizacion_id,sucursal_id,vehiculo_id,revision,persona_id)
            VALUES ('{Org}','{Suc}','{unidadNueva}',1,'{persona}');
            """);
        using var clave = RSA.Create(2048);
        var solicitud = new CertificateRequest("CN=localhost", clave, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName("localhost");
        solicitud.CertificateExtensions.Add(san.Build());
        solicitud.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        solicitud.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new("1.3.6.1.5.5.7.3.1") }, false));
        using var generado = solicitud.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddHours(1));
        // SChannel necesita que la clave del certificado esté disponible en el contenedor de claves de Windows.
        using var certificado = X509CertificateLoader.LoadPkcs12(generado.Export(X509ContentType.Pkcs12), null);
        _api.UseKestrel(o => o.Listen(IPAddress.Loopback, 0, l => l.UseHttps(certificado)));
        _api.StartServer();
        var direccion = _api.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        var puerto = new Uri(direccion).Port;
        using var verificador = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (_, presentado, _, errores) =>
                (errores & ~SslPolicyErrors.RemoteCertificateChainErrors) == SslPolicyErrors.None
                && presentado?.Thumbprint == certificado.Thumbprint
        };
        using var control = new HttpClient(verificador) { Timeout = TimeSpan.FromSeconds(10) };
        using var respuesta = await control.GetAsync($"https://localhost:{puerto}/api/auth/csrf");
        Assert.Equal(HttpStatusCode.OK, respuesta.StatusCode);
        var canal = $"tcp:{puerto}";
        using var limite = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        Task<string> EjecutarAdb(params string[] args) => EjecutarAdbAsync(adb, emulador, limite.Token, args);
        await EjecutarAdb("reverse", canal, canal);
        try
        {
            var instrumentacion = EjecutarAdb("shell", "am", "instrument", "-w", "-r",
                "-e", "class", "com.arlo.manifiestos.demo.SincronizacionHttpsTest",
                "-e", "api", $"https://localhost:{puerto}",
                "-e", "certificado", Convert.ToBase64String(certificado.Export(X509ContentType.Cert)),
                "-e", "correo", correo, "-e", "instalacion", instalacion.ToString(),
                "-e", "fecha", Hoy, "-e", "jornada", e.Jornada.ToString(), "-e", "parada", e.ParadaA.ToString(),
                "-e", "planta", e.Planta.ToString(), "-e", "unidadAnterior", e.Unidad.ToString(), "-e", "unidadNueva", unidadNueva.ToString(),
                "com.arlo.manifiestos.demo.pruebas.test/androidx.test.runner.AndroidJUnitRunner");
            string captura;
            do
            {
                captura = await bd.ConsultarAsync($"SELECT coalesce(max(captura_id::text),'') FROM negocio.captura_trabajo WHERE jornada_id='{e.Jornada}' AND tipo='SERVICIO_SANITARIOS'");
                if (captura.Length != 0) break;
                if (instrumentacion.IsCompleted) throw new InvalidOperationException(await instrumentacion);
                await Task.Delay(500, limite.Token);
            } while (true);
            await bd.SqlComoDuenoAsync($"""
                INSERT INTO negocio.cambio_unidad_dia(organizacion_id,sucursal_id,ruta_id,fecha,revision,vehiculo_id,persona_id)
                SELECT '{Org}','{Suc}',ruta_id,fecha,1,'{unidadNueva}','{persona}' FROM negocio.jornada WHERE id='{e.Jornada}';
                """);
            var resultado = await instrumentacion;
            Assert.Contains("OK (1 test)", resultado);
            Assert.Equal($"{e.Unidad}|{persona}", await bd.ConsultarAsync($"SELECT vehiculo_id||'|'||persona_id FROM negocio.succion_parada WHERE captura_id='{captura}'"));
            Assert.Equal("1", await bd.ConsultarAsync($"SELECT count(*) FROM negocio.evidencia_captura WHERE captura_id='{captura}' AND archivo_id IS NOT NULL"));
            Assert.Equal("1", await bd.ConsultarAsync($"SELECT count(*) FROM negocio.tirada t JOIN negocio.tirada_manifiesto m ON t.id=m.tirada_id WHERE t.vehiculo_id='{e.Unidad}'"));
            Assert.Equal("2", await bd.ConsultarAsync($"SELECT count(*) FROM negocio.evidencia_captura e JOIN negocio.tirada t USING (organizacion_id,sucursal_id,captura_id) WHERE t.vehiculo_id='{e.Unidad}' AND e.archivo_id IS NOT NULL"));
        }
        finally
        {
            await EjecutarAdbAsync(adb, emulador, CancellationToken.None, ["reverse", "--remove", canal]).WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    private static async Task<string> EjecutarAdbAsync(string adb, string emulador, CancellationToken ct, string[] args)
    {
        var inicio = new ProcessStartInfo(adb) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var arg in new[] { "-s", emulador }.Concat(args)) inicio.ArgumentList.Add(arg);
        using var proceso = Process.Start(inicio) ?? throw new InvalidOperationException("ADB no arrancó.");
        var salida = proceso.StandardOutput.ReadToEndAsync(ct);
        var errores = proceso.StandardError.ReadToEndAsync(ct);
        try { await proceso.WaitForExitAsync(ct); }
        catch (OperationCanceledException) { if (!proceso.HasExited) proceso.Kill(); throw; }
        var texto = await salida;
        if (proceso.ExitCode != 0) throw new InvalidOperationException($"ADB devolvió {proceso.ExitCode}: {await errores}");
        return texto;
    }
}
