# The installer (deploy/compose.yml): the game server's plugin and both server programs, built from this repository. At every start it
# puts them, with the launcher pre.sh and the bridge's hand-made data, into the volumes the other services mount, and exits; the game
# server and the sync start after it. Build context: the repository root (its .dockerignore keeps the context to these sources).

# Stage 1: the build. Restoring needs the network (NuGet: the CounterStrikeSharp API package the plugin compiles against).
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY core/ core/
COPY server/ server/
COPY bridge/ bridge/
COPY plugin/ plugin/
RUN dotnet publish server/OverStrike2.Server -c Release -r linux-x64 -o /out/program \
 && dotnet publish server/OverStrike2.Sync -c Release -r linux-x64 -o /out/program \
 && dotnet build plugin/OverStrike2 -c Release -o /out/plugin

# Stage 2: the payload, and a shell to copy it with.
FROM docker.io/library/busybox:stable
COPY --from=build /out/program/os2-server /out/program/os2-sync /payload/program/
COPY server/OverStrike2.Server/pre.sh /payload/program/
COPY --from=build /out/plugin/OverStrike2.dll /out/plugin/OverStrike2.pdb /out/plugin/blocked.txt /out/plugin/catchup.json /payload/plugins/OverStrike2/
COPY bridge/ours.json bridge/sizes.json bridge/blocked.json bridge/maps.json bridge/broken.json bridge/search.json /payload/data/
COPY deploy/installer/install.sh /install.sh
ENTRYPOINT ["/bin/sh", "/install.sh"]
