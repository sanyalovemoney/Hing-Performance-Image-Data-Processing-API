# syntax=docker/dockerfile:1

# ---- build stage -----------------------------------------------------------
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Restore against project files only — sources change far more often, so this
# layer stays cached across code edits.
COPY Directory.Build.props ./
COPY src/ImageProcessing.Core/ImageProcessing.Core.csproj src/ImageProcessing.Core/
COPY src/ImageProcessing.Processing/ImageProcessing.Processing.csproj src/ImageProcessing.Processing/
COPY src/ImageProcessing.Workers/ImageProcessing.Workers.csproj src/ImageProcessing.Workers/
COPY src/ImageProcessing.Api/ImageProcessing.Api.csproj src/ImageProcessing.Api/
RUN dotnet restore src/ImageProcessing.Api/ImageProcessing.Api.csproj

COPY src/ src/
RUN dotnet publish src/ImageProcessing.Api/ImageProcessing.Api.csproj \
    -c Release -o /app/publish /p:UseAppHost=false

# ---- runtime stage ----------------------------------------------------------
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app

ENV ASPNETCORE_URLS=http://+:8080 \
    DOTNET_gcServer=1
EXPOSE 8080

# Non-root user; owns the writable storage volume.
RUN groupadd --gid 1000 appuser \
    && useradd --uid 1000 --gid appuser --create-home appuser \
    && mkdir -p /app/storage \
    && chown -R appuser:appuser /app

COPY --from=build --chown=appuser:appuser /app/publish .

USER appuser
VOLUME ["/app/storage"]

# No in-image HEALTHCHECK: the runtime image ships without curl/wget, and
# orchestrators should probe GET /healthz (liveness) and GET /readyz
# (readiness, 503 at the queue high-watermark) over HTTP instead.

ENTRYPOINT ["dotnet", "ImageProcessing.Api.dll"]
