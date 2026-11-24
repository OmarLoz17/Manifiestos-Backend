// GENERADO por docs/legal/generar-contenido-web.py desde docs/legal/*.md. No se edita a mano.
// La versión es la fecha de vigencia y la huella el SHA-256 del texto fuente: lo que se acepta queda atado a un texto exacto.
namespace Manifiestos.Api.Legal;

public sealed record DocumentoLegalVigente(string Id, string Titulo, string Version, string Huella);

public static class DocumentosLegalesVigentes
{
    public static readonly IReadOnlyList<DocumentoLegalVigente> Todos =
    [
        new("terminos", "Términos y condiciones", "2026-11-24", "95f16d735ccb042aaf53fdf2b9504c64add60eac4da2af63ad8e9e1ef6fdf469"),
        new("privacidad", "Aviso de privacidad", "2026-11-24", "e424cb3b57e7640c46eca86ef13cf55046f0823fc4bb024bc0b5f7e410957d4a"),
    ];
}
