using System.Buffers.Binary;
using System.IO.Compression;

namespace Manifiestos.Api.Acceso;

/// <summary>
/// Valida que un archivo de evidencia sea de verdad una imagen del tipo declarado (no solo que empiece con la firma): estructura completa, dimensiones razonables y,
/// en PNG, sumas de verificación de cada bloque y datos que descomprimen al tamaño que piden sus dimensiones. No recomprime ni modifica el archivo.
/// </summary>
public static class ValidadorImagen
{
    public const int LadoMaximo = 10_000;
    public const long PixelesMaximos = 16_000_000;
    private static readonly byte[] FirmaPng = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>La foto es JPEG y las firmas son PNG: el tipo debe corresponder al rol de la evidencia.</summary>
    public static string? MimeDelRol(string rol) => rol == "FOTO" ? "image/jpeg" : rol.StartsWith("FIRMA_", StringComparison.Ordinal) ? "image/png" : null;

    public static bool Valida(string ruta, string mime) => Valida(File.ReadAllBytes(ruta), mime, out _, out _);

    public static bool Valida(byte[] b, string mime, out int ancho, out int alto)
    {
        if (mime == "image/png") return Png(b, out ancho, out alto);
        if (mime == "image/jpeg") return Jpeg(b, out ancho, out alto);
        ancho = alto = 0; return false;
    }

    private static bool Dimensiones(long w, long h) => w > 0 && h > 0 && w <= LadoMaximo && h <= LadoMaximo && w * h <= PixelesMaximos;

    // ---- PNG ----
    private static readonly uint[] TablaCrc = Enumerable.Range(0, 256).Select(n => { var c = (uint)n; for (var k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1; return c; }).ToArray();
    public static uint Crc32(ReadOnlySpan<byte> datos)
    {
        var c = 0xFFFFFFFFu;
        foreach (var x in datos) c = TablaCrc[(c ^ x) & 0xFF] ^ (c >> 8);
        return c ^ 0xFFFFFFFFu;
    }

    private static bool Png(byte[] b, out int ancho, out int alto)
    {
        ancho = alto = 0;
        if (b.Length < 8 + 25 + 12 || !b.AsSpan(0, 8).SequenceEqual(FirmaPng)) return false;
        var p = 8; var ihdr = false; var iend = false; var datosIdat = new MemoryStream();
        int profundidad = 0, color = 0, entrelazado = 0;
        while (p + 12 <= b.Length)
        {
            var len = BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(p));
            if (len > int.MaxValue || p + 12L + len > b.Length) return false;
            var tipo = b.AsSpan(p + 4, 4); var cuerpo = b.AsSpan(p + 8, (int)len);
            if (Crc32(b.AsSpan(p + 4, 4 + (int)len)) != BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(p + 8 + (int)len))) return false;
            if (!ihdr)
            {
                if (!tipo.SequenceEqual("IHDR"u8) || len != 13) return false;
                var w = BinaryPrimitives.ReadUInt32BigEndian(cuerpo); var h = BinaryPrimitives.ReadUInt32BigEndian(cuerpo[4..]);
                if (!Dimensiones(w, h)) return false;
                ancho = (int)w; alto = (int)h; profundidad = cuerpo[8]; color = cuerpo[9]; entrelazado = cuerpo[12];
                if (cuerpo[10] != 0 || cuerpo[11] != 0 || entrelazado > 1 || !ProfundidadValida(color, profundidad)) return false;
                ihdr = true;
            }
            else if (tipo.SequenceEqual("IDAT"u8)) datosIdat.Write(cuerpo);
            else if (tipo.SequenceEqual("IEND"u8)) { if (len != 0 || p + 12 != b.Length) return false; iend = true; break; }
            p += 12 + (int)len;
        }
        if (!ihdr || !iend || datosIdat.Length == 0) return false;
        // Los datos deben descomprimir exactamente al tamaño que piden las dimensiones (sin entrelazado; con él solo se exige que descompriman).
        var canales = color switch { 0 => 1, 2 => 3, 3 => 1, 4 => 2, _ => 4 };
        var bytesFila = ((long)ancho * canales * profundidad + 7) / 8;
        var esperado = entrelazado == 0 ? alto * (bytesFila + 1) : -1L;
        datosIdat.Position = 0;
        try
        {
            using var z = new ZLibStream(datosIdat, CompressionMode.Decompress);
            var buffer = new byte[81920]; long total = 0; int n;
            var tope = esperado >= 0 ? esperado : (long)ancho * alto * 8 + alto * 16L;
            while ((n = z.Read(buffer)) > 0) { total += n; if (total > tope) return false; }
            return esperado < 0 || total == esperado;
        }
        catch (InvalidDataException) { return false; }
    }

    private static bool ProfundidadValida(int color, int d) => color switch
    {
        0 => d is 1 or 2 or 4 or 8 or 16,
        3 => d is 1 or 2 or 4 or 8,
        2 or 4 or 6 => d is 8 or 16,
        _ => false,
    };

    // ---- JPEG ----
    private static bool Jpeg(byte[] b, out int ancho, out int alto)
    {
        ancho = alto = 0;
        if (b.Length < 20 || b[0] != 0xFF || b[1] != 0xD8) return false;
        var p = 2; var sof = false; var sos = false;
        while (p < b.Length)
        {
            if (b[p] != 0xFF) return false;
            while (p < b.Length && b[p] == 0xFF) p++;
            if (p >= b.Length) return false;
            var marcador = b[p++];
            if (marcador == 0xD9) return false;                                            // EOI antes de los datos de la imagen
            if (marcador == 0x00 || marcador == 0x01 || (marcador >= 0xD0 && marcador <= 0xD8)) continue;   // sin longitud
            if (p + 2 > b.Length) return false;
            var len = BinaryPrimitives.ReadUInt16BigEndian(b.AsSpan(p));
            if (len < 2 || p + len > b.Length) return false;
            if (marcador is >= 0xC0 and <= 0xCF && marcador is not (0xC4 or 0xC8 or 0xCC))
            {
                if (len < 8) return false;
                int h = BinaryPrimitives.ReadUInt16BigEndian(b.AsSpan(p + 3)), w = BinaryPrimitives.ReadUInt16BigEndian(b.AsSpan(p + 5));
                if (!Dimensiones(w, h) || b[p + 7] is 0 or > 4) return false;
                ancho = w; alto = h; sof = true;
            }
            if (marcador == 0xDA) { sos = true; p += len; break; }
            p += len;
        }
        if (!sof || !sos || p >= b.Length) return false;
        var fin = b.Length; while (fin > 0 && b[fin - 1] == 0) fin--;                      // el relleno de ceros al final es válido
        return fin - 2 >= p && b[fin - 2] == 0xFF && b[fin - 1] == 0xD9;
    }
}
