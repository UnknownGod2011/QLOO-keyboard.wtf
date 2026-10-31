// Offline geometry only: no browser, credentials, provider, microphone or billing.
import assert from 'node:assert/strict';
import {Luma,LumaFrameHealth} from '../../production/site/luma/luma.js';

let seed=12026;
const originalRandom=Math.random;
Math.random=()=>{seed=(seed*1664525+1013904223)>>>0;return seed/4294967296;};
let checks=0;
try {
  for(const [w,h,size] of [[1440,900,118],[1920,1080,118],[390,844,96],[768,1024,118]]) {
    const pet=Object.assign(Object.create(Luma.prototype),{w,h,options:{size},obstacles:[],actor:{style:{},dataset:{}}});
    const bands=new Set();
    for(let i=0;i<200;i++) {
      const p=pet.safeSpot();assert.ok(p&&pet.safe(p.x,p.y));checks++;
      bands.add(Math.floor(p.y/(h/4)));
    }
    assert.ok(bands.size>=3,'Roaming must not be constrained to a bottom strip.');checks++;
    pet.obstacles=[{left:w*.28,right:w*.7,top:h*.2,bottom:h*.68}];
    for(let i=0;i<200;i++) {
      const p=pet.safeSpot();assert.ok(p&&pet.safe(p.x,p.y));
      assert.ok(p.x+size<=w*.28-7||p.x>=w*.7+7||p.y+pet.petH+44<=h*.2-7||p.y>=h*.68+7);checks+=2;
    }
    pet.x=12;pet.y=12;
    assert.equal(pet.pathSafe({x:w*.5,y:h*.45}),false);checks++;
    pet.obstacles=[{left:0,right:w,top:0,bottom:h}];
    assert.equal(pet.safeSpot(),null);checks++;
    pet.home();assert.equal(pet.parked,true);assert.equal(pet.actor.style.visibility,'hidden');checks+=2;
  }
  for(let run=0;run<100;run++) {
    const health=new LumaFrameHealth();
    for(let i=0;i<7;i++){assert.equal(health.update(16),true);checks++;}
    assert.equal(health.update(16),false);checks++;
    assert.equal(health.update(300),true);checks++;
    for(let i=0;i<23;i++){assert.equal(health.update(16),true);checks++;}
    assert.equal(health.update(16),false);checks++;
    health.update(90);health.update(90);assert.equal(health.update(90),true);checks++;
    const slow=new LumaFrameHealth();for(let i=0;i<30;i++){assert.equal(slow.update(50),true);checks++;}
  }
  const walker=Object.assign(Object.create(Luma.prototype),{w:1440,h:900,options:{size:118,reduced:false},actor:{style:{},dataset:{}},obstacles:[{left:80,right:320,top:80,bottom:340}],x:100,y:100,time:0,positioned:true,state:'idle',particles:[],label:{textContent:''},bubble:{hidden:true},guide:false,speechUntil:0,gazeAt:0,pendingGaze:0});
  walker.relocate();assert.equal(walker.x,100);assert.equal(walker.y,100);assert.ok(walker.move?.layoutRetreat);checks+=3;
  const destination={...walker.move.to},duration=walker.move.duration;
  for(let elapsed=50;elapsed<=duration+50;elapsed+=50){const before={x:walker.x,y:walker.y};walker.time=elapsed;walker.advance(50);assert.ok(Math.hypot(walker.x-before.x,walker.y-before.y)<18,'Relocation must run continuously, not jump.');if(!walker.parked)assert.ok(walker.safe(walker.x,walker.y));checks++;}
  assert.equal(walker.x,destination.x);assert.equal(walker.y,destination.y);checks+=2;
  walker.options.reduced=true;walker.w=600;walker.x=900;walker.y=100;walker.obstacles=[];walker.actor.dataset.escaping='true';
  walker.relocate();assert.equal(walker.move,null);assert.equal(walker.parked,false);assert.ok(walker.safe(walker.x,walker.y));assert.equal(walker.actor.dataset.escaping,'false');checks+=4;
  walker.options.reduced=false;walker.shell={dataset:{}};walker.label={textContent:''};walker.w=1440;walker.h=900;walker.obstacles=[{left:600,right:750,top:0,bottom:900}];
  walker.x=200;walker.y=200;
  assert.equal(walker.pathSafe({x:1100,y:200}),false);checks++;
  for(let run=0;run<50;run++){
    walker.walkManual({x:run%2?1100:180,y:100+(run%5)*100});
    assert.equal(walker.move.manual,true);checks++;
    walker.time+=100;walker.advance(100);
    assert.equal(walker.move,null);checks++;
    assert.ok(walker.x>=8&&walker.x+walker.options.size<=walker.w-8);checks++;
    assert.ok(walker.y>=12&&walker.y+walker.petH+44<=walker.h-8);checks++;
  }
  walker.manualPlacement=true;walker.walkManual({x:800,y:200});const ownedMove=walker.move;
  walker.setObstacles([{left:0,right:1440,top:0,bottom:900}]);assert.equal(walker.move,ownedMove);checks++;
  walker.manualPlacement=false;walker.move=null;walker.obstacles=[];
  walker.walkManual({x:-9999,y:9999});walker.time+=100;walker.advance(100);
  assert.equal(walker.x,8+Math.max(0,(148-walker.options.size)/2));assert.equal(walker.y,walker.h-walker.petH-52);checks+=2;
  console.log(JSON.stringify({passed:true,checks,seed:12026,scope:'offline viewport/obstacle/toolbar-clearance geometry',providerCalls:0}));
} finally {Math.random=originalRandom;}
