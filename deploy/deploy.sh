#!/usr/bin/env bash
# Deploys RPhotoAlbum on a Docker server (TrueNAS SCALE, etc.).
#
# First deployment (the repo doesn't exist on the server yet):
#   curl -fsSL https://raw.githubusercontent.com/Rothen68/RPhotoAlbum/V3/deploy/deploy.sh -o deploy.sh
#   chmod +x deploy.sh
#   DEPLOY_DIR=/mnt/<pool>/apps/rphotoalbum ./deploy.sh
#
# Later updates: re-run the script already present in the cloned repo
# (it does a git pull before rebuilding):
#   $DEPLOY_DIR/deploy/deploy.sh
#
# Optional environment variables:
#   REPO_URL   (def: https://github.com/Rothen68/RPhotoAlbum.git)
#   BRANCH     (def: V3)
#   DEPLOY_DIR (def: $HOME/apps/rphotoalbum)

set -euo pipefail

REPO_URL="${REPO_URL:-https://github.com/Rothen68/RPhotoAlbum.git}"
BRANCH="${BRANCH:-V3}"
DEPLOY_DIR="${DEPLOY_DIR:-$HOME/apps/rphotoalbum}"

log() { printf '[%s] %s\n' "$(date '+%Y-%m-%d %H:%M:%S')" "$1"; }

if docker compose version >/dev/null 2>&1; then
  compose() { docker compose "$@"; }
elif command -v docker-compose >/dev/null 2>&1; then
  compose() { docker-compose "$@"; }
else
  echo "Error: neither 'docker compose' nor 'docker-compose' is available on this server." >&2
  exit 1
fi

if [ -d "$DEPLOY_DIR/.git" ]; then
  log "Existing repo in $DEPLOY_DIR — updating."
  cd "$DEPLOY_DIR"

  if [ -n "$(git status --porcelain)" ]; then
    echo "Error: $DEPLOY_DIR has uncommitted local changes." >&2
    echo "Resolve them manually (git status) before re-running the deployment." >&2
    exit 1
  fi

  # fetch BEFORE checkout: without this, `git checkout` only knows about branches already seen
  # at initial clone time (e.g. V2) — switching to a branch created since (e.g. V3) fails with
  # "pathspec did not match any file(s) known to git" until a fetch refreshes remote refs.
  git fetch origin
  git checkout "$BRANCH"
  # --ff-only: refuses to silently overwrite local commits that diverged from origin, rather
  # than a destructive reset --hard.
  git pull --ff-only origin "$BRANCH"
else
  log "No repo found — cloning into $DEPLOY_DIR."
  git clone --branch "$BRANCH" "$REPO_URL" "$DEPLOY_DIR"
  cd "$DEPLOY_DIR"
fi

if [ ! -f "$DEPLOY_DIR/.env" ]; then
  log "No .env file found — copying .env.example."
  cp "$DEPLOY_DIR/.env.example" "$DEPLOY_DIR/.env"
  echo
  echo "===> Edit $DEPLOY_DIR/.env with real values (pCloud credentials,"
  echo "===> application admin, APP_BASE_URL, TLS_SAN_IP...) then re-run this script."
  exit 1
fi

if [ ! -f "$DEPLOY_DIR/reverse-proxy/certs/server.crt" ]; then
  log "Generating the self-signed TLS certificate (required for offline viewing, #29)."
  bash "$DEPLOY_DIR/reverse-proxy/generate-cert.sh"
fi

log "Building Docker images (backend, frontend)."
compose build

log "Starting containers."
compose up -d

# reverse-proxy uses a stock nginx image (never rebuilt) with nginx.conf mounted as a volume —
# `compose up -d` only recreates a service if its IMAGE or its declaration in docker-compose.yml
# changes, never if only the CONTENT of a mounted file changed. Without this explicit restart, a
# change to nginx.conf silently has no effect after deployment (observed in real usage:
# proxy_read_timeout updated in the repo but still not applied after several consecutive
# deployments).
log "Restarting reverse-proxy to pick up any nginx.conf change."
compose restart reverse-proxy

log "Container status:"
compose ps

log "Deployment complete."
