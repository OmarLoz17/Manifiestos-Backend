# Manifiestos – Backend

API ASP.NET Core (.NET 10) y base PostgreSQL del Sistema de Manifiestos.

- Raíz: solución `Manifiestos.slnx`, API (`src/`) y pruebas (`tests/`).
- `database/`: migraciones SQL numeradas, scripts de PowerShell y datos demo ficticios.
- `compose.yaml`: PostgreSQL local.

Los secretos (conexión, GoogleClientId, OrganizacionId) van en .NET User Secrets, nunca en el repositorio.

Pruebas del backend:

    dotnet test --artifacts-path .salida-pruebas --nologo --verbosity minimal

Las migraciones aplicadas no se editan (se validan por SHA-256); los cambios van en migraciones nuevas.
El panel web (`web/`) y Android (`android/`) son repositorios hermanos; ver la documentación del proyecto en la carpeta superior.

Canales separados: `/api/auth/*` usa cookies web y solo admite roles con acceso al panel; `/api/movil/auth/*` usa Bearer opaco del framework y solo admite `telefono_operador` con instalación y entrega vigentes. No concede `datos.leer`/`datos.escribir` al operador. Migración 0021; revalidación en cada petición y renovación, revocación de sesión en servidor. El ID token Google debe incluir el nonce del desafío de un uso.

Alta local repetible: `database/Add-AccesoOperador.ps1`, correo/organización por parámetros. Sin nombre solo autoriza la cuenta; con nombre, sucursal y acceso de Operaciones crea la ficha; con instalación registra el equipo. No reasigna una entrega anterior ni cambia una cuenta de Operaciones a operador. HTTPS de laboratorio: `Start-LocalHttps.ps1`, tras preparar certificado/configuración con `../android/tools/Preparar-AccesoLocal.ps1`. Guía y límites en `../docs/android/acceso-movil-local.md`.
