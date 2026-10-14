#!/usr/bin/env python3
"""App-level pass (agreedCharacters>=3) under two Apple assumptions: apple='' and apple==whisper. tool=argv[2] reads whisper text lines.
Usage: app_eval.py <runs.jsonl> <tools/bin/agree_same_empty>"""
import json,sys,collections,subprocess
man={}
for d in ("clips","heldout"):
    for m in json.load(open(f"{d}/manifest.json")): man[f"{d}/{m['file']}"]=m
per=collections.defaultdict(list)
for l in open(sys.argv[1]):
    r=json.loads(l)
    if r["event"]=="run": per[r["file"]].append(r["text"].strip().replace("\n"," "))
fs=sorted(per); allt=[(f,t) for f in fs for t in per[f]]
out=subprocess.run([sys.argv[2]],input="\n".join(t for _,t in allt)+"\n",text=True,capture_output=True).stdout.split("\n")
res=collections.defaultdict(lambda:[0,0,0])
for (f,t),line in zip(allt,out):
    same,empty=map(int,line.split()); res[f][0]+=same; res[f][1]+=empty; res[f][2]+=1
S={}
for f in fs:
    m=man[f]; s="heldout" if f.startswith("heldout") else "base"; k=m["kind"]
    same,empty,n=res[f]
    d=S.setdefault(f"{s}-{k}",dict(clips=0,pass_apple_same=0,pass_apple_empty=0))
    d["clips"]+=1; d["pass_apple_same"]+=same*2>n; d["pass_apple_empty"]+=empty*2>n
print(json.dumps(S,ensure_ascii=False))
