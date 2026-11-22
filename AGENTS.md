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
  run automated, non-interactive live integration tests against the intended
  service through the actual application client, including serialization,
  authentication, response parsing, and the configured timeout. Tests must not
  require a person to send messages, press buttons, or approve prompts.
- A dedicated test account or deployment may be used when it exercises the
  same service contract and changed application path. Do not require production
  credentials or production test messages solely because the test account is
  separate; verify distinct production-specific prerequisites separately.
- Offline tests, mocks, green CI that excludes integration tests, and skipped
  tests are not substitutes. Missing credentials, provisioning, configuration,
  or service access block publication: keep work local and report the blocker.
  Do not substitute human-interactive tests; never request secret values in chat.
- Record commands, executed/passed counts, and the tested commit or exact local
  diff. Reviewers must verify evidence for the final integration code and
  require affected tests to be rerun after behavior/configuration changes.
- Treat deployment-triggering pushes/merges as releases; verify the hosted
  settings and prerequisites required by the change, not just local settings.
  Existing deployment and health checks may provide that evidence; do not
  request unrelated settings or secrets. A user publication/deployment hold
  blocks pushes, merges, release workflow runs, and re-enabling automatic
  deployment until explicit release approval. Passing tests or receiving
  credentials is not approval.
