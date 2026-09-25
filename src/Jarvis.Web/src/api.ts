export let csrf='';
export function setCsrf(value:string){csrf=value}
export async function api<T=any>(path:string,method='GET',body?:unknown,signal?:AbortSignal):Promise<T>{
 const response=await fetch('/api/v1'+path,{method,credentials:'same-origin',headers:{'Content-Type':'application/json','X-CSRF-Token':csrf},body:body===undefined?undefined:JSON.stringify(body),signal});
 if(!response.ok){const error=await response.json().catch(()=>({error:'Verbindung fehlgeschlagen'}));throw new Error(error.error||'HTTP '+response.status)}
 const text=await response.text();return (text?JSON.parse(text):undefined) as T;
}
export type Doc={id:string;data:Record<string,any>;updatedAt:string};
export type Tool={name:string;description:string;risk:number;fields:Record<string,{type:string;description:string;required:boolean;choices?:string[]}>};
export function errorText(e:unknown){return e instanceof Error?e.message:'Unbekannter Fehler'}
