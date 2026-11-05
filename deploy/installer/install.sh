#!/bin/sh
# The installer's one job (deploy/compose.yml): what this image was built with, into the volumes, then exit 0. Run again by every
# `docker compose up --build`; the game server's restart watcher notices a new program, pre.sh or plugin by itself.
#
#   /overstrike2  the program volume, read-only at /home/steam/overstrike2 in the game server and the sync: os2-server, os2-sync, pre.sh
#   /plugins      the plugins volume, read-only at /home/steam/cs2-plugins in the game server (paths.plugins): OverStrike2/
#   /game         the game's volume: pre.sh at its top, where the game image sources it before every engine start, and workshop-sync/,
#                 the sync's own part of it (staging, test tree, hand-overs), which the sync mounts as a subpath
#   /data         the bridge's data: our maps, the sizes and the blocked list every time (this repository is their master); the first
#                 map lists only while there are none, since from then on the Workshop sync writes them there
#   /depotcache, /sync-state   only made the game image's user's
#
# Every file is written beside its target and renamed over it, and only when it differs. Owner of what the game image's user writes:
# OS2_UID:OS2_GID (1000:1000, the image's steam user; the bridge runs as the same user).
set -eu
uid="${OS2_UID:-1000}" gid="${OS2_GID:-1000}"

put() { # put <source> <target> [mode]: the source's bytes at the target, renamed into place, verified
    if [ -f "$2" ] && cmp -s "$1" "$2"; then
        echo "  $2: same"
    else
        cp "$1" "$2.new" && cmp -s "$1" "$2.new" && chmod "${3:-644}" "$2.new" && mv -f "$2.new" "$2"
        echo "  $2: installed"
    fi
}

mkdir -p /overstrike2 /plugins/OverStrike2 /game/workshop-sync /data /depotcache /sync-state
echo "installer: the programs and the launcher"
put /payload/program/os2-server /overstrike2/os2-server 755
put /payload/program/os2-sync /overstrike2/os2-sync 755
put /payload/program/pre.sh /overstrike2/pre.sh
put /payload/program/pre.sh /game/pre.sh
echo "installer: the plugin"
for f in /payload/plugins/OverStrike2/*; do
    put "$f" "/plugins/OverStrike2/${f##*/}"
done
echo "installer: the bridge's data"
for f in ours.json sizes.json blocked.json; do
    put "/payload/data/$f" "/data/$f"
done
for f in maps.json broken.json search.json; do
    if [ -e "/data/$f" ]; then
        echo "  /data/$f: the Workshop sync's, left alone"
    else
        put "/payload/data/$f" "/data/$f"
    fi
done
chown "$uid:$gid" /game /game/pre.sh /game/workshop-sync /data /data/*.json /depotcache /sync-state
echo "installer: done (owner $uid:$gid)"
