#!/usr/bin/env bash
# Ensayo completo del paquete con datos ficticios, desde cero: migraciones, HTTPS por el proxy, cookies/CSRF, fotos, sobredimensionados,
# respaldo, restauración en instancia separada y QR anterior. Uso (desde backend/deploy):  bash ensayo/ensayo.sh [limpiar]
# Requiere Docker, openssl y Node. Las fotos sintéticas se generan con ensayo/Generar-Fotos.ps1 (Windows); en Linux copiar JPEG de prueba
# de 1,9 / 2,4 / 6 MiB a ensayo/.estado/{foto-valida,foto-2_5MiB,foto-5MiB}.jpg.
set -euo pipefail
cd "$(dirname "$0")/.."
export MSYS_NO_PATHCONV=1
ORIG=manifiestos-ensayo; REST=manifiestos-restaurado
base=(-f compose.yaml -f compose.ensayo.yaml)
c() { docker compose -p "$ORIG" "${base[@]}" "$@"; }
limpiar() {
  docker compose -p "$ORIG" "${base[@]}" down -v --remove-orphans 2>/dev/null || true
  PUERTO_TLS=8444 SUBRED_PRIVADA=172.31.0.0/24 IP_WEB=172.31.0.10 docker compose -p "$REST" "${base[@]}" down -v --remove-orphans 2>/dev/null || true
  rm -rf secretos respaldos restaurado .env ensayo/tls
}
if [ "${1:-}" = limpiar ]; then limpiar; echo 'Ensayo limpiado (contenedores, volúmenes, secretos y respaldos de ensayo).'; exit 0; fi

echo '### 0. Limpieza previa'; limpiar
mkdir -p ensayo/.estado
if ! ls ensayo/.estado/foto-valida.jpg >/dev/null 2>&1; then powershell.exe -NoProfile -ExecutionPolicy Bypass -File ensayo/Generar-Fotos.ps1; fi
rm -f ensayo/.estado/estado.json
echo '### 1. Secretos fuera de Git, certificado de ensayo y .env'
bash scripts/preparar-secretos.sh; bash ensayo/cert.sh; cp .env.example .env
export PROYECTO="$ORIG"
echo '### 2. Compilar imágenes, base nueva, 68 migraciones desde cero, demo ficticia, API y proxy en Production'
c up -d --build
c ps
echo '### 3. Production: salud, HTTPS por el proxy, cookies, enlaces directos, token E2E rechazado'
node ensayo/cliente.mjs prod
echo '### 4. Accesos ficticios; API en entorno E2E: CSRF, sesión, QR, móvil, foto válida, sobredimensionados'
c exec -T postgres psql -X -q -v ON_ERROR_STOP=1 -U manifiestos_owner -d manifiestos < ensayo/semilla-acceso.sql
c -f compose.ensayo-e2e.yaml up -d api; sleep 8
node ensayo/cliente.mjs e2e
echo '### 5. Respaldo'
frase=ensayo/.estado/clave-respaldo; [ -s "$frase" ] || openssl rand -base64 24 | tr -d '\n' > "$frase"
export RESPALDO_CLAVE_ARCHIVO="$PWD/$frase" ENSAYO=1
bash scripts/respaldar.sh
carpeta="$(ls -d respaldos/*/ | tail -1)"
echo '### 6. Restauración en instancia separada (otro proyecto, volúmenes, secretos, subred y puerto 8444)'
PROYECTO_NUEVO="$REST" PUERTO_TLS=8444 bash scripts/restaurar.sh "$carpeta"
node ensayo/cliente.mjs restaurado https://localhost:8444
for p in "$ORIG" "$REST"; do
  echo "# conteos en $p"; docker compose -p "$p" exec -T postgres psql -X -At -U manifiestos_owner -d manifiestos -c "SELECT (SELECT count(*) FROM infra.migracion) AS migraciones, (SELECT count(*) FROM negocio.captura) AS capturas, (SELECT count(*) FROM negocio.archivo) AS archivos, (SELECT count(*) FROM negocio.version_manifiesto) AS versiones_manifiesto"
done
echo '### 7. Recrear la API original en Production: sesiones, token móvil, QR y foto sobreviven'
c up -d --force-recreate api; sleep 8
node ensayo/cliente.mjs restaurado https://localhost:8443
echo '### Ensayo completo. Limpiar con: bash ensayo/ensayo.sh limpiar'
