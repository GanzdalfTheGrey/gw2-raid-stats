# Build stage
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

# Copy solution and project files
COPY *.slnx ./
COPY src/GW2RaidStats.Core/*.csproj ./src/GW2RaidStats.Core/
COPY src/GW2RaidStats.Infrastructure/*.csproj ./src/GW2RaidStats.Infrastructure/
COPY src/GW2RaidStats.Client/*.csproj ./src/GW2RaidStats.Client/
COPY src/GW2RaidStats.Server/*.csproj ./src/GW2RaidStats.Server/
COPY src/GW2RaidStats.Processor/*.csproj ./src/GW2RaidStats.Processor/
COPY src/GW2RaidStats.DiscordBot/*.csproj ./src/GW2RaidStats.DiscordBot/

# Restore dependencies
RUN dotnet restore gw2-raid-stats.slnx

# Copy everything else and build
COPY . .
WORKDIR /src/src/GW2RaidStats.Server
RUN dotnet publish -c Release -o /app/publish --no-restore

# Runtime stage
FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS final
WORKDIR /app

# Install curl for health checks
RUN apt-get update && apt-get install -y curl && rm -rf /var/lib/apt/lists/*

# Create non-root user for security
RUN adduser --disabled-password --gecos '' --uid 1000 appuser

# Copy published app
COPY --from=build /app/publish .

# Set ownership
RUN chown -R appuser:appuser /app

# Switch to non-root user
USER appuser

# Set environment variables
ENV ASPNETCORE_URLS=http://+:8080
ENV ASPNETCORE_ENVIRONMENT=Production

EXPOSE 8080

# Health check
HEALTHCHECK --interval=30s --timeout=10s --start-period=10s --retries=3 \
    CMD curl -f http://localhost:8080/api/health || exit 1

ENTRYPOINT ["dotnet", "GW2RaidStats.Server.dll"]
