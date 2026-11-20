#!/usr/bin/env bash
# Lo que ejecuta el temporizador: respaldar, verificar, rotar y, si algo falla, avisar.
# Configuración en /etc/manifiestos/respaldo.env (DESTINO, RESPALDO_CLAVE_ARCHIVO, PROYECTO, AVISO_URL opcional, COPIA_EXTERNA opcional).
# AVISO_URL: webhook (p. ej. un canal de Google Chat de Workspace) que recibe un texto JSON {"text": "..."} si algo falla.
# COPIA_EXTERNA: destino rsync/rclone fuera del VPS (p. ej. "rclone:manifiestos-respaldos"); sin él el respaldo vive solo en el VPS y se avisa.
set -uo pipefail
cd "$(dirname "$0")/.."
avisar() { echo "$1" >&2; [ -n "${AVISO_URL:-}" ] && curl -fsS -m 15 -H 'Content-Type: application/json' -d "{\"text\": \"Manifiestos respaldo: $1\"}" "$AVISO_URL" >/dev/null || true; }
trap 'avisar "FALLÓ el respaldo diario (línea $LINENO). Revisar journalctl -u manifiestos-respaldo."' ERR
set -e
export DESTINO="${DESTINO:-/var/respaldos/manifiestos}"
salida="$(bash scripts/respaldar.sh)"; carpeta="$(echo "$salida" | sed -n 's/^Respaldo listo en: //p')"
bash scripts/verificar-respaldo.sh "$carpeta"
bash scripts/rotar-respaldos.sh
if [ -n "${COPIA_EXTERNA:-}" ]; then
  case "$COPIA_EXTERNA" in rclone:*) rclone copy "$carpeta" "${COPIA_EXTERNA#rclone:}/$(basename "$carpeta")" ;; *) rsync -a "$carpeta" "$COPIA_EXTERNA/" ;; esac
else avisar "respaldo $(basename "$carpeta") listo SOLO en el VPS: configura COPIA_EXTERNA."; fi
echo "Respaldo diario correcto: $carpeta"
