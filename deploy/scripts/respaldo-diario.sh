#!/usr/bin/env bash
# Lo que ejecuta el temporizador: respaldar, verificar, copiar fuera del VPS, rotar y, si algo falla, avisar.
# Configuración en /etc/manifiestos/respaldo.env:
#   DESTINO, RESPALDO_CLAVE_ARCHIVO, PROYECTO, ARCHIVOS_MODO (completo|pendientes), AVISO_URL (opcional)
#   COPIA_EXTERNA: destino fuera del VPS.
#     rclone:<remoto>:<carpeta>   p. ej. rclone:drive-respaldos:manifiestos-respaldos  (Drive). El respaldo se CIFRA entero (AES-256, la frase de
#                                 RESPALDO_CLAVE_ARCHIVO) antes de salir, se sube como un solo archivo .tar.enc, se verifica su MD5 en el destino y
#                                 solo entonces se borra del VPS lo que sobra (se conservan LOCALES respaldos locales, 1 por omisión).
#     <ruta>                      carpeta (disco montado) con copia simple y verificación de huellas.
#   RETENCION_REMOTA_DIAS (35), LOCALES (1): cuánto se guarda en el destino y en el VPS una vez copiado.
#   Sin COPIA_EXTERNA el respaldo vive solo en el VPS: se rota 7 diarios/4 semanales/6 mensuales y se avisa.
# AVISO_CORREO + SMTP_*: además del webhook, correo con el aviso (ver scripts/avisar-correo.sh).
# AVISO_URL: webhook (p. ej. un espacio de Google Chat) que recibe {"text": "..."} si algo falla.
set -uo pipefail
AQUI="$(cd "$(dirname "$0")" && pwd)"; cd "$AQUI/.."
avisar() {
  echo "$1" >&2
  [ -n "${AVISO_URL:-}" ] && { curl -fsS -m 15 -H 'Content-Type: application/json' -d "{\"text\": \"Manifiestos respaldo: $1\"}" "$AVISO_URL" >/dev/null || true; }
  bash "$AQUI/avisar-correo.sh" "Respaldo: $1" "$1" || true
}
trap 'avisar "FALLO el respaldo diario (linea $LINENO). Revisar journalctl -u manifiestos-respaldo."' ERR
set -e
# El paquete cifrado temporal nunca se queda en el VPS, falle lo que falle.
trap '[ -z "${paquete:-}" ] || rm -f "$paquete"' EXIT
export DESTINO="${DESTINO:-/var/respaldos/manifiestos}"
salida="$(bash scripts/respaldar.sh)"; carpeta="$(echo "$salida" | sed -n 's/^Respaldo listo en: //p')"
nombre="$(basename "$carpeta")"
bash scripts/verificar-respaldo.sh "$carpeta"

if [ -z "${COPIA_EXTERNA:-}" ]; then
  bash scripts/rotar-respaldos.sh
  avisar "respaldo $nombre listo SOLO en el VPS: configure COPIA_EXTERNA."
else
  case "$COPIA_EXTERNA" in
    rclone:*)
      remoto="${COPIA_EXTERNA#rclone:}"
      paquete="$DESTINO/$nombre.tar.enc"
      # Un solo archivo cifrado: el volcado de la base y las copias no se suben en claro a una unidad compartida.
      tar cf - -C "$DESTINO" "$nombre" | RESPALDO_FRASE="$(cat "$RESPALDO_CLAVE_ARCHIVO")" openssl enc -aes-256-cbc -pbkdf2 -salt -pass env:RESPALDO_FRASE > "$paquete"
      rclone copyto "$paquete" "$remoto/$nombre.tar.enc"
      local_md5="$(md5sum "$paquete" | cut -d' ' -f1)"; remoto_md5="$(rclone md5sum "$remoto/$nombre.tar.enc" | cut -d' ' -f1)"
      [ -n "$remoto_md5" ] && [ "$local_md5" = "$remoto_md5" ] || { rclone deletefile "$remoto/$nombre.tar.enc" || true; echo "La copia externa no coincide (MD5): se retira del destino y no se borra nada local." >&2; false; }
      rm -f "$paquete"
      rclone delete "$remoto" --min-age "${RETENCION_REMOTA_DIAS:-35}d" --include '*.tar.enc' || avisar "no se pudo rotar la copia externa (se reintenta mañana)."
      ;;
    *)
      if command -v rsync >/dev/null 2>&1; then rsync -a "$carpeta" "$COPIA_EXTERNA/"; else cp -a "$carpeta" "$COPIA_EXTERNA/"; fi
      # La copia debe coincidir con el original: se comprueban las huellas ya en el destino.
      ( cd "$COPIA_EXTERNA/$nombre" && sha256sum -c SHA256SUMS >/dev/null )
      ;;
  esac
  # Copiado y verificado fuera del VPS: en el VPS solo se conservan los últimos LOCALES respaldos.
  DIARIOS="${LOCALES:-1}" SEMANALES=0 MENSUALES=0 bash scripts/rotar-respaldos.sh
fi
echo "Respaldo diario correcto: $carpeta"
