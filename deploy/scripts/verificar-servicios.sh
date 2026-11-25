#!/usr/bin/env bash
# Comprueba tras cada arranque (systemd/manifiestos-verificar.service) que la aplicación volvió: contenedores en marcha y sanos,
# respuesta pública (si URL_PUBLICA está definida), unidades de systemd fallidas y espacio en disco. Si algo está mal, avisa por correo.
# Variables opcionales: PROYECTO (manifiestos), SERVICIOS ("postgres api web cloudflared"), URL_PUBLICA, ESPERA_SEGUNDOS (600), DISCO_MAXIMO (85).
set -uo pipefail
AQUI="$(cd "$(dirname "$0")" && pwd)"
proyecto="${PROYECTO:-manifiestos}"
read -ra servicios <<< "${SERVICIOS:-postgres api web cloudflared}"
espera="${ESPERA_SEGUNDOS:-600}"
pausa="${PAUSA_SEGUNDOS:-15}"

revisar() {
  local s estado
  fallos=()
  for s in "${servicios[@]}"; do
    estado="$(docker ps -a --filter "label=com.docker.compose.project=$proyecto" --filter "label=com.docker.compose.service=$s" --format '{{.State}} {{.Status}}' | head -n 1)"
    case "$estado" in
      "") fallos+=("$s: no existe el contenedor") ;;
      running*"(unhealthy)"*|running*"(health: starting)"*) fallos+=("$s: sin responder ($estado)") ;;
      running*) ;;
      *) fallos+=("$s: detenido ($estado)") ;;
    esac
  done
  if [ -n "${URL_PUBLICA:-}" ] && ! curl -fsS -m 15 -o /dev/null "$URL_PUBLICA"; then fallos+=("la dirección pública no responde: $URL_PUBLICA"); fi
}

# Al arrancar los servicios tardan un poco: se insiste hasta el plazo antes de dar la alarma.
limite=$(( $(date +%s) + espera ))
while true; do
  revisar
  [ ${#fallos[@]} -eq 0 ] && break
  [ "$(date +%s)" -ge "$limite" ] && break
  sleep "$pausa"
done

# Unidades que no afectan a la aplicación y no deben dar alarma. cloud-init-hotplugd es de la preparación de la máquina virtual
# (detecta hardware conectado en caliente) y queda en error en el VPS sin efecto en los servicios.
ignoradas="manifiestos-verificar.service ${UNIDADES_IGNORADAS:-cloud-init-hotplugd.service}"
unidades="$(systemctl --failed --no-legend --plain 2>/dev/null | awk '{print $1}' | grep -vxF -f <(tr ' ' '\n' <<< "$ignoradas" | grep .) | tr '\n' ' ')"
[ -n "${unidades// /}" ] && fallos+=("unidades del sistema con error: $unidades")
uso="$(df --output=pcent / 2>/dev/null | tail -n 1 | tr -dc '0-9')"
[ -n "$uso" ] && [ "$uso" -ge "${DISCO_MAXIMO:-85}" ] && fallos+=("el disco está al $uso %")

if [ ${#fallos[@]} -eq 0 ]; then echo "Servicios correctos."; exit 0; fi
texto="$(printf '%s\n' "${fallos[@]}")"
echo "$texto" >&2
bash "$AQUI/avisar-correo.sh" "el servidor arrancó con fallas" "$texto" || true
exit 1
