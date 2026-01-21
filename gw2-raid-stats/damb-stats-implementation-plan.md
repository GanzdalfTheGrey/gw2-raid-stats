# GW2 Raid Stats - Implementation Plan

## Project Overview

**Project:** GW2 Raid Stats — Self-hosted raid statistics dashboard for Guild Wars 2 guilds  
**Repository:** `gw2-raid-stats` (public, open source)  
**License:** MIT  
**First Deployment:** Bad Memory Gang [DAMB] at `damb.ganzhomelab.com`

### What It Does
- Import arcdps/Elite Insights logs and parse statistics
- Track boss kills, player performance, and mechanics
- Searchable/filterable stats explorer
- Per-player, per-boss, per-class breakdowns
- Configurable yearly recap with fun stats and awards
- Fully self-hosted — each guild runs their own instance with their own data

---

## Tech Stack

| Layer | Technology |
|-------|------------|
| Frontend | Blazor WebAssembly + MudBlazor + MudBlazor.Extensions |
| UI Framework | MudBlazor (components, theming, charts) |
| UI Enhancements | MudBlazor.Extensions (animations, dialogs, file handling, etc.) |
| Theme | Light/Dark toggle via MudThemeProvider (colors configurable) |
| Backend | ASP.NET Core Web API |
| Database | PostgreSQL |
| Data Access | linq2db |
| Migrations | SQL scripts (or FluentMigrator) |
| Containerization | Docker + Docker Compose |
| Reverse Proxy/SSL | Cloudflare Tunnel (or any reverse proxy) |
| Source Control | GitHub (a-gansemer/gw2-raid-stats) |
| CI/CD | GitHub Actions (auto-deploy on push to main) |
| Hosting | Self-hosted (Proxmox, VPS, etc.) |

**Future:** Discord bot integration

---

## Configuration (Guild-Specific Settings)

All guild-specific settings live in `appsettings.json` (or environment variables):

```json
{
  "Guild": {
    "Name": "Bad Memory Gang",
    "Tag": "DAMB",
    "Logo": "/images/guild-logo.png",
    "Website": "https://example.com",
    "Discord": "https://discord.gg/example",
    "Description": "A GW2 PvE raiding guild"
  },
  "Theme": {
    "PrimaryColor": "#AA0404",
    "SecondaryColor": "#D4AF37",
    "DefaultDarkMode": true
  },
  "Recap": {
    "Enabled": true,
    "Year": 2025,
    "Published": false
  },
  "ConnectionStrings": {
    "DefaultConnection": "Host=postgres;Database=raidstats;Username=app;Password=SECRET"
  }
}
```

### Configurable Fun Stats / Awards

Awards are defined in a separate config file (`awards.json`) so guilds can customize:

```json
{
  "Awards": [
    {
      "Id": "oil-connoisseur",
      "Name": "Oil Connoisseur",
      "Description": "Most oil puddles stepped in",
      "Type": "MechanicCount",
      "Mechanic": "Oil",
      "BossFilter": "Deimos",
      "Icon": "local_fire_department"
    },
    {
      "Id": "floor-inspector",
      "Name": "Floor Inspector", 
      "Description": "Most time spent dead",
      "Type": "StatSum",
      "Stat": "DeathDurationMs",
      "Icon": "hotel"
    },
    {
      "Id": "rezzer-mvp",
      "Name": "Rezzer MVP",
      "Description": "Most resurrects",
      "Type": "StatSum",
      "Stat": "Resurrects",
      "Icon": "favorite"
    },
    {
      "Id": "dps-champion",
      "Name": "DPS Champion",
      "Description": "Highest single DPS pull",
      "Type": "StatMax",
      "Stat": "Dps",
      "Icon": "bolt"
    }
  ]
}
```

This allows any guild to:
- Add/remove awards
- Track different mechanics
- Customize names and icons

---

## Features (MVP)

### Home Dashboard
- [ ] **Guild Branding** — configurable name, logo, colors
- [ ] **Recent Activity** — last 10 kills/wipes with quick stats
- [ ] **This Week's Highlights** — top DPS, most kills, MVPs
- [ ] **Guild Quick Stats** — total kills, success rate, active raiders
- [ ] **Quick Links** — jump to bosses, players, explorer

### Stats Explorer (Search/Filter Page)
Full filtering capability for deep-diving into data:

| Filter | Type | Options |
|--------|------|---------|
| Boss | Dropdown | All bosses |
| Wing | Dropdown | 1-8 |
| Player | Dropdown | All guild members |
| Class/Spec | Dropdown | All professions |
| Date Range | Date picker | From/To |
| Mode | Multi-select | Normal, CM, LCM |
| Result | Toggle | All, Success only, Wipes only |

- [ ] **Results Table** — sortable, paginated encounter list
- [ ] **Aggregate Stats** — updates based on current filters (avg DPS, success rate, etc.)
- [ ] **Export** — download filtered results as CSV (future nice-to-have)

### Core Stats Views
- [ ] **Guild Overview** — aggregate stats for the guild
- [ ] **Individual Player Stats** — per-player performance breakdown
- [ ] **Per-Boss Stats** — success rates, kill times, DPS benchmarks
- [ ] **Per-Wing Stats** — wing completion rates, progression tracking
- [ ] **Per-Class/Spec Stats** — class representation, performance by spec
- [ ] **Leaderboards** — top DPS, most kills, fastest times, etc.

### Yearly Recap (Configurable)
A special curated page celebrating the year's achievements:

**Guild Totals:**
- Total bosses killed
- Total wipes
- Total raid hours
- Total damage dealt

**Awards (Configurable via awards.json):**
- Define custom awards based on mechanics or stats
- Each guild can customize their own award categories
- Default awards included (MVP, Rezzer, DPS Champion, etc.)

**Hall of Shame (Configurable):**
- Track any mechanic as a "fun fail" stat
- Default examples: oils, deaths, downs
- Guilds can add boss-specific mechanics

**Note:** Recap page visibility controlled by config (`Recap.Published`). Can be previewed by admins before publishing.

### Other Pages
- [ ] **About** — guild info from config (description, links, recruitment)
- [ ] **Admin** — log upload, data management (protected)

---

## Data Model (Based on Actual JSON Structure)

### JSON Field Mappings (gw2insights / Elite Insights)

Based on your MO CM log, here are the exact field paths:

#### Encounter-Level Fields
| Field | JSON Path | Example Value |
|-------|-----------|---------------|
| Boss Name | `fightName` | "Mursaat Overseer CM" |
| Is CM | `isCM` | true |
| Is Legendary CM | `isLegendaryCM` | false |
| Success | `success` | true |
| Duration (ms) | `durationMS` | 138275 |
| Duration (string) | `duration` | "02m 18s 275ms" |
| Start Time | `timeStartStd` | "2026-01-19 21:20:01 -06:00" |
| End Time | `timeEndStd` | "2026-01-19 21:22:21 -06:00" |
| Recorded By | `recordedAccountBy` | "Ganz.3917" |
| Trigger ID | `triggerID` | 17172 (boss ID) |
| Log Icon | `fightIcon` | URL to boss icon |

#### Player-Level Fields
| Field | JSON Path | Example |
|-------|-----------|---------|
| Character Name | `players[].name` | "Oops All Ganz" |
| Account Name | `players[].account` | "Ganz.3917" |
| Profession/Spec | `players[].profession` | "Conduit" |
| Squad Group | `players[].group` | 1 |
| Weapons | `players[].weapons` | ["Rifle", "2Hand", ...] |

#### Player Stats (from `players[].dpsAll[0]`)
| Field | JSON Path | Description |
|-------|-----------|-------------|
| Total DPS | `dps` | Combined DPS |
| Total Damage | `damage` | Total damage dealt |
| Power DPS | `powerDps` | Direct damage DPS |
| Condi DPS | `condiDps` | Condition damage DPS |
| Breakbar Damage | `breakbarDamage` | CC damage |

