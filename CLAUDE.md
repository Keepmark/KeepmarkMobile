# CLAUDE.md — Keepmark (MAUI Blazor Hybrid, Android-only)

Source of truth is `README.md` in this directory (requirements, env, commands,
architecture map, async/UI patterns, engineering rules, safety-backup spec).
Edit that file, not this one. Repo-wide layout is in `../AGENTS.md` (loaded via
`../CLAUDE.md`). Behavioral requirements for review: `docs/verification-spec.md`.

Quick reminders (details in README):

- Own git repo (`KeepmarkMobile/`, branch `master`); the parent dir is not a repo.
- Builds need `ANDROID_HOME`, `JAVA_HOME=$HOME/jdk-21` first on `PATH`.
- Every user-facing change bumps `ApplicationDisplayVersion`/`ApplicationVersion`
  in `src/Keepmark/Keepmark.csproj` (+ `?v=` in `wwwroot/index.html` for CSS/JS),
  commit as `v1.0.N (M): description`, push. Docs-only changes don't bump.
- Never touch `src/Keepmark.Core/Crypto/` without running both interop checks.
- `SyncEngine`: every `_gate.WaitAsync()` releases in `finally`; never silently
  merge a 409; every destructive flow snapshots first (fail closed).

@README.md
