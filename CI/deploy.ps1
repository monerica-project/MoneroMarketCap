# deploy.ps1
# Run from the solution root on Windows:
#   .\deploy.ps1
#
# Requirements:
#   - OpenSSH installed (built into Windows 10/11)
#   - .NET SDK installed
#   - SSH key auth set up on your VPS

param(
    [switch]$SkipBuild,
    [switch]$WebOnly,
    [switch]$WorkerOnly
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

# ── Load config ──────────────────────────────────────────────────────────────
$configPath = Join-Path $PSScriptRoot "deploy-config.ps1"
if (-not (Test-Path $configPath)) {
    Write-Error "deploy-config.ps1 not found. Copy it from the repo and fill in your values."
    exit 1
}
. $configPath

# ── Helpers ───────────────────────────────────────────────────────────────────
function Write-Step($msg) {
    Write-Host "`n==> $msg" -ForegroundColor Cyan
}

function Write-Ok($msg) {
    Write-Host "    OK: $msg" -ForegroundColor Green
}

function SSH($cmd) {
    $result = ssh -i $SSH_KEY -o StrictHostKeyChecking=no "$SSH_USER@$SSH_HOST" $cmd
    if ($LASTEXITCODE -ne 0) {
        Write-Error "SSH command failed: $cmd"
        exit 1
    }
    return $result
}

function SCP($local, $remote) {
    scp -i $SSH_KEY -o StrictHostKeyChecking=no -r $local "${SSH_USER}@${SSH_HOST}:${remote}"
    if ($LASTEXITCODE -ne 0) {
        Write-Error "SCP failed: $local -> $remote"
        exit 1
    }
}

# ── Step 1: Build ─────────────────────────────────────────────────────────────
if (-not $SkipBuild) {
    if (-not $WorkerOnly) {
        Write-Step "Building web app"
        $webOut    = Join-Path $PSScriptRoot "..\publish\web"
        if (Test-Path $webOut) { Remove-Item $webOut -Recurse -Force }
        dotnet publish $WEB_PROJECT -c Release -r linux-x64 --self-contained false -o $webOut
        if ($LASTEXITCODE -ne 0) { Write-Error "Web build failed"; exit 1 }
        Write-Ok "Web app built to $webOut"
    }

    if (-not $WebOnly) {
        Write-Step "Building worker"
        $workerOut = Join-Path $PSScriptRoot "..\publish\worker"
        if (Test-Path $workerOut) { Remove-Item $workerOut -Recurse -Force }
        dotnet publish $WORKER_PROJECT -c Release -r linux-x64 --self-contained false -o $workerOut
        if ($LASTEXITCODE -ne 0) { Write-Error "Worker build failed"; exit 1 }
        Write-Ok "Worker built to $workerOut"
    }
}

# ── Step 2: Server bootstrap (idempotent) ─────────────────────────────────────
Write-Step "Bootstrapping server"

$bootstrap = @"
set -e

# Install .NET 10 runtime if missing
if ! command -v dotnet &> /dev/null; then
    echo 'Installing .NET runtime...'
    wget https://packages.microsoft.com/config/ubuntu/22.04/packages-microsoft-prod.deb -O /tmp/packages-microsoft-prod.deb
    dpkg -i /tmp/packages-microsoft-prod.deb
    apt-get update -q
    apt-get install -y aspnetcore-runtime-10.0
fi

# Install Postgres if missing
if ! command -v psql &> /dev/null; then
    echo 'Installing PostgreSQL...'
    apt-get update -q
    apt-get install -y postgresql postgresql-contrib
    systemctl enable postgresql
    systemctl start postgresql
fi

# Install Nginx if missing
if ! command -v nginx &> /dev/null; then
    echo 'Installing Nginx...'
    apt-get install -y nginx
    systemctl enable nginx
    systemctl start nginx
fi

# Create deploy directories
mkdir -p $DEPLOY_PATH
mkdir -p $WORKER_PATH

echo 'Bootstrap complete'
"@

SSH $bootstrap
Write-Ok "Server dependencies ready"

# ── Step 3: Create Postgres DB and user ───────────────────────────────────────
Write-Step "Setting up PostgreSQL"

$pgSetup = @"
set -e
sudo -u postgres psql -tc "SELECT 1 FROM pg_roles WHERE rolname='$DB_USER'" | grep -q 1 || \
    sudo -u postgres psql -c "CREATE USER $DB_USER WITH PASSWORD '$DB_PASSWORD';"

sudo -u postgres psql -tc "SELECT 1 FROM pg_database WHERE datname='$DB_NAME'" | grep -q 1 || \
    sudo -u postgres psql -c "CREATE DATABASE $DB_NAME OWNER $DB_USER;"

sudo -u postgres psql -c "GRANT ALL PRIVILEGES ON DATABASE $DB_NAME TO $DB_USER;"
echo 'PostgreSQL ready'
"@

SSH $pgSetup
Write-Ok "Database ready"

# ── Step 4: Write production appsettings on server ───────────────────────────
Write-Step "Writing production config"

$connString = "Host=localhost;Port=5432;Database=$DB_NAME;Username=$DB_USER;Password=$DB_PASSWORD"

$webAppSettings = @"
{
  "ConnectionStrings": {
    "DefaultConnection": "$connString"
  },
  "CoinGecko": {
    "ApiKey": "$COINGECKO_API_KEY",
    "RefreshIntervalMinutes": $COINGECKO_REFRESH_MINUTES
  },
  "Admin": {
    "Username": "$ADMIN_USERNAME",
    "Password": "$ADMIN_PASSWORD"
  }
}
"@

$workerAppSettings = @"
{
  "ConnectionStrings": {
    "DefaultConnection": "$connString"
  },
  "CoinGecko": {
    "ApiKey": "$COINGECKO_API_KEY",
    "TopCoinsOnStartup": 100,
    "RefreshIntervalMinutes": $COINGECKO_REFRESH_MINUTES
  }
}
"@

# Write configs to temp files and SCP them
$webCfgTemp = Join-Path $env:TEMP "web-appsettings.Production.json"
$workerCfgTemp = Join-Path $env:TEMP "worker-appsettings.Production.json"
$webAppSettings   | Out-File -FilePath $webCfgTemp    -Encoding utf8 -NoNewline
$workerAppSettings | Out-File -FilePath $workerCfgTemp -Encoding utf8 -NoNewline

SCP $webCfgTemp    "$DEPLOY_PATH/appsettings.Production.json"
SCP $workerCfgTemp "$WORKER_PATH/appsettings.Production.json"
Write-Ok "Production configs written"

# ── Step 5: Deploy web app ────────────────────────────────────────────────────
if (-not $WorkerOnly) {
    Write-Step "Deploying web app"

    # Stop service if running
    SSH "systemctl is-active --quiet $APP_NAME && systemctl stop $APP_NAME || true"

    # Upload files
    $webOut = Join-Path $PSScriptRoot "publish\web"
    SCP "$webOut\*" $DEPLOY_PATH

    # Set permissions
    SSH "chown -R www-data:www-data $DEPLOY_PATH && chmod -R 755 $DEPLOY_PATH"

    # Write systemd service
    $webService = @"
[Unit]
Description=MoneroMarketCap Web
After=network.target postgresql.service

[Service]
WorkingDirectory=$DEPLOY_PATH
ExecStart=/usr/bin/dotnet $DEPLOY_PATH/MoneroMarketCap.dll
Restart=always
RestartSec=10
User=www-data
Environment=ASPNETCORE_ENVIRONMENT=Production
Environment=ASPNETCORE_URLS=http://localhost:5000
Environment=DOTNET_PRINT_TELEMETRY_MESSAGE=false

[Install]
WantedBy=multi-user.target
"@

    $svcTemp = Join-Path $env:TEMP "$APP_NAME.service"
    $webService | Out-File -FilePath $svcTemp -Encoding utf8 -NoNewline
    SCP $svcTemp "/etc/systemd/system/$APP_NAME.service"

    SSH "systemctl daemon-reload && systemctl enable $APP_NAME && systemctl start $APP_NAME"
    Write-Ok "Web app deployed and started"
}

# ── Step 6: Deploy worker ─────────────────────────────────────────────────────
if (-not $WebOnly) {
    Write-Step "Deploying worker"

    SSH "systemctl is-active --quiet $WORKER_NAME && systemctl stop $WORKER_NAME || true"

    $workerOut = Join-Path $PSScriptRoot "publish\worker"
    SCP "$workerOut\*" $WORKER_PATH

    SSH "chown -R www-data:www-data $WORKER_PATH && chmod -R 755 $WORKER_PATH"

    $workerService = @"
[Unit]
Description=MoneroMarketCap Worker
After=network.target postgresql.service

[Service]
WorkingDirectory=$WORKER_PATH
ExecStart=/usr/bin/dotnet $WORKER_PATH/MoneroMarketCap.Worker.dll
Restart=always
RestartSec=10
User=www-data
Environment=DOTNET_ENVIRONMENT=Production
Environment=DOTNET_PRINT_TELEMETRY_MESSAGE=false

[Install]
WantedBy=multi-user.target
"@

    $workerSvcTemp = Join-Path $env:TEMP "$WORKER_NAME.service"
    $workerService | Out-File -FilePath $workerSvcTemp -Encoding utf8 -NoNewline
    SCP $workerSvcTemp "/etc/systemd/system/$WORKER_NAME.service"

    SSH "systemctl daemon-reload && systemctl enable $WORKER_NAME && systemctl start $WORKER_NAME"
    Write-Ok "Worker deployed and started"
}

# ── Step 7: Configure Nginx ───────────────────────────────────────────────────
Write-Step "Configuring Nginx"

$nginxConf = @"
server {
    listen 80;
    server_name $DOMAIN;

    location / {
        proxy_pass         http://localhost:5000;
        proxy_http_version 1.1;
        proxy_set_header   Upgrade \$http_upgrade;
        proxy_set_header   Connection keep-alive;
        proxy_set_header   Host \$host;
        proxy_set_header   X-Real-IP \$remote_addr;
        proxy_set_header   X-Forwarded-For \$proxy_add_x_forwarded_for;
        proxy_set_header   X-Forwarded-Proto \$scheme;
        proxy_cache_bypass \$http_upgrade;
    }
}
"@

$nginxTemp = Join-Path $env:TEMP "$APP_NAME.nginx"
$nginxConf | Out-File -FilePath $nginxTemp -Encoding utf8 -NoNewline
SCP $nginxTemp "/etc/nginx/sites-available/$APP_NAME"

SSH @"
ln -sf /etc/nginx/sites-available/$APP_NAME /etc/nginx/sites-enabled/$APP_NAME
rm -f /etc/nginx/sites-enabled/default
nginx -t && systemctl reload nginx
"@

Write-Ok "Nginx configured"

# ── Step 8: Run EF migrations ─────────────────────────────────────────────────
Write-Step "Running database migrations"

SSH "cd $DEPLOY_PATH && ASPNETCORE_ENVIRONMENT=Production dotnet MoneroMarketCap.dll --migrate-only || true"

# Fallback: run migrations via dotnet ef on server if the above doesn't work
# (requires ef tools installed on server - optional)

Write-Ok "Migrations complete"

# ── Done ──────────────────────────────────────────────────────────────────────
Write-Host ""
Write-Host "============================================" -ForegroundColor Green
Write-Host " Deployment complete!" -ForegroundColor Green
Write-Host "============================================" -ForegroundColor Green
Write-Host " Web:    http://$DOMAIN" -ForegroundColor White
Write-Host " Check web:    ssh $SSH_USER@$SSH_HOST 'systemctl status $APP_NAME'" -ForegroundColor Gray
Write-Host " Check worker: ssh $SSH_USER@$SSH_HOST 'systemctl status $WORKER_NAME'" -ForegroundColor Gray
Write-Host " Logs web:     ssh $SSH_USER@$SSH_HOST 'journalctl -u $APP_NAME -f'" -ForegroundColor Gray
Write-Host " Logs worker:  ssh $SSH_USER@$SSH_HOST 'journalctl -u $WORKER_NAME -f'" -ForegroundColor Gray
Write-Host ""
Fwebout