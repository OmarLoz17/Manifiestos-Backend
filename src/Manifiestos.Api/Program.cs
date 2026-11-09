using Manifiestos.Api;
using Manifiestos.Api.Acceso;
using Manifiestos.Api.Clientes;
using Manifiestos.Api.Rutas;
using Manifiestos.Api.Geografia;
using Manifiestos.Api.Incidencias;
using Manifiestos.Api.Jornadas;
using Manifiestos.Api.Documentos;
using Manifiestos.Api.Operadores;
using Manifiestos.Api.Ordenes;
using Manifiestos.Api.Unidades;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AgregarProxyConfiable(builder.Configuration);
builder.Services.AgregarAccesoWeb();
builder.Services.AgregarRutas();
builder.Services.AgregarClientes();
builder.Services.AgregarGeografia();
builder.Services.AgregarUnidades();
builder.Services.AgregarOperadores();
builder.Services.AgregarManifiestos();
builder.Services.AgregarIncidencias();
builder.Services.AgregarJornadas();
builder.Services.AgregarOrdenes();

var app = builder.Build();

app.UseForwardedHeaders();
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();

app.MapearAcceso();
app.MapearRutas();
app.MapearClientes();
app.MapearGeografia();
app.MapearUnidades();
app.MapearOperadores();
app.MapearManifiestos();
app.MapearIncidencias();
app.MapearJornadas();
app.MapearOrdenes();

app.Run();

public partial class Program;
