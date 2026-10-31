// Original pop-performance choreography: planted contacts, moonwalk and sharp holds.
export const BEAT_MS=60000/112;
export const DANCE_DURATION=32*BEAT_MS;
const clamp=v=>Math.max(0,Math.min(1,v));
const smooth=v=>{v=clamp(v);return v*v*(3-2*v);};
const mix=(a,b,t)=>a+(b-a)*t;
const keys=[
 [0,0,0,0,0],[.8,0,0,0,0],[1.1,0,-8,15,-25],[1.75,0,-8,15,-25],
 [2.1,0,5,-8,-30],[2.75,0,5,-8,-30],[3.2,0,-6,12,-12],[4,0,-6,12,-12],
 [12,-64,-6,12,-12],[13,-57,9,-18,24],[14,-44,-9,25,-12],
 [15,-28,9,-14,25],[16,-14,-9,25,-14],[17,0,4,-8,12],[18,0,0,0,0],
 [19,0,-28,13,-22],[20.5,0,-28,13,-22],[22,0,0,0,0],
 [22.4,7,13,-22,28],[23,7,13,-22,28],[23.4,-7,-13,28,-22],[24,-7,-13,28,-22],
 [24.4,5,10,-14,22],[25,5,10,-14,22],[26,0,0,0,0],
 [26.35,0,-11,30,36],[29,0,-11,30,36],[30,0,-5,12,-18],[32,0,0,0,0]
];
export function dancePose(ms){
 const beat=clamp(ms/DANCE_DURATION)*32;let i=1;while(i<keys.length-1&&keys[i][0]<beat)i++;
 const a=keys[i-1],b=keys[i],raw=clamp((beat-a[0])/(b[0]-a[0])),t=beat>=4&&beat<=12?raw:smooth(raw);
 const p={beat,x:mix(a[1],b[1],t),angle:mix(a[2],b[2],t),left:mix(a[3],b[3],t),right:mix(a[4],b[4],t),lift:0,footLeft:0,footRight:0,toeLeft:0,toeRight:0};
 p.phase=beat<4?'Strike · hold · hit':beat<12?'Moonwalk':beat<18?'Heel-toe shuffle':beat<22?'The lean':beat<26?'Toe pivots':beat<30?'Signature freeze':'Curtain call';
 if(beat>=4&&beat<12){const u=(beat-4)%2/2,e=smooth((beat-4)/.35)*(1-smooth((beat-11.65)/.35));p.footLeft=Math.sin(u*Math.PI*2)*7*e;p.footRight=-p.footLeft;p.toeLeft=Math.max(0,Math.sin(u*Math.PI*2))*12*e;p.toeRight=Math.max(0,-Math.sin(u*Math.PI*2))*12*e;p.left+=Math.sin(u*Math.PI*2)*6*e;p.right-=Math.sin(u*Math.PI*2)*6*e;}
 else if(beat>=12&&beat<18){const e=Math.sin((beat-12)/6*Math.PI);p.footLeft=Math.sin(beat*Math.PI)*5*e;p.footRight=-p.footLeft;p.toeLeft=Math.max(0,Math.sin(beat*Math.PI))*10*e;p.toeRight=Math.max(0,-Math.sin(beat*Math.PI))*10*e;}
 else if(beat>=22&&beat<26){const e=Math.sin((beat-22)/4*Math.PI);p.toeLeft=Math.max(0,p.angle)*e;p.toeRight=Math.max(0,-p.angle)*e;}
 const blink=beat>29.2&&beat<29.7?(beat-29.2)/.5:-1;p.frame=blink<0?0:Math.min(5,Math.floor(blink*6));return p;
}
// Sweep the actual CSS transform around the same 50%/94% pivot as pet-body.
export function danceBounds(width,height,room=1,hat=false){
 let left=Infinity,top=Infinity,right=-Infinity,bottom=-Infinity;
 for(let i=0;i<=256;i++){const p=dancePose(DANCE_DURATION*i/256),a=p.angle*room*Math.PI/180,c=Math.cos(a),s=Math.sin(a);
  for(const x of [0,width])for(const y of [hat?-height*.22:0,height]){const px=x-width*.5,py=y-height*.94,tx=width*.5+px*c-py*s+p.x*room,ty=height*.94+px*s+py*c;left=Math.min(left,tx);top=Math.min(top,ty);right=Math.max(right,tx);bottom=Math.max(bottom,ty);}
 }
 return {left:left-3,top:top-3,width:right-left+6,height:bottom-top+6};
}
export class CrystalDance {
 constructor(image){
  const feet=[{name:'footLeft',root:[81,197],shape:[[55,173],[78,161],[98,186],[99,208],[48,208]]},{name:'footRight',root:[115,197],shape:[[102,186],[125,161],[146,175],[150,208],[101,208]]}];
  const regions=[{name:'left',root:[82,184],shape:[[0,0],[63,0],[64,112],[88,164],[83,190],[0,184]]},{name:'right',root:[113,188],shape:[[131,0],[192,0],[192,184],[124,194],[109,168],[131,110]]},{name:'body',root:[96,194],shape:[[63,0],[131,0],[131,110],[113,167],[128,182],[192,182],[192,208],[0,208],[0,176],[66,174],[63,112]]},...feet];
  const path=(c,shape)=>{c.beginPath();shape.forEach(([x,y],i)=>i?c.lineTo(x,y):c.moveTo(x,y));c.closePath();};
  this.frames=Array.from({length:6},(_,frame)=>regions.map(region=>{const canvas=document.createElement('canvas');canvas.width=192;canvas.height=208;const c=canvas.getContext('2d');c.save();path(c,region.shape);c.clip();c.drawImage(image,frame*192,0,192,208,0,0,192,208);c.restore();if(!region.name.startsWith('foot')){c.globalCompositeOperation='destination-out';for(const foot of feet){path(c,foot.shape);c.fill();}}return {...region,canvas};}));this.image=image;
 }
 draw(ctx,pose){
  if(Math.abs(pose.left)+Math.abs(pose.right)+Math.abs(pose.footLeft)+Math.abs(pose.footRight)<.01){ctx.drawImage(this.image,pose.frame*192,0,192,208,0,0,192,208);return;}
  for(const piece of this.frames[pose.frame]){ctx.save();const [x,y]=piece.root,isFoot=piece.name.startsWith('foot'),offset=isFoot?pose[piece.name]:0;ctx.translate(x+offset,y);ctx.rotate((isFoot?(piece.name==='footLeft'?-pose.toeLeft:pose.toeRight):(pose[piece.name]||0))*Math.PI/180);ctx.translate(-x,-y);ctx.drawImage(piece.canvas,0,0);ctx.restore();}
 }
}
