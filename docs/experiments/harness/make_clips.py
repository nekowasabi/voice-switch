#!/usr/bin/env python3
"""Generate the baseline clip set. Writes clips/*.wav (16k mono PCM16) + clips/manifest.json.
source: synthetic (generated here), tts-kyoko (repo fixtures, macOS say), real (human recording)."""
import json, shutil, numpy as np, wave
from pathlib import Path
R=16000; out=Path("clips"); rng=np.random.default_rng(7)
G=Path("repo/tests/parity/fixtures/speech_gate")
def w(name,x,kind,source,expect=""):
    x=np.clip(x,-1,1); 
    with wave.open(str(out/f"{name}.wav"),"wb") as f:
        f.setnchannels(1);f.setsampwidth(2);f.setframerate(R);f.writeframes((x*32767).astype("<i2").tobytes())
    M.append(dict(file=f"{name}.wav",kind=kind,source=source,expect=expect))
def rd(p):
    with wave.open(str(p)) as f: return np.frombuffer(f.readframes(f.getnframes()),"<i2").astype(np.float32)/32768
def pink(n):
    X=np.fft.rfft(rng.standard_normal(n)); f=np.arange(len(X)); f[0]=1; y=np.fft.irfft(X/np.sqrt(f),n); return y/np.abs(y).max()
def cough(n):  # burst of filtered noise with fast attack / exp decay, 2 bursts
    y=np.zeros(n)
    for s in (int(0.3*R),int(0.75*R)):
        L=int(0.25*R); e=np.exp(-np.arange(L)/(0.06*R))*np.minimum(1,np.arange(L)/(0.005*R))
        b=np.convolve(rng.standard_normal(L),np.ones(8)/8,"same")*e
        y[s:s+L]+=b[:max(0,min(L,n-s))]
    return 0.8*y/np.abs(y).max()
M=[]
z=lambda s:np.zeros(int(R*s))
# non-speech (expect empty)
w("silence-3s",z(3),"nonspeech","synthetic")
w("silence-10s",z(10),"nonspeech","synthetic")
w("dither-3s",rng.standard_normal(3*R)*1e-4,"nonspeech","synthetic")
w("white-3s",rng.standard_normal(3*R)*0.1,"nonspeech","synthetic")
w("pink-3s",pink(3*R)*0.3,"nonspeech","synthetic")
w("hum50-3s",0.2*np.sin(2*np.pi*50*np.arange(3*R)/R),"nonspeech","synthetic")
w("clicks-3s",np.where(rng.random(3*R)<0.0005,0.9,0)*rng.choice([-1,1],3*R),"nonspeech","synthetic")
w("cough-like-1.5s",np.concatenate([cough(int(1.5*R)),z(0.5)]),"nonspeech","synthetic")
w("cough-in-silence-4s",np.concatenate([z(1.5),cough(int(1.5*R)),z(1)]),"nonspeech","synthetic")
for f in ("noise-keystrokes","noise-burst"):
    w(f"repo-{f}",np.concatenate([z(.5),rd(G/f"{f}.wav"),z(.5)]),"nonspeech","synthetic")
# speech (expect text)
hai=np.concatenate([z(.8),rd(G/"backchannel-hai.wav"),z(.8)]);  # 0.26s clip padded: whisper.cpp skips input <1s
sent=rd(G/"speech-sentence.wav")
w("tts-hai-raw-0.26s",rd(G/"backchannel-hai.wav"),"speech","tts-kyoko","はい")
w("tts-hai",hai,"speech","tts-kyoko","はい")
w("tts-hai-quiet-20dB",hai*0.1,"speech","tts-kyoko","はい")
w("tts-sentence",sent,"speech","tts-kyoko","")
w("tts-sentence-quiet-20dB",sent*0.1,"speech","tts-kyoko","")
w("tts-sentence-pink-snr10",sent+pink(len(sent))*np.sqrt(np.mean(sent**2))/np.sqrt(np.mean(pink(len(sent))**2))*0.316,"speech","tts-kyoko","")
w("tts-sentence-cough",np.concatenate([cough(int(1.2*R)),sent]),"speech","tts-kyoko+synthetic","")
jfk=Path("wcpp/samples/jfk.wav")
if jfk.exists(): w("real-en-jfk",rd(jfk),"speech","real (English, whisper.cpp sample)","")
json.dump(M,open(out/"manifest.json","w"),ensure_ascii=False,indent=1)
print(len(M),"clips")
