export type SeoProject={id:string;name:string;baseUrl:string;normalizedHost:string;status:string;createdAt:string;settings?:CrawlSettings}
export type CrawlSettings={maxPages:number;maxDepth:number;concurrency:number;delayMilliseconds:number;timeoutSeconds:number;retryCount:number;obeyRobots:boolean;followRedirects:boolean;includeSubdomains:boolean;maxResponseBytes:number;userAgent:string;schedule?:'off'|'daily'|'weekly'|'monthly';scheduleHourUtc?:number}
export type Profile={id:string;email:string;displayName:string;companyName?:string;preferredLanguage:string;registeredAt?:string;termsAcceptedAt?:string}
export type ProjectMember={id:string;userId:string;email:string;displayName:string;role:string;createdAt:string}
export type AlertRule={id:string;type:string;threshold:number;channel:string;destination?:string;isEnabled:boolean;createdAt:string}
export type AlertEvent={id:string;alertRuleId:string;eventType:string;payloadJson:string;detectedAt:string;isRead:boolean}
export type TwoFactorStatus={enabled:boolean;sharedKey?:string;recoveryCodes?:string[]}
export type Crawl={id:string;status:string;pagesDiscovered:number;pagesCrawled:number;errors:number;startedAt?:string;finishedAt?:string;heartbeatAt?:string}
export type Issue={id:string;urlId?:string;ruleCode:string;severity:string;category:string;title:string;description?:string;evidenceJson:string;status?:string;url?:string}
export type Score={overall:number;technical:number;indexability:number;onPage:number;content:number;links:number;structuredData:number;performance:number;international:number;security:number;version:string;createdAt:string}
export type Recommendation={id:string;issueId?:string;priority:number;title:string;explanation:string;evidenceJson:string;expectedImpact:string;effort:string;confidence:number;status:string;createdAt:string}
export type Keyword={id:string;phrase:string;language:string;country:string;isTracked:boolean;lastMetricAt?:string;clicks:number|null;impressions:number|null;ctr:number|null;averagePosition:number|null;bestPage?:string;source:string}
export type Opportunity={keywordId:string;phrase:string;impressions:number;clicks:number;ctr:number;averagePosition:number;pageUrl?:string;opportunityScore:number;reason:string}
export type Competitor={id:string;name:string;baseUrl:string;normalizedHost:string;isActive:boolean;lastCrawlAt?:string;createdAt:string}
export type Report={id:string;type:string;format:string;status:string;crawlId?:string;createdAt:string}
export type Usage={plan:string;maxProjects:number;projectsUsed:number;pagesPerMonth:number;pagesUsed:number;maxKeywords:number;keywordsUsed:number;maxCompetitors:number;competitorsUsed:number;periodStart:string}
export type AiResponse={summary:string;observations:string[];rootCauses:string[];recommendations:string[];actions:string[];confidence:number;missingEvidence:string[];provider:string;promptVersion:string}
export type SearchConsoleStatus={connected:boolean;provider:string;expiresAt?:string;status:string}

