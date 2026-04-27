#!/usr/bin/env bash
# deploy.sh
# Run from the CI folder: ./deploy.sh
#
# Flags:
#   --skip-build    skip dotnet publish steps
#   --web-only      only deploy the web app
#   --worker-only   only deploy the worker
#   --ssl           install Let's Encrypt SSL after deploy
#   --tor           set up Tor hidden service
#
# Targets Ubuntu 24.04 LTS on the server (.NET 10 from Canonical archive).
# Requires locally: dotnet, jq, ssh, scp, tar. If SSH_PASSWORD is set in
# deploy-config.sh, sshpass is also required.

set -euo pipefail

# -- Parse flags ---------------------------------------------------------------
SKIP_BUILD=0
WEB_ONLY=0
WORKER_ONLY=0
DO_SSL=0
DO_TOR=0

while [[ $# -gt 0 ]]; do
    case "$1" in
        --skip-build)  SKIP_BUILD=1; shift ;;
        --web-only)    WEB_ONLY=1; shift ;;
        --worker-only) WORKER_ONLY=1; shift ;;
        --ssl)         DO_SSL=1; shift ;;
        --tor)         DO_TOR=1; shift ;;
        -h|--help)
            sed -n '2,15p' "$0"
            exit 0
            ;;
        *) echo "Unknown flag: $1" >&2; exit 1 ;;
    esac
done

# -- Load config ---------------------------------------------------------------
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
CONFIG_PATH="$SCRIPT_DIR/deploy-config.sh"

if [[ ! -f "$CONFIG_PATH" ]]; then
    echo "deploy-config.sh not found next to deploy.sh" >&2
    exit 1
fi
# shellcheck disable=SC1090
source "$CONFIG_PATH"

# -- Tool checks ---------------------------------------------------------------
for tool in dotnet jq ssh scp tar; do
    if ! command -v "$tool" >/dev/null 2>&1; then
        echo "Required tool missing: $tool" >&2
        exit 1
    fi
done

# -- Helpers -------------------------------------------------------------------
C_CYAN=$'\e[36m'; C_GREEN=$'\e[32m'; C_YELLOW=$'\e[33m'; C_RED=$'\e[31m'; C_GRAY=$'\e[90m'; C_MAGENTA=$'\e[35m'; C_RESET=$'\e[0m'

write_step() { echo; echo "${C_CYAN}==> $1${C_RESET}"; }
write_ok()   { echo "    ${C_GREEN}OK: $1${C_RESET}"; }
write_warn() { echo "    ${C_YELLOW}WARN: $1${C_RESET}"; }
write_err()  { echo "    ${C_RED}ERR: $1${C_RESET}" >&2; }

# Build SSH command prefix once
SSH_PREFIX=()
if [[ -n "${SSH_PASSWORD:-}" ]]; then
    if ! command -v sshpass >/dev/null 2>&1; then
        echo "SSH_PASSWORD is set but sshpass is not installed." >&2
        echo "Either install: sudo apt install -y sshpass" >&2
        echo "Or switch to key auth and remove SSH_PASSWORD from deploy-config.sh." >&2
        exit 1
    fi
    SSH_PREFIX=(sshpass -p "$SSH_PASSWORD")
fi

SSH_OPTS=(-o StrictHostKeyChecking=accept-new -o ConnectTimeout=10 -o ServerAliveInterval=30)

ssh_run() {
    "${SSH_PREFIX[@]}" ssh "${SSH_OPTS[@]}" "$SSH_USER@$SSH_HOST" "$1"
}

ssh_run_ignore() {
    "${SSH_PREFIX[@]}" ssh "${SSH_OPTS[@]}" "$SSH_USER@$SSH_HOST" "$1" || true
}

ssh_query() {
    # Returns trimmed stdout; never fails the script.
    "${SSH_PREFIX[@]}" ssh "${SSH_OPTS[@]}" "$SSH_USER@$SSH_HOST" "$1" 2>/dev/null | tr -d '\r' || true
}

scp_send() {
    "${SSH_PREFIX[@]}" scp "${SSH_OPTS[@]}" -r "$1" "$SSH_USER@$SSH_HOST:$2"
}

