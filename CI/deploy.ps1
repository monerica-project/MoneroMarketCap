# deploy.ps1
# Run from the CI folder: .\deploy.ps1
#
# Flags:
#   -SkipBuild    skip dotnet publish steps
#   -WebOnly      only deploy the web app
#   -WorkerOnly   only deploy the worker
#   -SSL          install Let's Encrypt SSL after deploy
#   -Tor          set up Tor hidden service

param(
    [switch]$SkipBuild,
    [switch]$WebOnly,
    [switch]$WorkerOnly,
    [switch]$SSL,
    [switch]$Tor
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

# -- Load config ---------------------------------------------------------------
$configPath = Join-Path $PSScriptRoot "deploy-config.ps1"
if (-not (Test-Path $configPath)) {
    Write-Error "deploy-config.ps1 not found next to deploy.ps1"
    exit 1
}
. $configPath

# -- Helpers -------------------------------------------------------------------
function Write-Step($msg) { Write-Host "`n==> $msg" -ForegroundColor Cyan }
function Write-Ok($msg)   { Write-Host "    OK: $msg" -ForegroundColor Green }
function Write-Warn($msg) { Write-Host "    WARN: $msg" -ForegroundColor Yellow }

function SSH($cmd) {
    & $PLINK -ssh -pw $SSH_PASSWORD -batch "$SSH_USER@$SSH_HOST" $cmd
    if ($LASTEXITCODE -ne 0) {
        Write-Error "SSH command failed: $cmd"
        exit 1
    }
}

function SSH-Ignore($cmd) {
    & $PLINK -ssh -pw $SSH_PASSWORD -batch "$SSH_USER@$SSH_HOST" $cmd
}

function SSH-Query($cmd) {
    # Runs a command and returns stdout as a trimmed string; does not fail the script.
    return (& $PLINK -ssh -pw $SSH_PASSWORD -batch "$SSH_USER@$SSH_HOST" $cmd 2>$null | Out-String).Trim()
}

function SCP($local, $remote) {
    & $PSCP -pw $SSH_PASSWORD -r -batch $local "${SSH_USER}@${SSH_HOST}:${remote}"
    if ($LASTEXITCODE -ne 0) {
        Write-Error "SCP failed: $local -> $remote"
        exit 1
    }
}

function Save-UnixFile([string]$path, [string]$content) {
    $clean = $content -replace "`r`n", "`n" -replace "`r", "`n"
    [System.IO.File]::WriteAllText($path, $clean, [System.Text.UTF8Encoding]::new($false))
}

function Run-RemoteScript([string]$localPath, [string]$remotePath) {
    SCP $localPath $remotePath
    SSH "chmod +x $remotePath && bash $remotePath"
}

# -- Maintenance page ----------------------------------------------------------
$MaintenanceHtml = @'
<!DOCTYPE html>
<html lang="en">
<head>
  <meta charset="UTF-8">
  <meta name="viewport" content="width=device-width, initial-scale=1.0">
  <meta http-equiv="refresh" content="15">
  <title>Updating - MoneroMarketCap</title>
  <style>
    *, *::before, *::after { box-sizing: border-box; margin: 0; padding: 0; }
    body {
      min-height: 100vh;
      display: flex;
      align-items: center;
      justify-content: center;
      background: #0f0f0f;
      color: #e0e0e0;
      font-family: -apple-system, BlinkMacSystemFont, "Segoe UI", sans-serif;
    }
    .card { text-align: center; padding: 3rem 2.5rem; max-width: 440px; }
    .icon {
      font-size: 2.8rem;
      margin-bottom: 1.25rem;
      display: inline-block;
      animation: spin 3s linear infinite;
    }
    @keyframes spin { from { transform: rotate(0deg); } to { transform: rotate(360deg); } }
    h1 { font-size: 1.5rem; font-weight: 600; margin-bottom: 0.75rem; color: #ff6600; }
    p  { font-size: 0.95rem; line-height: 1.6; color: #aaa; }
    .note { margin-top: 1.75rem; font-size: 0.8rem; color: #555; }
  </style>
</head>
<body>
  <div class="card">
    <div class="icon">&#9881;</div>
    <h1>Updating in progress</h1>
    <p>MoneroMarketCap is being updated and will be back shortly.</p>
    <p class="note">This page refreshes automatically every 15 seconds.</p>
  </div>
</body>
</html>
'@

function Enable-MaintenancePage {
    Write-Step "Enabling maintenance page"

    $htmlFile = Join-Path $env:TEMP "maintenance.html"
    Save-UnixFile $htmlFile $MaintenanceHtml
    SCP $htmlFile "/tmp/maintenance.html"
    SSH "docker exec nginx mkdir -p /var/www"
    SSH "docker cp /tmp/maintenance.html nginx:/var/www/maintenance.html"

    $certNow = SSH-Query "test -f /etc/letsencrypt/live/$DOMAIN/fullchain.pem && echo yes || echo no"

    if ($certNow -eq "yes") {
        $mConf  = "server {`n"
        $mConf += "    listen 80;`n"
        $mConf += "    server_name $DOMAIN www.$DOMAIN;`n"
        $mConf += "    return 301 https://`$host`$request_uri;`n"
        $mConf += "}`n`n"
        $mConf += "server {`n"
        $mConf += "    listen 443 ssl;`n"
        $mConf += "    server_name $DOMAIN www.$DOMAIN;`n"
        $mConf += "    ssl_certificate /etc/letsencrypt/live/$DOMAIN/fullchain.pem;`n"
        $mConf += "    ssl_certificate_key /etc/letsencrypt/live/$DOMAIN/privkey.pem;`n"
        $mConf += "    ssl_protocols TLSv1.2 TLSv1.3;`n"
        $mConf += "    ssl_ciphers HIGH:!aNULL:!MD5;`n"
        $mConf += "    location / {`n"
        $mConf += "        root /var/www;`n"
        $mConf += "        try_files /maintenance.html =503;`n"
        $mConf += "        add_header Retry-After 30;`n"
        $mConf += "    }`n"
        $mConf += "}`n"
    } else {
        $mConf  = "server {`n"
        $mConf += "    listen 80;`n"
        $mConf += "    server_name $DOMAIN www.$DOMAIN;`n"
        $mConf += "    location / {`n"
        $mConf += "        root /var/www;`n"
        $mConf += "        try_files /maintenance.html =503;`n"
        $mConf += "        add_header Retry-After 30;`n"
        $mConf += "    }`n"
        $mConf += "}`n"
    }

    $mFile = Join-Path $env:TEMP "$APP_NAME.maint.conf"
    Save-UnixFile $mFile $mConf
    SCP $mFile "/tmp/$APP_NAME.conf"
    SSH "docker cp /tmp/$APP_NAME.conf nginx:/etc/nginx/conf.d/$APP_NAME.conf"
    SSH "docker exec nginx nginx -s reload"
    Write-Ok "Maintenance page live"
}

function Wait-ForApp {
    Write-Step "Waiting for app to become healthy"
    $maxAttempts = 24   # 2 min total
    $attempt = 0
    $healthy = $false
    $lastStatus = ""
    while ($attempt -lt $maxAttempts) {
        $attempt++
        # -L follows redirects; only accept final 200. A broken page that redirects
        # or throws 500 will NOT pass this check.
        $lastStatus = SSH-Query "curl -sL -o /dev/null -w '%{http_code}' http://localhost:$APP_PORT/ 2>/dev/null || echo 000"
        if ($lastStatus -eq "200") { $healthy = $true; break }
        Write-Host "    Attempt $attempt/$maxAttempts - HTTP $lastStatus, retrying in 5s..." -ForegroundColor Yellow
        Start-Sleep -Seconds 5
    }
    if (-not $healthy) {
        Write-Host ""
        Write-Host "    App did not respond with HTTP 200 after $maxAttempts attempts." -ForegroundColor Red
        Write-Host "    Last status: HTTP $lastStatus" -ForegroundColor Red
        Write-Host "    Check logs:  journalctl -u $APP_NAME -n 80 --no-pager" -ForegroundColor Red
        Write-Error "Deployment aborted - app unhealthy"
        exit 1
    }
    Write-Ok "App is healthy (HTTP 200)"
}

# -- Step 1: Build -------------------------------------------------------------
if (-not $SkipBuild) {
    if (-not $WorkerOnly) {
        Write-Step "Building web app"
        $webOut = Join-Path $PSScriptRoot "..\publish\web"
        if (Test-Path $webOut) { Remove-Item $webOut -Recurse -Force }
        dotnet publish $WEB_PROJECT -c Release -r linux-x64 --self-contained false -o $webOut
        if ($LASTEXITCODE -ne 0) { Write-Error "Web build failed"; exit 1 }
        Write-Ok "Web app built"
    }

    if (-not $WebOnly) {
        Write-Step "Building worker"
        $workerOut = Join-Path $PSScriptRoot "..\publish\worker"
        if (Test-Path $workerOut) { Remove-Item $workerOut -Recurse -Force }
        dotnet publish $WORKER_PROJECT -c Release -r linux-x64 --self-contained false -o $workerOut
        if ($LASTEXITCODE -ne 0) { Write-Error "Worker build failed"; exit 1 }
        Write-Ok "Worker built"
    }
}

# -- Step 2: Bootstrap server (idempotent) ------------------------------------
Write-Step "Bootstrapping server"

SSH "apt-get update -q"
SSH "command -v dotnet > /dev/null 2>&1 || (wget -q https://packages.microsoft.com/config/ubuntu/22.04/packages-microsoft-prod.deb -O /tmp/ms.deb && dpkg -i /tmp/ms.deb && apt-get update -q && apt-get install -y aspnetcore-runtime-10.0)"
SSH "command -v psql > /dev/null 2>&1 || (apt-get install -y postgresql postgresql-contrib && systemctl enable postgresql && systemctl start postgresql)"
SSH-Ignore "systemctl start postgresql 2>/dev/null || true"
SSH "mkdir -p $DEPLOY_PATH $WORKER_PATH"
SSH-Ignore "ufw allow $APP_PORT/tcp 2>/dev/null || true"

Write-Ok "Server dependencies ready"

# -- Step 3: PostgreSQL --------------------------------------------------------
Write-Step "Setting up PostgreSQL"

$pgScript = "#!/bin/bash`n" +
    "set -e`n" +
    "sudo -u postgres psql -c `"CREATE USER $DB_USER WITH PASSWORD '$DB_PASSWORD';`" 2>/dev/null || true`n" +
    "sudo -u postgres psql -c `"CREATE DATABASE $DB_NAME OWNER $DB_USER;`" 2>/dev/null || true`n" +
    "sudo -u postgres psql -c `"GRANT ALL PRIVILEGES ON DATABASE $DB_NAME TO $DB_USER;`" 2>/dev/null || true`n" +
    "sudo -u postgres psql -d $DB_NAME -c `"GRANT ALL ON SCHEMA public TO $DB_USER;`" 2>/dev/null || true`n" +
    "sudo -u postgres psql -d $DB_NAME -c `"ALTER DEFAULT PRIVILEGES IN SCHEMA public GRANT ALL ON TABLES TO $DB_USER;`" 2>/dev/null || true`n" +
    "echo 'DB setup complete'`n"

$pgScriptFile = Join-Path $env:TEMP "pg-setup.sh"
Save-UnixFile $pgScriptFile $pgScript
Run-RemoteScript $pgScriptFile "/tmp/pg-setup.sh"

Write-Ok "Database ready"

# -- Step 4: Write appsettings.json --------------------------------------------
Write-Step "Writing config"

$connString = "Host=localhost;Port=5432;Database=$DB_NAME;Username=$DB_USER;Password=$DB_PASSWORD"

# If Tor is already set up on the server, grab the onion hostname.
$onionHost = ""
try {
    $onionHost = SSH-Query "cat /var/lib/tor/moneromarketcap/hostname 2>/dev/null || echo ''"
} catch {
    $onionHost = ""
}

if ($onionHost) {
    Write-Host "    Found onion: $onionHost" -ForegroundColor Gray
}

# Monerod config is optional. If $MONEROD_RPC_URL is not set or empty,
# the supply worker simply doesn't register and the site works normally.
$hasMonerod = $false
try {
    if ($MONEROD_RPC_URL -and $MONEROD_RPC_URL.Trim().Length -gt 0) {
        $hasMonerod = $true
    }
} catch {
    $hasMonerod = $false
}

$webCfg = [ordered]@{
    ConnectionStrings = @{
        DefaultConnection = $connString
    }
    CoinGecko = [ordered]@{
        ApiKey                 = $COINGECKO_API_KEY
        BaseUrl                = $COINGECKO_BASE_URL
        ApiKeyHeader           = $COINGECKO_API_KEY_HEADER
        TopCoinsOnStartup      = 100
        RefreshIntervalMinutes = [int]$COINGECKO_REFRESH_MINUTES
    }
    Admin = [ordered]@{
        Username = $ADMIN_USERNAME
        Password = $ADMIN_PASSWORD
    }
    Sponsors = [ordered]@{
        SourceUrl             = $SPONSOR_URL
        CacheTtlMinutes       = [int]$SPONSOR_CACHE_TTL
        RotateIntervalSeconds = [int]$SPONSOR_ROTATE_SECONDS
    }
}

if ($onionHost) {
    $webCfg.Tor = [ordered]@{
        OnionHost = $onionHost
    }
}

$workerCfg = [ordered]@{
    ConnectionStrings = @{
        DefaultConnection = $connString
    }
    CoinGecko = [ordered]@{
        ApiKey                 = $COINGECKO_API_KEY
        BaseUrl                = $COINGECKO_BASE_URL
        ApiKeyHeader           = $COINGECKO_API_KEY_HEADER
        RefreshIntervalMinutes = [int]$COINGECKO_REFRESH_MINUTES
    }
}

if ($hasMonerod) {
    $workerCfg.Monerod = [ordered]@{
        RpcUrl                 = $MONEROD_RPC_URL
        RefreshIntervalMinutes = [int]$MONEROD_REFRESH_MINUTES
        TimeoutSeconds         = [int]$MONEROD_TIMEOUT_SECONDS
    }
    Write-Host "    Monerod RPC: $MONEROD_RPC_URL" -ForegroundColor Gray
} else {
    Write-Host "    Monerod RPC: not configured (supply worker disabled)" -ForegroundColor Gray
}

$webCfgContent    = ($webCfg    | ConvertTo-Json -Depth 5) + "`n"
$workerCfgContent = ($workerCfg | ConvertTo-Json -Depth 5) + "`n"

$webCfgFile    = Join-Path $env:TEMP "web-appsettings.json"
$workerCfgFile = Join-Path $env:TEMP "worker-appsettings.json"
Save-UnixFile $webCfgFile    $webCfgContent
Save-UnixFile $workerCfgFile $workerCfgContent

Write-Ok "Config ready"

# -- Step 5: Deploy web app ----------------------------------------------------
# Order matters:
#   1. Show maintenance page.
#   2. Stop old service.
#   3. Copy new binaries + config.
#   4. Run migrations against the NEW DLL, while NO service is running.
#      If migrations fail, abort — we never start a binary against wrong schema.
#   5. Start new service, Wait-ForApp requires HTTP 200.
#   6. (Step 7) Restore real nginx config.
if (-not $WorkerOnly) {
    Write-Step "Deploying web app"

    Enable-MaintenancePage
    SSH-Ignore "systemctl stop $APP_NAME 2>/dev/null || true"

    $webOut = Join-Path $PSScriptRoot "..\publish\web"
    $webTar = Join-Path $env:TEMP "web.tar.gz"
    Push-Location $webOut
    & tar -czf $webTar .
    Pop-Location

    SCP $webTar "/tmp/web.tar.gz"
    SSH "mkdir -p $DEPLOY_PATH && tar -xzf /tmp/web.tar.gz -C $DEPLOY_PATH && rm /tmp/web.tar.gz"
    SCP $webCfgFile "$DEPLOY_PATH/appsettings.json"
    SSH "chown -R www-data:www-data $DEPLOY_PATH && chmod -R 755 $DEPLOY_PATH"

    # --- Run EF migrations BEFORE starting the new service ---
    # set -e ensures a migration failure exits non-zero, Run-RemoteScript fails,
    # and the deploy aborts before we start a binary against the wrong schema.
    Write-Step "Running database migrations (pre-start)"

    $migrateScript = "#!/bin/bash`n" +
        "set -e`n" +
        "export ConnectionStrings__DefaultConnection='Host=localhost;Port=5432;Database=$DB_NAME;Username=$DB_USER;Password=$DB_PASSWORD'`n" +
        "export ASPNETCORE_ENVIRONMENT=Production`n" +
        "cd $DEPLOY_PATH`n" +
        "echo 'Running migrations...'`n" +
        "dotnet MoneroMarketCap.Web.dll --migrate-only`n" +
        "echo 'Migrations complete.'`n"

    $migrateScriptFile = Join-Path $env:TEMP "migrate.sh"
    Save-UnixFile $migrateScriptFile $migrateScript
    Run-RemoteScript $migrateScriptFile "/tmp/migrate.sh"
    Write-Ok "Migrations applied"

    # --- Write systemd unit and start service ---
    $svcContent = "[Unit]`n" +
        "Description=MoneroMarketCap Web ($DOMAIN)`n" +
        "After=network.target postgresql.service`n" +
        "`n" +
        "[Service]`n" +
        "WorkingDirectory=$DEPLOY_PATH`n" +
        "ExecStart=/usr/bin/dotnet $DEPLOY_PATH/MoneroMarketCap.Web.dll`n" +
        "Restart=always`n" +
        "RestartSec=10`n" +
        "User=www-data`n" +
        "Environment=ASPNETCORE_ENVIRONMENT=Production`n" +
        "Environment=ASPNETCORE_URLS=http://0.0.0.0:$APP_PORT`n" +
        "Environment=DOTNET_PRINT_TELEMETRY_MESSAGE=false`n" +
        "`n" +
        "[Install]`n" +
        "WantedBy=multi-user.target`n"

    $svcFile = Join-Path $env:TEMP "$APP_NAME.service"
    Save-UnixFile $svcFile $svcContent
    SCP $svcFile "/etc/systemd/system/$APP_NAME.service"
    SSH "systemctl daemon-reload && systemctl enable $APP_NAME && systemctl restart $APP_NAME"

    Wait-ForApp  # exits the script if app doesn't return HTTP 200

    Write-Ok "Web app deployed on port $APP_PORT"
}

# -- Step 6: Deploy worker -----------------------------------------------------
if (-not $WebOnly) {
    Write-Step "Deploying worker"

    SSH-Ignore "systemctl stop $WORKER_NAME 2>/dev/null || true"

    $workerOut = Join-Path $PSScriptRoot "..\publish\worker"
    $workerTar = Join-Path $env:TEMP "worker.tar.gz"
    Push-Location $workerOut
    & tar -czf $workerTar .
    Pop-Location

    SCP $workerTar "/tmp/worker.tar.gz"
    SSH "mkdir -p $WORKER_PATH && tar -xzf /tmp/worker.tar.gz -C $WORKER_PATH && rm /tmp/worker.tar.gz"
    SCP $workerCfgFile "$WORKER_PATH/appsettings.json"
    SSH "chown -R www-data:www-data $WORKER_PATH && chmod -R 755 $WORKER_PATH"

    $workerSvcContent = "[Unit]`n" +
        "Description=MoneroMarketCap Worker ($DOMAIN)`n" +
        "After=network.target postgresql.service`n" +
        "`n" +
        "[Service]`n" +
        "WorkingDirectory=$WORKER_PATH`n" +
        "ExecStart=/usr/bin/dotnet $WORKER_PATH/MoneroMarketCap.Worker.dll`n" +
        "Restart=always`n" +
        "RestartSec=10`n" +
        "User=www-data`n" +
        "Environment=ASPNETCORE_ENVIRONMENT=Production`n" +
        "Environment=DOTNET_ENVIRONMENT=Production`n" +
        "Environment=DOTNET_PRINT_TELEMETRY_MESSAGE=false`n" +
        "`n" +
        "[Install]`n" +
        "WantedBy=multi-user.target`n"

    $workerSvcFile = Join-Path $env:TEMP "$WORKER_NAME.service"
    Save-UnixFile $workerSvcFile $workerSvcContent
    SCP $workerSvcFile "/etc/systemd/system/$WORKER_NAME.service"
    SSH "systemctl daemon-reload && systemctl enable $WORKER_NAME && systemctl restart $WORKER_NAME"

    Write-Ok "Worker deployed"
}

# -- Step 7: Configure Nginx (restore real proxy config, removing maintenance) -
Write-Step "Configuring Nginx"

$certExists = SSH-Query "test -f /etc/letsencrypt/live/$DOMAIN/fullchain.pem && echo yes || echo no"

if ($certExists -eq "yes") {
    SSH "cp -L /etc/letsencrypt/live/$DOMAIN/fullchain.pem /tmp/fullchain.pem && cp -L /etc/letsencrypt/live/$DOMAIN/privkey.pem /tmp/privkey.pem"
    SSH "docker exec nginx mkdir -p /etc/letsencrypt/live/$DOMAIN"
    SSH "docker cp /tmp/fullchain.pem nginx:/etc/letsencrypt/live/$DOMAIN/fullchain.pem"
    SSH "docker cp /tmp/privkey.pem nginx:/etc/letsencrypt/live/$DOMAIN/privkey.pem"

    $nginxSslConf  = "server {`n"
    $nginxSslConf += "    listen 80;`n"
    $nginxSslConf += "    server_name $DOMAIN www.$DOMAIN;`n"
    $nginxSslConf += "    return 301 https://`$host`$request_uri;`n"
    $nginxSslConf += "}`n"
    $nginxSslConf += "`n"
    $nginxSslConf += "server {`n"
    $nginxSslConf += "    listen 443 ssl;`n"
    $nginxSslConf += "    server_name $DOMAIN www.$DOMAIN;`n"
    $nginxSslConf += "    ssl_certificate /etc/letsencrypt/live/$DOMAIN/fullchain.pem;`n"
    $nginxSslConf += "    ssl_certificate_key /etc/letsencrypt/live/$DOMAIN/privkey.pem;`n"
    $nginxSslConf += "    ssl_protocols TLSv1.2 TLSv1.3;`n"
    $nginxSslConf += "    ssl_ciphers HIGH:!aNULL:!MD5;`n"
    $nginxSslConf += "`n"
    $nginxSslConf += "    location / {`n"
    $nginxSslConf += "        proxy_pass         http://172.17.0.1:$APP_PORT;`n"
    $nginxSslConf += "        proxy_http_version 1.1;`n"
    $nginxSslConf += "        proxy_set_header   Upgrade `$http_upgrade;`n"
    $nginxSslConf += "        proxy_set_header   Connection keep-alive;`n"
    $nginxSslConf += "        proxy_set_header   Host `$host;`n"
    $nginxSslConf += "        proxy_set_header   X-Real-IP `$remote_addr;`n"
    $nginxSslConf += "        proxy_set_header   X-Forwarded-For `$proxy_add_x_forwarded_for;`n"
    $nginxSslConf += "        proxy_set_header   X-Forwarded-Proto `$scheme;`n"
    $nginxSslConf += "        proxy_cache_bypass `$http_upgrade;`n"
    $nginxSslConf += "    }`n"
    $nginxSslConf += "}`n"

    $nginxConfFile = Join-Path $env:TEMP "$APP_NAME.nginx.conf"
    Save-UnixFile $nginxConfFile $nginxSslConf
    SCP $nginxConfFile "/tmp/$APP_NAME.conf"
    SSH "docker cp /tmp/$APP_NAME.conf nginx:/etc/nginx/conf.d/$APP_NAME.conf"
    SSH "docker exec nginx nginx -s reload"
    Write-Ok "Nginx configured for $DOMAIN (HTTPS)"
} else {
    $nginxConf  = "server {`n"
    $nginxConf += "    listen 80;`n"
    $nginxConf += "    server_name $DOMAIN www.$DOMAIN;`n"
    $nginxConf += "`n"
    $nginxConf += "    location / {`n"
    $nginxConf += "        proxy_pass         http://172.17.0.1:$APP_PORT;`n"
    $nginxConf += "        proxy_http_version 1.1;`n"
    $nginxConf += "        proxy_set_header   Upgrade `$http_upgrade;`n"
    $nginxConf += "        proxy_set_header   Connection keep-alive;`n"
    $nginxConf += "        proxy_set_header   Host `$host;`n"
    $nginxConf += "        proxy_set_header   X-Real-IP `$remote_addr;`n"
    $nginxConf += "        proxy_set_header   X-Forwarded-For `$proxy_add_x_forwarded_for;`n"
    $nginxConf += "        proxy_set_header   X-Forwarded-Proto `$scheme;`n"
    $nginxConf += "        proxy_cache_bypass `$http_upgrade;`n"
    $nginxConf += "    }`n"
    $nginxConf += "}`n"

    $nginxConfFile = Join-Path $env:TEMP "$APP_NAME.nginx.conf"
    Save-UnixFile $nginxConfFile $nginxConf
    SCP $nginxConfFile "/tmp/$APP_NAME.conf"
    SSH "docker cp /tmp/$APP_NAME.conf nginx:/etc/nginx/conf.d/$APP_NAME.conf"
    SSH "docker exec nginx nginx -s reload"
    Write-Ok "Nginx configured for $DOMAIN (HTTP only - run with -SSL to enable HTTPS)"
}

# -- Step 8: SSL (get cert - only needed once) ---------------------------------
if ($SSL) {
    Write-Step "Getting SSL certificate"

    SSH "apt-get install -y certbot"

    $sslScript  = "#!/bin/bash`n"
    $sslScript += "set -e`n"
    $sslScript += "echo 'Stopping docker nginx...'`n"
    $sslScript += "docker stop nginx`n"
    $sslScript += "sleep 2`n"
    $sslScript += "echo 'Getting cert...'`n"
    $sslScript += "certbot certonly --standalone -d $DOMAIN --non-interactive --agree-tos -m admin@$DOMAIN`n"
    $sslScript += "echo 'Starting docker nginx...'`n"
    $sslScript += "docker start nginx`n"
    $sslScript += "sleep 2`n"
    $sslScript += "echo 'Done'`n"

    $sslScriptFile = Join-Path $env:TEMP "ssl-setup.sh"
    Save-UnixFile $sslScriptFile $sslScript
    Run-RemoteScript $sslScriptFile "/tmp/ssl-setup.sh"

    Write-Ok "Certificate obtained - re-running nginx config with SSL..."

    SSH "cp -L /etc/letsencrypt/live/$DOMAIN/fullchain.pem /tmp/fullchain.pem && cp -L /etc/letsencrypt/live/$DOMAIN/privkey.pem /tmp/privkey.pem"
    SSH "docker exec nginx mkdir -p /etc/letsencrypt/live/$DOMAIN"
    SSH "docker cp /tmp/fullchain.pem nginx:/etc/letsencrypt/live/$DOMAIN/fullchain.pem"
    SSH "docker cp /tmp/privkey.pem nginx:/etc/letsencrypt/live/$DOMAIN/privkey.pem"

    $nginxSslConf  = "server {`n"
    $nginxSslConf += "    listen 80;`n"
    $nginxSslConf += "    server_name $DOMAIN www.$DOMAIN;`n"
    $nginxSslConf += "    return 301 https://`$host`$request_uri;`n"
    $nginxSslConf += "}`n"
    $nginxSslConf += "`n"
    $nginxSslConf += "server {`n"
    $nginxSslConf += "    listen 443 ssl;`n"
    $nginxSslConf += "    server_name $DOMAIN www.$DOMAIN;`n"
    $nginxSslConf += "    ssl_certificate /etc/letsencrypt/live/$DOMAIN/fullchain.pem;`n"
    $nginxSslConf += "    ssl_certificate_key /etc/letsencrypt/live/$DOMAIN/privkey.pem;`n"
    $nginxSslConf += "    ssl_protocols TLSv1.2 TLSv1.3;`n"
    $nginxSslConf += "    ssl_ciphers HIGH:!aNULL:!MD5;`n"
    $nginxSslConf += "`n"
    $nginxSslConf += "    location / {`n"
    $nginxSslConf += "        proxy_pass         http://172.17.0.1:$APP_PORT;`n"
    $nginxSslConf += "        proxy_http_version 1.1;`n"
    $nginxSslConf += "        proxy_set_header   Upgrade `$http_upgrade;`n"
    $nginxSslConf += "        proxy_set_header   Connection keep-alive;`n"
    $nginxSslConf += "        proxy_set_header   Host `$host;`n"
    $nginxSslConf += "        proxy_set_header   X-Real-IP `$remote_addr;`n"
    $nginxSslConf += "        proxy_set_header   X-Forwarded-For `$proxy_add_x_forwarded_for;`n"
    $nginxSslConf += "        proxy_set_header   X-Forwarded-Proto `$scheme;`n"
    $nginxSslConf += "        proxy_cache_bypass `$http_upgrade;`n"
    $nginxSslConf += "    }`n"
    $nginxSslConf += "}`n"

    $nginxSslFile = Join-Path $env:TEMP "$APP_NAME.ssl.nginx.conf"
    Save-UnixFile $nginxSslFile $nginxSslConf
    SCP $nginxSslFile "/tmp/$APP_NAME.conf"
    SSH "docker cp /tmp/$APP_NAME.conf nginx:/etc/nginx/conf.d/$APP_NAME.conf"
    SSH "docker exec nginx nginx -s reload"

    Write-Ok "SSL installed for $DOMAIN"
}

# -- Step 9: Tor hidden service (first time only) -----------------------------
if ($Tor) {
    Write-Step "Setting up Tor hidden service for $DOMAIN"

    $torScript  = "#!/bin/bash`n"
    $torScript += "set -e`n"
    $torScript += "apt-get install -y tor`n"
    $torScript += "systemctl enable tor`n"
    $torScript += "if ! grep -q 'moneromarketcap' /etc/tor/torrc; then`n"
    $torScript += "  echo '' >> /etc/tor/torrc`n"
    $torScript += "  echo '# MoneroMarketCap hidden service' >> /etc/tor/torrc`n"
    $torScript += "  echo 'HiddenServiceDir /var/lib/tor/moneromarketcap/' >> /etc/tor/torrc`n"
    $torScript += "  echo 'HiddenServicePort 80 127.0.0.1:$APP_PORT' >> /etc/tor/torrc`n"
    $torScript += "fi`n"
    $torScript += "systemctl restart tor`n"
    $torScript += "sleep 5`n"
    $torScript += "echo 'Onion address:'`n"
    $torScript += "cat /var/lib/tor/moneromarketcap/hostname`n"

    $torScriptFile = Join-Path $env:TEMP "tor-setup.sh"
    Save-UnixFile $torScriptFile $torScript
    Run-RemoteScript $torScriptFile "/tmp/tor-setup.sh"

    $onionAddress = SSH-Query "cat /var/lib/tor/moneromarketcap/hostname 2>/dev/null || echo 'not ready yet'"

    Write-Ok "Tor hidden service configured"
    Write-Host "   Onion: $onionAddress" -ForegroundColor Magenta
}

# -- Done ----------------------------------------------------------------------
Write-Host ""
Write-Host "============================================" -ForegroundColor Green
Write-Host " Deployment complete!" -ForegroundColor Green
Write-Host "============================================" -ForegroundColor Green
Write-Host " Site:   https://$DOMAIN" -ForegroundColor White
if ($Tor) {
    $onion = SSH-Query "cat /var/lib/tor/moneromarketcap/hostname 2>/dev/null || echo 'pending'"
    Write-Host " Onion:  http://$onion" -ForegroundColor Magenta
}
Write-Host ""
Write-Host " Useful commands:" -ForegroundColor Gray
Write-Host "   Web status:    plink -ssh -pw PASSWORD root@$SSH_HOST systemctl status $APP_NAME" -ForegroundColor Gray
Write-Host "   Worker status: plink -ssh -pw PASSWORD root@$SSH_HOST systemctl status $WORKER_NAME" -ForegroundColor Gray
Write-Host "   Web logs:      plink -ssh -pw PASSWORD root@$SSH_HOST journalctl -u $APP_NAME -f" -ForegroundColor Gray
Write-Host "   Worker logs:   plink -ssh -pw PASSWORD root@$SSH_HOST journalctl -u $WORKER_NAME -f" -ForegroundColor Gray
Write-Host ""