#!/usr/bin/env bash
# Respaldo completo: base (pg_dump) + roles del clúster + volumen de archivos + claves de sesión + secretos (cifrados).
# Uso (desde backend/deploy): RESPALDO_CLAVE_ARCHIVO=<archivo con la frase> [PROYECTO=manifiestos] [DESTINO=./respaldos] scripts/respaldar.sh
# Sale con el nombre de la carpeta creada. El respaldo contiene datos de producción: guardarlo fuera del VPS y cifrado en reposo.
# La API puede seguir en marcha; para un corte exacto entre base y archivos, detenerla un momento (docker compose stop api web).
set -euo pipefail
cd "$(dirname "$0")/.."
export MSYS_NO_PATHCONV=1
proyecto="${PROYECTO:-manifiestos}"
clave="${RESPALDO_CLAVE_ARCHIVO:?Indica RESPALDO_CLAVE_ARCHIVO (archivo con la frase de cifrado de los secretos)}"
secretos="${SECRETOS_DIR:-./secretos}"
destino="${DESTINO:-./respaldos}/$(date -u +%Y%m%d-%H%M%S)"
imagen="${IMAGEN_UTILIDADES:-postgres:18.6-bookworm}"
compose=(docker compose -p "$proyecto" -f compose.yaml)
[ -f compose.ensayo.yaml ] && [ -n "${ENSAYO:-}" ] && compose+=(-f compose.ensayo.yaml)
mkdir -p "$destino"; destino_abs="$(cd "$destino" && pwd)"
export SECRETOS_DIR="$secretos"

echo "== Base de datos"
"${compose[@]}" exec -T postgres pg_dump -U manifiestos_owner -d manifiestos -Fc > "$destino/base.dump"
"${compose[@]}" exec -T postgres pg_dumpall -U manifiestos_owner --globals-only > "$destino/roles.sql"
echo "== Volúmenes"
for vol in archivos claves_sesion; do
  docker run --rm -v "${proyecto}_${vol}:/v:ro" -v "$destino_abs:/o" "$imagen" tar czf "/o/$vol.tgz" -C /v .
done
echo "== Secretos (cifrados)"
tar czf - -C "$secretos" . | RESPALDO_FRASE="$(cat "$clave")" openssl enc -aes-256-cbc -pbkdf2 -salt -pass env:RESPALDO_FRASE > "$destino/secretos.tgz.enc"
( cd "$destino" && sha256sum base.dump roles.sql archivos.tgz claves_sesion.tgz secretos.tgz.enc > SHA256SUMS )
echo "Respaldo listo en: $destino"
ls -l "$destino"
