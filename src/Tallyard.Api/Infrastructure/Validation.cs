using FluentValidation;

namespace Tallyard.Api.Infrastructure;

/// <summary>Runs the registered FluentValidation validator for the request body of type T.</summary>
public sealed class Validate<T> : IEndpointFilter where T : class
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext ctx, EndpointFilterDelegate next)
    {
        var body = ctx.Arguments.OfType<T>().FirstOrDefault();
        var validator = ctx.HttpContext.RequestServices.GetService<IValidator<T>>();
        if (body is not null && validator is not null)
            await validator.ValidateAndThrowAsync(body, ctx.HttpContext.RequestAborted);
        return await next(ctx);
    }
}

public static class ValidationExtensions
{
    public static RouteHandlerBuilder Validated<T>(this RouteHandlerBuilder b) where T : class =>
        b.AddEndpointFilter<Validate<T>>();
}
