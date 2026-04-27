#!/usr/bin/env bash
# deploy-config.sh - sourced by deploy.sh
# Copy this to deploy-config.sh, fill in real values, do NOT commit.

# -- SSH ------------------------------------------------------------------------
SSH_USER="root"
SSH_HOST="your.vps.host"
# Leave SSH_PASSWORD unset/empty to use SSH key auth (recommended).
# If set, sshpass must be installed locally: sudo apt install -y sshpass
SSH_PASSWORD=""

# -- App ------------------------------------------------------------------------
DOMAIN="moneromarketcap.com"
APP_NAME="moneromarketcap"
WORKER_NAME="moneromarketcap-worker"
APP_PORT=5050
DEPLOY_PATH="/var/www/moneromarketcap"
WORKER_PATH="/var/www/moneromarketcap-worker"

# -- Build ----------------------------------------------------------------------
# Paths are relative to the CI/ folder (where deploy.sh lives).
WEB_PROJECT="../src/MoneroMarketCap.Web/MoneroMarketCap.Web.csproj"
WORKER_PROJECT="../src/MoneroMarketCap.Worker/MoneroMarketCap.Worker.csproj"

# -- Database -------------------------------------------------------------------
DB_NAME="moneromarketcap"
DB_USER="moneromarketcap"
DB_PASSWORD="change-me"

# -- CoinGecko ------------------------------------------------------------------
COINGECKO_API_KEY="CG-xxxxxxxxxxxxxxxxxxxxxxxx"
COINGECKO_BASE_URL="https://api.coingecko.com/api/v3/"
COINGECKO_API_KEY_HEADER="x-cg-demo-api-key"
COINGECKO_REFRESH_MINUTES=15

# -- Admin ----------------------------------------------------------------------
ADMIN_USERNAME="admin"
ADMIN_PASSWORD="change-me"

# -- Sponsors -------------------------------------------------------------------
SPONSOR_URL="https://example.com/sponsors.json"
SPONSOR_CACHE_TTL=10
SPONSOR_ROTATE_SECONDS=30

# -- BTCPay (optional) ----------------------------------------------------------
# Leave both empty/unset to disable node-verified XMR supply (falls back to CoinGecko).
BTCPAY_BASE_URL=""
BTCPAY_API_KEY=""
BTCPAY_REFRESH_MINUTES=15
BTCPAY_TIMEOUT_SECONDS=30
