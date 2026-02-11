# Changelog

All notable changes to GW2 Raid Stats will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [1.9.1] - 2025-02-10

### Fixed
- HTCM Prog best phase now uses canonical phase ordering (Zhaitan is correctly ranked higher than Void Giant phases)

## [1.9.0] - 2025-02-10

### Added
- Patch notes system with automatic Discord notifications on new versions
- `/patchnotes` command to view current or specific version notes
- `/versions` command to view version history

### Fixed
- Survivor award now correctly excludes /gg deaths (matches Wall of Shame logic)
- Logs view now shows most recent first by default
- HTCM Prog best phase now correctly correlates phase name with phase index
- Session summary header layout improvements for smaller screens

## [1.8.0] - 2025-02-04

### Added
- Session Summary MVP Stats
  - Top DPS (non-support players)
  - Best Boon DPS (support players)
  - Best CC (breakbar damage)
  - Most Resurrects
  - Survivor (fewest deaths)
- Wall of Shame Stats
  - Most Deaths, Most Downs, Least CC, Most Damage Taken
- Wipes now included in MVP stats calculations

## [1.7.2] - 2025-02-03

### Fixed
- Death tracking now excludes /gg (forfeit) deaths for more accurate stats

## [1.7.1] - 2025-02-01

### Fixed
- Rescan service no longer times out
- Background scan context issues
- JSON rescan file path (report.json not log.json)
- Uses built-in arcdps check for healing power

## [1.7.0] - 2025-02-01

### Added
- Healing Power stat tracking
- Healing DPS leaderboard
- ReScan JSON service for re-importing encounter data without re-parsing logs
- Top 5 "Tooter" notifications when records are broken
- Record notifications now show all players who broke the record

### Fixed
- Aetherblade Hideout and Harvest Temple DPS calculations
- Leaderboard now indicates if top DPS was on a boon build

## [1.6.1] - 2025-01-31

### Added
- New bot commands: `/leaderboard`, `/myrecords`, `/myboonrecords`, `/help`

## [1.6.0] - 2025-01-30

### Added
- Discord Bot integration
  - Session end summary notifications
  - New record notifications
  - HTCM progression milestone notifications
  - `/config notifications` - Set notification channel
  - `/config shame` - Toggle Wall of Shame
  - `/config status` - View bot configuration
  - `/link` - Link Discord to GW2 account

### Fixed
- Docker configuration for Discord bot

## [1.5.2] - 2025-01-29

### Fixed
- HTCM Prog UI responsiveness
- DateTime offset display issues
- Home screen date display

## [1.5.1] - 2025-01-28

### Fixed
- Mechanic ICD (internal cooldown) moved to in-memory for performance
- Mordremoth shockwave mechanic tracking
- HTCM HTML log viewer integration

## [1.5.0] - 2025-01-28

### Added
- HTCM (Harvest Temple CM) Progression Tracking
  - Session-by-session progress view
  - Best phase and HP tracking
  - Pull-by-pull breakdown
  - Player mechanics tracking per session
- Mechanics lookup and tracking system
- Damage taken stat in yearly recap
- Download raw .zevtc files from encounter pages

## [1.4.2] - 2025-01-26

### Added
- Delete logs from admin page
- Log links in leaderboard entries

## [1.4.1] - 2025-01-25

### Fixed
- Twin Largos encounter handling (multiple attempts)
- Yearly recap only shows for completed years
- Boss page now only shows guild members in DPS rankings

## [1.4.0] - 2025-01-25

### Added
- Log Search/Filter page with filters for boss, wing, CM, success, date range
- HTML Report Viewer (view reports like dps.report without leaving the app)
- Leaderboards UI with better groupings
- Cloudflare tunnel support for deployment

### Fixed
- Processing race condition

## [1.3.1] - 2025-01-25

### Fixed
- Dockerfile path issues
- PostgreSQL local storage configuration

## [1.3.0] - 2025-01-24

### Added
- Automatic .zevtc log parsing with GW2 Elite Insights
- Drop logs into queue folder for automatic processing
- Docker Compose for local builds

## [1.2.0] - 2025-01-22

### Added
- Personal Player Recap (Spotify Wrapped style yearly summary per player)

## [1.1.1] - 2025-01-22

### Added
- Admin panel with basic authentication
- Most recent session stats on home page
- Boss information pages

### Fixed
- Timestamp handling improvements
- Upload processing reliability

## [1.1.0] - 2025-01-22

### Added
- Yearly Guild Recap feature with fun statistics

## [1.0.0] - 2025-01-21

### Added
- Initial release
- Log import from Elite Insights JSON
- DPS Leaderboards per boss
- Player statistics and profiles
- Guild member filtering (exclude pugs from stats)
- Included players management
- MudBlazor-based responsive UI
- PostgreSQL database storage
- Docker deployment support
