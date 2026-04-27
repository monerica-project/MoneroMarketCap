#!/usr/bin/env bash
# backup-postgres.sh
# Dumps the production Postgres DB on the VPS and downloads it locally.

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
CONFIG_PATH="$SCRIPT_DIR/backup-config.sh"

if [[ ! -f "$CONFIG_PATH" ]]; then
    echo "backup-config.sh not found next to backup-postgres.sh" >&2
    exit 1
fi
# shellcheck disable=SC1090
source "$CONFIG_PATH"

# Tool checks
for tool in ssh scp; do
    if ! command -v "$tool" >/dev/null 2>&1; then
        echo "Required tool missing: $tool" >&2
        exit 1
    fi
done

# SSH prefix (key auth by default; sshpass fallback if SSH_PASSWORD set)
SSH_PREFIX=()
if [[ -n "${SSH_PASSWORD:-}" ]]; then
    if ! command -v sshpass >/dev/null 2>&1; then
        echo "SSH_PASSWORD is set but sshpass is not installed." >&2
        echo "Install: sudo apt install -y sshpass   (or remove SSH_PASSWORD to use SSH key auth)" >&2
        exit 1
    fi
    SSH_PREFIX=(sshpass -p "$SSH_PASSWORD")
fi

SSH_OPTS=(-o StrictHostKeyChecking=accept-new -o ConnectTimeout=10 -o ServerAliveInterval=30)

DATE=$(date +"%Y-%m-%d_%H-%M")
REMOTE_FILE="/tmp/backup_${DATE}.dump"
LOCAL_FILE="$LOCAL_BACKUP_DIR/backup_${DATE}.dump"

mkdir -p "$LOCAL_BACKUP_DIR"

echo "Dumping database on server..."
"${SSH_PREFIX[@]}" ssh "${SSH_OPTS[@]}" "$SSH_USER@$SSH_HOST" \
    "PGPASSWORD='$DB_PASSWORD' pg_dump -h 127.0.0.1 -U $DB_USER -d $DB_NAME -F c -f $REMOTE_FILE"

echo "Downloading backup to $LOCAL_FILE ..."
"${SSH_PREFIX[@]}" scp "${SSH_OPTS[@]}" "$SSH_USER@$SSH_HOST:$REMOTE_FILE" "$LOCAL_FILE"

echo "Cleaning up remote file..."
"${SSH_PREFIX[@]}" ssh "${SSH_OPTS[@]}" "$SSH_USER@$SSH_HOST" "rm -f $REMOTE_FILE"

SIZE=$(du -h "$LOCAL_FILE" | cut -f1)
echo -e "\e[32mDone! Backup saved to: $LOCAL_FILE ($SIZE)\e[0m"
