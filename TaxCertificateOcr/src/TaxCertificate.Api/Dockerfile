# Multi-arch: the official .NET images publish linux/arm64, so this builds natively on M1.
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Restore first so the layer is cached across source edits.
COPY Directory.Build.props ./
COPY src/TaxCertificate.Application/TaxCertificate.Application.csproj src/TaxCertificate.Application/
COPY src/TaxCertificate.Infrastructure/TaxCertificate.Infrastructure.csproj src/TaxCertificate.Infrastructure/
COPY src/TaxCertificate.Api/TaxCertificate.Api.csproj src/TaxCertificate.Api/
RUN dotnet restore src/TaxCertificate.Api/TaxCertificate.Api.csproj

COPY src/ src/
RUN dotnet publish src/TaxCertificate.Api/TaxCertificate.Api.csproj \
    -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app

# Non-root: the API only reads request bodies into memory, so it needs no write access anywhere.
RUN useradd --uid 10001 --create-home --shell /usr/sbin/nologin apiuser
USER 10001

COPY --from=build /app ./
ENV ASPNETCORE_URLS=http://0.0.0.0:8080 \
    ASPNETCORE_ENVIRONMENT=Production \
    DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=false
EXPOSE 8080
ENTRYPOINT ["dotnet", "TaxCertificate.Api.dll"]
