# AGENTS Instructions

## Git

- Write commits in [Conventional Commits 1.0.0](https://www.conventionalcommits.org/en/v1.0.0/) format.
- Start the type, the scope and the description in lowercase. Keep the case of code identifiers and proper nouns.
- For a non-trivial change, write a commit body that explains why.
- Mark a breaking change with `!` and a `BREAKING CHANGE:` footer.
- Author commits as the user alone. Add no agent trailers or links, such as `Co-Authored-By` or a session URL.
- Commit as the plan progresses.
- Before you push, squash related commits into one commit to keep the history clean.
- Push only after the user approves that push.

## Releases

- Version releases with [Semantic Versioning 2.0.0](https://semver.org/).
- Name each release tag with the bare version, such as `1.2.3`. If the repo already uses a tag prefix, such as `v`, keep it.
- When you create a changelog, use [Keep a Changelog 1.1.0](https://keepachangelog.com/en/1.1.0/) format. Keep the format of an existing changelog.
- Release in this order:
  1. Update the version files and the changelog in one commit, such as `chore(release): prepare 1.2.3`.
  2. Push the commit to the default branch.
  3. Wait until the branch pipeline of that commit passes.
  4. Tag that commit with an annotated tag: `git tag -a 1.2.3 -m "Release 1.2.3" <commit>`. Tag a release candidate as `1.2.3-rc.1`.
  5. Push the tag alone: `git push origin 1.2.3`. The tag pipeline creates the release and publishes any package.
- Push the commit and the tag in separate pushes. Do not use `git push --follow-tags`. One push starts two overlapping pipelines, and a single runner queues the tag pipeline.

## Tests

- Add a test to the repo only when it guards behavior that no existing test covers.
- The test must fail when that behavior breaks. Weigh this before you add each test.
- Run throwaway tests freely to check your work.
- Keep throwaway tests out of the repo unless they meet the bar above.
- Before each commit, run these commands and make sure that both pass:
  1. `dotnet build WorkLens.slnx -c Release -warnaserror`
  2. `dotnet test --solution WorkLens.slnx -c Release`
- The test platform sometimes crashes with `Releasing the double mapped memory failed` or an MSBuild node failure. Run the tests again once before you debug it.

## CI

- Keep a CI check only when it catches a concrete failure that no existing check covers.
- Identify that failure before you add or keep the check.
- Apply the test rules to CI checks too.
- Run the repo's entry points against real inputs or isolated fixtures.
- Reject duplicate checks, source-text assertions, copied inventories and checks that only prove that a command or file exists.
- Propagate command failures.
- Count a missing prerequisite or skipped required coverage as a failure.
- Run the checks in branch and pull request pipelines, never in tag pipelines. A tag pipeline builds and smoke-tests any package, publishes it, and then creates the release.
- Start each release job with a check that the tagged commit is on the default branch.
- Match release jobs to the tag format of the version, such as `/^[0-9]+\.[0-9]+\.[0-9]+(-[0-9A-Za-z.-]+)?$/`.
- Protect the release tags. Only protected refs can read the release credentials.
- A tag pipeline uses the CI config of the tagged commit. Merge a CI change to the default branch before you tag.
- After you change these rules, audit the repo against them.
- Validate changes locally. Then verify the required CI on the pushed commit.
- Report each uncovered contract and each unavailable check.
- Name CI files in kebab-case by purpose, such as `deploy-production.yml`.
- Keep reserved host filenames, including `.gitlab-ci.yml`.
- Name workflows in Title Case, such as `Deploy Production`.
- Keep the repository name out of workflow names.
- Name the workflow that validates the whole repository by its action alone, such as `Validate`.
- Name job IDs in snake_case, such as `build_image`.
- Start each GitHub step name with a verb, such as `Install Dependencies` or `Run Tests`.
- Preserve proper nouns and code identifiers when applying case.
- Prefer a specific purpose, such as `deploy-production.yml`, over a vague name, such as `production.yml`.
- Omit redundant host or artifact words, as in `github-action-build.yml` and `build-workflow.yml`.
- Name each workflow, job and step by its target and action.
- Keep job display names unique within the repository.
- Update dispatch references, dependencies, release gates, required check contexts and badges together when renaming CI artifacts.
- Run the checks in a workflow on `pull_request` and on `push` to branches.
- Run the release in a separate workflow on `push` of tags that match `[0-9]+.[0-9]+.[0-9]+` or `[0-9]+.[0-9]+.[0-9]+-*`. GitHub tag filters are globs.
- Check out release jobs with `fetch-depth: 0`, so that the ancestry check sees the default branch.
- Keep release secrets in an environment that allows only the release tags. Protect the tags with a tag ruleset.

## Boundaries

- WorkLens calls no LLM and depends on no LLM SDK. The caller writes `report.json`. WorkLens validates it, then lays it out and renders it.
- Only the entry project `src/WorkLens.Cli` reads environment variables and config files. Other projects get values as arguments.
- The user supplies the source tokens in environment variables. Never write a token to a file, a log or a test fixture.

## Language

- Write the docs under `docs/` and the README in Traditional Chinese.
- Write code, code comments, `AGENTS.md` and commit messages in English.

## Docs

- Keep each doc concise and plain. Each sentence must carry information.
- Write only standing rules in `AGENTS.md`. A standing rule changes how an agent does later, unrelated tasks.
- Record a fact about one place or one failure at its source: a code comment, a test, a commit body, an ADR or an issue.
- Write English docs in US English, to the [ASD-STE100](https://www.asd-ste100.org/) writing rules:
  - Give one instruction in each sentence.
  - Write instructions in the imperative and in the active voice.
  - Use one word for one meaning, and one meaning for one word.
  - An instruction has 20 words or fewer. A description has 25 words or fewer.
- Review each sentence over these limits. Split it if it holds more than one instruction or fact.
- Use a numbered list for three or more instructions in a fixed order, also across sentences.
- Write a set of full-sentence items, or of more than five items, as a bullet list. Otherwise, the set can stay in one sentence, even when the sentence is over the limits.
- Write a non-English doc in two passes:
  1. Write an English draft to these rules.
  2. Translate the draft:
     1. Keep its sentence boundaries and one term for each concept.
     2. Keep commands, paths and code unchanged.
     3. Use the natural word order of the target language.
- Read `docs/architecture/overview.md` before you add a project or a package reference. Tests enforce its dependency rules.
- Read `docs/design/domain-model.md` before you change an IR model or a module contract.
- Read `docs/design/schema-versioning.md` before you change a file under `schemas/`.
- Read `docs/design/cli-ux.md` before you change a command, an option, an error code or an exit code. Update it in the same change.
- Read `docs/security/security-privacy.md` before you change token handling, logging or the collected content.
- Record each architecture decision as an ADR in `docs/adr/`.
- Read `docs/plans/implementation-backlog.md` for the scope of each milestone.

<!-- CODEGRAPH_START -->
## CodeGraph

In repositories indexed by CodeGraph (a `.codegraph/` directory exists at the repo root), reach for it BEFORE grep/find or reading files when you need to understand or locate code:

- **MCP tool** (when available): `codegraph_explore` answers most code questions in one call — the relevant symbols' verbatim source plus the call paths between them, including dynamic-dispatch hops grep can't follow. Name a file or symbol in the query to read its current line-numbered source. If it's listed but deferred, load it by name via tool search.
- **Shell** (always works): `codegraph explore "<symbol names or question>"` prints the same output.

If there is no `.codegraph/` directory, skip CodeGraph entirely — indexing is the user's decision.
<!-- CODEGRAPH_END -->
