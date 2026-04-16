# Multi-stage build for MtgDeckStudio.Web (.NET 10).
# Usage:
#   docker build -t mtg-deck-studio .
#   docker run --rm -p 8080:8080 -v mtg_data:/data mtg-deck-studio
#
# Persistence:
#   Set MTG_DATA_DIR=/data (already set below) and mount a volume there.
#   Both the SQLite category-knowledge DB and ChatGPT analysis artifacts
#   are redirected under that single directory.

# ---------- build stage ----------
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copy csproj files first so dotnet restore is cached separately from the source copy.
COPY MtgDeckStudio.sln .
COPY MtgDeckStudio.Core/MtgDeckStudio.Core.csproj MtgDeckStudio.Core/
COPY MtgDeckStudio.Web/MtgDeckStudio.Web.csproj MtgDeckStudio.Web/
COPY MtgDeckStudio.CLI/MtgDeckStudio.CLI.csproj MtgDeckStudio.CLI/
COPY MtgDeckStudio.Core.Tests/MtgDeckStudio.Core.Tests.csproj MtgDeckStudio.Core.Tests/
COPY MtgDeckStudio.Web.Tests/MtgDeckStudio.Web.Tests.csproj MtgDeckStudio.Web.Tests/
RUN dotnet restore MtgDeckStudio.Web/MtgDeckStudio.Web.csproj

# Copy the rest of the source.
COPY MtgDeckStudio.Core/ MtgDeckStudio.Core/
COPY MtgDeckStudio.Web/ MtgDeckStudio.Web/

RUN dotnet publish MtgDeckStudio.Web/MtgDeckStudio.Web.csproj \
    -c Release \
    -o /app/publish \
    --no-restore \
    /p:UseAppHost=false

# ---------- runtime stage ----------
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

# Single persistent data dir. Services resolve their paths relative to MTG_DATA_DIR when set.
RUN mkdir -p /data

COPY --from=build /app/publish .

ENV ASPNETCORE_ENVIRONMENT=Production
ENV MTG_DATA_DIR=/data
EXPOSE 8080

VOLUME ["/data"]

# Resolve ASPNETCORE_URLS at container start so $PORT (set by Render/Fly Launch) wins when provided.
ENTRYPOINT ["sh", "-c", "ASPNETCORE_URLS=http://+:${PORT:-8080} exec dotnet MtgDeckStudio.Web.dll"]
