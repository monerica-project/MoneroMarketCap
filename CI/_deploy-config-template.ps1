# deploy-config.ps1
# Edit these values before running deploy.ps1

# SSH connection
$SSH_HOST     = ""
$SSH_USER     = "root"
$SSH_KEY      = "$HOME\.ssh\id_rsa"        # path to your private key
$SSH_PASSWORD = ""

# PuTTY tools location
$PLINK = "C:\Windows\System32\plink.exe"
$PSCP  = "C:\Windows\System32\pscp.exe" 

# App settings
$APP_NAME     = ""
$WORKER_NAME  = ""
$DOMAIN       = ""
$APP_PORT     = 5000
$DEPLOY_PATH  = "/var/www/$APP_NAME"
$WORKER_PATH  = "/var/www/$WORKER_NAME"

# .NET projects (relative to this script)
$WEB_PROJECT = "..\src\MoneroMarketCap.Web\MoneroMarketCap.Web.csproj"
$WORKER_PROJECT = "..\src\MoneroMarketCap.Worker\MoneroMarketCap.Worker.csproj"

# Postgres
$DB_NAME     = ""
$DB_USER     = ""
$DB_PASSWORD = ""

$SPONSOR_URL              = ""
$SPONSOR_CACHE_TTL        = 5
$SPONSOR_ROTATE_SECONDS   = 30

# App config (written to appsettings.Production.json on server)
$COINGECKO_API_KEY         = ""
$COINGECKO_REFRESH_MINUTES = 8
$ADMIN_USERNAME            = "admin"
$ADMIN_PASSWORD            = "changeme_admin_password"
