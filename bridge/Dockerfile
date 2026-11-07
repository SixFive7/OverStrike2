# The bridge's image. Build context: the repository root - the bridge's sources and core/, the library it shares with the server
# programs (the settings file among them): docker build -f bridge/Dockerfile . (deploy/bridge.py sends exactly bridge/ and core/).

# Stage 1: native AOT publish. The -aot SDK variant ships clang and zlib, which ILCompiler needs.
FROM mcr.microsoft.com/dotnet/sdk:10.0-noble-aot AS build
WORKDIR /src
COPY bridge/OverStrike2.Bridge.csproj bridge/
COPY core/OverStrike2.Core.csproj core/
RUN dotnet restore bridge/OverStrike2.Bridge.csproj -r linux-x64
COPY bridge/ bridge/
COPY core/ core/
RUN dotnet publish bridge/OverStrike2.Bridge.csproj -c Release -r linux-x64 --no-restore -o /out

# Stage 2: chiseled Ubuntu (no shell, no package manager, non-root by default). runtime-deps is all a native AOT binary needs;
# tzdata is copied from the SDK image so TZ=Area/City resolves inside the chiseled image (it ships without zoneinfo).
FROM mcr.microsoft.com/dotnet/runtime-deps:10.0-noble-chiseled
COPY --from=build /usr/share/zoneinfo /usr/share/zoneinfo
COPY --from=build /out/os2-bridge /app/os2-bridge
# everything else comes from the settings: the data files in paths.bridgeData, the port in bridge.port
EXPOSE 8080
ENTRYPOINT ["/app/os2-bridge"]
