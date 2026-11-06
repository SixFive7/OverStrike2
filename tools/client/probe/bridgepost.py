"""What only the restart watcher or other players can send the bridge, sent by hand, for the screenshots no harness verb reaches: a restart announced, an
update's notice, fake voters. TEST COPY AND CANDIDATE BRIDGE ONLY: production's bridge never hears any of it. Before every send all of this must hold:

  - the harness holds the server lock;
  - the game server's container is a test copy and running (its cs2.test-config label, testcopy.py run);
  - the bridge's container is the candidate and running (its own label, testcopy.py bridge-run), with its /data on the -candidate copy;
  - production's game server and production's bridge are both aside, exited.

So what these verbs change lands in the candidate's copy of the data, and goes with it (testcopy.py bridge-rm). Only the routes below, and no verb takes a route or a
free text body from the command line. The calls go from inside the game container, the way the game server's own log push does, to the bridge's container name
(bridge.container in the private config), which the checks above make the candidate. The secret travels in the exec's environment and is never printed.

    python bridgepost.py restart <kind> <when> <seconds>    /restart-notice: gg-restart 1 <kind> <when> <seconds>
                                                            kind: game-update, our-update, daily, downloads; when: match-end, now
    python bridgepost.py restart-cancel <kind>              /restart-notice: gg-restart 1 cancel <kind>
    python bridgepost.py updated <kind> <words...>          /updated: gg-updated 1 <kind> <words> ("CS2 1.41.9.6")
    python bridgepost.py quit-notice                        /quitting: gg-quit 1 daily: the bridge takes the server for going down: a player's !restart counting down sends no quit after it
    python bridgepost.py fake-on [n]                        /log: n of the fake players (1-4: Test A to Test D) connect and enter the game
    python bridgepost.py fake-off                           /log: all of them leave, and HUDtest, whom css_gghud click makes present
    python bridgepost.py fake-crash                         /log: the first three time out, which marks the map they are on and rolls the dice: a map change counting down. Call it off in time: quit-notice
    python bridgepost.py client-off <account>               /log: the bridge takes that real player for gone: its lines stop naming them; the game still shows them everything
    python bridgepost.py client-on <account>                /log: a chat line under the test name: the bridge has them back, under that name, until their next click
    python bridgepost.py act <fake 0-3> <word>              /act: that fake player's click: next, random, previous, restart, rematch, time10 or time60
    python bridgepost.py vote <fake 0-3> <card>             /act: that fake player's click on the open vote's card, its number on the wire: Keep 1, the rest 2 up (the round from
                                                            GET /hud, which it waits up to 8 s for: a click and a log push go through the bridge's one loop)
    python bridgepost.py check                              the checks above, nothing sent

A notice with `when now` counts down to a quit that only the restart watcher would send, and a test copy has none: cancel it (restart-cancel) when the picture is
taken, because no vote can start until then. The answers to /log and /act say nothing of what they did: read a click's effect on the screen or in the bridge's log.
"""
import re
import sys
import time
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))
import probe  # noqa: E402  (the server lock and the Docker API)
import testcopy  # noqa: E402  (what a test copy and a candidate are)

FAKES = [("Test A", "[U:1:42424251]"), ("Test B", "[U:1:42424252]"), ("Test C", "[U:1:42424253]"), ("Test D", "[U:1:42424254]")]   # test names only: the pictures are public
CLIENT_NAME = "Alice"                         # docshots.py's test name for the real client, in what the bridge says about them
ACTS = {"next": "!next", "random": "!next random", "previous": "!previous", "restart": "!restart", "rematch": "!rematch", "time10": "!time 10", "time60": "!time 60"}   # bridge/Hud.cs's Actions, those a picture needs
HUDTEST = ("HUDtest", "[U:1:42424243]")       # plugin/OverStrike2's css_gghud click for docshots.py's account that is not on the server
KINDS = ("game-update", "our-update", "daily", "downloads")
WHEN = ("match-end", "now")
ROUND_TRIES, ROUND_WAIT = 5, 2.0
# inside the game container: the URL from the exec's environment, the body from its argument; only the answer comes back
POST = ("import os,sys,urllib.request as u;r=u.Request(os.environ['OS2_BRIDGE_URL'],data=sys.argv[1].encode(),method='POST');"
        "a=u.urlopen(r,timeout=8);print(a.status,a.read(400).decode('utf-8','replace'))")