#### Player Defenses (from `players[].defenses[0]`)
| Field | JSON Path | Description |
|-------|-----------|-------------|
| Deaths | `deadCount` | Times fully dead |
| Death Duration | `deadDuration` | Time spent dead (ms) |
| Downs | `downCount` | Times downed |
| Down Duration | `downDuration` | Time spent downed (ms) |
| Damage Taken | `damageTaken` | Total damage received |
| Dodges | `dodgeCount` | Dodge count |
| Blocks | `blockedCount` | Blocked attacks |
| Evades | `evadedCount` | Evaded attacks |

#### Player Support (from `players[].support[0]`)
| Field | JSON Path | Description |
|-------|-----------|-------------|
| Resurrects | `resurrects` | Allies resurrected |
| Resurrect Time | `resurrectTime` | Time spent rezzing |
| Condi Cleanse | `condiCleanse` | Conditions cleansed |
| Boon Strips | `boonStrips` | Boons stripped from enemies |
| Stun Breaks | `stunBreak` | Stuns broken |

#### Mechanics (from `mechanics[]`)
| Field | JSON Path | Description |
|-------|-----------|-------------|
| Mechanic Name | `name` | Short name (e.g., "Jade") |
| Full Name | `fullName` | Full name (e.g., "Jade Aura") |
| Description | `description` | What it means |
| Events | `mechanicsData[]` | Array of {actor, time} |

**MO CM Mechanics Found:**
- `Dead` — Player died
- `Downed` — Player downed
- `Got up` — Player rallied
- `Res` — Player resurrected someone
- `Jade` — Jade Soldier's Aura hit
- `Jade Expl` — Jade Soldier's Death Explosion
- `Protect (SAK)` — Took protect special action
- `Dispel (SAK)` — Took dispel special action
- `Claim (SAK)` — Took claim special action

---

### Database Schema

```sql
-- Players table
CREATE TABLE players (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    account_name VARCHAR(50) NOT NULL UNIQUE,  -- e.g., "Ganz.3917"
    display_name VARCHAR(100),                  -- optional friendly name
    first_seen TIMESTAMPTZ NOT NULL,
    created_at TIMESTAMPTZ DEFAULT NOW()
);

-- Encounters table
CREATE TABLE encounters (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    trigger_id INT NOT NULL,                    -- boss ID from JSON
    boss_name VARCHAR(100) NOT NULL,            -- e.g., "Mursaat Overseer CM"
    wing INT,                                   -- 1-7 (null for strikes/fractals)
    is_cm BOOLEAN NOT NULL DEFAULT FALSE,
    is_legendary_cm BOOLEAN NOT NULL DEFAULT FALSE,
    success BOOLEAN NOT NULL,
    duration_ms INT NOT NULL,
    encounter_time TIMESTAMPTZ NOT NULL,
    recorded_by VARCHAR(50),                    -- account that recorded
    log_url VARCHAR(500),                       -- dps.report link if available
    json_hash VARCHAR(64),                      -- SHA256 of JSON for deduplication
    icon_url VARCHAR(500),
    created_at TIMESTAMPTZ DEFAULT NOW(),
    
    UNIQUE(json_hash)                           -- prevent duplicate imports
);

-- Player encounter stats (join table with stats)
CREATE TABLE player_encounters (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    player_id UUID NOT NULL REFERENCES players(id),
    encounter_id UUID NOT NULL REFERENCES encounters(id),
    character_name VARCHAR(100) NOT NULL,       -- in-game character name
    profession VARCHAR(50) NOT NULL,            -- spec name (e.g., "Conduit")
    squad_group INT,
    
    -- DPS stats
    dps INT NOT NULL,
    damage BIGINT NOT NULL,
    power_dps INT,
    condi_dps INT,
    breakbar_damage DECIMAL(10,2),
    
    -- Defense stats
    deaths INT NOT NULL DEFAULT 0,
    death_duration_ms INT DEFAULT 0,
    downs INT NOT NULL DEFAULT 0,
    down_duration_ms INT DEFAULT 0,
    damage_taken BIGINT DEFAULT 0,
    
    -- Support stats
    resurrects INT DEFAULT 0,
    condi_cleanse INT DEFAULT 0,
    boon_strips INT DEFAULT 0,
    
    -- Weapons used
    weapons JSONB,                              -- ["Rifle", "2Hand", ...]
    
    -- Boss-specific mechanics (flexible)
    mechanics JSONB,                            -- {"jade_hits": 5, "dispels": 2, ...}
    
    created_at TIMESTAMPTZ DEFAULT NOW(),
    
    UNIQUE(player_id, encounter_id)
);

-- Mechanics summary (for fun stats)
CREATE TABLE mechanic_events (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    encounter_id UUID NOT NULL REFERENCES encounters(id),
    player_id UUID REFERENCES players(id),      -- null for non-player mechanics
    mechanic_name VARCHAR(100) NOT NULL,
    mechanic_full_name VARCHAR(200),
    description TEXT,
    event_time_ms INT NOT NULL,                 -- time in encounter
    created_at TIMESTAMPTZ DEFAULT NOW()
);

-- Indexes for common queries
CREATE INDEX idx_encounters_boss ON encounters(boss_name);
CREATE INDEX idx_encounters_time ON encounters(encounter_time);
CREATE INDEX idx_encounters_success ON encounters(success);
CREATE INDEX idx_player_encounters_player ON player_encounters(player_id);
CREATE INDEX idx_player_encounters_encounter ON player_encounters(encounter_id);
CREATE INDEX idx_player_encounters_profession ON player_encounters(profession);
CREATE INDEX idx_mechanic_events_encounter ON mechanic_events(encounter_id);
CREATE INDEX idx_mechanic_events_player ON mechanic_events(player_id);
CREATE INDEX idx_mechanic_events_name ON mechanic_events(mechanic_name);
```

### Wing Mapping (for categorization)

```csharp
public static class WingMapping
{
    public static int? GetWing(int triggerId) => triggerId switch
    {
        // Wing 1 - Spirit Vale
        15438 => 1,  // Vale Guardian
        15429 => 1,  // Gorseval
        15375 => 1,  // Sabetha
        
        // Wing 2 - Salvation Pass
        16123 => 2,  // Slothasor
        16088 => 2,  // Bandit Trio
        16137 => 2,  // Matthias
        
        // Wing 3 - Stronghold of the Faithful
        16235 => 3,  // Escort
        16246 => 3,  // Keep Construct
        16286 => 3,  // Twisted Castle
        16253 => 3,  // Xera
        
        // Wing 4 - Bastion of the Penitent
        17194 => 4,  // Cairn
        17172 => 4,  // Mursaat Overseer (your log!)
        17188 => 4,  // Samarog
        17154 => 4,  // Deimos
        
        // Wing 5 - Hall of Chains
        19767 => 5,  // Soulless Horror
        19828 => 5,  // River of Souls
        19536 => 5,  // Statues of Grenth
        19450 => 5,  // Dhuum
        
        // Wing 6 - Mythwright Gambit
        21105 => 6,  // Conjured Amalgamate
        21089 => 6,  // Twin Largos
        20934 => 6,  // Qadim
        
        // Wing 7 - The Key of Ahdashim
        22006 => 7,  // Cardinal Adina
        21964 => 7,  // Cardinal Sabir
        22000 => 7,  // Qadim the Peerless
        
        // Wing 8 - Mount Balrior
        26725 => 8,  // Greer
        26774 => 8,  // Decima
        26712 => 8,  // Ura
        
        _ => null    // Strikes, fractals, etc.
    };
}
```

### Notes on Schema
- `json_hash` prevents importing the same log twice
- `mechanics` JSONB column allows flexible per-boss tracking without schema changes
- `mechanic_events` table enables "fun stats" queries like "total oils stepped in"
- Wing mapping handles categorization automatically based on trigger ID
- All timestamps stored in UTC with timezone info

---

## Data Pipeline

### Initial Import (2025 Logs)

```
NAS (JSON files) → Import Script → PostgreSQL
```

1. Mount NAS share to Proxmox container (or copy files)
2. Run one-time import script to parse all JSON files
3. Populate database with historical data

### Ongoing Weekly Updates

