# Notes for Claude

## Where this repository lives

Since 2026-10-31 it's **github.com/Comp-any/Automatic-Clay-Former-** (moved from demonic69/Automatic-Clay-Former-;
git still forwards the old address). Use the new name in `gh api repos/...` calls: the old one answers with a redirect
to `repositories/<id>`, which the session's proxy refuses ("Numeric-ID repository paths ... are not supported").

A session that has the old demonic69 copy attached can't attach the Comp-any one (add_repo: "a different
repository named ... is already attached"), so it has no API access to it: pushing still works through the old
address, and the public pages (`https://github.com/Comp-any/Automatic-Clay-Former-/actions`, `/releases`) can be
read with WebFetch. Start a new session with Comp-any/Automatic-Clay-Former- as its source for full access.

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
test results are notices on the run.

**Publishing:** a push to `main` whose `modinfo.json` version has no release yet publishes it by itself once it has
built and passed the tests (tag `v` + the version, the zip, `RELEASE_NOTES.md` as the notes). So publishing a new
version is: raise the version, update `RELEASE_NOTES.md`, push to `main`. Running the workflow with `release=true`
(`gh workflow run build.yml -R Comp-any/Automatic-Clay-Former- --ref main -f release=true`) also publishes, and
replaces the zip of an existing release.

**Always publish each new version** once its build and tests pass, without asking first (the user's
instruction, 2026-10-20). The one exception: if you asked the user a question about that work and they haven't
answered, wait for the answer. Bump the version in `modinfo.json` and update `RELEASE_NOTES.md` first.
