#!/usr/bin/env bash
# Déploiement de RPhotoAlbum sur un serveur Docker (TrueNAS SCALE, etc.).
#
# Premier déploiement (le dépôt n'existe pas encore sur le serveur) :
#   curl -fsSL https://raw.githubusercontent.com/Rothen68/RPhotoAlbum/V3/deploy/deploy.sh -o deploy.sh
#   chmod +x deploy.sh
#   DEPLOY_DIR=/mnt/<pool>/apps/rphotoalbum ./deploy.sh
#
# Mises à jour suivantes : relancer le script déjà présent dans le dépôt cloné
# (il fait un git pull avant de reconstruire) :
#   $DEPLOY_DIR/deploy/deploy.sh
#
# Variables d'environnement optionnelles :
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
  echo "Erreur : ni 'docker compose' ni 'docker-compose' n'est disponible sur ce serveur." >&2
  exit 1
fi

if [ -d "$DEPLOY_DIR/.git" ]; then
  log "Dépôt existant dans $DEPLOY_DIR — mise à jour."
  cd "$DEPLOY_DIR"

  if [ -n "$(git status --porcelain)" ]; then
    echo "Erreur : $DEPLOY_DIR contient des modifications locales non commitées." >&2
    echo "Résous-les manuellement (git status) avant de relancer le déploiement." >&2
    exit 1
  fi

  # fetch AVANT checkout : sans ça, `git checkout` ne connaît que les branches déjà vues lors
  # du clonage initial (ex. V2) — basculer vers une branche créée depuis (ex. V3) échoue avec
  # "pathspec did not match any file(s) known to git" tant qu'aucun fetch n'a rafraîchi les
  # références distantes.
  git fetch origin
  git checkout "$BRANCH"
  # --ff-only : refuse d'écraser silencieusement des commits locaux qui auraient
  # divergé de l'origine, plutôt qu'un reset --hard destructeur.
  git pull --ff-only origin "$BRANCH"
else
  log "Aucun dépôt trouvé — clonage dans $DEPLOY_DIR."
  git clone --branch "$BRANCH" "$REPO_URL" "$DEPLOY_DIR"
  cd "$DEPLOY_DIR"
fi

if [ ! -f "$DEPLOY_DIR/.env" ]; then
  log "Aucun fichier .env trouvé — copie de .env.example."
  cp "$DEPLOY_DIR/.env.example" "$DEPLOY_DIR/.env"
  echo
  echo "===> Édite $DEPLOY_DIR/.env avec les vraies valeurs (identifiants pCloud,"
  echo "===> admin applicatif, APP_BASE_URL, TLS_SAN_IP...) puis relance ce script."
  exit 1
fi

if [ ! -f "$DEPLOY_DIR/reverse-proxy/certs/server.crt" ]; then
  log "Génération du certificat TLS auto-signé (HTTPS requis par la consultation hors-ligne, #29)."
  bash "$DEPLOY_DIR/reverse-proxy/generate-cert.sh"
fi

log "Construction des images Docker (backend, frontend)."
compose build

log "Démarrage des conteneurs."
compose up -d

# reverse-proxy utilise une image nginx stock (jamais reconstruite) avec nginx.conf monté en
# volume — `compose up -d` ne recrée un service que si son IMAGE ou sa déclaration dans
# docker-compose.yml change, jamais si seul le CONTENU d'un fichier monté a changé. Sans ce
# redémarrage explicite, une modification de nginx.conf reste silencieusement sans effet après
# déploiement (constaté en conditions réelles : proxy_read_timeout mis à jour dans le dépôt mais
# toujours pas appliqué après plusieurs déploiements successifs).
log "Redémarrage du reverse-proxy pour prendre en compte un éventuel changement de nginx.conf."
compose restart reverse-proxy

log "État des conteneurs :"
compose ps

log "Déploiement terminé."
