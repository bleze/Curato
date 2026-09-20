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
RUN dotnet restore Curato.Api/Curato.Api.csproj
COPY Curato.Core/ Curato.Core/
COPY Curato.Data/ Curato.Data/
COPY Curato.iCloud/ Curato.iCloud/
COPY Curato.Media/ Curato.Media/
COPY Curato.Picasa/ Curato.Picasa/
COPY Curato.Organizer/ Curato.Organizer/
COPY Curato.Vision/ Curato.Vision/
COPY Curato.Api/ Curato.Api/
# requirements.txt for the icloudpd install below — needs to exist at this
# relative path before `dotnet publish` runs (Curato.Api.csproj also has a
# Content item copying it into the build/publish output, for local dev).
COPY tools/ tools/
RUN dotnet publish Curato.Api/Curato.Api.csproj -c Release -o /app/publish --no-restore

# --- Final runtime image ------------------------------------------------------
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app

# ffmpeg for video metadata/thumbnails (PLAN.md §1); libheif for HEIC decode;
# python3/pip for direct iCloud integration (§4 — icloudpd, MIT-licensed,
# invoked directly as an external process by Curato.iCloud rather than
# ported into C#; python3/pip are needed both to install it and to run it,
# since the icloudpd executable is itself a Python program).
RUN apt-get update \
    && apt-get install -y --no-install-recommends ffmpeg libheif1 python3 python3-pip \
    && rm -rf /var/lib/apt/lists/*

COPY --from=api-build /app/publish ./

# --break-system-packages: Debian's system pip refuses a bare global install
# (PEP 668) — acceptable here since the whole container is already a single-
# purpose, isolated environment, not a shared system Python. requirements.txt
# was already copied into the publish output above (Curato.Api.csproj's
# Content item), so it's picked up from there rather than needing its own COPY.
RUN pip install --no-cache-dir --break-system-packages -r ./tools/icloud-sync/requirements.txt
COPY --from=web-build /src/Curato.Web/dist ./wwwroot

# The library (photos + .curato/) is expected to be bind-mounted, not baked in.
VOLUME ["/data"]

EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080
ENTRYPOINT ["dotnet", "Curato.Api.dll"]
