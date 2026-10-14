// In-process harness mirroring WhisperGate.swift: one context, greedy default params, language=ja, n_threads=argv.
// usage: bench MODEL THREADS RUNS clip.wav...   -> JSONL on stdout
#include "whisper.h"
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <time.h>
#include <unistd.h>
static double now(){struct timespec t;clock_gettime(CLOCK_MONOTONIC,&t);return t.tv_sec+t.tv_nsec*1e-9;}
static long rss_kb(){long a,b;FILE*f=fopen("/proc/self/statm","r");fscanf(f,"%ld %ld",&a,&b);fclose(f);return b*sysconf(_SC_PAGESIZE)/1024;}
static float* rd(const char*p,int*n){FILE*f=fopen(p,"rb");fseek(f,0,2);long L=ftell(f);fseek(f,44,0);int k=(L-44)/2;short*s=malloc(k*2);fread(s,2,k,f);fclose(f);float*x=malloc(k*4);for(int i=0;i<k;i++)x[i]=s[i]/32768.f;free(s);*n=k;return x;}
static void js(const char*s){putchar('"');for(;*s;s++){if(*s=='"'||*s=='\\')putchar('\\');if((unsigned char)*s<0x20)printf("\\u%04x",*s);else putchar(*s);}putchar('"');}
static void noop(enum ggml_log_level l,const char*t,void*u){}
int main(int c,char**v){
  whisper_log_set(noop,NULL);
  double t0=now(); struct whisper_context_params cp=whisper_context_default_params();
  struct whisper_context*ctx=whisper_init_from_file_with_params(v[1],cp); if(!ctx)return 2;
  printf("{\"event\":\"load\",\"sec\":%.3f,\"rss_kb\":%ld}\n",now()-t0,rss_kb());
  int th=atoi(v[2]),runs=atoi(v[3]);
  for(int r=0;r<runs;r++)for(int i=4;i<c;i++){int n;float*x=rd(v[i],&n);
    struct whisper_full_params p=whisper_full_default_params(WHISPER_SAMPLING_GREEDY);
    p.print_progress=p.print_realtime=p.print_timestamps=p.print_special=false;p.n_threads=th;p.language="ja"; if(getenv("AUDIO_CTX"))p.audio_ctx=atoi(getenv("AUDIO_CTX")); if(getenv("NO_FALLBACK"))p.temperature_inc=0; if(getenv("SUPPRESS_NST"))p.suppress_nst=true; if(getenv("NO_CONTEXT"))p.no_context=true;
    double a=now();int rc=whisper_full(ctx,p,x,n);double dt=now()-a;
    char buf[8192]="";float nsp=-1;int ns=rc?0:whisper_full_n_segments(ctx);
    for(int s=0;s<ns;s++){strncat(buf,whisper_full_get_segment_text(ctx,s),sizeof(buf)-strlen(buf)-1);if(s==0)nsp=whisper_full_get_segment_no_speech_prob(ctx,0);}
    printf("{\"event\":\"run\",\"run\":%d,\"file\":",r);js(v[i]);printf(",\"audio_sec\":%.3f,\"sec\":%.3f,\"rc\":%d,\"rss_kb\":%ld,\"no_speech_prob\":%.3f,\"text\":",n/16000.0,dt,rc,rss_kb(),nsp);js(buf);printf("}\n");fflush(stdout);free(x);}
  whisper_free(ctx);return 0;}
