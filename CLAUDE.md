# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

GW2 Raid Stats is a self-hosted Guild Wars 2 raid statistics dashboard. It processes arcdps combat logs via GW2 Elite Insights, stores metrics in PostgreSQL, and displays stats through a Blazor WebAssembly frontend.

## Build & Run Commands

```bash
# Build all projects
dotnet build gw2-raid-stats/src/gw2-raid-stats.slnx

# Build release
dotnet build gw2-raid-stats/src/gw2-raid-stats.slnx --configuration Release

# Run tests (none currently exist)
dotnet test gw2-raid-stats/src/gw2-raid-stats.slnx
```

### Local Development

```bash
# Start database + processor (from gw2-raid-stats/gw2-raid-stats/)
docker-compose -f docker-compose.dev.yml up -d --build

# Run the web server
cd gw2-raid-stats/src/GW2RaidStats.Server
dotnet run
# Access at https://localhost:5001

# View processor logs
docker-compose -f docker-compose.dev.yml logs -f processor

# Rebuild processor after changes
docker-compose -f docker-compose.dev.yml up -d --build processor

# Stop everything
docker-compose -f docker-compose.dev.yml down

# Fresh start (removes data)
docker-compose -f docker-compose.dev.yml down -v
```

### Production Docker

```bash
cd gw2-raid-stats/gw2-raid-stats
docker-compose up -d
```

## Architecture

```
GW2RaidStats.Client       → Blazor WASM frontend (MudBlazor UI)
GW2RaidStats.Server       → ASP.NET Core REST API
GW2RaidStats.Infrastructure → Database (LINQ2DB), services, business logic
GW2RaidStats.Core         → Shared models, DTOs, Elite Insights log parsing types
GW2RaidStats.Processor    → Background worker that runs GW2 Elite Insights CLI
```

### Multi-Process Design

- **Server** (app container): Hosts API + serves Blazor WASM client
- **Processor** (processor container): Background worker polling for `.zevtc` files, runs GW2 Elite Insights CLI to parse logs

### Data Flow

1. User uploads `.zevtc` file (web UI) or drops into `data/gw2-logs/queue/pending/`
2. Processor picks up file → runs GW2 Elite Insights → generates JSON + HTML reports
3. Processor stores reports in `data/gw2-logs/encounters/{year}/{month}/{id}/`
4. `LogImportService` parses the JSON and imports encounter data into PostgreSQL

### Key Services (Infrastructure/Services/)

- `LogImportService` / `BulkImportService` - Parse Elite Insights JSON, import to DB
- `StatsService` - Overall guild statistics
- `BossStatsService` - Per-boss metrics
- `LeaderboardService` - DPS rankings (guild members only)
- `LogSearchService` - Advanced log filtering
- `PlayerProfileService` - Individual player stats
- `RecapService` / `PlayerRecapService` - Yearly recap generation

### Database Entities (Infrastructure/Database/Entities/)

- `EncounterEntity` - Raid encounters
- `PlayerEntity` - Guild members
- `PlayerEncounterEntity` - Per-player stats per encounter
- `MechanicEventEntity` - Tracked mechanics from logs
- `IncludedPlayerEntity` - Accounts marked as guild members (filters out pugs)
- `IgnoredBossEntity` - Encounters excluded from stats

## File Paths

Queue folders for log processing:
- `data/gw2-logs/queue/pending/` - Drop new logs here
- `data/gw2-logs/queue/processing/` - Currently being parsed
- `data/gw2-logs/queue/failed/` - Failed (with `.error.txt` files)
- `data/gw2-logs/encounters/` - Processed encounters with reports

## Code Conventions

- Entities use `*Entity` suffix
- Services use `*Service` suffix
- Configuration classes use `*Options` suffix
- Controllers follow RESTful patterns at `/api/` routes
- Swagger available at `/swagger` in development

## Domain Knowledge

- GW2 Elite Insights CLI (v3.17.0.0) is the external tool that parses arcdps logs
- `WingMapping.cs` contains boss/wing ID mappings for GW2 raid encounters
- Leaderboards explicitly filter to guild members only (no pugs)
- "Recap" features generate Spotify Wrapped-style yearly summaries
