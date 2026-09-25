import {useState} from 'react';
import {api,errorText,setCsrf} from './api';
import {Fields,sections} from './Settings';
export default function Auth({setup,onDone}:{setup:boolean;onDone:()=>void}){
 const[step,setStep]=useState(0),[data,setData]=useState({token:'',username:'',password:'',email:'',timezone:Intl.DateTimeFormat().resolvedOptions().timeZone,language:'de',code:''});
 const[settings,setSettings]=useState<Record<string,any>>({ai:{enabled:false,protocol:'responses',baseUrl:'https://api.openai.com/v1'},internet:{enabled:false,provider:'brave',maxPages:12,maxDurationSeconds:180,maxDownloadMb:10},obsidian:{enabled:false,writable:false}});
 const[section,setSection]=useState('ai'),[error,setError]=useState(''),[busy,setBusy]=useState(false);
 async function submit(e:React.FormEvent){e.preventDefault();setError('');if(setup&&step===0){setStep(1);return}setBusy(true);try{
  if(setup)await api('/setup','POST',{...data,settings});
  const result=await api('/auth/login','POST',data);setCsrf(result.csrf);onDone();
 }catch(e){setError(errorText(e))}finally{setBusy(false)}}
 return <div className="auth"><div className="brand"><span className="brandmark">J</span>JARVIS<span className="badge">SELF-HOSTED</span></div><div className="authcard">
 <p className="eyebrow">{setup?'DEIN PERSÖNLICHER ASSISTENT':'WILLKOMMEN ZURÜCK'}</p><h1>{setup?'Ein Zuhause für deine KI.':'Alles an einem Ort.'}</h1>
 <p className="muted">{setup?'Richte den Administrator und deine Verbindungen ein. Integrationen kannst du jederzeit ergänzen.':'Melde dich an, um mit JARVIS weiterzuarbeiten.'}</p>
 <form onSubmit={submit}>{!setup||step===0?<div className="fields">
 {setup&&<label>Setup-Token aus .env<input required type="password" value={data.token} onChange={e=>setData({...data,token:e.target.value})}/></label>}
 <label>Benutzername<input required autoComplete="username" minLength={3} value={data.username} onChange={e=>setData({...data,username:e.target.value})}/></label>
 <label>Passwort<input required type="password" autoComplete={setup?'new-password':'current-password'} minLength={setup?12:1} value={data.password} onChange={e=>setData({...data,password:e.target.value})}/></label>
 {setup?<><label>E-Mail<input required type="email" value={data.email} onChange={e=>setData({...data,email:e.target.value})}/></label><label>Zeitzone<input required value={data.timezone} onChange={e=>setData({...data,timezone:e.target.value})}/></label></>:<label>MFA-Code (falls aktiviert)<input inputMode="numeric" autoComplete="one-time-code" value={data.code} onChange={e=>setData({...data,code:e.target.value})}/></label>}
 </div>:<><div className="tabs">{Object.entries(sections).map(([key,s])=><button type="button" className={section===key?'selected':''} key={key} onClick={()=>setSection(key)}>{s.label}</button>)}</div><Fields section={section} value={settings[section]||{}} onChange={value=>setSettings({...settings,[section]:value})}/><p className="muted">Ohne KI-Konfiguration funktioniert die Verwaltung; Chat benötigt einen erreichbaren Provider und ein Modell. OAuth-Konten werden nach der Einrichtung verbunden.</p></>}
 {error&&<p className="error" role="alert">{error}</p>}<div className="row">{setup&&step>0&&<button type="button" onClick={()=>setStep(0)}>Zurück</button>}<button className="primary" disabled={busy}>{busy?'Verbinde …':setup?(step===0?'Weiter zu Verbindungen':'JARVIS einrichten'):'Anmelden'}</button></div></form></div><p className="authfoot">Dein Server. Deine Daten. Deine Entscheidungen.</p></div>
}
