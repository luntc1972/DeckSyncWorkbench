# Multi-stage build for DeckFlow.Web (.NET 10)

# ---------- build stage ----------
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Install Node.js + npm because DeckFlow.Web.csproj runs a custom TypeScript build target
RUN apt-get update \
    && apt-get install -y curl ca-certificates gnupg \
    && mkdir -p /etc/apt/keyrings \
    && curl -fsSL https://deb.nodesource.com/gpgkey/nodesource-repo.gpg.key \
       | gpg --dearmor -o /etc/apt/keyrings/nodesource.gpg \
    && echo "deb [signed-by=/etc/apt/keyrings/nodesource.gpg] https://deb.nodesource.com/node_20.x nodistro main" \
       > /etc/apt/sources.list.d/nodesource.list \
    && apt-get update \
    && apt-get install -y nodejs \
    && apt-get clean \
    && rm -rf /var/lib/apt/lists/*

# Copy files needed for restore first
COPY DeckFlow.sln ./
COPY Directory.Build.props ./

COPY DeckFlow.Core/DeckFlow.Core.csproj DeckFlow.Core/
COPY DeckFlow.Web/DeckFlow.Web.csproj DeckFlow.Web/
COPY DeckFlow.CLI/DeckFlow.CLI.csproj DeckFlow.CLI/

# Restore .NET dependencies
RUN dotnet restore DeckFlow.Web/DeckFlow.Web.csproj

# Copy the rest of the repo
COPY . .

# Install local TypeScript where the csproj expects it:
# ./node_modules/typescript/bin/tsc
WORKDIR /src/DeckFlow.Web
RUN npm install typescript@5.2.2

# Publish the web app
WORKDIR /src
RUN dotnet publish DeckFlow.Web/DeckFlow.Web.csproj \
    -c Release \
    -o /app/publish \
    --no-restore \
    /p:UseAppHost=false

# ---------- runtime stage ----------
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

RUN mkdir -p /data

COPY --from=build /app/publish .

ENV ASPNETCORE_ENVIRONMENT=Production
ENV MTG_DATA_DIR=/data

EXPOSE 8080
VOLUME ["/data"]

ENTRYPOINT ["sh", "-c", "ASPNETCORE_URLS=http://+:${PORT:-8080} exec dotnet DeckFlow.Web.dll"]