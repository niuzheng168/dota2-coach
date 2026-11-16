import {loadMinimapIcons,drawCampIcon} from './simulator-icons.js';
const $=id=>document.getElementById(id),canvas=$('route-map'),ctx=canvas.getContext('2d');
const token=new URLSearchParams(location.hash.slice(1)).get('token')||sessionStorage.getItem('dota-sandbox-token');
if(location.hash){sessionStorage.setItem('dota-sandbox-token',token);history.replaceState(null,'',location.pathname+location.search);}
const studyMode=new URLSearchParams(location.search).get('study')==='100',fullMatches=new Map();
let galleryPage=0,selectionGeneration=0;
let corpus,context,icons,background,current,range,cursor,playing=false,camera={x:0,y:0,zoom:1},drag=null,hover=null;
const clock=t=>`${String(Math.floor(t/60)).padStart(2,'0')}:${(t%60).toFixed(1).padStart(4,'0')}`;
const campName=id=>id.replace('neutralcamp_good_','R').replace('neutralcamp_evil_','D');
const campKinds={small:'小野',medium:'中野',large:'大野',ancient:'远古'};
const itemNames={item_power_treads:'动力鞋',item_mask_of_madness:'疯狂面具',item_manta:'分身斧',item_black_king_bar:'黑皇杖',
  item_greater_crit:'代达罗斯',item_butterfly:'蝴蝶',item_satanic:'撒旦之邪力',item_tpscroll:'TP',item_magic_wand:'魔棒',
  item_circlet:'圆环',item_yasha:'夜叉',item_lesser_crit:'水晶剑',item_quelling_blade:'补刀斧',item_dragon_lance:'魔龙枪',
  item_khanda:'Khanda',item_lifesteal:'吸血面具',item_infused_raindrop:'凝魂之露',item_broadsword:'阔剑',item_swift_blink:'迅捷跳刀'};
