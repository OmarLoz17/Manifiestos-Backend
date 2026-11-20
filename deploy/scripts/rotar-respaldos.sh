#!/usr/bin/env bash
# Conserva los N respaldos diarios más recientes y los semanales/mensuales más antiguos; borra el resto.
# Uso: DESTINO=/var/respaldos/manifiestos DIARIOS=7 SEMANALES=4 MENSUALES=6 scripts/rotar-respaldos.sh [--simular]
# Un respaldo es una carpeta AAAAmmdd-HHMMSS. Solo toca carpetas con ese nombre dentro de DESTINO.
set -euo pipefail
destino="${DESTINO:-./respaldos}"; diarios="${DIARIOS:-7}"; semanales="${SEMANALES:-4}"; mensuales="${MENSUALES:-6}"
simular=0; [ "${1:-}" = "--simular" ] && simular=1
mapfile -t todas < <(ls -1 "$destino" 2>/dev/null | grep -E '^[0-9]{8}-[0-9]{6}$' | sort -r)
declare -A conservar
cont=0; nsem=0; nmes=0; declare -A sem mes
for r in "${todas[@]}"; do
  dia="${r:0:8}"; cont=$((cont + 1))
  [ "$cont" -le "$diarios" ] && conservar[$r]=1
  semana="$(date -u -d "${dia:0:4}-${dia:4:2}-${dia:6:2}" +%G-%V 2>/dev/null || echo "$dia")"
  if [ -z "${sem[$semana]:-}" ] && [ "$nsem" -lt "$semanales" ]; then sem[$semana]=1; nsem=$((nsem + 1)); conservar[$r]=1; fi
  m="${dia:0:6}"
  if [ -z "${mes[$m]:-}" ] && [ "$nmes" -lt "$mensuales" ]; then mes[$m]=1; nmes=$((nmes + 1)); conservar[$r]=1; fi
done
borrados=0
for r in "${todas[@]}"; do
  [ -n "${conservar[$r]:-}" ] && continue
  if [ "$simular" = 1 ]; then echo "borraría $r"; else rm -rf -- "${destino:?}/$r"; echo "borrado $r"; fi
  borrados=$((borrados + 1))
done
echo "Respaldos: ${#todas[@]}; conservados: $(( ${#todas[@]} - borrados )); borrados: $borrados"
