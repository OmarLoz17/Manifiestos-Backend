#!/usr/bin/env bash
# Certificado autofirmado SOLO para el ensayo local (7 días). Producción usa el certificado de Cloudflare en el borde.
set -euo pipefail
cd "$(dirname "$0")"; mkdir -p tls
[ -e tls/cert.pem ] && { echo 'Se conserva el certificado de ensayo.'; exit 0; }
MSYS_NO_PATHCONV=1 openssl req -x509 -newkey rsa:2048 -nodes -days 7 -subj "/CN=localhost" -addext "subjectAltName=DNS:localhost,IP:127.0.0.1" -keyout tls/key.pem -out tls/cert.pem 2>/dev/null
chmod 644 tls/key.pem tls/cert.pem
echo 'Certificado de ensayo creado en deploy/ensayo/tls/.'
