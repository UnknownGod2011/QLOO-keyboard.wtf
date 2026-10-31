import {CrystalGrowth,peekTimeline} from './crystal-growth.js';
import {WinterHat} from './winter-hat.js';
import {CrystalDance,dancePose,danceBounds,DANCE_DURATION} from './crystal-dance.js';
const ROWS={idle:[0,6,120],right:[1,8,100],left:[2,8,100],wave:[3,4,180],jump:[4,5,140],error:[5,8,170],waiting:[6,6,190],loading:[7,6,160],review:[8,6,160]};
const clamp=(v,a,b)=>Math.max(a,Math.min(b,v));
const ease=t=>t*t*(3-2*t);
const back=t=>1+2.70158*(t-1)**3+1.70158*(t-1)**2;
// Presentation only: never touches a Jarvis turn, microphone or provider timer.
export class LumaFrameHealth {
 constructor(){this.blocked=true;this.shown=false;this.good=0;this.slow=0;}
 update(delta){
  if(!(delta>0))return this.blocked;
  this.slow=delta>85?this.slow+1:0;
  if(delta>250||this.slow>=3){this.blocked=true;this.good=0;}
  if(this.blocked){this.good=delta<45?this.good+1:0;if(this.good>=(this.shown?24:8)){this.blocked=false;this.shown=true;this.slow=0;}}
  return this.blocked;
 }
}
export class Luma {
 constructor(stage){
  this.stage=stage;this.shell=stage.closest('.luma-shell');this.actor=stage.querySelector('[data-luma-pet]');this.body=stage.querySelector('[data-luma-body]');this.canvas=stage.querySelector('[data-luma-sprite]');this.ctx=this.canvas.getContext('2d');this.fx=stage.querySelector('[data-luma-particles]');this.fxCtx=this.fx.getContext('2d');this.bubble=this.shell.querySelector('[data-luma-bubble]');this.label=this.shell.querySelector('[data-luma-activity]');
  this.options={roaming:true,voice:false,captions:true,reduced:matchMedia('(prefers-reduced-motion: reduce)').matches,avoid:true,paused:false,size:150,glow:.08};
  this.x=0;this.y=0;this.time=0;this.last=0;this.state='idle';this.action=null;this.move=null;this.guide=false;this.particles=[];this.nextAmbient=6000;this.gaze=0;this.pendingGaze=0;this.gazeAt=0;this.pointerAt=-10000;this.speechUntil=0;this.destroyed=false;this.obstacles=[];this.parked=false;
  this.health=new LumaFrameHealth();this.shell.dataset.performance='warming';this.fxPainted=false;this.positioned=false;
  this.image=new Image();const atlasReady=new Promise((resolve,reject)=>{this.image.onload=async()=>{try{await this.image.decode?.();resolve();}catch{reject(new Error('Luma artwork could not load.'));}};this.image.onerror=()=>reject(new Error('Luma artwork could not load.'));});this.image.src=new URL('./assets/spritesheet.webp',import.meta.url).href;
  this.resizeObserver=new ResizeObserver(()=>this.resize());this.resizeObserver.observe(stage);
  this.onPointer=e=>{const dx=e.clientX-this.x-this.options.size/2,dy=e.clientY-this.y-this.petH/2;this.pendingGaze=Math.round((Math.atan2(dx,-dy)*180/Math.PI+360)/22.5)%16;this.pointerAt=this.time;};document.addEventListener('pointermove',this.onPointer,{passive:true});
  this.clickTimer=null;this.onClick=()=>{clearTimeout(this.clickTimer);this.clickTimer=setTimeout(()=>this.play(this.state==='sleep'?'wake':'jump'),220);};this.onDouble=()=>{clearTimeout(this.clickTimer);this.play('wave');};this.body.addEventListener('click',this.onClick);this.body.addEventListener('dblclick',this.onDouble);
  this.tick=this.tick.bind(this);this.ready=atlasReady.then(async()=>{if(this.destroyed)return;this.growth=new CrystalGrowth(this.image);this.dancer=new CrystalDance(this.image);this.winterHat=new WinterHat(this.body,this.image);await this.winterHat.ready;if(this.destroyed)return;this.resize();this.home();this.render();this.label.textContent='Luma';this.resume();});this.ready.catch(()=>{if(!this.destroyed)this.label.textContent='Luma artwork is unavailable.';});
 }
 resize(){const previousWidth=this.w;this.w=this.stage.clientWidth;this.h=this.stage.clientHeight;if(previousWidth&&previousWidth!==this.w){this.action=null;this.move=null;this.particles=[];}const ratio=Math.min(devicePixelRatio||1,2);this.fx.width=this.w*ratio;this.fx.height=this.h*ratio;this.fxPainted=false;this.fxCtx.setTransform(ratio,0,0,ratio,0,0);this.actor.style.width=this.options.size+'px';this.actor.style.height=this.petH+'px';if(!this.positioned)this.home();else this.relocate();}
 get petH(){return this.options.size*208/192;}
 home(){if(this.positioned){this.relocate();return;}const p=this.safeSpot();this.parked=!p;this.actor.style.visibility=p?'':'hidden';if(p){this.x=p.x;this.y=p.y;this.positioned=true;}}
 relocate(){
  this.action=null;this.move=null;this.actor.dataset.escaping='false';
  if(this.safe(this.x,this.y)){this.parked=false;this.actor.style.visibility='';this.actor.dataset.escaping='false';return;}
  this.parked=true;this.actor.style.visibility='hidden';
  const candidates=[];
  for(let y=12;y+this.petH+44<this.h-8;y+=24)for(let x=12;x+this.options.size<this.w-8;x+=24)if(this.safe(x,y))candidates.push({x,y});
  candidates.sort((a,b)=>Math.hypot(a.x-this.x,a.y-this.y)-Math.hypot(b.x-this.x,b.y-this.y));
  if(this.options.reduced){const p=candidates[0];if(p){this.x=p.x;this.y=p.y;this.parked=false;this.actor.style.visibility='';}return;}
  const to=candidates.slice(0,24).find(p=>{let emerged=false;for(let i=0;i<=24;i++){const t=i/24,clear=this.safe(this.x+(p.x-this.x)*t,this.y+(p.y-this.y)*t);if(emerged&&!clear)return false;if(clear)emerged=true;}return emerged;});
  if(to){this.actor.style.visibility='';this.actor.dataset.escaping='true';this.move={from:{x:this.x,y:this.y},to,start:this.time,duration:Math.max(450,Math.hypot(to.x-this.x,to.y-this.y)/225*1000),layoutRetreat:true};}
 }
 setObstacles(rects){this.obstacles=rects;if(this.manualPlacement||this.move?.manual||this.shell.dataset.entrance==='true')return;if(!this.positioned){this.home();return;}if(this.action?.name==='peek'||this.move&&!this.move.layoutRetreat&&!this.pathSafe(this.move.to)||!this.safe(this.x,this.y)||this.parked)this.relocate();}
 clearOfContent(x,y,width=this.options.size,height=this.petH+44){const overhang=width===this.options.size?Math.max(0,(148-width)/2):0;return this.obstacles.every(r=>x+width+overhang<=r.left-7||x-overhang>=r.right+7||y+height<=r.top-7||y>=r.bottom+7);}
 recoverPeek(){if(this.action?.name==='peek'&&!this.safe(this.x,this.y))this.home();}
 safe(x,y,width=this.options.size,height=this.petH+44){const overhang=width===this.options.size?Math.max(0,(148-width)/2):0;return Number.isFinite(x)&&Number.isFinite(y)&&x-overhang>=8&&y>=12&&x+width+overhang<=this.w-8&&y+height<=this.h-8&&this.clearOfContent(x,y,width,height);}
 danceSpace(){
  for(const room of [1,.65,.4]){const b=danceBounds(this.options.size,this.petH,room,this.winterHatEnabled),fits=p=>this.safe(p.x+b.left,p.y+b.top,b.width,b.height);
   if(fits(this))return {x:this.x,y:this.y,room};
   const choices=[];for(let y=this.h-this.petH-12;y>26;y-=8)for(let x=12;x<this.w-this.options.size-12;x+=12){const p={x,y};if(fits(p)&&this.pathSafe(p))choices.push(p);}
   choices.sort((a,b)=>Math.hypot(a.x-this.x,a.y-this.y)-Math.hypot(b.x-this.x,b.y-this.y));if(choices.length)return {...choices[0],room};
  }return null;
 }
 safeSpot(){for(let i=0;i<100;i++){const x=12+Math.random()*Math.max(1,this.w-this.options.size-24);const y=12+Math.random()*Math.max(1,this.h-this.petH-68);if(this.safe(x,y))return{x,y};}for(let y=this.h-this.petH-52;y>=12;y-=24)for(let x=this.w-this.options.size-12;x>=8;x-=24)if(this.safe(x,y))return{x,y};return null;}
 pathSafe(to){for(let i=0;i<=16;i++){const t=i/16;if(!this.safe(this.x+(to.x-this.x)*t,this.y+(to.y-this.y)*t))return false;}return true;}
 reachableSpot(){for(let i=0;i<28;i++){const p=this.safeSpot();if(p&&this.pathSafe(p)&&Math.hypot(p.x-this.x,p.y-this.y)>35)return p;}return null;}
 walk(to,duration){if(!to||!this.pathSafe(to))return;if(this.options.reduced){this.x=to.x;this.y=to.y;return;}this.move={from:{x:this.x,y:this.y},to,start:this.time,duration:duration||Math.max(1000,Math.hypot(to.x-this.x,to.y-this.y)/75*1000)};this.label.textContent='A little wander';}
 // Explicit user placement may cross copy; automatic wandering still uses pathSafe.
 walkManual(to){if(!to||!Number.isFinite(to.x)||!Number.isFinite(to.y))return;const overhang=Math.max(0,(148-this.options.size)/2);to={x:clamp(to.x,8+overhang,Math.max(8+overhang,this.w-this.options.size-overhang-8)),y:clamp(to.y,12,Math.max(12,this.h-this.petH-52))};if(this.options.reduced){this.x=to.x;this.y=to.y;return;}this.move={from:{x:this.x,y:this.y},to,start:this.time,duration:100,manual:true};this.label.textContent='Following your lead';}
 configure(name,value){this.options[name]=value;if(name==='size')this.resize();if(name==='captions'&&!this.guide)this.bubble.hidden=!value||this.time>this.speechUntil;if(name==='paused'){this.shell.classList.toggle('luma-frozen',value);if(value)this.suspend();else this.resume();this.label.textContent=value?'Luma paused':'Luma';}
  if(name==='size'){this.action=null;this.move=null;this.home();}if(name==='reduced'){this.shell.classList.toggle('luma-reduced',value);if(value){this.action=null;this.move=null;this.particles=[];this.home();}this.render();}
  if(name==='avoid'&&value){this.move=null;this.home();}this.nextAmbient=this.time+6000;
 }
 setState(state){this.recoverPeek();this.action=null;if(!this.move?.layoutRetreat&&!this.move?.entering)this.move=null;this.state=state;this.actor.classList.toggle('sleep',state==='sleep');this.nextAmbient=this.time+6000;this.label.textContent={idle:'Looking around',loading:'Working on it…',error:'Something needs a little care',waiting:'Waiting for you',review:'Ready for a look',sleep:'Dreaming of little crystals'}[state]||state;
  if(state==='idle'&&!this.guide)this.bubble.hidden=true;
 }
 play(name){if(this.options.paused)return;this.recoverPeek();this.move=null;this.actor.classList.remove('sleep');if(name==='wander'){this.action=null;this.setState('idle');this.walk(this.reachableSpot());return;}if(name==='teleport'){this.teleport(this.safeSpot());return;}if(name==='peek'){this.peek();return;}if(name==='sleep'){this.setState('sleep');return;}if(name==='wake'){this.setState('idle');name='wave';}
  if(name==='dance'&&this.options.reduced){this.say('A little dance in my heart.',3000);return;}
  let danceRoom=1;if(name==='dance'){const space=this.danceSpace();if(!space){this.say('I need a little more room for my footwork. Try a smaller pet size.',4000);return;}danceRoom=space.room;if(Math.hypot(space.x-this.x,space.y-this.y)>1){this.action=null;this.walk(space,Math.max(450,Math.hypot(space.x-this.x,space.y-this.y)*6));this.move.thenDance=true;this.bubble.hidden=true;this.label.textContent='Making room for my footwork';return;}}
  const duration={jump:800,wave:1500,celebrate:2200,shimmer:1300,dance:DANCE_DURATION}[name]||1200;this.action={name,start:this.time,duration,returnState:this.state==='sleep'?'idle':this.state};this.label.textContent={jump:'A happy little hop',wave:'Hello, you!',celebrate:'You did it!',shimmer:'A little crystal light',dance:'Finding the rhythm'}[name]||name;
  if(name==='dance'){this.bubble.hidden=true;this.speechUntil=0;this.action.room=danceRoom;}
  if(name==='celebrate')this.particles=[];
 }
 teleport(to){if(!to)return;this.move=null;this.particles=[];this.action={name:'teleport',start:this.time,duration:4000,from:{x:this.x,y:this.y},to,returnState:this.state};this.label.textContent='A little seed, growing into Luma';this.bubble.hidden=true;}
 peek(destination,preferredSide,entering=false){if(this.options.reduced||this.parked)return false;const side=preferredSide||(Math.random()<.5?'left':'right');const to=destination||this.safeSpot();if(!to)return false;const from=entering?{x:side==='left'?-this.options.size-8:this.w+8,y:Math.max(26,this.h-this.petH-14)}:{x:this.x,y:this.y};const plan=peekTimeline(from,to,this.w,this.options.size,this.h,side);for(const segment of plan.segments)for(let i=0;i<=20;i++){const t=i/20,x=segment.from.x+(segment.to.x-segment.from.x)*t,y=segment.from.y+(segment.to.y-segment.from.y)*t;if(!this.clearOfContent(x,y))return false;}if(entering){this.x=from.x;this.y=from.y;}this.action={name:'peek',start:this.time,...plan,to,returnState:this.state};this.label.textContent='Run, hide… peekaboo!';this.bubble.hidden=true;return true;}
 say(text,duration=4500){this.shell.querySelector('[data-luma-speech]').textContent=text;this.speechUntil=this.time+duration;this.bubble.hidden=!this.options.captions&&!this.guide;}
 cancelSpeech(){/* Narration and Live are owned by the surface adapter, never Jarvis. */}
 burst(x,y,n=20){for(let i=0;i<n;i++){const a=Math.random()*Math.PI*2,speed=35+Math.random()*90;this.particles.push({x,y,vx:Math.cos(a)*speed,vy:Math.sin(a)*speed-25,life:1.1+Math.random(),age:0,size:2+Math.random()*4});}}
 suspend(){cancelAnimationFrame(this.raf);this.raf=0;this.last=0;}
 resume(){if(this.destroyed||this.raf||this.options.paused||document.hidden||!this.growth)return;this.last=0;this.health.slow=0;if(!this.health.shown){this.health.blocked=true;this.health.good=0;this.shell.dataset.performance='warming';this.onPerformance?.(true,false);}this.raf=requestAnimationFrame(this.tick);}
 tick(now){this.raf=0;if(this.destroyed||this.options.paused||document.hidden)return;const interval=this.last?now-this.last:0,dt=Math.min(interval,50);this.last=now;const before=this.health.blocked,blocked=this.options.reduced?false:this.health.update(interval);this.shell.dataset.performance=blocked?(this.health.shown?'recovering':'warming'):'ready';if(before!==blocked||this.options.reduced&&before){this.health.blocked=blocked;this.onPerformance?.(blocked,this.health.shown);}if(!blocked){this.time+=dt;this.advance(dt);this.render();this.onFrame?.(this.time);}this.raf=requestAnimationFrame(this.tick);}
 advance(dt){if(this.move){const t=clamp((this.time-this.move.start)/this.move.duration,0,1);this.x=this.move.from.x+(this.move.to.x-this.move.from.x)*ease(t);this.y=this.move.from.y+(this.move.to.y-this.move.from.y)*ease(t);if(this.move.layoutRetreat){this.parked=!this.safe(this.x,this.y);this.actor.style.visibility='';}if(t>=1){const dance=this.move.thenDance;this.move=null;this.actor.dataset.escaping='false';this.label.textContent='Looking around';if(dance)this.play('dance');}}
  if(this.action){const a=this.action,p=clamp((this.time-a.start)/a.duration,0,1);if(a.name==='teleport'){if(p<.29){this.x=a.from.x;this.y=a.from.y;}else{this.x=a.to.x;this.y=a.to.y;}}if(a.name==='peek'){const elapsed=this.time-a.start;const segment=a.segments.find(s=>elapsed<s.start+s.duration)||a.segments.at(-1);const t=clamp((elapsed-segment.start)/segment.duration,0,1);this.x=segment.from.x+(segment.to.x-segment.from.x)*t;this.y=segment.from.y+(segment.to.y-segment.from.y)*t;a.segment=segment;if(!segment.running&&this.x>-this.options.size&&this.x<this.w){this.label.textContent='Peekaboo!';}else this.label.textContent='Tiny crystal feet, off I go';}if(p>=1){this.state=a.returnState;this.action=null;this.actor.classList.toggle('sleep',this.state==='sleep');this.label.textContent=this.state==='idle'?'Looking around':this.state;this.nextAmbient=this.time+5000+Math.random()*4000;}}
  // Product surfaces schedule rare actions explicitly; the playground's frequent demo loop is not reused.
  if(!this.guide&&this.time>this.speechUntil)this.bubble.hidden=true;
  if(this.time-this.gazeAt>220){this.gaze=this.pendingGaze;this.gazeAt=this.time;}
  this.particles=this.particles.filter(p=>{p.age+=dt/1000;p.x+=p.vx*dt/1000;p.y+=p.vy*dt/1000;p.vy+=35*dt/1000;return p.age<p.life;});
 }
 render(){if(!this.image.complete||!this.image.naturalWidth)return;let row=0,frame=0,scale=1,opacity=1,dy=0,rotate=0,dx=0,dance=null;const reduced=this.options.reduced;const a=this.action;
  const idlePhase=this.time%5600,blink=idlePhase>4200&&idlePhase<4920;
  if(this.state==='sleep'){row=0;frame=2;scale=reduced?1:.96+Math.sin(this.time/1500)*.018;opacity=.78;}
  else if(ROWS[this.state]&&this.state!=='idle'){const spec=ROWS[this.state];row=spec[0];frame=reduced?0:Math.floor(this.time/spec[2])%spec[1];}
  else if(!reduced){if(blink){frame=clamp(Math.floor((idlePhase-4200)/120),0,5);}else{const dir=this.time-this.pointerAt<2400?this.gaze:Math.round((Math.sin(this.time/2100)*2+4))%16;row=9+Math.floor(dir/8);frame=dir%8;}dy=Math.sin(this.time/1500)*1.8;rotate=Math.sin(this.time/1900)*.65;}
  if(this.move&&!reduced){row=this.move.to.x>this.move.from.x?1:2;frame=Math.floor(this.time/100)%8;dy=0;rotate=0;}
  if(a){const p=clamp((this.time-a.start)/a.duration,0,1);if(a.name==='jump'){row=4;frame=reduced?0:Math.min(4,Math.floor(p*5));dy=0;rotate=0;}if(a.name==='wave'){row=3;frame=reduced?0:Math.floor(p*8)%4;}
   if(a.name==='celebrate'&&!reduced){if(p<.38){row=4;frame=Math.min(4,Math.floor(p/.38*5));dy=0;rotate=0;}else{row=3;frame=Math.min(3,Math.floor((p-.38)/.62*4));dy=Math.sin((p-.38)/.62*Math.PI)*-2;rotate=Math.sin((p-.38)/.62*Math.PI*2)*1.5;}}
   if(a.name==='teleport'){row=0;frame=0;rotate=0;dy=0;if(reduced){opacity=p<.29?1-p/.29:clamp((p-.36)/.64,0,1);}else{opacity=p>.22&&p<.36?0:1;}}
   if(a.name==='peek'&&!reduced){const seg=a.segment;if(seg?.running){row=seg.direction==='right'?1:2;frame=Math.floor((this.time-a.start)/95)%8;rotate=0;dy=0;}else{row=a.side==='left'?9:10;frame=4;rotate=a.side==='left'?5:-5;dy=0;}}

  }
  if(a?.name==='dance'&&!reduced){dance=dancePose(this.time-a.start);row=0;frame=dance.frame;const room=a.room??1;dx=dance.x*room;dy=-dance.lift*room;rotate=dance.angle*room;scale=1;this.label.textContent=dance.phase;}
  this.ctx.clearRect(0,0,192,208);if(dance)this.dancer.draw(this.ctx,dance);else if(a?.name==='teleport'&&!reduced){const p=clamp((this.time-a.start)/a.duration,0,1);if(p<.22)this.growth.draw(this.ctx,1-ease(p/.22));else if(p>=.36&&p<.91)this.growth.draw(this.ctx,(p-.36)/.55);else if(p>=.91){const f=Math.min(5,Math.floor((p-.91)/.09*6));this.ctx.drawImage(this.image,f*192,0,192,208,0,0,192,208);}}else this.ctx.drawImage(this.image,frame*192,row*208,192,208,0,0,192,208);
  const shimmer=a?.name==='shimmer';if(shimmer&&!reduced){const progress=(this.time-a.start)/a.duration;const x=progress*340-80;this.ctx.save();this.ctx.globalCompositeOperation='source-atop';const g=this.ctx.createLinearGradient(x-24,0,x+24,0);g.addColorStop(0,'#ffffff00');g.addColorStop(.5,'#d6f8ff88');g.addColorStop(1,'#ffffff00');this.ctx.fillStyle=g;this.ctx.fillRect(0,0,192,208);this.ctx.restore();}
  this.winterHat?.update({row,frame,time:this.time,enabled:!!this.winterHatEnabled,reduced,secondary:dance?Math.sin(dance.beat*Math.PI-.8)*1.8:0,teleport:a?.name==='teleport'?clamp((this.time-a.start)/a.duration,0,1):null});
  const halo=this.actor.querySelector('.halo');const happy=a?.name==='celebrate';const bloom=happy&&!reduced?Math.sin(clamp((this.time-a.start)/a.duration,0,1)*Math.PI)**2:0;
  this.actor.style.transform=`translate(${this.x}px,${this.y}px)`;this.body.style.transform=`translate(${dx}px,${dy}px) scale(${scale}) rotate(${rotate}deg)`;this.body.style.opacity=opacity;halo.style.opacity=Math.min(.18,this.options.glow*((reduced?1:.85+Math.sin(this.time/1800)*.15)+bloom*.8))*opacity;halo.style.transform=`scale(${1+bloom*.3})`;this.actor.dataset.motion=a?.name||this.state;this.actor.dataset.spriteRow=String(row);this.actor.dataset.spriteFrame=String(frame);this.actor.dataset.danceBeat=dance?dance.beat.toFixed(2):'';const shadow=this.actor.querySelector('.ground-shadow');shadow.style.opacity=opacity*(dance?1-dance.lift*.025:1);shadow.style.transform=dance?`translateX(${dx}px) scale(${1-dance.lift*.012})`:'';
  const ctx=this.fxCtx,hasFx=!reduced&&(a?.name==='teleport'||this.particles.length>0);if(hasFx||this.fxPainted)ctx.clearRect(0,0,this.w,this.h);this.fxPainted=hasFx;if(a?.name==='teleport'&&!reduced){const p=(this.time-a.start)/a.duration;if(p>.29&&p<.92){const t=clamp((p-.29)/.63,0,1),cx=this.x+this.options.size*.5,cy=this.y+this.petH*.95;ctx.save();ctx.globalAlpha=Math.sin(t*Math.PI)*.38;ctx.strokeStyle='#7ba5df';ctx.lineWidth=1.4;ctx.beginPath();ctx.ellipse(cx,cy,8+Math.sin(t*Math.PI)*this.options.size*.3,2+Math.sin(t*Math.PI)*7,0,0,Math.PI*2);ctx.stroke();ctx.restore();}}if(!reduced)for(const p of this.particles){ctx.globalAlpha=1-p.age/p.life;ctx.fillStyle='#69b8ed';ctx.beginPath();ctx.moveTo(p.x,p.y-p.size);ctx.lineTo(p.x+p.size*.55,p.y);ctx.lineTo(p.x,p.y+p.size);ctx.lineTo(p.x-p.size*.55,p.y);ctx.closePath();ctx.fill();}ctx.globalAlpha=1;
  // Host positions floating captions; original crystal motion/artwork stays intact.
 }
 destroy(){this.destroyed=true;cancelAnimationFrame(this.raf);clearTimeout(this.clickTimer);this.cancelSpeech();this.resizeObserver.disconnect();document.removeEventListener('pointermove',this.onPointer);this.body.removeEventListener('click',this.onClick);this.body.removeEventListener('dblclick',this.onDouble);}
}
