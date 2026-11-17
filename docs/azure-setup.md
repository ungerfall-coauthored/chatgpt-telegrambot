# Azure setup and fork deployment

This fork incorporates [the upstream change](https://github.com/Ungerfall/chatgpt-telegrambot/pull/17).
Use **separate test resources and a separate Telegram bot**; never deploy this fork
to the upstream Function App, queue, database, or bot.

Visual Studio individual Azure credits are intended for development and testing,
not production. Check the [subscription terms](https://learn.microsoft.com/azure/devtest/offer/quickstart-individual-credit),
service eligibility, region, quotas, and pricing in your subscription; credits do
not guarantee model availability or cover every charge. Set a Cost Management
budget and alerts. Alerts are not a hard spending cap.

## 1. Create Azure OpenAI deployments

1. Activate the Visual Studio Azure benefit, then select that subscription in
   [Azure portal](https://portal.azure.com/). Create an **Azure OpenAI** resource
   in a region with quota for your desired models.
2. Open Microsoft Foundry from the resource and deploy a chat-completions model
   compatible with your chosen Azure API version. Record the **deployment
   name**, not just the model name. A small model is a sensible starting point
   for a low-volume group; compare current regional prices and availability.
   Optionally deploy a different model for summaries; otherwise they use the
   chat deployment. Both daily summaries and `/tldrtoday` use this selection.
3. Optional: deploy an available GPT-image model supported by the
   `2025-04-01-preview` image API. Image generation incurs additional charges.
   The integration below expects base64 PNG output at 1024x1024. DALL-E 3 is
   retired in Azure; do not create a DALL-E 3 deployment for this fork.
4. In the Azure resource's **Keys and Endpoint**, copy the resource endpoint and
   a key. Enable network access for the machine or Function App that will call
   it. Keep the key private: never put it in Git, a GitHub issue, or chat.

| Setting | Meaning |
| --- | --- |
| `AZURE_OPENAI_ENDPOINT` | HTTPS resource endpoint, e.g. `https://NAME.openai.azure.com/` (no `/openai` path) |
| `AZURE_OPENAI_API_KEY` | Resource key, stored as a secret |
| `AZURE_OPENAI_CHAT_DEPLOYMENT` | Required chat deployment name |
| `AZURE_OPENAI_SUMMARY_DEPLOYMENT` | Optional summary deployment; defaults to chat |
| `AZURE_OPENAI_IMAGE_DEPLOYMENT` | Optional image deployment; without it `/image` reports disabled |
| `AZURE_OPENAI_API_VERSION` | Optional; defaults to `2024-10-21` |
| `AZURE_OPENAI_IMAGE_API_VERSION` | Optional; defaults to `2025-04-01-preview` |

Use models compatible with the request parameters and the chosen API versions.
See the request settings below before switching model families. The optional upstream Decisions feature
is a **separate OpenAI API** integration, not Azure: leave `DECISIONS_API_KEY`
unset and `DecisionsEnabled`, `ContinuationEnabled`, and `AutonomousEnabled`
false unless you deliberately configure and accept that separate service.

### Context windows, request budgets, and model compatibility

The old 1,638-token chat limit and 3,072-token summary chunks are replaced with
configurable budgets. Defaults are deliberately smaller than a model's full
context window: a large window is capacity, not free memory. History is sent and
billed again on each request. Chat keeps the newest fitting messages in
chronological order; summaries still split larger inputs into bounded chunks.

Set these in Function App **Environment variables** (or export them for the
worker/local tests). Each suffix has two independent settings: prefix it with
`AZURE_OPENAI_CHAT_` or `AZURE_OPENAI_SUMMARY_`.

| Suffix | Chat default | Summary default | Meaning |
| --- | --- | --- | --- |
| `CONTEXT_WINDOW_TOKENS` | `128000` | `128000` | Set to the actual model/version context capacity; not discovered automatically |
| `INPUT_TOKENS` | `16384` | `32768` | Maximum estimated input, including system prompt, instruction, current message, and message framing |
| `MAX_COMPLETION_TOKENS` | `2048` | `2048` | API output ceiling, including reasoning tokens when applicable |
| `SAFETY_MARGIN_TOKENS` | `1024` | `1024` | Additional headroom for tokenizer/framing differences |
| `TEMPERATURE` | `0` | `0.2` | Number from 0 to 2, or `omit` to exclude the parameter entirely |

Configuration must satisfy **input + max completion + safety margin <= context
window**. Increasing only the window does not increase the input budget.
Invalid budgets fail before an API call. Mandatory input that exceeds the budget
is rejected, not silently truncated; increase the input budget or shorten it.
`SummaryChunkTokens` remains a legacy fallback for summary input tokens, but
`AZURE_OPENAI_SUMMARY_INPUT_TOKENS` takes precedence; the old 32K ceiling is gone.

For example, after confirming your model's capacity, you can increase
`AZURE_OPENAI_CHAT_INPUT_TOKENS` to `32768`, keeping the remaining defaults.
For a model that does not accept custom sampling temperature, set **both**
`AZURE_OPENAI_CHAT_TEMPERATURE=omit` and
`AZURE_OPENAI_SUMMARY_TEMPERATURE=omit`, including when the same deployment
serves both workloads. Shrinking uses the chat profile too.

Newer reasoning models are not automatically drop-in replacements. Select an
API version that supports the exact deployed model and Chat Completions, omit
unsupported temperature, and allow sufficient completion tokens for reasoning
and visible text (for example, try `8192` rather than `2048` and measure).
The pinned SDK does not expose a reasoning-effort setting; it uses the service's
default. This configuration does not add Responses API support or guarantee
compatibility with every model. Consult Microsoft's
[reasoning-model guidance](https://learn.microsoft.com/azure/foundry/openai/how-to/reasoning)
and run the opt-in tests before switching.

Additional settings:

| Setting | Default | Meaning |
| --- | --- | --- |
| `AZURE_OPENAI_TOKEN_ENCODING` | `o200k_base` | SharpToken encoding name, shared by chat and summaries; use one appropriate for the deployed model families |
| `AZURE_OPENAI_AUTONOMOUS_MAX_COMPLETION_TOKENS` | `120`, capped by chat output limit | Output ceiling for optional autonomous interjections; increase if reasoning consumes it |
| `MessageRetentionDays` | `1` | Positive whole days to retain **newly stored** messages; set `7` for a week if the group accepts the longer retention |
| `ShrinkMinimumTokens` | `512` | Minimum message size eligible for the existing shrink job; positive integer, no old 32K ceiling |

Token counting is an estimate; retain a safety margin and use API-reported usage
as authoritative. A larger context does not recover deleted history. Retention
changes do not rewrite existing documents' TTLs; enable Cosmos container TTL
for per-item expiration to take effect. Longer retention increases stored data
and may increase request costs. Daily summaries and `/tldrtoday` still summarize
the current UTC day, not the entire retention period.

The Azure shrink job still runs on messages older than an hour. Larger windows
make aggressive shrinking less necessary: raise `ShrinkMinimumTokens` or set
`AzureWebJobs.ShrinkMessage.Disabled=true` to retain verbatim text. Original text
is preserved in `OriginalMessage` when a message is shrunk; disabling the job
does not restore previously shortened messages. Telegram's 4,096-character send
limit, delivery chunking, and reply-frequency/cooldown safeguards are unchanged.

## 2. Run live Azure tests locally

Install the exact SDK in `global.json`. From the repository root in a private
terminal, set the three required values; use deployment names from step 1.
The key is read without echo and is not included in the shell command history:

```bash
export AZURE_OPENAI_ENDPOINT='https://YOUR-RESOURCE.openai.azure.com/'
export AZURE_OPENAI_CHAT_DEPLOYMENT='YOUR-CHAT-DEPLOYMENT'
read -rs -p 'Azure OpenAI key: ' AZURE_OPENAI_API_KEY; echo
export AZURE_OPENAI_API_KEY
# Optional: export AZURE_OPENAI_SUMMARY_DEPLOYMENT='YOUR-SUMMARY-DEPLOYMENT'
# Optional, incurs image charges: export AZURE_OPENAI_IMAGE_DEPLOYMENT='YOUR-IMAGE-DEPLOYMENT'
export AZURE_OPENAI_LIVE_TEST=1
dotnet restore src/Ungerfall.ChatGpt.TelegramBot.sln --locked-mode
dotnet test tests/Ungerfall.ChatGpt.TelegramBot.Tests/Ungerfall.ChatGpt.TelegramBot.Tests.csproj \
  -c Release --no-restore --filter 'FullyQualifiedName~AzureOpenAiLiveTests'
unset AZURE_OPENAI_API_KEY AZURE_OPENAI_LIVE_TEST
```

The test makes one short paid request for each chat/summary profile, even when
they use the same deployment, and, if configured, one paid image request.
It uses the configured temperatures, completion limits, and API version.
It does not test Telegram or Cosmos DB or validate a full-size context request.
Failures can mean key, networking, quota, deployment, model, or API-version
incompatibility. Never paste the key or a connection string into logs or chat.
CI does not run live tests or receive these secrets.

Existing Cosmos tests are separately opt-in: create a **test** Cosmos DB for
NoSQL database named `telegram-bot`; supply its connection string in `CosmosDb`
and set `INTEGRATION_TESTS=1`. Run only the `CosmosDbTests` filter. They create
and write to `tests-timedTaskExecutions` (partition key `/chatId`), and leave
test data behind. The existing `DailyQuizTests` additionally sends a Telegram
message and needs a test bot token and `QuizChats`; do not run it against a
real group.

## 3. Provision a separate Function App for testing

1. In Azure portal create a test Function App for **.NET 8 isolated**, its
   storage account, a Cosmos DB for NoSQL account, and a Service Bus namespace.
   Create a `tgbot-updates` queue. In Cosmos create a database and separate
   messages and timed-tasks containers, both partitioned by `/chatId`.
   Configure appropriate network access. These services have their own costs.
2. In Function App **Environment variables**, configure the Azure OpenAI
   settings above, `TELEGRAM_BOT_TOKEN` for a new test bot,
   `TELEGRAM_BOT_USERNAME` (its username, with or without `@`),
   `CosmosDatabase`, `CosmosTelegramMessagesContainer`,
   `CosmosTimedTasksContainer`, `CosmosDbConnectionString`, and
   `ServiceBusConnection`. Set `TestUsers`, `QuizChats`, and
   `DailySummaryChats` to comma-separated numeric test IDs. Function runtime
   settings include `FUNCTIONS_WORKER_RUNTIME=dotnet-isolated` and
   `AzureWebJobsStorage`. Prefer Key Vault references for secrets.
3. Before the first deployment, disable the `DailySummary`, `DailyQuiz`,
   `DailyFilmsQuiz`, `DailyVideoGamesQuiz`, and `ShrinkMessage` timer functions
   by setting each `AzureWebJobs.<function-name>.Disabled` to `true` in the
   Function App's environment variables. Enable them only when ready.
   The inherited group whitelist is hardcoded; only its listed groups or IDs in
   `TestUsers` can use the bot. Group system prompts are also inherited.
4. Generate a random Telegram webhook secret using only letters, digits,
   underscores, and hyphens (1–256 characters). Set it as
   `TELEGRAM_WEBHOOK_SECRET` in the Function App and register the test bot's
   webhook with the **same** `secret_token` using Telegram's
   [setWebhook](https://core.telegram.org/bots/api#setwebhook) API and the
   Function's `/api/TelegramBot` HTTPS URL. Never share the token, secret or
   resulting Bot API URL. Requests with a missing or incorrect secret receive
   HTTP 401 and are not queued. Do not register the upstream bot's webhook.

The copied upstream PR changes the Telegram queue wire format. Before switching
an existing deployment, stop ingress and drain the old queue; do not run mixed
versions. This fork has not been validated against a live Telegram deployment.

## Deployment announcements

The Function's `ReleaseAnnouncements` timer checks once a minute; the polling
worker checks at startup and once a minute. **Disabled by default**, including
locally. Do not enable it against the real bot while testing.

| Setting | Default | Meaning |
| --- | --- | --- |
| `ReleaseAnnouncementsEnabled` | `false` | Set `true` in the deployed app to announce versions |
| `ReleaseVersion` | Git SHA embedded by the .NET SDK | Optional override: the full 40-character deployed commit SHA; required for packages built outside a Git checkout |
| `ReleaseRepository` | `ungerfall-coauthored/chatgpt-telegrambot` | GitHub repository containing the deployed commits and merged PRs |
| `ReleaseNotesMaxCommits` | `50` | Maximum commits compared between deployments (1–100); larger ranges fail visibly instead of silently omitting changes |
| `ReleaseRetryDelayMinutes` | `15` | Backoff after pre-send failures or definite Telegram rejection (1–1440); avoids repeatedly paying for failed summary requests |
| `GITHUB_RELEASE_NOTES_TOKEN` | unset | Optional secret with read-only **Contents** and **Pull requests** access, for private repositories or higher GitHub API limits |

Each distinct deployed SHA produces one short Russian announcement for every
whitelisted group and every **negative** group ID in `TestUsers`; private chats
and the whitelist's default prompt entry are excluded. Review that whitelist
before enabling this feature. Groups with the same prior version share one
Azure summary request during a run. Summaries use the summary deployment and
its input/output budgets. PR titles are supplied as untrusted data; do not put
secrets in PR titles.

After a group's first announcement, notes include merged PRs between its last
announced commit and the current commit, not just the latest PR. For the first
announcement only PRs merged at the deployed commit are considered, avoiding a
dump of the entire repository history. Direct commits without associated merged
PRs get a generic technical-update message. Reverting to an already announced
SHA does not announce it again. A new group gets the current version on the
next check. The runtime reads GitHub; there are no runtime GitHub write permissions.
Public repositories need no GitHub token, but unauthenticated API rate limits
can delay announcements; errors retry after the configured backoff.

Persistent per-group/version receipts use the existing timed-tasks Cosmos
container (`/chatId`) with `ttl: -1`, independently of message retention.
Conditional writes and five-minute processing leases prevent concurrent sends.
Notes/model failures before sending, and definite Telegram rejections, can retry.
Before calling Telegram, the receipt is durably marked `sending`. An uncertain
send or crash in that window is **not retried automatically**: avoiding duplicate
group messages takes priority over guaranteed delivery. Check logs and the
receipt's stage/message ID before manually recovering a missing announcement.
Do not delete `sending` or `completed` receipts without checking the group first.
Cosmos and Telegram cannot provide an atomic exactly-once transaction.

No announcement is sent just because a build finishes: the code must actually
run in the deployed app with the feature enabled. This is a startup/runtime
notification, **not an overall deployment-health guarantee**. In deployment
slots, keep `ReleaseAnnouncementsEnabled=false` in staging as a slot-sticky
setting, and `true` only in the live slot. The later CD workflow must gate its
activation/swap on health checks. Keep it disabled in local settings and use a
separate test bot/group for any explicit live announcement test.

## 4. Enable manual CD from the fork

The workflow builds all projects, runs offline tests, and packages the Function.
It deploys **only** on a manually dispatched workflow on the fork's `master`
branch, after a successful build, to the `fork-test` GitHub environment. It
does not deploy on pushes, pull requests, or to the worker. Merge the desired
fork branches into fork `master` before dispatching.

1. In the fork's GitHub settings create environment `fork-test`; configure
   required reviewers and restrict deployment branches to `master`, where your
   GitHub plan supports these protections. Add environment variable
   `AZURE_FUNCTIONAPP_NAME` with the **test** Function App name.
2. In Azure create a user-assigned managed identity (or app registration) and
   grant it **Website Contributor** scoped to that test Function App only. Add
   a federated credential with issuer `https://token.actions.githubusercontent.com`,
   audience `api://AzureADTokenExchange`, and subject
   `repo:ungerfall-coauthored/chatgpt-telegrambot:environment:fork-test`.
   See [Microsoft's OIDC setup](https://learn.microsoft.com/azure/azure-functions/functions-how-to-github-actions).
3. In the GitHub environment add `AZURE_CLIENT_ID`, `AZURE_TENANT_ID`, and
   `AZURE_SUBSCRIPTION_ID` as environment secrets. No Azure OpenAI or Telegram
   keys are needed in GitHub Actions; they stay in the Function App/Key Vault.
4. In the fork's Actions tab run **Build, test and deploy** on `master`, review
   and approve the `fork-test` deployment, then check Function logs and test
   with the separate bot. Configure a new workflow/identity for any other
   environment rather than pointing this one at upstream resources.