**Option A: Manual Upload**
- Admin page in the app to upload new JSON files
- Parses and imports on upload

**Option B: Automated Sync**
- Script watches NAS folder for new files
- Auto-imports new logs on a schedule (cron job)
- Preferred for "live stats" goal

**Recommendation:** Start with Option A, add Option B later

---

## Infrastructure Setup

### Proxmox LXC Container

| Setting | Value |
|---------|-------|
| Template | Ubuntu 22.04 or Debian 12 |
| Hostname | `damb-stats` |
| CPU | 4 cores |
| RAM | 4 GB |
| Swap | 2 GB |
| Disk | 50 GB |
| Network | DHCP or static IP (your preference) |

### Software to Install

```bash
# Update system
apt update && apt upgrade -y

# Install Docker
curl -fsSL https://get.docker.com | sh

# Install Docker Compose
apt install docker-compose-plugin -y

# Add your user to docker group
usermod -aG docker $USER

# Install cloudflared
curl -L https://github.com/cloudflare/cloudflared/releases/latest/download/cloudflared-linux-amd64.deb -o cloudflared.deb
dpkg -i cloudflared.deb
```

### Directory Structure on Server

```
/opt/damb-stats/
├── docker-compose.yml
├── .env                    # environment variables (DB passwords, etc.)
└── data/
    └── postgres/           # PostgreSQL data directory
```

---

## Docker Configuration

### docker-compose.yml

```yaml
version: '3.8'

services:
  app:
    image: ghcr.io/a-gansemer/gw2-raid-stats:latest
    container_name: gw2-raid-stats
    restart: unless-stopped
    ports:
      - "8080:8080"
    environment:
      - ASPNETCORE_ENVIRONMENT=Production
      - ConnectionStrings__DefaultConnection=Host=postgres;Database=raidstats;Username=app;Password=${DB_PASSWORD}
      # Guild configuration can be set via environment variables
      - Guild__Name=${GUILD_NAME:-My Guild}
      - Guild__Tag=${GUILD_TAG:-TAG}
    volumes:
      - ./config/appsettings.Production.json:/app/appsettings.Production.json:ro
      - ./config/awards.json:/app/awards.json:ro
      - ./images:/app/wwwroot/images/custom:ro
    depends_on:
      - postgres

  postgres:
    image: postgres:16-alpine
    container_name: gw2-raid-stats-db
    restart: unless-stopped
    environment:
      - POSTGRES_DB=raidstats
      - POSTGRES_USER=app
      - POSTGRES_PASSWORD=${DB_PASSWORD}
    volumes:
      - ./data/postgres:/var/lib/postgresql/data
    ports:
      - "127.0.0.1:5432:5432"  # Only accessible locally

  cloudflared:
    image: cloudflare/cloudflared:latest
    container_name: gw2-raid-stats-tunnel
    restart: unless-stopped
    command: tunnel --no-autoupdate run --token ${TUNNEL_TOKEN}
```

### .env (DO NOT COMMIT TO GIT)

```env
DB_PASSWORD=your_secure_password_here
TUNNEL_TOKEN=your_cloudflare_tunnel_token
GUILD_NAME=Bad Memory Gang
GUILD_TAG=DAMB
```

---

## Project Structure

```
GW2RaidStats/
├── src/
│   ├── GW2RaidStats.Client/         # Blazor WASM project
│   │   ├── Pages/
│   │   │   ├── Index.razor          # Home Dashboard
│   │   │   ├── Explorer.razor       # Stats Explorer (search/filter)
│   │   │   ├── Guild.razor          # Guild overview stats
│   │   │   ├── Players.razor        # Player list
│   │   │   ├── Player.razor         # Individual player stats
│   │   │   ├── Bosses.razor         # Boss list
│   │   │   ├── Boss.razor           # Per-boss stats
│   │   │   ├── Wings.razor          # Per-wing stats
│   │   │   ├── Classes.razor        # Per-class stats
│   │   │   ├── Leaderboards.razor   # Top performers
│   │   │   ├── Recap.razor          # Yearly Recap (configurable year)
│   │   │   ├── About.razor          # About the guild (from config)
│   │   │   └── Admin/
│   │   │       ├── Upload.razor     # Log upload page
│   │   │       └── Manage.razor     # Data management
│   │   ├── Components/
│   │   │   ├── StatCard.razor       # Reusable stat display card
│   │   │   ├── PlayerTable.razor    # MudTable for player lists
│   │   │   ├── BossCard.razor       # Boss info card
│   │   │   ├── AwardCard.razor      # Award/fun stat highlight card
│   │   │   └── Charts/
│   │   │       ├── DpsChart.razor   # MudChart for DPS over time
│   │   │       └── ClassPieChart.razor
│   │   ├── Layout/
│   │   │   ├── MainLayout.razor     # MudLayout with nav drawer
│   │   │   └── NavMenu.razor        # MudNavMenu
│   │   ├── Shared/
│   │   │   └── ThemeProvider.razor  # Light/Dark theme toggle
│   │   ├── Services/
│   │   │   ├── ApiClient.cs
│   │   │   └── GuildConfigService.cs  # Reads guild config for UI
│   │   ├── wwwroot/
│   │   │   ├── css/
│   │   │   │   └── app.css          # Custom styles (minimal)
│   │   │   └── images/
│   │   │       └── default-logo.png # Fallback logo
│   │   ├── _Imports.razor           # Global usings including MudBlazor
│   │   └── Program.cs               # MudBlazor service registration
│   │
│   ├── GW2RaidStats.Server/         # ASP.NET Core API + Host
│   │   ├── Controllers/
│   │   │   ├── PlayersController.cs
│   │   │   ├── EncountersController.cs
│   │   │   ├── StatsController.cs
│   │   │   ├── ConfigController.cs  # Serves guild config to client
│   │   │   └── ImportController.cs
│   │   ├── appsettings.json         # Guild config, connection strings
│   │   ├── awards.json              # Configurable awards/fun stats
│   │   └── Program.cs
│   │
│   ├── GW2RaidStats.Core/           # Shared models & interfaces
│   │   ├── Models/
│   │   │   ├── Player.cs
│   │   │   ├── Encounter.cs
│   │   │   ├── MechanicEvent.cs
│   │   │   └── Award.cs
│   │   ├── DTOs/
│   │   └── Configuration/
│   │       ├── GuildConfig.cs
│   │       ├── ThemeConfig.cs
│   │       └── AwardConfig.cs
│   │
│   └── GW2RaidStats.Infrastructure/ # Database & external services
│       ├── Database/
│       │   ├── RaidStatsDb.cs       # linq2db DataConnection
│       │   └── Tables/              # Generated/scaffolded table classes
│       │       ├── Player.cs
│       │       ├── Encounter.cs
│       │       ├── PlayerEncounter.cs
│       │       └── MechanicEvent.cs
│       ├── Migrations/              # SQL migration scripts
│       │   ├── 001_InitialSchema.sql
│       │   └── 002_AddIndexes.sql
│       ├── Repositories/
│       └── Services/
│           ├── LogImportService.cs
│           └── AwardCalculationService.cs
│
├── tests/
│   └── GW2RaidStats.Tests/
│
├── docs/
│   ├── deployment.md                # How to deploy your own instance
│   ├── configuration.md             # All config options explained
│   └── awards.md                    # How to create custom awards
│
├── .github/
│   └── workflows/
│       └── deploy.yml
│
├── Dockerfile
├── docker-compose.yml               # Production deployment
├── docker-compose.dev.yml           # Local development
├── .gitignore
├── LICENSE                          # MIT
├── README.md                        # Project overview, quick start
└── GW2RaidStats.sln
```

---

## MudBlazor + Extensions Setup

### Package Installation

In `GW2RaidStats.Client.csproj`:
```xml
<PackageReference Include="MudBlazor" Version="7.*" />
<PackageReference Include="MudBlazor.Extensions" Version="2.*" />
```

### Program.cs Configuration

