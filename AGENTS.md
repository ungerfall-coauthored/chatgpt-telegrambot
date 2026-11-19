# AGENTS.md

GenAI sandbox fork. Read [.agents/rules/coauthor-isolation.md](.agents/rules/coauthor-isolation.md) first (upstream isolation: never PR or push to `Ungerfall/*`).

## Repository

See README.md. GenAI sandbox fork under ungerfall-coauthored.

## Layout

Inspect repository root for source layout.

## Commands

- Build: `dotnet build src/Ungerfall.ChatGpt.TelegramBot.sln -c Release`
- Offline tests: `dotnet test tests/Ungerfall.ChatGpt.TelegramBot.Tests/Ungerfall.ChatGpt.TelegramBot.Tests.csproj -c Release --filter 'Category!=Integration'`
- Decision live tests: privately export `AZURE_DECISIONS_ENDPOINT`,
  `AZURE_DECISIONS_DEPLOYMENT`, and `AZURE_DECISIONS_API_KEY`, then run
  `AZURE_OPENAI_LIVE_TEST=1 dotnet test tests/Ungerfall.ChatGpt.TelegramBot.Tests/Ungerfall.ChatGpt.TelegramBot.Tests.csproj -c Release --filter 'Category=Integration&FullyQualifiedName~AzureOpenAiLiveTests.Decision'`.
  `dotnet test` does not load `local.settings.json` automatically. Confirm tests
  executed and passed, rather than being skipped. See `docs/azure-setup.md`.

## Workflow

- Work on branch `coauthor/<short-task-slug>`.
- Push only to `ungerfall-coauthored` remotes.
- Do not open PRs to upstream `Ungerfall`.

## Validation and release gates

- Before pushing, opening a PR, or merging external-service behavior changes,
  run relevant live integration tests against the intended service/deployment
  through the actual application client, including serialization,
  authentication, response parsing, and the configured timeout.
- Offline tests, mocks, green CI that excludes integration tests, and skipped
  tests are not substitutes. Missing credentials, provisioning, configuration,
  or service access block publication: keep work local and report the blocker.
  Never request secret values in chat.
- Record commands, executed/passed counts, and the tested commit or exact local
  diff. Reviewers must verify evidence for the final integration code and
  require affected tests to be rerun after behavior/configuration changes.
- Treat deployment-triggering pushes/merges as releases; verify hosted settings,
  not just local settings. A user publication/deployment hold blocks pushes,
  merges, release workflow runs, and re-enabling automatic deployment until
  explicit release approval. Passing tests or receiving credentials is not
  approval.
