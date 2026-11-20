#!/usr/bin/env bash
# Crea los secretos del despliegue en deploy/secretos/ (ignorado por Git). El directorio es 700; los archivos 644 porque Compose los monta
# con el dueño del anfitrión y el usuario sin privilegios del contenedor debe poder leerlos (la protección es el directorio). Repetible: nunca sobrescribe uno existente
# (cambiar la llave del QR invalidaría todos los QR ya impresos; cambiar claves de la base exige coordinarlo con la base).
set -euo pipefail
cd "$(dirname "$0")/.."
dir="${SECRETOS_DIR:-./secretos}"
mkdir -p "$dir"; chmod 700 "$dir" 2>/dev/null || true
crear() { local n="$1"; shift; if [ -e "$dir/$n" ]; then echo "Se conserva $n"; else "$@" > "$dir/$n"; chmod 644 "$dir/$n" 2>/dev/null || true; echo "Creado $n"; fi; }

crear postgres_password bash -c 'openssl rand -base64 36 | tr -d "\n"'
crear api_password bash -c 'openssl rand -hex 32 | tr -d "\n"'
if [ ! -e "$dir/ConnectionStrings__Manifiestos" ]; then
  printf 'Host=postgres;Port=5432;Database=manifiestos;Username=manifiestos_api;Password=%s' "$(cat "$dir/api_password")" > "$dir/ConnectionStrings__Manifiestos"
  chmod 644 "$dir/ConnectionStrings__Manifiestos" 2>/dev/null || true; echo "Creado ConnectionStrings__Manifiestos"
fi
# Llave de firma del QR: ECDSA P-256, PKCS#8. Respaldarla aparte; sin ella los QR impresos dejan de verificarse.
crear llave_validacion.pem openssl genpkey -algorithm EC -pkeyopt ec_paramgen_curve:P-256
# Token del túnel: se pega cuando exista el túnel de Cloudflare (no se crea aquí).
[ -e "$dir/tunel_token" ] || { : > "$dir/tunel_token"; chmod 644 "$dir/tunel_token" 2>/dev/null || true; echo "Creado tunel_token vacío (pendiente)"; }
