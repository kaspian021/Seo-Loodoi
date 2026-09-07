export type SeoProject={id:string;name:string;baseUrl:string;normalizedHost:string;status:string;createdAt:string}
export type Crawl={id:string;status:string;pagesDiscovered:number;pagesCrawled:number;errors:number;startedAt?:string;finishedAt?:string;heartbeatAt?:string}
export type Issue={id:string;urlId?:string;ruleCode:string;severity:string;category:string;title:string;evidenceJson:string}
export type Score={overall:number;technical:number;indexability:number;onPage:number;content:number;links:number;structuredData:number;performance:number;international:number;security:number;version:string;createdAt:string}

type Tokens={accessToken:string;refreshToken:string;expiresIn:number}
export class ApiError extends Error{fields:Record<string,string[]>;constructor(message:string,fields:Record<string,string[]>={}){super(message);this.fields=fields}}
const base=(import.meta.env.VITE_API_URL as string|undefined)?.replace(/\/$/,'')??''
let refreshing:Promise<boolean>|null=null
export const session={
  hasToken:()=>Boolean(localStorage.getItem('loodoi.access')),
  clear:()=>{localStorage.removeItem('loodoi.access');localStorage.removeItem('loodoi.refresh')},
  save:(x:Tokens)=>{localStorage.setItem('loodoi.access',x.accessToken);localStorage.setItem('loodoi.refresh',x.refreshToken)},
}
async function refresh(){const token=localStorage.getItem('loodoi.refresh');if(!token)return false;try{const r=await fetch(`${base}/api/auth/refresh`,{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({refreshToken:token})});if(!r.ok)throw 0;session.save(await r.json());return true}catch{session.clear();return false}}
async function request<T>(path:string,init:RequestInit={},retry=true):Promise<T>{
  const headers=new Headers(init.headers);if(init.body&&!headers.has('Content-Type'))headers.set('Content-Type','application/json');const token=localStorage.getItem('loodoi.access');if(token)headers.set('Authorization',`Bearer ${token}`)
  const response=await fetch(`${base}${path}`,{...init,headers});
  if(response.status===401&&retry){refreshing??=refresh().finally(()=>refreshing=null);if(await refreshing)return request<T>(path,init,false)}
  if(!response.ok){let message=`خطای ${response.status}`;let fields:Record<string,string[]>={};try{const body=await response.json();message=body.error??body.detail??body.title??message;fields=body.errors??{}}catch{/**/}throw new ApiError(message,fields)}
  if(response.status===204)return undefined as T;return response.json() as Promise<T>
}
export const api={
  async login(email:string,password:string){const tokens=await request<Tokens>('/api/auth/login?useCookies=false',{method:'POST',body:JSON.stringify({email,password})},false);session.save(tokens)},
  async register(data:{fullName:string;email:string;companyName?:string;password:string;confirmPassword:string;acceptTerms:boolean;preferredLanguage:string}){await request('/api/account/register',{method:'POST',body:JSON.stringify(data)},false);await this.login(data.email,data.password)},
  projects:()=>request<SeoProject[]>('/api/seo/projects'),
  createProject:(name:string,baseUrl:string)=>request<SeoProject>('/api/seo/projects',{method:'POST',body:JSON.stringify({name,baseUrl})}),
  crawls:(projectId:string)=>request<Crawl[]>(`/api/seo/projects/${projectId}/crawls`),
  startCrawl:(projectId:string)=>request<Crawl>(`/api/seo/projects/${projectId}/crawls`,{method:'POST'}),
  crawlAction:(projectId:string,crawlId:string,action:'pause'|'resume'|'cancel')=>request<void>(`/api/seo/projects/${projectId}/crawls/${crawlId}/${action}`,{method:'POST'}),
  issues:(projectId:string)=>request<Issue[]>(`/api/seo/projects/${projectId}/issues`),
  score:async(projectId:string)=>{try{return await request<Score>(`/api/seo/projects/${projectId}/scores/latest`)}catch(e){if(e instanceof Error&&e.message.includes('404'))return null;throw e}},
}
