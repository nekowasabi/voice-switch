"""Red/green checks for audit findings 10 and 11 in scripts/journal_triage.py.

Reuses the World / Net fixtures of tests/test_journal_triage.py: HOME is a temp folder and urlopen is replaced,
so nothing reaches the network or the user's cache.
"""
import contextlib
import io
import sys
import tempfile
import urllib.error
from datetime import timedelta
from pathlib import Path
from unittest import mock

sys.path.insert(0, str(Path(__file__).resolve().parents[3] / "tests"))
import test_journal_triage as t  # noqa: E402


def drive(world, argv, reply):
    net = t.Net(reply)
    home = str(world.home)
    err = io.StringIO()
    with t.environ(HOME=home, USERPROFILE=home, LOCALAPPDATA=home, TYPESAFE_API_KEY="k-sentinel", JEV_API_KEY=None), \
            mock.patch.object(t.triage.urllib.request, "urlopen", net.urlopen), \
            mock.patch.object(t.socket.socket, "connect", net.connect), \
            contextlib.redirect_stdout(io.StringIO()), contextlib.redirect_stderr(err):
        try:
            t.triage.main(argv)
        except SystemExit:
            pass
    assert net.connects == 0, "a real connection was attempted"
    return net, err.getvalue()


def world_with_two_rows(tmp):
    world = t.World(tmp)
    world.write([t.row("a", t.T0, t.TEXTS[0]), t.row("b", t.T0 + timedelta(seconds=20), t.TEXTS[0])])
    return world


def cache():
    """Finding 10: an answer cached by --no-context must not stand in for a request with context."""
    with tempfile.TemporaryDirectory() as tmp:
        world = world_with_two_rows(tmp)
        argv = ["list", t.DAY, "--root", str(world.root)]
        drive(world, [*argv, "--no-context"], t.answers())
        net, _ = drive(world, argv, t.answers())
        with_context = [s for s in net.states() if "context" in s]
        if not with_context:
            print(f"the run with context sent {len(net.requests)} requests and none carried context; "
                  "it reused the --no-context answers from the cache")
            return 1
        print("the run with context asked Jev again with context")
        return 0


def reason():
    """Finding 11: when every Jev call fails, stderr says why (status or error type), without the text."""
    with tempfile.TemporaryDirectory() as tmp:
        world = world_with_two_rows(tmp)
        failure = urllib.error.HTTPError("https://api.typesafe.ai/v1/systemone", 401, "Unauthorized", {}, None)
        _, stderr = drive(world, ["list", t.DAY, "--root", str(world.root)], failure)
        for secret in ("QZX", t.FOCUS, t.TASK):
            assert secret not in stderr, f"stderr carries {secret}"
        if "401" not in stderr and "Unauthorized" not in stderr:
            print(f"a 401 from Jev left stderr {stderr.strip()!r}; the rows only show unjudged:error")
            return 1
        print("stderr names the Jev failure")
        return 0


if __name__ == "__main__":
    sys.exit({"cache": cache, "reason": reason}[sys.argv[1]]())
