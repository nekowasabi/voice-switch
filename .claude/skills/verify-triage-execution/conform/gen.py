#!/usr/bin/env python3
"""Write transitions.json: every reachable state and transition of TriageExec.tla under design.cfg.

Usage: gen.py <tla2tools.jar> <out.json>
TLC runs Spec (the model as checked) and TriageExecEvents.tla (Probe prints each step and is then FALSE).
The "T" lines must number exactly the steps Spec generated, so an action missing from Probe fails here.
"A" lines are the same actions with the row's intent set to another value; the app cannot know intent.
"""
import hashlib, json, os, re, shutil, subprocess, sys, tempfile

HERE = os.path.dirname(os.path.abspath(__file__))
SKILL = os.path.dirname(HERE)
TOKEN = re.compile(r'\s*(<<|>>|\|->|:>|@@|"(?:[^"\\]|\\.)*"|[\[\]{}(),]|[A-Za-z_][A-Za-z_0-9]*|-?\d+)')


def parse(text):
    """TLC's ToString of a value -> JSON value. Records and functions become dicts, tuples and sets lists."""
    toks, pos = [], 0
    while pos < len(text):
        m = TOKEN.match(text, pos)
        if not m:
            if text[pos:].strip() == "":
                break
            raise ValueError("cannot read TLC value at: " + text[pos:pos + 40])
        toks.append(m.group(1))
        pos = m.end()
    i = 0

    def value():
        nonlocal i
        t = toks[i]; i += 1
        if t in ("<<", "{"):
            close = ">>" if t == "<<" else "}"
            out = []
            while toks[i] != close:
                out.append(value())
                if toks[i] == ",": i += 1
            i += 1
            return out
        if t == "[":
            out = {}
            while toks[i] != "]":
                k = toks[i]; i += 2  # key, |->
                out[k] = value()
                if toks[i] == ",": i += 1
            i += 1
            return out
        if t == "(":
            out = {}
            while toks[i] != ")":
                k = value(); i += 1  # :>
                out[k] = value()
                if toks[i] == "@@": i += 1
            i += 1
            return out
        if t.startswith('"'): return json.loads(t)
        if t == "TRUE": return True
        if t == "FALSE": return False
        if re.fullmatch(r"-?\d+", t): return int(t)
        return t  # a model value such as u1

    v = value()
    if i != len(toks): raise ValueError("trailing tokens in: " + text[:80])
    return v


def init_lines(stdout):
    return [l for l in stdout.splitlines() if l.startswith('"<<\\"I\\"')]


def tlc(jar, work, spec, cfg):
    p = subprocess.run(["java", "-XX:+UseParallelGC", "-cp", jar, "tlc2.TLC", "-workers", "1", "-config", cfg, spec],
                       cwd=work, capture_output=True, text=True)
    if p.returncode != 0 or "No error has been found" not in p.stdout:
        sys.exit("TLC failed on %s:\n%s" % (spec, p.stdout[-3000:]))
    m = re.search(r"(\d+) states generated, (\d+) distinct states found", p.stdout)
    return p.stdout, int(m.group(1)), int(m.group(2))


