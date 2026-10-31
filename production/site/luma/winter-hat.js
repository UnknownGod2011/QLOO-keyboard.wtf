// A removable textured accessory. The original crystal atlas stays untouched.
export class WinterHat {
 constructor(body,image){
  const accessory=new Image();this.ready=new Promise((resolve,reject)=>{accessory.onload=async()=>{try{await accessory.decode?.();resolve();}catch{reject(new Error('Luma accessory could not load.'));}};accessory.onerror=()=>reject(new Error('Luma accessory could not load.'));});accessory.src=new URL('./assets/santa-hat-realistic.png',import.meta.url).href;
  this.image=image;this.anchors=new Map();this.probe=document.createElement('canvas');this.probe.width=192;this.probe.height=208;this.ctx=this.probe.getContext('2d',{willReadFrequently:true});
  this.el=document.createElementNS('http://www.w3.org/2000/svg','svg');this.el.setAttribute('viewBox','0 0 192 208');this.el.setAttribute('aria-hidden','true');this.el.classList.add('winter-hat');
  this.el.innerHTML=`<g class="hat-anchor"><ellipse cx="0" cy="3" rx="22" ry="2.6" fill="#12202f" opacity=".16"/><g class="hat-fabric"><image href="${new URL('./assets/santa-hat-realistic.png',import.meta.url).href}" x="-34" y="-43" width="76" height="50.667" preserveAspectRatio="xMidYMid meet"/></g></g>`;
  this.anchor=this.el.querySelector('.hat-anchor');this.fabric=this.el.querySelector('.hat-fabric');this.el.hidden=true;body.append(this.el);
 }
 locate(row,frame){
  const key=row*8+frame;if(this.anchors.has(key))return this.anchors.get(key);
  const c=this.ctx;c.clearRect(0,0,192,208);c.drawImage(this.image,frame*192,row*208,192,208,0,0,192,208);const data=c.getImageData(0,0,192,208).data;
  const opaque=(x,y)=>data[(y*192+x)*4+3]>210;
  let top=20,tip=96,found=false;
  for(let y=0;y<130&&!found;y++){let total=0,count=0;for(let x=35;x<157;x++)if(opaque(x,y)){total+=x;count++;}if(count>=3){top=y;tip=total/count;found=true;}}
  const y=Math.min(175,top+23);let left=192,right=0;
  for(let x=Math.max(0,Math.floor(tip-32));x<Math.min(192,tip+33);x++)if(opaque(x,y)){left=Math.min(left,x);right=Math.max(right,x);}
  const x=right>=left?(left+right)/2:tip,width=right>=left?right-left:42;
  const result={x,y,scale:Math.max(.72,Math.min(1.15,(width+8)/52)),angle:Math.max(-24,Math.min(24,Math.atan2(tip-x,23)*180/Math.PI))};this.anchors.set(key,result);return result;
 }
 update({row,frame,time,enabled,reduced,teleport,secondary=0}){
  const hidden=!enabled||(teleport!==null&&teleport<.91);this.el.hidden=hidden;this.el.style.display=hidden?'none':'';if(hidden)return;
  const a=this.locate(row,frame),sway=reduced?0:Math.sin(time/650)*.6;
  this.anchor.setAttribute('transform',`translate(${a.x} ${a.y}) rotate(${a.angle}) scale(${a.scale})`);
  this.fabric.setAttribute('transform',`rotate(${sway+secondary})`);
  this.el.dataset.frame=`${row}:${frame}`;
 }
}

