#!/usr/bin/env bash
# Generates a self-signed TLS certificate for HTTPS access on the local network — required for
# offline viewing (issue #29): the browser's Cache Storage API only exists in a secure context
# (HTTPS, or http://localhost), never on a plain-HTTP LAN address. Self-signed, no domain name:
# the browser shows a trust warning the first time (expected, not an error); install server.crt
# as a trusted certificate on each device to stop seeing it.
#
# Idempotent: regenerates nothing if reverse-proxy/certs/ already has a certificate — delete it
# to force regeneration. Re-run this (then redeploy and re-approve the new certificate on each
# device) if TLS_SAN_IP changes, e.g. an unreserved DHCP LAN IP.
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
  echo "Certificate already present in $CERTS_DIR — nothing to do (delete it to regenerate)."
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

echo "Self-signed certificate generated in $CERTS_DIR (valid 10 years, SAN: localhost, 127.0.0.1, $SAN_IP)."
