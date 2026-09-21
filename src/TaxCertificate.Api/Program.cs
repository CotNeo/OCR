using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using TaxCertificate.Api.Configuration;
using TaxCertificate.Api.Middleware;
using TaxCertificate.Api.Services;
using TaxCertificate.Api.Swagger;
using TaxCertificate.Application.Configuration;
using TaxCertificate.Application.Interfaces;
using TaxCertificate.Application.Parsers;
using TaxCertificate.Application.Services;
using TaxCertificate.Infrastructure.Ocr;

var builder = WebApplication.CreateBuilder(args);

// ---------------------------------------------------------------- options
builder.Services
    .AddOptions<OcrOptions>()
    .Bind(builder.Configuration.GetSection(OcrOptions.SectionName))
    .Validate(o => Uri.TryCreate(o.BaseUrl, UriKind.Absolute, out _), "Ocr:BaseUrl must be an absolute URI.")
    .Validate(o => o.TimeoutSeconds > 0, "Ocr:TimeoutSeconds must be greater than zero.")
    .ValidateOnStart();

builder.Services
    .AddOptions<UploadOptions>()
    .Bind(builder.Configuration.GetSection(UploadOptions.SectionName))
    .Validate(o => o.MaxFileSizeBytes > 0, "Upload:MaxFileSizeBytes must be greater than zero.")
    .Validate(o => o.MaxConcurrentAnalyses > 0, "Upload:MaxConcurrentAnalyses must be greater than zero.")
    .ValidateOnStart();

builder.Services
    .AddOptions<ParserOptions>()
    .Bind(builder.Configuration.GetSection(ParserOptions.SectionName))
    .ValidateOnStart();

// AnalyzerOptions reads IncludeRawResult from the same Ocr section, so the flag has one home.
builder.Services.AddSingleton<IOptions<AnalyzerOptions>>(sp =>
{
    var ocrOptions = sp.GetRequiredService<IOptions<OcrOptions>>().Value;
    return Options.Create(new AnalyzerOptions { IncludeRawResult = ocrOptions.IncludeRawResult });
});

// ---------------------------------------------------------------- kestrel limits
builder.Services.Configure<Microsoft.AspNetCore.Server.Kestrel.Core.KestrelServerOptions>(options =>
{
    var max = builder.Configuration.GetValue<long?>($"{UploadOptions.SectionName}:MaxFileSizeBytes")
              ?? 10 * 1024 * 1024;
    options.Limits.MaxRequestBodySize = max;
});

builder.Services.Configure<Microsoft.AspNetCore.Http.Features.FormOptions>(options =>
{
    var max = builder.Configuration.GetValue<long?>($"{UploadOptions.SectionName}:MaxFileSizeBytes")
              ?? 10 * 1024 * 1024;
    options.MultipartBodyLengthLimit = max;
});

// ---------------------------------------------------------------- services
builder.Services
    .AddControllers()
    .AddJsonOptions(options =>
    {
        // No global null-ignore on purpose. The contract distinguishes two kinds of absence:
        // a field inside `data`/`validation` that was searched for and not found stays an
        // explicit `null` (so a client can tell "looked, absent" from "not in this version"),
        // while envelope members that simply do not apply (`error`, `rawOcr`) are dropped via
        // per-property [JsonIgnore] attributes on the DTOs.
        options.JsonSerializerOptions.Encoder =
            System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping;
    });

builder.Services.AddSingleton<ITaxCertificateParser, TaxCertificateParser>();
builder.Services.AddSingleton<ITaxCertificateAnalyzer, TaxCertificateAnalyzer>();
builder.Services.AddSingleton<OcrConcurrencyLimiter>();

builder.Services
    .AddHttpClient<IOcrClient, OcrClient>((serviceProvider, client) =>
    {
        var options = serviceProvider.GetRequiredService<IOptions<OcrOptions>>().Value;
        client.BaseAddress = new Uri(options.BaseUrl);
        client.Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds);
    })
    .AddOcrRetryPolicy();

builder.Services.AddHealthChecks()
    .AddCheck<OcrServiceHealthCheck>("ocr-service", tags: ["ready"]);

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new Microsoft.OpenApi.Models.OpenApiInfo
    {
        Title = "Tax Certificate OCR API",
        Version = "v1",
        Description =
            "Türk vergi levhalarını tamamen lokal olarak (PP-OCRv5 + deterministic parser) " +
            "structured JSON'a dönüştürür. Hiçbir harici OCR/LLM servisi kullanılmaz.",
    });

    options.OperationFilter<FileUploadOperationFilter>();
});

var app = builder.Build();

// ---------------------------------------------------------------- pipeline
app.UseMiddleware<GlobalExceptionMiddleware>();

app.UseSwagger();
app.UseSwaggerUI(options =>
{
    options.SwaggerEndpoint("/swagger/v1/swagger.json", "Tax Certificate OCR API v1");
    options.DocumentTitle = "Tax Certificate OCR API";
});

// Serves wwwroot/index.html at "/" - a dependency-free upload/inspect page for local
// testing. Same-origin, so no CORS configuration is needed. UseDefaultFiles must run
// before UseStaticFiles for the "/" -> index.html rewrite to take effect.
app.UseDefaultFiles();
app.UseStaticFiles();

app.MapControllers();

app.MapHealthChecks("/health", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
{
    ResponseWriter = HealthResponseWriter.WriteAsync,
});

app.Run();
