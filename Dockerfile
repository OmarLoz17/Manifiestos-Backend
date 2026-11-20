# Imagen de la API. Contexto: la carpeta backend/. Sin secretos: se montan en /run/secrets al ejecutar.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS compilar
WORKDIR /src
COPY global.json ./
COPY src/Manifiestos.Api/Manifiestos.Api.csproj src/Manifiestos.Api/
RUN dotnet restore src/Manifiestos.Api/Manifiestos.Api.csproj
COPY src/Manifiestos.Api/ src/Manifiestos.Api/
RUN dotnet publish src/Manifiestos.Api/Manifiestos.Api.csproj -c Release --no-restore -o /publicado /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
# Directorios de datos persistentes (volúmenes con nombre): archivos de evidencia, claves de sesión y registro. Dueño: el usuario sin privilegios.
RUN mkdir -p /datos/archivos /datos/claves-sesion /datos/registro && chown -R app:app /datos
COPY --from=compilar /publicado ./
ENV ASPNETCORE_ENVIRONMENT=Production \
    DOTNET_gcServer=0 \
    Archivos__Ruta=/datos/archivos \
    DataProtection__Ruta=/datos/claves-sesion \
    Registro__Ruta=/datos/registro
USER app
EXPOSE 8080
ENTRYPOINT ["dotnet", "Manifiestos.Api.dll"]
