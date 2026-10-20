# Notes for Claude

## Don't assume something is blocked: try it

Before telling the user you can't do something (publish, trigger a workflow, push, read results), try it.
Only say it's blocked after an actual call was refused, and quote the error.

On 2026-10-20 Claude told the user it couldn't publish their Mod DB Uploader app because two other GitHub
calls had been refused. It never tried. `gh workflow run` then worked on the first try.

What was tested from a Claude session on 2026-10-20 (this can change, so still try first):

| Works | Refused by the session's proxy |
|---|---|
| `git push` to branches, including `main` | Repository settings writes (`gh api -X PATCH repos/...`, e.g. changing the default branch) |
| | Pushing a tag (`git push origin v2.0.0`: HTTP 403), and making one through the API (`POST repos/.../git/refs`: "Write access to this GitHub API path is not permitted through this proxy"). Publish through the workflow instead (below) |
| `gh workflow run` (workflow_dispatch) | `repos/.../actions/permissions` |
| Reading runs, jobs and steps: `gh api repos/.../actions/runs`, `.../runs/<id>/jobs`, `gh run watch <id>` | Job logs and artifact downloads: they redirect to `*.blob.core.windows.net`, which is blocked |
| Check-run annotations: `gh api repos/.../check-runs/<job id>/annotations` | GraphQL (`gh repo view`, `gh pr list`, `gh release download`): use `gh api` REST calls instead |
| Downloading a release file: `gh api -H "Accept: application/octet-stream" repos/.../releases/assets/<asset id> > file` | |
| GitHub itself (`git clone`, `github.com`) | NuGet, `cdn.vintagestory.at`, apt and Microsoft's .NET downloads: the workspace has no .NET SDK, so builds happen on GitHub Actions |

## Building and reading CI results

GitHub Actions (`.github/workflows/build.yml`) builds every push against the official Vintage Story server
files and runs `tests/PlannerTests`. Since job logs can't be read from a session, a failing build or test step
puts the end of its output into an annotation (read it with the check-runs annotations call above), and the
test results are notices on the run. To publish a release, run the workflow with `release=true`:
`gh workflow run build.yml -R demonic69/Automatic-Clay-Former- --ref main -f release=true`. It makes the tag
(`v` + the version in `modinfo.json`) and the release itself, with `RELEASE_NOTES.md` as its notes. Do that
only when the user asks.
