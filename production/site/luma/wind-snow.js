// Original layered snow simulation. No assets or code from Igloo are used.
export function mountWindSnow(canvas,{isSuspended=()=>false,wind=()=>.55,density=()=>.6}={}){
 const ctx=canvas.getContext('2d');
 if(!ctx)throw new Error('Canvas unavailable');
 let width=0,height=0,ratio=1,flakes=[],raf=0,last=0,time=0,active=true,sleeping=true,gust=0;
 const pointer={x:-1000,y:-1000,vx:0,vy:0,last:0};
 function flake(){const z=Math.pow(Math.random(),1.6);return {x:Math.random()*width,y:Math.random()*height,z,r:.45+z*2.1,vx:0,vy:0,seed:Math.random()*100,angle:Math.random()*6.28};}
 function resize(){width=innerWidth;height=innerHeight;ratio=Math.min(devicePixelRatio||1,1.6);canvas.width=Math.round(width*ratio);canvas.height=Math.round(height*ratio);ctx.setTransform(ratio,0,0,ratio,0,0);}
 function move(e){if(e.pointerType==='touch'||isSuspended())return;const now=performance.now(),dt=Math.max(.012,(now-pointer.last)/1000);if(now-pointer.last<150){pointer.vx=Math.max(-1700,Math.min(1700,(e.clientX-pointer.x)/dt));pointer.vy=Math.max(-1700,Math.min(1700,(e.clientY-pointer.y)/dt));}pointer.x=e.clientX;pointer.y=e.clientY;pointer.last=now;}
 function leave(){pointer.x=-1000;pointer.y=-1000;pointer.vx=pointer.vy=0;}
 function frame(now){if(!active)return;raf=requestAnimationFrame(frame);const suspended=isSuspended()||document.hidden;canvas.hidden=suspended;
  if(suspended){if(!sleeping){ctx.clearRect(0,0,width,height);gust=0;leave();}sleeping=true;last=now;return;}
  if(sleeping){last=now;sleeping=false;}
  const dt=Math.min((now-last)/1000,.035);last=now;time+=dt;
  if(width!==innerWidth||height!==innerHeight)resize();
  const count=Math.round(Math.min(1100,width*height/1500)*(.35+density()*1.3));
  while(flakes.length<count)flakes.push(flake());if(flakes.length>count)flakes.length=count;
  ctx.clearRect(0,0,width,height);gust*=Math.exp(-dt*.6);
  pointer.vx*=Math.exp(-dt*5);pointer.vy*=Math.exp(-dt*5);
  const breeze=(32+wind()*120)*(Math.sin(time*.28)*.5+.65)+gust*310;
  for(const f of flakes){
   const depth=.22+f.z*1.5;
   const swirl=Math.sin(f.y*.007+time*.75+f.seed)*18+Math.cos(f.x*.004-time*.45)*14;
   let targetX=(breeze+swirl)*depth,targetY=(15+f.z*24)+Math.sin(time*.7+f.seed)*8+gust*Math.sin(f.seed+time)*45;
   const dx=f.x-pointer.x,dy=f.y-pointer.y,dist=Math.hypot(dx,dy),radius=170+f.z*70;
   if(dist<radius){const force=Math.pow(1-dist/radius,2);targetX+=force*(pointer.vx*.65+dx*2-dy*1.5);targetY+=force*(pointer.vy*.55+dy*2+dx*1.5);}
   const response=1-Math.exp(-dt*(1.6+f.z*2.5));f.vx+=(targetX-f.vx)*response;f.vy+=(targetY-f.vy)*response;
   f.x+=f.vx*dt;f.y+=f.vy*dt;f.angle+=dt*(.3+f.z);
   if(f.x>width+35)f.x=-30;if(f.x< -35)f.x=width+30;if(f.y>height+30)f.y=-20;if(f.y< -35)f.y=height+30;
   const opacity=(.22+f.z*.48)*(.8+.2*Math.sin(time+f.seed));
   const streak=Math.min(18,Math.hypot(f.vx,f.vy)*.014*f.z);
   ctx.strokeStyle=`rgba(220,238,255,${opacity})`;ctx.fillStyle=`rgba(245,250,255,${opacity})`;
   if(streak>2){const speed=Math.max(1,Math.hypot(f.vx,f.vy));ctx.lineWidth=f.r*.7;ctx.lineCap='round';ctx.beginPath();ctx.moveTo(f.x,f.y);ctx.lineTo(f.x-f.vx/speed*streak,f.y-f.vy/speed*streak);ctx.stroke();}
   ctx.beginPath();ctx.ellipse(f.x,f.y,f.r,f.r*(.4+.25*Math.abs(Math.sin(f.angle))),f.angle,0,Math.PI*2);ctx.fill();
   // Large near flakes have a soft halo, while distant snow stays pin-sharp.
   if(f.z>.85){ctx.fillStyle=`rgba(224,242,255,${opacity*.07})`;ctx.beginPath();ctx.arc(f.x,f.y,f.r*2.5,0,Math.PI*2);ctx.fill();}
  }
  canvas.dataset.particles=String(flakes.length);canvas.dataset.gust=gust.toFixed(2);
 }
 resize();window.addEventListener('pointermove',move,{passive:true});document.addEventListener('pointerleave',leave);raf=requestAnimationFrame(frame);
 return {suspend(){cancelAnimationFrame(raf);raf=0;if(!sleeping){ctx.clearRect(0,0,width,height);gust=0;leave();}sleeping=true;canvas.hidden=true;},resume(){if(active&&!raf){last=0;sleeping=true;raf=requestAnimationFrame(frame);}},gust(){if(!isSuspended())gust=1.25;},destroy(){active=false;cancelAnimationFrame(raf);raf=0;window.removeEventListener('pointermove',move);document.removeEventListener('pointerleave',leave);ctx.clearRect(0,0,width,height);canvas.hidden=true;}};
}
