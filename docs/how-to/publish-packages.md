# Publish Hugo Packages with Docker CI

Ship NuGet artifacts directly from the containerized CI build so every release uses the exact bits that passed the test matrix.

## What you will do

- Update package versions and create a signed release tag.
- Let `.github/workflows/docker-ci.yml` build, test, and pack the libraries inside `docker/ci/Dockerfile.ci`.
- Push the generated `.nupkg`/`.snupkg` files to NuGet.org and GitHub Packages.
- Attach the packages to an auto-generated GitHub Release.

Estimated time: 10–15 minutes once the release PR is green.

## Prerequisites

- .NET 10 SDK installed locally (matches `global.json`).
- Maintainer access to `github.com/df49b9cd/Hugo`.
- A NuGet.org account configured for [Trusted Publishing](https://learn.microsoft.com/nuget/nuget-org/github-trusted-publishers) and added as a co-owner of every `Hugo.*` package (the username is stored in `NUGET_PUBLISHER_USERNAME`).
- Repository secrets:
  - `NUGET_PUBLISHER_USERNAME` — the NuGet.org account that requested Trusted Publishing.
  - Optional `CODECOV_TOKEN` for coverage uploads (already used elsewhere).
- Workflow permissions left at their defaults (`contents: write`, `packages: write`, `id-token: write`).

## Step&nbsp;1 — Bump versions and validate locally

1. Update the `<Version>` property in each packable project under `src/`.
2. Run the full test sweep locally:  
   `dotnet test Hugo.slnx -c Release`.
3. Commit the changes with a conventional message such as `chore: prepare v1.2.3`.

## Step&nbsp;2 — Tag the release

Create and push a tag that matches the workflow filter (`v*`):

```bash
git tag v1.2.3
git push origin v1.2.3
```

Only `push` events on tags that start with `v` trigger publication, so regular pushes and pull requests keep running tests without producing packages.

## Step&nbsp;3 — Let Docker CI build, test, and pack

- `docker/ci/Dockerfile.ci` restores, builds, and runs the unit/integration/feature suites, then packs every `src/Hugo*.csproj` into `/repo/artifacts/packages`.
- The workflow copies `artifacts/packages` off the CI container, uploads them as a build artifact, and sets up the .NET SDK on the runner for publishing.
- `NuGet/login@v1` exchanges the repository OIDC token for a short-lived API key, so no permanent `NUGET_API_KEY` secret is required.

Monitor the “docker ci” run until the `Publish packages to NuGet.org`, `Publish packages to GitHub Packages`, and `Create GitHub Release` steps succeed.

## Step&nbsp;4 — Verify registries and release assets

1. NuGet.org: confirm the new version appears for each `Hugo*` package (packages propagate within a few minutes).
2. GitHub Packages: visit `https://github.com/orgs/df49b9cd/packages` and ensure the versions match NuGet.
3. GitHub Release: check the auto-generated release for tag `v1.2.3` and download the attached `.nupkg`/`.snupkg` files if needed.

## Troubleshooting

- **Missing packages:** Ensure the Docker build completed; the workflow uploads `packages` as an artifact even on failed publish attempts.
- **NuGet push failures:** Trusted Publishing must list this repository; re-run the workflow after approving the association in NuGet.org under “Publisher settings”.
- **GitHub Packages rejects uploads:** Verify `PackageId` matches the repository owner namespace (e.g., `Hugo.Diagnostics.OpenTelemetry`) and that the `GITHUB_TOKEN` still has `packages: write` permission.
- **Release skipped:** Tag must start with `v` (e.g., `v1.2.3`). Retag or push a corrected lightweight tag if necessary.
