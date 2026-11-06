using Manifiestos.Api.Rutas;

namespace Manifiestos.Api.Unidades;

// ---- Respuestas (JSON camelCase) ----

public sealed record CapacidadOpcion(short Id, decimal ValorM3);
public sealed record RutaDeUnidad(Guid Id, string Nombre);

/// <summary>Última ficha de la unidad. Revisión 0 = la unidad todavía no tiene ficha.</summary>
public sealed record UnidadFicha(
    Guid Id, Guid SucursalId, int Revision, string? NumeroEconomico, string? Placas, string? Marca, string? Tipo,
    short? CapacidadId, decimal? CapacidadM3, string? Clasificacion, string? Descripcion,
    OperadorResumen? Operador, IReadOnlyList<RutaDeUnidad> Rutas);

public sealed record ListaUnidades(IReadOnlyList<UnidadFicha> Unidades, IReadOnlyList<CapacidadOpcion> Capacidades);

// ---- Solicitudes ----

public sealed record SolicitudNuevaUnidad(
    Guid? SucursalId, string? NumeroEconomico, string? Placas, string? Marca, string? Tipo,
    short? CapacidadId, string? Clasificacion, string? Descripcion);

public sealed record SolicitudEditarUnidad(
    string? NumeroEconomico, string? Placas, string? Marca, string? Tipo,
    short? CapacidadId, string? Clasificacion, string? Descripcion, int? Revision);

// ---- Datos ya validados para el repositorio ----

public sealed record DatosUnidad(
    string NumeroEconomico, string? Placas, string? Marca, string? Tipo,
    short CapacidadId, string? Clasificacion, string? Descripcion);
