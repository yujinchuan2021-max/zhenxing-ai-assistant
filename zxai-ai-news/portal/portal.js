(() => {
  'use strict';
  const $ = id => document.getElementById(id);
  const embedded = !!window.chrome?.webview;
  const categories = ['', 'ai-models', 'ai-products', 'industry', 'paper', 'tip'];
  const words = {
    zh: {title:'枕星AI资讯',subtitle:'模型 · Agent · 产品 · 研究',aiSettings:'个人 AI',refresh:'刷新',search:'搜索',searchHint:'搜索模型、工具与资讯',more:'加载更多',sources:'本页资讯来源',sourceNote:'服务器每天北京时间 09:00 自动更新，客户端无需保持运行；未配置 AI 也可阅读。采集失败时保留仍有效的来源缓存。资讯保留来源和原文入口，摘要供快速了解，重要信息请核对原文。',footer:'枕星图吧AI助手',footerNote:'每日北京时间 09:00 更新 · 无需配置 AI',all:'全部',models:'AI 模型',products:'工具与产品',industry:'行业动态',paper:'研究进展',tip:'使用技巧',latest:'最新资讯',focus:'重点资讯',recent:'新近资讯',focusNote:'服务器整理',recentNote:'快速浏览 · 按发布时间',personalFocus:'你的关注',personalNote:'个人 AI 精选',original:'原文 ↗',details:'展开摘要',dateUnknown:'日期未提供',loading:'正在获取资讯…',failed:'暂时无法更新，已加载的资讯仍可阅读。',empty:'没有找到相关资讯。',aiWorking:'个人 AI 正在整理…',aiReady:'个人 AI 整理完成',aiFailed:'个人 AI 暂不可用，来源资讯可继续阅读。',aiBadge:'个人 AI 摘要',editorBadge:'中文整理',update:'更新于',cached:'正在显示最近缓存。',stale:'资讯已更新，请刷新后继续浏览。',noSummary:'来源未提供摘要，可直接阅读原文。',today:'今天',yesterday:'昨天'},
    en: {title:'Zhenxing AI News',subtitle:'Models · Agents · Products · Research',aiSettings:'Your AI',refresh:'Refresh',search:'Search',searchHint:'Search models, tools and news',more:'Load more',sources:'Sources on this page',sourceNote:'The server updates daily at 09:00 Beijing time; the client does not need to stay open, and no AI setup is required to read. If collection fails, source content is retained while its cache remains valid. Sources and original links are preserved; check the originals for important details.',footer:'枕星图吧AI助手',footerNote:'Daily at 09:00 Beijing time · No AI setup needed',all:'All',models:'AI models',products:'Tools and products',industry:'Industry',paper:'Research',tip:'Tips',latest:'Latest news',focus:'Highlights',recent:'Recent news',focusNote:'Server editorial',recentNote:'Quick view · By publication date',personalFocus:'Your highlights',personalNote:'Selected by your AI',original:'Original ↗',details:'Show summary',dateUnknown:'Date unavailable',loading:'Loading news…',failed:'News could not be updated. Previously loaded news is still available.',empty:'No matching news.',aiWorking:'Your AI is preparing summaries…',aiReady:'Your AI summaries are ready',aiFailed:'Your AI is unavailable. Source news is still available.',aiBadge:'Your AI summary',editorBadge:'Chinese summary',update:'Updated',cached:'Showing recently cached news.',stale:'News has been updated. Refresh to continue.',noSummary:'No summary provided. Read the original.',today:'Today',yesterday:'Yesterday'}
  };
  let language = new URLSearchParams(location.search).get('lang') === 'en' ? 'en' : 'zh';
  let state = {query:{category:'',search:''},items:[],nextCursor:'',loading:true,failed:false,aiState:'unconfigured'};
  let controller, generation = 0, previousKey = '', itemSignature = '', hostStateSeen = false;
  const expanded = new Set();
  const text = key => words[language][key];
  const categoryText = id => text({'':'all','ai-models':'models','ai-products':'products',industry:'industry',paper:'paper',tip:'tip'}[id] || 'all');
  function element(tag, className, value) { const node=document.createElement(tag);if(className)node.className=className;if(value!=null)node.textContent=value;return node; }
  function safeUrl(raw) { try {const url=new URL(raw);return url.protocol==='https:'&&!url.username&&!url.password?url.href:null;}catch{return null;} }
  function parsedDate(raw) {const value=raw&&new Date(raw);return value&&!Number.isNaN(value.getTime())?value:null;}
  function date(raw) {return parsedDate(raw)?.toLocaleString(language==='en'?'en-US':'zh-CN',{year:'numeric',month:'2-digit',day:'2-digit',hour:'2-digit',minute:'2-digit'})||text('dateUnknown');}
  function dayKey(raw) {const value=parsedDate(raw);return value?[value.getFullYear(),String(value.getMonth()+1).padStart(2,'0'),String(value.getDate()).padStart(2,'0')].join('-'):'unknown';}
  function dayLabel(raw) {const value=parsedDate(raw);if(!value)return text('dateUnknown');const now=new Date(),yesterday=new Date(now);yesterday.setDate(now.getDate()-1);return dayKey(raw)===dayKey(now.toISOString())?text('today'):dayKey(raw)===dayKey(yesterday.toISOString())?text('yesterday'):value.toLocaleDateString(language==='en'?'en-US':'zh-CN',{month:'short',day:'numeric'});}
  function send(message) {if(embedded)window.chrome.webview.postMessage(message);}
  function link(item) {const node=element('a','read-original',text('original'));node.href=safeUrl(item.originalUrl)||'#';node.target='_blank';node.rel='noopener noreferrer';if(embedded)node.addEventListener('click',event=>{event.preventDefault();send({type:'original',id:item.id});});return node;}
  function titleLink(item) {const node=link(item);node.className='title-link';node.textContent=/^v?\d+\.\d+/.test(item.title)?item.source+' · '+item.title:item.title;return node;}
  function card(item,focus=false) {
    const article=element('article',focus?'focus-card':'news-card');article.dataset.id=item.id;
    const meta=element('div','article-meta');meta.append(element('span','category',categoryText(item.category)));
    if(!focus){meta.append(element('span',null,item.source));const when=parsedDate(item.publishedAt);meta.append(element('span',null,when?when.toLocaleTimeString(language==='en'?'en-US':'zh-CN',{hour:'2-digit',minute:'2-digit',hour12:false}):text('dateUnknown')));}
    article.append(meta);const heading=element('h3');heading.append(titleLink(item));article.append(heading);
    if(focus){article.append(element('div','meta',item.source+' · '+dayLabel(item.publishedAt)));return article;}
    article.append(element('p','summary',item.summary||text('noSummary')));
    if(item.reason&&item.serverEdited)article.append(element('p','reason',item.reason));
    const bottom=element('div','article-bottom');bottom.append(element('span',item.aiGenerated||item.serverEdited?'ai-badge':'',item.aiGenerated?text('aiBadge'):item.serverEdited?text('editorBadge'):''),link(item));article.append(bottom);
    if(item.summary?.length>150){const detail=element('details','article-details');detail.open=expanded.has(item.id);detail.append(element('summary',null,text('details')),element('p',null,item.summary));detail.addEventListener('toggle',()=>{if(detail.open)expanded.add(item.id);else expanded.delete(item.id);});article.append(detail);}
    return article;
  }
  function focusRows(items) {
    const personal=items.filter(i=>i.aiGenerated&&i.featured);
    const selected=items.filter(i=>i.serverEdited&&i.selected);
    if(personal.length)return {title:'personalFocus',note:'personalNote',rows:personal.slice(0,4)};
    if(selected.length)return {title:'focus',note:'focusNote',rows:selected.slice(0,4)};
    const threshold=Date.now()-7*86400000, seen=new Set();
    const recent=items.filter(i=>{const when=parsedDate(i.publishedAt);return when&&when.getTime()>=threshold&&when.getTime()<=Date.now()+86400000;});
    return {title:'recent',note:'recentNote',rows:recent.filter(i=>!seen.has(i.source)&&seen.add(i.source)).slice(0,4)};
  }
  function render(next) {
    const key=JSON.stringify(next.query), changedQuery=previousKey&&previousKey!==key;previousKey=key;state=next;
    document.documentElement.lang=language==='en'?'en':'zh-CN';document.title=text('title');
    document.querySelectorAll('[data-text]').forEach(node=>node.textContent=text(node.dataset.text));
    $('search').placeholder=text('searchHint');$('search').setAttribute('aria-label',text('search'));
    $('categories').replaceChildren(...categories.map(id=>{const button=element('button',null,categoryText(id));button.type='button';if(state.query.category===id)button.setAttribute('aria-current','page');button.onclick=()=>query(id,$('search').value);return button;}));
    const busy=state.loading||state.aiState==='working';$('refresh').disabled=busy;$('more').disabled=busy;$('more').hidden=!state.nextCursor;$('configure').hidden=!embedded;
    $('status').textContent=state.loading?text('loading'):state.failed?text('failed'):state.items.length===0?text('empty'):state.fromCache?text('cached'):'';
    $('update-time').textContent=state.retrievedAt?text('update')+' '+date(state.retrievedAt):'';
    $('list-title').textContent=state.query.category?categoryText(state.query.category):text('latest');
    $('loaded-count').textContent=language==='en'?state.items.length+' loaded':'已加载 '+state.items.length+' 条';
    const signature=JSON.stringify([language,key,state.items]);
    if(signature!==itemSignature){
      itemSignature=signature;const offset=changedQuery?0:window.scrollY;
      const ordered=[...state.items].sort((a,b)=>(parsedDate(b.publishedAt)?.getTime()||0)-(parsedDate(a.publishedAt)?.getTime()||0));
      const focus=focusRows(ordered);$('focus-section').hidden=!!state.query.category||!!state.query.search||!focus.rows.length;
      $('focus-title').textContent=text(focus.title);$('focus-note').textContent=text(focus.note);$('lead').replaceChildren(...focus.rows.map(i=>card(i,true)));
      const groups=new Map();for(const item of ordered){const key=dayKey(item.publishedAt);if(!groups.has(key))groups.set(key,[]);groups.get(key).push(item);}
      $('cards').replaceChildren(...[...groups].map(([key,items])=>{const group=element('section','day-group');const heading=element('div','day-heading');heading.append(element('h3',null,dayLabel(items[0].publishedAt)),element('span',null,key==='unknown'?'':key));const rows=element('div','day-items');rows.append(...items.map(i=>card(i)));group.append(heading,rows);return group;}));
      $('sources').replaceChildren(...[...new Set(state.items.map(i=>i.source))].map(source=>element('span',null,source)));
      requestAnimationFrame(()=>window.scrollTo({top:offset,behavior:'instant'}));
    }else if(changedQuery)window.scrollTo({top:0,behavior:'instant'});
    $('ai-status').hidden=!embedded||!['working','ready','failed'].includes(state.aiState);
    $('ai-status').textContent=state.aiState==='working'?text('aiWorking'):state.aiState==='failed'?text('aiFailed'):text('aiReady');
  }
  async function read(category='',search='',more=false) {
    controller?.abort();controller=new AbortController();const current=++generation;
    const same=state.query.category===category&&state.query.search===search,before=state;
    render({...state,query:{category,search},items:same?state.items:[],nextCursor:same?state.nextCursor:'',loading:true,failed:false});
    const params=new URLSearchParams({mode:'all',limit:'20',category,q:search});if(more)params.set('cursor',before.nextCursor);
    try{
      const response=await fetch('/api/ai-news/v1/items?'+params,{signal:controller.signal});if(!response.ok)throw new Error(response.status===409?'stale':'failed');
      const batch=await response.json();if(current!==generation)return;if(batch.schemaVersion!==1||!Array.isArray(batch.items)||!batch.page)throw new Error('failed');
      const rows=batch.items.filter(i=>i.id&&i.title&&safeUrl(i.links?.original)).map(i=>({id:i.id,title:i.title,summary:i.summary||'',source:i.source?.name||'',originalUrl:i.links.original,publishedAt:i.publishedAt,category:i.category,aiGenerated:false,featured:false,serverEdited:i.editorial?.status==='ready',selected:i.selected===true,reason:i.reason||''}));
      const merged=more?[...before.items,...rows]:rows,seen=new Set();
      render({query:{category,search},items:merged.filter(i=>!seen.has(i.id)&&seen.add(i.id)),nextCursor:batch.page.hasMore?batch.page.nextCursor||'':'',loading:false,failed:false,retrievedAt:batch.updatedAt,aiState:'unconfigured'});
    }catch(error){if(current!==generation||error.name==='AbortError')return;render({...state,nextCursor:error.message==='stale'?'':state.nextCursor,loading:false,failed:true});if(error.message==='stale')$('status').textContent=text('stale');}
  }
  function query(category='',search='',type='query'){search=search.trim().slice(0,120);if(embedded)send({type,category,search});else read(category,search);}
  $('refresh').onclick=()=>query(state.query.category,$('search').value,'refresh');
  $('more').onclick=()=>{if(embedded)send({type:'more'});else read(state.query.category,state.query.search,true);};
  $('configure').onclick=()=>send({type:'configure'});
  $('search-form').onsubmit=event=>{event.preventDefault();query(state.query.category,$('search').value);};
  if(embedded){window.chrome.webview.addEventListener('message',event=>{const value=event.data;if(!value||value.type!=='state'||!Array.isArray(value.items)||!value.query)return;if(!hostStateSeen){$('search').value=value.query.search||'';hostStateSeen=true;}language=value.language==='en-US'?'en':'zh';if(value.theme==='light'||value.theme==='dark')document.documentElement.dataset.theme=value.theme;render(value);});render(state);send({type:'ready'});}else{render(state);read();}
})();
