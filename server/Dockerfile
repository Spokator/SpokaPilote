# syntax=docker/dockerfile:1
# SpokaPilote Server — image de la console centrale.
# Construction depuis la racine du dépôt (le Directory.Build.props racine fournit la version du produit) :
#   docker build -f server/Dockerfile -t spokapilote-server .
# ou : docker compose -f server/docker-compose.yml up -d --build

# ---------------------------------------------------------------- construction
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
ENV DOTNET_NOLOGO=1 DOTNET_CLI_TELEMETRY_OPTOUT=1

# Restauration d'abord (couche mise en cache tant que le .csproj ne change pas).
COPY Directory.Build.props ./
COPY server/SpokaPilote.Server/SpokaPilote.Server.csproj server/SpokaPilote.Server/
RUN dotnet restore server/SpokaPilote.Server/SpokaPilote.Server.csproj

COPY server/SpokaPilote.Server/ server/SpokaPilote.Server/
RUN dotnet publish server/SpokaPilote.Server/SpokaPilote.Server.csproj \
      -c Release -o /app --no-restore -p:UseAppHost=false

# ---------------------------------------------------------------- exécution
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
LABEL org.opencontainers.image.title="SpokaPilote Server" \
      org.opencontainers.image.description="Console centrale SpokaPilote : rapports des postes, matériel sans pilote, politique de pilotes" \
      org.opencontainers.image.source="https://github.com/Spokator/SpokaPilote"

ENV ASPNETCORE_HTTP_PORTS=8080 \
    SPOKAPILOTE_DATA_DIR=/data \
    DOTNET_NOLOGO=1 \
    DOTNET_CLI_TELEMETRY_OPTOUT=1

WORKDIR /app
COPY --from=build /app ./

# Utilisateur non root fourni par les images .NET (« app », UID $APP_UID = 1654), propriétaire du volume de données.
RUN mkdir -p /data && chown "$APP_UID:$APP_UID" /data && chmod 700 /data
USER $APP_UID
VOLUME ["/data"]
EXPOSE 8080

# Sonde de vie sans outil supplémentaire (ni curl ni wget dans l'image) : requête HTTP via /dev/tcp de bash.
HEALTHCHECK --interval=30s --timeout=5s --start-period=15s --retries=3 \
  CMD ["bash", "-c", "exec 3<>/dev/tcp/127.0.0.1/8080 && printf 'GET /api/health HTTP/1.1\\r\\nHost: localhost\\r\\nConnection: close\\r\\n\\r\\n' >&3 && grep -q '\"status\":\"ok\"' <&3"]

ENTRYPOINT ["dotnet", "SpokaPilote.Server.dll"]
