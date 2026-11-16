import assert from 'node:assert/strict';
import { once } from 'node:events';
import fs from 'node:fs/promises';
import { createSandboxServer } from '../src/sandbox/server.ts';
import { nearestWalkable } from '../src/simulation/data.ts';
const {chromium}=await import(process.env.PLAYWRIGHT_MODULE||'playwright');
const {server,token}=createSandboxServer();server.listen(0,'127.0.0.1');await once(server,'listening');
const origin=`http://127.0.0.1:${server.address().port}`;
const browser=await chromium.launch({headless:true,executablePath:process.env.PLAYWRIGHT_EXECUTABLE_PATH});
const state=async()=>await(await fetch(origin+'/api/sim/state',{headers:{Authorization:`Bearer ${token}`}})).json();
try{
  await fs.mkdir('.tmp',{recursive:true});
  const page=await browser.newPage({viewport:{width:1440,height:1120},hasTouch:true});
  const errors=[];page.on('pageerror',e=>errors.push(e.message));
  // Observe real canvas text calls, not a test-only status substitute.
  await page.addInitScript(()=>{
    window.campText=[];
    window.minimapDraws=[];
    const drawImage=CanvasRenderingContext2D.prototype.drawImage;
    CanvasRenderingContext2D.prototype.drawImage=function(source,...args){
      if(this.canvas.id==='sim-map'&&source.dataset?.minimapAsset){
        const t=this.getTransform();
        window.minimapDraws.push({asset:source.dataset.minimapAsset,args,angle:Math.atan2(t.b,t.a)});
        window.minimapDraws=window.minimapDraws.slice(-256);
      }
      return drawImage.call(this,source,...args);
    };
    const original=CanvasRenderingContext2D.prototype.fillText;
    CanvasRenderingContext2D.prototype.fillText=function(text,x,y,...rest){
      if(/^[RD]\d+ |^(可得 |预计 |解封后|刷新 |检查 )/.test(text)){
        window.campText.push({text,x,y});window.campText=window.campText.slice(-256);
      }
      return original.call(this,text,x,y,...rest);
    };
  });
  await page.route('**/*',r=>new URL(r.request().url()).origin===origin?r.continue():r.abort());
  await page.goto(`${origin}/#token=${token}`);
  await page.waitForFunction(()=>document.querySelector('#sim-status').textContent.includes('已就绪'));
  assert.equal(new URL(page.url()).hash,'');
  assert.equal((await state()).hero,null);
  assert.equal(await page.locator('#sim-map').getAttribute('data-icon-status'),'ready');
  const nativeCamps=await page.evaluate(()=>window.minimapDraws.filter(d=>d.asset==='camp-empty'));
  assert.equal(new Set(nativeCamps.map(d=>d.args.slice(0,4).join(','))).size,4,'four actual atlas sprite types are drawn');
  const simContext=await(await fetch(origin+'/api/sim/context',{headers:{Authorization:`Bearer ${token}`}})).json();
  const mapSpan=Math.max(simContext.map.bounds.max.x-simContext.map.bounds.min.x,simContext.map.bounds.max.y-simContext.map.bounds.min.y);
  const mapPoint=async(c,zoom=1)=>{
    const r=await page.locator('#sim-map').boundingBox();
    return {x:r.x+r.width/2+c.x*r.width/mapSpan*zoom,y:r.y+r.height/2-c.y*r.height/mapSpan*zoom};
  };
  const r5=simContext.map.camps.find(c=>c.id==='neutralcamp_good_5'),tooltip=page.locator('#camp-tooltip');
  const hover=async(c,zoom=1)=>{
    await page.locator('#sim-map').scrollIntoViewIfNeeded();
    await page.mouse.move(8,8);
    const q=await mapPoint(c,zoom);await page.mouse.move(q.x,q.y);
    try{
      await page.waitForFunction(id=>{const el=document.querySelector('#camp-tooltip');return !el.hidden&&el.dataset.campId===id;},c.id,{timeout:3000});
    }catch(error){
      console.error('Hover failed',c.id,q,await tooltip.getAttribute('data-camp-id'),await tooltip.isVisible(),errors);
      await page.screenshot({path:'.tmp/simulator-hover-failure.png',fullPage:true});throw error;
    }
  };
  const tooltipInsideMap=async()=>{
    const box=await tooltip.boundingBox(),map=await page.locator('#sim-map').boundingBox();
    assert.ok(box&&box.x>=map.x&&box.y>=map.y&&box.x+box.width<=map.x+map.width&&box.y+box.height<=map.y+map.height,'tooltip fits within map');
  };
  assert.equal(await page.locator('#camp-choice option:not([value=""])').count(),28);
  assert.match(await page.locator('#camp-summary').textContent(),/有怪 0 · 空 28/);
  assert.equal(await tooltip.isVisible(),false);
  assert.equal(await page.locator('#camp-choice').inputValue(),'');
  assert.equal(await page.evaluate(()=>window.campText.length),0,'no always-on camp text is painted');
  await page.screenshot({path:'.tmp/simulator-clean-map.png',fullPage:true});
  const beforeHover=await state();
  for(const c of simContext.map.camps){await hover(c);await tooltipInsideMap();}
  assert.deepEqual(await state(),beforeHover,'hover is read-only, not a simulation action');
  await hover(r5);
  assert.equal(await tooltip.getAttribute('data-pinned'),'false');
  assert.match(await page.locator('#camp-refresh').textContent(),/01:00.0 · 60.0s/);
  assert.match(await page.locator('#camp-gold').textContent(),/≈ 101 金（74–150）/);
  await page.screenshot({path:'.tmp/simulator-camp-hover.png',fullPage:true});
  // Moving into the popup keeps it readable; moving away dismisses it.
  const popup=await tooltip.boundingBox();
  await page.mouse.move(popup.x+25,popup.y+25);
  assert.equal(await tooltip.isVisible(),true);
  await page.mouse.move(8,8);await tooltip.waitFor({state:'hidden'});
  await page.locator('#zoom-in').click();await hover(r5,1.3);await tooltipInsideMap();
  await page.locator('#home-view').click();
  await hover(r5);let q=await mapPoint(r5);await page.mouse.click(q.x,q.y);
  await page.mouse.move(8,8);
  assert.equal(await tooltip.getAttribute('data-pinned'),'true');
  assert.equal(await page.locator('#camp-choice').inputValue(),'neutralcamp_good_5');
  await page.keyboard.press('Escape');await tooltip.waitFor({state:'hidden'});
  assert.equal(await page.locator('#camp-choice').inputValue(),'');
  await page.locator('#show-camps').uncheck();
  q=await mapPoint(r5);await page.mouse.move(q.x,q.y);assert.equal(await tooltip.isVisible(),false);
  assert.equal(await page.locator('#camp-choice').isDisabled(),true);
  await page.locator('#show-camps').check();
  await page.locator('#camp-choice').selectOption(r5.id);
  await page.locator('#step').click();await page.waitForFunction(()=>document.querySelector('#clock').textContent==='00:00.2');
  assert.equal((await state()).tick,1);
  // Advancing and seeking are UI operations. Read-only API queries provide independent evidence.
  const seek=async tick=>{
    await page.locator('#timeline').fill(String(tick));await page.locator('#timeline').dispatchEvent('input');
    await page.waitForFunction(t=>document.querySelector('#frame-label').textContent===`帧 ${t} / 3000`,tick);
  };
  await seek(299);
  assert.match(await page.locator('#camp-refresh').textContent(),/01:00.0 · 0.2s/);
  assert.match(await page.locator('#camp-availability').textContent(),/空营地/);
  await page.screenshot({path:'.tmp/simulator-camps-before-spawn.png',fullPage:true});
  await seek(300);
  assert.match(await page.locator('#camp-availability').textContent(),/有怪/);
  assert.equal(await page.locator('#camp-gold').textContent(),'95 金');
  assert.match(await page.locator('#camp-refresh').textContent(),/02:00.0 · 60.0s/);
  await seek(600);
  let current=await state();assert.ok(current.totals.laneAttacks>0&&current.totals.towerAttacks>0&&current.totals.kills>0);
  assert.equal(current.hero,null);assert.equal(current.economy.totalGold,0);
  assert.ok(Number(await page.locator('#tower-attacks').textContent())>0);
  await page.screenshot({path:'.tmp/simulator-no-hero.png',fullPage:true});
  // Real-time play advances integer frames and pause stops the wall-clock loop.
  await page.locator('#play').click();
  await page.waitForFunction(()=>document.querySelector('#clock').textContent!=='02:00.0');
  await page.locator('#play').click();await page.waitForTimeout(250);
  const paused=(await state()).tick;await page.waitForTimeout(450);assert.equal((await state()).tick,paused);
  await seek(300);await page.locator('#add-hero').click();
  await page.waitForFunction(()=>document.querySelector('#hero-state').textContent.includes('Lv 10'));
  const equip=async item=>{
    await page.locator('#item-choice').selectOption(item);
    await Promise.all([page.waitForResponse(r=>r.url().endsWith('/api/sim/command')&&r.status()===200),page.locator('#equip').click()]);
  };
  for(const i of ['item_power_treads','item_mask_of_madness','item_greater_crit','item_platemail','item_cloak'])await equip(i);
  assert.equal(await page.locator('#inventory button.filled').count(),5);
  assert.ok((await page.locator('#attributes').textContent()).includes('30.0%'));
  current=await state();const victim=current.units.find(u=>u.campId==='neutralcamp_good_5'&&u.hp>0);
  await page.locator('#target-choice').selectOption(victim.id);
  await page.locator('#attack').click();await page.locator('#mask').click();
  await page.locator('#advance').click();
  await page.waitForFunction(()=>document.querySelector('#clock').textContent==='01:10.0');
  current=await state();assert.ok(current.economy.neutralGold>0);assert.ok(current.totals.heroAttacks>0);
  const nativeHero=await page.evaluate(()=>({
    portrait:window.minimapDraws.findLast(d=>d.asset==='npc_dota_hero_luna'),
    arrow:window.minimapDraws.findLast(d=>d.asset==='sheet'),
  }));
  assert.ok(nativeHero.portrait&&nativeHero.arrow);
  assert.equal(nativeHero.portrait.angle,0,'portrait does not rotate with facing');
  assert.ok(Math.abs(nativeHero.arrow.angle+current.hero.facing)<1e-8,'arrow follows the current frame facing');
  assert.equal(current.camps.find(c=>c.id==='neutralcamp_good_5').hasNeutrals,false);
  assert.match(await page.locator('#camp-availability').textContent(),/空营地/);
  assert.match(await page.locator('#camp-gold').textContent(),/≈ 101 金/);
  assert.match(await page.locator('#camp-refresh').textContent(),/02:00.0 · 50.0s/);
  assert.ok(Number.isFinite(current.hero.facing));
  const gold=current.economy.totalGold,hp=current.hero.hp,position=[current.hero.x,current.hero.y];
  const camps=current.camps;
  await page.locator('#focus-hero').click();
  await page.screenshot({path:'.tmp/simulator-luna.png',fullPage:true});
  await seek(310);await seek(350);
  current=await state();assert.equal(current.economy.totalGold,gold);assert.equal(current.hero.hp,hp);
  assert.deepEqual([current.hero.x,current.hero.y],position);
  assert.deepEqual(current.camps,camps);
  // In the past, change equipment through UI: future is replaced, not stale replay appended.
  await seek(310);await page.locator('#inventory button').filter({hasText:'代达罗斯'}).click();
  await page.waitForFunction(()=>document.querySelector('#branch').textContent==='分支 2');
  assert.equal((await state()).hero.loadout.items.includes('item_greater_crit'),false);
  // Scenario reset, then place hero near a tower with canvas input and attack it.
  await page.locator('#reset').click();await page.waitForFunction(()=>document.querySelector('#clock').textContent==='00:00.0');
  await page.locator('#home-view').click();
  current=await state();const tower=current.units.find(u=>u.name==='npc_dota_badguys_tower1_bot');
  const position2=nearestWalkable({x:tower.x-360,y:tower.y});
  const rect=await page.locator('#sim-map').boundingBox(),span=20992;
  await page.locator('#place-hero').click();
  await page.mouse.click(rect.x+rect.width/2+position2.x*rect.width/span,rect.y+rect.height/2-position2.y*rect.height/span);
  await page.waitForFunction(()=>document.querySelector('#hero-state').textContent.includes('Lv 10'));
  for(const i of ['item_power_treads','item_mask_of_madness','item_greater_crit','item_platemail','item_cloak'])await equip(i);
  await page.locator('#target-choice').selectOption(tower.id);await page.locator('#attack').click();await page.locator('#mask').click();
  await page.locator('#advance').click();await page.waitForFunction(()=>document.querySelector('#clock').textContent==='00:10.0');
  await page.locator('#advance').click();await page.waitForFunction(()=>document.querySelector('#clock').textContent==='00:20.0');
  current=await state();assert.equal(current.units.find(u=>u.id===tower.id).hp,0);assert.ok(current.totals.towerAttacks>0);
  await page.screenshot({path:'.tmp/simulator-tower-combat.png',fullPage:true});
  // A separate lane attack via ordinary placement and selection.
  await page.locator('#reset').click();await page.waitForFunction(()=>document.querySelector('#clock').textContent==='00:00.0');
  await seek(175);current=await state();const creep=current.units.find(u=>u.kind==='lane'&&u.team==='dire'&&u.hp>0);
  const pos=nearestWalkable({x:creep.x-200,y:creep.y+100});
  await page.locator('#place-hero').click();
  await page.mouse.click(rect.x+rect.width/2+pos.x*rect.width/span,rect.y+rect.height/2-pos.y*rect.height/span);
  await page.waitForFunction(()=>document.querySelector('#hero-state').textContent.includes('Lv 10'));
  for(const i of ['item_power_treads','item_greater_crit'])await equip(i);
  await page.locator('#target-choice').selectOption(creep.id);await page.locator('#attack').click();
  await page.locator('#advance').click();await page.waitForFunction(()=>document.querySelector('#clock').textContent==='00:45.0');
  assert.ok((await state()).economy.laneGold>0);
  // Empty camp can be blocked by an actual placed hero on a refresh boundary.
  await page.locator('#reset').click();await page.waitForFunction(()=>document.querySelector('#clock').textContent==='00:00.0');
  await seek(299);
  const blockPos=nearestWalkable(simContext.map.camps.find(c=>c.id==='neutralcamp_good_5'));
  await page.locator('#camp-choice').selectOption('neutralcamp_good_5');
  await page.locator('#place-hero').click();
  await page.mouse.click(rect.x+rect.width/2+blockPos.x*rect.width/span,rect.y+rect.height/2-blockPos.y*rect.height/span);
  await page.waitForFunction(()=>document.querySelector('#camp-availability').textContent==='空营地 · 占框');
  await page.locator('#step').click();await page.waitForFunction(()=>document.querySelector('#clock').textContent==='01:00.0');
  assert.equal((await state()).camps.find(c=>c.id==='neutralcamp_good_5').hasNeutrals,false);
  assert.match(await page.locator('#camp-refresh').textContent(),/02:00.0 · 60.0s/);
  assert.match(await page.locator('#camp-detail').textContent(),/跳过本次刷新/);
  await page.screenshot({path:'.tmp/simulator-camps-blocked.png',fullPage:true});
  await page.locator('#remove-hero').click();
  await page.waitForFunction(()=>document.querySelector('#camp-availability').textContent==='空营地 · 未占框');
  assert.match(await page.locator('#camp-refresh').textContent(),/02:00.0 · 60.0s/);
  await seek(600);
  assert.match(await page.locator('#camp-availability').textContent(),/有怪/);
  assert.equal(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth),true);
  await page.setViewportSize({width:390,height:844});await page.waitForTimeout(100);
  assert.equal(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth),true);
  await page.locator('#clear-camp').click();await tooltip.waitFor({state:'hidden'});
  await page.locator('#sim-map').scrollIntoViewIfNeeded();
  q=await mapPoint(r5);await page.touchscreen.tap(q.x,q.y);
  assert.equal(await tooltip.getAttribute('data-pinned'),'true');await tooltipInsideMap();
  assert.equal(await page.evaluate(()=>window.campText.length),0,'camp text stays in the single interactive popup');
  await page.screenshot({path:'.tmp/simulator-mobile.png',fullPage:true});
  const broken=await browser.newPage();
  try{
    await broken.route('**/minimap/luna.png',route=>route.abort());
    await broken.goto(`${origin}/#token=${token}`);
    await broken.waitForFunction(()=>document.querySelector('#sim-map').dataset.iconStatus==='failed');
    assert.match(await broken.locator('#map-art-error').textContent(),/图标资源加载失败/);
    assert.equal(await broken.locator('#map-art-error').isVisible(),true);
  }finally{await broken.close();}
  assert.deepEqual(errors,[]);
  console.log('PASS simulator browser: native camp sprites, upright Luna portrait and facing arrow, missing-image notice, clean map/28 hovers, pin/Escape/zoom/touch, countdown/gold/rewind, combat/inventory/timeline.');
}finally{await browser.close();await new Promise(resolve=>server.close(resolve));}
