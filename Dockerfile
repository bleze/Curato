# syntax=docker/dockerfile:1

# --- Build the web frontend -------------------------------------------------
FROM node:22-alpine AS web-build
WORKDIR /src/Curato.Web
COPY Curato.Web/package*.json ./
RUN npm ci
COPY Curato.Web/ ./
RUN npm run build

# --- Build the .NET server ---------------------------------------------------
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS api-build
WORKDIR /src
COPY Curato.slnx Directory.Build.props ./
COPY Curato.Core/Curato.Core.csproj Curato.Core/
COPY Curato.Data/Curato.Data.csproj Curato.Data/
COPY Curato.iCloud/Curato.iCloud.csproj Curato.iCloud/
COPY Curato.Media/Curato.Media.csproj Curato.Media/
COPY Curato.Picasa/Curato.Picasa.csproj Curato.Picasa/
COPY Curato.Organizer/Curato.Organizer.csproj Curato.Organizer/
COPY Curato.Vision/Curato.Vision.csproj Curato.Vision/
COPY Curato.Api/Curato.Api.csproj Curato.Api/
# linux-x64 only: without a runtime identifier the publish output carries every
# platform's native libraries (SkiaSharp, libheif for Windows/macOS/ARM...),
# which roughly doubled the app layer every update has to download.
RUN dotnet restore Curato.Api/Curato.Api.csproj -r linux-x64
COPY Curato.Core/ Curato.Core/
COPY Curato.Data/ Curato.Data/
COPY Curato.iCloud/ Curato.iCloud/
COPY Curato.Media/ Curato.Media/
COPY Curato.Picasa/ Curato.Picasa/
COPY Curato.Organizer/ Curato.Organizer/
COPY Curato.Vision/ Curato.Vision/
COPY Curato.Api/ Curato.Api/
# Curato.Api.csproj links tools/icloud-sync into its output, so it must exist here.
COPY tools/ tools/
RUN dotnet publish Curato.Api/Curato.Api.csproj -c Release -r linux-x64 --self-contained false -o /app/publish --no-restore

# --- Final runtime image ------------------------------------------------------
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app

# ffmpeg: video thumbnails. libheif1 + its HEVC plugin: HEIC decoding (on
# Linux HeicDecoder loads the system libheif; the NuGet-bundled one needs
# libraries this distro doesn't have). python3-venv: icloudpd, which
# Curato.iCloud runs as an external process. curl: HEALTHCHECK.
RUN apt-get update \
    && apt-get install -y --no-install-recommends ffmpeg libheif1 libheif-plugin-libde265 python3 python3-venv curl \
    && rm -rf /var/lib/apt/lists/*

# icloudpd in its own venv, on PATH (iCloudProcessOptions calls plain `icloudpd`).
COPY tools/icloud-sync/requirements.txt /tmp/icloud-requirements.txt
RUN python3 -m venv /opt/icloudpd \
    && /opt/icloudpd/bin/pip install --no-cache-dir -r /tmp/icloud-requirements.txt \
    && rm /tmp/icloud-requirements.txt
ENV PATH="/opt/icloudpd/bin:${PATH}"

COPY --from=api-build /app/publish ./
COPY --from=web-build /src/Curato.Web/dist ./wwwroot

# /data: Curato's own settings (admin account, iCloud session). Photo
# libraries are mounted separately, wherever the compose file puts them
# (e.g. /photos), and their .curato/ folder lives inside the library itself.
# HOME points at /data too: the container may run as any UID (compose
# `user:`), which has no home directory, and icloudpd/Python write dotfiles.
ENV Curato__DataDirectory=/data \
    HOME=/data \
    ASPNETCORE_HTTP_PORTS=8080 \
    DOTNET_CLI_TELEMETRY_OPTOUT=1
RUN mkdir -p /data && chown app:app /data
VOLUME ["/data"]

# Never root: Curato writes into the photo share, and files it creates should
# belong to a normal user. `app` is the .NET image's built-in non-root user;
# compose overrides it with `user:` to match the NAS user owning the photos.
USER app

EXPOSE 8080
HEALTHCHECK --interval=30s --timeout=5s --start-period=20s --retries=3 \
    CMD curl -fsS http://localhost:8080/health || exit 1
ENTRYPOINT ["dotnet", "Curato.Api.dll"]
