#!/usr/bin/env bash
# Aplica las migraciones pendientes (misma lógica y mismo registro infra.migracion que database/Migrate.ps1) y deja
# preparado el rol limitado manifiestos_api con la contraseña del secreto. Se ejecuta dentro del contenedor «migrar»
# (imagen de PostgreSQL, que trae psql). Repetible: lo ya aplicado solo se compara por SHA-256; si una migración
# aplicada cambió, se detiene sin tocar nada.
set -euo pipefail

export PGHOST="${PGHOST:-postgres}" PGUSER="${PGUSER:-manifiestos_owner}" PGDATABASE="${PGDATABASE:-manifiestos}"
export PGPASSWORD="$(tr -d '\r\n' < /run/secrets/postgres_password)"
carpeta="${MIGRACIONES:-/migraciones}"
psql_base=(psql -X -q -v ON_ERROR_STOP=1)

sql="$(mktemp)"
{
  echo '\set ON_ERROR_STOP on'
  echo 'BEGIN;'
  echo 'SELECT pg_advisory_xact_lock(824070105);'
  echo 'CREATE SCHEMA IF NOT EXISTS infra;'
  echo 'CREATE TABLE IF NOT EXISTS infra.migracion (nombre text PRIMARY KEY, sha256 text NOT NULL, aplicada_en timestamptz NOT NULL DEFAULT clock_timestamp());'
  total=0
  for f in $(ls "$carpeta" | LC_ALL=C sort); do
    [[ "$f" =~ ^[0-9]{4}_[a-z0-9_]+\.sql$ ]] || { echo "Nombre inválido: $f" >&2; exit 1; }
    hash="$(sha256sum "$carpeta/$f" | cut -d' ' -f1)"
    echo "SELECT EXISTS (SELECT 1 FROM infra.migracion WHERE nombre = '$f') AS aplicada \gset"
    echo '\if :aplicada'
    echo "DO \$check\$ BEGIN IF NOT EXISTS (SELECT 1 FROM infra.migracion WHERE nombre = '$f' AND sha256 = '$hash') THEN RAISE EXCEPTION 'Migracion modificada: $f'; END IF; END \$check\$;"
    echo '\else'
    cat "$carpeta/$f"; echo
    echo "INSERT INTO infra.migracion(nombre, sha256) VALUES ('$f', '$hash');"
    echo '\endif'
    total=$((total + 1))
  done
  echo 'COMMIT;'
} > "$sql"
[ "$total" -gt 0 ] || { echo 'No hay migraciones.' >&2; exit 1; }

until pg_isready -q; do sleep 1; done
"${psql_base[@]}" -f "$sql"
rm -f "$sql"
echo "Migraciones: $(psql -X -At -c 'SELECT count(*) FROM infra.migracion') aplicadas de $total archivos."

# Rol limitado de la API: puede conectarse y hereda solo manifiestos_app (sin superusuario, DDL ni BYPASSRLS).
# La contraseña viaja por stdin, no por la línea de comandos.
pw="$(tr -d '\r\n' < /run/secrets/api_password)"
[[ "$pw" =~ ^[0-9a-f]{64}$ ]] || { echo 'Formato inesperado de api_password.' >&2; exit 1; }
"${psql_base[@]}" <<SQL
\set pw '$pw'
SELECT CASE WHEN EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'manifiestos_api')
    THEN format('ALTER ROLE manifiestos_api LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOBYPASSRLS INHERIT PASSWORD %L', :'pw')
    ELSE format('CREATE ROLE manifiestos_api LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOBYPASSRLS INHERIT PASSWORD %L', :'pw')
END \gexec
GRANT manifiestos_app TO manifiestos_api;
SQL
echo 'Rol manifiestos_api preparado.'

# Solo ensayo: datos 100 % ficticios (la variable la fija compose.ensayo.yaml; nunca en producción).
if [ "${CARGAR_DEMO:-0}" = "1" ]; then
  if [ "$(psql -X -At -c "SELECT count(*) FROM negocio.organizacion")" = "0" ]; then
    "${psql_base[@]}" -f /demo/datos_demo.sql
    echo 'Datos demo ficticios cargados.'
  else echo 'La base ya tiene organizaciones; no se carga la demo.'; fi
fi
