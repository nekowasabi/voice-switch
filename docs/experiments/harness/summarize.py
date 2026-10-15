#!/usr/bin/env python3
"""summarize.py runs.jsonl label -> baseline section JSON on stdout."""
import json,sys,statistics as st,re
rows=[json.loads(l) for l in open(sys.argv[1])]; man={m["file"]:m for m in json.load(open("clips/manifest.json"))}
HALL=re.compile(r"ご視聴|ありがとうございました|チャンネル登録|音楽|字幕|お疲れ様")
load=rows[0]; runs=[r for r in rows if r["event"]=="run"]
per={}
for r in runs: per.setdefault(r["file"].split("/")[-1],[]).append(r)
clips=[];fp=0;fp_h=0;miss=0;ns=0;sp=0
for f,rs in sorted(per.items()):
    m=man[f]; secs=[r["sec"] for r in rs]; txt=[r["text"].strip() for r in rs]
    t=max(set(txt),key=txt.count)
    c=dict(file=f,kind=m["kind"],source=m["source"],audio_sec=rs[0]["audio_sec"],runs=len(rs),
      latency_box_cpu_median=round(st.median(secs),3),latency_min=round(min(secs),3),latency_max=round(max(secs),3),
      rtf_median=round(st.median(secs)/rs[0]["audio_sec"],2),text=t,stable_text=len(set(txt))==1,failures=sum(r["rc"]!=0 for r in rs))
    if m["kind"]=="nonspeech":
        ns+=1; c["false_transcription"]=bool(t); fp+=bool(t); c["hallucination_phrase"]=bool(HALL.search(t)); fp_h+=c["hallucination_phrase"]
    else:
        sp+=1; ok=bool(t) and (not m["expect"] or m["expect"] in t); c["miss"]=not ok; miss+=not ok
    clips.append(c)
rss=[r["rss_kb"] for r in runs]
print(json.dumps(dict(label=sys.argv[2],model_load_sec=load["sec"],
  summary=dict(nonspeech_clips=ns,nonspeech_false_transcriptions=fp,of_which_hallucination_phrase=fp_h,speech_clips=sp,speech_misses=miss,
   total_calls=len(runs),failures=sum(r["rc"]!=0 for r in runs),rss_mb_after_load=load["rss_kb"]//1024,rss_mb_first_call=rss[0]//1024,rss_mb_last_call=rss[-1]//1024,
   latency_box_cpu_median_all=round(st.median([r["sec"] for r in runs if r["audio_sec"]>=1]),3)),clips=clips),ensure_ascii=False,indent=1))
