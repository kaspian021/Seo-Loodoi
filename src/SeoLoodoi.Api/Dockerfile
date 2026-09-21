# syntax=docker/dockerfile:1
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY SeoLoodoi.slnx ./
COPY src/SeoLoodoi.Domain/SeoLoodoi.Domain.csproj src/SeoLoodoi.Domain/
COPY src/SeoLoodoi.Application/SeoLoodoi.Application.csproj src/SeoLoodoi.Application/
COPY src/SeoLoodoi.Infrastructure/SeoLoodoi.Infrastructure.csproj src/SeoLoodoi.Infrastructure/
COPY src/SeoLoodoi.Api/SeoLoodoi.Api.csproj src/SeoLoodoi.Api/
RUN dotnet restore src/SeoLoodoi.Api/SeoLoodoi.Api.csproj

COPY src/ src/
RUN dotnet publish src/SeoLoodoi.Api/SeoLoodoi.Api.csproj \
    --configuration Release --no-restore --output /app/publish \
    /p:UseAppHost=false /warnaserror

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
RUN apt-get update \
    && apt-get install --yes --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/*
COPY --from=build /app/publish ./

ENV ASPNETCORE_ENVIRONMENT=Production \
    ASPNETCORE_HTTP_PORTS=8080 \
    DOTNET_EnableDiagnostics=0
EXPOSE 8080
HEALTHCHECK --interval=30s --timeout=5s --start-period=30s --retries=3 \
    CMD curl --fail --silent http://127.0.0.1:8080/health/ready >/dev/null || exit 1
USER $APP_UID
ENTRYPOINT ["dotnet", "SeoLoodoi.Api.dll"]
