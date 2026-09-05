# Security

## The incident (2026-08-16)

AI translation stopped working. Two separate things caused it.

**1. A key was hardcoded and published.**

```
Services/GeminiService.cs:28
private const string DefaultApiKey = "AIzaSyCuYg…REDACTED…7HDw";
```

That line is in all four commits of the public repository. Google's secret scanner finds keys
like this automatically and revokes them — it does not need anyone to *use* the key, only to
publish it. A key in the same Google Cloud project was subsequently flagged with
`"Your API key was reported as leaked. Please use another API key."`

**2. The app then made a temporary failure permanent.**

`GeminiService.RecordHealth` wrote `ModelState.Unauthorized` to `settings.json` with
`CooldownUntil: null`, and `ModelChain.UsableInOrder` skipped unauthorized models
unconditionally. Once all three models in the chain were marked, the chain yielded *nothing* on
every subsequent lookup — no request was ever sent again. The key recovered; the app had no way
to find out.

### Fixed

- Every unhealthy model state is now time-boxed (`ModelChain.CooldownFor`). Nothing is permanent.
- Cached health is discarded when the API key changes, tracked by a salted fingerprint so the key
  itself never lands in the plaintext `settings.json`.
- "Test connection" resets health before probing, so it reports what the API does *now*.
- Regression tests: `ModelChainTests.UnauthorizedModel_IsRetried_AfterCooldownExpires` and
  `UnhealthyModel_WithNoCooldown_IsUsable`.

### Still to do — requires manual action

- [ ] **Revoke the key beginning `AIzaSyCuYg`** (full value in the git history) at
      https://console.cloud.google.com/apis/credentials
- [ ] **Scrub it from git history** and force-push:
      ```
      pip install git-filter-repo
      git filter-repo --replace-text <(echo '<the full key>==>REDACTED')
      git push --force --all
      ```
      Rotating without scrubbing does not help — the scanner re-reads history.
- [ ] **Create replacement keys in a fresh Google Cloud project**, not the flagged one.

## How keys work now

| Where | What | Protection |
|---|---|---|
| `.env` next to the exe | Dev-only, plaintext | Gitignored, deleted on first run — but only once the encrypted copy is confirmed written |
| `bundled.keys` next to the exe | What actually ships | AES, key derived from an app constant |
| `%LOCALAPPDATA%\Tarjem\keys.dat` | Runtime store — shipped keys, plus the user's own under a `USER_` prefix | DPAPI, per Windows user |
| `settings.json` | No keys at all | n/a |

### User-supplied keys (changed 2026-08)

Keys typed into Settings or the welcome flow used to be written to `settings.json` in plaintext,
even though `SecureKeyService` already had the encryption path — it was simply never called.
`settings.json` is the file people copy between machines, attach to bug reports and sync to cloud
folders, so that was the same class of exposure as the incident above, just slower.

They now go straight into `keys.dat` under a `USER_` prefix, which keeps them distinct from the
shipped key (the app has to know which one you're on to tell you the shared one may be slow).
`AppSettings` keeps the four old properties for one purpose only: on startup `SettingsService`
moves any value it finds there into the encrypted store and blanks it — and blanks it *only* if
the encrypted write is verified, so a failed DPAPI write can't destroy the key.

Packaging: put the keys in `.env`, run `Tarjem.exe --pack-keys`, delete the `.env`, ship
`bundled.keys`. See `API-KEYS-TO-GET.txt`.

### `bundled.keys` is obfuscation, not encryption

The unlock passphrase is a constant inside a decompilable app. Anyone determined gets the keys
out in minutes, and that is an accepted trade: these are free-tier keys shared by every install,
and the app tells users so and offers to use their own instead.

What it *does* defeat is the thing that actually caused the incident. Automated scanners match on
the literal shape of a key (`AIza…`). A key that never appears in that shape — not on disk, not
in a repo, not in a zip someone uploads — is never auto-flagged and auto-revoked. That is the
entire threat being defended against, and obfuscation is sufficient for it.

The one rule with no exceptions: **a key must never appear as a literal in a `.cs` file.** That is
what happened, and it is the only failure mode here that is genuinely unrecoverable.

## User data

Everything stays on the machine. `%LOCALAPPDATA%\Tarjem\` holds `settings.json`, `history.json`,
`keys.dat` and 7 days of logs. The only thing that leaves is the word or region being translated,
sent to whichever provider is selected. History can be turned off entirely, and "Clear all data"
removes the lot including stored keys.