```csharp
using MudBlazor.Services;
using MudExtensions.Services;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");

// Add MudBlazor
builder.Services.AddMudServices(config =>
{
    config.SnackbarConfiguration.PositionClass = Defaults.Classes.Position.BottomRight;
    config.SnackbarConfiguration.PreventDuplicates = false;
    config.SnackbarConfiguration.NewestOnTop = true;
    config.SnackbarConfiguration.ShowCloseIcon = true;
    config.SnackbarConfiguration.VisibleStateDuration = 5000;
    config.SnackbarConfiguration.HideTransitionDuration = 500;
    config.SnackbarConfiguration.ShowTransitionDuration = 500;
});

// Add MudBlazor.Extensions
builder.Services.AddMudExtensions();

// Add your API client
builder.Services.AddScoped(sp => new HttpClient 
{ 
    BaseAddress = new Uri(builder.HostEnvironment.BaseAddress) 
});

await builder.Build().RunAsync();
```

### _Imports.razor

```razor
@using MudBlazor
@using MudExtensions
@using MudExtensions.Enums
```

### index.html (add CSS/JS)

```html
<link href="_content/MudBlazor/MudBlazor.min.css" rel="stylesheet" />
<link href="_content/MudBlazor.Extensions/MudExtensions.min.css" rel="stylesheet" />

<!-- Before closing </body> -->
<script src="_content/MudBlazor/MudBlazor.min.js"></script>
<script src="_content/MudBlazor.Extensions/MudExtensions.min.js"></script>
```

### Useful MudBlazor.Extensions Components

| Component | Use Case |
|-----------|----------|
| `MudLoading` | Full-page loading with animations |
| `MudAnimate` | Animate any element on visibility |
| `MudSplitter` | Resizable split panels |
| `MudGallery` | Image galleries (for boss icons?) |
| `MudSpeedDial` | Floating action buttons |
| `MudTextFieldExtended` | Enhanced text input with debounce |
| `MudSelectExtended` | Searchable select with virtualization |
| `MudListExtended` | Drag & drop lists |
| `MudStepper` | Multi-step forms (good for log upload wizard) |
| `MudWheelPicker` | Wheel-style date/time picker |

### MainLayout.razor (with theme toggle)

```razor
@inherits LayoutComponentBase

<MudThemeProvider @bind-IsDarkMode="@_isDarkMode" Theme="_theme" />
<MudPopoverProvider />
<MudDialogProvider />
<MudSnackbarProvider />

<MudLayout>
    <MudAppBar Elevation="1">
        <MudIconButton Icon="@Icons.Material.Filled.Menu" 
                       Color="Color.Inherit" 
                       Edge="Edge.Start" 
                       OnClick="@ToggleDrawer" />
        <MudText Typo="Typo.h5" Class="ml-3">DAMB Stats</MudText>
        <MudSpacer />
        <MudIconButton Icon="@(_isDarkMode ? Icons.Material.Filled.LightMode : Icons.Material.Filled.DarkMode)"
                       Color="Color.Inherit"
                       OnClick="@ToggleDarkMode" />
    </MudAppBar>
    
    <MudDrawer @bind-Open="_drawerOpen" ClipMode="DrawerClipMode.Always" Elevation="2">
        <NavMenu />
    </MudDrawer>
    
    <MudMainContent>
        <MudContainer MaxWidth="MaxWidth.ExtraLarge" Class="my-4">
            @Body
        </MudContainer>
    </MudMainContent>
</MudLayout>

@code {
    private bool _drawerOpen = true;
    private bool _isDarkMode = true;  // Default to dark mode
    
    private MudTheme _theme = new()
    {
        PaletteLight = new PaletteLight()
        {
            Primary = "#AA0404",      // GW2-ish red
            Secondary = "#D4AF37",    // Gold accent
            AppbarBackground = "#AA0404"
        },
        PaletteDark = new PaletteDark()
        {
            Primary = "#AA0404",
            Secondary = "#D4AF37",
            Surface = "#1e1e1e",
            Background = "#121212",
            AppbarBackground = "#1e1e1e"
        }
    };
    
    private void ToggleDrawer() => _drawerOpen = !_drawerOpen;
    private void ToggleDarkMode() => _isDarkMode = !_isDarkMode;
}
```

### Example Component: StatCard.razor

```razor
<MudCard Elevation="2" Class="ma-2">
    <MudCardContent>
        <MudText Typo="Typo.subtitle2" Color="Color.Secondary">@Title</MudText>
        <MudText Typo="Typo.h4">@Value</MudText>
        @if (!string.IsNullOrEmpty(Subtitle))
        {
            <MudText Typo="Typo.caption" Color="Color.TextSecondary">@Subtitle</MudText>
        }
    </MudCardContent>
</MudCard>

@code {
    [Parameter] public string Title { get; set; }
    [Parameter] public string Value { get; set; }
    [Parameter] public string? Subtitle { get; set; }
}
```

### Using MudBlazor Charts

```razor
@* Simple bar chart for DPS comparison *@
<MudChart ChartType="ChartType.Bar" 
          ChartSeries="@_series" 
          XAxisLabels="@_labels"
          Width="100%" 
          Height="300px" />

@code {
    private List<ChartSeries> _series = new()
    {
        new ChartSeries { Name = "DPS", Data = new double[] { 35416, 31898, 31742, 30575, 27136 } }
    };
    
    private string[] _labels = { "Oops All Ganz", "Rikka Dukat", "Coffee Poops", "Stevengamer", "Entvi" };
}
```

### Example: Animated Stat Cards with MudExtensions

```razor
<MudGrid>
    @foreach (var (stat, index) in _stats.Select((s, i) => (s, i)))
    {
        <MudItem xs="12" sm="6" md="3">
            <MudAnimate Trigger="@_loaded" 
                        Animation="AnimationType.FadeIn" 
                        Duration="0.5" 
                        Delay="@(index * 0.1)">
                <StatCard Title="@stat.Title" Value="@stat.Value" Icon="@stat.Icon" />
            </MudAnimate>
        </MudItem>
    }
</MudGrid>

@code {
    private bool _loaded = false;
    
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender)
        {
            _loaded = true;
            StateHasChanged();
        }
    }
}
```

### Example: Loading Overlay

```razor
<MudLoadingButton @bind-Loading="@_loading" 
                  Variant="Variant.Filled" 
                  Color="Color.Primary"
                  OnClick="@LoadData">
    <LoadingContent>Loading...</LoadingContent>
    <ChildContent>Load Stats</ChildContent>
</MudLoadingButton>

<MudOverlay @bind-Visible="_loading" DarkBackground="true" AutoClose="false">
    <MudProgressCircular Color="Color.Primary" Indeterminate="true" />
</MudOverlay>
```

### Example: Log Upload Wizard with Stepper

```razor
<MudStepper @bind-ActiveIndex="_stepIndex" Linear="true" Color="Color.Primary">
    <MudStep Title="Select Files" Icon="@Icons.Material.Filled.UploadFile">
        <MudFileUpload T="IReadOnlyList<IBrowserFile>" 
                       Accept=".json" 
                       MaximumFileCount="100"
                       OnFilesChanged="OnFilesSelected">
            <ActivatorContent>
                <MudPaper Outlined="true" Class="pa-8 d-flex flex-column align-center gap-4">
                    <MudIcon Icon="@Icons.Material.Filled.CloudUpload" Size="Size.Large" />
                    <MudText>Drag and drop JSON logs here or click to browse</MudText>
                    <MudButton Variant="Variant.Filled" Color="Color.Primary">
                        Select Files
                    </MudButton>
                </MudPaper>
            </ActivatorContent>
        </MudFileUpload>
    </MudStep>
    
    <MudStep Title="Review" Icon="@Icons.Material.Filled.Checklist">
        <MudText Typo="Typo.h6">@_files.Count files selected</MudText>
        <MudList T="string" Dense="true">
            @foreach (var file in _files.Take(10))
            {
                <MudListItem Icon="@Icons.Material.Filled.Description">
                    @file.Name (@(file.Size / 1024) KB)
                </MudListItem>
            }
            @if (_files.Count > 10)
            {
                <MudListItem>...and @(_files.Count - 10) more</MudListItem>
            }
        </MudList>
    </MudStep>
    
    <MudStep Title="Import" Icon="@Icons.Material.Filled.Sync">
        <MudProgressLinear Value="@_progress" Max="100" Color="Color.Primary" Rounded="true" Size="Size.Large">
            <MudText Typo="Typo.body2">@_progress% — @_currentFile</MudText>
        </MudProgressLinear>
        <MudText Class="mt-4">Imported @_importedCount of @_files.Count logs</MudText>
    </MudStep>
    
    <MudStep Title="Done" Icon="@Icons.Material.Filled.CheckCircle">
        <MudAnimate Animation="AnimationType.Pulse" Trigger="@(_stepIndex == 3)">
            <MudAlert Severity="Severity.Success">
                Successfully imported @_importedCount logs!
            </MudAlert>
        </MudAnimate>
    </MudStep>
</MudStepper>
```

