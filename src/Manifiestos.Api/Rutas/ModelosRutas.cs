namespace Manifiestos.Api.Rutas;

// ---- Respuestas (JSON camelCase) ----

/// <summary>Clasificacion: 'sanitarios' | 'fosas' | null (unidad sin clasificar).</summary>
public sealed record UnidadResumen(Guid Id, string? NumeroEconomico, decimal? CapacidadM3, string? Clasificacion = null);
public sealed record OperadorResumen(Guid Id, string Nombre);
public sealed record RutaReferencia(Guid Id, string Nombre);

/// <summary>Baños succionados por día desde la última tirada (solo informativo; fecha ISO yyyy-MM-dd).</summary>
public sealed record DesgloseCarga(string Fecha, int Banos);

/// <summary>
/// Carga actual de la UNIDAD (no de la ruta): baños succionados desde su última tirada, en m³ estimados
/// (baños × 16 L / 1,000; estimación, no medición). El porcentaje no tiene tope y puede pasar de 100.
/// </summary>
public sealed record CargaUnidad(
    Guid UnidadId, decimal CapacidadM3, int Banos, decimal M3, decimal Porcentaje,
    DateTimeOffset? UltimaTiradaEn, IReadOnlyList<DesgloseCarga> Desglose);

/// <summary>Órdenes de fosas por tirar por día (fecha ISO yyyy-MM-dd de la jornada en que se succionaron).</summary>
public sealed record DesgloseFosas(string Fecha, int Ordenes);

/// <summary>
/// Carga por tirar de la UNIDAD de fosas: órdenes (vueltas) succionadas (captura SERVICIO_OPERADOR) cuya descarga en planta
/// (RECEPCION_PLANTA) aún no llega, aunque sean de días anteriores. Cada orden llena la unidad: M3 = Ordenes × CapacidadM3
/// (estimación por regla, no medición) y Porcentaje = Ordenes × 100 (sin tope). UltimaDescargaEn = última recepción en planta.
/// </summary>
/// Una unidad de fosas también puede hacer baños (parada por parada): los baños succionados que aún no se tiran suman a la carga con su estimado de 16 L cada
/// uno (Banos), así que M3 y Porcentaje incluyen órdenes y baños (Ordenes = solo órdenes de fosa).
public sealed record CargaFosas(
    Guid UnidadId, decimal CapacidadM3, int Ordenes, decimal M3, decimal Porcentaje,
    DateTimeOffset? UltimaDescargaEn, IReadOnlyList<DesgloseFosas> Desglose, int Banos = 0);

/// <summary>
/// Última revisión de la ruta, sin el detalle de paradas. Servicio = clasificación de su unidad. Terciado = LMV / MJS
/// (u otras iniciales de los días). Banos solo en sanitarios; Ordenes y VolumenEstimadoM3 solo en fosas (el volumen es
/// una estimación por regla —capacidad de la unidad llena por orden de fosa fija—, no una medición). Carga solo en sanitarios;
/// CargaFosas solo en rutas de unidades de fosas (null en sanitarios).
/// </summary>
public sealed record RutaResumen(
    Guid Id, Guid SucursalId, int Revision, string Nombre, IReadOnlyList<int> Dias,
    UnidadResumen Unidad, OperadorResumen? Operador, int Paradas, int Clientes, int Banos,
    string? Servicio = null, string? Terciado = null, int? Ordenes = null, CargaUnidad? Carga = null,
    decimal? VolumenEstimadoM3 = null, IReadOnlyList<string>? ClientesNombres = null, CargaFosas? CargaFosas = null);

