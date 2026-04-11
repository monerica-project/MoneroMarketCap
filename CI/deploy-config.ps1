# deploy-config.ps1
# Edit these values before running deploy.ps1

# SSH connection
$SSH_HOST     = "your.server.ip"
$SSH_USER     = "root"
$SSH_KEY      = "$HOME\.ssh\id_rsa"        # path to your private key

# App settings
$APP_NAME     = "moneromarketcap"
$WORKER_NAME  = "moneromarketcap-worker"
$DOMAIN       = "moneromarketcap.com"      # or your server IP for testing
$DEPLOY_PATH  = "/var/www/$APP_NAME"
$WORKER_PATH  = "/var/www/$WORKER_NAME"

# .NET projects (relative to this script)
$WEB_PROJECT    = "..\src\MoneroMarketCap\MoneroMarketCap.csproj"
$WORKER_PROJECT = "..\src\MoneroMarketCap.Worker\MoneroMarketCap.Worker.csproj"

# Postgres
$DB_NAME     = "moneromarketcap"
$DB_USER     = "mmcapp"
$DB_PASSWORD = "changeme_strong_password"

# App config (written to appsettings.Production.json on server)
$COINGECKO_API_KEY         = "your-coingecko-api-key"
$COINGECKO_REFRESH_MINUTES = 8
$ADMIN_USERNAME            = "admin"
$ADMIN_PASSWORD            = "changeme_admin_password"
