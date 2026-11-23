# Releasing Curato

Mirrors the sibling project MediaCurator's release discipline, adapted for a
Docker-first server (see `PLAN.md` §18). The Windows-hosted server/Velopack path is
paused (`PLAN.md` §1) and not part of this flow yet.

## Steps

1. Bump the version. Single source of truth: `<Version>` in `Curato.Api/Curato.Api.csproj`
   (semver — MAJOR for breaking changes, MINOR for features, PATCH for fixes).
2. Commit with a message that **starts with the literal prefix `Release `** (e.g.
   `Release 0.2.0`) and push to `main`. That exact prefix is what CI's skip-guard in
   `.github/workflows/build.yml` matches on, to avoid a redundant build right before
   the tag push triggers the real one.
   ```
   git commit -m "Release 0.2.0"
   git push origin main
   ```
3. Tag and push:
   ```
   git tag v0.2.0
   git push origin v0.2.0
   ```
4. CI builds and pushes a `linux/amd64` Docker image to `ghcr.io/bleze/curato`, tagged
   `0.2.0` and `sha-…`, then publishes a GitHub Release with auto-generated notes.
   (`latest` is published on every push to `main`, independent of releases — that's
   what the NAS's `deploy/update.sh` pulls.)
5. Sanity-check: pull the new image on a test NAS/machine and confirm it starts and
   serves the UI before considering the release done.

## Rules

- Never move or retag an already-pushed tag. If a release build is bad, cut a new
  patch version and tag instead.
- One-time repo setting (from MediaCurator's runbook — still applies here): **Settings
  → Actions → General → Workflow permissions → "Read and write permissions"**, or the
  release job's GitHub Release step fails on permissions.
