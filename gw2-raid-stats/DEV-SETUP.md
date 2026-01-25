# Local Development Setup

## Prerequisites
- .NET 9 SDK
- Docker & Docker Compose
- (Optional) A few .zevtc log files to test with

## Quick Start

### 1. Start the database and processor
```bash
docker-compose -f docker-compose.dev.yml up -d --build
```

This starts:
- **PostgreSQL** on port 5432 (runs migrations automatically)
- **Processor** with GW2 Elite Insights (watches for .zevtc files)

### 2. Run the web application
```bash
cd src/GW2RaidStats.Server
dotnet run
```

The app will be available at `https://localhost:5001` (or `http://localhost:5000`)

### 3. Test raw log processing

**Option A: Via the UI**
1. Go to `https://localhost:5001/admin/upload`
2. Use the "Upload Raw Logs" section to upload .zevtc files
3. Watch the "Processing Queue" section for status

**Option B: Drop files directly**
1. Copy .zevtc files to `./data/gw2-logs/queue/pending/`
2. The processor will automatically pick them up

### 4. View processor logs
```bash
docker-compose -f docker-compose.dev.yml logs -f processor
```

## Directory Structure

When running locally, files are stored in `./data/gw2-logs/`:
```
data/gw2-logs/
├── queue/
│   ├── pending/      # Files waiting to be processed
│   ├── processing/   # Currently being parsed
│   └── failed/       # Failed to parse (with .error.txt files)
└── encounters/       # Processed encounters
    └── {year}/{month}/{id}/
        ├── log.zevtc     # Original log
        ├── report.json   # Parsed JSON
        └── report.html   # HTML report (viewable in browser)
```

## Useful Commands

```bash
# Rebuild processor after code changes
docker-compose -f docker-compose.dev.yml up -d --build processor

# Stop everything
docker-compose -f docker-compose.dev.yml down

# Stop and remove data (fresh start)
docker-compose -f docker-compose.dev.yml down -v
rm -rf ./data/gw2-logs/*

# View all container logs
docker-compose -f docker-compose.dev.yml logs -f

# Check queue status via API
curl http://localhost:5000/api/logs/queue/status
```

## Configuration

### Server (`src/GW2RaidStats.Server/appsettings.json`)
- `Storage:BasePath` - Where to store/serve encounter files
- `ConnectionStrings:DefaultConnection` - Database connection

### Processor (environment variables in docker-compose.dev.yml)
- `Processor__MaxConcurrentProcessing` - Parallel parse workers (default: 2)
- `Processor__PollingIntervalSeconds` - How often to check for new files

## Troubleshooting

### Processor not picking up files
- Check processor logs: `docker-compose -f docker-compose.dev.yml logs processor`
- Ensure files have `.zevtc` or `.evtc` extension
- Check the `queue/failed/` folder for error logs

### Database connection issues
- Ensure postgres is healthy: `docker-compose -f docker-compose.dev.yml ps`
- Check postgres logs: `docker-compose -f docker-compose.dev.yml logs postgres`

### GW2EI parsing errors
- Some very old logs may not parse correctly
- Check the `.error.txt` file in `queue/failed/` for details
