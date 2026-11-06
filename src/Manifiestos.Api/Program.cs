using Manifiestos.Api;
using Manifiestos.Api.Acceso;
using Manifiestos.Api.Clientes;
using Manifiestos.Api.Rutas;
using Manifiestos.Api.Geografia;
using Manifiestos.Api.Operadores;
using Manifiestos.Api.Unidades;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AgregarProxyConfiable(builder.Configuration);
builder.Services.AgregarAccesoWeb();
builder.Services.AgregarRutas();
builder.Services.AgregarClientes();
builder.Services.AgregarGeografia();
builder.Services.AgregarUnidades();
builder.Services.AgregarOperadores();

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

app.Run();

public partial class Program;
