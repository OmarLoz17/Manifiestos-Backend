using Manifiestos.Api.Rutas;

namespace Manifiestos.Api.Unidades;

// ---- Respuestas (JSON camelCase) ----

public sealed record CapacidadOpcion(short Id, decimal ValorM3);
public sealed record RutaDeUnidad(Guid Id, string Nombre, IReadOnlyList<int> Dias);

/// <summary>Última ficha de la unidad. Revisión 0 = la unidad todavía no tiene ficha.</summary>
public sealed record UnidadFicha(
    Guid Id, Guid SucursalId, int Revision, string? NumeroEconomico, string? Placas, string? Marca, string? Tipo,
    short? CapacidadId, decimal? CapacidadM3, string? Clasificacion, string? Descripcion,
    OperadorResumen? Operador, IReadOnlyList<RutaDeUnidad> Rutas, int AsignacionRevision, IReadOnlyList<string> Avisos);

public sealed record ListaUnidades(IReadOnlyList<UnidadFicha> Unidades, IReadOnlyList<CapacidadOpcion> Capacidades);

// ---- Solicitudes ----

public sealed record SolicitudNuevaUnidad(
    Guid? SucursalId, string? NumeroEconomico, string? Placas, string? Marca, string? Tipo,
    short? CapacidadId, string? Clasificacion, string? Descripcion);

public sealed record SolicitudEditarUnidad(
    string? NumeroEconomico, string? Placas, string? Marca, string? Tipo,
    short? CapacidadId, string? Clasificacion, string? Descripcion, int? Revision);

/// <summary>Operador de la unidad. OperadorId null = quitar al operador. Revision = revisión de la asignación que se vio (0 = no había).</summary>
public sealed record SolicitudOperadorUnidad(Guid? OperadorId, int? Revision);

// ---- Datos ya validados para el repositorio ----

public sealed record DatosUnidad(
    string NumeroEconomico, string? Placas, string? Marca, string? Tipo,
    short CapacidadId, string? Clasificacion, string? Descripcion);
