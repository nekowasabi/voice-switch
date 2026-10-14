#!/usr/bin/env python3
"""App pass = agreed>=3 per run, clip passes if majority of runs pass. tool reads apple\twhisper, prints agreed.
Assumptions: A) apple='' B) apple==whisper raw text C) speech only: apple=TTS source sentence (JFK excluded: not TTS).
Usage: app_eval3.py <runs.jsonl> <tools/bin/agree_pair or agree_pair_main>"""
import json,sys,collections,subprocess
SRC={"tts-hai":"はい","tts-hai-quiet-20dB":"はい","tts-hai-raw-0.26s":"はい"}
SENT="画面の右上にあるボタンを押してください。"  # assumed source = clean TTS whisper output
man={}
for d in ("clips","heldout"):
    for m in json.load(open(f"{d}/manifest.json")): man[f"{d}/{m['file']}"]=m
per=collections.defaultdict(list)
for l in open(sys.argv[1]):
    r=json.loads(l)
    if r["event"]=="run": per[r["file"]].append(r["text"].strip().replace("\n"," ").replace("\t"," "))
q=[]
for f in sorted(per):
    n=f.split("/")[-1][:-4]; src=SRC.get(n, SENT if n.startswith("tts-sentence") else None)
    for t in per[f]: q+=[(f,"A","",t),(f,"B",t,t)]+([(f,"C",src,t)] if src else [])
out=subprocess.run([sys.argv[2]],input="\n".join(a+"\t"+w for _,_,a,w in q)+"\n",text=True,capture_output=True).stdout.split()
votes=collections.defaultdict(list)
for (f,k,_,_),o in zip(q,out): votes[(f,k)].append(int(o)>=3)
S=collections.defaultdict(lambda:collections.Counter())
for (f,k),v in votes.items():
    m=man[f]; s=("heldout" if f.startswith("heldout") else "base")+"-"+m["kind"]
    S[s][k+"_pass"]+=sum(v)*2>len(v); S[s][k+"_n"]+=1
print(json.dumps(S,ensure_ascii=False))
