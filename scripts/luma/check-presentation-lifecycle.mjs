// Offline presentation checks. No credentials, provider, microphone or billing.
import assert from 'node:assert/strict';
import {Luma,LumaFrameHealth} from '../../production/site/luma/luma.js';
import {mountWindSnow} from '../../production/site/luma/wind-snow.js';
import {placeLumaPanel} from '../../production/site/luma/panel-placement.js';
import {nextCaptionLength} from '../../production/site/luma/luma-site.js';

let checks=0;
const previous=new Map();
const install=(key,value)=>{previous.set(key,Object.getOwnPropertyDescriptor(globalThis,key));Object.defineProperty(globalThis,key,{value,writable:true,configurable:true});};
const frames=new Map();let frameId=0;
install('requestAnimationFrame',callback=>{frames.set(++frameId,callback);return frameId;});
install('cancelAnimationFrame',id=>frames.delete(id));
install('document',{hidden:false,addEventListener(){},removeEventListener(){}});
install('window',{addEventListener(){},removeEventListener(){}});
install('innerWidth',390);install('innerHeight',844);install('devicePixelRatio',1);
try {
  assert.equal(nextCaptionLength(0,100,16),2);checks++;
  assert.equal(nextCaptionLength(95,100,50),100);checks++;
  assert.equal(nextCaptionLength(0,100,16,true),100);checks++;
  for(let run=0;run<50;run++) {
    const pet={x:900,y:300,width:118,height:128};
    const at=placeLumaPanel({viewport:{width:1440,height:900},pet,panel:{width:290,height:150},anchor:{x:12,y:12,width:290},clear:()=>true});
    assert.ok(Math.hypot(Math.max(pet.x-at.x-290,at.x-pet.x-118,0),Math.max(pet.y-at.y-150,at.y-pet.y-164,0))<=48);checks++;
  }
  for(let run=0;run<50;run++) {
    const health=new LumaFrameHealth();for(let i=0;i<8;i++)health.update(16);
    const pet=Object.assign(Object.create(Luma.prototype),{health,raf:0,last:100,destroyed:false,options:{paused:false},growth:{},shell:{dataset:{performance:'ready'}},tick(){},x:212,y:88,time:4123,state:'idle',action:{name:'wave'},onPerformance(){throw Error('A normal tab return must not restart visual warm-up.');}});
    document.hidden=true;pet.suspend();assert.equal(pet.raf,0);assert.equal(pet.last,0);checks+=2;
    pet.resume();assert.equal(pet.raf,0);checks++;
    document.hidden=false;pet.resume();assert.ok(pet.raf);assert.equal(pet.health.blocked,false);assert.equal(pet.shell.dataset.performance,'ready');assert.equal(pet.time,4123);assert.equal(pet.x,212);assert.equal(pet.action.name,'wave');checks+=6;
    pet.resume();assert.equal(frames.size,1);checks++;
    pet.suspend();assert.equal(frames.size,0);checks++;
    pet.destroyed=true;pet.resume();assert.equal(frames.size,0);checks++;
  }
  const entering=Object.assign(Object.create(Luma.prototype),{action:null,move:{entering:true},actor:{classList:{toggle(){}}},label:{textContent:''},bubble:{hidden:true},time:0});
  entering.setState('waiting');assert.equal(entering.move.entering,true);checks++;
  entering.move={};entering.setState('idle');assert.equal(entering.move,null);checks++;
  for(const [width,height,size] of [[1920,1080,118],[1440,900,118],[768,1024,118],[390,844,96],[360,640,96],[320,480,96]]) {
    for(let y=12;y+size*208/192+44<height-8;y+=31)for(let x=20;x+size<width-20;x+=41) {
      const pet={x,y,width:size,height:size*208/192};
      for(const wanted of [68,160,450,2000]) {
        const panel={width:Math.min(290,width-24),height:wanted};
        const at=placeLumaPanel({viewport:{width,height},pet,panel,anchor:{x,y,width:panel.width},clear:()=>false});
        assert.ok(at,'Dense content still needs a non-overlapping caption position.');
        assert.ok(at.x>=12&&at.y>=12&&at.x+panel.width<=width-12&&at.y+at.maxHeight<=height-12);
        assert.ok(at.x+panel.width<=x-8||at.x>=x+size+8||at.y+at.maxHeight<=y-8||at.y>=y+pet.height+44,'Caption must never cover crystal/controls, including stale anchors.');checks+=3;
      }
    }
  }
  // A hidden/reduced/off effect retains its object but does no animation work.
  let suspended=false,clears=0;
  const ctx={setTransform(){},clearRect(){clears++;},beginPath(){},ellipse(){},fill(){},arc(){},moveTo(){},lineTo(){},stroke(){}};
  const canvas={dataset:{},hidden:true,getContext:()=>ctx};
  const snow=mountWindSnow(canvas,{isSuspended:()=>suspended,density:()=>.08,wind:()=>.14});
  const frame=now=>{const [id,callback]=frames.entries().next().value;frames.delete(id);callback(now);};
  frame(1000);assert.equal(canvas.hidden,false);assert.ok(Number(canvas.dataset.particles)>0);checks+=2;
  const particleCount=canvas.dataset.particles;
  for(let cycle=0;cycle<50;cycle++) {
    suspended=true;snow.suspend();assert.equal(frames.size,0);assert.equal(canvas.hidden,true);checks+=2;
    const afterClear=clears;snow.suspend();assert.equal(clears,afterClear);checks++;
    suspended=false;snow.resume();snow.resume();assert.equal(frames.size,1);checks++;
    frame(10000+cycle*10000);assert.equal(canvas.hidden,false);assert.equal(canvas.dataset.particles,particleCount);checks+=2;
  }
  snow.destroy();assert.equal(frames.size,0);snow.resume();assert.equal(frames.size,0);checks+=2;
  console.log(JSON.stringify({passed:true,checks,tabCycles:50,snowCycles:50,providerCalls:0,scope:'panel non-overlap, visual continuity, effect suspension and cleanup'}));
} finally {for(const [key,descriptor] of previous){if(descriptor)Object.defineProperty(globalThis,key,descriptor);else delete globalThis[key];}}
