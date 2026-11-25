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

# Lo que queda sin instalar (no es de seguridad: Docker, versiones menores…) se avisa para instalarlo a mano. El correo sale cuando la
# lista cambia y, si sigue igual, se recuerda cada RECORDAR_DIAS (7): no uno cada noche.
avisar_pendientes() {
  local estado="${ESTADO_DIR:-/var/lib/manifiestos}" dias="${RECORDAR_DIAS:-7}" pendientes huella marca
  pendientes="$(apt-get -s dist-upgrade 2>/dev/null | awk '$1 == "Inst" { print $2 }' | sort)" || return 0
  marca="$estado/actualizaciones-avisadas"
  if [ -z "$pendientes" ]; then rm -f "$marca"; echo "Sin actualizaciones pendientes."; return 0; fi
  huella="$(printf '%s' "$pendientes" | sha256sum | cut -d' ' -f1)"
  if [ -f "$marca" ] && [ "$(cat "$marca")" = "$huella" ] && [ -z "$(find "$marca" -mtime "+$((dias - 1))")" ]; then
    echo "Actualizaciones pendientes ya avisadas."; return 0
  fi
  local total; total="$(printf '%s\n' "$pendientes" | wc -l | tr -d ' ')"
  echo "Actualizaciones pendientes: $total"
  bash "$AQUI/avisar-correo.sh" "$total actualizaciones pendientes en el servidor" "No se instalan solas (las de seguridad ya se aplicaron):
$pendientes

Para instalarlas con el sistema sin uso: sudo apt-get update && sudo apt-get dist-upgrade" || true
  mkdir -p "$estado" && printf '%s' "$huella" > "$marca"
}
avisar_pendientes

if [ -n "${SIN_REINICIO:-}" ];then echo "SIN_REINICIO: no se reinicia."; exit 0; fi
echo "Actualizaciones al día: se reinicia el servidor."
systemctl reboot
