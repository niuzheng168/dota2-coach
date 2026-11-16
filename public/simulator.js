import {loadMinimapIcons,drawCampIcon,drawHeroIcon,heroMarkerSize} from './simulator-icons.js';
const $=id=>document.getElementById(id);
const params=new URLSearchParams(location.hash.slice(1));
const token=params.get('token')||sessionStorage.getItem('dota-sandbox-token');
if(params.has('token')){sessionStorage.setItem('dota-sandbox-token',token);history.replaceState(null,'','/simulator');}
let context,state,background,minimapIcons=null,selected=null,selectedCamp=null,playing=false,placing=false,queue=Promise.resolve(),busy=0,seeking=false,seekTimer;
let camera={x:0,y:0,zoom:1},drag=null;
let hoverPoint=null,hoveredCamp=null;
const canvas=$('sim-map'),ctx=canvas.getContext('2d');
const clock=t=>`${String(Math.floor(t/60)).padStart(2,'0')}:${(t%60).toFixed(1).padStart(4,'0')}`;
const fmt=n=>Number(n).toFixed(1);
const names={hero:'英雄',lane:'小兵',neutral:'野怪',tower:'防御塔'};
const campKinds={small:'小野',medium:'中野',large:'大野',ancient:'远古'};
const campName=c=>c.id.replace('neutralcamp_good_','R').replace('neutralcamp_evil_','D');
function label(u){
  if(u.kind==='hero')return context.heroes.find(h=>h.id===u.name)?.name||u.name;
  if(u.kind==='tower')return `${u.team==='radiant'?'天辉':'夜魇'} ${u.name.match(/tower(\d)/)?.[1]||''}塔 ${u.name.split('_').at(-1)}`;
  if(u.kind==='lane')return `${u.team==='radiant'?'天辉':'夜魇'}${u.projectileSpeed?'远程兵':'近战兵'}`;
  return u.name.replace('npc_dota_neutral_','');
}
function status(text,error=false){$('sim-status').textContent=text;$('sim-status').classList.toggle('error',error);}
async function api(url,body){
  const r=await fetch(url,{method:body===undefined?'GET':'POST',headers:{Authorization:`Bearer ${token||''}`,'Content-Type':'application/json'},
    body:body===undefined?undefined:JSON.stringify(body)});
  const value=await r.json();if(!r.ok)throw new Error(value.error||`HTTP ${r.status}`);return value;
}
function job(fn){
  busy++;const work=queue.then(fn,fn);queue=work.catch(()=>{});
  return work.catch(e=>{playing=false;$('play').textContent='▶ 播放';status(e.message,true);}).finally(()=>{busy--;});
}
function change(endpoint,body){
  return job(async()=>{
    state=await api('/api/sim/'+endpoint,{...body,revision:state?.timeline.revision});render();
    status(state.branched?'已建立新分支，原未来已舍弃。':endpoint==='seek'?'已定位到精确帧；金币与单位状态同步还原。':'模拟状态已更新。');
  });
}
const command=c=>change('command',{command:c});
function p(world){
  const b=context.map.bounds,w=canvas.clientWidth,h=canvas.clientHeight;
  const span=Math.max(b.max.x-b.min.x,b.max.y-b.min.y),scale=Math.min(w,h)/span*camera.zoom;
  return {x:w/2+(world.x-camera.x)*scale,y:h/2-(world.y-camera.y)*scale};
}
function worldPoint(x,y){
  const b=context.map.bounds,span=Math.max(b.max.x-b.min.x,b.max.y-b.min.y),r=canvas.getBoundingClientRect();
  const scale=Math.min(canvas.clientWidth,canvas.clientHeight)/span*camera.zoom;
  return {x:camera.x+(x-r.left-canvas.clientWidth/2)/scale,y:camera.y-(y-r.top-canvas.clientHeight/2)/scale};
}
function path(points,color,width=1){
  if(!points.length)return;ctx.beginPath();ctx.strokeStyle=color;ctx.lineWidth=width;
  points.forEach((v,i)=>{const q=p(v);i?ctx.lineTo(q.x,q.y):ctx.moveTo(q.x,q.y);});ctx.stroke();
}
function drawCampMarkers(active){
  if(!$('show-camps').checked)return;
  const statuses=new Map(state.camps.map(c=>[c.id,c]));
  for(const camp of context.map.camps){
    const c=statuses.get(camp.id),q=p(camp);
    if(q.x<0||q.x>canvas.clientWidth||q.y<0||q.y>canvas.clientHeight)continue;
    const size=16+Math.min(3,camera.zoom-1)*2;
    drawCampIcon(ctx,minimapIcons,camp.kind,q,size,c);
    if(camp.id===active){ctx.beginPath();ctx.arc(q.x,q.y,size/2+3,0,Math.PI*2);ctx.strokeStyle='#f5d27f';ctx.lineWidth=1;ctx.stroke();}
  }
}
function renderCamps(){
  const full=state.camps.filter(c=>c.hasNeutrals).length;
  $('camp-summary').textContent=`${state.camps.length} 野点 · 有怪 ${full} · 空 ${state.camps.length-full}`;
  $('camp-choice').value=selectedCamp||'';
  $('clear-camp').hidden=!selectedCamp;
}
function renderCampTooltip(id){
  const tooltip=$('camp-tooltip'),camp=context.map.camps.find(c=>c.id===id);
  tooltip.hidden=!camp;
  canvas.removeAttribute('aria-describedby');$('camp-choice').removeAttribute('aria-describedby');
  if(!camp)return;
  const c=state.camps.find(c=>c.id===id),anchor=p(camp);
  const w=canvas.clientWidth,h=canvas.clientHeight;
  // A pulled neutral may be hovered outside its original camp. Keep its
  // ownership, but put that preview next to the pointer rather than offscreen.
  const q=hoveredCamp===id&&hoverPoint?hoverPoint:anchor;
  if(q.x<0||q.x>w||q.y<0||q.y>h){tooltip.hidden=true;return;}
  tooltip.dataset.campId=id;tooltip.dataset.pinned=String(id===selectedCamp);
  $('camp-title').textContent=`${campName(c)} · ${campKinds[camp.kind]}`;
  $('camp-pin-state').textContent=id===selectedCamp?'已固定':'悬停预览';
  $('camp-availability').textContent=c.hasNeutrals?`有怪 · ${c.aliveCount} 只${c.outsideCount?'（拉出 '+c.outsideCount+'）':''}`:c.spawnBlocked?'空营地 · 占框':'空营地 · 未占框';
  $('camp-availability').dataset.state=c.hasNeutrals?'occupied':c.spawnBlocked?'blocked':'empty';
  $('camp-refresh').textContent=`${clock(c.nextSpawnTime)} · ${c.secondsUntilSpawn.toFixed(1)}s 后${c.nextSpawnTick>state.timeline.maxTick?'（时间轴外）':''}`;
  $('camp-gold-label').textContent=c.hasNeutrals?'当前剩余野怪收益':'下次生成一组 · 预计收益';
  $('camp-gold').textContent=c.hasNeutrals?`${c.currentGold} 金`:`≈ ${Math.round(c.nextSpawnGold.mean)} 金（${c.nextSpawnGold.min}–${c.nextSpawnGold.max}）`;
  $('camp-detail').textContent=[
    c.spawnBlocked?`刷新框内有 ${c.blockerIds.length} 个活单位；若保持到检查时，将跳过本次刷新。`:'刷新框未占用；检查时仍未占用才会生成。',
    c.outsideCount?'拉出框外的活怪仍计入本野点，不误标为空。':'',
    c.hasNeutrals?'金币为所属活怪的赏金合计。':'预计金币为同类模拟组合的等概率均值。',
  ].filter(Boolean).join(' ');
  $('camp-interaction-hint').textContent=id===selectedCamp?'已固定 · 点击空白处或按 Esc 关闭':'点击野点固定 · 移开鼠标隐藏';
  const tw=tooltip.offsetWidth,th=tooltip.offsetHeight;
  tooltip.style.left=`${Math.max(6,Math.min(w-tw-6,q.x+16+tw>w?q.x-tw-16:q.x+16))}px`;
  tooltip.style.top=`${Math.max(6,Math.min(h-th-6,q.y+16+th>h?q.y-th-16:q.y+16))}px`;
  canvas.setAttribute('aria-describedby','camp-tooltip');$('camp-choice').setAttribute('aria-describedby','camp-tooltip');
}
function draw(){
  if(!context||!state)return;
  hoveredCamp=!drag&&!placing&&hoverPoint?campAtPoint(hoverPoint)?.id||null:null;
  const active=$('show-camps').checked&&!placing?(hoveredCamp||selectedCamp):null;
  canvas.style.cursor=placing?'crosshair':hoveredCamp?'help':'crosshair';
  const w=canvas.clientWidth,h=canvas.clientHeight,dpr=devicePixelRatio||1;
  if(canvas.width!==Math.round(w*dpr)||canvas.height!==Math.round(h*dpr)){canvas.width=Math.round(w*dpr);canvas.height=Math.round(h*dpr);}
  ctx.setTransform(dpr,0,0,dpr,0,0);ctx.fillStyle='#0d1920';ctx.fillRect(0,0,w,h);
  if(background){
    const o=context.map.overview,a=p({x:o.posX,y:o.posY}),b=p({x:o.posX+o.coordinateSpan,y:o.posY-o.coordinateSpan});
    ctx.globalAlpha=.55;ctx.drawImage(background,a.x,a.y,b.x-a.x,b.y-a.y);ctx.globalAlpha=1;
  }
  if($('show-paths').checked)for(const l of context.map.lanePaths)path(l.points.map(([x,y])=>({x,y})),l.team==='radiant'?'#7da58290':'#b8899490',1);
  if($('show-trees').checked)for(const [x,y]of context.map.trees){const q=p({x,y});ctx.fillStyle='#99beac';ctx.fillRect(q.x-1,q.y-1,2,2);}
  if(active){
    const c=context.map.camps.find(c=>c.id===active),a=p(c.bounds.min),b=p(c.bounds.max);
    ctx.fillStyle='#f5d27f15';ctx.fillRect(a.x,b.y,b.x-a.x,a.y-b.y);
    ctx.strokeStyle='#f5d27f90';ctx.lineWidth=1;ctx.setLineDash([3,3]);ctx.strokeRect(a.x,b.y,b.x-a.x,a.y-b.y);ctx.setLineDash([]);
  }
  if(state.hero&&state.heroPath.length)path([state.hero,...state.heroPath],'#d2b6ff',1.5);
  const target=state.units.find(u=>u.id===selected);
  if(target&&target.hp>0){
    const q=p(target);ctx.beginPath();ctx.strokeStyle='#efda9290';ctx.lineWidth=1;
    const span=Math.max(context.map.bounds.max.x-context.map.bounds.min.x,context.map.bounds.max.y-context.map.bounds.min.y);
    ctx.arc(q.x,q.y,(target.attackRange+target.radius)*w/span*camera.zoom,0,Math.PI*2);ctx.stroke();
  }
  for(const u of state.units){
    if(u.kind==='hero')continue; // Portrait is drawn last, above camp sprites.
    const q=p(u);if(q.x<-30||q.x>w+30||q.y<-30||q.y>h+30)continue;
    const dead=u.hp<=0,r=u.kind==='tower'?7:Math.min(5,2.6+camera.zoom*.45);
    ctx.fillStyle=dead?'#526068':u.team==='radiant'?'#96e3ab':u.team==='dire'?'#f08a97':'#f1c775';
    if(u.id===selected){ctx.beginPath();ctx.arc(q.x,q.y,r+5,0,Math.PI*2);ctx.strokeStyle='#fff1bb';ctx.lineWidth=1.5;ctx.stroke();}
    if(dead){ctx.strokeStyle='#667178';ctx.lineWidth=1;ctx.beginPath();ctx.moveTo(q.x-r/2,q.y-r/2);ctx.lineTo(q.x+r/2,q.y+r/2);ctx.moveTo(q.x+r/2,q.y-r/2);ctx.lineTo(q.x-r/2,q.y+r/2);ctx.stroke();continue;}
    if(u.kind==='tower'){
      ctx.fillRect(q.x-r/2,q.y-r/2,r,r);ctx.strokeStyle='#e7eed5';ctx.strokeRect(q.x-r/2,q.y-r/2,r,r);
      ctx.fillStyle='#f4ebce';ctx.font='9px system-ui';ctx.fillText(u.name.match(/tower(\d)/)?.[1]||'',q.x,q.y-9);
    }else{
      ctx.beginPath();if(u.kind==='neutral')ctx.arc(q.x,q.y,r,0,2*Math.PI);
      else{ctx.moveTo(q.x,q.y-r);ctx.lineTo(q.x+r,q.y+r);ctx.lineTo(q.x-r,q.y+r);ctx.closePath();}
      ctx.fill();
    }
    if(u.hp<u.maxHp||u.id===selected){
      const width=14;ctx.fillStyle='#10202a';ctx.fillRect(q.x-width/2,q.y-r-5,width,3);
      ctx.fillStyle=u.team==='dire'?'#ef9ca4':'#8edfaa';ctx.fillRect(q.x-width/2,q.y-r-5,width*u.hp/u.maxHp,3);
    }
    if(u.windup){ctx.beginPath();ctx.strokeStyle='#fff3ac';ctx.arc(q.x,q.y,r+2,0,Math.PI);ctx.stroke();}
  }
  for(const shot of state.projectiles){
    const q=p(shot),t=state.units.find(u=>u.id===shot.targetId);
    if(t){const v=p(t),d=Math.hypot(v.x-q.x,v.y-q.y)||1;ctx.beginPath();ctx.strokeStyle=shot.critical?'#fff6bf':shot.sourceKind==='tower'?'#ffcf81':'#b3e9ef';ctx.lineWidth=shot.sourceKind==='tower'?2.5:1.5;ctx.moveTo(q.x,q.y);ctx.lineTo(q.x-(v.x-q.x)/d*8,q.y-(v.y-q.y)/d*8);ctx.stroke();}
    ctx.fillStyle=shot.critical?'#fff4a3':'#e8e9c5';ctx.beginPath();ctx.arc(q.x,q.y,shot.sourceKind==='tower'?3:2,0,Math.PI*2);ctx.fill();
  }
  drawCampMarkers(active);
  if(state.hero){
    const hero=state.hero,q=p(hero),size=heroMarkerSize(camera.zoom);
    if(q.x>=-size&&q.x<=w+size&&q.y>=-size&&q.y<=h+size){
      if(hero.id===selected){ctx.beginPath();ctx.arc(q.x,q.y,size*.7,0,Math.PI*2);ctx.strokeStyle='#fff1bb';ctx.lineWidth=1.5;ctx.stroke();}
      drawHeroIcon(ctx,minimapIcons,hero,q,size);
      if(hero.hp>0&&(hero.hp<hero.maxHp||hero.id===selected)){
        ctx.fillStyle='#10202a';ctx.fillRect(q.x-size/2,q.y-size/2-6,size,3);
        ctx.fillStyle='#8edfaa';ctx.fillRect(q.x-size/2,q.y-size/2-6,size*hero.hp/hero.maxHp,3);
      }
      if(hero.windup){ctx.beginPath();ctx.strokeStyle='#fff3ac';ctx.arc(q.x,q.y,size*.7,0,Math.PI);ctx.stroke();}
    }
  }
  renderCampTooltip(active);
}
function updateButtons(){
  const h=state.hero,alive=h&&h.hp>0,enemy=state.units.some(u=>u.id===selected&&u.hp>0&&u.team!==h?.team);
  $('add-hero').disabled=!!h;$('place-hero').disabled=!!h;$('remove-hero').disabled=!h;
  $('equip').disabled=!alive||h.loadout.items.length>=6;$('treads').disabled=!alive;
  $('attack').disabled=!alive||!enemy;$('beam').disabled=!alive||!enemy;
  $('stop-hero').disabled=!alive;$('focus-hero').disabled=!h;
  $('mask').disabled=!alive||!h.loadout.items.includes('item_mask_of_madness')||h.maskReady>state.time||h.mana<25;
  $('mask').textContent=h?.maskReady>state.time?`疯脸 CD ${Math.ceil(h.maskReady-state.time)}s`:'开启疯脸';
}
function render(){
  $('clock').textContent=clock(state.time);$('frame-label').textContent=`帧 ${state.tick} / ${state.timeline.maxTick}`;
  if(!seeking)$('timeline').value=state.tick;$('branch').textContent=`分支 ${state.timeline.branch}`;
  const alive=state.units.filter(u=>u.hp>0);
  $('population').textContent=`${alive.filter(u=>u.kind==='lane').length} / ${alive.filter(u=>u.kind==='neutral').length}`;
  $('creep-attacks').textContent=state.totals.laneAttacks;$('tower-attacks').textContent=state.totals.towerAttacks;$('kills').textContent=state.totals.kills;
  $('gold').textContent=`${state.economy.totalGold} 金`;$('lane-income').textContent=`兵线 ${state.economy.laneGold}`;
  $('neutral-income').textContent=`野怪 ${state.economy.neutralGold}`;$('last-hits').textContent=`补刀 ${state.economy.lastHits}`;
  const h=state.hero;
  $('hero-state').textContent=!h?'未加入':h.hp<=0?'已死亡':`${h.team==='radiant'?'天辉':'夜魇'} · Lv ${h.level}`;
  $('hero-readout').replaceChildren();
  if(!h){const note=document.createElement('p');note.className='empty';note.textContent='场景没有英雄；小兵与塔独立运行。';$('hero-readout').append(note);}
  else{const note=document.createElement('p');note.className='empty';note.textContent=`位置 ${Math.round(h.x)}, ${Math.round(h.y)} · 面向 ${Math.round(h.facing*180/Math.PI)}°`; $('hero-readout').append(note);}
  $('hp').textContent=h?`${Math.round(h.hp)} / ${Math.round(h.maxHp)}`:'—';$('mp').textContent=h?`${Math.round(h.mana)} / ${Math.round(h.maxMana)}`:'—';
  $('hp-bar').style.width=h?`${h.hp/h.maxHp*100}%`:'0';$('mp-bar').style.width=h?`${h.mana/h.maxMana*100}%`:'0';
  const a=h?.attributes;
  const attrs=a?[['攻击',`${fmt(a.damageMin)}–${fmt(a.damageMax)}`],['护甲',fmt(a.armor)],['魔抗',`${fmt(a.magicResistance*100)}%`],
    ['攻速',fmt(a.attackSpeed)],['攻击间隔',`${a.attackInterval.toFixed(2)}s`],['移速',fmt(a.moveSpeed)],
    ['暴击率',`${fmt(a.critChance*100)}%`],['暴击倍率',`${a.critMultiplier.toFixed(2)}×`],
    ['力量',fmt(a.strength)],['敏捷',fmt(a.agility)],['智力',fmt(a.intelligence)],['攻击距离',fmt(a.attackRange)]]:[];
  $('attributes').replaceChildren(...attrs.flatMap(([label,value])=>{const dt=document.createElement('dt'),dd=document.createElement('dd');dt.textContent=label;dd.textContent=value;return[dt,dd];}));
  if(h)$('treads').value=h.loadout.treads;
  $('inventory').replaceChildren(...Array.from({length:6},(_,i)=>{
    const item=h?.loadout.items[i],button=document.createElement('button');button.textContent=item?context.items.find(x=>x.id===item).name:`${i+1} · 空`;
    button.className=item?'filled':'';button.disabled=!item||h.hp<=0;button.title=item?'点击移除此装备':'空装备栏';
    button.onclick=()=>command({type:'equip',loadout:{items:h.loadout.items.filter((_,n)=>n!==i),treads:h.loadout.treads}});return button;
  }));
  // Preserve a dead selected tower/hero for inspection, don't silently attack another unit.
  if(selected&&!state.units.some(u=>u.id===selected))selected=null;
  const target=state.units.find(u=>u.id===selected);
  if(document.activeElement!==$('target-choice')){
    $('target-choice').replaceChildren();
    const placeholder=document.createElement('option');placeholder.value='';placeholder.textContent='点击地图选择单位';$('target-choice').append(placeholder);
    const groupKinds=['hero','tower','neutral','lane'];
    for(const kind of groupKinds){
      const group=document.createElement('optgroup');group.label=names[kind];
      for(const u of state.units.filter(u=>u.kind===kind&&(u.hp>0||u.id===selected))){
        const o=document.createElement('option');o.value=u.id;o.textContent=`${label(u)} · ${Math.ceil(u.hp)} HP · ${u.id.split(':').at(-1)}`;group.append(o);
      }$('target-choice').append(group);
    }
    $('target-choice').value=selected||'';
  }
  $('selection-kind').textContent=target?names[target.kind]:'—';
  $('target-detail').textContent=target?`${label(target)} · HP ${Math.ceil(target.hp)}/${Math.ceil(target.maxHp)} · 护甲 ${fmt(target.armor)} · 攻击 ${fmt(target.damageMin)}–${fmt(target.damageMax)} · 移速 ${target.moveSpeed}`:'选择小兵、野怪或塔，查看属性及攻击。';
  const eventNames={wave_spawn:'三路出兵',neutral_spawn:'营地刷新',hero_added:'英雄加入',hero_removed:'英雄移除',equipment:'装备变更',
    order:'英雄指令',death:'单位阵亡',gold:'击杀金币',attack:'发起攻击',damage:'命中伤害',mask:'开启疯脸',beam:'月光',camp_blocked:'营地占框'};
  $('sim-events').replaceChildren(...state.events.filter(e=>['gold','death','equipment','mask','beam','hero_added','wave_spawn','neutral_spawn'].includes(e.type)||e.sourceKind==='hero').slice(-12).reverse().map(e=>{
    const li=document.createElement('li');li.textContent=`${clock(e.tick*.2)} ${eventNames[e.type]||e.type}${e.amount!==undefined?' '+fmt(e.amount):''}${e.critical?' 暴击':''}${e.detail?' '+e.detail:''}`;return li;
  }));
  if(state.ended){playing=false;$('play').textContent='▶ 播放';}
  renderCamps();updateButtons();draw();
}
function addHero(position){
  return command({type:'add_hero',hero:$('hero-type').value,level:Number($('hero-level').value),team:$('hero-team').value,
    position,loadout:{items:[],treads:'agi'}});
}
function nearestUnit(event){
  const r=canvas.getBoundingClientRect(),x=event.clientX-r.left,y=event.clientY-r.top;
  const hits=state.units.filter(u=>u.hp>0).map(u=>({u,d:Math.hypot(p(u).x-x,p(u).y-y)})).filter(h=>h.d<(h.u.kind==='hero'?heroMarkerSize(camera.zoom)*.85:12)).sort((a,b)=>a.d-b.d);
  return hits[0]?.u;
}
function pointerPoint(event){
  const r=canvas.getBoundingClientRect();return {x:event.clientX-r.left,y:event.clientY-r.top};
}
function campAtPoint(point){
  if(!$('show-camps').checked||!state)return null;
  const candidates=context.map.camps.map(c=>{
    const q=p(c),a=p(c.bounds.min),b=p(c.bounds.max);
    const inBox=point.x>=a.x&&point.x<=b.x&&point.y>=b.y&&point.y<=a.y;
    return {c,d:Math.hypot(point.x-q.x,point.y-q.y),inBox};
  });
  const marker=candidates.filter(c=>c.d<=12).sort((a,b)=>a.d-b.d)[0];
  if(marker)return marker.c;
  const neutral=state.units.filter(u=>u.kind==='neutral'&&u.hp>0&&u.campId)
    .map(u=>({u,d:Math.hypot(p(u).x-point.x,p(u).y-point.y)})).filter(u=>u.d<10).sort((a,b)=>a.d-b.d)[0];
  if(neutral)return context.map.camps.find(c=>c.id===neutral.u.campId);
  return candidates.filter(c=>c.inBox).sort((a,b)=>a.d-b.d)[0]?.c||null;
}
function dismissCamp(){
  selectedCamp=null;hoveredCamp=null;hoverPoint=null;renderCamps();draw();
}
canvas.onclick=e=>{
  if(!state)return;
  if(placing){placing=false;$('place-hero').textContent='在地图放置';addHero(worldPoint(e.clientX,e.clientY));return;}
  const u=nearestUnit(e);selected=u?.id||null;
  selectedCamp=u?u.campId||null:campAtPoint(pointerPoint(e))?.id||null;
  canvas.focus({preventScroll:true});
  render();
};
canvas.oncontextmenu=e=>{
  e.preventDefault();if(!state?.hero)return;
  const u=nearestUnit(e);
  if(u&&u.team!==state.hero.team){selected=u.id;command({type:'order',order:{type:'attack',targetId:u.id}});}
  else command({type:'order',order:{type:'move',position:worldPoint(e.clientX,e.clientY)}});
};
canvas.onwheel=e=>{e.preventDefault();camera.zoom=Math.max(1,Math.min(6,camera.zoom*(e.deltaY<0?1.2:1/1.2)));draw();};
canvas.onpointerdown=e=>{if(e.button===1){e.preventDefault();drag={x:e.clientX,y:e.clientY,center:{...camera}};hoverPoint=null;canvas.setPointerCapture(e.pointerId);draw();}};
canvas.onpointermove=e=>{
  if(!state)return;
  if(!drag){
    hoverPoint=e.pointerType==='touch'?null:pointerPoint(e);
    if((!placing&&hoverPoint?campAtPoint(hoverPoint)?.id||null:null)!==hoveredCamp)draw();
    return;
  }
  const span=Math.max(context.map.bounds.max.x-context.map.bounds.min.x,context.map.bounds.max.y-context.map.bounds.min.y);
  const scale=Math.min(canvas.clientWidth,canvas.clientHeight)/span*camera.zoom;
  camera.x=drag.center.x-(e.clientX-drag.x)/scale;
  camera.y=drag.center.y+(e.clientY-drag.y)/scale;draw();};
