using Microsoft.OpenApi.Models;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace TaxCertificate.Api.Swagger;

/// <summary>
/// Makes the analyze endpoint show a real file picker in Swagger UI.
/// Swashbuckle infers <c>IFormFile</c> as a string when it arrives via <c>[FromForm]</c> on a
/// minimal signature, so the multipart schema is declared explicitly here.
/// </summary>
public sealed class FileUploadOperationFilter : IOperationFilter
{
    public void Apply(OpenApiOperation operation, OperationFilterContext context)
    {
        var hasFormFile = context.MethodInfo
            .GetParameters()
            .Any(p => p.ParameterType == typeof(IFormFile) || p.ParameterType == typeof(IFormFile[]));

        if (!hasFormFile)
        {
            return;
        }

        operation.RequestBody = new OpenApiRequestBody
        {
            Required = true,
            Content =
            {
                ["multipart/form-data"] = new OpenApiMediaType
                {
                    Schema = new OpenApiSchema
                    {
                        Type = "object",
                        Required = new HashSet<string> { "file" },
                        Properties =
                        {
                            ["file"] = new OpenApiSchema
                            {
                                Type = "string",
                                Format = "binary",
                                Description = "Vergi levhası (PDF, JPEG veya PNG).",
                            },
                        },
                    },
                },
            },
        };
    }
}