### Example: Searchable Player Select

```razor
<MudSelectExtended T="string"
                   Label="Player"
                   @bind-Value="_selectedPlayer"
                   Clearable="true"
                   SearchBox="true"
                   SearchBoxPlaceholder="Search players..."
                   SearchBoxAutoFocus="true">
    @foreach (var player in _players)
    {
        <MudSelectItemExtended T="string" Value="@player">@player</MudSelectItemExtended>
    }
</MudSelectExtended>
```

### Example Page: Explorer.razor (Stats Explorer with Filters)

```razor
@page "/explorer"
@inject IStatsApiClient Api

<MudText Typo="Typo.h4" Class="mb-4">Stats Explorer</MudText>

<MudPaper Class="pa-4 mb-4">
    <MudGrid>
        <MudItem xs="12" sm="6" md="3">
            <MudSelect T="int?" Label="Wing" @bind-Value="_selectedWing" Clearable="true">
                @for (int i = 1; i <= 8; i++)
                {
                    <MudSelectItem Value="@((int?)i)">Wing @i</MudSelectItem>
                }
            </MudSelect>
        </MudItem>
        
        <MudItem xs="12" sm="6" md="3">
            <MudSelect T="string" Label="Boss" @bind-Value="_selectedBoss" Clearable="true">
                @foreach (var boss in _bosses)
                {
                    <MudSelectItem Value="@boss">@boss</MudSelectItem>
                }
            </MudSelect>
        </MudItem>
        
        <MudItem xs="12" sm="6" md="3">
            <MudSelect T="string" Label="Player" @bind-Value="_selectedPlayer" Clearable="true">
                @foreach (var player in _players)
                {
                    <MudSelectItem Value="@player">@player</MudSelectItem>
                }
            </MudSelect>
        </MudItem>
        
        <MudItem xs="12" sm="6" md="3">
            <MudSelect T="string" Label="Class/Spec" @bind-Value="_selectedSpec" Clearable="true">
                @foreach (var spec in _specs)
                {
                    <MudSelectItem Value="@spec">@spec</MudSelectItem>
                }
            </MudSelect>
        </MudItem>
        
        <MudItem xs="12" sm="6" md="3">
            <MudDateRangePicker Label="Date Range" @bind-DateRange="_dateRange" />
        </MudItem>
        
        <MudItem xs="12" sm="6" md="3">
            <MudSelect T="string" Label="Mode" @bind-Value="_selectedMode" Clearable="true">
                <MudSelectItem Value="@("Normal")">Normal</MudSelectItem>
                <MudSelectItem Value="@("CM")">Challenge Mode</MudSelectItem>
                <MudSelectItem Value="@("LCM")">Legendary CM</MudSelectItem>
            </MudSelect>
        </MudItem>
        
        <MudItem xs="12" sm="6" md="3">
            <MudSelect T="bool?" Label="Result" @bind-Value="_successFilter" Clearable="true">
                <MudSelectItem Value="@((bool?)true)">Success</MudSelectItem>
                <MudSelectItem Value="@((bool?)false)">Wipe</MudSelectItem>
            </MudSelect>
        </MudItem>
        
        <MudItem xs="12" sm="6" md="3" Class="d-flex align-end">
            <MudButton Variant="Variant.Filled" Color="Color.Primary" OnClick="@Search" Class="mr-2">
                Search
            </MudButton>
            <MudButton Variant="Variant.Outlined" OnClick="@ClearFilters">
                Clear
            </MudButton>
        </MudItem>
    </MudGrid>
</MudPaper>

@* Aggregate Stats for Current Filter *@
<MudGrid Class="mb-4">
    <MudItem xs="6" sm="3">
        <StatCard Title="Encounters" Value="@_aggregateStats.TotalEncounters.ToString()" />
    </MudItem>
    <MudItem xs="6" sm="3">
        <StatCard Title="Success Rate" Value="@($"{_aggregateStats.SuccessRate:P0}")" />
    </MudItem>
    <MudItem xs="6" sm="3">
        <StatCard Title="Avg DPS" Value="@($"{_aggregateStats.AvgDps:N0}")" />
    </MudItem>
    <MudItem xs="6" sm="3">
        <StatCard Title="Avg Duration" Value="@(_aggregateStats.AvgDuration.ToString(@"mm\:ss"))" />
    </MudItem>
</MudGrid>

@* Results Table *@
<MudTable Items="@_encounters" Hover="true" Striped="true" Loading="@_loading" LoadingProgressColor="Color.Primary">
    <HeaderContent>
        <MudTh><MudTableSortLabel SortBy="new Func<EncounterDto, object>(x => x.EncounterTime)">Date</MudTableSortLabel></MudTh>
        <MudTh>Boss</MudTh>
        <MudTh>Mode</MudTh>
        <MudTh>Result</MudTh>
        <MudTh>Duration</MudTh>
        <MudTh>Top DPS</MudTh>
    </HeaderContent>
    <RowTemplate>
        <MudTd DataLabel="Date">@context.EncounterTime.ToString("MMM dd, HH:mm")</MudTd>
        <MudTd DataLabel="Boss">@context.BossName</MudTd>
        <MudTd DataLabel="Mode">
            @if (context.IsLegendaryCM) { <MudChip Size="Size.Small" Color="Color.Error">LCM</MudChip> }
            else if (context.IsCM) { <MudChip Size="Size.Small" Color="Color.Warning">CM</MudChip> }
            else { <MudChip Size="Size.Small">Normal</MudChip> }
        </MudTd>
        <MudTd DataLabel="Result">
            @if (context.Success)
            {
                <MudIcon Icon="@Icons.Material.Filled.CheckCircle" Color="Color.Success" />
            }
            else
            {
                <MudIcon Icon="@Icons.Material.Filled.Cancel" Color="Color.Error" />
            }
        </MudTd>
        <MudTd DataLabel="Duration">@TimeSpan.FromMilliseconds(context.DurationMs).ToString(@"mm\:ss")</MudTd>
        <MudTd DataLabel="Top DPS">@context.TopDpsPlayer (@context.TopDps.ToString("N0"))</MudTd>
    </RowTemplate>
    <PagerContent>
        <MudTablePager />
    </PagerContent>
</MudTable>

@code {
    private int? _selectedWing;
    private string? _selectedBoss;
    private string? _selectedPlayer;
    private string? _selectedSpec;
    private string? _selectedMode;
    private bool? _successFilter;
    private DateRange? _dateRange;
    
    private List<string> _bosses = new();
    private List<string> _players = new();
    private List<string> _specs = new();
    
    private List<EncounterDto> _encounters = new();
    private AggregateStatsDto _aggregateStats = new();
    private bool _loading = false;
    
    protected override async Task OnInitializedAsync()
    {
        _bosses = await Api.GetBossNamesAsync();
        _players = await Api.GetPlayerNamesAsync();
        _specs = await Api.GetSpecNamesAsync();
        await Search();
    }
    
    private async Task Search()
    {
        _loading = true;
        var filter = new EncounterFilter
        {
            Wing = _selectedWing,
            BossName = _selectedBoss,
            PlayerAccount = _selectedPlayer,
            Profession = _selectedSpec,
            StartDate = _dateRange?.Start,
            EndDate = _dateRange?.End,
            IsCM = _selectedMode == "CM" || _selectedMode == "LCM",
            IsLCM = _selectedMode == "LCM",
            Success = _successFilter
        };
        
        _encounters = await Api.GetEncountersAsync(filter);
        _aggregateStats = await Api.GetAggregateStatsAsync(filter);
        _loading = false;
    }
    
    private async Task ClearFilters()
    {
        _selectedWing = null;
        _selectedBoss = null;
        _selectedPlayer = null;
        _selectedSpec = null;
        _selectedMode = null;
        _successFilter = null;
        _dateRange = null;
        await Search();
    }
}
```