public sealed record SitioDetalle(Guid Id, string? Nombre, string? Direccion, string? Referencia);
/// <summary>Ordenes = fosas planeadas del cliente en la parada (la orden de fosa fija es una vuelta); 0 en sanitarios.</summary>
public sealed record ClienteParada(Guid ClienteId, string Nombre, int? Banos, int? Fosas = null, bool Autorizado = false, int Ordenes = 0);
/// <summary>Tipo: 'sanitarios' | 'fosa_fija' | 'fosa_propia' (null en rutas sin clasificar).</summary>
public sealed record ParadaDetalle(
    Guid Id, int Posicion, int Revision, SitioDetalle Sitio, IReadOnlyList<ClienteParada> Clientes, int Banos,
    string? Tipo = null, int Ordenes = 0);

public sealed record UnidadOpcion(Guid Id, string? NumeroEconomico, decimal? CapacidadM3, Guid? OperadorId, string? OperadorNombre, string? Clasificacion = null);
public sealed record OperadorOpcion(Guid Id, string Nombre);
public sealed record ClienteOpcion(Guid Id, string Nombre, bool Autorizado = false);
public sealed record ClienteSitioOpcion(Guid ClienteId, int? Banos, int? Fosas);
/// <summary>Libre = sitio normal (cualquier cliente general); si no, Clientes = el cliente autorizado dueño del sitio.</summary>
public sealed record PuntoServicioOpcion(Guid Id, string? Nombre, string? Direccion, IReadOnlyList<ClienteSitioOpcion>? Clientes = null, bool Libre = false);
/// <summary>Sitio ofrecido para una ruta del servicio pedido; RutaActual = una ruta vigente de ese servicio que ya lo tiene (informativo: el mismo sitio puede estar en varias rutas).</summary>
/// <summary>Propietario = cliente autorizado dueño del sitio (null = sitio normal, para clientes generales).</summary>
/// <summary>RutaActual = la primera ruta donde está el sitio; Rutas = TODAS (un sitio puede estar en varias rutas a la vez, PG-25).</summary>
public sealed record SitioOpcion(Guid Id, string? Nombre, string? Direccion, ClienteOpcion? Propietario, RutaReferencia? RutaActual, IReadOnlyList<RutaReferencia>? Rutas = null);
public sealed record ClienteGeneralOpcion(Guid Id, string Nombre, RutaReferencia? RutaActual);
public sealed record OpcionesRuta(
    IReadOnlyList<UnidadOpcion> Unidades, IReadOnlyList<OperadorOpcion> Operadores,
    IReadOnlyList<ClienteOpcion> Clientes, IReadOnlyList<PuntoServicioOpcion> PuntosServicio,
    IReadOnlyList<SitioOpcion>? Sitios = null, IReadOnlyList<ClienteGeneralOpcion>? ClientesGenerales = null);

public sealed record RevisionGuardada(Guid Id, int Revision);

// ---- Solicitudes ----

/// <summary>Nombre ya no se acepta: el servidor lo genera (número económico + terciado); si llega se ignora.</summary>
public sealed record SolicitudNuevaRuta(Guid? SucursalId, string? Nombre, int[]? Dias, Guid? UnidadId, Guid? OperadorId);
public sealed record SolicitudEditarRuta(string? Nombre, int[]? Dias, Guid? UnidadId, Guid? OperadorId, int? Revision);
public sealed record SolicitudNuevoSitio(
    string? Nombre, string? Calle, string? Colonia, string? Municipio, string? Estado, string? CodigoPostal, string? Referencia);
public sealed record SolicitudClienteParada(Guid? ClienteId, int? Banos, int? Fosas = null);
/// <summary>
/// Alta de parada. Contrato nuevo: solo SitioId (+ Reasignar) → parada sin clientes. Contrato anterior (compatibilidad):
/// SitioId|NuevoSitio + Clientes[] completos, que sigue siendo la forma de editar una parada entera (PUT).
/// </summary>
public sealed record SolicitudParada(Guid? SitioId, SolicitudNuevoSitio? NuevoSitio, SolicitudClienteParada[]? Clientes, int? Revision, bool? Reasignar = null);
public sealed record SolicitudAgregarCliente(Guid? ClienteId, int? Banos, int? Ordenes, bool? Reasignar, int? Revision);
public sealed record SolicitudEditarClienteParada(int? Banos, int? Ordenes, int? Revision);
public sealed record SolicitudOrden(Guid[]? Paradas);

