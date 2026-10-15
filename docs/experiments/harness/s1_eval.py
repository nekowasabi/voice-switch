#!/usr/bin/env python3
"""S1 eval: whisper text before/after the hallucination filter.
Usage: s1_eval.py <runs.jsonl> <tools/bin/hallucination_filter>"""
import json,sys,collections
from tool_io import run_tool
rows=[json.loads(l) for l in open(sys.argv[1]) if '"run"' in l]
man={}
for d in ("clips","heldout"):
    for m in json.load(open(f"{d}/manifest.json")): man[f"{d}/{m['file']}"]=m
per=collections.defaultdict(list)
for r in rows: per[r["file"]].append(r)
files=sorted(per); texts=[collections.Counter(r["text"].strip() for r in per[f]).most_common(1)[0][0] for f in files]
flt=run_tool(sys.argv[2],[t.replace("\n"," ") for t in texts],"hallucination_filter",allowed=lambda o: o in ("0","1"))
out=dict(sets={},clips=[])
for f,t,h in zip(files,texts,flt):
    m=man[f]; s=f.split("/")[0]; after="" if h=="1" else t
    st=out["sets"].setdefault(s,dict(nonspeech=0,fp_before=0,fp_after=0,speech=0,miss_before=0,miss_after=0))
    if m["kind"]=="nonspeech": st["nonspeech"]+=1; st["fp_before"]+=bool(t); st["fp_after"]+=bool(after)
    else:
        ok=lambda x: bool(x) and (not m["expect"] or m["expect"] in x); st["speech"]+=1; st["miss_before"]+=not ok(t); st["miss_after"]+=not ok(after)
    segs=[r.get("segments",[]) for r in per[f]]
    out["clips"].append(dict(file=f,kind=m["kind"],runs=len(per[f]),stable=len({r["text"] for r in per[f]})==1,text=t,rejected=h=="1",segments_run0=segs[0]))
print(json.dumps(out,ensure_ascii=False,indent=1))