---

## GitHub Actions CI/CD

### .github/workflows/deploy.yml

```yaml
name: Build and Deploy

on:
  push:
    branches: [ main ]

env:
  REGISTRY: ghcr.io
  IMAGE_NAME: ${{ github.repository }}

# Container will be: ghcr.io/a-gansemer/gw2-raid-stats:latest

jobs:
  build-and-deploy:
    runs-on: ubuntu-latest
    permissions:
      contents: read
      packages: write

    steps:
      - name: Checkout repository
        uses: actions/checkout@v4

      - name: Set up .NET
        uses: actions/setup-dotnet@v4
        with:
          dotnet-version: '8.0.x'

      - name: Run tests
        run: dotnet test --verbosity normal

      - name: Log in to GitHub Container Registry
        uses: docker/login-action@v3
        with:
          registry: ${{ env.REGISTRY }}
          username: ${{ github.actor }}
          password: ${{ secrets.GITHUB_TOKEN }}

      - name: Build and push Docker image
        uses: docker/build-push-action@v5
        with:
          context: .
          push: true
          tags: |
            ${{ env.REGISTRY }}/${{ env.IMAGE_NAME }}:latest
            ${{ env.REGISTRY }}/${{ env.IMAGE_NAME }}:${{ github.sha }}

      - name: Deploy to server
        uses: appleboy/ssh-action@v1
        with:
          host: ${{ secrets.SERVER_HOST }}
          username: ${{ secrets.SERVER_USER }}
          key: ${{ secrets.SERVER_SSH_KEY }}
          script: |
            cd /opt/gw2-raid-stats
            docker compose pull
            docker compose up -d --remove-orphans
            docker image prune -f
```

### GitHub Secrets Required

| Secret | Description |
|--------|-------------|
| `SERVER_HOST` | IP or hostname of your Proxmox container |
| `SERVER_USER` | SSH username |
| `SERVER_SSH_KEY` | Private SSH key for authentication |

---

## Cloudflare Tunnel Setup

### Steps