run_remote_script() {
    local local_path="$1"
    local remote_path="$2"
    scp_send "$local_path" "$remote_path"
    ssh_run "chmod +x $remote_path && bash $remote_path"
}

# -- Maintenance page ----------------------------------------------------------
read -r -d '' MAINTENANCE_HTML <<'HTML' || true
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
HTML

write_nginx_maint_ssl() {
cat <<EOF
server {
    listen 80;
    server_name $DOMAIN www.$DOMAIN;
    return 301 https://\$host\$request_uri;
}

server {
    listen 443 ssl;
    server_name $DOMAIN www.$DOMAIN;
    ssl_certificate /etc/letsencrypt/live/$DOMAIN/fullchain.pem;
    ssl_certificate_key /etc/letsencrypt/live/$DOMAIN/privkey.pem;
    ssl_protocols TLSv1.2 TLSv1.3;
    ssl_ciphers HIGH:!aNULL:!MD5;
    location / {
        root /var/www;
        try_files /maintenance.html =503;
        add_header Retry-After 30;
    }
}
EOF
}

write_nginx_maint_plain() {
cat <<EOF
server {
    listen 80;
    server_name $DOMAIN www.$DOMAIN;
    location / {
        root /var/www;
        try_files /maintenance.html =503;
        add_header Retry-After 30;
    }
}
EOF
}

