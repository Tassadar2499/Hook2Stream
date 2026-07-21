using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace Hook2Stream.Api;

internal sealed class OpenApiSecurityTransformers :
    IOpenApiDocumentTransformer,
    IOpenApiOperationTransformer
{
    private const string SessionScheme = "oauthSession";
    private const string CsrfScheme = "csrf";
    private const string LocalScheme = "localDevelopmentBearer";

    public Task TransformAsync(
        OpenApiDocument document,
        OpenApiDocumentTransformerContext context,
        CancellationToken cancellationToken)
    {
        document.Components ??= new OpenApiComponents();
        document.Components.SecuritySchemes ??=
            new Dictionary<string, IOpenApiSecurityScheme>(StringComparer.Ordinal);
        document.Components.SecuritySchemes[SessionScheme] = new OpenApiSecurityScheme
        {
            Type = SecuritySchemeType.ApiKey,
            In = ParameterLocation.Cookie,
            Name = "__Host-h2s_session",
            Description = "Opaque HttpOnly browser session cookie (h2s_session in Development/Testing)."
        };
        document.Components.SecuritySchemes[CsrfScheme] = new OpenApiSecurityScheme
        {
            Type = SecuritySchemeType.ApiKey,
            In = ParameterLocation.Header,
            Name = Authentication.OAuthCsrfMiddleware.HeaderName,
            Description = "Double-submit CSRF token required with an OAuth session on unsafe methods."
        };
        document.Components.SecuritySchemes[LocalScheme] = new OpenApiSecurityScheme
        {
            Type = SecuritySchemeType.Http,
            Scheme = "bearer",
            BearerFormat = "opaque development token",
            Description = "Loopback-only Development/Testing authentication."
        };
        return Task.CompletedTask;
    }

    public Task TransformAsync(
        OpenApiOperation operation,
        OpenApiOperationTransformerContext context,
        CancellationToken cancellationToken)
    {
        var metadata = context.Description.ActionDescriptor.EndpointMetadata;
        if (metadata.OfType<IAllowAnonymous>().Any() || !metadata.OfType<IAuthorizeData>().Any())
        {
            return Task.CompletedTask;
        }

        var oauth = new OpenApiSecurityRequirement
        {
            [new OpenApiSecuritySchemeReference(SessionScheme, context.Document, null)] = []
        };
        if (!IsSafe(context.Description.HttpMethod))
        {
            oauth[new OpenApiSecuritySchemeReference(CsrfScheme, context.Document, null)] = [];
        }

        operation.Security =
        [
            oauth,
            new OpenApiSecurityRequirement
            {
                [new OpenApiSecuritySchemeReference(LocalScheme, context.Document, null)] = []
            }
        ];
        return Task.CompletedTask;
    }

    private static bool IsSafe(string? method) =>
        method is not null &&
        (HttpMethods.IsGet(method) ||
        HttpMethods.IsHead(method) ||
        HttpMethods.IsOptions(method) ||
        HttpMethods.IsTrace(method));
}
