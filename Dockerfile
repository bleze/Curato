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
COPY Curato.slnx ./
COPY Curato.Core/Curato.Core.csproj Curato.Core/
COPY Curato.Data/Curato.Data.csproj Curato.Data/
COPY Curato.Media/Curato.Media.csproj Curato.Media/
COPY Curato.Organizer/Curato.Organizer.csproj Curato.Organizer/
COPY Curato.Vision/Curato.Vision.csproj Curato.Vision/
COPY Curato.Api/Curato.Api.csproj Curato.Api/
RUN dotnet restore Curato.Api/Curato.Api.csproj
COPY Curato.Core/ Curato.Core/
COPY Curato.Data/ Curato.Data/
COPY Curato.Media/ Curato.Media/
COPY Curato.Organizer/ Curato.Organizer/
COPY Curato.Vision/ Curato.Vision/
COPY Curato.Api/ Curato.Api/
RUN dotnet publish Curato.Api/Curato.Api.csproj -c Release -o /app/publish --no-restore

# --- Final runtime image ------------------------------------------------------
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app

# ffmpeg for video metadata/thumbnails (PLAN.md §1); libheif for HEIC decode.
RUN apt-get update \
    && apt-get install -y --no-install-recommends ffmpeg libheif1 \
    && rm -rf /var/lib/apt/lists/*

COPY --from=api-build /app/publish ./
COPY --from=web-build /src/Curato.Web/dist ./wwwroot

# The library (photos + .curato/) is expected to be bind-mounted, not baked in.
VOLUME ["/data"]

EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080
ENTRYPOINT ["dotnet", "Curato.Api.dll"]
