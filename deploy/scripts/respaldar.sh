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
# Un respaldo incompleto (sin SHA256SUMS, que se escribe al final) se borra: no debe contar para la rotación ni parecer válido.
trap '[ -f "$destino_abs/SHA256SUMS" ] || { rm -rf "$destino_abs"; echo "Respaldo incompleto descartado." >&2; }' EXIT
export SECRETOS_DIR="$secretos"

echo "== Base de datos"
"${compose[@]}" exec -T postgres pg_dump -U manifiestos_owner -d manifiestos -Fc > "$destino/base.dump"
"${compose[@]}" exec -T postgres pg_dumpall -U manifiestos_owner --globals-only > "$destino/roles.sql"
echo "== Volúmenes"
docker run --rm -v "${proyecto}_claves_sesion:/v:ro" -v "$destino_abs:/o" "$imagen" tar czf "/o/claves_sesion.tgz" -C /v .
# ARCHIVOS_MODO=completo (por omisión): todo el volumen de evidencias.
# ARCHIVOS_MODO=pendientes: solo lo que AÚN no tiene copia verificada en Drive (con Drive activo y la limpieza por retención, es
# lo único que el VPS tiene y Drive no). Lo demás vive en Drive; restaurar sirve esos archivos desde Drive. Antes de usarlo,
# comprobar que Drive esté activo y archivando (si no, «pendientes» sería casi todo, y está bien: nunca omite lo no archivado).
modo="${ARCHIVOS_MODO:-completo}"
case "$modo" in
  completo) docker run --rm -v "${proyecto}_archivos:/v:ro" -v "$destino_abs:/o" "$imagen" tar czf "/o/archivos.tgz" -C /v . ;;
  pendientes)
    "${compose[@]}" exec -T postgres psql -X -At -U manifiestos_owner -d manifiestos -c "SELECT v.clave_externa FROM negocio.ubicacion_archivo v WHERE v.proveedor = 'vps' AND NOT EXISTS (SELECT 1 FROM negocio.ubicacion_archivo d WHERE (d.organizacion_id, d.sucursal_id, d.archivo_id) = (v.organizacion_id, v.sucursal_id, v.archivo_id) AND d.proveedor = 'drive' AND d.verificada_en IS NOT NULL)" > "$destino/archivos.lista"
    docker run --rm -i -v "${proyecto}_archivos:/v:ro" -v "$destino_abs:/o" "$imagen" bash -c 'cd /v && tar czf /o/archivos.tgz --ignore-failed-read -T -' < "$destino/archivos.lista"
    rm -f "$destino/archivos.lista" ;;
  *) echo "ARCHIVOS_MODO debe ser completo o pendientes." >&2; exit 1 ;;
esac
echo "$modo" > "$destino/archivos.modo"
echo "== Secretos (cifrados)"
tar czf - -C "$secretos" . | RESPALDO_FRASE="$(cat "$clave")" openssl enc -aes-256-cbc -pbkdf2 -salt -pass env:RESPALDO_FRASE > "$destino/secretos.tgz.enc"
( cd "$destino" && sha256sum base.dump roles.sql archivos.tgz archivos.modo claves_sesion.tgz secretos.tgz.enc > SHA256SUMS )
echo "Respaldo listo en: $destino"
ls -l "$destino"
