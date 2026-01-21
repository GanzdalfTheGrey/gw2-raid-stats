# Build stage
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Copy solution and project files
COPY *.slnx ./
COPY src/GW2RaidStats.Core/*.csproj ./src/GW2RaidStats.Core/
COPY src/GW2RaidStats.Infrastructure/*.csproj ./src/GW2RaidStats.Infrastructure/
COPY src/GW2RaidStats.Client/*.csproj ./src/GW2RaidStats.Client/
COPY src/GW2RaidStats.Server/*.csproj ./src/GW2RaidStats.Server/

# Restore dependencies
RUN dotnet restore gw2-raid-stats.slnx

# Copy everything else and build
COPY . .
WORKDIR /src/src/GW2RaidStats.Server
RUN dotnet publish -c Release -o /app/publish --no-restore

# Runtime stage
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final
WORKDIR /app
COPY --from=build /app/publish .

# Set environment variables
ENV ASPNETCORE_URLS=http://+:8080
ENV ASPNETCORE_ENVIRONMENT=Production

EXPOSE 8080

ENTRYPOINT ["dotnet", "GW2RaidStats.Server.dll"]
