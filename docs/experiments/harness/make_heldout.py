#!/usr/bin/env python3
"""Held-out nonspeech clips (NOT used to build the S1 reject list). Synthetic only. -> heldout/*.wav + manifest."""
import json,wave,numpy as np
from pathlib import Path
R=16000;out=Path("heldout");rng=np.random.default_rng(1234);M=[]
def w(name,x):
    x=np.clip(x,-1,1)
    with wave.open(str(out/f"{name}.wav"),"wb") as f: f.setnchannels(1);f.setsampwidth(2);f.setframerate(R);f.writeframes((x*32767).astype("<i2").tobytes())
    M.append(dict(file=f"{name}.wav",kind="nonspeech",source="synthetic",expect=""))
t=lambda s:np.arange(int(R*s))/R; z=lambda s:np.zeros(int(R*s))
b=np.cumsum(rng.standard_normal(int(R*5)));b-=np.convolve(b,np.ones(800)/800,"same");w("brown-5s",0.3*b/np.abs(b).max())
x=t(6);w("fan-120hz-6s",0.05*(np.sin(2*np.pi*120*x)+0.5*np.sin(2*np.pi*240*x)+0.3*np.sin(2*np.pi*360*x))+0.01*rng.standard_normal(len(x)))
y=z(2.5)
for s in (0.4,0.9,1.6):
    i=int(s*R);L=int(0.04*R);y[i:i+L]+=0.7*rng.standard_normal(L)*np.exp(-np.arange(L)/(0.006*R))
w("knocks-2.5s",y)
L=int(0.35*R);e=np.exp(-np.arange(L)/(0.09*R))*np.minimum(1,np.arange(L)/(0.01*R))
c=np.convolve(rng.standard_normal(L),np.hanning(24)/12,"same")*e;w("single-cough-in-1.2s",np.concatenate([z(0.6),0.6*c/np.abs(c).max(),z(0.25)]))
br=np.convolve(rng.standard_normal(int(R*4)),np.ones(40)/40,"same")*(0.5-0.5*np.cos(2*np.pi*t(4)/2));w("breath-4s",0.2*br/np.abs(br).max())
w("silence-1.5s",z(1.5))
json.dump(M,open(out/"manifest.json","w"),ensure_ascii=False,indent=1)
