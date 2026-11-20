using System.IO.Compression;
using Manifiestos.Api.Acceso;

namespace Manifiestos.Api.Tests;

/// <summary>Imágenes mínimas pero VÁLIDAS (el servidor ya no acepta cualquier cosa que empiece con la firma del formato). `semilla` y `relleno` hacen distinto el contenido.</summary>
internal static class ImagenesDePrueba
{
    public static byte[] Jpeg(int semilla = 0, int relleno = 0)
    {
        var s = new MemoryStream(); var azar = new Random(semilla + 1);
        s.Write([0xFF, 0xD8]);
        for (var restante = relleno; restante > 0;)
        {
            var n = Math.Min(restante, 60_000); var datos = new byte[n]; azar.NextBytes(datos);
            s.Write([0xFF, 0xFE, (byte)((n + 2) >> 8), (byte)((n + 2) & 0xFF)]); s.Write(datos); restante -= n;
        }
        s.Write([0xFF, 0xC0, 0x00, 0x11, 0x08, 0x00, 0x08, 0x00, 0x08, 0x03, 0x01, 0x22, 0x00, 0x02, 0x11, 0x01, 0x03, 0x11, 0x01]);
        s.Write([0xFF, 0xDA, 0x00, 0x0C, 0x03, 0x01, 0x00, 0x02, 0x11, 0x03, 0x11, 0x00, 0x3F, 0x00]);
        s.Write([0x12, 0x34, (byte)(semilla & 0x7F), 0x56]);
        s.Write([0xFF, 0xD9]);
        return s.ToArray();
    }

    public static byte[] Png(int semilla = 0, bool crcMalo = false)
    {
        var s = new MemoryStream();
        s.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        Bloque(s, "IHDR", [0, 0, 0, 8, 0, 0, 0, 8, 8, 0, 0, 0, 0], false);
        Bloque(s, "tEXt", System.Text.Encoding.ASCII.GetBytes("Comment\0semilla-" + semilla), false);
        var crudo = new MemoryStream();
        using (var z = new ZLibStream(crudo, CompressionLevel.SmallestSize, leaveOpen: true)) z.Write(new byte[8 * (1 + 8)]);
        Bloque(s, "IDAT", crudo.ToArray(), crcMalo);
        Bloque(s, "IEND", [], false);
        return s.ToArray();
    }

    private static void Bloque(Stream s, string tipo, byte[] datos, bool crcMalo)
    {
        var n = datos.Length; s.Write([(byte)(n >> 24), (byte)(n >> 16), (byte)(n >> 8), (byte)n]);
        var cuerpo = System.Text.Encoding.ASCII.GetBytes(tipo).Concat(datos).ToArray(); s.Write(cuerpo);
        var crc = ValidadorImagen.Crc32(cuerpo) ^ (crcMalo ? 1u : 0u);
        s.Write([(byte)(crc >> 24), (byte)(crc >> 16), (byte)(crc >> 8), (byte)crc]);
    }
}