// ---- Datos ya validados para el repositorio ----

/// <summary>El nombre de la ruta no se recibe: el repositorio lo genera con el número económico vigente y el terciado.</summary>
public sealed record DatosRuta(short Dias, Guid UnidadId, Guid? OperadorId);
public sealed record DatosNuevoSitio(string Nombre, string? Calle, string? Colonia, string? Municipio, string? Estado, string? CodigoPostal, string? Referencia);
public sealed record DatosClienteParada(Guid ClienteId, int? Banos, int? Fosas = null);
public sealed record DatosParada(Guid? SitioId, DatosNuevoSitio? NuevoSitio, IReadOnlyList<DatosClienteParada> Clientes);

public enum EstadoEscritura { Correcta, NoEncontrada, SinPermiso, Conflicto, Invalida, Regla }

/// <summary>
/// Codigo = código estable del error de negocio ({"error": codigo}); RutaActual = ruta que ya tiene el sitio o cliente
/// (solo en conflictos de reasignación). Regla = incumple una regla de negocio (422).
/// </summary>
public sealed record ResultadoEscritura(
    EstadoEscritura Estado, Guid Id = default, int Revision = 0, string? Mensaje = null,
    string? Codigo = null, RutaReferencia? RutaActual = null)
{
    public static ResultadoEscritura Correcta(Guid id, int revision) => new(EstadoEscritura.Correcta, id, revision);
    public static readonly ResultadoEscritura NoEncontrada = new(EstadoEscritura.NoEncontrada);
    public static readonly ResultadoEscritura SinPermiso = new(EstadoEscritura.SinPermiso);
    public static ResultadoEscritura Conflicto(string mensaje, string? codigo = null, RutaReferencia? rutaActual = null) =>
        new(EstadoEscritura.Conflicto, Mensaje: mensaje, Codigo: codigo, RutaActual: rutaActual);
    public static ResultadoEscritura Invalida(string mensaje) => new(EstadoEscritura.Invalida, Mensaje: mensaje);
    public static ResultadoEscritura Regla(string codigo, string mensaje) => new(EstadoEscritura.Regla, Mensaje: mensaje, Codigo: codigo);
}

/// <summary>Días de la semana: 1 = lunes … 7 = domingo; en la base, máscara lunes = 1&lt;&lt;0 … domingo = 1&lt;&lt;6.</summary>
public static class DiasSemana
{
    // Iniciales: lunes L, martes M, miércoles M, jueves J, viernes V, sábado S, domingo D. Los terciados habituales son
    // LMV (lunes, miércoles, viernes = 21) y MJS (martes, jueves, sábado = 42); otros conjuntos se derivan igual.
    private const string Iniciales = "LMMJVSD";

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

    /// <summary>Iniciales de los días en orden de la semana: 21 → "LMV", 42 → "MJS".</summary>
    public static string Terciado(short mascara) => mascara switch
    {
        21 => "LMV",
        42 => "MJS",
        // Otros conjuntos: letras sin ambigüedad (martes Ma, miércoles Mi), igual que la vista previa del panel (decisión 12-nov-2026).
        _ => Desambiguado(mascara),
    };

    /// <summary>Días sin ambigüedad (L, Ma, Mi, J, V, S, D) para distinguir rutas de la misma unidad con igual terciado.</summary>
    public static string Desambiguado(short mascara) =>
        string.Concat(Enumerable.Range(1, 7).Where(d => (mascara & (1 << (d - 1))) != 0).Select(d => new[] { "L", "Ma", "Mi", "J", "V", "S", "D" }[d - 1]));

    /// <summary>Nombre de la ruta: número económico de la unidad + terciado (p. ej. ARM-111-LMV).</summary>
    public static string NombreRuta(string numeroEconomico, short mascara) => $"{numeroEconomico.Trim()}-{Terciado(mascara)}";
}
