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
