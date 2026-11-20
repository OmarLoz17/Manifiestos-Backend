#!/usr/bin/env bash
# Restaura un respaldo en una instancia SEPARADA (otro proyecto de Compose, otros volúmenes, otros secretos y otro puerto).
# Uso (desde backend/deploy): RESPALDO_CLAVE_ARCHIVO=<frase> PROYECTO_NUEVO=manifiestos-restaurado PUERTO_TLS=8444 scripts/restaurar.sh <carpeta-del-respaldo>
# No toca la instancia original. Con ENSAYO=1 aplica compose.ensayo.yaml (TLS autofirmado y puerto local); en el VPS se omite.
set -euo pipefail
cd "$(dirname "$0")/.."
export MSYS_NO_PATHCONV=1
origen="${1:?Indica la carpeta del respaldo}"; origen="$(cd "$origen" && pwd)"
clave="${RESPALDO_CLAVE_ARCHIVO:?Indica RESPALDO_CLAVE_ARCHIVO}"
nuevo="${PROYECTO_NUEVO:?Indica PROYECTO_NUEVO (distinto del original)}"
imagen="${IMAGEN_UTILIDADES:-postgres:18.6-bookworm}"
secretos="${SECRETOS_RESTAURADOS:-./restaurado/$nuevo/secretos}"
export PROYECTO="$nuevo" SECRETOS_DIR="$secretos"
# Otra subred privada: la de la instancia original sigue ocupada en el mismo anfitrión.
export SUBRED_PRIVADA="${SUBRED_PRIVADA:-172.31.0.0/24}" IP_WEB="${IP_WEB:-172.31.0.10}"
compose=(docker compose -p "$nuevo" -f compose.yaml)
[ -n "${ENSAYO:-}" ] && compose+=(-f compose.ensayo.yaml)
[ -n "${ENSAYO_E2E:-}" ] && compose+=(-f compose.ensayo-e2e.yaml)

echo "== Verificando integridad del respaldo"
( cd "$origen" && sha256sum -c SHA256SUMS )
if docker compose -p "$nuevo" ps -q 2>/dev/null | grep -q .; then echo "El proyecto $nuevo ya existe; elige otro nombre o bórralo antes." >&2; exit 1; fi

echo "== Secretos"
mkdir -p "$secretos"
RESPALDO_FRASE="$(cat "$clave")" openssl enc -d -aes-256-cbc -pbkdf2 -pass env:RESPALDO_FRASE < "$origen/secretos.tgz.enc" | tar xzf - -C "$secretos"
ls "$secretos"

echo "== Instancia nueva (sin iniciar la API)"
"${compose[@]}" up --no-start postgres api web >/dev/null
"${compose[@]}" up -d postgres
until [ "$(docker inspect -f '{{.State.Health.Status}}' "$("${compose[@]}" ps -q postgres)")" = healthy ]; do sleep 2; done

echo "== Roles y base"
# Los roles ya existentes (manifiestos_owner) generan un aviso inocuo.
"${compose[@]}" exec -T postgres psql -X -q -U manifiestos_owner -d postgres < "$origen/roles.sql" 2>&1 | grep -v "already exists" || true
"${compose[@]}" exec -T postgres pg_restore -U manifiestos_owner -d manifiestos --exit-on-error < "$origen/base.dump"

echo "== Volúmenes"
for vol in archivos claves_sesion; do
  docker run --rm -v "${nuevo}_${vol}:/v" -v "$origen:/o:ro" "$imagen" tar xzf "/o/$vol.tgz" -C /v
done

echo "== Arranque (las migraciones solo comparan huellas: lo ya aplicado no se repite)"
"${compose[@]}" up -d
echo "Instancia restaurada: proyecto $nuevo"
"${compose[@]}" ps
