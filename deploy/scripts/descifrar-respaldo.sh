#!/usr/bin/env bash
# Abre un respaldo cifrado de la copia externa (AAAAmmdd-HHMMSS.tar.enc) y deja la carpeta del respaldo lista para scripts/restaurar.sh.
# Uso: RESPALDO_CLAVE_ARCHIVO=<archivo con la frase> scripts/descifrar-respaldo.sh <archivo.tar.enc> <carpeta-destino>
# Bajar antes el archivo desde Drive (rclone copy remoto:carpeta/AAAAmmdd-HHMMSS.tar.enc .).
set -euo pipefail
archivo="${1:?Indica el archivo .tar.enc}"; destino="${2:?Indica la carpeta donde dejar el respaldo}"
clave="${RESPALDO_CLAVE_ARCHIVO:?Indica RESPALDO_CLAVE_ARCHIVO}"
mkdir -p "$destino"
RESPALDO_FRASE="$(cat "$clave")" openssl enc -d -aes-256-cbc -pbkdf2 -pass env:RESPALDO_FRASE < "$archivo" | tar xf - -C "$destino"
echo "Respaldo abierto en: $destino/$(ls "$destino" | grep -E '^[0-9]{8}-[0-9]{6}$' | tail -1)"
