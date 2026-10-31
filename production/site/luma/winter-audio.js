// Original procedural ambience: filtered wind, warm pads, glass harmonics.
// No recordings or music from reference websites are used.
import {BEAT_MS} from './crystal-dance.js';
export const MOODS={
 winter:{name:'Glacier night',description:'Cold air · distant glass chimes',wind:.50,cutoff:1200,pad:.35,tones:[146.832,220,293.665,369.994],bells:[587.33,880,1108.73],gap:10,bell:.018},
 lobby:{name:'Cozy starship',description:'Soft engine hum · warm cabin tones · distant sonar',wind:.12,cutoff:360,pad:.85,tones:[82.407,123.471,164.814,246.942],bells:[329.628,493.883,659.255],gap:8,bell:.024},
 dream:{name:'Moonlit music box',description:'Soft felt notes · floating harmony',wind:.06,cutoff:500,pad:.55,tones:[130.813,195.998,261.626,329.628],bells:[523.251,659.255,783.991,1046.502],gap:3.5,bell:.035},
 deep:{name:'Deep-space drift',description:'Low, slow chords · quiet air · no chimes',wind:.08,cutoff:260,pad:.95,tones:[65.406,97.999,130.813,164.814],bells:[],gap:20,bell:0}
};
export class WinterAudio {
 constructor({status,isPaused,wind,duck=()=>false}){this.status=status;this.isPaused=isPaused;this.wind=wind;this.duck=duck;this.enabled=false;this.disposed=false;this.volume=1;this.nodes=[];this.lastDanceKey=null;this.lastBeat=-1;this.nextBell=0;this.sync=this.sync.bind(this);this.visibility=()=>this.sync();document.addEventListener('visibilitychange',this.visibility);}
 build(){
  const Audio=window.AudioContext||window.webkitAudioContext;if(!Audio)throw new Error('Audio is unavailable in this browser.');
  const c=this.context=new Audio();this.master=c.createGain();this.master.gain.value=0;
  const compressor=c.createDynamicsCompressor();compressor.threshold.value=-14;compressor.ratio.value=5;this.master.connect(compressor);compressor.connect(c.destination);
  this.analyser=c.createAnalyser();this.analyser.fftSize=512;this.master.connect(this.analyser);this.samples=new Float32Array(512);
  this.room=c.createConvolver();const ir=c.createBuffer(2,c.sampleRate*3,c.sampleRate);
  for(let ch=0;ch<2;ch++){const a=ir.getChannelData(ch);for(let i=0;i<a.length;i++)a[i]=(Math.random()*2-1)*Math.pow(1-i/a.length,3)*.45;}
  this.room.buffer=ir;const wet=c.createGain();wet.gain.value=.28;this.room.connect(wet);wet.connect(this.master);
  const buffer=c.createBuffer(1,c.sampleRate*12,c.sampleRate),noise=buffer.getChannelData(0);let brown=0;
  for(let i=0;i<noise.length;i++){brown=(brown+.018*(Math.random()*2-1))/1.018;noise[i]=brown*3;}
  const source=c.createBufferSource();source.buffer=buffer;source.loop=true;
  const high=c.createBiquadFilter();high.type='highpass';high.frequency.value=100;
  this.windFilter=c.createBiquadFilter();this.windFilter.type='lowpass';this.windFilter.frequency.value=700;
  this.windGain=c.createGain();this.windGain.gain.value=.3;
  source.connect(high);high.connect(this.windFilter);this.windFilter.connect(this.windGain);this.windGain.connect(this.master);source.start();this.nodes.push(source);
  this.pads=[];
  for(const [i,f] of [146.832,220,293.665,369.994].entries())for(const detune of [-3,3]){const o=c.createOscillator(),g=c.createGain(),pan=c.createStereoPanner();o.type='sine';o.frequency.value=f;o.detune.value=detune;g.gain.value=i===0?.055:.026;pan.pan.value=(i/3-.5)*.7;o.connect(g);g.connect(pan);pan.connect(this.master);pan.connect(this.room);o.start();this.nodes.push(o);this.pads.push({o,g,i});}
 }
 async setEnabled(value){if(this.disposed)return;this.enabled=value;this.revision=(this.revision||0)+1;const revision=this.revision;
  try{if(value&&!this.context)this.build();if(value&&this.context)await this.context.resume();if(this.disposed||revision!==this.revision)return;clearInterval(this.interval);this.interval=value?setInterval(this.sync,120):null;this.sync();}
  catch(e){if(this.disposed||revision!==this.revision)return;this.enabled=false;this.report('Unavailable',e.message);throw e;}
 }
 report(state,message=state){this.status.textContent=message;this.status.dataset.audio=state.toLowerCase();this.status.dataset.context=this.context?.state||'not-created';}
 setVolume(value){if(!Number.isFinite(value))return;this.volume=Math.max(0,Math.min(1,value));this.sync();}
 setMood(value){if(!MOODS[value])return;this.mood=value;this.nextBell=(this.context?.currentTime||0)+2;this.sync();}
 sync(){if(this.disposed)return;if(!this.context){this.report('Off','Soft wind and distant crystal tones. Starts muted.');return;}
  const c=this.context,t=c.currentTime,m=MOODS[this.mood||'winter'],paused=this.isPaused()||document.hidden,target=this.enabled&&!paused?this.volume*(this.duck()?.25:1):0;
  for(const {o,g,i} of this.pads){o.frequency.setTargetAtTime(m.tones[i],t,.8);g.gain.setTargetAtTime((i===0?.07:.035)*m.pad,t,.8);}
  this.status.dataset.mood=this.mood||'winter';
  this.master.gain.setTargetAtTime(target,t,target?1.2:.08);
  const gust=Math.max(0,(this.gustUntil||0)-t)/4;
  this.windGain.gain.setTargetAtTime(m.wind*(.7+this.wind()*.5+Math.sin(t*.21)*.08)+gust*.18,t,.7);
  this.windFilter.frequency.setTargetAtTime(m.cutoff+Math.sin(t*.13)*m.cutoff*.15,t,1);
  if(this.enabled&&!paused&&c.state==='suspended'&&!this.resuming){this.resuming=true;c.resume().catch(()=>{this.enabled=false;this.report('Unavailable','Tap Soundscape again to resume.');}).finally(()=>this.resuming=false);}
  if(this.enabled&&!paused&&c.state==='running'&&t>this.nextBell){if(m.bells.length)this.bell(m.bells[Math.floor(Math.random()*m.bells.length)],m.bell,(Math.random()-.5)*1.1);this.nextBell=t+m.gap+Math.random()*m.gap*.5;}
  this.analyser.getFloatTimeDomainData(this.samples);let energy=0;for(const s of this.samples)energy+=s*s;this.status.dataset.rms=Math.sqrt(energy/this.samples.length).toFixed(6);
  this.report(!this.enabled?'Off':paused?'Paused':this.volume===0?'Muted':'Playing',!this.enabled?'Soundscape off':paused?'Soundscape paused':this.volume===0?'Soundscape muted':m.description);
  if((!this.enabled||paused)&&c.state==='running'){if(!this.suspendTimer)this.suspendTimer=setTimeout(()=>{this.suspendTimer=null;if(!this.disposed&&(!this.enabled||this.isPaused()||document.hidden))c.suspend().then(()=>this.sync()).catch(()=>{});},500);}else if(this.suspendTimer){clearTimeout(this.suspendTimer);this.suspendTimer=null;}
 }
 bell(frequency,volume=.025,position=0){if(!this.context||!this.enabled||this.isPaused()||document.hidden)return;const c=this.context,t=c.currentTime,pan=c.createStereoPanner();pan.pan.value=position;pan.connect(this.master);pan.connect(this.room);let remaining=3;
  for(const [ratio,level,decay] of [[1,1,2.8],[2.01,.22,1.8],[3.98,.07,.8]]){const o=c.createOscillator(),g=c.createGain();o.frequency.value=frequency*ratio;g.gain.setValueAtTime(0,t);g.gain.linearRampToValueAtTime(volume*level,t+.025);g.gain.exponentialRampToValueAtTime(.00001,t+decay);o.connect(g);g.connect(pan);o.start(t);o.stop(t+decay+.05);o.onended=()=>{o.disconnect();g.disconnect();if(!--remaining)pan.disconnect();};}
 }
 dance(key,elapsed){if(key!==this.lastDanceKey){this.lastDanceKey=key;this.lastBeat=-1;}if(key===null)return;const beat=Math.floor(elapsed/BEAT_MS);if(beat!==this.lastBeat){this.lastBeat=beat;this.thump(beat%2?130:65,beat%2?.07:.12);if(beat%2===0){const melody=[293.665,369.994,440,369.994,329.628,293.665,220,293.665];this.bell(melody[Math.floor(beat/2)%8],.035,Math.sin(beat)*.3);}}}
 thump(frequency,volume){if(!this.context||!this.enabled||this.isPaused()||document.hidden)return;const c=this.context,t=c.currentTime,o=c.createOscillator(),g=c.createGain();o.frequency.setValueAtTime(frequency,t);o.frequency.exponentialRampToValueAtTime(frequency*.45,t+.15);g.gain.setValueAtTime(0,t);g.gain.linearRampToValueAtTime(volume,t+.006);g.gain.exponentialRampToValueAtTime(.00001,t+.22);o.connect(g);g.connect(this.master);o.start(t);o.stop(t+.24);o.onended=()=>{o.disconnect();g.disconnect();};}
 gust(){if(!this.context||!this.enabled||this.isPaused())return;this.gustUntil=this.context.currentTime+4;this.sync();}
 destroy(){if(this.disposed)return;this.disposed=true;this.enabled=false;this.revision=(this.revision||0)+1;clearInterval(this.interval);clearTimeout(this.suspendTimer);document.removeEventListener('visibilitychange',this.visibility);this.nodes.forEach(n=>{try{n.stop();}catch{}});this.context?.close().catch(()=>{});}
}
