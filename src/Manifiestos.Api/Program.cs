using Manifiestos.Api.Acceso;
using Manifiestos.Api.Clientes;
using Manifiestos.Api.Rutas;
using Manifiestos.Api.Geografia;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AgregarAccesoWeb();
builder.Services.AgregarRutas();
builder.Services.AgregarClientes();
builder.Services.AgregarGeografia();

var app = builder.Build();

app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();

app.MapearAcceso();
app.MapearRutas();
app.MapearClientes();
app.MapearGeografia();

app.Run();

public partial class Program;
