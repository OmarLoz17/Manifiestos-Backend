using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Manifiestos.Api.Acceso;
using Manifiestos.Api.Datos;
using Npgsql;

namespace Manifiestos.Api.Documentos;

/*
  QR de validación del manifiesto (decisión 19-nov-2026, mockup aprobado; migración 0058).
  El QR impreso lleva «<id del registro>.<firma>». El registro guarda una copia congelada de lo que se imprime (JSON canónico) y su huella SHA-256; la firma es
  ECDSA P-256 (IEEE P1363, 64 bytes) con una llave privada que solo existe en el servidor. Se eligió P-256 y no Ed25519 porque .NET no trae Ed25519 y esta curva
  se puede verificar con la llave pública en cualquier plataforma (módulo futuro de verificación, Agua y Drenaje) sin usuarios en este sistema.
  La página pública no recalcula nada: comprueba la firma del registro y enseña los datos emitidos para compararlos con el papel («Verificar lo impreso»).
*/

/// <summary>Lo que se congela y firma al imprimir. El orden de las propiedades fija el JSON canónico.</summary>
public sealed record ContenidoValidacion(
    int V, string Folio, string Tipo, DateTimeOffset Fecha, int Revision, string? Generador, string? Transportista, string? Destinatario,
    decimal? Cantidad, string? Unidad, string? NumeroEconomico, int Firmas);

public sealed record CodigoValidacion(string Codigo, string Huella);
public sealed record DatosValidados(string Folio, string Tipo, DateTimeOffset EmitidoEn, string? Generador, string? Transportista, string? Destinatario,
    decimal? Cantidad, string? Unidad, string? NumeroEconomico, int Firmas, string Huella);
/// <summary>«autentico» | «reemplazado» (hay una versión posterior del manifiesto) | «no_autentico» (sin datos).</summary>
public sealed record ResultadoValidacion(string Estado, DatosValidados? Datos);
public sealed record VerificacionImpresa(string? Folio, decimal? Cantidad, string? Destinatario, string? Generador, string? Transportista, int? Firmas);
public sealed record DiferenciaImpresa(string Campo, string? Impreso, string? Emitido);
public sealed record ResultadoVerificacion(string Estado, bool Coincide, IReadOnlyList<DiferenciaImpresa> Diferencias);

/// <summary>Llave de firma (ECDSA P-256). Se lee de Validacion:LlavePem o de un archivo PEM; si no existe se crea (y debe respaldarse: ver operacion.MD).</summary>
public sealed class LlaveValidacion
{
    private readonly ECDsa _llave;
    public string LlaveId { get; }
    public string PublicaPem { get; }

    public LlaveValidacion(IConfiguration config, IHostEnvironment entorno)
    {
        _llave = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var pem = config["Validacion:LlavePem"];
        if (string.IsNullOrWhiteSpace(pem))
        {
            var ruta = config["Validacion:RutaLlave"] ?? Path.GetFullPath(Path.Combine(entorno.ContentRootPath, "..", "..", ".local", "llave-validacion.pem"));
            if (File.Exists(ruta)) pem = File.ReadAllText(ruta);
            else if (entorno.IsDevelopment() || entorno.IsEnvironment("E2E") || entorno.IsEnvironment("Testing"))
            {
                // Desarrollo y pruebas: se crea sola. En producción NO: una llave nueva invalidaría todos los QR ya impresos.
                using var nueva = ECDsa.Create(ECCurve.NamedCurves.nistP256);
                pem = nueva.ExportPkcs8PrivateKeyPem();
                Directory.CreateDirectory(Path.GetDirectoryName(ruta)!);
                File.WriteAllText(ruta, pem);
            }
            else throw new InvalidOperationException("Falta la llave de validación de manifiestos (Validacion:LlavePem o Validacion:RutaLlave).");
        }
        _llave.ImportFromPem(pem);
        PublicaPem = _llave.ExportSubjectPublicKeyInfoPem();
        LlaveId = Convert.ToHexString(SHA256.HashData(_llave.ExportSubjectPublicKeyInfo()))[..8].ToLowerInvariant();
    }

