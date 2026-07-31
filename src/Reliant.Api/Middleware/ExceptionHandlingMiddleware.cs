using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using System.Text.Json;

namespace Reliant.Api.Middleware;

public class ExceptionHandlingMiddleware(RequestDelegate next, ILogger<ExceptionHandlingMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await next(context);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unhandled exception. TraceId: {TraceId}", context.TraceIdentifier);
            await WriteProblemDetails(context, ex);
        }
    }

    private static async Task WriteProblemDetails(HttpContext context, Exception ex)
    {
        var (status, title, detail) = ex switch
        {
            Reliant.Domain.Entities.InvalidStateTransitionException ist => (409, "Invalid state transition", ist.Message),
            ArgumentException arg => (400, "Bad request", arg.Message),
            InvalidOperationException inv => (400, "Bad request", inv.Message),
            _ => (500, "Internal server error", "An unexpected error occurred")
        };

        var problem = new ProblemDetails
        {
            Type = $"https://reliant.dev/errors/{status}",
            Title = title,
            Status = status,
            Detail = detail,
            Instance = context.Request.Path
        };
        problem.Extensions["trace-id"] = context.TraceIdentifier;

        context.Response.StatusCode = status;
        context.Response.ContentType = "application/problem+json";
        await JsonSerializer.SerializeAsync(context.Response.Body, problem);
    }
}