# the vote's line of the state, and nothing else of it: the rest has players' names in it
GET_VOTE = ("import os,urllib.request as u;a=u.urlopen(os.environ['OS2_BRIDGE_URL'],timeout=8);"
            "print('\\n'.join(l for l in a.read().decode('utf-8','replace').splitlines() if l.startswith('mv\\t')))")


def env_of(d, name):
    return next((e.split("=", 1)[1] for e in (d.get("Config") or {}).get("Env") or [] if e.startswith(name + "=")), None)


def refuse(why):
    sys.exit(f"REFUSED: {why} - nothing sent")


def gate():
    """Every check of this file's first paragraph; returns the candidate's inspect."""
    probe.need_lock()
    game = testcopy.inspect(testcopy.GAME)
    if game is None or not testcopy.is_test(game):
        refuse(f"{testcopy.GAME} is not a test copy")
    if game["State"]["Status"] != "running":
        refuse("the test copy is not running")
    b = testcopy.inspect(testcopy.BRIDGE)
    if b is None or ((b.get("Config") or {}).get("Labels") or {}).get(testcopy.TEST_LABEL) != testcopy.BRIDGE_TAG:
        refuse(f"{testcopy.BRIDGE} is not the candidate")
    if b["State"]["Status"] != "running":
        refuse("the candidate is not running")
    data = testcopy.data_mount(b)
    if not data or not data.rstrip("/").endswith("-candidate"):
        refuse("the candidate's /data is not its copy")
    for name, what in ((testcopy.ASIDE, "production's game server"), (testcopy.BRIDGE_ASIDE, "production's bridge")):
        d = testcopy.inspect(name)
        if d is None or d["State"]["Status"] != "exited":
            refuse(f"{what} is not aside and stopped")
    return b


def route(b):
    """The candidate's port and secret, from its own inspect: its settings text when it has one, else the environment. Never printed."""
    secret, port = env_of(b, "LOG_SECRET") or "", env_of(b, "HTTP_PORT") or "8080"
    text = env_of(b, testcopy.CONTENT)
    if text:
        section = (testcopy.jsonc(text) or {}).get("bridge") or {}
        secret, port = section.get("secret") or secret, str(section.get("port") or port)
        if secret.startswith("env:"):
            secret = env_of(b, secret[4:]) or ""
    if not secret or secret.startswith("file:"):
        refuse("the candidate's secret cannot be read from its inspect")
    return secret, port


def exec_in_copy(url, cmd, secret):
    """One exec in the test copy, the URL in its environment; its output with the secret scrubbed, should it ever be in there."""
    return probe.exec_in(testcopy.GAME, cmd, env={"OS2_BRIDGE_URL": url}).replace(secret, "<secret>").strip()


def post(b, path, body, answers=True):
    """answers: the route says ok or refused in words; else (/log, /act) the status is all there is, and what follows it (the live state, on /act) is not printed."""
    secret, port = route(b)
    answer = exec_in_copy(f"http://{testcopy.BRIDGE}:{port}/{path}/{secret}", ["python3", "-c", POST, body], secret)
    ok = answer.startswith("200 ok:") if answers else answer[:3] == "200" and answer[3:4] in ("", " ", "\n")
    said = answer if answers or not ok else "200"
    print(time.strftime("%H:%M:%S"), f"/{path}:", " | ".join(body.split("\n"))[:160].replace("\t", " "), "->", said[:200])
    if not ok:
        sys.exit(1)


def open_round(b):
    """The round of the vote that is open, from the bridge's own state; refuses when none has come up in ROUND_TRIES looks."""
    secret, port = route(b)
    for i in range(ROUND_TRIES):
        state = exec_in_copy(f"http://{testcopy.BRIDGE}:{port}/hud/{secret}", ["python3", "-c", GET_VOTE], secret)
        line = next((l.split("\t") for l in state.splitlines() if l.startswith("mv\t")), None)
        if line and len(line) >= 3 and line[2] == "open":
            return line[1]
        if i + 1 < ROUND_TRIES:
            time.sleep(ROUND_WAIT)
    refuse("no vote is open")


