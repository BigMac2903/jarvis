import {useEffect,useState} from 'react';
import {api,errorText} from './api';
export const sections:Record<string,{label:string;fields:[string,string,string][]}>={
 ai:{label:'KI & Modelle',fields:[['enabled','Aktiviert','checkbox'],['protocol','Protokoll (responses oder chat)','text'],['baseUrl','API-Basis-URL','url'],['apiKey','API-Key (leer = beibehalten)','password'],['model','Aufgabenmodell','text'],['visionModel','Vision-Modell','text'],['realtimeModel','Realtime-Modell','text'],['embeddingModel','Embedding-Modell (optional, für Memory.Index)','text']]},
 internet:{label:'Internet & Research',fields:[['enabled','Websuche aktiv','checkbox'],['provider','Provider (brave oder searxng)','text'],['apiKey','Brave API-Key','password'],['searxUrl','SearXNG URL','url'],['browser','Browser aktiv','checkbox'],['automation','Browser-Automation aktiv','checkbox'],['deepResearch','Deep Research aktiv','checkbox'],['downloads','Downloads aktiv','checkbox'],['pdf','PDF-Lesen aktiv','checkbox'],['maxPages','Maximale Seiten','number'],['maxDurationSeconds','Maximale Dauer (Sekunden)','number'],['maxDownloadMb','Downloadlimit (MB)','number']]},
 google:{label:'Google Calendar & Gmail',fields:[['enabled','Verbindung aktiv','checkbox'],['clientId','OAuth Client-ID','text'],['clientSecret','OAuth Client-Secret','password']]},
 microsoft:{label:'Microsoft 365',fields:[['enabled','Verbindung aktiv','checkbox'],['clientId','Application Client-ID','text'],['clientSecret','Client-Secret','password']]},
 twilio:{label:'Twilio Telefonie',fields:[['enabled','Telefonie aktiv','checkbox'],['realtime','ConversationRelay Streaming (Twilio-Freischaltung erforderlich)','checkbox'],['accountSid','Account SID','text'],['authToken','Auth Token','password'],['number','Twilio Nummer (+49…)','tel'],['callerId','Verifizierte Caller-ID','tel']]},
 obsidian:{label:'Obsidian',fields:[['enabled','Vault verwenden','checkbox'],['writable','Schreibzugriff erlauben','checkbox']]},
 voice:{label:'Live Voice',fields:[['enabled','Audio an konfigurierten Anbieter übertragen','checkbox']]}
};
export function Fields({section,value,onChange}:{section:string;value:Record<string,any>;onChange:(v:Record<string,any>)=>void}){
 return <div className="fields">{sections[section].fields.map(([key,label,type])=><label key={key} className={type==='checkbox'?'check':''}>{type==='checkbox'?<><input type="checkbox" checked={!!value[key]} onChange={e=>onChange({...value,[key]:e.target.checked})}/>{label}</>:<>{label}<input type={type} autoComplete={type==='password'?'new-password':'off'} value={value[key]??''} onChange={e=>onChange({...value,[key]:type==='number'?(e.target.value===''?null:Number(e.target.value)):e.target.value})}/></>}</label>)}</div>
}
export default function Settings({initial='ai'}:{initial?:string}){
 const [all,setAll]=useState<Record<string,any>>({}),[section,setSection]=useState(initial),[message,setMessage]=useState(''),[busy,setBusy]=useState(false),[mfa,setMfa]=useState<any>(),[code,setCode]=useState('');
 useEffect(()=>{api('/settings').then(setAll).catch(e=>setMessage(errorText(e)))},[]);
 async function act(action:()=>Promise<unknown>){setBusy(true);try{await action();setMessage('Erfolgreich.')}catch(e){setMessage(errorText(e))}finally{setBusy(false)}}
 return <><div className="tabs">{Object.entries(sections).map(([k,s])=><button className={section===k?'selected':''} key={k} onClick={()=>setSection(k)}>{s.label}</button>)}</div>
 <section className="panel"><h2>{sections[section].label}</h2><Fields section={section} value={all[section]||{}} onChange={v=>setAll({...all,[section]:v})}/>
 {section==='obsidian'&&<p className="muted">Der Vault-Pfad wird beim Start als OBSIDIAN_HOST_PATH gemountet. Dateien bleiben auf deinem Server.</p>}
 {['google','microsoft'].includes(section)&&<p className="muted">OAuth-Weiterleitung: {location.origin}/api/v1/oauth/{section}/callback</p>}
 <div className="row"><button disabled={busy} className="primary" onClick={()=>act(async()=>{await api('/settings/'+section,'PUT',all[section]||{});const cleaned={...all[section]};['apiKey','clientSecret','authToken','password'].forEach(k=>delete cleaned[k]);setAll({...all,[section]:cleaned})})}>Speichern</button>
 {['ai','internet','google','microsoft','twilio'].includes(section)&&<button disabled={busy} onClick={()=>act(()=>api('/settings/'+section+'/test','POST',{}))}>Verbindung testen</button>}
 {['google','microsoft'].includes(section)&&<button disabled={busy} onClick={()=>act(async()=>{const r=await api('/oauth/'+section+'/start','POST',{});location.assign(r.url)})}>Konto verbinden</button>}</div><p role="status">{message}</p></section>
 <section className="panel"><h2>Zwei-Faktor-Anmeldung</h2><p className="muted">TOTP-Schlüssel in einer Authenticator-App speichern. Sichere zusätzlich dein verschlüsseltes Backup und den Master-Key.</p>
 {!mfa?<button onClick={()=>act(async()=>setMfa(await api('/auth/mfa/enroll','POST',{})))}>MFA einrichten</button>:<><code className="secret">{mfa.secret}</code><label>Bestätigungscode<input value={code} inputMode="numeric" onChange={e=>setCode(e.target.value)}/></label><button onClick={()=>act(async()=>{await api('/auth/mfa/confirm','POST',{code});setMfa(null)})}>MFA aktivieren</button></>}</section></>
}
