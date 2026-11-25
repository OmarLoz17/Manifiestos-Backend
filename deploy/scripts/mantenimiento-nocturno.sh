#!/usr/bin/env bash
# Mantenimiento nocturno del VPS (systemd/manifiestos-mantenimiento.timer, 01:30 hora de México):
# instala SOLO las actualizaciones automáticas del sistema (unattended-upgrades: seguridad de la distribución; no toca Docker ni las
# imágenes de la aplicación) y reinicia el servidor. Si la actualización falla NO reinicia (un reinicio a medias puede dejarlo sin arrancar)
# y avisa por correo. La configuración del correo es la de scripts/avisar-correo.sh.
set -uo pipefail
AQUI="$(cd "$(dirname "$0")" && pwd)"
avisar() { echo "$1" >&2; bash "$AQUI/avisar-correo.sh" "Mantenimiento: $1" "$2" || true; }
registro="$(mktemp)"; trap 'rm -f "$registro"' EXIT
export DEBIAN_FRONTEND=noninteractive

fallar() {
  avisar "$1; NO se reinició el servidor" "$1. No se reinició. Últimas líneas:
$(tail -n 20 "$registro")
Revisar: journalctl -u manifiestos-mantenimiento"
  exit 1
}

# El respaldo (03:30) no debe coincidir; si por algún motivo sigue en marcha, no se reinicia encima de él.
if systemctl is-active --quiet manifiestos-respaldo.service; then
  : > "$registro"; fallar "hay un respaldo en curso"
fi

apt-get update >> "$registro" 2>&1 || fallar "no se pudo consultar las actualizaciones"
unattended-upgrade -v >> "$registro" 2>&1 || fallar "falló la instalación de actualizaciones"
cat "$registro"

if [ -n "${SIN_REINICIO:-}" ]; then echo "SIN_REINICIO: no se reinicia."; exit 0; fi
echo "Actualizaciones al día: se reinicia el servidor."
systemctl reboot
