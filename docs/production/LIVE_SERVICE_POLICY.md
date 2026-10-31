# Live service policy — 7 October 2026

The 1.4.21 desktop package and its build-source commit remain unchanged. The
separately deployed Managed backend corrects conservative input-text coverage
after a coherent terminal provider receipt. This changes only the spending
guard; it does not invent final usage or change customer settlement.

## Production configuration

- `PaidSystem__DailySpendingCapsEnabled=false`: ordinary daily user/company
  spending ceilings are disabled. Defaults remain protective for an unconfigured
  deployment. Existing daily counters and numeric shard migration markers remain
  intact; do not reset or change them to disable enforcement.
- `PaidSystem__LiveSessionCreditMicrosCap=1000000000000000`, matching
  `config/modelRouting.routes.live_audio.maxSessionCostCreditMicros`: an
  arithmetic-safe, effectively nonrestrictive ceiling, not an upfront debit.
- Rolling reservations remain one credit. Actual usage, available credit,
  authentication, concurrency, provider ownership, prices and models are unchanged.
- Managed connection lifetime remains 720 seconds; the shared desktop prepares
  a quiet-turn rollover at approximately 9.5 minutes. Personal uses the same
  client rollover but direct provider transport and no Managed spending caps.

Provider/network errors can still interrupt a connection. Do not promise an
uninterrupted network or automatically replay a command whose effects are unknown.
Truthful interruption feedback and session recovery remain the accepted behavior.

## Financial safety is separate from ordinary spending limits

Unknown interrupted usage is not guessed into a charge or refund. The reviewed
new-enrollment reconciliation policy has its own waiting period, bounded company
loss allowance and durable review fence for repeated unresolved sessions. Those
are loss/security controls, not a daily paid-usage allowance. They remain active.
An operator must examine authoritative evidence before resolving an account that
has acquired a reconciliation-review fence; do not clear it on a timer.

## Release and rollback

The desktop installer is built from `07a57667055a1c47c7a1990ec0d45d7f20a0cd48`.
A later backend/site source commit is not its build provenance. The reviewed
backend source and deployment configuration must travel together. Preserve
compatible reviewed financial writers for rollback; do not roll back to a writer
that ignores loss/exposure/deletion fences. Route-policy rollback must use a
masked compare-and-set, never overwrite an entire routing or financial document.

Full current backend tests: 1,410 passed, zero failed/skipped, using a local
Firestore emulator. Unchanged installed-client policy slice: 81 passed. These
tests do not claim physical microphone/playback, real payment/refund or timed
production hold-disposition acceptance.
