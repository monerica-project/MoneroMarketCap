#!/usr/bin/env bash
#
# Deploys MoneroMarketCap to its ACTUAL production host, monerica-vps, where
# moneromarketcap.com is served (migrated there 2026-07-29):
#   Web:    systemd `moneromarketcap`,        /var/www/moneromarketcap,        Kestrel 127.0.0.1:5120, www-data
#   Worker: systemd `moneromarketcap-worker`, /var/www/moneromarketcap-worker,                          www-data
#   host nginx vhost /etc/nginx/sites-available/moneromarketcap.com.conf, Tor onion, PostgreSQL db `moneromarketcap`.
#
# NOTE: the legacy CI/deploy.sh + CI/deploy-config.sh point at the OLD, now-decommissioned
# Docker-nginx box. Deploying there is a SILENT NO-OP for the live
# site and that script also GENERATES appsettings.json from config values. Use THIS
# script instead — it targets monerica-vps and EXCLUDES appsettings*.json so it can never
# overwrite the server's production connection string / API keys / admin creds.
#
# Flags:  --skip-build   --web-only   --worker-only
#
set -euo pipefail

SKIP_BUILD=0; WEB_ONLY=0; WORKER_ONLY=0
while [[ $# -gt 0 ]]; do
    case "$1" in
        --skip-build)  SKIP_BUILD=1; shift ;;
        --web-only)    WEB_ONLY=1; shift ;;
        --worker-only) WORKER_ONLY=1; shift ;;
        -h|--help)     sed -n '2,18p' "$0"; exit 0 ;;
        *) echo "Unknown flag: $1" >&2; exit 1 ;;
    esac
done

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
WEB_PROJECT="$SCRIPT_DIR/../src/MoneroMarketCap.Web/MoneroMarketCap.Web.csproj"
WORKER_PROJECT="$SCRIPT_DIR/../src/MoneroMarketCap.Worker/MoneroMarketCap.Worker.csproj"
WEB_OUT="$SCRIPT_DIR/../publish/web"
WORKER_OUT="$SCRIPT_DIR/../publish/worker"

SSH_TARGET="monerica-vps"          # ~/.ssh/config alias (host + SSH port 56777 + user)
SSH_PORT=56777
WEB_PATH="/var/www/moneromarketcap"
WORKER_PATH="/var/www/moneromarketcap-worker"
WEB_SERVICE="moneromarketcap"
WORKER_SERVICE="moneromarketcap-worker"
WEB_PORT=5120
DOMAIN="moneromarketcap.com"

# rsync excludes: never touch server config, data-protection keys, or uploaded content.
EXCLUDES=(--exclude 'appsettings.json' --exclude 'appsettings.Production.json' \
          --exclude 'appsettings.Development.json' --exclude 'DataProtection-Keys/' \
          --exclude 'keys/' --exclude 'wwwroot/uploads/')

sync_dir() {  # $1=local out  $2=remote path
    rsync -rlptDz --rsync-path="sudo rsync" -e "ssh -p $SSH_PORT" "${EXCLUDES[@]}" "$1"/ "$SSH_TARGET":"$2"/
}

if [[ $WORKER_ONLY -eq 0 ]]; then
    if [[ $SKIP_BUILD -eq 0 ]]; then
        echo "==> Publishing web (linux-x64, framework-dependent)"
        rm -rf "$WEB_OUT"
        dotnet publish "$WEB_PROJECT" -c Release -r linux-x64 --self-contained false \
            -o "$WEB_OUT" /p:ErrorOnDuplicatePublishOutputFiles=false
    fi
    echo "==> Syncing web → $SSH_TARGET:$WEB_PATH (server config preserved)"
    sync_dir "$WEB_OUT" "$WEB_PATH"

    echo "==> Running EF migrations against the deployed DLL (reads server appsettings)"
    ssh -p "$SSH_PORT" "$SSH_TARGET" \
        "sudo bash -c 'cd $WEB_PATH && ASPNETCORE_ENVIRONMENT=Production dotnet MoneroMarketCap.Web.dll --migrate-only'"

    ssh -p "$SSH_PORT" "$SSH_TARGET" "sudo chown -R www-data:www-data $WEB_PATH && sudo systemctl restart $WEB_SERVICE"
fi

if [[ $WEB_ONLY -eq 0 ]]; then
    if [[ $SKIP_BUILD -eq 0 ]]; then
        echo "==> Publishing worker (linux-x64, framework-dependent)"
        rm -rf "$WORKER_OUT"
        dotnet publish "$WORKER_PROJECT" -c Release -r linux-x64 --self-contained false \
            -o "$WORKER_OUT" /p:ErrorOnDuplicatePublishOutputFiles=false
    fi
    echo "==> Syncing worker → $SSH_TARGET:$WORKER_PATH (server config preserved)"
    sync_dir "$WORKER_OUT" "$WORKER_PATH"
    ssh -p "$SSH_PORT" "$SSH_TARGET" "sudo chown -R www-data:www-data $WORKER_PATH && sudo systemctl restart $WORKER_SERVICE"
fi

echo "==> Health check"
sleep 4
if [[ $WORKER_ONLY -eq 0 ]]; then
    app=$(ssh -p "$SSH_PORT" "$SSH_TARGET" "curl -sL -o /dev/null -w '%{http_code}' http://127.0.0.1:$WEB_PORT/ 2>/dev/null || echo 000")
    pub=$(curl -sL -o /dev/null -w "%{http_code}" "https://$DOMAIN/" || echo 000)
    web_state=$(ssh -p "$SSH_PORT" "$SSH_TARGET" "systemctl is-active $WEB_SERVICE" || true)
    echo "    web service: $web_state   app :$WEB_PORT → HTTP $app   public https://$DOMAIN → HTTP $pub"
fi
if [[ $WEB_ONLY -eq 0 ]]; then
    worker_state=$(ssh -p "$SSH_PORT" "$SSH_TARGET" "systemctl is-active $WORKER_SERVICE" || true)
    echo "    worker service: $worker_state"
fi

if [[ $WORKER_ONLY -eq 0 ]]; then
    [ "$web_state" = "active" ] && [ "$app" = "200" ] && [ "$pub" = "200" ] \
        && echo "==> Done." \
        || { echo "!! Check the service (journalctl -u $WEB_SERVICE)"; exit 1; }
else
    echo "==> Done."
fi
