namespace Manifiestos.Api.Rutas;

// ---- Respuestas (JSON camelCase) ----

public sealed record UnidadResumen(Guid Id, string? NumeroEconomico, decimal? CapacidadM3);
public sealed record OperadorResumen(Guid Id, string Nombre);

/// <summary>Última revisión de la ruta, sin el detalle de paradas.</summary>
public sealed record RutaResumen(
    Guid Id, Guid SucursalId, int Revision, string Nombre, IReadOnlyList<int> Dias,
    UnidadResumen Unidad, OperadorResumen? Operador, int Paradas, int Clientes, int Banos);

public sealed record SitioDetalle(Guid Id, string? Nombre, string? Direccion, string? Referencia);
public sealed record ClienteParada(Guid ClienteId, string Nombre, int? Banos, int? Fosas = null);
public sealed record ParadaDetalle(Guid Id, int Posicion, int Revision, SitioDetalle Sitio, IReadOnlyList<ClienteParada> Clientes, int Banos);

public sealed record UnidadOpcion(Guid Id, string? NumeroEconomico, decimal? CapacidadM3, Guid? OperadorId, string? OperadorNombre);
public sealed record OperadorOpcion(Guid Id, string Nombre);
public sealed record ClienteOpcion(Guid Id, string Nombre);
public sealed record ClienteSitioOpcion(Guid ClienteId, int? Banos, int? Fosas);
public sealed record PuntoServicioOpcion(Guid Id, string? Nombre, string? Direccion, IReadOnlyList<ClienteSitioOpcion>? Clientes = null);
public sealed record OpcionesRuta(
    IReadOnlyList<UnidadOpcion> Unidades, IReadOnlyList<OperadorOpcion> Operadores,
    IReadOnlyList<ClienteOpcion> Clientes, IReadOnlyList<PuntoServicioOpcion> PuntosServicio);

public sealed record RevisionGuardada(Guid Id, int Revision);

// ---- Solicitudes ----

public sealed record SolicitudNuevaRuta(Guid? SucursalId, string? Nombre, int[]? Dias, Guid? UnidadId, Guid? OperadorId);
public sealed record SolicitudEditarRuta(string? Nombre, int[]? Dias, Guid? UnidadId, Guid? OperadorId, int? Revision);
public sealed record SolicitudNuevoSitio(
    string? Nombre, string? Calle, string? Colonia, string? Municipio, string? Estado, string? CodigoPostal, string? Referencia);
public sealed record SolicitudClienteParada(Guid? ClienteId, int? Banos, int? Fosas = null);
public sealed record SolicitudParada(Guid? SitioId, SolicitudNuevoSitio? NuevoSitio, SolicitudClienteParada[]? Clientes, int? Revision);
public sealed record SolicitudOrden(Guid[]? Paradas);

// ---- Datos ya validados para el repositorio ----

public sealed record DatosRuta(string Nombre, short Dias, Guid UnidadId, Guid? OperadorId);
public sealed record DatosNuevoSitio(string Nombre, string? Calle, string? Colonia, string? Municipio, string? Estado, string? CodigoPostal, string? Referencia);
public sealed record DatosClienteParada(Guid ClienteId, int? Banos, int? Fosas = null);
public sealed record DatosParada(Guid? SitioId, DatosNuevoSitio? NuevoSitio, IReadOnlyList<DatosClienteParada> Clientes);

public enum EstadoEscritura { Correcta, NoEncontrada, SinPermiso, Conflicto, Invalida }

public sealed record ResultadoEscritura(EstadoEscritura Estado, Guid Id = default, int Revision = 0, string? Mensaje = null)
{
    public static ResultadoEscritura Correcta(Guid id, int revision) => new(EstadoEscritura.Correcta, id, revision);
    public static readonly ResultadoEscritura NoEncontrada = new(EstadoEscritura.NoEncontrada);
    public static readonly ResultadoEscritura SinPermiso = new(EstadoEscritura.SinPermiso);
    public static ResultadoEscritura Conflicto(string mensaje) => new(EstadoEscritura.Conflicto, Mensaje: mensaje);
    public static ResultadoEscritura Invalida(string mensaje) => new(EstadoEscritura.Invalida, Mensaje: mensaje);
}

/// <summary>Días de la semana: 1 = lunes … 7 = domingo; en la base, máscara lunes = 1&lt;&lt;0 … domingo = 1&lt;&lt;6.</summary>
public static class DiasSemana
{
    public static bool TryMascara(int[]? dias, out short mascara)
    {
        mascara = 0;
        if (dias is null || dias.Length == 0) return false;
        foreach (var d in dias)
        {
            if (d is < 1 or > 7) return false;
            mascara |= (short)(1 << (d - 1));
        }
        return true;
    }

    public static IReadOnlyList<int> Lista(short mascara) =>
        Enumerable.Range(1, 7).Where(d => (mascara & (1 << (d - 1))) != 0).ToList();
}
