#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."
if [[ $# != 1 ]]; then echo 'Usage: ./scripts/account.sh USERNAME' >&2; exit 1; fi
read -rsp 'Password (at least 12 characters): ' account_password
printf '\n'
read -rsp 'Repeat password: ' account_repeat
printf '\n'
[[ "$account_password" == "$account_repeat" ]] || { echo 'Passwords differ.' >&2; exit 1; }
printf '%s' "$account_password" | docker compose run --rm --no-deps -T audio account -file /data/accounts.json -password-stdin "$1"
unset account_password account_repeat