type Tokens={accessToken:string;refreshToken:string;expiresIn:number}
export class ApiError extends Error{fields:Record<string,string[]>;status:number;constructor(message:string,fields:Record<string,string[]>={},status=0){super(message);this.fields=fields;this.status=status}}
export class TwoFactorRequiredError extends Error{constructor(){super('Two-factor authentication is required.')}}
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
  if(!response.ok){let message=`خطای ${response.status}`;let fields:Record<string,string[]>={};try{const body=await response.json();message=body.error??body.detail??body.title??message;fields=body.errors??{}}catch{/**/}throw new ApiError(message,fields,response.status)}
  if(response.status===204||response.status===202)return undefined as T;const text=await response.text();return (text?JSON.parse(text):undefined) as T
}
export const api={
  login:async(email:string,password:string,twoFactorCode?:string,twoFactorRecoveryCode?:string)=>{try{const tokens=await request<Tokens>('/api/auth/login?useCookies=false',{method:'POST',body:JSON.stringify({email,password,...(twoFactorCode?{twoFactorCode}: {}),...(twoFactorRecoveryCode?{twoFactorRecoveryCode}: {})})},false);session.save(tokens)}catch(error){if(error instanceof ApiError&&/two[ -]?factor|2fa/i.test(error.message))throw new TwoFactorRequiredError();throw error}},
  forgotPassword:async(email:string)=>{await request('/api/auth/forgotPassword',{method:'POST',body:JSON.stringify({email})},false)},
  resetPassword:async(email:string,resetCode:string,newPassword:string,confirmPassword:string)=>{await request('/api/auth/resetPassword',{method:'POST',body:JSON.stringify({email,resetCode,newPassword,confirmPassword})},false)},
  register:async(data:{fullName:string;email:string;companyName?:string;password:string;confirmPassword:string;acceptTerms:boolean;preferredLanguage:string})=>{const result=await request<{requiresEmailConfirmation?:boolean}>('/api/account/register',{method:'POST',body:JSON.stringify(data)},false);if(result.requiresEmailConfirmation)return false;await api.login(data.email,data.password);return true},
  profile:()=>request<Profile>('/api/account/me'),
  updateProfile:(data:{displayName:string;companyName?:string;preferredLanguage:'fa'|'en'})=>request<Profile>('/api/account/me',{method:'PUT',body:JSON.stringify(data)}),
  twoFactor:()=>request<TwoFactorStatus>('/api/account/security/2fa'),
  updateTwoFactor:(data:{enable?:boolean;code?:string;resetAuthenticatorKey?:boolean;resetRecoveryCodes?:boolean})=>request<TwoFactorStatus>('/api/account/security/2fa',{method:'POST',body:JSON.stringify(data)}),
  projects:()=>request<SeoProject[]>('/api/seo/projects'),
  createProject:(name:string,baseUrl:string)=>request<SeoProject>('/api/seo/projects',{method:'POST',body:JSON.stringify({name,baseUrl})}),
  crawls:(projectId:string)=>request<Crawl[]>(`/api/seo/projects/${projectId}/crawls`),
  startCrawl:(projectId:string)=>request<Crawl>(`/api/seo/projects/${projectId}/crawls`,{method:'POST'}),
  crawlAction:(projectId:string,crawlId:string,action:'pause'|'resume'|'cancel')=>request<void>(`/api/seo/projects/${projectId}/crawls/${crawlId}/${action}`,{method:'POST'}),
  issues:(projectId:string,crawlId?:string)=>request<Issue[]>(`/api/seo/projects/${projectId}/issues${crawlId?`?crawlId=${crawlId}`:''}`),
  score:async(projectId:string)=>{try{return await request<Score>(`/api/seo/projects/${projectId}/scores/latest`)}catch(e){if(e instanceof ApiError&&e.status===404)return null;throw e}},
  scoreHistory:(projectId:string)=>request<Score[]>(`/api/seo/projects/${projectId}/scores/history`),
  dashboard:(projectId:string)=>request<Record<string,unknown>>(`/api/seo/projects/${projectId}/dashboard`),
  usage:()=>request<Usage>('/api/seo/usage'),
  settings:(projectId:string)=>request<CrawlSettings>(`/api/seo/projects/${projectId}/settings`),
  updateSettings:(projectId:string,data:Partial<CrawlSettings>)=>request<CrawlSettings>(`/api/seo/projects/${projectId}/settings`,{method:'PUT',body:JSON.stringify(data)}),
  pages:(projectId:string,crawlId?:string)=>request<{total:number;pages:Array<Record<string,unknown>>}>(`/api/seo/projects/${projectId}/pages${crawlId?`?crawlId=${crawlId}`:''}`),
  recommendations:(projectId:string)=>request<Recommendation[]>(`/api/seo/projects/${projectId}/recommendations`),
  updateRecommendation:(projectId:string,id:string,status:string)=>request<void>(`/api/seo/projects/${projectId}/recommendations/${id}`,{method:'PATCH',body:JSON.stringify({status})}),
  updateIssue:(projectId:string,id:string,status:string)=>request<void>(`/api/seo/projects/${projectId}/issues/${id}`,{method:'PATCH',body:JSON.stringify({status})}),
  keywords:(projectId:string)=>request<Keyword[]>(`/api/seo/projects/${projectId}/keywords`),
  addKeyword:(projectId:string,phrase:string)=>request<Keyword>(`/api/seo/projects/${projectId}/keywords`,{method:'POST',body:JSON.stringify({phrase,language:'fa',country:'IR',isTracked:true})}),
  deleteKeyword:(projectId:string,id:string)=>request<void>(`/api/seo/projects/${projectId}/keywords/${id}`,{method:'DELETE'}),
  opportunities:(projectId:string)=>request<Opportunity[]>(`/api/seo/projects/${projectId}/keywords/opportunities`),
  searchConsoleStatus:(projectId:string)=>request<SearchConsoleStatus>(`/api/seo/projects/${projectId}/search-console/status`),
  searchConsoleConnect:(projectId:string)=>request<{authorizationUrl:string}>(`/api/seo/projects/${projectId}/search-console/connect`),
  searchConsoleSync:(projectId:string,startDate:string,endDate:string)=>request<{rowsReceived:number;keywordsUpdated:number;partial:boolean}>(`/api/seo/projects/${projectId}/search-console/sync`,{method:'POST',body:JSON.stringify({startDate,endDate,country:'ALL',device:'ALL'})}),
  members:(projectId:string)=>request<ProjectMember[]>(`/api/seo/projects/${projectId}/members`),
  addMember:(projectId:string,email:string,role:'Viewer'|'Editor'|'Admin')=>request<ProjectMember>(`/api/seo/projects/${projectId}/members`,{method:'POST',body:JSON.stringify({email,role})}),
  updateMember:(projectId:string,id:string,role:'Viewer'|'Editor'|'Admin')=>request<void>(`/api/seo/projects/${projectId}/members/${id}`,{method:'PATCH',body:JSON.stringify({role})}),
  removeMember:(projectId:string,id:string)=>request<void>(`/api/seo/projects/${projectId}/members/${id}`,{method:'DELETE'}),
  alertRules:(projectId:string)=>request<AlertRule[]>(`/api/seo/projects/${projectId}/alerts/rules`),
  addAlertRule:(projectId:string,data:{type:string;threshold:number;channel:string;destination?:string})=>request<AlertRule>(`/api/seo/projects/${projectId}/alerts/rules`,{method:'POST',body:JSON.stringify(data)}),
  deleteAlertRule:(projectId:string,id:string)=>request<void>(`/api/seo/projects/${projectId}/alerts/rules/${id}`,{method:'DELETE'}),
  alertEvents:(projectId:string)=>request<AlertEvent[]>(`/api/seo/projects/${projectId}/alerts/events`),
  readAlert:(projectId:string,id:string)=>request<void>(`/api/seo/projects/${projectId}/alerts/events/${id}/read`,{method:'POST'}),
  checkAlerts:(projectId:string)=>request<{created:number}>(`/api/seo/projects/${projectId}/alerts/check`,{method:'POST'}),
  competitors:(projectId:string)=>request<Competitor[]>(`/api/seo/projects/${projectId}/competitors`),
  addCompetitor:(projectId:string,name:string,baseUrl:string)=>request<Competitor>(`/api/seo/projects/${projectId}/competitors`,{method:'POST',body:JSON.stringify({name,baseUrl})}),
  deleteCompetitor:(projectId:string,id:string)=>request<void>(`/api/seo/projects/${projectId}/competitors/${id}`,{method:'DELETE'}),
  ai:(projectId:string)=>request<AiResponse>(`/api/seo/projects/${projectId}/ai/analyze`,{method:'POST'}),
  reports:(projectId:string)=>request<Report[]>(`/api/seo/projects/${projectId}/reports`),
  createReport:(projectId:string,format:'json'|'csv'|'pdf')=>request<Report>(`/api/seo/projects/${projectId}/reports`,{method:'POST',body:JSON.stringify({type:'Executive',format})}),
  reportUrl:(projectId:string,id:string)=>`${base}/api/seo/projects/${projectId}/reports/${id}/download`,
  downloadReport:async(projectId:string,id:string)=>{const token=localStorage.getItem('loodoi.access');const response=await fetch(`${base}/api/seo/projects/${projectId}/reports/${id}/download`,{headers:token?{Authorization:`Bearer ${token}`}:{}});if(!response.ok)throw new ApiError(`خطای ${response.status}`,{},response.status);const blob=await response.blob();const url=URL.createObjectURL(blob);const anchor=document.createElement('a');anchor.href=url;anchor.download=`seo-loodoi-${id}`;document.body.appendChild(anchor);anchor.click();anchor.remove();window.setTimeout(()=>URL.revokeObjectURL(url),1000)},
}
