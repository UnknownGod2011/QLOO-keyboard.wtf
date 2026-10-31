import {createLumaVoiceAdapter} from './luma/luma-live.js';

const topic = () => {
  const name = location.pathname.split('/').pop().replace(/\.html$/, '');
  return ({pricing:'pricing',account:'account',support:'support',changelog:'support'})[name] || 'overview';
};

/** No permanent key or account records enter the pet context. No automatic POST replay. */
export function createPublicLumaVoiceAdapter() {
  if (['localhost','127.0.0.1','[::1]'].includes(location.hostname)) {
    return createLumaVoiceAdapter({
      canStart:async({signal,phase='start'}={})=>{
        // The active session owns its issued grant. Admission cooldown is only
        // for a NEW connection, never a reason to stop the healthy owner.
        if(phase==='health')return true;
        const response=await fetch('/api/luma/voice-available',{signal,cache:'no-store',credentials:'same-origin'});
        const status=await response.json();
        if(!response.ok||status.available!==true)throw new Error(status.message||'Luma voice is not configured in this local preview. The caption tour still works.');
        return true;
      },
      requestToken:async({signal}={})=>{
        const response=await fetch('/api/luma/live-token',{method:'POST',signal,credentials:'same-origin',headers:{'Content-Type':'application/json'},body:JSON.stringify({section:topic()})});
        const grant=await response.json();
        if(!response.ok||!grant.token)throw new Error(grant.message||'Luma voice preview is temporarily unavailable.');
        return grant;
      },
    });
  }
  return createLumaVoiceAdapter({
    requestToken: async ({signal} = {}) => {
      if (['localhost','127.0.0.1','[::1]'].includes(location.hostname)) {
        throw new Error('This local website preview has no public voice credentials. The tour still works; Settings uses its protected Personal configuration.');
      }
      const {api} = await import('./app.js');
      try {
        return await api('/v1/luma/live-token', {
          authenticated:true,
          retryAuthentication:false,
          method:'POST', signal,
          body:JSON.stringify({section:topic()}),
        });
      } catch (error) {
        if (error?.status === 401 || error?.code === 'firebase-signed-out')
          throw new Error('Sign in on Account to talk with Luma. The caption tour works without signing in.');
        if (error?.status === 429)
          throw new Error('Luma voice has reached its short-session limit. Please try later.');
        throw new Error('Luma voice is not available right now. The tour and text guidance still work.');
      }
    },
  });
}
