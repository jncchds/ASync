# ── Stage 1: Build React UI ───────────────────────────────────────────────────
FROM node:22-alpine AS ui-build
WORKDIR /ui
COPY src/async-ui/package*.json ./
RUN npm ci
COPY src/async-ui/ ./
COPY VERSION /VERSION
# Build to ./dist (override the dev outDir pointing to ASP.NET wwwroot)
RUN npx vite build --outDir ./dist --emptyOutDir

# ── Stage 2: Build .NET API ───────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS api-build
WORKDIR /src

# Copy solution and project files first (layer caching)
COPY ASync.slnx Directory.Build.props Directory.Packages.props VERSION ./
COPY src/ASync.Contracts/ASync.Contracts.csproj src/ASync.Contracts/
COPY src/ASync.Core/ASync.Core.csproj src/ASync.Core/
COPY src/ASync.Infrastructure/ASync.Infrastructure.csproj src/ASync.Infrastructure/
COPY src/ASync.Api/ASync.Api.csproj src/ASync.Api/
RUN dotnet restore src/ASync.Api/ASync.Api.csproj

# Copy source and build
COPY src/ src/
RUN dotnet publish src/ASync.Api/ASync.Api.csproj -c Release -o /app/publish --no-restore

# ── Stage 3: Runtime ──────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
# Npgsql probes for GSSAPI on every connection and prints an error when the library is missing.
RUN apt-get update && apt-get install -y --no-install-recommends libgssapi-krb5-2 && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=api-build /app/publish ./

# Copy React build output into wwwroot
COPY --from=ui-build /ui/dist ./wwwroot

EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080
ENTRYPOINT ["dotnet", "ASync.Api.dll"]