    public byte[] Firmar(byte[] datos) => _llave.SignData(datos, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
    public bool Verificar(byte[] datos, byte[] firma) =>
        firma.Length == 64 && _llave.VerifyData(datos, firma, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
}

public interface IServicioValidacion
{
    Task<CodigoValidacion?> EmitirAsync(Guid organizacion, Guid acceso, Guid manifiesto, CancellationToken ct);
    Task<ResultadoValidacion> ConsultarAsync(string codigo, string origen, CancellationToken ct);
    Task<ResultadoVerificacion> VerificarAsync(string codigo, VerificacionImpresa impreso, string origen, CancellationToken ct);
}

public sealed class ServicioValidacion(NpgsqlDataSource datos, IRepositorioManifiestos manifiestos, LlaveValidacion llave) : IServicioValidacion
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = false };

    internal static string Base64Url(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    internal static byte[]? DeBase64Url(string s)
    {
        try
        {
            var t = s.Replace('-', '+').Replace('_', '/');
            return Convert.FromBase64String(t.PadRight(t.Length + (4 - t.Length % 4) % 4, '='));
        }
        catch (FormatException) { return null; }
    }

    /// <summary>Huella corta legible en el papel: 12 caracteres hexadecimales en grupos de 4 («7F3A-91C2-0B5E»).</summary>
    internal static string HuellaCorta(byte[] huella)
    {
        var h = Convert.ToHexString(huella)[..12];
        return $"{h[..4]}-{h[4..8]}-{h[8..12]}";
    }

    internal static ContenidoValidacion Contenido(ManifiestoResumen m)
    {
        string? Nombre(string rol) => m.Participantes.FirstOrDefault(x => x.Rol == rol)?.Nombre;
        var cantidad = m.Renglones.Where(r => r.Cantidad is not null).Sum(r => r.Cantidad!.Value);
        var firmas = m.Evidencias?.Where(e => e.Rol is "FIRMA_CLIENTE" or "FIRMA_OPERADOR" or "FIRMA_RECEPCION").Select(e => e.Rol).Distinct().Count() ?? 0;
        return new ContenidoValidacion(1, m.Folio!, m.Tipo, new DateTimeOffset(m.Fecha.UtcDateTime.Ticks - m.Fecha.UtcDateTime.Ticks % TimeSpan.TicksPerSecond, TimeSpan.Zero),
            m.Revision, Nombre("generador"), Nombre("transportista"), Nombre("destinatario") ?? m.Descarga?.Planta,
            m.Renglones.Any(r => r.Cantidad is not null) ? cantidad / 1.0000000000000000000000000000m : null, m.Renglones.FirstOrDefault()?.UnidadCantidad, m.Unidad?.NumeroEconomico, firmas);
    }

    public async Task<CodigoValidacion?> EmitirAsync(Guid organizacion, Guid acceso, Guid manifiesto, CancellationToken ct)
    {
        var m = (await manifiestos.ListarAsync(organizacion, acceso, ct)).FirstOrDefault(x => x.Id == manifiesto);
        if (m is null || string.IsNullOrWhiteSpace(m.Folio)) return null;   // sin folio emitido no hay QR
        var texto = JsonSerializer.Serialize(Contenido(m), Json);
        var bytes = Encoding.UTF8.GetBytes(texto);
        var huella = SHA256.HashData(bytes);
        var firma = llave.Firmar(bytes);
        await using var tx = await TransaccionContexto.AbrirAsync(datos, organizacion, acceso, ct);
        Guid id;
        await using (var cmd = tx.Comando("SELECT seguridad.registrar_codigo_validacion($1, $2, $3, $4, $5, $6)", m.Id, m.Revision, texto, huella, firma, llave.LlaveId))
            id = (Guid)(await cmd.ExecuteScalarAsync(ct))!;
        // Si la huella ya estaba registrada, su firma es la guardada (ECDSA no es determinista): el código usa SIEMPRE la del registro.
        byte[] firmaGuardada;
        await using (var cmd = tx.Comando("SELECT firma FROM negocio.codigo_validacion_manifiesto WHERE id = $1", id))
            firmaGuardada = (byte[])(await cmd.ExecuteScalarAsync(ct))!;
        await tx.ConfirmarAsync(ct);
        return new CodigoValidacion($"{Base64Url(id.ToByteArray())}.{Base64Url(firmaGuardada)}", HuellaCorta(huella));
    }

    private sealed record Registro(string Contenido, byte[] Huella, byte[] Firma, DateTimeOffset Creada, int Revision, int RevisionActual, Guid Id);

    /// <summary>Lee el registro del código y comprueba que la firma del QR sea la guardada y valga. null = no auténtico (sin distinguir el motivo).</summary>
    private async Task<Registro?> LeerAsync(string codigo, CancellationToken ct)
    {
        var partes = (codigo ?? "").Split('.');
        if (partes.Length != 2 || codigo!.Length > 200) return null;
        var idBytes = DeBase64Url(partes[0]); var firma = DeBase64Url(partes[1]);
        if (idBytes is not { Length: 16 } || firma is not { Length: 64 }) return null;
        var id = new Guid(idBytes);
        await using var con = await datos.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand("SELECT contenido, huella, firma, creada_en, revision, revision_actual FROM seguridad.consultar_codigo_validacion($1)", con);
        cmd.Parameters.AddWithValue(id);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return null;
        var contenido = r.GetString(0); var guardada = (byte[])r.GetValue(2);
        var bytes = Encoding.UTF8.GetBytes(contenido);
        if (!CryptographicOperations.FixedTimeEquals(guardada, firma) || !CryptographicOperations.FixedTimeEquals((byte[])r.GetValue(1), SHA256.HashData(bytes)) || !llave.Verificar(bytes, firma)) return null;
        return new Registro(contenido, (byte[])r.GetValue(1), firma, new DateTimeOffset(DateTime.SpecifyKind(r.GetDateTime(3), DateTimeKind.Utc)), r.GetInt32(4), r.GetInt32(5), id);
    }

    private async Task RegistrarEscaneoAsync(Guid? id, string resultado, string origen, CancellationToken ct)
    {
        if (id is null) return;
        await using var con = await datos.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand("SELECT seguridad.registrar_escaneo_validacion($1, $2, $3)", con);
        cmd.Parameters.AddWithValue(id.Value); cmd.Parameters.AddWithValue(resultado); cmd.Parameters.AddWithValue(origen);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static DatosValidados Datos(Registro g)
    {
        var c = JsonSerializer.Deserialize<ContenidoValidacion>(g.Contenido, Json)!;
        return new DatosValidados(c.Folio, c.Tipo, g.Creada, c.Generador, c.Transportista, c.Destinatario, c.Cantidad, c.Unidad, c.NumeroEconomico, c.Firmas, HuellaCorta(g.Huella));
    }

    public async Task<ResultadoValidacion> ConsultarAsync(string codigo, string origen, CancellationToken ct)
    {
        var g = await LeerAsync(codigo, ct);
        if (g is null) return new ResultadoValidacion("no_autentico", null);
        var estado = g.RevisionActual > g.Revision ? "reemplazado" : "autentico";
        await RegistrarEscaneoAsync(g.Id, estado, origen, ct);
        return new ResultadoValidacion(estado, Datos(g));
    }

    private static string Norm(string? s)
    {
        var d = (s ?? "").Trim().Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder();
        foreach (var ch in d) if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark) sb.Append(char.ToUpperInvariant(ch));
        return string.Join(' ', sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    public async Task<ResultadoVerificacion> VerificarAsync(string codigo, VerificacionImpresa i, string origen, CancellationToken ct)
    {
        var g = await LeerAsync(codigo, ct);
        if (g is null) return new ResultadoVerificacion("no_autentico", false, []);
        var e = Datos(g);
        var dif = new List<DiferenciaImpresa>();
        void Texto(string campo, string? impreso, string? emitido) { if (impreso is not null && Norm(impreso) != Norm(emitido)) dif.Add(new DiferenciaImpresa(campo, impreso, emitido)); }
        Texto("folio", i.Folio, e.Folio); Texto("generador", i.Generador, e.Generador); Texto("transportista", i.Transportista, e.Transportista); Texto("destinatario", i.Destinatario, e.Destinatario);
        if (i.Cantidad is { } cant && Math.Abs(cant - (e.Cantidad ?? 0m)) >= 0.005m)
            dif.Add(new DiferenciaImpresa("cantidad", cant.ToString("0.##", CultureInfo.InvariantCulture), (e.Cantidad ?? 0m).ToString("0.##", CultureInfo.InvariantCulture)));
        if (i.Firmas is { } f && f != e.Firmas) dif.Add(new DiferenciaImpresa("firmas", f.ToString(CultureInfo.InvariantCulture), e.Firmas.ToString(CultureInfo.InvariantCulture)));
        var estado = g.RevisionActual > g.Revision ? "reemplazado" : "autentico";
        await RegistrarEscaneoAsync(g.Id, dif.Count == 0 ? "verificacion_coincide" : "verificacion_no_coincide", origen, ct);
        return new ResultadoVerificacion(estado, dif.Count == 0, dif);
    }
}

public static class EndpointsValidacion
{
    public static IServiceCollection AgregarValidacionManifiestos(this IServiceCollection services) =>
        services.AddSingleton<LlaveValidacion>().AddSingleton<IServicioValidacion, ServicioValidacion>();

    /// <summary>Huella (no la dirección) del origen para detectar muchos escaneos desde lugares distintos sin guardar datos personales.</summary>
    private static string Origen(HttpContext http) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(http.Connection.RemoteIpAddress?.ToString() ?? "sin-ip")))[..12].ToLowerInvariant();

    public static void MapearValidacion(this IEndpointRouteBuilder app)
    {
        // Panel (con sesión y CSRF): registra y firma lo que se va a imprimir. Imprimir es leer: basta datos.leer sobre el manifiesto.
        app.MapPost("/api/manifiestos/{id:guid}/validacion", async (Guid id, HttpContext http, IServicioValidacion validacion) =>
        {
            if (!ServiciosAcceso.LeerIds(http.User, out var org, out var acceso)) return Results.Unauthorized();
            http.Response.Headers.CacheControl = "no-store";
            var r = await validacion.EmitirAsync(org, acceso, id, http.RequestAborted);
            return r is null ? Results.Json(new MensajeError("El manifiesto no existe o todavía no tiene folio."), statusCode: StatusCodes.Status409Conflict) : Results.Ok(r);
        }).AddEndpointFilter(EndpointsAcceso.ValidarCsrf);

        // Página pública (sin sesión). Todo lo que no cuadra responde igual: «no auténtico», sin pistas.
        app.MapGet("/api/validacion/llave-publica", (LlaveValidacion llave) => Results.Text(llave.PublicaPem, "application/x-pem-file"))
            .AllowAnonymous().RequireRateLimiting(ServiciosAcceso.PoliticaValidacion);
        app.MapGet("/api/validacion/{codigo}", async (string codigo, HttpContext http, IServicioValidacion validacion) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            return Results.Ok(await validacion.ConsultarAsync(codigo, Origen(http), http.RequestAborted));
        }).AllowAnonymous().RequireRateLimiting(ServiciosAcceso.PoliticaValidacion);
        app.MapPost("/api/validacion/{codigo}/verificar", async (string codigo, VerificacionImpresa cuerpo, HttpContext http, IServicioValidacion validacion) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            return Results.Ok(await validacion.VerificarAsync(codigo, cuerpo, Origen(http), http.RequestAborted));
        }).AllowAnonymous().RequireRateLimiting(ServiciosAcceso.PoliticaValidacion);
    }
}
