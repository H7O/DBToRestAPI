# --- Build stage ---
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copy solution and project file first for layer caching
COPY DBToRestAPI.sln .
COPY DBToRestAPI/DBToRestAPI.csproj DBToRestAPI/

# Restore dependencies (cached unless .csproj changes)
RUN dotnet restore DBToRestAPI/DBToRestAPI.csproj

# Copy everything else and publish
COPY . .
RUN dotnet publish DBToRestAPI/DBToRestAPI.csproj \
    --configuration Release \
    --no-restore \
    --output /app

# --- Runtime stage ---
FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app

# Create non-root user
RUN groupadd -r appuser && useradd -r -g appuser -d /app appuser

# Copy published output
COPY --from=build /app .

# Ensure config and data directories are writable
RUN chown -R appuser:appuser /app/config /app/demo.db

# Run as non-root
USER appuser

# HTTP port (HTTPS requires mounting a certificate — see docs/topics/16-tls-certificates.md)
EXPOSE 5000

ENV ASPNETCORE_ENVIRONMENT=Production

ENTRYPOINT ["dotnet", "DBToRestAPI.dll"]