canvas.onpointerup=canvas.onpointercancel=canvas.onlostpointercapture=()=>{if(drag){drag=null;hoverPoint=null;draw();}};
canvas.onpointerleave=e=>{
  if($('camp-tooltip').contains(e.relatedTarget))return;
  hoverPoint=null;draw();
};
$('camp-tooltip').onpointerleave=()=>{hoverPoint=null;draw();};
$('clear-camp').onclick=dismissCamp;
document.addEventListener('keydown',e=>{if(e.key==='Escape'&&state)dismissCamp();});
$('play').onclick=()=>{playing=!playing;$('play').textContent=playing?'❚❚ 暂停':'▶ 播放';};
$('step').onclick=()=>change('advance',{ticks:1});$('advance').onclick=()=>change('advance',{ticks:50});
$('reset').onclick=()=>{playing=false;$('play').textContent='▶ 播放';selected=null;selectedCamp=null;hoverPoint=null;change('reset',{seed:Number($('seed').value)});};
$('add-hero').onclick=()=>addHero(context.defaultHeroSpawns[$('hero-team').value]);
$('hero-type').onchange=()=>{$('add-hero').textContent=`添加 ${context.heroes.find(h=>h.id===$('hero-type').value).name}`;};
$('place-hero').onclick=()=>{placing=!placing;hoverPoint=null;draw();$('place-hero').textContent=placing?'点击地图…':'在地图放置';status('点击可行走位置放置英雄；这只是场景编辑，不是游戏内传送。');};
$('remove-hero').onclick=()=>command({type:'remove_hero'});
$('stop-hero').onclick=()=>command({type:'order',order:{type:'stop'}});
$('mask').onclick=()=>command({type:'cast',name:'mask'});
$('beam').onclick=()=>command({type:'cast',name:'beam',targetId:selected});
$('attack').onclick=()=>command({type:'order',order:{type:'attack',targetId:selected}});
$('target-choice').onchange=()=>{selected=$('target-choice').value||null;const u=state.units.find(u=>u.id===selected);if(u?.campId)selectedCamp=u.campId;render();};
$('camp-choice').onchange=()=>{
  selectedCamp=$('camp-choice').value||null;hoverPoint=null;
  const camp=context.map.camps.find(c=>c.id===selectedCamp);
  if(camp){const q=p(camp);if(q.x<0||q.x>canvas.clientWidth||q.y<0||q.y>canvas.clientHeight)camera={...camera,x:camp.x,y:camp.y};}
  render();
};
$('equip').onclick=()=>{const h=state.hero;if(h)command({type:'equip',loadout:{items:[...h.loadout.items,$('item-choice').value],treads:h.loadout.treads}});};
$('treads').onchange=()=>{if(state.hero)command({type:'equip',loadout:{items:state.hero.loadout.items,treads:$('treads').value}});};
$('timeline').oninput=()=>{
  playing=false;$('play').textContent='▶ 播放';seeking=true;clearTimeout(seekTimer);
  const tick=Number($('timeline').value);$('clock').textContent=clock(tick*.2);
  seekTimer=setTimeout(async()=>{await change('seek',{tick});seeking=false;render();},120);
};
$('zoom-in').onclick=()=>{camera.zoom=Math.min(6,camera.zoom*1.3);draw();};
$('zoom-out').onclick=()=>{camera.zoom=Math.max(1,camera.zoom/1.3);draw();};
$('home-view').onclick=()=>{camera={x:0,y:0,zoom:1};draw();};
$('focus-hero').onclick=()=>{if(state.hero){camera={x:state.hero.x,y:state.hero.y,zoom:3};draw();}};
for(const id of ['show-paths','show-trees'])$(id).onchange=draw;
$('show-camps').onchange=()=>{$('camp-choice').disabled=!$('show-camps').checked;dismissCamp();};
new ResizeObserver(draw).observe(canvas);
try{
  context=await api('/api/sim/context');state=await api('/api/sim/state');
  try{minimapIcons=await loadMinimapIcons();canvas.dataset.iconStatus='ready';}
  catch{$('map-art-error').hidden=false;$('map-art-error').textContent='小地图图标资源加载失败；? 表示缺失图片，不是游戏图标。';canvas.dataset.iconStatus='failed';}
  for(const c of context.map.camps){const o=document.createElement('option');o.value=c.id;o.textContent=`${campName(c)} · ${campKinds[c.kind]}`;$('camp-choice').append(o);}
  for(const h of context.heroes){const o=document.createElement('option');o.value=h.id;o.textContent=h.name;$('hero-type').append(o);}
  for(const i of context.items){const o=document.createElement('option');o.value=i.id;o.textContent=i.name;$('item-choice').append(o);}
  for(const l of context.limits){const li=document.createElement('li');li.textContent=l;$('sim-limits').append(li);}
  try{const r=await fetch('/api/map-overview',{headers:{Authorization:`Bearer ${token}`}});if(r.ok)background=await createImageBitmap(await r.blob());}catch{}
  render();status(background?'已就绪。点击播放观察兵线；可以随时加入 Luna。':'已就绪；底图缓存不可用，仍显示核验坐标与实际单位。');
  setInterval(()=>{if(!playing||busy||seeking||!state)return;change('advance',{ticks:Number($('speed').value)});},200);
}catch(e){status(`${e.message}。请使用本机服务提供的会话地址。`,true);}
