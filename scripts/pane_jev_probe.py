#!/usr/bin/env python3
"""Ask Jev which tmux pane a dictation is addressed to, over a fixed pane catalog.

Same question shape as `jevRequestBody` in PaneRoute.swift and `PaneRoute.JevRequestBody` in C#.
Needs TYPESAFE_API_KEY (or JEV_API_KEY).
Prints one line per case so thresholds can be read off real answers.
"""
import json
import os
import sys
import urllib.request

PANES = [
    {"id": "%0", "window": "voice-switch", "title": "editor", "command": "nvim", "agents": []},
    {"id": "%1", "window": "voice-switch", "title": "", "command": "node", "agents": ["claude"]},
    {"id": "%2", "window": "dotfiles", "title": "", "command": "node", "agents": ["claude"]},
    {"id": "%3", "window": "shell", "title": "", "command": "zsh", "agents": []},
]

# (utterance, expected pane id or None)
CASES = [
    ("ネオビムでこの関数の名前を直して", "%0"),
    ("クロードに、テストを書いてと伝えて", None),
    ("ドットファイルのクロードで、さっきの変更をコミットして", "%2"),
    ("ボイススイッチのクロードにログを見てもらって", "%1"),
    ("シェルで make test を実行して", "%3"),
    ("dotfiles の claude で、さっきの変更をコミットして", "%2"),
    ("今日はいい天気ですね", None),
    ("node のバージョンを上げる方法を調べてメモしておいて", None),
]


def question(panes):
    criteria = {
        p["id"]: {"window": p["window"], "title": p["title"], "command": p["command"], "agents": p["agents"]}
        for p in panes
    }
    criteria["none"] = "The dictation does not name or clearly address any one of the listed panes."
    return {
        "type": "choice",
        "instructions": "`dictation` is speech-to-text output, so pane names may be misheard or written in katakana. "
        "Which listed tmux pane does the speaker address by name (window, title, command, or agent)?",
        "criteria": criteria,
    }


def ask(key, utterance, panes):
    body = json.dumps({"state": {"dictation": utterance}, "model": "jev-latest",
                       "questions": {"pane": question(panes)}}).encode()
    req = urllib.request.Request("https://api.typesafe.ai/v1/systemone", data=body, method="POST",
                                 headers={"Authorization": f"Bearer {key}", "Content-Type": "application/json"})
    with urllib.request.urlopen(req, timeout=10) as r:
        return json.load(r)["answers"]["pane"]


def main():
    key = os.environ.get("TYPESAFE_API_KEY") or os.environ.get("JEV_API_KEY")
    if not key:
        sys.exit("TYPESAFE_API_KEY or JEV_API_KEY is required")
    right = 0
    for utterance, want in CASES:
        a = ask(key, utterance, PANES)
        got = None if a["choice"] == "none" else a["choice"]
        right += got == want
        print(f"{'OK ' if got == want else 'NG '} want={want} got={got} conf={a['confidence']:.2f} {utterance}")
    print(f"{right}/{len(CASES)}")


if __name__ == "__main__":
    main()
