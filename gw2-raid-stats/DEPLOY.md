# Deployment Guide

This guide covers deploying GW2 Raid Stats to your home network using Docker and Cloudflare Tunnel.

## Prerequisites

- Docker and Docker Compose installed
- A Cloudflare account with a domain
- Git installed

## Quick Start

### 1. Clone the Repository

```bash
git clone https://github.com/a-gansemer/gw2-raid-stats.git
cd gw2-raid-stats/gw2-raid-stats
```

### 2. Create Cloudflare Tunnel

1. Go to [Cloudflare Zero Trust Dashboard](https://one.dash.cloudflare.com/)
2. Navigate to **Access** → **Tunnels**
3. Click **Create a tunnel**
4. Name it (e.g., `gw2-raid-stats`)
5. Copy the tunnel token (you'll need this later)
6. Configure the public hostname:
   - **Subdomain**: `raids` (or your preference)
   - **Domain**: Select your domain
   - **Service**: `http://app:8080`

### 3. Configure Environment

```bash
# Copy example environment file
cp .env.example .env

# Edit with your values
nano .env
```

Set the following values in `.env`:

```env
# Generate a strong password for the database
DB_PASSWORD=your_secure_db_password

# Admin panel password
ADMIN_PASSWORD=your_admin_password

# Paste your Cloudflare tunnel token
TUNNEL_TOKEN=eyJhIjoiYWNj...

# Your guild info
GUILD_NAME=My Guild
GUILD_TAG=TAG
```

### 4. Start the Services

```bash
# Pull the latest images and start
docker compose up -d

# Check logs
docker compose logs -f

# Check health
docker compose ps
```

### 5. Initialize the Database

The database schema is created automatically on first run. If you need to run migrations manually:

```bash
# Connect to the database
docker compose exec postgres psql -U app -d raidstats

# Or run a SQL file
docker compose exec -T postgres psql -U app -d raidstats < schema.sql
```

## Accessing the App

- **Public URL**: `https://raids.yourdomain.com` (via Cloudflare Tunnel)
- **Local URL**: `http://localhost:8080` (if port 8080 is exposed)

## Administration

### Updating

```bash
# Pull latest image
docker compose pull

# Restart with new image
docker compose up -d
```

### Database Backups

```bash
# Create a backup
docker compose exec postgres pg_dump -U app raidstats > ./data/backups/backup_$(date +%Y%m%d).sql

# Restore from backup
docker compose exec -T postgres psql -U app -d raidstats < ./data/backups/backup_20240101.sql
```

### View Logs

```bash
# All services
docker compose logs -f

# Specific service
docker compose logs -f app
docker compose logs -f postgres
docker compose logs -f cloudflared
```

### Stop Services

```bash
# Stop all
docker compose down

# Stop and remove volumes (WARNING: deletes data)
docker compose down -v
```

## Troubleshooting

### Tunnel Not Connecting

1. Check the tunnel token is correct in `.env`
2. Verify tunnel status in Cloudflare dashboard
3. Check cloudflared logs: `docker compose logs cloudflared`

### Database Connection Issues

1. Ensure postgres is healthy: `docker compose ps`
2. Check postgres logs: `docker compose logs postgres`
3. Verify DB_PASSWORD matches in `.env`

### App Not Starting

1. Check app logs: `docker compose logs app`
2. Verify health endpoint: `curl http://localhost:8080/api/health`
3. Ensure database is ready before app starts

## CI/CD with GitHub Actions

The repository includes a GitHub Actions workflow that:

1. Builds and tests on every push/PR
2. Builds and pushes Docker image to GitHub Container Registry on main branch

### Auto-Deploy Setup (Optional)

To automatically deploy when a new image is pushed:

1. Install [Watchtower](https://containrrr.dev/watchtower/) on your server
2. Or use a webhook to trigger `docker compose pull && docker compose up -d`

## Security Notes

- The admin password protects the admin panel (upload, manage, recap stats)
- All traffic through Cloudflare Tunnel is encrypted
- Database is not exposed externally (internal network only)
- App runs as non-root user in container
