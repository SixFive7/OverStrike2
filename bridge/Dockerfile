# The bridge's image. Build context: the repository root - the bridge's sources and core/, the library it shares with the server
# programs (the settings file among them): docker build -f bridge/Dockerfile . (deploy/bridge.py sends exactly bridge/ and core/).

# Stage 1: native AOT publish. The -aot SDK variant ships clang and zlib, which ILCompiler needs.
FROM mcr.microsoft.com/dotnet/sdk:10.0-noble-aot AS build
WORKDIR /src
COPY bridge/GunGameBridge.csproj bridge/
COPY core/OverStrike2.Core.csproj core/
RUN dotnet restore bridge/GunGameBridge.csproj -r linux-x64
COPY bridge/ bridge/
COPY core/ core/
RUN dotnet publish bridge/GunGameBridge.csproj -c Release -r linux-x64 --no-restore -o /out

# Stage 2: chiseled Ubuntu (no shell, no package manager, non-root by default). runtime-deps is all a native AOT binary needs;
# tzdata is copied from the SDK image so TZ=Area/City resolves inside the chiseled image (it ships without zoneinfo).
FROM mcr.microsoft.com/dotnet/runtime-deps:10.0-noble-chiseled
COPY --from=build /usr/share/zoneinfo /usr/share/zoneinfo
COPY --from=build /out/GunGameBridge /app/GunGameBridge
COPY bridge/maps.json /app/maps.json
# the map browser's full-text index (--search-index keeps it in step), read from beside whichever maps.json MAPS_FILE names
COPY bridge/search.json /app/search.json
ENV MAPS_FILE=/app/maps.json STATE_DIR=/data HTTP_PORT=8080
EXPOSE 8080
ENTRYPOINT ["/app/GunGameBridge"]
