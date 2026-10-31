# Luma product-surface adapter (local preparation only)

The crystal atlas is a byte-identical copy of the verified original Luma pet:
`3968af492f7be6cb2c8c1b62d7b81544311af29aeaf2b2a79358594cc39cda81`.
The exported/installed pet and `luma-safe-v1` were not edited.

The original motion/rendering modules, articulated dance, staged crystal growth,
glass/water solver, original procedural ambience and snow/hat are reused here.
The playground controls and fake installation demonstration are not included.
The renderer is adapted for a zero-footprint viewport overlay, layout-aware
content clearance, explicit rare scheduling and animation suspension. Its old
global `body` classes, automatic demo chatter and global speech cancellation
are removed. No sprites are repainted. Artwork uses canvas alpha and restrained
dark-theme drop shadows; there is no painted purple border or pulsing aura.

## Host contract

Import `mountLuma` from `luma-site.js`, load `luma-ui.css`, and provide a fixed,
pointer-transparent root. Call `mountLuma({surface:'website'|'settings',root,voiceAdapter?,allowedState?})`.
Returned methods: `destroy`, `hide`, `show`, `tour`, `react`, `setTheme`,
`updateAllowedState`, `diagnostics`. Diagnostics contain only presentation state.

The host controls theme through `data-theme` and `luma:theme` with
`detail:{theme:'light'|'dark'}`. Root-specific theme is supported. No broad page,
selection, filesystem, account, credential or settings enumeration occurs.
`product-guide.js` and `product-facts.json` document the deliberately limited
product grounding; the server must validate its own independent context.

Optional voice adapter: `start({onState,onCaption,onError,onStop})`, `stop()`,
`setMuted(boolean)`. Without it the Voice icon explains availability truthfully;
captions, tour and controls remain usable. This adapter never starts Jarvis,
changes modes, switches credentials, executes actions, or charges an account.

## Lifecycle and consent

- Captions introduce Luma after its atlas loads. Tour is dismissible/replayable.
- Narration and ambience start off. They require an explicit user control.
- Microphone use requires the distinct pet Voice control plus browser permission.
- Pause/hide/out-of-view/hidden-document suspend effects and animation. Voice is
  stopped rather than silently holding a microphone/socket. Page teardown removes
  listeners, RAF work, observer, effects contexts and pet-only audio resources.
- Pointer effects never filter, move, sample or distort page text. Touch scrolling
  does not continuously inject the wake. GPU failure leaves the guide usable.
- No pet column, footer section or spacer changes the page layout. Roaming uses
  the entire viewport and cached content rectangles, refreshed on scroll/resize
  and layout changes rather than scanning the DOM per animation frame. If a
  dense view has no clear spot, the actor stays hidden instead of blocking it.
  An explicitly opened guidance popover can overlap a dense mobile view; an
  unsolicited welcome is suppressed when no clear bubble space is available.
- The four icons are actor children, centered directly below it. Only the
  bottom hover/focus region (or a touch tap) reveals them. No permanent footer
  controls are shown. Settings (or right-click/Shift+F10) opens customization and
  accessibility options. No emotion/weather knobs exist.
- Dark mode automatically attaches the original hat and starts gentle snow.
  Random gusts occur every 8–16 seconds after a short randomized initial gap.
  Light mode removes snow/hat; reduced-motion/pause/hidden views stop snowfall.
- Dancing waits 8–15 minutes; regrowth/teleport waits 6–12 minutes. No demo buttons
  force those rare behaviors. Reduced-motion suppresses roaming/effects.
- Stored preferences are optional local presentation flags only, never keys/data.

## Provenance and notices

`licenses/react-bits.md` preserves the upstream MIT + Commons Clause notice for
the adapted fluid solver. Do not redistribute that component as a standalone
product. Original procedural audio/snow are not copied recordings or assets
from Igloo or Lusion. `assets/HAT-GENERATION.md` preserves generated hat provenance.
These files preserve existing notices; they are not a new legal clearance.

## Acceptance boundary

JavaScript syntax and unchanged artwork hashes are independently verifiable
offline. Host-page rendering, focus/tap, themes, permissions, audio balance and
provider eligibility are separate gates. A local implementation is not an
installed desktop acceptance, live-provider proof or public deployment.

Local preparation checks on 6 October 2026: syntax checks passed for every reused
and adapted JavaScript module. The real local website was inspected at 1440 × 900
and 390 × 844 in light/dark themes. Caption tour start/skip/replay, three cursor
cycles, pause, hide/reopen, optional ambience, manual mood retention and truthful
unconfigured-voice handling passed; fresh console reads reported zero errors or
warnings. Page text had no filter/displacement; no horizontal overflow was found;
both Windows download URLs remained unchanged. Automatic dark snow and the
frame-attached realistic hat were visually inspected, not forced by a demo.
Screenshots are under `output/playwright/luma/` in the local checkout.

Two additional offline tests: 50 DOM lifecycle cycles using a fake, non-provider
voice adapter proved exactly 50 starts/stops/disposals, paused-voice no dispatch,
hide cleanup and idempotent destruction. Another 200 Node cases resolved/rejected
delayed audio-context resume after destruction: zero orphan intervals/listeners,
200 closed contexts. These are lifecycle tests, not microphone/provider/listening
acceptance. No billable/provider request was made by these checks.
Another 300 edge-peek plans across the 200px Settings and 310/360px website
habitats passed finite-coordinate, full-outside-edge, return-to-destination and
vertical-clearance checks. Only this adapter copy’s old demo footer offset changed.
Those earlier habitat checks predate the requested visual correction. The final
overlay has 8,852 offline assertions covering full-height roaming,
obstacle rejection, bottom-toolbar clearance, fully occupied views, continuous
walk relocation, reduced motion and animation-health recovery, plus a
fresh 50-cycle fake-voice teardown run. `scripts/luma/check-roaming.mjs` is the
repeatable no-provider geometry gate.

Loading waits for decoded atlas and hat assets. Eight stable frames warm up the
actor; severe/repeated stalls hide it and freeze its animation time until stable
recovery. Caption/Stop controls remain available. Optional effects back off after
a real stall rather than repeatedly rebuilding expensive GPU work. This is a
Luma-only presentation safeguard, not Jarvis audio/Live health logic.

Scrolling/layout changes use a short, continuous escape run to the nearest
clear position, not random relocation. Its hit target is disabled during escape.
Explicit guidance bubbles keep a fixed readable viewport anchor while scrolling;
only viewport fitting can move them. Peek is allowed at an unobstructed entrance,
then has a 4–8 minute cooldown. No hello can cut an entrance/peek short.