write_nginx_proxy_ssl() {
cat <<EOF
server {
    listen 80;
    server_name $DOMAIN www.$DOMAIN;
    return 301 https://\$host\$request_uri;
}

server {
    listen 443 ssl;
    server_name $DOMAIN www.$DOMAIN;
    ssl_certificate /etc/letsencrypt/live/$DOMAIN/fullchain.pem;
    ssl_certificate_key /etc/letsencrypt/live/$DOMAIN/privkey.pem;
    ssl_protocols TLSv1.2 TLSv1.3;
    ssl_ciphers HIGH:!aNULL:!MD5;

    location / {
        proxy_pass         http://172.17.0.1:$APP_PORT;
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
EOF
}

write_nginx_proxy_plain() {
cat <<EOF
server {
    listen 80;
    server_name $DOMAIN www.$DOMAIN;

    location / {
        proxy_pass         http://172.17.0.1:$APP_PORT;
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
EOF
}

enable_maintenance_page() {
    write_step "Enabling maintenance page"

    local html_file="/tmp/maintenance.html.local"
    printf '%s\n' "$MAINTENANCE_HTML" > "$html_file"
    scp_send "$html_file" "/tmp/maintenance.html"
    ssh_run "docker exec nginx mkdir -p /var/www"
    ssh_run "docker cp /tmp/maintenance.html nginx:/var/www/maintenance.html"

    local cert_now
    cert_now=$(ssh_query "test -f /etc/letsencrypt/live/$DOMAIN/fullchain.pem && echo yes || echo no")

    local m_file="/tmp/$APP_NAME.maint.conf"
    if [[ "$cert_now" == "yes" ]]; then
        write_nginx_maint_ssl > "$m_file"
    else
        write_nginx_maint_plain > "$m_file"
    fi

    scp_send "$m_file" "/tmp/$APP_NAME.conf"
    ssh_run "docker cp /tmp/$APP_NAME.conf nginx:/etc/nginx/conf.d/$APP_NAME.conf"
    ssh_run "docker exec nginx nginx -s reload"
    write_ok "Maintenance page live"
}

wait_for_app() {
    write_step "Waiting for app to become healthy"
    local max_attempts=24    # 2 min total
    local last_status=""
    local i
    for ((i=1; i<=max_attempts; i++)); do
        # -L follows redirects; only accept final 200. A broken page that redirects
        # or throws 500 will NOT pass this check.
        last_status=$(ssh_query "curl -sL -o /dev/null -w '%{http_code}' http://localhost:$APP_PORT/ 2>/dev/null || echo 000")
        if [[ "$last_status" == "200" ]]; then
            write_ok "App is healthy (HTTP 200)"
            return
        fi
        echo "    ${C_YELLOW}Attempt $i/$max_attempts - HTTP $last_status, retrying in 5s...${C_RESET}"
        sleep 5
    done
    echo
    write_err "App did not respond with HTTP 200 after $max_attempts attempts."
    write_err "Last status: HTTP $last_status"
    write_err "Check logs:  journalctl -u $APP_NAME -n 80 --no-pager"
    exit 1
}

# -- Step 1: Build -------------------------------------------------------------
if [[ $SKIP_BUILD -eq 0 ]]; then
    if [[ $WORKER_ONLY -eq 0 ]]; then
        write_step "Building web app"
        WEB_OUT="$SCRIPT_DIR/../publish/web"
        rm -rf "$WEB_OUT"
        dotnet publish "$WEB_PROJECT" -c Release -r linux-x64 --self-contained false -o "$WEB_OUT"
        write_ok "Web app built"
    fi

    if [[ $WEB_ONLY -eq 0 ]]; then
        write_step "Building worker"
        WORKER_OUT="$SCRIPT_DIR/../publish/worker"
        rm -rf "$WORKER_OUT"
        dotnet publish "$WORKER_PROJECT" -c Release -r linux-x64 --self-contained false -o "$WORKER_OUT"
        write_ok "Worker built"
    fi
fi

# These vars are needed by later steps even if SKIP_BUILD is set.
WEB_OUT="${WEB_OUT:-$SCRIPT_DIR/../publish/web}"
WORKER_OUT="${WORKER_OUT:-$SCRIPT_DIR/../publish/worker}"

# -- Step 2: Bootstrap server (idempotent) ------------------------------------
write_step "Bootstrapping server"

ssh_run "apt-get update -q"
# Ubuntu 24.04 has aspnetcore-runtime-10.0 in the Canonical archive directly
# (no Microsoft package feed needed; avoids the .NET package mix-up problem).
ssh_run "command -v dotnet >/dev/null 2>&1 || apt-get install -y aspnetcore-runtime-10.0"
ssh_run "command -v psql >/dev/null 2>&1 || (apt-get install -y postgresql postgresql-contrib && systemctl enable postgresql && systemctl start postgresql)"
ssh_run_ignore "systemctl start postgresql 2>/dev/null || true"
ssh_run "mkdir -p $DEPLOY_PATH $WORKER_PATH"
ssh_run_ignore "ufw allow $APP_PORT/tcp 2>/dev/null || true"

write_ok "Server dependencies ready"

# -- Step 3: PostgreSQL --------------------------------------------------------
write_step "Setting up PostgreSQL"

PG_SCRIPT_FILE="/tmp/pg-setup.sh.local"
cat > "$PG_SCRIPT_FILE" <<EOF
#!/bin/bash
set -e
sudo -u postgres psql -c "CREATE USER $DB_USER WITH PASSWORD '$DB_PASSWORD';" 2>/dev/null || true
sudo -u postgres psql -c "CREATE DATABASE $DB_NAME OWNER $DB_USER;" 2>/dev/null || true
sudo -u postgres psql -c "GRANT ALL PRIVILEGES ON DATABASE $DB_NAME TO $DB_USER;" 2>/dev/null || true
sudo -u postgres psql -d $DB_NAME -c "GRANT ALL ON SCHEMA public TO $DB_USER;" 2>/dev/null || true
sudo -u postgres psql -d $DB_NAME -c "ALTER DEFAULT PRIVILEGES IN SCHEMA public GRANT ALL ON TABLES TO $DB_USER;" 2>/dev/null || true
echo 'DB setup complete'
EOF
run_remote_script "$PG_SCRIPT_FILE" "/tmp/pg-setup.sh"

write_ok "Database ready"

# -- Step 4: Write appsettings.json --------------------------------------------
write_step "Writing config"

CONN_STRING="Host=localhost;Port=5432;Database=$DB_NAME;Username=$DB_USER;Password=$DB_PASSWORD"

ONION_HOST=$(ssh_query "cat /var/lib/tor/moneromarketcap/hostname 2>/dev/null || echo ''")
if [[ -n "$ONION_HOST" ]]; then
    echo "    ${C_GRAY}Found onion: $ONION_HOST${C_RESET}"
fi

# BTCPay config is optional. If unset/empty, supply worker doesn't register
# and the page falls back to CoinGecko.
HAS_BTCPAY=0
if [[ -n "${BTCPAY_BASE_URL:-}" && -n "${BTCPAY_API_KEY:-}" ]]; then
    HAS_BTCPAY=1
fi

WEB_CFG_FILE="/tmp/web-appsettings.json"
WORKER_CFG_FILE="/tmp/worker-appsettings.json"

# Web config (Tor section conditional on $ONION_HOST)
jq -n \
    --arg conn        "$CONN_STRING" \
    --arg cgKey       "$COINGECKO_API_KEY" \
    --arg cgBase      "$COINGECKO_BASE_URL" \
    --arg cgHdr       "$COINGECKO_API_KEY_HEADER" \
    --argjson cgRefresh "$COINGECKO_REFRESH_MINUTES" \
    --arg adminUser   "$ADMIN_USERNAME" \
    --arg adminPass   "$ADMIN_PASSWORD" \
    --arg sponsorUrl  "$SPONSOR_URL" \
    --argjson sponsorTtl "$SPONSOR_CACHE_TTL" \
    --argjson sponsorRot "$SPONSOR_ROTATE_SECONDS" \
    --arg onion       "$ONION_HOST" \
    '{
        ConnectionStrings: { DefaultConnection: $conn },
        CoinGecko: {
            ApiKey: $cgKey,
            BaseUrl: $cgBase,
            ApiKeyHeader: $cgHdr,
            TopCoinsOnStartup: 100,
            RefreshIntervalMinutes: $cgRefresh
        },
        Admin: {
            Username: $adminUser,
            Password: $adminPass
        },
        Sponsors: {
            SourceUrl: $sponsorUrl,
            CacheTtlMinutes: $sponsorTtl,
            RotateIntervalSeconds: $sponsorRot
        }
    }
    + (if $onion != "" then { Tor: { OnionHost: $onion } } else {} end)' \
    > "$WEB_CFG_FILE"

# Worker config (BtcPay section conditional)
if [[ $HAS_BTCPAY -eq 1 ]]; then
    jq -n \
        --arg conn        "$CONN_STRING" \
        --arg cgKey       "$COINGECKO_API_KEY" \
        --arg cgBase      "$COINGECKO_BASE_URL" \
        --arg cgHdr       "$COINGECKO_API_KEY_HEADER" \
        --argjson cgRefresh "$COINGECKO_REFRESH_MINUTES" \
        --arg btcBase     "$BTCPAY_BASE_URL" \
        --arg btcKey      "$BTCPAY_API_KEY" \
        --argjson btcRefresh "$BTCPAY_REFRESH_MINUTES" \
        --argjson btcTimeout "$BTCPAY_TIMEOUT_SECONDS" \
        '{
            ConnectionStrings: { DefaultConnection: $conn },
            CoinGecko: {
                ApiKey: $cgKey,
                BaseUrl: $cgBase,
                ApiKeyHeader: $cgHdr,
                RefreshIntervalMinutes: $cgRefresh
            },
            BtcPay: {
                BaseUrl: $btcBase,
                ApiKey: $btcKey,
                RefreshMinutes: $btcRefresh,
                TimeoutSeconds: $btcTimeout
            }
        }' > "$WORKER_CFG_FILE"
    echo "    ${C_GRAY}BTCPay supply source: $BTCPAY_BASE_URL${C_RESET}"
else
    jq -n \
        --arg conn        "$CONN_STRING" \
        --arg cgKey       "$COINGECKO_API_KEY" \
        --arg cgBase      "$COINGECKO_BASE_URL" \
        --arg cgHdr       "$COINGECKO_API_KEY_HEADER" \
        --argjson cgRefresh "$COINGECKO_REFRESH_MINUTES" \
        '{
            ConnectionStrings: { DefaultConnection: $conn },
            CoinGecko: {
                ApiKey: $cgKey,
                BaseUrl: $cgBase,
                ApiKeyHeader: $cgHdr,
                RefreshIntervalMinutes: $cgRefresh
            }
        }' > "$WORKER_CFG_FILE"
    echo "    ${C_GRAY}BTCPay: not configured (node-verified supply disabled)${C_RESET}"
fi

write_ok "Config ready"

# -- Step 5: Deploy web app ----------------------------------------------------
# Order matters:
#   1. Show maintenance page.
#   2. Stop old service.
#   3. Copy new binaries + config.
#   4. Run migrations against the NEW DLL, while NO service is running.
#      If migrations fail, abort - we never start a binary against wrong schema.
#   5. Start new service, wait_for_app requires HTTP 200.
#   6. (Step 7) Restore real nginx config.
if [[ $WORKER_ONLY -eq 0 ]]; then
    write_step "Deploying web app"

    enable_maintenance_page
    ssh_run_ignore "systemctl stop $APP_NAME 2>/dev/null || true"

    WEB_TAR="/tmp/web.tar.gz.local"
    tar -czf "$WEB_TAR" -C "$WEB_OUT" .

    scp_send "$WEB_TAR" "/tmp/web.tar.gz"
    ssh_run "mkdir -p $DEPLOY_PATH && tar -xzf /tmp/web.tar.gz -C $DEPLOY_PATH && rm /tmp/web.tar.gz"
    scp_send "$WEB_CFG_FILE" "$DEPLOY_PATH/appsettings.json"
    ssh_run "chown -R www-data:www-data $DEPLOY_PATH && chmod -R 755 $DEPLOY_PATH"

    # --- Run EF migrations BEFORE starting the new service ---
    write_step "Running database migrations (pre-start)"

    MIGRATE_SCRIPT_FILE="/tmp/migrate.sh.local"
    cat > "$MIGRATE_SCRIPT_FILE" <<EOF
#!/bin/bash
set -e
export ConnectionStrings__DefaultConnection='Host=localhost;Port=5432;Database=$DB_NAME;Username=$DB_USER;Password=$DB_PASSWORD'
export ASPNETCORE_ENVIRONMENT=Production
cd $DEPLOY_PATH
echo 'Running migrations...'
dotnet MoneroMarketCap.Web.dll --migrate-only
echo 'Migrations complete.'
EOF
    run_remote_script "$MIGRATE_SCRIPT_FILE" "/tmp/migrate.sh"
    write_ok "Migrations applied"

    # --- Write systemd unit and start service ---
    SVC_FILE="/tmp/$APP_NAME.service.local"
    cat > "$SVC_FILE" <<EOF
[Unit]
Description=MoneroMarketCap Web ($DOMAIN)
After=network.target postgresql.service

[Service]
WorkingDirectory=$DEPLOY_PATH
ExecStart=/usr/bin/dotnet $DEPLOY_PATH/MoneroMarketCap.Web.dll
Restart=always
RestartSec=10
User=www-data
Environment=ASPNETCORE_ENVIRONMENT=Production
Environment=ASPNETCORE_URLS=http://0.0.0.0:$APP_PORT
Environment=DOTNET_PRINT_TELEMETRY_MESSAGE=false

[Install]
WantedBy=multi-user.target
EOF
    scp_send "$SVC_FILE" "/etc/systemd/system/$APP_NAME.service"
    ssh_run "systemctl daemon-reload && systemctl enable $APP_NAME && systemctl restart $APP_NAME"

    wait_for_app   # exits the script if app doesn't return HTTP 200

    write_ok "Web app deployed on port $APP_PORT"
fi

# -- Step 6: Deploy worker -----------------------------------------------------
if [[ $WEB_ONLY -eq 0 ]]; then
    write_step "Deploying worker"

    ssh_run_ignore "systemctl stop $WORKER_NAME 2>/dev/null || true"

    WORKER_TAR="/tmp/worker.tar.gz.local"
    tar -czf "$WORKER_TAR" -C "$WORKER_OUT" .

    scp_send "$WORKER_TAR" "/tmp/worker.tar.gz"
    ssh_run "mkdir -p $WORKER_PATH && tar -xzf /tmp/worker.tar.gz -C $WORKER_PATH && rm /tmp/worker.tar.gz"
    scp_send "$WORKER_CFG_FILE" "$WORKER_PATH/appsettings.json"
    ssh_run "chown -R www-data:www-data $WORKER_PATH && chmod -R 755 $WORKER_PATH"

    WORKER_SVC_FILE="/tmp/$WORKER_NAME.service.local"
    cat > "$WORKER_SVC_FILE" <<EOF
[Unit]
Description=MoneroMarketCap Worker ($DOMAIN)
After=network.target postgresql.service

[Service]
WorkingDirectory=$WORKER_PATH
ExecStart=/usr/bin/dotnet $WORKER_PATH/MoneroMarketCap.Worker.dll
Restart=always
RestartSec=10
User=www-data
Environment=ASPNETCORE_ENVIRONMENT=Production
Environment=DOTNET_ENVIRONMENT=Production
Environment=DOTNET_PRINT_TELEMETRY_MESSAGE=false

[Install]
WantedBy=multi-user.target
EOF
    scp_send "$WORKER_SVC_FILE" "/etc/systemd/system/$WORKER_NAME.service"
    ssh_run "systemctl daemon-reload && systemctl enable $WORKER_NAME && systemctl restart $WORKER_NAME"

    write_ok "Worker deployed"
fi

# -- Step 7: Configure Nginx (restore real proxy config, removing maintenance) -
write_step "Configuring Nginx"

CERT_EXISTS=$(ssh_query "test -f /etc/letsencrypt/live/$DOMAIN/fullchain.pem && echo yes || echo no")

if [[ "$CERT_EXISTS" == "yes" ]]; then
    ssh_run "cp -L /etc/letsencrypt/live/$DOMAIN/fullchain.pem /tmp/fullchain.pem && cp -L /etc/letsencrypt/live/$DOMAIN/privkey.pem /tmp/privkey.pem"
    ssh_run "docker exec nginx mkdir -p /etc/letsencrypt/live/$DOMAIN"
    ssh_run "docker cp /tmp/fullchain.pem nginx:/etc/letsencrypt/live/$DOMAIN/fullchain.pem"
    ssh_run "docker cp /tmp/privkey.pem nginx:/etc/letsencrypt/live/$DOMAIN/privkey.pem"

    NGINX_CONF_FILE="/tmp/$APP_NAME.nginx.conf.local"
    write_nginx_proxy_ssl > "$NGINX_CONF_FILE"
    scp_send "$NGINX_CONF_FILE" "/tmp/$APP_NAME.conf"
    ssh_run "docker cp /tmp/$APP_NAME.conf nginx:/etc/nginx/conf.d/$APP_NAME.conf"
    ssh_run "docker exec nginx nginx -s reload"
    write_ok "Nginx configured for $DOMAIN (HTTPS)"
else
    NGINX_CONF_FILE="/tmp/$APP_NAME.nginx.conf.local"
    write_nginx_proxy_plain > "$NGINX_CONF_FILE"
    scp_send "$NGINX_CONF_FILE" "/tmp/$APP_NAME.conf"
    ssh_run "docker cp /tmp/$APP_NAME.conf nginx:/etc/nginx/conf.d/$APP_NAME.conf"
    ssh_run "docker exec nginx nginx -s reload"
    write_ok "Nginx configured for $DOMAIN (HTTP only - run with --ssl to enable HTTPS)"
fi

# -- Step 8: SSL (get cert - only needed once) ---------------------------------
if [[ $DO_SSL -eq 1 ]]; then
    write_step "Getting SSL certificate"

    ssh_run "apt-get install -y certbot"

    SSL_SCRIPT_FILE="/tmp/ssl-setup.sh.local"
    cat > "$SSL_SCRIPT_FILE" <<EOF
#!/bin/bash
set -e
echo 'Stopping docker nginx...'
docker stop nginx
sleep 2
echo 'Getting cert...'
certbot certonly --standalone -d $DOMAIN --non-interactive --agree-tos -m admin@$DOMAIN
echo 'Starting docker nginx...'
docker start nginx
sleep 2
echo 'Done'
EOF
    run_remote_script "$SSL_SCRIPT_FILE" "/tmp/ssl-setup.sh"

    write_ok "Certificate obtained - re-running nginx config with SSL..."

    ssh_run "cp -L /etc/letsencrypt/live/$DOMAIN/fullchain.pem /tmp/fullchain.pem && cp -L /etc/letsencrypt/live/$DOMAIN/privkey.pem /tmp/privkey.pem"
    ssh_run "docker exec nginx mkdir -p /etc/letsencrypt/live/$DOMAIN"
    ssh_run "docker cp /tmp/fullchain.pem nginx:/etc/letsencrypt/live/$DOMAIN/fullchain.pem"
    ssh_run "docker cp /tmp/privkey.pem nginx:/etc/letsencrypt/live/$DOMAIN/privkey.pem"

    NGINX_SSL_FILE="/tmp/$APP_NAME.ssl.nginx.conf.local"
    write_nginx_proxy_ssl > "$NGINX_SSL_FILE"
    scp_send "$NGINX_SSL_FILE" "/tmp/$APP_NAME.conf"
    ssh_run "docker cp /tmp/$APP_NAME.conf nginx:/etc/nginx/conf.d/$APP_NAME.conf"
    ssh_run "docker exec nginx nginx -s reload"

    write_ok "SSL installed for $DOMAIN"
fi

# -- Step 9: Tor hidden service (first time only) -----------------------------
if [[ $DO_TOR -eq 1 ]]; then
    write_step "Setting up Tor hidden service for $DOMAIN"

    TOR_SCRIPT_FILE="/tmp/tor-setup.sh.local"
    cat > "$TOR_SCRIPT_FILE" <<EOF
#!/bin/bash
set -e
apt-get install -y tor
systemctl enable tor
if ! grep -q 'moneromarketcap' /etc/tor/torrc; then
  echo '' >> /etc/tor/torrc
  echo '# MoneroMarketCap hidden service' >> /etc/tor/torrc
  echo 'HiddenServiceDir /var/lib/tor/moneromarketcap/' >> /etc/tor/torrc
  echo 'HiddenServicePort 80 127.0.0.1:$APP_PORT' >> /etc/tor/torrc
fi
systemctl restart tor
sleep 5
echo 'Onion address:'
cat /var/lib/tor/moneromarketcap/hostname
EOF
    run_remote_script "$TOR_SCRIPT_FILE" "/tmp/tor-setup.sh"

    ONION_ADDRESS=$(ssh_query "cat /var/lib/tor/moneromarketcap/hostname 2>/dev/null || echo 'not ready yet'")

    write_ok "Tor hidden service configured"
    echo "   ${C_MAGENTA}Onion: $ONION_ADDRESS${C_RESET}"
fi

# -- Done ----------------------------------------------------------------------
echo
echo "${C_GREEN}============================================${C_RESET}"
echo "${C_GREEN} Deployment complete!${C_RESET}"
echo "${C_GREEN}============================================${C_RESET}"
echo " Site:   https://$DOMAIN"
if [[ $DO_TOR -eq 1 ]]; then
    ONION=$(ssh_query "cat /var/lib/tor/moneromarketcap/hostname 2>/dev/null || echo 'pending'")
    echo " ${C_MAGENTA}Onion:  http://$ONION${C_RESET}"
fi
echo
echo " ${C_GRAY}Useful commands:${C_RESET}"
echo "   ${C_GRAY}Web status:    ssh $SSH_USER@$SSH_HOST systemctl status $APP_NAME${C_RESET}"
echo "   ${C_GRAY}Worker status: ssh $SSH_USER@$SSH_HOST systemctl status $WORKER_NAME${C_RESET}"
echo "   ${C_GRAY}Web logs:      ssh $SSH_USER@$SSH_HOST journalctl -u $APP_NAME -f${C_RESET}"
echo "   ${C_GRAY}Worker logs:   ssh $SSH_USER@$SSH_HOST journalctl -u $WORKER_NAME -f${C_RESET}"
echo
