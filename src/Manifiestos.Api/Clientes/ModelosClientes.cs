namespace Manifiestos.Api.Clientes;

// ---- Respuestas (JSON camelCase) ----

/// <summary>Parada cuya última revisión incluye al cliente, con los baños planeados (null = servicio sin baños).</summary>
public sealed record AsignacionCliente(Guid RutaId, string RutaNombre, Guid ParadaId, int Posicion, int? Banos, int? Fosas = null, Guid? SitioId = null);

public sealed record SitioCliente(Guid SitioId, int Revision, int RevisionFicha,
    string? Nombre, string? Calle, string? NumeroExterior, string? Colonia, string? CodigoPostal,
    string? Municipio, string? Estado, int? Banos, int? Fosas, bool EnRuta = false);

public sealed record SolicitudSitioCliente(Guid? SitioId, int? Revision, int? RevisionFicha,
    string? Nombre, string? Calle, string? NumeroExterior, string? Colonia, string? CodigoPostal,
    string? Municipio, string? Estado, int? Banos, int? Fosas, bool Eliminar = false);
public sealed record SolicitudGuardarSitios(SolicitudSitioCliente[]? Sitios);

/// <summary>
/// Cliente con los datos de la última ficha de su empresa. Revision = revisión de esa ficha (0 si no tiene).
/// Autorizacion = número de la autorización vigente 'Medio ambiente'/'Generador' (null = cliente general).
/// </summary>
public sealed record ClienteResumen(
    Guid Id, Guid SucursalId, int Revision, string Nombre,
    string? NombreFiscal, string? NombreComercial, string? Rfc,
    string? Calle, string? NumeroExterior, string? Colonia, string? CodigoPostal, string? Municipio, string? Estado,
    string? Telefono, string? Correo, string? Autorizacion,
    IReadOnlyList<AsignacionCliente> Asignaciones, int Banos, int? Fosas = null, IReadOnlyList<SitioCliente>? Sitios = null);

// ---- Solicitudes ----

public sealed record SolicitudNuevoCliente(
    Guid? SucursalId, string? NombreFiscal, string? NombreComercial, string? Rfc,
    string? Calle, string? NumeroExterior, string? Colonia, string? CodigoPostal, string? Municipio, string? Estado,
    string? Telefono, string? Correo, string? Autorizacion, SolicitudSitioCliente[]? Sitios = null);

/// <summary>
/// Revision = revisión de la ficha que se editó. AutorizacionVista (opcional) = número de autorización
/// vigente que vio quien edita ("" si el cliente era general). Cambiar solo la autorización no sube la
/// revisión de la ficha, así que el panel la envía para que el servidor responda 409 si alguien más la
/// cambió mientras tanto. Si se omite (null), no se comprueba.
/// </summary>
public sealed record SolicitudEditarCliente(
    string? NombreFiscal, string? NombreComercial, string? Rfc,
    string? Calle, string? NumeroExterior, string? Colonia, string? CodigoPostal, string? Municipio, string? Estado,
    string? Telefono, string? Correo, string? Autorizacion, int? Revision, string? AutorizacionVista = null, SolicitudSitioCliente[]? Sitios = null);

// ---- Datos ya validados (recortados; vacío = null) ----

public sealed record DatosCliente(
    string? NombreFiscal, string? NombreComercial, string? Rfc,
    string? Calle, string? NumeroExterior, string? Colonia, string? CodigoPostal, string? Municipio, string? Estado,
    string? Telefono, string? Correo, string? Autorizacion, SolicitudSitioCliente[]? Sitios = null);
