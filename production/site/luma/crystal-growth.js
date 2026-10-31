// Each crystal grows from its own root. The original atlas remains untouched.
const clamp=v=>Math.max(0,Math.min(1,v));
const smooth=v=>{v=clamp(v);return v*v*(3-2*v);};
const pieces=[
 {start:.02,end:.33,root:[95,198],shape:[[0,164],[65,157],[95,176],[95,208],[0,208]]},
 {start:.08,end:.39,root:[100,198],shape:[[95,176],[139,158],[192,160],[192,208],[95,208]]},
 {start:.15,end:.74,root:[92,196],shape:[[0,0],[63,0],[64,111],[89,166],[83,185],[0,176]]},
 {start:.29,end:.84,root:[112,197],shape:[[130,0],[192,0],[192,176],[126,189],[110,168],[131,109]]},
 {start:.05,end:.78,root:[96,199],shape:[[63,0],[131,0],[131,138],[117,182],[96,200],[65,174]]},
 {start:.47,end:.92,root:[81,199],shape:[[60,156],[81,141],[105,176],[102,208],[57,208]]},
 {start:.55,end:.96,root:[113,199],shape:[[103,181],[120,148],[141,166],[134,208],[102,208]]}
];

export class CrystalGrowth {
 constructor(image){
  this.image=image;
  this.texture=document.createElement('canvas');this.texture.width=192;this.texture.height=208;
  const c=this.texture.getContext('2d');c.drawImage(image,0,0,192,208,0,0,192,208);
  // Crystalline texture covers the eyes until the face wakes at full height.
  for(const [x,y,w,h] of [[80,75,17,28],[103,75,18,28]]){
   c.save();c.beginPath();c.ellipse(x+w/2,y+h/2,w*.57,h*.58,0,0,Math.PI*2);c.clip();
   c.drawImage(image,87,112,28,33,x-2,y-2,w+4,h+4);c.restore();
  }
 }
 draw(ctx,progress){
  const p=clamp(progress);ctx.clearRect(0,0,192,208);
  if(p<=0)return;
  if(p>=.997){ctx.drawImage(this.image,0,0,192,208,0,0,192,208);return;}
  // A rooted, faceted seed — no confetti or whole-character scaling.
  const seed=Math.sin(Math.min(1,p/.6)*Math.PI)*.7;
  ctx.save();ctx.globalAlpha=seed;ctx.translate(96,195);
  const g=ctx.createLinearGradient(-8,-16,8,0);g.addColorStop(0,'#defaff');g.addColorStop(.45,'#74c9e9');g.addColorStop(1,'#377bb1');
  ctx.fillStyle=g;ctx.beginPath();ctx.moveTo(0,-18);ctx.lineTo(8,-7);ctx.lineTo(5,2);ctx.lineTo(-5,2);ctx.lineTo(-8,-7);ctx.closePath();ctx.fill();ctx.restore();
  for(const piece of pieces){
   const t=smooth((p-piece.start)/(piece.end-piece.start));if(!t)continue;
   const [rx,ry]=piece.root;
   ctx.save();ctx.translate(rx,ry);ctx.scale(.42+.58*t,Math.max(.001,t));ctx.translate(-rx,-ry);
   ctx.beginPath();piece.shape.forEach(([x,y],i)=>i?ctx.lineTo(x,y):ctx.moveTo(x,y));ctx.closePath();ctx.clip();
   ctx.drawImage(this.texture,0,0);
   // A moving icy highlight marks the formation front, attached to the art.
   ctx.globalCompositeOperation='source-atop';
   const light=ctx.createLinearGradient(0,15,0,205);light.addColorStop(0,`rgba(207,250,255,${.32*(1-t)})`);light.addColorStop(.55,'rgba(207,250,255,0)');
   ctx.fillStyle=light;ctx.fillRect(0,0,192,208);ctx.restore();
  }
  if(p>.86){
   ctx.save();ctx.globalAlpha=smooth((p-.86)/.12);
   for(const [x,y,w,h] of [[79,73,20,32],[101,73,23,32]])ctx.drawImage(this.image,x,y,w,h,x,y,w,h);
   ctx.restore();
  }
 }
}

export function peekTimeline(from,to,width,size,height,side){
 // The roaming overlay has no playground footer. Peek approaches the real edge.
 const floor=Math.max(26,height-size*208/192-14);
 const hidden=side==='left'?-size-8:width+8;
 const peeking=side==='left'?-size*.58:width-size*.42;
 const segments=[];let cursor={...from},elapsed=0;
 const go=(point,hold=0)=>{const distance=Math.hypot(point.x-cursor.x,point.y-cursor.y);const duration=hold||Math.max(150,distance/225*1000);segments.push({from:{...cursor},to:{...point},start:elapsed,duration,running:!hold,direction:point.x>=cursor.x?'right':'left'});elapsed+=duration;cursor={...point};};
 go({x:from.x,y:floor});go({x:hidden,y:floor});go(cursor,380);
 go({x:peeking,y:floor});go(cursor,850);go({x:hidden,y:floor});go(cursor,350);
 go({x:to.x,y:floor});go(to);
 return {segments,duration:elapsed,side};
}
