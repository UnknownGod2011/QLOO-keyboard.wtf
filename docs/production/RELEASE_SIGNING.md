# Release signing and updater trust

keyboard.wtf update manifests are authenticated with ECDSA P-256 and SHA-256.
The desktop embeds only public keys. The current key ID is
`kwt-release-2026-01`; its private key must remain outside the repository,
GitHub Actions, build artifacts, logs, and the installed application.

The signed payload binds the channel, version, minimum compatible version,
publication time, approved GitHub Release URL, installer SHA-256 and byte size,
release-notes digest, and signing-key ID. The updater rejects unknown keys,
modified metadata, downgrades/replays, unsupported upgrade paths, excessive
future publication times, non-HTTPS downloads,
unapproved hosts, redirect escapes, oversized/incomplete packages, and hash
mismatches. An update is downloaded only after manifest verification and is
installed only after a user explicitly chooses Install.
An older signed manifest for a newer compatible version remains usable after
a long offline period; publication age alone is not an expiration rule.

## Signing a candidate

Build and test a clean commit first. Keep the private key at a restricted path
outside the clone. The following is a template, not an instruction to publish
the current preparation source: replace `VERSION` and `MINIMUM_VERSION` with
the approved exact release and clean-upgrade acceptance values. Then run:

```powershell
dotnet run --project tools/KeyboardWtf.ReleaseSigner -c Release -- `
  --package dist/installer/keyboard-wtf-setup.exe `
  --output production/site/releases/stable.json `
  --private-key "$env:USERPROFILE/.keyboard-wtf-release-signing/kwt-release-2026-01-private.pem" `
  --release-notes docs/releases/VERSION.md `
  --channel stable `
  --version VERSION `
  --minimum-version MINIMUM_VERSION
```

The signer derives the public key and verifies the completed manifest before
writing it atomically. This proves internal signature consistency, not that
the supplied private key matches the desktop's embedded trusted key. Before
feed publication, an exact-artifact gate must validate the final manifest
using `new UpdateManifestVerifier()` with its default embedded trust set, the
intended channel and the oldest accepted installed version. Verify its hash
and size against the exact accepted installer bytes. A wrong private key must
fail this gate even when its supplied key ID is `kwt-release-2026-01`.

Review the manifest, installer hash, CI provenance and SBOM before publishing.
Never sign an artifact rebuilt after Beta validation. ECDSA feed signing does
not give an unsigned EXE an Authenticode publisher identity; do not claim it
does or suppress the documented Windows/SmartScreen limitation.

## Read-only exact-artifact verification

The existing release tool now has a separate `verify` subcommand. The template
below reads local files only; it does not sign, download, install, publish or
write a receipt file. Replace every placeholder with the approved exact inputs.

```powershell
dotnet run --project tools/KeyboardWtf.ReleaseSigner -c Release --no-build -- verify `
  --manifest APPROVED_MANIFEST_JSON `
  --package EXACT_ACCEPTED_INSTALLER/keyboard-wtf-setup.exe `
  --application EXACT_ACCEPTED_PUBLISH_DIR/keyboard.wtf.exe `
  --release-notes EXACT_SIGNED_NOTES_MD `
  --provenance EXACT_RELEASE_PROVENANCE_JSON `
  --channel stable `
  --version VERSION `
  --oldest-client-version OLDEST_ACCEPTED_VERSION `
  --source-commit APPROVED_FULL_40_HEX_COMMIT
```

There is no command-line trust/key override. Verification uses the desktop's
default embedded manifest trust, exact canonical version/tag/package URL,
signed installer hash/size, exact notes, PE file versions and the current
six-field CI provenance declaration. Binary reads deny write/delete sharing
during those checks. Inputs and console diagnostics are bounded; failures do
not print source text, paths or upstream exception content.

The signed compressed installer/download envelope remains 250 MiB. The
separately supplied unpacked self-contained application has a 512 MiB local
verification resource bound, not a new product or download limit. Those sizes
must not be conflated: the protected accepted application is 315,239,886 bytes
while its installer is 84,394,650 bytes. Empty or oversized application inputs
are refused before metadata/hash work. The application hash remains observed,
not authenticated by the existing installer-only manifest.

An exit-zero receipt proves only those stated comparisons. It explicitly does
NOT establish public flavor, Authenticode, reproducible-build/source attestation
or that the separately supplied application is embedded in the installer.
Application SHA-256 is recorded for later installed/publish comparison, not
authenticated by the current manifest schema. The package extraction/install,
canonical identity, SBOM/package allowlist and clean-Windows acceptance gates
must establish those remaining relationships before publication.

## Rotation and compromise

For normal rotation, ship a desktop version containing both the old and new
public keys, wait for adoption, sign with the new key ID, and remove the old key
only in a later release. For suspected private-key compromise, stop publishing
updates, remove the compromised key from the feed and next trusted desktop,
preserve GitHub/audit evidence, rotate repository and release credentials, and
distribute the recovery build through an independently verified installer path.
Changing an installer hash without a valid non-revoked manifest signature must
never be accepted as recovery.
