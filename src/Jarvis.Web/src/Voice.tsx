import {useEffect,useRef,useState} from 'react';
import {Mic,Square} from 'lucide-react';
import {api,csrf,errorText} from './api';
export default function Voice(){
 const[active,setActive]=useState(false),[status,setStatus]=useState('Bereit, wenn du es bist.'),[transcript,setTranscript]=useState<string[]>([]);
 const pc=useRef<RTCPeerConnection|null>(null),stream=useRef<MediaStream|null>(null),audio=useRef<HTMLAudioElement|null>(null),channel=useRef<RTCDataChannel|null>(null),request=useRef<AbortController|null>(null);
 const conversation=useRef(crypto.randomUUID());
 const[pushToTalk,setPushToTalk]=useState(false);
 function stop(){request.current?.abort();channel.current?.close();pc.current?.close();stream.current?.getTracks().forEach(t=>t.stop());if(audio.current){audio.current.pause();audio.current.srcObject=null}setActive(false);setStatus('Mikrofon ausgeschaltet.')}
 useEffect(()=>()=>stop(),[]);
 async function start(){
  try{
   setStatus('Mikrofon und Sprachverbindung starten …');const media=await navigator.mediaDevices.getUserMedia({audio:true});stream.current=media;
   if(pushToTalk)media.getAudioTracks()[0].enabled=false;
   const connection=new RTCPeerConnection();pc.current=connection;
   const output=new Audio();output.autoplay=true;audio.current=output;connection.ontrack=e=>{output.srcObject=e.streams[0]};
   connection.addTrack(media.getAudioTracks()[0]);const dc=connection.createDataChannel('oai-events');channel.current=dc;
   dc.onmessage=async event=>{
    const e=JSON.parse(event.data);
    if(e.type==='input_audio_buffer.speech_started'){setStatus('Ich höre zu …');request.current?.abort()}
    if(e.type==='response.output_audio_transcript.done')setTranscript(t=>[...t,'JARVIS: '+e.transcript]);
    if(e.type==='conversation.item.input_audio_transcription.completed')setTranscript(t=>[...t,'Du: '+e.transcript]);
    if(e.type==='response.function_call_arguments.done'&&e.name==='ask_jarvis'){
     const args=JSON.parse(e.arguments);setTranscript(t=>[...t,'Du: '+args.message]);setStatus('JARVIS arbeitet …');
     const controller=new AbortController();request.current=controller;
     try{const result=await api('/chat','POST',{message:args.message,conversation:conversation.current},controller.signal);
      dc.send(JSON.stringify({type:'conversation.item.create',item:{type:'function_call_output',call_id:e.call_id,output:JSON.stringify(result)}}));
      dc.send(JSON.stringify({type:'response.create'}));setStatus('JARVIS antwortet …');
     }catch(err){if(dc.readyState==='open'){dc.send(JSON.stringify({type:'conversation.item.create',item:{type:'function_call_output',call_id:e.call_id,output:JSON.stringify({error:errorText(err)})}}));dc.send(JSON.stringify({type:'response.create'}))}}
    }
    if(e.type==='error')setStatus(e.error?.message||'Sprachfehler');
   };
   const offer=await connection.createOffer();await connection.setLocalDescription(offer);
   const response=await fetch('/api/v1/voice/session',{method:'POST',headers:{'Content-Type':'application/sdp','X-CSRF-Token':csrf},body:offer.sdp});
   if(!response.ok)throw new Error((await response.json()).error||'Sprachverbindung fehlgeschlagen');
   await connection.setRemoteDescription({type:'answer',sdp:await response.text()});setActive(true);setStatus('Ich höre zu. Du kannst mich jederzeit unterbrechen.');
  }catch(e){stop();setStatus(errorText(e))}
 }
 return <section className="panel voice"><p className="eyebrow">LIVE VOICE</p><h1>Sprechen wir darüber.</h1><p className="muted">Audio wird erst nach deinem Klick an den aktivierten Sprachprovider übertragen.</p><label className="check"><input type="checkbox" disabled={active} checked={pushToTalk} onChange={e=>setPushToTalk(e.target.checked)}/>Push-to-talk</label><button className={'orb '+(active?'listening':'')} aria-label={active?'Mikrofon stoppen':'Sprachmodus starten'} onClick={active?stop:start}>{active?<Square size={34}/>:<Mic size={40}/>}</button>{active&&pushToTalk&&<button className="primary" onPointerDown={e=>{e.currentTarget.setPointerCapture(e.pointerId);const track=stream.current?.getAudioTracks()[0];if(track)track.enabled=true}} onPointerUp={()=>{const track=stream.current?.getAudioTracks()[0];if(track)track.enabled=false}} onPointerCancel={()=>{const track=stream.current?.getAudioTracks()[0];if(track)track.enabled=false}}>Zum Sprechen gedrückt halten</button>}<p role="status">{status}</p><div className="transcript">{transcript.map((line,i)=><p key={i}>{line}</p>)}</div></section>
}