const abilityNames={luna_lucent_beam:'月光',luna_lunar_orbit:'环月',luna_moon_glaive:'月刃',luna_lunar_blessing:'月之祝福',luna_eclipse:'月蚀'};
const pretty=name=>itemNames[name]||String(name||'未识别').replace(/^item_/,'').replaceAll('_',' ');
const rankLabel=m=>m.tournament?'职业赛事':m.matchMmr!=null?`${m.matchMmr} MMR`:`${m.rankLabel||'排名未提供'}（MMR 未提供）`;
async function api(url){const r=await fetch(url,{headers:{Authorization:`Bearer ${token||''}`}});if(!r.ok)throw Error(`HTTP ${r.status}`);return r.json();}
function atBefore(rows,time){let lo=0,hi=rows.length;while(lo<hi){const mid=(lo+hi)>>1;if((rows[mid].time??rows[mid][0])<=time)lo=mid+1;else hi=mid;}return rows[Math.max(0,lo-1)];}
function windowFor(match){
  const requested=$('replay-window').value==='all'?[0,match.duration]:$('replay-window').value.split(',').map(Number);
  const a=match.frames.find(f=>f.time>=requested[0]),b=match.frames.find(f=>f.time>=requested[1])||match.frames.at(-1);
  if(!a||b.time<=a.time)return null;
  const elapsed=b.time-a.time,lane=b.gold[0]-a.gold[0],neutral=b.gold[1]-a.gold[1];
  return {from:a.time,to:b.time,elapsed,lane,neutral,rate:(lane+neutral)*60/elapsed,lastHits:b.lastHits-a.lastHits,stacks:b.stacks-a.stacks};
}
function projection(point,element=canvas,cam=camera){
  const span=Math.max(context.map.bounds.max.x-context.map.bounds.min.x,context.map.bounds.max.y-context.map.bounds.min.y);
  const size=element.clientWidth;
  return {x:size/2+(point.x-cam.x)*size/span*cam.zoom,y:size/2-(point.y-cam.y)*size/span*cam.zoom};
}
function fit(element){
  const size=element.clientWidth,dpr=devicePixelRatio||1;
  if(element.width!==Math.round(size*dpr)||element.height!==Math.round(size*dpr)){element.width=Math.round(size*dpr);element.height=Math.round(size*dpr);}
  const c=element.getContext('2d');c.setTransform(dpr,0,0,dpr,0,0);return c;
}
function routeColor(time,r){
  const t=Math.max(0,Math.min(1,(time-r.from)/(r.to-r.from)));
  return `rgb(${Math.round(87+168*t)},${Math.round(217-33*t)},${Math.round(197-105*t)})`;
}
function mapBase(c,element,cam){
  const size=element.clientWidth;c.fillStyle='#0d1920';c.fillRect(0,0,size,size);
  const o=context.map.overview,a=projection({x:o.posX,y:o.posY},element,cam),b=projection({x:o.posX+o.coordinateSpan,y:o.posY-o.coordinateSpan},element,cam);
  if(background){c.globalAlpha=.55;c.drawImage(background,a.x,a.y,b.x-a.x,b.y-a.y);c.globalAlpha=1;}
}
function drawRoute(c,element,cam,match,r,end,mini=false){
  let previous=null;
  for(const point of match.trace){
    if(point[0]<r.from||point[0]>r.to){previous=null;continue;}
    const q=projection({x:point[1],y:point[2]},element,cam);
    if(previous&&point[3]&&previous.point[3]&&!point[4]){
      c.beginPath();c.moveTo(previous.q.x,previous.q.y);c.lineTo(q.x,q.y);
      c.strokeStyle=routeColor(point[0],r);c.lineWidth=mini?1.5:2.2;c.globalAlpha=point[0]<=end ? .9 : .12;c.stroke();
    }
    previous={point,q};
  }
  c.globalAlpha=1;
}
function renderGallery(){
  for(const button of $('route-gallery').children){
    const match=corpus.matches.find(m=>m.matchId===Number(button.dataset.id)),r=windowFor(match),mini=button.querySelector('canvas');
    const c=fit(mini),cam={x:0,y:0,zoom:1};mapBase(c,mini,cam);
    button.setAttribute('aria-pressed',String(match.matchId===current?.matchId));
    if(!r){button.querySelector('p').textContent='比赛未覆盖该观察窗口';button.querySelector('small').textContent=`实际时长 ${clock(match.duration)}`;continue;}
    drawRoute(c,mini,cam,match,r,r.to,true);
    button.querySelector('p').textContent=`兵线 ${r.lane} + 野怪 ${r.neutral} 金`;
    button.querySelector('small').textContent=`${clock(r.from)}–${clock(r.to)} · 线野 ${r.rate.toFixed(1)} 金/分`;
  }
}
function draw(){
  if(!current)return;
  fit(canvas);mapBase(ctx,canvas,camera);const frame=atBefore(current.frames,cursor);
  context.map.camps.forEach((camp,i)=>{
    const q=projection(camp);
    drawCampIcon(ctx,icons,camp.kind,q,12+camera.zoom,{hasNeutrals:frame.campCounts[i]>0,spawnBlocked:false});
  });
  drawRoute(ctx,canvas,camera,current,range,cursor);
  if($('show-kills').checked)for(const kill of current.kills){
    if(kill.time<range.from||kill.time>cursor||kill.x===null||kill.y===null)continue;
    const q=projection(kill);ctx.fillStyle=kill.kind==='neutral'?'#e9c77f':'#8be3cd';
    ctx.globalAlpha=kill.illusion ? .35 : .7;ctx.fillRect(q.x-1.5,q.y-1.5,3,3);
  }ctx.globalAlpha=1;
  const point=atBefore(current.trace,cursor),q=projection({x:point[1],y:point[2]});
  const portrait=icons.heroes.npc_dota_hero_luna;
  ctx.globalAlpha=point[3]?1:.45;ctx.drawImage(portrait,q.x-14,q.y-14,28,28);ctx.globalAlpha=1;
  // Facing was not part of these raw extracts. Do not invent an orientation
  // arrow from displacement and present it as the player's facing.
  tooltip(frame);
}
function tooltip(frame){
  const box=$('route-tooltip');box.hidden=true;if(!hover)return;
  const nearCamp=context.map.camps.map((c,i)=>({c,i,q:projection(c)})).find(({q})=>Math.hypot(q.x-hover.x,q.y-hover.y)<11);
  let text;
  if(nearCamp){
    const {c,i}=nearCamp,kills=current.kills.filter(k=>k.campId===c.id&&k.time>=range.from&&k.time<=range.to);
    text=`${campName(c.id)} · ${campKinds[c.kind]}\n${clock(frame.time)}：所属活野怪 ${frame.campCounts[i]} 只\n窗口已关联击杀 ${kills.length} 只（不是到访/清空次数）`;
  }else{
    let best=null,d=10;
    for(const p of current.trace){
      if(p[0]<range.from||p[0]>range.to||!p[3])continue;
      const q=projection({x:p[1],y:p[2]}),distance=Math.hypot(q.x-hover.x,q.y-hover.y);
      if(distance<d){best=p;d=distance;}
    }
    if(best){const f=atBefore(current.frames,best[0]);text=`实录 ${clock(best[0])} · 等级 ${f.level}\n位置 ${Math.round(best[1])}, ${Math.round(best[2])}\n本体轨迹；不以附近野点推断击杀`; }
  }
  if(!text)return;box.textContent=text;box.hidden=false;
  box.style.left=`${Math.max(5,Math.min(canvas.clientWidth-box.offsetWidth-5,hover.x+15))}px`;
  box.style.top=`${Math.max(5,Math.min(canvas.clientHeight-box.offsetHeight-5,hover.y+15))}px`;
}
function snapshot(){
  if(!current)return;
  const f=atBefore(current.frames,cursor),loadout=current.loadouts[f.loadout];
  $('replay-clock').textContent=clock(cursor);$('replay-time').value=cursor;
  $('sample-time').textContent=`记录采样 ${clock(f.time)}`;
  $('replay-level').textContent=`Lv ${f.level}${f.alive?'':' · 已死亡'}`;
  $('replay-hero').textContent=`生命 ${f.hp} · 魔法 ${Math.round(f.mana)} · 累计补刀 ${f.lastHits}\n累计兵线 ${f.gold[0]} / 野怪 ${f.gold[1]} 金`;
  $('replay-items').replaceChildren(...loadout.items.map(i=>{const s=document.createElement('span');s.textContent=pretty(i.name);s.className=i.slot>5?'slot-extra':'';s.title=`实录装备栏 ${i.slot}`;return s;}));
  $('replay-abilities').replaceChildren(...loadout.abilities.filter(a=>a.name.startsWith('luna_')).map(a=>{
    const s=document.createElement('span');s.textContent=`${abilityNames[a.name]||a.name} ${a.level}`;return s;
  }));
  draw();
}
async function select(){
  if(!corpus||!context)return;
  const id=Number($('replay-match').value),generation=++selectionGeneration;
  playing=false;
  const meta=corpus.matches.find(m=>m.matchId===id);
  try{
    if(studyMode&&!fullMatches.has(id)){
      $('replay-status').textContent='正在加载本场完整细粒度轨迹…';
      const loaded=await api(`/api/farming-study/${id}`);
      if(loaded.matchId!==id)throw Error('回放标识不匹配');
      fullMatches.set(id,loaded);
      if(fullMatches.size>8)fullMatches.delete(fullMatches.keys().next().value);
    }
    if(generation!==selectionGeneration)return;
    current=studyMode?fullMatches.get(id):meta;
  }catch(error){if(generation===selectionGeneration)$('replay-status').textContent=`轨迹不可用：${error.message}`;return;}
  for(const option of $('replay-window').options)option.disabled=option.value!=='all'&&Number(option.value.split(',')[0])>=current.duration;
  if($('replay-window').selectedOptions[0]?.disabled)$('replay-window').value='all';
  range=windowFor(current);cursor=range.to;
  playing=false;$('replay-play').textContent='▶ 播放轨迹';hover=null;
  $('replay-time').min=range.from;$('replay-time').max=range.to;
  $('route-heading').textContent=`${current.player} · ${clock(range.from)}–${clock(range.to)}`;
  $('replay-player').textContent=current.player;$('replay-result').textContent=current.won?'胜利':'失利';$('replay-result').dataset.won=current.won;
  $('replay-meta').textContent=`${current.team==='radiant'?'天辉':'夜魇'} · ${rankLabel(current)}${current.matchMmr!=null?'（对局平均）':''}\n${current.startIso.replace('T',' ').slice(0,19)} UTC\n比赛 ${current.matchId} · 时长 ${clock(current.duration)}`;
  $('replay-source').href=current.source;
  $('replay-validation').textContent=`7.41f · build ${current.gameBuild} · 完整解析\n28 野点 / 22 塔坐标匹配，10 名玩家终局补刀核对通过。`;
  $('replay-lane-gold').textContent=range.lane;$('replay-neutral-gold').textContent=range.neutral;
  $('replay-farm-rate').textContent=range.rate.toFixed(1);$('replay-cs').textContent=`${range.lastHits} / ${range.stacks}`;
  const kills=current.kills.filter(k=>k.kind==='neutral'&&k.time>=range.from&&k.time<=range.to),groups=[];
  for(const kill of kills.filter(k=>k.campId)){
    const last=groups.at(-1);
    if(last&&last.id===kill.campId&&last.illusion===kill.illusion){last.to=kill.time;last.count++;}
    else groups.push({id:kill.campId,from:kill.time,to:kill.time,count:1,illusion:kill.illusion});
  }
  $('replay-camps').replaceChildren(...groups.map(g=>{
    const li=document.createElement('li'),small=document.createElement('span');
    li.textContent=`${campName(g.id)} · ${g.count} 次击杀${g.illusion?' · 幻象':''}`;
    small.textContent=`${clock(g.from)}–${clock(g.to)}`;li.append(small);return li;
  }));
  $('replay-unresolved').textContent=`未消歧 ${kills.filter(k=>!k.campId).length}`;
  $('model-study').hidden=!meta.review;
  if(meta.review){
    $('study-summary').textContent=meta.review.summary;
    $('study-cautions').replaceChildren(...[
      ...meta.review.counterexamples.map(c=>`${c.fact}（${c.evidenceIds.join('、')}）`),
      ...meta.review.unknowns,
    ].map(text=>{const li=document.createElement('li');li.textContent=text;return li;}));
    $('study-observations').replaceChildren(...meta.review.observations.map(o=>{
      const article=document.createElement('article'),fact=document.createElement('p'),interpretation=document.createElement('p'),question=document.createElement('p');
      fact.textContent=`实录：${o.fact}`;interpretation.textContent=`解读：${o.interpretation}`;question.textContent=`现场需判断：${o.coachQuestion}`;
      article.append(fact,interpretation,question);
      for(const evidence of o.evidenceIds){
        const number=Number(evidence.split(':w')[1]),button=document.createElement('button');button.textContent=`${clock(number*30)} 证据`;
        button.onclick=async()=>{$('replay-window').value='all';await select();cursor=Math.min(range.to,number*30);playing=false;snapshot();};
        article.append(button);
      }
      return article;
    }));
  }
  $('stack-study').hidden=!meta.stackingCase;
  if(meta.stackingCase){
    $('stack-proof').replaceChildren(...meta.stackingCase.events.map(event=>{
      const row=document.createElement('p');
      const relevant=event.camps.filter(c=>c.id.startsWith('neutralcamp_evil_'));
      row.textContent=`${clock(event.minute)}：${relevant.map(c=>`${campName(c.id)}，框外旧怪 ${c.oldAlive} + 新刷新 ${c.freshSpawns.length}`).join('；')}。`;
      const button=document.createElement('button');button.textContent='查看刷新前后';
      button.onclick=async()=>{$('replay-window').value='all';await select();cursor=event.minute-2;playing=true;$('replay-speed').value='5';$('replay-play').textContent='❚❚ 暂停';snapshot();};
      row.append(button);return row;
    }));
    const note=document.createElement('small');note.textContent='另有天辉营地同时刷新，不归因于 Luna。已核对旧怪存活、框外坐标和真实出生时刻；未提取完整仇恨/输入指令，不把本场秒数硬编码。';$('stack-proof').append(note);
  }
  snapshot();renderGallery();$('replay-status').textContent='已就绪：真实坐标与分项收益；只读研究，不启用实战路线。';
}
function buildGallery(){
  const pageSize=studyMode?12:6,list=corpus.matches.slice(galleryPage*pageSize,(galleryPage+1)*pageSize);
  $('route-gallery').replaceChildren();
  for(const match of list){
    const card=document.createElement('button');card.className='route-card';card.dataset.id=match.matchId;
    const header=document.createElement('header'),title=document.createElement('strong'),meta=document.createElement('span');
    title.textContent=match.player;meta.textContent=`${match.team==='radiant'?'天辉':'夜魇'} · ${rankLabel(match)}`;
    header.append(title,meta);card.append(header,document.createElement('canvas'),document.createElement('p'),document.createElement('small'));
    card.onclick=()=>{$('replay-match').value=match.matchId;select();};$('route-gallery').append(card);
  }
  $('gallery-page').textContent=` ${galleryPage+1} / ${Math.ceil(corpus.matches.length/pageSize)} `;
  $('gallery-prev').disabled=galleryPage===0;$('gallery-next').disabled=(galleryPage+1)*pageSize>=corpus.matches.length;
  if(current)renderGallery();
}
$('gallery-prev').onclick=()=>{galleryPage--;buildGallery();};
$('gallery-next').onclick=()=>{galleryPage++;buildGallery();};
const point=e=>{const r=canvas.getBoundingClientRect();return {x:e.clientX-r.left,y:e.clientY-r.top};};
canvas.onpointermove=e=>{
  if(!context||!current)return;
  if(drag){
    const span=Math.max(context.map.bounds.max.x-context.map.bounds.min.x,context.map.bounds.max.y-context.map.bounds.min.y);
    camera.x=drag.cam.x-(e.clientX-drag.x)*span/canvas.clientWidth/camera.zoom;
    camera.y=drag.cam.y+(e.clientY-drag.y)*span/canvas.clientHeight/camera.zoom;hover=null;
  }else hover=point(e);draw();
};
canvas.onpointerleave=()=>{hover=null;draw();};
canvas.onpointerdown=e=>{if(e.button===1){e.preventDefault();drag={x:e.clientX,y:e.clientY,cam:{...camera}};canvas.setPointerCapture(e.pointerId);}};
canvas.onpointerup=canvas.onpointercancel=()=>{drag=null;};
canvas.onwheel=e=>{e.preventDefault();camera.zoom=Math.max(1,Math.min(5,camera.zoom*(e.deltaY<0?1.2:1/1.2)));hover=null;draw();};
$('route-zoom-in').onclick=()=>{camera.zoom=Math.min(5,camera.zoom*1.3);draw();};
$('route-zoom-out').onclick=()=>{camera.zoom=Math.max(1,camera.zoom/1.3);draw();};
$('route-home').onclick=()=>{camera={x:0,y:0,zoom:1};draw();};
$('show-kills').onchange=draw;
$('replay-match').onchange=$('replay-window').onchange=select;
$('replay-time').oninput=()=>{playing=false;$('replay-play').textContent='▶ 播放轨迹';cursor=Number($('replay-time').value);snapshot();};
$('replay-play').onclick=()=>{if(!range)return;if(cursor>=range.to)cursor=range.from;playing=!playing;$('replay-play').textContent=playing?'❚❚ 暂停':'▶ 播放轨迹';snapshot();};
new ResizeObserver(()=>{draw();if(current)renderGallery();}).observe(canvas);
try{
  [corpus,context,icons]=await Promise.all([api(studyMode?'/api/farming-study':'/api/farming-replays'),api('/api/sim/context'),loadMinimapIcons()]);
  const response=await fetch('/api/map-overview',{headers:{Authorization:`Bearer ${token}`}});
  if(!response.ok)throw Error('核验底图不可用');background=await createImageBitmap(await response.blob());
  for(const match of corpus.matches){
    const option=document.createElement('option');option.value=match.matchId;option.textContent=`${match.player} · ${rankLabel(match)} · ${match.matchId}`;$('replay-match').append(option);
  }
  buildGallery();$('gallery-pagination').hidden=!studyMode;
  $('gallery-title').textContent=`${corpus.matches.length} 场路线对照`;
  $('corpus-status').textContent=studyMode?`100 场完整回放 · ${corpus.stats.professionalLabelled} 场职业标识 · 100 份模型复盘 · 7.41f`:`${corpus.matches.length} 场新下载天梯回放 · D2PT 职业标识 · 7.41f`;
  $('study-aggregate').hidden=!studyMode;
  if(studyMode)$('study-aggregate').textContent=`${corpus.stats.full10to20Windows} 场完整 10–20 分钟窗口：线野收入中位 ${corpus.stats.medianFarmPerMinute.toFixed(1)} 金/分 · 兵线占合计 ${(corpus.stats.pooledLaneFraction*100).toFixed(1)}% · 仅录像实录，不是教练实战 GPM`;
  for(const note of corpus.notes){const li=document.createElement('li');li.textContent=note;$('replay-notes').append(li);}
  await select();
  setInterval(()=>{if(playing){cursor=Math.min(range.to,cursor+Number($('replay-speed').value)*.2);if(cursor>=range.to){playing=false;$('replay-play').textContent='▶ 播放轨迹';}snapshot();}},200);
}catch(error){$('replay-status').textContent=`回放资料未就绪：${error.message}`;$('replay-status').className='error';}
