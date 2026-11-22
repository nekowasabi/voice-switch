"""scripts/mail-send <recipient>: the first line of stdin is the subject, all of stdin is the body, Mail.app sends it.

osascript is replaced by a fake on PATH that records its AppleScript source, its arguments and its environment, so no
mail is sent. The dictated text and the recipient must reach osascript as data (an argument or an environment value),
never inside the AppleScript source, so a quote in the text cannot run AppleScript.
MAIL_SEND points the test at another copy of the script (code-mutants.sh uses it).
"""
import json
import os
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

SCRIPT = Path(os.environ.get("MAIL_SEND") or Path(__file__).parents[1] / "scripts/mail-send")

FAKE = r'''#!%s
import json, os, sys
args = sys.argv[1:]
stdin = sys.stdin.read()
sources, data, i = [], [], 0
while i < len(args):
    if args[i] == "-e":
        sources.append(args[i + 1]); i += 2
    elif args[i] in ("-s", "-l"):
        i += 2
    else:
        break
rest = args[i:]
if sources:
    source = "\n".join(sources); data = rest + [stdin]
elif not rest or rest[0] == "-":
    source = stdin; data = rest[1:]
else:
    source = open(rest[0], encoding="utf-8").read(); data = rest[1:] + [stdin]
log = os.environ["FAKE_OSASCRIPT_LOG"]
with open(log, "a", encoding="utf-8") as f:
    f.write(json.dumps({"source": source, "data": data, "env": dict(os.environ)}, ensure_ascii=False) + "\n")
sys.exit(int(os.environ.get("FAKE_OSASCRIPT_EXIT", "0")))
''' % sys.executable


class MailSend(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        root = Path(self.tmp.name)
        (root / "bin").mkdir()
        fake = root / "bin/osascript"
        fake.write_text(FAKE, encoding="utf-8")
        fake.chmod(0o755)
        self.log = root / "calls.jsonl"
        self.path = f"{root / 'bin'}:/usr/bin:/bin"

    def tearDown(self):
        self.tmp.cleanup()

    def send(self, *args, text="", exit_code=0):
        env = {k: v for k, v in os.environ.items() if not k.startswith("FAKE_OSASCRIPT")}
        env |= {"PATH": self.path, "FAKE_OSASCRIPT_LOG": str(self.log), "FAKE_OSASCRIPT_EXIT": str(exit_code)}
        return subprocess.run(["/bin/sh", str(SCRIPT), *args], input=text.encode(), env=env, capture_output=True, timeout=30)

    def calls(self):
        return [json.loads(l) for l in self.log.read_text(encoding="utf-8").splitlines()] if self.log.exists() else []

    @staticmethod
    def values(call):
        """Everything the script handed osascript as data: arguments, stdin when it is not the source, environment."""
        return list(call["data"]) + list(call["env"].values())

    def test_the_first_line_is_the_subject_and_the_whole_text_is_the_body(self):
        text = "タスクシュートの昼食を 12 時に\n終了は 13 時\n\n"
        result = self.send("muse@example.test", text=text)
        self.assertEqual(result.returncode, 0, result.stderr)
        calls = self.calls()
        self.assertEqual(len(calls), 1)
        values = self.values(calls[0])
        for want in ("muse@example.test", "タスクシュートの昼食を 12 時に", text):
            self.assertIn(want, values)

    def test_one_line_is_both_subject_and_body(self):
        self.assertEqual(self.send("muse@example.test", text="来週の会議室を押さえて").returncode, 0)
        self.assertGreaterEqual(self.values(self.calls()[0]).count("来週の会議室を押さえて"), 2)

    def test_quotes_in_the_text_or_recipient_never_become_applescript(self):
        attack = '" & (do shell script "touch /tmp/mail-send-pwned") & "'
        text = f'件名 {attack}\nend tell\ndo shell script "id"\n本文 {attack}'
        recipient = f'muse@example.test{attack}'
        self.assertEqual(self.send(recipient, text=text).returncode, 0)
        call = self.calls()[0]
        self.assertNotIn("do shell script", call["source"])
        self.assertNotIn("mail-send-pwned", call["source"])
        self.assertIn(text, self.values(call))
        self.assertIn(recipient, self.values(call))

    def test_a_send_osascript_refuses_exits_non_zero(self):
        result = self.send("muse@example.test", text="送れない依頼", exit_code=1)
        self.assertNotEqual(result.returncode, 0)
        self.assertEqual(len(self.calls()), 1)

    def test_without_exactly_one_recipient_nothing_is_sent(self):
        for args in ((), ("",), ("a@example.test", "b@example.test")):
            with self.subTest(args=args):
                self.assertNotEqual(self.send(*args, text="宛先が無い").returncode, 0)
        self.assertEqual(self.calls(), [])


if __name__ == "__main__":
    unittest.main()
