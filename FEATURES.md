# GW2 Raid Stats - Feature List

A self-hosted Guild Wars 2 raid statistics dashboard for guilds to track performance, analyze logs, and celebrate achievements.

---

## Core Features

### Log Management
- **Automatic Log Processing** - Drop `.zevtc` files into a queue folder for automatic parsing
- **Web Upload** - Upload logs directly through the admin interface
- **Duplicate Detection** - Automatically skips already-imported logs
- **HTML Report Viewing** - View detailed reports (like dps.report) directly in the app

### Log Search & Filtering
- **Advanced Search** - Search all encounter logs with multiple filters:
  - Boss name (with autocomplete)
  - Wing number
  - Mode (Normal / Challenge Mode)
  - Result (Kill / Wipe)
  - Recorded by
  - Date range
- **Paginated Results** - Browse through hundreds of logs efficiently
- **Direct Links** - Quick access to boss details and HTML reports

---

## Statistics & Analytics

### Player Statistics
- **Player Profiles** - Individual pages for each guild member showing:
  - Total encounters, kills, and wipes
  - Success rate
  - Most played classes/specializations
  - Personal bests (top DPS performances)
  - Recent encounter history
- **Player Leaderboards** - Compare performance across guild members

### Boss Statistics
- **Boss Overview** - See all bosses with:
  - Total encounters, kills, and wipes
  - Success rate
  - Fastest and average kill times
  - Grouped by raid wing
- **Boss Detail Pages** - Deep dive into each boss:
  - Performance metrics
  - Top DPS records (guild members only)
  - Recent encounter history
- **Ignored Bosses** - Hide non-relevant encounters from stats

### Leaderboards
- **Top DPS Rankings** - Per-boss leaderboards showing:
  - Best DPS performances
  - Boon DPS category (for support builds doing damage)
  - Character name and profession
  - Boon support credits (quickness/alacrity providers)
- **Guild Members Only** - Leaderboards exclude pugs/non-guild members
- **Normal & CM Separate** - Different leaderboards for each difficulty

---

## Yearly Recap

### Guild Recap
An animated, Spotify Wrapped-style presentation of the guild's year including:
- Total encounters, kills, and wipes
- Hours spent raiding
- Most killed boss
- Nemesis boss (most wipes)
- Most attempted bosses
- Favorite classes and specializations
- Top DPS moment of the year
- Total deaths and death leader
- Total damage dealt
- Breakbar damage champions
- Clutch saves (resurrects)
- Most diverse player (most specs played)
- Custom fun stat awards

### Player Recap
Individual yearly recaps for each guild member with personalized stats.

### Fun Stats Awards
- **Customizable Awards** - Create custom awards based on mechanics:
  - Track any mechanic from Elite Insights logs
  - Positive awards (e.g., "Most Orbs Collected")
  - Negative awards (e.g., "Floor Tank Champion")
  - Custom titles and descriptions

---

## Administration

### Guild Configuration
- **Included Players** - Manage which accounts are guild members
- **Ignored Bosses** - Exclude specific encounters from statistics
- **Guild Branding** - Customize guild name, tag, and logo

### Data Management
- **Bulk Import** - Import multiple logs at once
- **Processing Queue** - Monitor log processing status
- **Failed Logs** - Review and retry failed imports

---

## Technical Features

### Deployment
- **Docker Support** - Easy deployment with Docker Compose
- **Cloudflare Tunnel** - Secure remote access without port forwarding
- **PostgreSQL Database** - Reliable data storage

### Performance
- **Background Processing** - Logs processed asynchronously
- **Efficient Queries** - Fast loading even with thousands of logs
- **Automatic Migrations** - Database updates handled automatically

---

## Data Tracked Per Encounter

- Boss name and trigger ID
- Challenge Mode / Legendary CM status
- Success/failure
- Duration
- Encounter timestamp
- Log recorder

### Per Player Per Encounter
- DPS (total, power, condi)
- Damage dealt
- Breakbar damage
- Deaths and down count
- Damage taken
- Resurrects performed
- Condition cleanse
- Boon strips
- Quickness/Alacrity generation
- Healing stats
- Profession/specialization
- Squad group

---

## Access

- **Public Dashboard** - Stats viewable by all guild members
- **Admin Panel** - Password-protected administrative functions
- **Mobile Friendly** - Responsive design works on all devices

---

## Future-Ready

The modular architecture makes it easy to add:
- Strike mission support
- Fractal tracking
- Additional statistics and visualizations
- API integrations

---

*Built with .NET 9, Blazor WebAssembly, and PostgreSQL*
