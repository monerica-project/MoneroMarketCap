# MoneroMarketCap

Live XMR price aggregator and market data site powering [moneromarketcap.com](https://moneromarketcap.com).

## Prerequisites

- .NET SDK (matching the version specified in the `.csproj` files)
- PostgreSQL
- A CoinGecko API key
- PowerShell (for deployment and backups)
- PuTTY tools (`plink.exe` and `pscp.exe`) — for Windows-based deployment

## Local Setup

### 1. Clone the repository

```bash
git clone https://github.com/YOURUSER/MoneroMarketCap.git
cd MoneroMarketCap
```

### 2. Create `appsettings.json`

Create an `appsettings.json` file in the `MoneroMarketCap.Web` folder using the following format, filling in your own values:

```json
{
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Microsoft.AspNetCore": "Warning"
    }
  },
  "AllowedHosts": "*",
  "ConnectionStrings": {
    "DefaultConnection": "Host=localhost;Port=PORT;Database=YOURDB;Username=YOURUSERNAME;Password=YOURPASSWORD"
  },
  "CoinGecko": {
    "ApiKey": "APIKEY"
  },
  "Sponsors": {
    "SourceUrl": "https://app.monerica.com/sponsoredlisting/activesponsorjson",
    "CacheTtlMinutes": 5,
    "RotateIntervalSeconds": 30
  }
}
```

Replace the placeholder values:

- `PORT` — your PostgreSQL port (typically `5432`)
- `YOURDB` — the name of your PostgreSQL database
- `YOURUSERNAME` / `YOURPASSWORD` — PostgreSQL credentials
- `APIKEY` — your CoinGecko API key

> **Do not commit this file.** Add it to `.gitignore`.

### 3. Apply database migrations

From the solution root, run the EF Core migrations against your PostgreSQL database to create the schema.

### 4. Run the application

```bash
dotnet run --project src/MoneroMarketCap.Web/MoneroMarketCap.Web.csproj
```

## Deployment

Deployment is handled by a PowerShell-based CI/CD pipeline that uses PuTTY (`plink`/`pscp`) to push the published build to a Linux VPS running `systemd`. Both the web app and the background worker are deployed.

### Create `CI/deploy-config.ps1`

Create a file at `CI/deploy-config.ps1` with the following contents, filling in every value for your environment:

```powershell
# deploy-config.ps1
# Edit these values before running deploy.ps1

# SSH connection
$SSH_HOST     = "IP"
$SSH_USER     = "USER"
$SSH_KEY      = "$HOME\.ssh\id_rsa"        # path to your private key
$SSH_PASSWORD = "PASSWORD"

# PuTTY tools location
$PLINK = "C:\Windows\System32\plink.exe"
$PSCP  = "C:\Windows\System32\pscp.exe"

# App settings
$APP_NAME     = "WEBAPP"
$WORKER_NAME  = "WORKERAPP"
$DOMAIN       = "DOMAINNAME" # example.com
$APP_PORT     = PORTNUMBER
$DEPLOY_PATH  = "/var/www/$APP_NAME"
$WORKER_PATH  = "/var/www/$WORKER_NAME"

# .NET projects (relative to this script)
$WEB_PROJECT = "..\src\MoneroMarketCap.Web\MoneroMarketCap.Web.csproj"
$WORKER_PROJECT = "..\src\MoneroMarketCap.Worker\MoneroMarketCap.Worker.csproj"

# Postgres
$DB_NAME     = "DBNAME"
$DB_USER     = "DBUSER"
$DB_PASSWORD = "DBPASS"

$SPONSOR_URL              = "https://app.monerica.com/sponsoredlisting/activesponsorjson"
$SPONSOR_CACHE_TTL        = 5
$SPONSOR_ROTATE_SECONDS   = 30

# App config (written to appsettings.Production.json on server)
$COINGECKO_API_KEY         = "APIKEY"
$COINGECKO_REFRESH_MINUTES = 8
$ADMIN_USERNAME            = "USERNAME"
$ADMIN_PASSWORD            = "PASSWORD"
```

Field reference:

- **SSH connection** — host/IP, user, private key path, and password for the target VPS
- **PuTTY tools** — paths to `plink.exe` and `pscp.exe` on your local machine
- **App settings** — systemd service names for the web app and worker, public domain, port, and deploy paths on the server
- **.NET projects** — paths (relative to the script) to the web and worker `.csproj` files
- **Postgres** — database name and credentials on the server
- **Sponsors** — source URL and cache/rotation timings for sponsored listings
- **App config** — written to `appsettings.Production.json` on the server at deploy time (CoinGecko key, refresh interval, admin credentials)

> **Do not commit `deploy-config.ps1`.** It contains plaintext credentials.

### Run the deployment

From PowerShell, run the deployment script in the `CI` folder. It will:

1. Publish the web and worker projects
2. Upload both builds to their respective paths on the server via `pscp`
3. Write `appsettings.Production.json` on the server from the config values
4. Restart the systemd services for `$APP_NAME` and `$WORKER_NAME` via `plink`

## Backups

Database backups are pulled from the server by a separate PowerShell script that reads its own config file.

### Create `CI/backup-config.ps1`

Create a file at `CI/backup-config.ps1` with the following contents:

```powershell
# =============================================
# backup-config.ps1 - Edit these values
# =============================================
$SSH_HOST     = "IP"
$SSH_USER     = "USER"
$SSH_PASSWORD = "PASSWORD"

$DB_NAME     = "DBNAME"
$DB_USER     = "USER"
$DB_PASSWORD = "PASSWORD"

$LOCAL_BACKUP_DIR = "LOCALFILEPATH"
```

Field reference:

- **SSH** — host/IP and credentials for the VPS
- **DB** — PostgreSQL database name and credentials on the server
- **LOCAL_BACKUP_DIR** — local path on your machine where `pg_dump` output will be saved

> **Do not commit `backup-config.ps1`.**

Run the backup script from PowerShell whenever you want a fresh dump pulled down locally.

## .gitignore

Make sure at minimum the following are ignored:

```
**/appsettings.json
**/appsettings.Production.json
CI/deploy-config.ps1
CI/backup-config.ps1
```

## License

See `LICENSE` file.