def main():
    jar, out_path = sys.argv[1], sys.argv[2]
    work = tempfile.mkdtemp(prefix="triage-conform-")
    try:
        for f in ("TriageExec.tla",):
            shutil.copy(os.path.join(SKILL, f), work)
        shutil.copy(os.path.join(HERE, "TriageExecEvents.tla"), work)
        cfg = open(os.path.join(SKILL, "design.cfg")).read()
        base = re.sub(r"^INVARIANTS .*$", "", cfg, flags=re.M)
        open(os.path.join(work, "plain.cfg"), "w").write(base)
        open(os.path.join(work, "events.cfg"), "w").write(base.replace("SPECIFICATION Spec", "SPECIFICATION EvSpec"))
        _, gen_plain, distinct_plain = tlc(jar, work, "TriageExec.tla", "plain.cfg")
        stdout, gen_ev, distinct_ev = tlc(jar, work, "TriageExecEvents.tla", "events.cfg")
    finally:
        shutil.rmtree(work, ignore_errors=True)
    if (gen_plain, distinct_plain) != (gen_ev, distinct_ev):
        sys.exit("EvNext differs from Next: Spec generated %d (%d distinct), EvSpec %d (%d distinct)"
                 % (gen_plain, distinct_plain, gen_ev, distinct_ev))

    index, states, init, edges, consts, real = {}, [], set(), {}, None, 0

    def sid(st):
        alive, crashes, rows = st
        key = json.dumps([alive, crashes, sorted(rows.items())])
        if key not in index:
            index[key] = len(states)
            states.append({"alive": alive, "crashes": crashes, "rows": rows})
        return index[key]

    for line in stdout.splitlines():
        if not line.startswith('"<<'):
            continue
        v = parse(json.loads(line))
        if v[0] == "I":
            init.add(sid(v[1])); consts = v[2]
        else:
            real += v[0] == "T"
            k = (sid(v[2]), json.dumps(v[1]))
            to = sid(v[3])
            if edges.setdefault(k, to) != to:
                sys.exit("event %s from state %d has two next states" % (v[1], k[0]))
    if real != gen_plain - len(init_lines(stdout)):
        sys.exit("Probe printed %d real steps, Spec generated %d (minus initial states): Probe misses an action of Next"
                 % (real, gen_plain))
    u, kinds, routes, heavy, verdicts, max_crashes, alternate = consts
    fields = ["stage", "persisted", "verdict", "verified", "runs", "runKind", "graced", "userOK", "retries", "dismissed", "dRuns",
              "route", "runRoute"]
    doc = {
        "about": "Generated by .claude/skills/verify-triage-execution/conform/gen.py from TriageExec.tla + design.cfg. Do not edit.",
        "model_sha256": hashlib.sha256(open(os.path.join(SKILL, "TriageExec.tla"), "rb").read()).hexdigest(),
        "config_sha256": hashlib.sha256(cfg.encode()).hexdigest(),
        "tlc": {"states_generated": gen_plain, "distinct_states_with_intent": distinct_plain},
        "constants": {"U": sorted(u), "Kinds": sorted(kinds), "Routes": sorted(routes), "Heavy": sorted(heavy),
                      "AlternateRoutes": sorted(alternate), "Verdicts": sorted(verdicts), "MaxCrashes": max_crashes},
        "row_fields": fields,
    }
    # Rows and events repeat a lot; store each once and refer to it by index.
    rows, row_ix, events, event_ix = [], {}, [], {}

    def intern(table, ix, value):
        key = json.dumps(value)
        if key not in ix:
            ix[key] = len(table); table.append(value)
        return ix[key]

    doc["rows"] = rows
    doc["events"] = events
    doc["init"] = sorted(init)
    doc["states"] = [[s["alive"], s["crashes"]] + [intern(rows, row_ix, s["rows"][x]) for x in sorted(u)] for s in states]
    doc["transitions"] = [[f, intern(events, event_ix, json.loads(e)), t] for (f, e), t in sorted(edges.items())]
    with open(out_path, "w") as fh:  # one table entry per line, so a model change shows as a readable diff
        fh.write("{\n")
        keys = list(doc)
        for n, k in enumerate(keys):
            end = ",\n" if n < len(keys) - 1 else "\n"
            if isinstance(doc[k], list) and doc[k] and isinstance(doc[k][0], list):
                fh.write(json.dumps(k) + ":[\n" + ",\n".join(json.dumps(x, separators=(",", ":")) for x in doc[k]) + "\n]" + end)
            else:
                fh.write(json.dumps(k) + ":" + json.dumps(doc[k], separators=(",", ":")) + end)
        fh.write("}\n")
    print("wrote %s: %d states, %d transitions (TLC: %d generated, %d distinct with intent)"
          % (out_path, len(states), len(edges), gen_plain, distinct_plain))


if __name__ == "__main__":
    main()
