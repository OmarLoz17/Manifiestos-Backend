#!/usr/bin/env bash
# Envía un correo de texto simple por SMTP con curl (sin instalar nada). Uso: scripts/avisar-correo.sh "<asunto>" "<texto>"
# Configuración (variables de entorno, normalmente en /etc/manifiestos/respaldo.env):
#   AVISO_CORREO        destinatario(s), separados por coma (p. ej. soporte@arlocapital.com). Vacío = no envía nada.
#   SMTP_URL            smtps://smtp.gmail.com:465 (SSL directo) o smtp://smtp-relay.gmail.com:587 (con SMTP_STARTTLS=1)
#   SMTP_REMITENTE      dirección From (debe ser una cuenta o dominio autorizado en el servidor SMTP)
#   SMTP_USUARIO        usuario SMTP (vacío = sin autenticación, p. ej. relay por IP de Google Workspace)
#   SMTP_CLAVE_ARCHIVO  archivo (permisos 600) con la contraseña o contraseña de aplicación; nunca en .env
# El mensaje solo lleva el aviso (sin datos del sistema). Falla en silencio hacia el llamador: un correo caído no debe romper el respaldo.
set -uo pipefail
asunto="${1:?asunto}"; texto="${2:?texto}"
[ -n "${AVISO_CORREO:-}" ] && [ -n "${SMTP_URL:-}" ] && [ -n "${SMTP_REMITENTE:-}" ] || exit 0
mensaje="$(mktemp)"; trap 'rm -f "$mensaje"' EXIT
{
  printf 'From: Manifiestos <%s>\r\n' "$SMTP_REMITENTE"
  printf 'To: %s\r\n' "$AVISO_CORREO"
  printf 'Subject: [Manifiestos] %s\r\n' "$asunto"
  printf 'Date: %s\r\n' "$(date -R)"
  printf 'MIME-Version: 1.0\r\nContent-Type: text/plain; charset=utf-8\r\nContent-Transfer-Encoding: 8bit\r\n\r\n'
  printf '%s\r\n\r\nServidor: %s\r\nHora (UTC): %s\r\n' "$texto" "$(hostname)" "$(date -u +%FT%TZ)"
} > "$mensaje"
args=(--silent --show-error --max-time 30 --url "$SMTP_URL" --mail-from "$SMTP_REMITENTE" --upload-file -)
[ "${SMTP_STARTTLS:-0}" = 1 ] && args+=(--ssl-reqd)
IFS=',' read -ra para <<< "$AVISO_CORREO"; for p in "${para[@]}"; do args+=(--mail-rcpt "$(echo "$p" | xargs)"); done
if [ -n "${SMTP_USUARIO:-}" ]; then
  [ -r "${SMTP_CLAVE_ARCHIVO:-/nonexistent}" ] || { echo "avisar-correo: falta SMTP_CLAVE_ARCHIVO legible." >&2; exit 0; }
  args+=(--user "$SMTP_USUARIO:$(tr -d '\r\n' < "$SMTP_CLAVE_ARCHIVO")")
fi
curl "${args[@]}" < "$mensaje" || echo "avisar-correo: no se pudo enviar el correo." >&2
exit 0
