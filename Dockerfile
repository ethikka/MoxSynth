# syntax=docker/dockerfile:1
#
# MoxSynth web app + API (for the Android app) as one container.
# Data (card catalogue, profiles, collections, prices, sign-in keys) lives in /app/App_Data – mount a volume there.
#
#   docker compose up -d --build        (see compose.yaml and README → "Running in Docker")
#
# Works on x64 and ARM64 (e.g. a NAS or Raspberry Pi): the SDK stage runs on the build machine's platform and
# publishes for the target architecture.

# ---------------------------------------------------------------- build
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG TARGETARCH
WORKDIR /src

# Restore first (cached until a project file changes). Only the server and its contracts – not the Android app.
COPY src/MoxSynth.Contracts/MoxSynth.Contracts.csproj src/MoxSynth.Contracts/
COPY src/MoxSynth/MoxSynth.csproj src/MoxSynth/
RUN dotnet restore src/MoxSynth/MoxSynth.csproj -a $TARGETARCH

COPY src/MoxSynth.Contracts/ src/MoxSynth.Contracts/
COPY src/MoxSynth/ src/MoxSynth/
RUN dotnet publish src/MoxSynth/MoxSynth.csproj -c Release -a $TARGETARCH --no-restore \
        -o /app -p:UseAppHost=false \
    && rm -rf /app/App_Data/*

# ---------------------------------------------------------------- runtime
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app .

# The data folder must be writable by the (non-root) app user; a bind mount can also be run as your own
# user id instead (compose.yaml: PUID / PGID).
RUN mkdir -p /app/App_Data && chown -R $APP_UID /app/App_Data
VOLUME /app/App_Data

ENV ASPNETCORE_HTTP_PORTS=8080 \
    ASPNETCORE_ENVIRONMENT=Production \
    DOTNET_gcServer=0
EXPOSE 8080

USER $APP_UID
ENTRYPOINT ["dotnet", "MoxSynth.dll"]
