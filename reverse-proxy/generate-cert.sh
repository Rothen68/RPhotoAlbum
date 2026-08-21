#!/usr/bin/env bash
# Génère un certificat TLS auto-signé pour l'accès HTTPS sur le réseau local — nécessaire à la
# consultation hors-ligne (issue #29) : l'API Cache Storage du navigateur n'existe que dans un
# contexte sécurisé (HTTPS, ou http://localhost), jamais sur une adresse LAN en HTTP simple.
# Auto-signé, pas de nom de domaine : le navigateur affiche un avertissement de confiance la
# première fois (normal, pas une erreur) ; installer server.crt comme certificat de confiance sur
# chaque appareil pour ne plus le revoir.
#
# Idempotent : ne régénère rien si reverse-proxy/certs/ contient déjà un certificat — le supprimer
# pour forcer une régénération. À relancer (puis re-déployer et ré-approuver le nouveau certificat
# sur chaque appareil) si TLS_SAN_IP change, ex. IP LAN non réservée en DHCP.
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
CERTS_DIR="$SCRIPT_DIR/certs"
ENV_FILE="$SCRIPT_DIR/../.env"

if [ -f "$ENV_FILE" ]; then
  set -a
  # shellcheck disable=SC1090
  source "$ENV_FILE"
  set +a
fi

SAN_IP="${TLS_SAN_IP:-127.0.0.1}"

if [ -f "$CERTS_DIR/server.crt" ] && [ -f "$CERTS_DIR/server.key" ]; then
  echo "Certificat déjà présent dans $CERTS_DIR — rien à faire (le supprimer pour régénérer)."
  exit 0
fi

mkdir -p "$CERTS_DIR"

openssl req -x509 -nodes -newkey rsa:2048 \
  -keyout "$CERTS_DIR/server.key" \
  -out "$CERTS_DIR/server.crt" \
  -days 3650 \
  -subj "/CN=$SAN_IP" \
  -addext "subjectAltName=DNS:localhost,IP:127.0.0.1,IP:$SAN_IP"

chmod 600 "$CERTS_DIR/server.key"

echo "Certificat auto-signé généré dans $CERTS_DIR (valide 10 ans, SAN: localhost, 127.0.0.1, $SAN_IP)."
