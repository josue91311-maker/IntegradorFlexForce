using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using IntegradorFlexForce.Services;

var builder = WebApplication.CreateBuilder(args);

// Servicios del Framework y API
builder.Services.AddControllers();

// Motor de Sincronización y Servicio de Fondo
builder.Services.AddSingleton<SyncEngine>();
builder.Services.AddSingleton<SyncProductosEngine>();
builder.Services.AddSingleton<SyncVendedoresEngine>();
builder.Services.AddSingleton<SyncStockEngine>();
builder.Services.AddHostedService<SyncBackgroundWorker>();

// Habilitar CORS si se requiere acceso desde otra IP
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod();
    });
});

var app = builder.Build();

app.UseCors();
app.UseDefaultFiles();
app.UseStaticFiles();

app.MapControllers();

// Fallback al index.html del portal
app.MapFallbackToFile("index.html");

app.Run();
