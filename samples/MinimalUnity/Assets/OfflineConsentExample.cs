using System;
using System.Collections;
using System.Text;
using System.Threading;
using Playreel.Client;
using Playreel.Gameplay;
using UnityEngine;
namespace Playreel.Sample {
public sealed class OfflineConsentExample : MonoBehaviour {
 bool consent; PlayreelRecorder recorder;
 void OnGUI(){
 GUILayout.BeginArea(new Rect(20,20,660,300),GUI.skin.box);
 GUILayout.Label("playreel: offline lifecycle sample");
 GUILayout.Label("No cloud, native runtime, Steam ticket or microphone is used here.");
 consent=GUILayout.Toggle(consent,"I understand a production session records game video/audio and uploads diagnostic logs.");
 GUI.enabled=consent&&recorder==null;
 if(GUILayout.Button("Run local consent / stop check")){
 recorder=gameObject.AddComponent<PlayreelRecorder>();
 recorder.Begin(consent,false,new OfflineFactory(),RecordingProfile.Standard().Freeze(),Application.dataPath,"sample");
 }
 GUI.enabled=true;
 GUILayout.Label(recorder==null?"Idle; package import did not start recording.":"State: "+recorder.State+"; result: "+(recorder.Failure??"waiting"));
 if(recorder!=null&&GUILayout.Button("Stop and remove recorder")){recorder.Stop();Destroy(recorder);recorder=null;}
 GUILayout.EndArea();
 }
 sealed class OfflineFactory:IRecordingFactory,IRecordingTransport,IRecordingTickets {
 public bool IsLive=>false;
 public RecordingDependencies CreateAfterConsent()=>new RecordingDependencies{
 Trust=new RecordingTrust(480,"012345678901","us-east-1","https://example.execute-api.us-east-1.amazonaws.com/","playreel-","example-playreel-uploads"),
 Issuer=this,Tickets=this,Runner=_=>new Runner()};
 public IRecordingHttpOperation Begin(RecordingHttpRequest request){request.Validate();return new Operation();}
 public IEnumerator Begin(string identity,Func<string,IEnumerator> consumer,Action<string> failure,CancellationToken cancellation){failure("ticket_unavailable");yield break;}
 }
 sealed class Operation:IRecordingHttpOperation {
 public bool IsDone=>true;
 public RecordingHttpResponse Response=>new RecordingHttpResponse(200,Encoding.UTF8.GetBytes("{\"protocolVersion\":1,\"serverUtc\":\""+DateTimeOffset.UtcNow.ToString("O")+"\",\"enabled\":false}"));
 public void Dispose(){}
 }
 sealed class Runner:IRecordingRunner {
 public bool Ready=>false;public bool Released=>true;public string Error=>null;public bool Degraded=>false;public uint? NativeState=>null;
 public void Begin(){}public void Tick(RecordingTiming timing,bool generating){}public void Stop(){}
 }
}
}
