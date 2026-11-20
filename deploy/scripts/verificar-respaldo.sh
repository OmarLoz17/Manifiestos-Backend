#!/usr/bin/env bash
# Comprueba un respaldo SIN restaurarlo: huellas SHA-256, que el volcado de la base se pueda leer y que los volúmenes y los
# secretos cifrados abran (con la frase). No sustituye el ensayo periódico de restauración completa (scripts/restaurar.sh).
# Uso: RESPALDO_CLAVE_ARCHIVO=<frase> scripts/verificar-respaldo.sh <carpeta>
set -euo pipefail
export MSYS_NO_PATHCONV=1
origen="${1:?Indica la carpeta del respaldo}"; origen="$(cd "$origen" && pwd)"
clave="${RESPALDO_CLAVE_ARCHIVO:?Indica RESPALDO_CLAVE_ARCHIVO}"
imagen="${IMAGEN_UTILIDADES:-postgres:18.6-bookworm}"
( cd "$origen" && sha256sum -c SHA256SUMS ) >/dev/null
docker run --rm -v "$origen:/o:ro" "$imagen" bash -c '
  pg_restore --list /o/base.dump | grep -q "TABLE DATA" &&
  tar tzf /o/archivos.tgz >/dev/null && tar tzf /o/claves_sesion.tgz >/dev/null &&
  grep -q "CREATE ROLE manifiestos_api" /o/roles.sql'
RESPALDO_FRASE="$(cat "$clave")" openssl enc -d -aes-256-cbc -pbkdf2 -pass env:RESPALDO_FRASE < "$origen/secretos.tgz.enc" | tar tzf - | grep -q 'llave_validacion.pem'
echo "Respaldo verificado: $origen"
