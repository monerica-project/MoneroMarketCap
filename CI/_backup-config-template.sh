#!/usr/bin/env bash
# backup-config.sh - sourced by backup-postgres.sh

# SSH
SSH_USER="root"
SSH_HOST="your.vps.host"
# Leave empty to use SSH key auth (recommended).
SSH_PASSWORD=""

# Database (the credentials the prod app uses)
DB_NAME="moneromarketcap"
DB_USER="moneromarketcap"
DB_PASSWORD="prod-db-password"

# Where dumps land on your local machine
LOCAL_BACKUP_DIR="$HOME/backups/moneromarketcap"
