using Manifiestos.Api;
using Manifiestos.Api.Archivos;
using Manifiestos.Api.Acceso;
using Manifiestos.Api.Clientes;
using Manifiestos.Api.Rutas;
using Manifiestos.Api.Geografia;
using Manifiestos.Api.Incidencias;
using Manifiestos.Api.Jornadas;
using Manifiestos.Api.Documentos;
using Manifiestos.Api.Operadores;
using Manifiestos.Api.Avisos;
using Manifiestos.Api.Ordenes;
using Manifiestos.Api.Registro;
using Manifiestos.Api.Sitios;
using Manifiestos.Api.Unidades;

var builder = WebApplication.CreateBuilder(args);
builder.AgregarSecretosEnArchivos();
builder.AgregarRegistroEnArchivo();
builder.AgregarClavesDeSesionPersistentes();

builder.Services.AgregarProxyConfiable(builder.Configuration);
builder.Services.AgregarAccesoWeb();
builder.Services.AgregarAccesoMovil();
builder.Services.AgregarRutas();
builder.Services.AgregarClientes();
builder.Services.AgregarSitios();
builder.Services.AgregarPlantas();
builder.Services.AgregarGeografia();
builder.Services.AgregarUnidades();
builder.Services.AgregarOperadores();
builder.Services.AgregarManifiestos();
builder.Services.AgregarValidacionManifiestos();
builder.Services.AgregarIncidencias();
builder.Services.AgregarJornadas();
builder.Services.AgregarOrdenes();
builder.Services.AgregarAvisosPush(builder.Configuration);
builder.Services.AgregarDrive(builder.Configuration);

var app = builder.Build();

// En producción la llave del QR debe existir al arrancar: una llave nueva invalidaría todos los QR impresos, y sin esta línea
// la ausencia solo se descubriría con la primera emisión.
if (app.Environment.IsProduction()) _ = app.Services.GetRequiredService<Manifiestos.Api.Documentos.LlaveValidacion>();

app.UseForwardedHeaders();
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();

app.MapearSalud();
app.MapearAcceso();
app.MapearAccesoMovil();
app.MapearCapturasMovil();
app.MapearRutas();
app.MapearClientes();
app.MapearSitios();
app.MapearPlantas();
app.MapearGeografia();
app.MapearUnidades();
app.MapearOperadores();
app.MapearManifiestos();
app.MapearValidacion();
app.MapearIncidencias();
app.MapearPorCorregir();
app.MapearErroresDelCliente();
app.MapearJornadas();
app.MapearOrdenes();
app.MapearArchivos();

app.Run();

public partial class Program;