1. Go to [Cloudflare Zero Trust Dashboard](https://one.dash.cloudflare.com/)
2. Navigate to **Networks** → **Tunnels**
3. Click **Create a tunnel**
4. Name it: `damb-stats`
5. Copy the tunnel token
6. Add to your `.env` file on the server
7. Configure public hostname:
   - **Subdomain:** `damb`
   - **Domain:** `ganzhomelab.com`
   - **Service:** `http://app:8080`

### Benefits
- No port forwarding on UDM Pro needed
- Free SSL/TLS certificates
- DDoS protection
- Your home IP stays hidden

---

## Dockerfile

```dockerfile
# Build stage
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Copy solution and restore
COPY *.sln .
COPY src/DAMB.Stats.Client/*.csproj ./src/DAMB.Stats.Client/
COPY src/DAMB.Stats.Server/*.csproj ./src/DAMB.Stats.Server/
COPY src/DAMB.Stats.Core/*.csproj ./src/DAMB.Stats.Core/
COPY src/DAMB.Stats.Infrastructure/*.csproj ./src/DAMB.Stats.Infrastructure/
RUN dotnet restore

# Copy everything else and build
COPY . .
WORKDIR /src/src/DAMB.Stats.Server
RUN dotnet publish -c Release -o /app/publish

# Runtime stage
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final
WORKDIR /app
COPY --from=build /app/publish .

ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080

ENTRYPOINT ["dotnet", "DAMB.Stats.Server.dll"]
```

---

## GW2 Insights JSON Import

### Verified JSON Structure (from your MO CM log)

The following field paths have been verified against your actual log file:

**Encounter-Level Fields:**
| Field | JSON Path | Example Value |
|-------|-----------|---------------|
| Boss Name | `fightName` | "Mursaat Overseer CM" |
| Boss ID | `triggerID` | 17172 |
| Challenge Mode | `isCM` | true |
| Legendary CM | `isLegendaryCM` | false |
| Success | `success` | true |
| Duration (ms) | `durationMS` | 138275 |
| Start Time | `timeStartStd` | "2026-01-19 21:20:01 -06:00" |
| End Time | `timeEndStd` | "2026-01-19 21:22:21 -06:00" |
| Recorded By | `recordedAccountBy` | "Ganz.3917" |
| Boss Icon | `fightIcon` | URL to icon |

**Player Fields (from `players[]` array):**
| Field | JSON Path | Example |
|-------|-----------|---------|
| Character Name | `name` | "Coffee Poops" |
| Account | `account` | "Sir Buttles.5182" |
| Profession/Spec | `profession` | "Daredevil" |
| Squad Group | `group` | 1 |
| Total DPS | `dpsAll[0].dps` | 31742 |
| Total Damage | `dpsAll[0].damage` | 4388123 |
| Power DPS | `dpsAll[0].powerDps` | 26742 |
| Condi DPS | `dpsAll[0].condiDps` | 5000 |
| Breakbar Damage | `dpsAll[0].breakbarDamage` | 1500 |
| Deaths | `defenses[0].deadCount` | 0 |
| Death Duration | `defenses[0].deadDuration` | 0 |
| Downs | `defenses[0].downCount` | 1 |
| Down Duration | `defenses[0].downDuration` | 3500 |
| Damage Taken | `defenses[0].damageTaken` | 125000 |
| Resurrects | `support[0].resurrects` | 2 |
| Condi Cleanse | `support[0].condiCleanse` | 16 |
| Boon Strips | `support[0].boonStrips` | 3 |

**Mechanics (from `mechanics[]` array):**
| Field | JSON Path | Example |
|-------|-----------|---------|
| Short Name | `name` | "Jade Expl" |
| Full Name | `fullName` | "Jade Explosion" |
| Description | `description` | "Jade Soldier's Death Explosion" |
| Player Hit | `mechanicsData[].actor` | "Coffee Poops" |
| Time (ms) | `mechanicsData[].time` | 75925 |

### Sample Mechanics from MO CM

From your actual log, these mechanics were tracked:
- **Dead** / **Downed** / **Got up** - Death/down tracking
- **Res** - Resurrect events
- **Jade** - Jade Aura hits
- **Jade Expl** - Jade Soldier explosion hits
- **Protect (SAK)** / **Dispel (SAK)** / **Claim (SAK)** - Special Action Key usage

### Import Service Implementation

```csharp
public class LogImportService
{
    private readonly RaidStatsDb _db;
    private readonly ILogger<LogImportService> _logger;

    public LogImportService(RaidStatsDb db, ILogger<LogImportService> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<ImportResult> ImportLogAsync(string jsonPath)
    {
        var jsonContent = await File.ReadAllTextAsync(jsonPath);
        var jsonHash = ComputeSha256(jsonContent);
        
        // Check for duplicate
        var exists = await _db.Encounters
            .AnyAsync(e => e.JsonHash == jsonHash);
        
        if (exists)
        {
            return ImportResult.Duplicate();
        }
        
        var log = JsonSerializer.Deserialize<EliteInsightsLog>(jsonContent);
        
        // Use transaction for atomicity
        using var transaction = await _db.BeginTransactionAsync();
        
        try
        {
            // Insert encounter
            var encounterId = Guid.NewGuid();
            await _db.InsertAsync(new Encounter
            {
                Id = encounterId,
                TriggerId = log.TriggerID,
                BossName = log.FightName,
                Wing = WingMapping.GetWing(log.TriggerID),
                IsCM = log.IsCM,
                IsLegendaryCM = log.IsLegendaryCM,
                Success = log.Success,
                DurationMs = log.DurationMS,
                EncounterTime = DateTimeOffset.Parse(log.TimeStartStd),
                RecordedBy = log.RecordedAccountBy,
                IconUrl = log.FightIcon,
                JsonHash = jsonHash
            });
            
            // Batch collect player encounters and mechanics
            var playerEncounters = new List<PlayerEncounter>();
            var mechanicEvents = new List<MechanicEvent>();
            var playerCache = new Dictionary<string, Guid>(); // account -> id
            
            // Load existing players
            var existingPlayers = await _db.Players.ToDictionaryAsync(p => p.AccountName, p => p.Id);
            
            // Find new players to insert
            var newPlayers = log.Players
                .Where(p => !existingPlayers.ContainsKey(p.Account))
                .Select(p => new Player
                {
                    Id = Guid.NewGuid(),
                    AccountName = p.Account,
                    FirstSeen = DateTimeOffset.Parse(log.TimeStartStd)
                })
                .ToList();
            
            // Bulk insert new players
            if (newPlayers.Any())
            {
                await _db.BulkCopyAsync(newPlayers);
            }
            
            // Build player cache
            foreach (var p in existingPlayers) playerCache[p.Key] = p.Value;
            foreach (var p in newPlayers) playerCache[p.AccountName] = p.Id;
            
            // Build player encounters
            foreach (var playerLog in log.Players)
            {
                var playerId = playerCache[playerLog.Account];
                var dpsStats = playerLog.DpsAll?.FirstOrDefault();
                var defenseStats = playerLog.Defenses?.FirstOrDefault();
                var supportStats = playerLog.Support?.FirstOrDefault();
                
                playerEncounters.Add(new PlayerEncounter
                {
                    Id = Guid.NewGuid(),
                    PlayerId = playerId,
                    EncounterId = encounterId,
                    CharacterName = playerLog.Name,
                    Profession = playerLog.Profession,
                    SquadGroup = playerLog.Group,
                    Dps = dpsStats?.Dps ?? 0,
                    Damage = dpsStats?.Damage ?? 0,
                    PowerDps = dpsStats?.PowerDps ?? 0,
                    CondiDps = dpsStats?.CondiDps ?? 0,
                    BreakbarDamage = dpsStats?.BreakbarDamage ?? 0,
                    Deaths = defenseStats?.DeadCount ?? 0,
                    DeathDurationMs = defenseStats?.DeadDuration ?? 0,
                    Downs = defenseStats?.DownCount ?? 0,
                    DownDurationMs = defenseStats?.DownDuration ?? 0,
                    DamageTaken = defenseStats?.DamageTaken ?? 0,
                    Resurrects = supportStats?.Resurrects ?? 0,
                    CondiCleanse = supportStats?.CondiCleanse ?? 0,
                    BoonStrips = supportStats?.BoonStrips ?? 0
                });
            }
            
            // Bulk insert player encounters
            await _db.BulkCopyAsync(playerEncounters);
            
            // Build mechanic events
            if (log.Mechanics != null)
            {
                // Map character names to player IDs for this encounter
                var charToPlayer = log.Players.ToDictionary(p => p.Name, p => playerCache[p.Account]);
                
                foreach (var mechanic in log.Mechanics)
                {
                    if (mechanic.MechanicsData == null) continue;
                    
                    foreach (var eventData in mechanic.MechanicsData)
                    {
                        if (!charToPlayer.TryGetValue(eventData.Actor, out var playerId))
                            continue; // Skip non-player actors (e.g., NPCs)
                        
                        mechanicEvents.Add(new MechanicEvent
                        {
                            Id = Guid.NewGuid(),
                            EncounterId = encounterId,
                            PlayerId = playerId,
                            MechanicName = mechanic.Name,
                            MechanicFullName = mechanic.FullName,
                            Description = mechanic.Description,
                            EventTimeMs = eventData.Time
                        });
                    }
                }
            }
            
            // Bulk insert mechanic events
            if (mechanicEvents.Any())
            {
                await _db.BulkCopyAsync(mechanicEvents);
            }
            
            await transaction.CommitAsync();
            
            _logger.LogInformation("Imported {Boss} ({Result}) with {PlayerCount} players, {MechanicCount} mechanic events",
                log.FightName, log.Success ? "kill" : "wipe", playerEncounters.Count, mechanicEvents.Count);
            
            return ImportResult.Success(encounterId);
        }
        catch (Exception ex)
        {
            await transaction.RollbackAsync();
            _logger.LogError(ex, "Failed to import log {Path}", jsonPath);
            throw;
        }
    }
    
    private static string ComputeSha256(string content)
    {
        using var sha256 = SHA256.Create();
        var bytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(content));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
```

### RaidStatsDb (linq2db DataConnection)

```csharp
public class RaidStatsDb : DataConnection
{
    public RaidStatsDb(DataOptions<RaidStatsDb> options) : base(options.Options) { }
    
    public ITable<Player> Players => this.GetTable<Player>();
    public ITable<Encounter> Encounters => this.GetTable<Encounter>();
    public ITable<PlayerEncounter> PlayerEncounters => this.GetTable<PlayerEncounter>();
    public ITable<MechanicEvent> MechanicEvents => this.GetTable<MechanicEvent>();
}
```

### Example Query (Stats Explorer)

```csharp
public async Task<List<EncounterDto>> SearchEncountersAsync(EncounterFilter filter)
{
    var query = _db.Encounters.AsQueryable();
    
    if (filter.Wing.HasValue)
        query = query.Where(e => e.Wing == filter.Wing);
    
    if (!string.IsNullOrEmpty(filter.BossName))
        query = query.Where(e => e.BossName.Contains(filter.BossName));
    
    if (filter.IsCM.HasValue)
        query = query.Where(e => e.IsCM == filter.IsCM);
    
    if (filter.Success.HasValue)
        query = query.Where(e => e.Success == filter.Success);
    
    if (filter.StartDate.HasValue)
        query = query.Where(e => e.EncounterTime >= filter.StartDate);
    
    if (filter.EndDate.HasValue)
        query = query.Where(e => e.EncounterTime <= filter.EndDate);
    
    if (!string.IsNullOrEmpty(filter.PlayerAccount))
    {
        query = query.Where(e => 
            _db.PlayerEncounters.Any(pe => 
                pe.EncounterId == e.Id && 
                pe.Player.AccountName == filter.PlayerAccount));
    }
    
    return await query
        .OrderByDescending(e => e.EncounterTime)
        .Take(100)
        .Select(e => new EncounterDto
        {
            Id = e.Id,
            BossName = e.BossName,
            IsCM = e.IsCM,
            Success = e.Success,
            DurationMs = e.DurationMs,
            EncounterTime = e.EncounterTime
        })
        .ToListAsync();
}
```

### JSON DTOs

```csharp
public class EliteInsightsLog
{
    [JsonPropertyName("triggerID")]
    public int TriggerID { get; set; }
    
    [JsonPropertyName("fightName")]
    public string FightName { get; set; }
    
    [JsonPropertyName("isCM")]
    public bool IsCM { get; set; }
    
    [JsonPropertyName("isLegendaryCM")]
    public bool IsLegendaryCM { get; set; }
    
    [JsonPropertyName("success")]
    public bool Success { get; set; }
    
    [JsonPropertyName("durationMS")]
    public int DurationMS { get; set; }
    
    [JsonPropertyName("timeStartStd")]
    public string TimeStartStd { get; set; }
    
    [JsonPropertyName("recordedAccountBy")]
    public string RecordedAccountBy { get; set; }
    
    [JsonPropertyName("fightIcon")]
    public string FightIcon { get; set; }
    
    [JsonPropertyName("players")]
    public List<PlayerLog> Players { get; set; }
    
    [JsonPropertyName("mechanics")]
    public List<MechanicLog> Mechanics { get; set; }
}

public class PlayerLog
{
    [JsonPropertyName("name")]
    public string Name { get; set; }
    
    [JsonPropertyName("account")]
    public string Account { get; set; }
    
    [JsonPropertyName("profession")]
    public string Profession { get; set; }
    
    [JsonPropertyName("group")]
    public int Group { get; set; }
    
    [JsonPropertyName("weapons")]
    public List<string> Weapons { get; set; }
    
    [JsonPropertyName("dpsAll")]
    public List<DpsStats> DpsAll { get; set; }
    
    [JsonPropertyName("defenses")]
    public List<DefenseStats> Defenses { get; set; }
    
    [JsonPropertyName("support")]
    public List<SupportStats> Support { get; set; }
}

public class DpsStats
{
    [JsonPropertyName("dps")]
    public int Dps { get; set; }
    
    [JsonPropertyName("damage")]
    public long Damage { get; set; }
    
    [JsonPropertyName("powerDps")]
    public int PowerDps { get; set; }
    
    [JsonPropertyName("condiDps")]
    public int CondiDps { get; set; }
    
    [JsonPropertyName("breakbarDamage")]
    public decimal BreakbarDamage { get; set; }
}

public class DefenseStats
{
    [JsonPropertyName("deadCount")]
    public int DeadCount { get; set; }
    
    [JsonPropertyName("deadDuration")]
    public int DeadDuration { get; set; }
    
    [JsonPropertyName("downCount")]
    public int DownCount { get; set; }
    
    [JsonPropertyName("downDuration")]
    public int DownDuration { get; set; }
    
    [JsonPropertyName("damageTaken")]
    public long DamageTaken { get; set; }
}

public class SupportStats
{
    [JsonPropertyName("resurrects")]
    public int Resurrects { get; set; }
    
    [JsonPropertyName("condiCleanse")]
    public int CondiCleanse { get; set; }
    
    [JsonPropertyName("boonStrips")]
    public int BoonStrips { get; set; }
}

public class MechanicLog
{
    [JsonPropertyName("name")]
    public string Name { get; set; }
    
    [JsonPropertyName("fullName")]
    public string FullName { get; set; }
    
    [JsonPropertyName("description")]
    public string Description { get; set; }
    
    [JsonPropertyName("mechanicsData")]
    public List<MechanicData> MechanicsData { get; set; }
}

public class MechanicData
{
    [JsonPropertyName("actor")]
    public string Actor { get; set; }
    
    [JsonPropertyName("time")]
    public int Time { get; set; }
}
```

### Boss-Specific Mechanic Tracking

Here are mechanics to track for "fun stats" (you can add more as you see them in logs):

| Boss | Mechanic Name | Fun Stat Name |
|------|---------------|---------------|
| Deimos | "Oil" or similar | Oils stepped in |
| Vale Guardian | "Green" | Greens failed |
| Gorseval | "Slam" | Slams taken |
| Sabetha | "Sapper" | Sappers missed |
| Matthias | "Hadouken" | Hadoukens eaten |
| Dhuum | "Crack" | Floor cracks |
| Qadim | "Shockwave" | Shockwaves failed |
| Decima | (need to check) | Wipe mechanics |

**Note:** You'll need to examine a Deimos JSON to find the exact mechanic name for oil puddles. Upload one when you can and I'll identify it!

---

## Development Workflow

### Initial Setup (One Time)

```bash
# Clone repo
git clone https://github.com/a-gansemer/gw2-raid-stats.git
cd gw2-raid-stats

# Open in Visual Studio
start GW2RaidStats.sln

# Start local PostgreSQL (via Docker)
docker compose -f docker-compose.dev.yml up -d postgres

# Run migrations (apply SQL scripts)
# Option 1: Manual via psql
psql -h localhost -U app -d raidstats -f src/GW2RaidStats.Infrastructure/Migrations/001_InitialSchema.sql

# Option 2: Or use the built-in migration runner (if implemented)
dotnet run --project src/GW2RaidStats.Server -- --migrate

# Run the app
dotnet run --project src/GW2RaidStats.Server
```

### Daily Development

1. Pull latest: `git pull`
2. Make changes in Visual Studio
3. Test locally: F5
4. Commit: View → Git Changes → Stage → Commit
5. Push: Sync button
6. GitHub Actions auto-deploys to production

---

## Implementation Order

### Phase 1: Foundation (Week 1-2)
- [ ] Create GitHub repo (`a-gansemer/gw2-raid-stats`)
- [ ] Set up solution structure (4 projects)
- [ ] Install MudBlazor, configure theming from config
- [ ] Create database models & migrations
- [ ] Create configuration models (GuildConfig, ThemeConfig, AwardConfig)
- [ ] Basic API endpoints (CRUD for players, encounters)
- [ ] Set up Proxmox LXC container
- [ ] Configure Docker + PostgreSQL + Cloudflare Tunnel
- [ ] Get CI/CD pipeline working (deploy "Hello World" MudBlazor app)

### Phase 2: Data Import (Week 2-3)
- [ ] Build import service with verified JSON mappings
- [ ] Create admin upload page
- [ ] Import all 2025 logs from NAS
- [ ] Verify data integrity
- [ ] Set up mechanic event tracking

### Phase 3: Home Dashboard & Explorer (Week 3-4)
- [ ] Home dashboard with guild branding from config
- [ ] Stats Explorer page with all filters
- [ ] Aggregate stats calculations
- [ ] Results table with sorting/pagination

### Phase 4: Core Stats Views (Week 4-5)
- [ ] Guild overview page
- [ ] Individual player pages
- [ ] Per-boss stats
- [ ] Per-wing stats
- [ ] Per-class/spec stats
- [ ] Leaderboards

### Phase 5: Polish & Documentation (Week 5-6)
- [ ] About page (reads from config)
- [ ] Charts and visualizations
- [ ] Mobile responsiveness
- [ ] Performance optimization
- [ ] **Deployment documentation** (for other guilds)
- [ ] **Configuration documentation**
- [ ] **README with quick start guide**

### Phase 6: Yearly Recap (End of Year)
- [ ] Recap page with configurable awards
- [ ] Award calculation service (reads awards.json)
- [ ] Hall of Shame mechanics (configurable)
- [ ] Publish toggle in config

### Future Enhancements
- [ ] Discord bot integration
- [ ] Auto-sync from NAS (watch folder)
- [ ] Build/gear tracking
- [ ] Player comparison tools
- [ ] Multi-year recap support
- [ ] Strikes/Fractals support

---

## Decisions Made ✓

| Question | Answer |
|----------|--------|
| GitHub repo | `gw2-raid-stats` (public, MIT license) |
| UI Framework | MudBlazor |
| Charts | MudBlazor.Charts (built-in) |
| Theme | Configurable colors, light/dark toggle |
| JSON Structure | Verified from MO CM log |
| Live Stats | Dashboard + Explorer with filters |
| Recap | Configurable awards, published via config |
| Deployment | Auto-deploy on push to main |
| Open Source | Yes — any guild can deploy their own instance |

---

## Still Needed

- [ ] **Deimos JSON** — to find oil puddle mechanic name
- [ ] **Decima JSON** — to find wipe mechanics
- [ ] **Your guild logo** — for DAMB deployment
- [ ] **About page content** — guild description for DAMB

---

## Resources

- [GW2 Insights](https://gw2insights.com/)
- [Elite Insights Parser](https://github.com/baaron4/GW2-Elite-Insights-Parser)
- [Blazor Documentation](https://learn.microsoft.com/en-us/aspnet/core/blazor/)
- [MudBlazor Documentation](https://mudblazor.com/)
- [MudBlazor GitHub](https://github.com/MudBlazor/MudBlazor)
- [MudBlazor.Extensions Documentation](https://www.mudex.org/)
- [MudBlazor.Extensions GitHub](https://github.com/fgilde/MudBlazor.Extensions)
- [linq2db Documentation](https://linq2db.github.io/)
- [linq2db GitHub](https://github.com/linq2db/linq2db)
- [PostgreSQL](https://www.postgresql.org/docs/)
- [Cloudflare Tunnel Docs](https://developers.cloudflare.com/cloudflare-one/connections/connect-apps/)
