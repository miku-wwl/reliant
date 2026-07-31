using Microsoft.AspNetCore.RateLimiting;
using Reliant.Application;
using Reliant.Infrastructure;
using System.Threading.RateLimiting;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenApi();
builder.Services.AddControllers();

builder.Services.AddReliantApplication();
builder.Services.AddReliantInfrastructure(builder.Configuration);

builder.Services.AddRateLimiter(options =>
{
    options.AddFixedWindowLimiter("org-limit", opt =>
    {
        opt.PermitLimit = 100;
        opt.Window = TimeSpan.FromMinutes(1);
        opt.QueueProcessingOrder = QueueProcessingOrder.OldestFirst;
        opt.QueueLimit = 0;
    });
    options.OnRejected = (context, ct) =>
    {
        context.HttpContext.Response.Headers["Retry-After"] = "60";
        return ValueTask.CompletedTask;
    };
});

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseMiddleware<Reliant.Api.Middleware.ExceptionHandlingMiddleware>();
app.UseMiddleware<Reliant.Api.Middleware.TenantMiddleware>();
app.UseRateLimiter();
app.MapControllers();

app.Run();

/// <summary>
/// Entry point for integration tests (WebApplicationFactory&lt;Program&gt;).
/// </summary>
public partial class Program
{
}
