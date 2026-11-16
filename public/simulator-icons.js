// Artwork and sprite rectangles come from the installed client's pinned
// mod_textures.txt, not hand-drawn approximations or unrelated hero portraits.
export const heroMarkerSize=zoom=>28+Math.min(3,Math.max(0,zoom-1))*2;
export const facingRotation=facing=>-facing;

async function image(asset,tag){
  const img=new Image();img.dataset.minimapAsset=tag;img.src=asset.url;
  await img.decode();
  if(img.naturalWidth!==asset.width||img.naturalHeight!==asset.height)throw new Error('小地图图片尺寸与来源记录不一致');
  return img;
}
function tint(sheet,color,tag){
  const canvas=document.createElement('canvas');canvas.width=sheet.naturalWidth;canvas.height=sheet.naturalHeight;
  canvas.dataset.minimapAsset=tag;
  const ctx=canvas.getContext('2d');
  ctx.drawImage(sheet,0,0);
  ctx.globalCompositeOperation='multiply';ctx.fillStyle=color;ctx.fillRect(0,0,canvas.width,canvas.height);
  ctx.globalCompositeOperation='destination-in';ctx.drawImage(sheet,0,0);
  return canvas;
}
export async function loadMinimapIcons(){
  const response=await fetch('/minimap/manifest.json');
  if(!response.ok)throw new Error('小地图图标清单不可用');
  const manifest=await response.json();
  const [sheet,heroes]=await Promise.all([
    image(manifest.sheet,'sheet'),
    Promise.all(Object.entries(manifest.heroes).map(async([name,asset])=>[name,await image(asset,name)])),
  ]);
  return {manifest,sheet,heroes:Object.fromEntries(heroes),camps:{
    occupied:tint(sheet,'#eed279','camp-occupied'),
    empty:tint(sheet,'#b4bdc2','camp-empty'),
    blocked:tint(sheet,'#f3a981','camp-blocked'),
  }};
}
function sprite(ctx,image,rect,x,y,size){
  ctx.drawImage(image,rect.x,rect.y,rect.width,rect.height,x-size/2,y-size/2,size,size);
}
function missing(ctx,x,y,size){
  ctx.save();ctx.strokeStyle='#b0b8bb';ctx.strokeRect(x-size/3,y-size/3,size*2/3,size*2/3);
  ctx.fillStyle='#b0b8bb';ctx.font=`${Math.round(size*.55)}px system-ui`;ctx.textAlign='center';ctx.fillText('?',x,y+size*.18);ctx.restore();
}
export function drawCampIcon(ctx,icons,kind,point,size,status){
  if(!icons){missing(ctx,point.x,point.y,size);return;}
  const state=status.hasNeutrals?'occupied':status.spawnBlocked?'blocked':'empty';
  ctx.save();ctx.globalAlpha=status.hasNeutrals?1:status.spawnBlocked ? .85 : .45;
  sprite(ctx,icons.camps[state],icons.manifest.camps[kind],point.x,point.y,size);
  ctx.restore();
}
export function drawHeroIcon(ctx,icons,hero,point,size){
  const portrait=icons?.heroes[hero.name];
  if(!portrait){missing(ctx,point.x,point.y,size);return;}
  ctx.save();
  if(hero.hp>0){
    ctx.save();ctx.translate(point.x,point.y);ctx.rotate(facingRotation(hero.facing));
    sprite(ctx,icons.sheet,icons.manifest.heroArrow,0,0,size*1.7);
    ctx.restore();
  }else{ctx.globalAlpha=.5;ctx.filter='grayscale(1)';}
  // Only the arrow rotates. The face is always upright, including while moving
  // south/west and when seeking back through earlier facing values.
  ctx.drawImage(portrait,point.x-size/2,point.y-size/2,size,size);
  ctx.restore();
  if(hero.hp<=0){
    ctx.save();ctx.strokeStyle='#dc9296';ctx.lineWidth=1.5;ctx.beginPath();
    ctx.moveTo(point.x-size*.4,point.y-size*.4);ctx.lineTo(point.x+size*.4,point.y+size*.4);ctx.stroke();ctx.restore();
  }
}
