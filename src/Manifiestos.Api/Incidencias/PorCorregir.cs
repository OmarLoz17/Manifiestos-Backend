using Manifiestos.Api.Acceso;
using Manifiestos.Api.Datos;
using Npgsql;

namespace Manifiestos.Api.Incidencias;

public sealed record SolicitudCorreccion(string? Accion, Guid? ParadaId);
public sealed record SolicitudSinUbicar(string? Accion, Guid? OrdenId, Guid? ParadaId);

/// <summary>
/// Pestaña «Por corregir» de Incidencias (migración 0066): servicios de sanitarios cuya foto se tomó junto a otra parada de la ruta.
/// La lista y la decisión viven en funciones SQL que revisan el permiso de la cuenta (lectura para ver, escritura para decidir).
/// </summary>
public static class PorCorregir
{
    public static void MapearPorCorregir(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/incidencias/por-corregir");
        g.MapGet("", async (HttpContext http, NpgsqlDataSource datos) =>
        {
            if (!ServiciosAcceso.LeerIds(http.User, out var org, out var acceso)) return Results.Unauthorized();
            http.Response.Headers.CacheControl = "no-store";
            await using var tx = await TransaccionContexto.AbrirAsync(datos, org, acceso, http.RequestAborted);
            await using var cmd = tx.Comando("SELECT seguridad.por_corregir_web($1, $2)::text", org, acceso);
            return Results.Text((string)(await cmd.ExecuteScalarAsync(http.RequestAborted))!, "application/json");
        });
        // «No se pudo ubicar» (migración 0068): lo que el servidor recibió pero no logró ligar con el plan. Se puede marcar como corregido o asignar a una orden/parada.
        g.MapGet("/sin-ubicar", async (HttpContext http, NpgsqlDataSource datos) =>
        {
            if (!ServiciosAcceso.LeerIds(http.User, out var org, out var acceso)) return Results.Unauthorized();
            http.Response.Headers.CacheControl = "no-store";
            await using var tx = await TransaccionContexto.AbrirAsync(datos, org, acceso, http.RequestAborted);
            await using var cmd = tx.Comando("SELECT seguridad.sin_ubicar_web($1, $2)::text", org, acceso);
            return Results.Text((string)(await cmd.ExecuteScalarAsync(http.RequestAborted))!, "application/json");
        });
        g.MapPost("/sin-ubicar/{id:guid}/resolver", async (Guid id, SolicitudSinUbicar s, HttpContext http, NpgsqlDataSource datos) =>
        {
            if (!ServiciosAcceso.LeerIds(http.User, out var org, out var acceso)) return Results.Unauthorized();
            if (s.Accion is not ("corregido" or "asignar")) return Results.BadRequest(new MensajeError("La acción debe ser corregido o asignar."));
            await using var tx = await TransaccionContexto.AbrirAsync(datos, org, acceso, http.RequestAborted);
            await using var cmd = tx.Comando("SELECT seguridad.resolver_sin_ubicar_web($1, $2, $3, $4, $5, $6)", org, acceso, id, s.Accion, (object?)s.OrdenId ?? DBNull.Value, (object?)s.ParadaId ?? DBNull.Value);
            var estado = (string)(await cmd.ExecuteScalarAsync(http.RequestAborted))!;
            if (estado == "ok") await tx.ConfirmarAsync(http.RequestAborted);
            return estado switch
            {
                "ok" => Results.NoContent(),
                "sin_permiso" => Results.Json(new MensajeError("No tienes permiso para corregir registros."), statusCode: StatusCodes.Status403Forbidden),
                "no_encontrada" => Results.Json(new MensajeError("No se encontró el registro."), statusCode: StatusCodes.Status404NotFound),
                "ya_resuelto" => Results.Json(new MensajeError("Alguien más ya resolvió este registro. Recarga para ver el cambio."), statusCode: StatusCodes.Status409Conflict),
                "destino_ocupado" => Results.Json(new MensajeError("Esa parada ya tiene su servicio registrado."), statusCode: StatusCodes.Status409Conflict),
                "no_se_puede_asignar" => Results.Json(new MensajeError("Este registro no se puede asignar; márcalo como corregido."), statusCode: StatusCodes.Status409Conflict),
                _ => Results.BadRequest(new MensajeError("Elige una orden o parada de la misma ruta.")),
            };
        }).AddEndpointFilter(EndpointsAcceso.ValidarCsrf);
        g.MapPost("/{id:guid}/resolver", async (Guid id, SolicitudCorreccion s, HttpContext http, NpgsqlDataSource datos) =>
        {
            if (!ServiciosAcceso.LeerIds(http.User, out var org, out var acceso)) return Results.Unauthorized();
            if (s.Accion is not ("mover" or "dejar")) return Results.BadRequest(new MensajeError("La acción debe ser mover o dejar."));
            await using var tx = await TransaccionContexto.AbrirAsync(datos, org, acceso, http.RequestAborted);
            await using var cmd = tx.Comando("SELECT seguridad.corregir_captura_web($1, $2, $3, $4, $5)", org, acceso, id, s.Accion, (object?)s.ParadaId ?? DBNull.Value);
            var estado = (string)(await cmd.ExecuteScalarAsync(http.RequestAborted))!;
            if (estado == "ok") await tx.ConfirmarAsync(http.RequestAborted);
            return estado switch
            {
                "ok" => Results.NoContent(),
                "sin_permiso" => Results.Json(new MensajeError("No tienes permiso para corregir registros."), statusCode: StatusCodes.Status403Forbidden),
                "no_encontrada" => Results.Json(new MensajeError("No se encontró el registro."), statusCode: StatusCodes.Status404NotFound),
                "ya_resuelto" => Results.Json(new MensajeError("Alguien más ya resolvió este registro. Recarga para ver el cambio."), statusCode: StatusCodes.Status409Conflict),
                "ya_en_manifiesto" => Results.Json(new MensajeError("Este servicio ya está en un manifiesto: no se puede mover."), statusCode: StatusCodes.Status409Conflict),
                "destino_ocupado" => Results.Json(new MensajeError("Esa parada ya tiene su servicio registrado."), statusCode: StatusCodes.Status409Conflict),
                _ => Results.BadRequest(new MensajeError("Elige una parada de la misma ruta.")),
            };
        }).AddEndpointFilter(EndpointsAcceso.ValidarCsrf);
    }
}
