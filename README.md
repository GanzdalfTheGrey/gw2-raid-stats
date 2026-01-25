# GW2 Raid Stats

A self-hosted Guild Wars 2 raid statistics dashboard for guilds. Import arcdps logs, track performance, view leaderboards, and generate yearly recaps.

![.NET](https://img.shields.io/badge/.NET-9.0-512BD4)
![Blazor](https://img.shields.io/badge/Blazor-WebAssembly-512BD4)
![PostgreSQL](https://img.shields.io/badge/PostgreSQL-16-336791)
![Docker](https://img.shields.io/badge/Docker-Ready-2496ED)

## Features

### Log Management
- **Automatic Processing** - Drop `.zevtc` files into a queue folder for automatic parsing via GW2 Elite Insights
- **Web Upload** - Upload logs directly through the admin interface
- **Duplicate Detection** - Automatically skips already-imported logs
- **HTML Reports** - View detailed reports (like dps.report) directly in the app

### Statistics & Analytics
- **Player Profiles** - Individual stats, most played classes, personal bests
- **Boss Statistics** - Kill counts, success rates, fastest times, grouped by wing
- **Log Search** - Filter logs by boss, wing, mode, result, date range, and more
- **Leaderboards** - Per-boss DPS rankings (guild members only)

### Yearly Recap
- **Animated Presentation** - Spotify Wrapped-style guild year in review
- **Custom Awards** - Create fun awards based on any tracked mechanic
- **Player Recaps** - Individual yearly stats for each guild member

### Administration
- **Guild Member Management** - Define who's in the guild (filters out pugs from leaderboards)
- **Ignored Bosses** - Exclude specific encounters from statistics
- **Customizable Branding** - Guild name, tag, logo, and theme colors

## Quick Start

### Prerequisites
- Docker and Docker Compose
- (Optional) Cloudflare Tunnel for remote access

### Deployment

1. **Clone the repository**
   ```bash
   git clone https://github.com/a-gansemer/gw2-raid-stats.git
   cd gw2-raid-stats/gw2-raid-stats
   ```

2. **Configure environment**
   ```bash
   cp .env.example .env
   # Edit .env with your settings:
   # - POSTGRES_PASSWORD
   # - ADMIN_PASSWORD
   # - GUILD_NAME, GUILD_TAG
   # - CLOUDFLARE_TUNNEL_TOKEN (optional)
   ```

3. **Start the application**
   ```bash
   docker compose up -d
   ```

4. **Access the dashboard**
   - Local: `http://localhost:5000`
   - With Cloudflare Tunnel: Your configured domain

### Importing Logs

**Option 1: Web Upload**
- Navigate to Admin → Upload Logs
- Drag and drop `.zevtc` files

**Option 2: Queue Folder**
- Drop `.zevtc` files into `./data/gw2-logs/queue/pending/`
- The processor will automatically parse and import them

## Architecture

```
┌─────────────────┐     ┌─────────────────┐     ┌─────────────────┐
│  Blazor WASM    │────▶│  ASP.NET Core   │────▶│   PostgreSQL    │
│    Frontend     │     │      API        │     │    Database     │
└─────────────────┘     └─────────────────┘     └─────────────────┘
                               ▲
                               │
                        ┌──────┴──────┐
                        │  Processor  │
                        │  (GW2 EI)   │
                        └─────────────┘
```

### Projects

| Project | Description |
|---------|-------------|
| `GW2RaidStats.Client` | Blazor WebAssembly frontend (MudBlazor UI) |
| `GW2RaidStats.Server` | ASP.NET Core REST API |
| `GW2RaidStats.Infrastructure` | Database access, services, business logic |
| `GW2RaidStats.Core` | Shared models and utilities |
| `GW2RaidStats.Processor` | Background worker for log parsing |

## Development

### Prerequisites
- .NET 9.0 SDK
- PostgreSQL (or use Docker)
- Node.js (optional, for frontend tooling)

### Local Development

1. **Start the database**
   ```bash
   docker compose -f docker-compose.dev.yml up -d
   ```

2. **Run the server**
   ```bash
   cd src/GW2RaidStats.Server
   dotnet run
   ```

3. **Access the app**
   - https://localhost:5001

### Project Structure

```
gw2-raid-stats/
├── src/
│   ├── GW2RaidStats.Client/      # Blazor frontend
│   ├── GW2RaidStats.Server/      # API backend
│   ├── GW2RaidStats.Infrastructure/  # Data layer
│   ├── GW2RaidStats.Core/        # Shared code
│   └── GW2RaidStats.Processor/   # Log processor
├── data/
│   └── gw2-logs/
│       ├── queue/pending/        # Drop logs here
│       └── encounters/           # Processed logs
├── docker-compose.yml            # Production
├── docker-compose.dev.yml        # Development
└── Dockerfile                    # Server image
```

## Tech Stack

- **Frontend**: Blazor WebAssembly, MudBlazor
- **Backend**: ASP.NET Core 9.0, LINQ2DB
- **Database**: PostgreSQL 16
- **Log Parsing**: GW2 Elite Insights CLI
- **Deployment**: Docker, Cloudflare Tunnel

## Configuration

### Environment Variables

| Variable | Description | Default |
|----------|-------------|---------|
| `POSTGRES_PASSWORD` | Database password | (required) |
| `ADMIN_PASSWORD` | Admin panel password | (required) |
| `GUILD_NAME` | Your guild's name | `My Guild` |
| `GUILD_TAG` | Your guild's tag | `TAG` |
| `CLOUDFLARE_TUNNEL_TOKEN` | Cloudflare tunnel token | (optional) |

### Application Settings

See `appsettings.json` for additional configuration options including:
- Database connection string
- Storage paths
- Theme colors
- Processing options

## API Documentation

When running in development, Swagger UI is available at `/swagger`.

## Contributing

Contributions are welcome! Please feel free to submit issues and pull requests.

## License

MIT

## Acknowledgments

- [GW2 Elite Insights Parser](https://github.com/baaron4/GW2-Elite-Insights-Parser) for log parsing
- [MudBlazor](https://mudblazor.com/) for the UI component library
- [arcdps](https://www.deltaconnected.com/arcdps/) for combat logging
