"""D2/D3 finite receipt checks; payload timing is not TLS/network wire throughput."""
from pathlib import Path
import json,csv,sys
root=Path(sys.argv[1])
def read(p):return json.loads(p.read_text(encoding="utf-8-sig"))
# D8 keeps each actual SDK PutMedia attempt separate. A decoded prefix is not a
# retained tail, a synthetic ACK, or authority to stitch ambiguous files together.
if any(root.glob("retry-*")):
 require_recovery=(root/"recovery-required.txt").exists() or "--require-recovery" in sys.argv
 check_only="--check-only" in sys.argv
 result={}
 for p in sorted(root.glob("retry-*")):
  receipt=read(p/"receipt.json");assert receipt["terminal"] and receipt["workerDone"] and receipt["callbacksQuiescent"]
  files=sorted(p.glob("media.mkv.attempt-*.mkv"));assert 1<=len(files)<=8 and len(files)==receipt["mediaCalls"]
  if require_recovery and p.name in ("retry-once","retry-outage"):assert len(files)>=2,"SDK recovery did not create a replacement upload"
  attempts=[];all_reads=[];used=0;handles=[]
  for f in files:
   trace=[json.loads(line) for line in Path(str(f)+".transport.jsonl").read_text().splitlines()]
   assert len(trace)<=4101
   identity=trace[0];assert identity["event"]=="attempt" and identity["owner"]==1
   handles.append(identity["uploadHandle"]);assert identity["usedAtBegin"]>=used
   begin=next(e for e in trace if e["event"]=="begin");end=next(e for e in trace if e["event"]=="end")
   completion=next(e for e in trace if e["event"]=="completion")
   terminated=next((e for e in trace if e["event"]=="streamTerminated"),None)
   assert completion["uploadHandle"]==identity["uploadHandle"]
   reads=[e for e in trace if e["event"]=="read"];all_reads+=reads
   amount=sum(e["bytes"] for e in reads);assert amount==f.stat().st_size<=8*1024*1024
   used+=amount
   pacer=next(e for e in trace if e["event"]=="pacer");assert pacer["used"]>=used
   decode=read(Path(str(f)+".decode.json"))
   tracks={t["kind"]:{"decodedBuffers":int(t["decoded"]["buffers"]),"firstPtsNs":t["decoded"]["firstPtsNs"],"lastPtsNs":t["decoded"]["lastPtsNs"],"eos":t["decoded"]["eosObserved"]} for t in decode["tracks"]}
   row=dict(file=f.name,**identity,bytes=amount,curlCode=end["curlCode"],completion=completion,streamTerminated=terminated,decodeStatus=decode["status"],decodeReason=decode["reason"],tracks=tracks,continuousCoverageClaim=False,sourceTailKnown=False)
   selected=Path(str(f)+".selected.json")
   if selected.exists():row["selectedQueries"]=read(selected).get("frameSelections",{}).get("queries",[])
   if any(e["event"]=="injectedDisconnect" for e in trace):
    assert end["curlCode"]==28 and completion["httpStatus"]==0 and completion["callResult"]==599 and not completion["terminating"]
    row["retryableTimeoutObserved"]=True
    if (root/"termination-observer.txt").exists():assert terminated is not None and terminated["callResult"]==599
   attempts.append(row)
  assert len(set(handles))==len(handles)
  assert used==receipt["attemptedPayloadBytes"] and receipt["parsedSimulatedAcks"]==0
  events=list(csv.DictReader((p/"media.mkv.events.csv").open()))
  assert not any(int(e["kind"])==2 for e in events)
  accepted=[e for e in events if int(e["kind"])==1 and int(e["track"])==1 and int(e["result"])==0]
  targets=sorted({str(int(e["sdkPts100ns"])//10000*1000000) for e in accepted},key=int)
  targets=[targets[i] for i in sorted({round(i*(len(targets)-1)/min(47,len(targets)-1)) for i in range(min(48,len(targets)))})] if len(targets)>1 else targets
  assert targets
  if not check_only:(p/"selected-pts.txt").write_text(",".join(targets))
  freq=begin["frequency"];rate=pacer["rateBytesPerSecond"];cap=pacer["creditCapBytes"];quantum=pacer["quantumBytes"]
  assert all(0<e["bytes"]<=quantum for e in all_reads)
  all_reads.sort(key=lambda e:e["qpc"])
  peak=lambda seconds:max((sum(y["bytes"] for y in all_reads if x["qpc"]<=y["qpc"]<x["qpc"]+freq*seconds) for x in all_reads),default=0)
  peak100=peak(.1);peak1=peak(1);assert peak100<=cap+rate/10+64 and peak1<=cap+rate+64
  if p.name=="retry-control":assert len(attempts)==1 and attempts[0]["curlCode"]==0 and attempts[0]["decodeStatus"]=="decoded" and set(attempts[0]["tracks"])=={"video","audio"}
  if require_recovery and p.name=="retry-once":
   assert attempts[0]["streamTerminated"]["status"]==0 and attempts[1]["decodeStatus"]=="decoded"
   if "selectedQueries" in attempts[1]:assert any(int(q["matchCount"])>0 for q in attempts[1]["selectedQueries"] if int(q["targetPtsNs"])>=int(targets[len(targets)//2]))
  stop=None
  if p.name=="retry-outage":
   stop=next(json.loads(line) for line in (p/"fixture.log").read_text().splitlines() if line.startswith('{"outageStopRequested"'))
   assert stop["outageStopRequested"]
   stop["withinEightSecondQuitGrace"]=stop["stopToTerminalMilliseconds"]<=8000
  observed={target:[a["attempt"] for a in attempts if any(q["targetPtsNs"]==target and int(q["matchCount"])>0 for q in a.get("selectedQueries",[]))] for target in targets}
  result[p.name]=dict(mediaCalls=receipt["mediaCalls"],sdkRetried=len(attempts)>1,attempts=attempts,simulatedAcks=0,sharedSenderRateBytesPerSecond=rate,creditBytes=cap,peak100msBytes=peak100,peak1sBytes=peak1,sourceSampleTargetsNs=targets,observedSampleMatches=observed,replayedObservedSampleTargetsNs=[t for t,m in observed.items() if len(m)>1],unobservedSampleTargetsNs=[t for t,m in observed.items() if not m],sampledOnly=True,crossFileNavigationAuthority=False,stop=stop,recoveryRequired=require_recovery,nativeReason=receipt["reason"],gaps=receipt["gaps"])
 if not check_only:(root/"measurements.json").write_text(json.dumps(result,indent=2))
 print(json.dumps({k:{"attempts":v["mediaCalls"],"sdkRetried":v["sdkRetried"],"peak100msBytes":v["peak100msBytes"],"decodes":[a["decodeStatus"] for a in v["attempts"]],"sampleDuplicates":len(v["replayedObservedSampleTargetsNs"])} for k,v in result.items()},indent=2))
 sys.exit(0)
result={}
paced=(root/"policy.json").exists()
names=("baseline-noack","stall-noack","stop-wait") if paced else ("baseline","stall","baseline-noack","stall-noack") if (root/"stall-noack").exists() else ("baseline","stall","timeout")
names=tuple(n for n in names if (root/n).exists())
for name in names:
 p=root/name
 trace=[json.loads(s) for s in (p/"media.mkv.transport.jsonl").read_text().splitlines()]
 assert len(trace)<=4098 and trace[-1]["event"]=="end"
 reads=[r for r in trace if r["event"]=="read"]
 assert all(r["bytes"]>0 and r["bytes"]<=16384 for r in reads)
 assert sum(r["bytes"] for r in reads)==(p/"media.mkv").stat().st_size<=8*1024*1024
 assert all(reads[i]["qpc"]>=reads[i-1]["qpc"] for i in range(1,len(reads)))
 freq=trace[0]["frequency"];origin=trace[0]["qpc"]
 peak=max((sum(y["bytes"] for y in reads if x["qpc"]<=y["qpc"]<x["qpc"]+freq/10) for x in reads),default=0)
 receipt=read(p/"receipt.json")
 timed=[r for r in trace if "qpc" in r]
 assert (reads or name=="stop-wait") and all(timed[i]["qpc"]>=timed[i-1]["qpc"] for i in range(1,len(timed)))
 assert receipt["terminal"] and receipt["workerDone"] and receipt["callbacksQuiescent"] and not read(p/"process.json")["ExitCode"]
 row=dict(transportCurlCode=trace[-1]["curlCode"],attemptedPayloadBytes=sum(r["bytes"] for r in reads),peak100msBytes=peak,peak100msEquivalentMbps=round(peak*80/1e6,3),mediaCalls=receipt["mediaCalls"],nativeReason=receipt["reason"],acceptedVideo=receipt["acceptedVideo"],acceptedAudio=receipt["acceptedAudio"],encodedIngressBytes=receipt["encodedBytes"],simulatedAcks=receipt["parsedSimulatedAcks"],resets=receipt["resets"],gaps=receipt["gaps"])
 if name.startswith("stall") or name=="timeout":
  stall=next(r for r in trace if r["event"]=="stall")
  row["stallStartSeconds"]=(stall["qpc"]-origin)/freq
 if name.startswith("baseline"): assert trace[-1]["curlCode"]==0 and not any(r["event"]=="stall" for r in trace)
 if name.startswith("stall"):
  resume=next(r for r in trace if r["event"]=="resume")
  assert 1.9<=(resume["qpc"]-stall["qpc"])/freq<=2.2 and (resume["qpc"]-origin)/freq>=2.75
  assert not any(stall["qpc"]<=r["qpc"]<resume["qpc"] for r in reads)
  row["stallDurationSeconds"]=(resume["qpc"]-stall["qpc"])/freq
  row["firstResume100msBytes"]=sum(r["bytes"] for r in reads if resume["qpc"]<=r["qpc"]<resume["qpc"]+freq/10)
  row["firstResume100msEquivalentMbps"]=round(row["firstResume100msBytes"]*80/1e6,3)
  events=list(csv.DictReader((p/"media.mkv.events.csv").open()))
  accepted=[e for e in events if int(e["kind"])==1 and int(e["track"])==1 and int(e["result"])==0]
  inside=[e for e in accepted if stall["qpc"]<=int(e["qpc"])<resume["qpc"]]
  minimum=20 if (root/"profile.txt").exists() and (root/"profile.txt").read_text(encoding="utf-8-sig").strip()=="low" else 30
  assert len(inside)>=minimum
  row["videoSDKAcceptancesDuringStall"]=len(inside)
  # Quantize to MKV's explicitly configured 1ms scale; query actual generated
  # frame timestamps, never assume every frame between endpoints was decoded.
  groups=[[e for e in accepted if int(e["qpc"])<stall["qpc"]],inside,[e for e in accepted if int(e["qpc"])>=resume["qpc"]]]
  selected=[str(int(g[len(g)//2]["sdkPts100ns"])//10000*1000000) for g in groups]
  (p/"selected-pts.txt").write_text(",".join(selected))
  row["selectedBeforeDuringAfterStallPtsNs"]=selected
 if name=="timeout":
  assert any(r["event"]=="injectedTimeout" for r in trace)
  assert not any(r["event"]=="resume" for r in trace)
  assert trace[-1]["curlCode"]==28
  row["injectedCurlTimeoutCode"]=28
 elif name!="stop-wait":
  assert receipt["mediaCalls"]==1
  d=read(p/"decode.json")
  assert d["status"]=="decoded" and d["decodeEos"] and len(d["tracks"])==2
  row["decoded"]={t["kind"]:dict(buffers=int(t["decoded"]["buffers"]),eos=t["decoded"]["eosObserved"]) for t in d["tracks"]}
  if (root/"profile.txt").exists():
   profile=(root/"profile.txt").read_text(encoding="utf-8-sig").strip()
   width,height,fps=(1280,720,30) if profile=="standard" else (640,360,15 if profile=="low" else 30)
   tracks={t["kind"]:t for t in d["tracks"]}
   video=tracks["video"]["decoded"]["caps"];audio=tracks["audio"]["decoded"]["caps"]
   assert all(v in video for v in (f"width=(int){width}",f"height=(int){height}",f"framerate=(fraction){fps}/1"))
   assert "rate=(int)48000" in audio and "channels=(int)2" in audio and "codec_data=(buffer)1190" in tracks["audio"]["demux"]["caps"]
   if name.startswith("baseline"):
    encoded=list(csv.DictReader((p/"media.mkv.events.csv").open()));rates={}
    for track in (1,2):
     samples=[e for e in encoded if int(e["kind"])==11 and int(e["track"])==track]
     duration=max(int(e["ptsNs"]) for e in samples)-min(int(e["ptsNs"]) for e in samples)
     rates[str(track)]=round(sum(int(e["bytes"]) for e in samples)*8e9/duration)
    assert 90000<=rates["2"]<=110000
    if profile=="low":assert 500000<=rates["1"]<=1100000
    elif profile=="standard":assert 1400000<=rates["1"]<=2200000
    row["observedEncodedBitsPerSecond"]=rates
 if name.startswith("stall") and (p/"selected-decode.json").exists():
  selected=read(p/"selected-decode.json")["frameSelections"]["queries"]
  assert len(selected)==3 and selected[0]["status"]=="unique_observed"
  row["selectedBeforeDuringAfterStallStatus"]=[q["status"] for q in selected]
  row["postResumeMediaDemonstrated"]=all(q["status"]=="unique_observed" for q in selected)
 if "noack" in name or name=="stop-wait":
  assert receipt["parsedSimulatedAcks"]==0
  assert not any(int(e["kind"])==2 for e in csv.DictReader((p/"media.mkv.events.csv").open()))
 if any("sdkStatus" in r for r in reads):
  aborts=[r for r in trace if r["event"]=="sdkAbort"]
  row["abortSdkStatuses"]=[{"status":hex(r["sdkStatus"]),"calls":r["sdkCalls"],"bytes":r["sdkBytes"]} for r in aborts]
  row["sdkReadStatuses"]=sorted({hex(r["sdkStatus"]) for r in trace if r.get("sdkCalls",0)})
  assert all(r["sdkCalls"]>0 and r["sdkStatus"]!=0xffffffff for r in reads)
  assert all(r["sdkStatus"]!=0xffffffff for r in aborts if r["sdkCalls"])
 if paced:
  pacer=next(r for r in trace if r["event"]=="pacer")
  assert pacer["used"]==sum(r["bytes"] for r in reads)
  rate=pacer.get("rateBytesPerSecond",294912);cap=pacer.get("creditCapBytes",4096);quantum=pacer.get("quantumBytes",4096)
  assert all(r["bytes"]<=quantum for r in reads)
  assert peak<=cap+rate/10+64
  peak_second=max((sum(y["bytes"] for y in reads if x["qpc"]<=y["qpc"]<x["qpc"]+freq) for x in reads),default=0)
  assert peak_second<=cap+rate+64
  row["pacing"]={"rateBytesPerSecond":rate,"creditCapBytes":cap,"quantumBytes":quantum,"traceToleranceBytes":64,"peak1sBytes":peak_second,**pacer}
  if name=="stop-wait": assert pacer["cancelled"]>=1 and pacer["foreignReadRejected"]==1 and trace[-1]["curlCode"]==42
 result[name]=row
stall_name="stall-noack" if paced else "stall"
baseline_name="baseline-noack" if paced else "baseline"
if stall_name in result:result["stallVsBaselinePeak100msRatio"]=round(result[stall_name]["peak100msBytes"]/result[baseline_name]["peak100msBytes"],3)
(root/"measurements.json").write_text(json.dumps(result,indent=2))
print(json.dumps({k:{"bytes":v["attemptedPayloadBytes"],"curlCode":v["transportCurlCode"],"gaps":v["gaps"],"decoded":v.get("decoded"),"recovery":v.get("postResumeMediaDemonstrated")} for k,v in result.items() if isinstance(v,dict)},indent=2))
