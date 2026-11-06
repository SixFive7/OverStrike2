"""The offline tests' deployment: a private config of made-up values in a scratch folder (the repository's .work/, gitignored), and the
harness's state beside it - set up before probe.py or testcopy.py is imported, removed by the test at its end. No server is ever asked:
each test replaces probe.req, the one door to the Docker API."""
import os
import tempfile
from pathlib import Path

REPO = Path(__file__).resolve().parents[3]


def make(prefix):
    """A scratch folder with config.toml in it; OVERSTRIKE2_CONFIG and CS2CLIENT_STATE point there. Returns the folder."""
    work = REPO / ".work"
    work.mkdir(exist_ok=True)
    tmp = Path(tempfile.mkdtemp(prefix=prefix, dir=str(work)))
    (tmp / "bridge-data").mkdir()
    (tmp / "config.toml").write_text(
        "[docker]\napi = \"https://docker.example.com:2376\"\n\n"
        "[bridge]\nimage = \"localhost/overstrike2-bridge\"\ncontainer = \"bridge\"\ngame_container = \"cs2\"\n"
        f"data_dir = '{tmp / 'bridge-data'}'\n\n"
        f"[server]\naddress = \"203.0.113.10:27015\"\nlock_file = '{tmp / 'server.lock'}'\n\n"
        "[client]\nprivate_names = [\"Realname\"]\n\n[client.test_labels]\n\"monitor.mute\" = \"true\"\n", encoding="utf-8")
    os.environ["OVERSTRIKE2_CONFIG"] = str(tmp / "config.toml")
    os.environ["CS2CLIENT_STATE"] = str(tmp / "state")
    return tmp