def log_lines(lines):
    stamp = time.strftime("%m/%d/%Y - %H:%M:%S.000 - ")
    return "\n".join(stamp + line for line in lines)


def fake(arg):
    return FAKES[int(arg)] if arg.isdigit() and int(arg) < len(FAKES) else sys.exit(f"a fake player is 0-{len(FAKES) - 1}, not {arg}")


def main(argv):
    if not argv or argv[0] in ("-h", "--help"):
        print(__doc__)
        return
    verb, args = argv[0], argv[1:]
    b = gate()
    if verb == "check":
        print("all checks hold: a test copy, the candidate on its copy of the data, production and its bridge aside")
    elif verb == "restart":
        if len(args) != 3 or args[0] not in KINDS or args[1] not in WHEN or not args[2].isdigit():
            sys.exit(f"usage: restart <{'|'.join(KINDS)}> <{'|'.join(WHEN)}> <seconds>")
        post(b, "restart-notice", "\t".join(["gg-restart", "1", *args]))
    elif verb == "restart-cancel":
        if len(args) != 1 or args[0] not in KINDS:
            sys.exit(f"usage: restart-cancel <{'|'.join(KINDS)}>")
        post(b, "restart-notice", "\t".join(["gg-restart", "1", "cancel", args[0]]))
    elif verb == "updated":
        if len(args) < 2 or args[0] not in KINDS or not re.fullmatch(r"[\w .-]{1,60}", " ".join(args[1:])):
            sys.exit(f"usage: updated <{'|'.join(KINDS)}> <words>")
        post(b, "updated", "\t".join(["gg-updated", "1", args[0], " ".join(args[1:])]))
    elif verb == "fake-on":
        n = int(args[0]) if args and args[0].isdigit() else len(FAKES) if not args else 0
        if not 1 <= n <= len(FAKES):
            sys.exit(f"usage: fake-on [1-{len(FAKES)}]")
        lines = [f'"{name}<9{i}><{sid}><>" connected, address ""' for i, (name, sid) in enumerate(FAKES[:n])]
        lines += [f'"{name}<9{i}><{sid}><CT>" entered the game' for i, (name, sid) in enumerate(FAKES[:n])]
        post(b, "log", log_lines(lines), answers=False)
    elif verb == "fake-off":
        post(b, "log", log_lines(f'"{name}<9{i}><{sid}><CT>" disconnected (reason "NETWORK_DISCONNECT_DISCONNECT_BY_USER")' for i, (name, sid) in enumerate([*FAKES, HUDTEST])), answers=False)
    elif verb == "fake-crash":
        post(b, "log", log_lines(f'"{name}<9{i}><{sid}><CT>" disconnected (reason "NETWORK_DISCONNECT_TIMEDOUT")' for i, (name, sid) in enumerate(FAKES[:3])), answers=False)
    elif verb == "quit-notice":
        post(b, "quitting", "gg-quit\t1\tdaily")
    elif verb in ("client-off", "client-on"):
        if len(args) != 1 or not args[0].isdigit():
            sys.exit(f"usage: {verb} <account>")
        who = f'"{CLIENT_NAME}<99><[U:1:{args[0]}]><CT>"'
        post(b, "log", log_lines([f'{who} disconnected (reason "NETWORK_DISCONNECT_DISCONNECT_BY_USER")' if verb == "client-off" else f'{who} say "back"']), answers=False)
    elif verb == "act":
        if len(args) != 2 or args[1] not in ACTS:
            sys.exit(f"usage: act <fake 0-{len(FAKES) - 1}> <{'|'.join(ACTS)}>")
        name, sid = fake(args[0])
        post(b, "act", f"{sid}\t{name}\t{ACTS[args[1]]}", answers=False)
    elif verb == "vote":
        if len(args) != 2 or not args[1].isdigit():
            sys.exit(f"usage: vote <fake 0-{len(FAKES) - 1}> <card>")
        name, sid = fake(args[0])
        post(b, "act", f"{sid}\t{name}\t!mv {open_round(b)} {args[1]}", answers=False)
    else:
        sys.exit(f"unknown verb {verb}: {__doc__}")


if __name__ == "__main__":
    main(sys.argv[1:])
