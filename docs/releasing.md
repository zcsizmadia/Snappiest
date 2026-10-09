# Releasing

Releases are published to nuget.org by `.github/workflows/release.yml` using
[NuGet trusted publishing](https://learn.microsoft.com/nuget/nuget-org/trusted-publishing): the workflow exchanges its
GitHub OIDC token for a short-lived API key, so no long-lived key is stored in the repository.

## One-time setup

1. **nuget.org**: sign in, open *Trusted Publishing*, and add a policy for GitHub Actions:
   repository owner `zcsizmadia`, repository `Snappiest`, workflow file `release.yml`, environment `nuget`.
   A policy for a private repository starts temporarily active for 7 days and becomes permanent after the first
   successful publish; if the window runs out, re-activate it on the same page.
2. **GitHub**: in *Settings > Environments* create an environment named `nuget` (optionally add yourself as a required
   reviewer to approve every publish), and add the repository secret `NUGET_USER` with your nuget.org profile name
   (the user name, not an email address).

## Cutting a release

1. Set `<VersionPrefix>` in `src/Snappiest/Snappiest.csproj` to the new version and merge that to `main` through a pull
   request.
2. Optional dry run: *Actions > Release > Run workflow* on `main` runs the tests and packs the package (version
   `<prefix>-ci.<run>`), and publishes nothing.
3. Tag and push: `git tag v0.9.0 && git push origin v0.9.0`.
   The workflow checks that the tag matches `VersionPrefix`, runs the tests on Linux x64/arm64 and Windows, packs,
   waits for approval if the environment requires it, pushes the `.nupkg` and `.snupkg` to nuget.org and creates the
   GitHub release with generated notes. Tags with a suffix (`v0.10.0-rc.1`) are published as pre-releases.

A published version cannot be replaced on nuget.org, only unlisted, so check the dry run before tagging.
