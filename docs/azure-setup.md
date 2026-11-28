# Azure setup and fork deployment

This fork incorporates [the upstream change](https://github.com/Ungerfall/chatgpt-telegrambot/pull/17).
For an isolated test environment, use separate resources and a separate Telegram
bot. The replacement procedure in section 3 instead targets only the approved
fork-owned app and its existing resources and service bot. Never deploy this
fork to the upstream Function App, queue, database, or bot.

Visual Studio individual Azure credits are intended for development and testing,
not production. Check the [subscription terms](https://learn.microsoft.com/azure/devtest/offer/quickstart-individual-credit),
service eligibility, region, quotas, and pricing in your subscription; credits do
not guarantee model availability or cover every charge. Set a Cost Management
budget and alerts. Alerts are not a hard spending cap.

### Telegram credential-safe diagnostics

Both hosts register Telegram through `AddTelegramBotClient`. It removes the
default HTTP client loggers and their URL-bearing scopes, and logs only the
method, status, elapsed time, and failure type. URLs, headers, payloads, and
exception details are not included. Other HTTP clients retain their diagnostics.
Header or query-string redaction alone cannot protect Telegram's token-bearing
URL path. Any additional dependency/exception telemetry collector must also be
configured not to capture these URLs.

Run the noninteractive regression against the dedicated test bot:

```sh
TELEGRAM_LOGGING_LIVE_TEST=1 dotnet test tests/Ungerfall.ChatGpt.TelegramBot.Tests/Ungerfall.ChatGpt.TelegramBot.Tests.csproj -c Release --filter 'FullyQualifiedName~TelegramHttpClientLoggingLiveTests'
```

Privately supply `IntegrationTests_TELEGRAM_BOT_TOKEN`. The tests call `getMe`
and deliberately reject `getChat(0)`; they do not send messages or require user
interaction.

If a token has already appeared in logs, deploy the logging fix, revoke/rotate
the token through BotFather, update the private deployment settings and any
consumers, and verify the replacement. Restrict access to affected retained
telemetry and follow the service's purge/retention procedure. A code fix does
not invalidate the exposed credential or remove historical logs. Never paste
the old or replacement token into chat, commits, or issue/PR descriptions.

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
| `AZURE_OPENAI_IMAGE_API_VERSION` | Optional; defaults to `2025-04-01-preview` |

Chat completions use Azure's `/openai/v1/` endpoint and do not require an API
version setting. Image generation retains its versioned Azure deployment
endpoint. Use models compatible with the request settings and image API version.
See the request settings below before switching model families. Optional
no-mention replies use a separate **Microsoft-Decision-1.1** deployment in
Foundry, described below. They do not call the public OpenAI API.

### Cosmos history proof of concept

`src/utils.history-poc` is an isolated prototype, not a deployed history feature.
It does not change the bot's Cosmos SDK, webhook, retention, or tool definitions.
The prototype uses `AZURE_OPENAI_EMBEDDINGS_DEPLOYMENT` (for example
`bot-embeddings`) and the dedicated `AZURE_OPENAI_EMBEDDINGS_API_KEY`.
`AZURE_OPENAI_EMBEDDINGS_ENDPOINT` is optional and otherwise defaults to
`AZURE_OPENAI_ENDPOINT`. Embeddings use the official OpenAI SDK, `/openai/v1/`,
and 512 dimensions.

Privately load the settings into the test process, then run:

```sh
HISTORY_POC_LIVE_TEST=1 dotnet test tests/Ungerfall.ChatGpt.TelegramBot.Tests/Ungerfall.ChatGpt.TelegramBot.Tests.csproj -c Release --filter 'FullyQualifiedName~CosmosHistoryPocTests' --logger 'console;verbosity=detailed'
```

Run this selector separately from offline tests, which replace process-wide
settings with synthetic values. The tests require no Telegram interaction.
They first verify the embedding
deployment. The retrieval test refuses to create a container unless a read-only
throughput probe confirms serverless mode; it never requests provisioned
throughput or changes existing containers. Vector search must be available on
the account. Each run creates a uniquely named `history-poc-*` container with
a one-hour document TTL and deletes that container in cleanup.

Without `HISTORY_POC_EXPORT`, the corpus is synthetic. Set that variable to a
local Telegram Desktop `result.json.zip` only for a consented chat. The prototype
samples at most 1,000 recent conversation chunks, preserves message order and
day/topic boundaries, and truncates text for the experiment. It is not a complete
or lossless importer. Neither the export nor source text belongs in Git, CI
artifacts, or test output.

The experiment measures four factual retrievals, approximate/exact top-five
overlap, partition isolation, idempotent replay, stale ETag rejection, deletion,
embedding input tokens, latency, and observed request units. It stops issuing
normal operations after 100,000 observed RUs and caps initial embedding input at
500,000 tokens. The RU threshold is a stop-after-observation guard, not a hard
billing cap; cleanup still runs. Identical approximate/exact results or RU costs
do not prove that DiskANN, rather than a scan, served the queries. DiskANN is
approximate, and a small passing corpus does not prove recall or cost for the
full archive. Production support
still needs durable asynchronous ingestion, permission enforcement, edit/delete
reconciliation, coverage reporting, retention policy, and bounded history tools.

### Durable Telegram history

The production archive foundation is separate from the one-day rolling context.
Live capture, asynchronous embeddings and read-only history tools are opt-in.
Keep `HistoryEnabled=false` until those paths and hosted prerequisites pass
their release gates.

When enabled for `HistoryChats`, authenticated incoming originals and edits are
archived before normal reply handling. Only known consenting nonbot authors are
captured; newcomers use `/historyconsent`. Callbacks, ephemeral IDs, bot messages
and messages sent on behalf of a chat (including anonymous administrators) are
excluded because their individual author is not verified. Full captions and
metadata-only activity remain canonical.
Embeddings never run on the reply path.

`DispatchHistory` leases pending hour/topic work and sends references to the
`tgbot-history` Service Bus queue in the existing namespace. `IndexHistory`
builds lossless, Unicode-safe chunks of at most 512 tokens with the dedicated
`bot-embeddings` deployment. It conditionally activates a versioned manifest,
rejects stale source changes, checks current consent and removes superseded
vectors. Bounded maintenance pages repair orphan cleanup; raw originals and
media binaries are not queue payloads. Oversized hour buckets fail explicitly
rather than silently truncating the archive.

Only user `66215515` in an opted-in chat can use `history_query`, `history_get`,
`history_search`, `history_activity` and `history_coverage`. Topic scope is
server-owned. History tools are not available to autonomous replies or scheduled
summaries; the separately configured on-this-day task reads the archive directly.
Read-only SQL accepts permitted SELECT/WHERE/GROUP BY/ORDER BY clauses,
not FROM, joins, subqueries or mutations. Aggregates complete or return an error;
row reads have signed, expiring chat/user/topic/query-bound cursors. Use
`history_activity` for exact yearly ASCII histograms, not vector-hit counts.
When an unaliased row projection includes both `c.userId` and `c.text`, the
result includes per-author counts of returned text messages on that page.
These are sample sizes, not activity totals or proof of archive coverage.
Each turn permits three history calls and 4000 retrieved tokens.

Originals with unknown author IDs remain stored but are not exposed to AI until
identity/consent can be established. Forwarded originals remain stored; they are
excluded from embedding/text retrieval, but consenting senders' forwarding
activity counts as source-message activity. There is no automatic history
retrieval for unsolicited replies.

The optional `OnThisDay` task runs at 09:00 UTC (12:00 fixed UTC+03:00). It is
disabled until `OnThisDayChatId` is set privately to one group already enabled
in `HistoryChats`; do not set it until the group and deployment are approved.
Before release, verify the hosted timer uses UTC (`WEBSITE_TIME_ZONE` unset or
UTC); local settings alone do not prove the hosted timer timezone.
For each previous year starting with the earliest available consenting original,
it sends all imported, consenting, non-forwarded text messages in the local calendar day
to one model request for that year. If that day's messages exceed the bounded
query or model input limit, the year is marked unavailable instead of silently
sampling. Empty years say no text is available in the archive, not that nothing
happened. The recap has a “В этот день:” heading and expandable details where
Telegram supports rich messages; otherwise it falls back to plain text, split
into messages when necessary.
On February 28 in a
non-leap year, both February 28 and 29 of leap years are included; on February
29, non-leap years are explicitly identified. No vector index is required.
It uses the monthly history RU budget; model usage has separate charges. Consent
is rechecked before and after generation and immediately before each delivery. A
deterministic daily receipt is claimed before delivery; a consent change,
ambiguous delivery or crash after the claim requires manual reconciliation
rather than an automatic duplicate.

`/historyforget` revokes the requesting author's consent and resumably purges
their archived originals, author counters and affected vectors. Recent rolling
originals are removed and future rolling reads/writes suppress revoked authors.
Conversation-policy context and cached decision inputs are checked against the
author's current consent generation; old entries cannot reappear after renewed
consent. Entries and receipts retain their existing TTLs. Purge source/counter
updates and completion are fenced by the active purge epoch.
Minimal opt-out records and anonymous source tombstones remain to prevent
backfill resurrection. Index cleanup may continue asynchronously. New consent
does not restore tombstones. Copies already sent to Telegram or AI providers
cannot be recalled. Ordinary group deletion updates are not delivered by
Telegram, so the archive cannot infer deletions from message-ID gaps.

`HistoryMonthlyBudgetUsd` defaults to `5`. A shared UTC-month ledger reserves and
settles observed RU/embedding costs for indexing and retrieval, allowing source
capture to continue when those operations pause. Crashed reservations remain
conservatively charged. This is not a hard Azure billing cap; answer generation,
storage, queue/Functions costs and unobserved operations are separate.

`historyMessages` retains full original text, media metadata, source timestamps,
consent records, activity counters, import checkpoints and indexing work intents.
Canonical source, counter and work changes commit atomically; duplicate updates
and stale exports do not add activity or resurrect tombstones. Explicit consent
revocation wins over an import, including races with source batches.
Signed Desktop export IDs are retained as opaque source identities; negative
IDs must not be used as Telegram Bot API links. Media binaries are not imported.

`historyChunks` is a rebuildable 512-dimensional cosine quantized-flat index
containing references and character ranges rather than duplicate source text.
Both containers use `/chatId` and disabled default expiry (`-1`). Provisioning
checks serverless, partition, expiry and vector/index policies; it never changes
existing mismatched containers or requests throughput.

Privately load the existing Cosmos settings and set `HistoryChats` to exactly
one consented group for imports. Container names can be overridden with
`HistoryMessagesContainer` and `HistoryChunksContainer`.

```sh
dotnet run --project src/utils.history/provision -c Release
dotnet run --project src/utils.history/import -c Release -- --verify-client
dotnet run --project src/utils.history/import -c Release -- --dry-run
dotnet run --project src/utils.history/import -c Release -- --seed-consent --confirm-consent
dotnet run --project src/utils.history/import -c Release -- --confirm-consent
```

Set `HISTORY_EXPORT` privately to the local ZIP path. The dry run streams the
complete export, validates its target and reports only aggregate sizes/tokens.
Writes require confirmed chat-scoped consent. Import checkpoints are keyed by
archive SHA-256; ETag leases prevent simultaneous importers, and source batches
commit with their checkpoint. Re-running resumes the same archive or reports
that it is already complete. Explicitly revoked authors are not re-authorized.
After an interrupted importer, wait for its three-minute lease to expire.
Seed confirmed export-author consent before enabling capture, preserving prior
revocations. Verify hosted bot visibility and active webhook message/edit
coverage with `--verify-telegram`; no human Telegram interaction is required.
Enable capture before the full backfill. `--max-messages=256` creates a bounded
source checkpoint for RU/latency calibration without marking the export complete.

`HistoryImportBudgetUsd` defaults to `2`. This is a stop-after-observed
source-operation RU guard at $0.305/million RUs, not a hard billing cap or a
budget covering embeddings, storage and other Azure services. Review the budget
before resuming a budget-stopped import. Do not upload archives, settings,
identities or original text into Git, CI, logs or test failure output.

```sh
DURABLE_HISTORY_LIVE_TEST=1 dotnet test tests/Ungerfall.ChatGpt.TelegramBot.Tests/Ungerfall.ChatGpt.TelegramBot.Tests.csproj -c Release --filter 'FullyQualifiedName~DurableHistoryLiveTests'
```

Run live selectors separately from offline tests. The canonical tests use a
disposable synthetic serverless container. `--verify-client` separately checks
source serialization and vector SQL with the actual production Cosmos SDK
3.43.1, using and then removing an isolated synthetic partition; the test
project's newer PoC SDK cannot establish that runtime contract.

### Storage function tools

The chat deployment must support strict function calling. Storage tools are
available only for Telegram user `66215515` in an already allowed chat; they
are not exposed to other users, unsolicited replies, scheduled summaries,
image generation, or message shrinking.

`storage_catalog` and `storage_schema` describe the registered
`quiz_questions` dataset. It maps to the existing
`CosmosTimedTasksContainer` through the DAL, with the current Telegram chat
as the server-selected `/chatId` partition. No new container or credentials
are needed. Ordinary CS, film, and video-game quizzes support CRUD. `book_quizzes` exposes
the Linux Programming Interface series for querying and updating upcoming
questions. `book_quiz_progress` exposes delivery progress read-only; it is not
an editable dataset. Chat history, receipts, and deployment data remain outside
these tools.

`storage_list` accepts controlled Cosmos SQL **SELECT**, **WHERE** and
**ORDER BY** clauses (`select`, `predicate`, `orderBy`), not KQL or a full query.
The server owns `FROM`, dataset filters and the chat partition. Use
`select: "VALUE COUNT(1)"` for scalar counts, `select: "VALUE true"` with
`limit: 1` for existence, and small projections such as
`"c.id, c.correctOptionId"` for discovery. `VALUE` field/object projections,
field aliases and a SELECT-only `c.version` token are supported.
`select: "*"` (or legacy omitted/null select) returns full editable views.
Scalar counts/exists use `data.value`; regular projections use `data.records`.
Other `VALUE` projections use an array in `data.value`.

Ordering accepts one documented field with ASC/DESC. Multi-field ordering is
rejected before querying because composite indexes are not configured. Book delivery order is `c.sequence`;
ordinary `c._ts` is last modification time, not a separate delivery timestamp.
The server permits documented fields/operators only, reads at
most two pages and 10 records, limits result data to 12 KB, and stops after
observing more than 50 RU. RU is measured after a page returns: this is not a
hard Azure spending cap. Counts ignore the row limit but still obey time/page/RU
budgets; an incomplete count fails instead of returning a partial number, and an
incomplete existence check without a matching row fails instead of returning false.
There are no joins, subqueries, internal-field access or unrestricted pagination.
`storage_get`, `storage_create`, `storage_update`, and `storage_delete`
operate through the same DAL. Creates validate 1-5 questions per call;
updates/deletes require the current version and reject posted quizzes or
quizzes reserved for scheduled delivery.
Book updates preserve citation/sequence/image metadata and reject sequences
below progress `nextSequence` or any series with `sending=true`. The mutation
guards the progress ETag in the same transaction, so a concurrent sender cannot
send stale answer options. Book creation/deletion still uses ordered ingestion
rather than tools that could create gaps in the series.

To check the last N delivered book questions, read progress and count sequences
`max(1,nextSequence-N)` through `nextSequence-1`. Answer position 1 is the
zero-based `correctOptionId = 0`. `storage_shuffle_options` then reshuffles only
upcoming questions' answer options in batches of up to 64. Use `limit: 64` to
avoid spending model rounds on small batches. A batch uses at most 66 Cosmos
transaction operations (64 quiz patches, a progress guard and a receipt),
below Cosmos's 100-operation limit. It preserves the
correct answer text, changes its position, and leaves question text/order and
citations unchanged. Responses contain only counts, IDs and `hasMore`; repeat
while `hasMore=true`. Per-request markers prevent processing the same question
twice across batches, and atomic receipts make retries safe. A later conflict
can leave earlier successful batches committed; the model must report those
and stop rather than claim all questions were changed.
Mutations and durable retry receipts commit atomically in the same partition.
The scheduler takes a per-chat, per-quiz-type delivery lock and reserves a quiz
using its Cosmos ETag before sending its poll. Completion atomically marks it
posted and removes the lock. An interrupted or uncertain delivery leaves the
lock in place and requires manual reconciliation rather than an automatic
resend. Check Telegram delivery before clearing `deliveryPending` or marking the
quiz posted and deleting its `quiz-delivery-lock:<quiz-type>` record.
Predicates containing backslash escapes are rejected to prevent SQL scope
escapes.

The SDK loop preserves assistant tool calls and their matching tool results.
It is limited to 8 rounds, 12 calls, two error-correction retries, a
three-minute overall timeout and 15 seconds per DAL call. Each model response
retains the configured output ceiling; the whole tool turn is capped at four
times that ceiling, also bounded by `AZURE_OPENAI_CHAT_TOOL_OUTPUT_TOKENS`
(8192 by default). Input includes tool schemas and
intermediate results. Query errors are returned as sanitized tool results,
including compiler codes and actionable supported-query guidance, never raw
Cosmos exceptions, keys, or stack traces. Provider rejections and local DAL
validation failures are logged without query arguments. Recovery, call and
round exhaustion have distinct error types and counters; only token-budget
errors use the context-overflow reply. Further writes are blocked
after conflicts or uncertain failures. A failed final response does not imply
that earlier writes were rolled back.

The fork deployment applies `config/chat-input-budget.json`: **131072 chat
input tokens** and a **400000-token model context capacity**. Only these two
non-secret settings are managed by the input profile. The deployment verifies the
existing output/safety budgets fit and that other AI settings remain untouched.
These deployment overrides do not change the generic library defaults.

The separately approved `config/chat-output-budget.json` sets **32768 tokens
per chat response** and **65536 output tokens per tool turn**. Output includes
hidden reasoning, not just the final reply. Summary/autonomous settings,
input/context settings and timeouts are unchanged. Replies remain concise.
The output deployment step writes only these two chat output keys and verifies
all other AI settings are unchanged.

Storage turns emit `AI storage start/request/response/tool/complete/abort` logs
with a generated turn ID, round/call counters, input estimates, actual output
and reasoning usage, finish reasons, elapsed time and precise abort reasons.
Tool logs include dataset, limit, masked SELECT/WHERE/ORDER BY shapes, scalar
counts, status/code and result size. String literals, numbers and unknown
identifiers are redacted; prompts, replies, records, IDs, credentials, URLs
and raw provider exceptions are not logged by these diagnostics. A successful
turn does not emit an abort. Filter Application Insights traces by
`message startswith "AI storage "` and the turn ID to reconstruct a failure.

An automated live regression sends more than the old 16384-token input budget
through the actual configured chat client, with the same output ceiling:

```sh
AZURE_OPENAI_LIVE_TEST=1 dotnet test tests/Ungerfall.ChatGpt.TelegramBot.Tests/Ungerfall.ChatGpt.TelegramBot.Tests.csproj -c Release --filter 'FullyQualifiedName~ReviewedChatInputBudgetAcceptsLargeInput'
```

Noninteractive live tests require the existing private Azure/Cosmos settings:

```sh
STORAGE_TOOLS_LIVE_TEST=1 dotnet test tests/Ungerfall.ChatGpt.TelegramBot.Tests/Ungerfall.ChatGpt.TelegramBot.Tests.csproj -c Release --filter 'FullyQualifiedName~StorageToolsLiveTests'
```

`dotnet test` does not load `local.settings.json`; load settings privately into
the test process. Tests use unique synthetic partitions in the configured
container, validate real CRUD/query failures, and invoke a synthetic Telegram
update through the real Azure chat client. Telegram transport is mocked:
no messages, button presses, or Telegram credentials are required. Fixture
records and receipts are deleted afterwards. The tools' intermediate arguments
and results never enter ordinary chat history; the final bot reply does.
The exact Russian Linux-quiz regression covers both ten delivered/eight
upcoming questions and the production-shaped four delivered/128 upcoming
questions, checking every answer and citation after the shuffle.
The separate exact read-only request checks the whole series, including
upcoming questions, rather than substituting the latest delivered sample.

### Optional reply decisions: Microsoft-Decision-1.1

Deploy **Microsoft-Decision-1.1**, not a generative chat model, from the Foundry
model catalog. It returns numerical decisions without generating prose. Prefer
**Data Zone Standard** in a European data zone close to the West Europe bot
when available; **Global Standard** is also supported. Deployment type alone
does not guarantee latency: measure representative chat examples before enabling
the feature. Availability, quota, and Visual Studio subscription eligibility
must be checked in the portal.

Copy the **resource endpoint**, **deployment name**, and **resource API key**
from the deployment details page. Keep them separate from the chat/image
settings, even if the deployments share a Foundry resource.

| Setting | Meaning |
| --- | --- |
| `AZURE_DECISIONS_ENDPOINT` | HTTPS resource root, e.g. `https://NAME.services.ai.azure.com/`; no project or API path |
| `AZURE_DECISIONS_DEPLOYMENT` | Your Microsoft-Decision-1.1 deployment name, e.g. `bot-decisions` |
| `AZURE_DECISIONS_API_KEY` | Key belonging to that resource; never an OpenAI platform key |

Set all three, or leave all three unset to disable the integration. Partial
configuration fails validation. Remove the obsolete `DECISIONS_API_KEY`; it
is no longer read. The chat `AZURE_OPENAI_API_VERSION` setting is no longer
read; image API version configuration remains unchanged.

The client uses `POST /providers/microsoft/v1/systemone`, named `noul`
questions, and `answers.<question>.noul` probabilities. It does not use
`/openai/v1/decisions` or Chat Completions.
See [Microsoft's model/API guide](https://learn.microsoft.com/azure/foundry/foundry-models/how-to/use-foundry-models-microsoft-decision).

For local Functions, add the following inside **`Values`** in the existing,
git-ignored **`src/presentation.azure-function/local.settings.json`** (not
`settings.local.json`), replacing placeholders privately:

```json
{
  "AZURE_DECISIONS_ENDPOINT": "https://YOUR-RESOURCE.services.ai.azure.com/",
  "AZURE_DECISIONS_DEPLOYMENT": "bot-decisions",
  "AZURE_DECISIONS_API_KEY": "YOUR-RESOURCE-KEY"
}
```

These settings do not enable no-mention replies by themselves. `ConversationPolicyChats`
remains an opt-in JSON dictionary keyed by group ID. For example, enabling
**natural-language requests and continuations, without unsolicited interjections**,
for a test group is this single string setting:

```json
{
  "ConversationPolicyChats": "{\"-1001234567890\":{\"DecisionsEnabled\":true,\"NaturalRequestsEnabled\":true,\"ContinuationEnabled\":true,\"AutonomousEnabled\":false}}"
}
```

`NaturalRequestsEnabled` permits explicit requests such as "I'd like the bot's
opinion" without an `@mention`, earlier bot reply, or active continuation
window, including after another human speaks or when replying to that human.
A cheap candidate filter looks for English/Russian bot references or the
configured `TELEGRAM_BOT_USERNAME`; the decision model must still confirm
bot-directed intent. Merely discussing bots is not an instruction to answer,
and ordinary group questions do not spend this path's API budget.

`ContinuationEnabled` permits recent follow-ups without an `@mention`;
`AutonomousEnabled` separately permits occasional unsolicited interjections.
All three flags default to false and require `DecisionsEnabled=true`.
Natural requests share `DecisionDailyLimit` (default 20 per group) and the
`MinProbability` threshold (default 0.95), but do not consume unsolicited
interjection quotas or depend on their sampling/cooldown. Those restrictions
continue to apply only to unsolicited interjections. Missing configuration,
invalid/refused answers, API failures, and the three-second request timeout
all leave the bot silent. Evaluate false positives and latency before changing
real group flags. Do not run a local consumer against the deployed bot's
production queues. Telegram must deliver ordinary group messages: disable
privacy mode in BotFather or make the bot a group administrator.

Local settings are not deployed. For the hosted bot, configure the three keys
and any deliberate group opt-ins in the Function App's environment variables
or Key Vault references; never in GitHub Actions secrets.

The fork deployment also applies reviewed, **non-secret** flag patches from
`config/conversation-policy.json` through its existing app-scoped OIDC identity.
The food group enables natural requests and continuations, with unsolicited
interjections disabled; other groups and custom limits are preserved.
The pipeline checks the three hosted model settings exist and re-reads the
merged policy before deploying. It never writes or logs model keys.
Managed flags override portal edits on the next release. To disable one, set
it explicitly to `false` in the file; removing an entry does not erase existing
hosted configuration. Routine policy changes require no interactive `az login`.

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

Token counting is an estimate; retain a safety margin and use API-reported usage
as authoritative. A larger context does not recover deleted history. Retention
changes do not rewrite existing documents' TTLs; enable Cosmos container TTL
for per-item expiration to take effect. Longer retention increases stored data
and may increase request costs. Daily summaries and `/tldrtoday` still summarize
the current UTC day, not the entire retention period.

Automatic message shrinking has been retired. Chat context and summaries use
the preserved original text of surviving legacy shrunk records when available;
no documents are rewritten and their expiration deadlines are unchanged.
Missing or expired originals cannot be reconstructed without an external export.
The legacy fields remain readable during the transition. Obsolete
`ShrinkMinimumTokens` and `AzureWebJobs.ShrinkMessage.Disabled` settings can be
removed after verifying the deployed function list no longer contains
`ShrinkMessage`. Daily summaries, `/tldrtoday`, Telegram's 4,096-character send
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
If all three `AZURE_DECISIONS_*` settings are exported, two separate tests each
make one paid Microsoft Decision request: a numerical-response probe and an
explicit-continuation check through the production `DecisionsClient`.
To test only that deployment without chat/image charges, export those three
settings privately and run:

```bash
AZURE_OPENAI_LIVE_TEST=1 dotnet test \
  tests/Ungerfall.ChatGpt.TelegramBot.Tests/Ungerfall.ChatGpt.TelegramBot.Tests.csproj \
  -c Release --filter 'Category=Integration&FullyQualifiedName~AzureOpenAiLiveTests.Decision'
```

`dotnet test` does not load `local.settings.json` automatically. Confirm both
Decision tests execute and pass, with none skipped. The numerical probe allows
15 seconds for provisioning/warm-up diagnostics; the production-client test
uses the bot's actual three-second timeout and 0.95 probability threshold.

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

## 3. Replace the old Function App

The replacement uses the existing `rg-chatgpt` resource group and the existing
Storage, Cosmos DB, and Service Bus resources. It does **not** create a second
database/queue or delete those resources. The old Consumption app is
`fapp-ungerfall-chatgpt-telegram-webhook`; the new Flex app is
`fapp-ungerfall-chatgpt-telegram-flex`. CI is explicitly pinned to the latter
and rejects the former. Do not delete the resource group.

The old Service Bus queues currently have no active messages. They have
dead-letter messages (108 in `tgbot-updates`, 12 in `tgbot-messages` at
inspection); leave these intact for diagnosis. Do not purge queues or dead
letters during replacement.

1. Create `fapp-ungerfall-chatgpt-telegram-flex` in `rg-chatgpt`, **West Europe**,
   Linux **Flex Consumption**, **.NET 10 isolated**, zero always-ready
   instances, and 2,048 MB instance memory. Linux legacy Consumption cannot
   host .NET 10. Reuse storage account `rgchatgptb4db` for host and deployment
   storage; preserve the storage security configuration.
2. Carry forward the existing service-bot configuration that should remain
   unchanged: `TELEGRAM_BOT_TOKEN`, `TELEGRAM_BOT_USERNAME` (if customized),
   `TestUsers`, `QuizChats`, `DailySummaryChats`, and any customized
   `ConversationPolicyChats`. Also carry forward `CosmosDatabase`,
   `CosmosDbConnectionString`, `CosmosTelegramMessagesContainer`,
   `CosmosTimedTasksContainer`, `ServiceBusConnection`, `AzureWebJobsStorage`,
   and the existing Application Insights connection string. Never copy
   `OPENAI_API_KEY`, `OPENAI_ORG`, the deprecated `WEBSITE_RUN_FROM_PACKAGE`,
   or an obsolete Functions runtime setting.
   Configure the `AZURE_OPENAI_*` values from your private local Functions
   settings; keep them private and do not put them in GitHub.
3. Generate a new `TELEGRAM_WEBHOOK_SECRET` (letters, digits, underscore,
   hyphen; 1–256 characters). The new webhook rejects requests without this
   secret. Keep the old webhook registered until the new package is healthy.
4. Before deployment/cutover, disable these functions on the new app:
   `QueuedUpdateHandler`, `DailySummary`, `DailyQuiz`, `DailyFilmsQuiz`,
   `DailyVideoGamesQuiz`, and `ShrinkMessage`.
   Keep the inherited group whitelist intact. Once the webhook is ready, the
   cutover operator enables the queue/timer handlers.

The fork's changes include a Telegram queue wire-format change. Stop the old
app and confirm the active update count is zero before starting the new queue
consumer. Keep the dead-letter queues intact. The webhook should be switched
only after the replacement's health endpoint verifies the exact deployed SHA.

## Deployment announcements

Announcements are a **one-shot post-deployment pipeline step**, not a timer or
startup task. The `release-announcer` console artifact reuses the application's
GitHub source, Azure summary client, Telegram client, and Cosmos receipt code.
It requires the fork's `master` Actions context and matching embedded commit SHA.

After verifying live health at the exact deployed SHA, the existing app-scoped
Azure OIDC identity reads the current Function App settings privately. The runner
receives the existing Azure OpenAI key/summary configuration, Telegram token,
and Cosmos connection in its process environment, not arguments or files.
No new Azure key or GitHub secret copy is required. GitHub API reads use the
job's short-lived `GITHUB_TOKEN` with read-only Contents/Pull requests permissions.
The bot runtime no longer needs release settings or a GitHub token.

Each distinct deployed SHA produces one short Russian announcement to the
single private Telegram user `66215515`. Whitelisted groups and `TestUsers`
entries do not receive release announcements. That user must have started the
deployed bot in a private chat or Telegram will reject the delivery and the
release job will fail. Summaries use the summary deployment and its input/output
budgets. PR titles are supplied as untrusted data; do not put secrets in PR
titles.

After the first announcement, notes include merged PRs between the last
announced commit and the current commit, not just the latest PR. For the first
announcement only PRs merged at the deployed commit are considered, avoiding a
dump of the entire repository history. Direct commits without associated merged
PRs get a generic technical-update message. Reverting to an already announced
SHA does not announce it again.
Comparisons are bounded to 50 commits; larger ranges fail rather than omit changes.

Persistent per-recipient/version receipts use the existing timed-tasks Cosmos
container (`/chatId`) with `ttl: -1`, independently of message retention.
Conditional writes and five-minute processing leases prevent concurrent sends.
There are at most three attempts, ten seconds apart, within an eight-minute runner
deadline. Notes/model failures before sending, and definite Telegram rejections,
release their processing leases for the next bounded attempt.
Before calling Telegram, the receipt is durably marked `sending`. An uncertain
send or crash in that window is **not retried automatically**: avoiding duplicate
messages takes priority over guaranteed delivery. Check logs and the receipt's
stage/message ID before manually recovering a missing announcement. Do not
delete `sending` or `completed` receipts without checking the private chat first.
Cosmos and Telegram cannot provide an atomic exactly-once transaction.

No announcement is sent just because a build finishes or a new worker starts.
After the live health check, the pipeline runner persists a permanent
`release-deployment:<SHA>` approval in the timed-tasks container's `/chatId=0`
partition, then executes delivery. The job succeeds only when the configured
user has a completed receipt with a Telegram message ID. Busy or uncertain
receipts are failures, not silently successful skips. Failures are visible in
Actions; there is no background polling or delayed retry after the job exits.

The old activation endpoint and announcement timers are removed. Legacy
`ReleaseAnnouncementsEnabled`, `ReleaseAnnouncementsRequireApproval`,
`ReleaseVersion`, and `ReleaseRetryDelayMinutes` app settings no longer control
the running bot. They can be removed separately; no key rotation is necessary.

For live Telegram delivery validation, privately configure
`IntegrationTests_TELEGRAM_BOT_TOKEN` and `BOOK_QUIZ_TEST_CHAT` for the
dedicated test bot and private test chat, set
`RELEASE_ANNOUNCEMENT_LIVE_TEST=1`, and run:
`dotnet test tests/Ungerfall.ChatGpt.TelegramBot.Tests/Ungerfall.ChatGpt.TelegramBot.Tests.csproj -c Release --filter 'Category=Integration&FullyQualifiedName~ReleaseAnnouncementLiveTests'`.
This exercises the actual Telegram client and removes its test message after
delivery. For pre-publication notes-generation validation, privately export the
Azure OpenAI settings plus `GITHUB_TOKEN`, set `AZURE_OPENAI_LIVE_TEST=1`, and run:
`dotnet test tests/Ungerfall.ChatGpt.TelegramBot.Tests/Ungerfall.ChatGpt.TelegramBot.Tests.csproj -c Release --filter 'FullyQualifiedName~PipelineAnnouncementUsesRealGitHubAndAzureSummaryWithoutSendingTelegram'`.
This exercises the actual GitHub/model clients and summary generation while
mocking Telegram delivery and Cosmos writes; it does not send production messages.

## 4. Enable automatic CD from the fork

The workflow validates itself, runs deployment-script and offline .NET tests,
and builds all six projects with the SDK pinned in `global.json`. It stamps
the artifact with the exact commit SHA, packages the Function including hidden
binding files, and checks its SHA-256 after downloading it for deployment.
Local settings are never published.

PRs get CI only: no environment credentials, Azure OIDC permission, live model
calls, deployment, or Telegram messages. On fork `master`, a successful push
build (normally after a PR merge) deploys automatically **only when repository
variable `AZURE_DEPLOYMENT_ENABLED=true`**. Manual dispatch on fork `master`
is also supported with that same gate. Deployments are serialized without
cancelling an active deployment; superseded queued commits are skipped.
This workflow never deploys the polling worker or upstream resources.

### One-time portal bootstrap

1. Complete section 3. Review the inherited whitelist before enabling CD:
   every allowed group will receive the deployment announcement.
2. In Azure portal, create a **user-assigned managed identity** in `rg-chatgpt`.
   On the **new Function App**, open **Access control (IAM)**,
   add **Website Contributor**, and select that identity. Scope the assignment
   to this app, not the subscription or old resource group. The workflow needs
   to deploy code, read app metadata/default Function keys, and update the two
   announcement app settings; it does not provision infrastructure. Never
   grant it access to the old app or subscription-wide scopes.
3. On that identity, open **Federated credentials > Add credential**. Choose
   GitHub Actions, organization `ungerfall-coauthored`, repository
   `chatgpt-telegrambot`, entity type **Environment**, name `fork-test`.
   Verify issuer `https://token.actions.githubusercontent.com`, audience
   `api://AzureADTokenExchange`, and the exact subject reported by the
   workflow's **Log in to Azure with OIDC** step. This fork uses the
   immutable-ID subject
   `repo:ungerfall-coauthored@351226014/chatgpt-telegrambot@1460148473:environment:fork-test`.
   If the portal's GitHub template generates the legacy subject without
   `@<ID>` components, use **Other issuer** to enter the exact subject instead.
   A mismatch fails login with `AADSTS700213`; do not broaden the trust or
   print the OIDC token when diagnosing it.
   There is no client secret or publish profile to generate.
4. In the fork's **Settings > Environments > fork-test**, restrict deployment
   branches to selected branch `master`. Leave required reviewers off for
   autonomous deployment, or deliberately enable them to require manual
   approval. Add these environment settings:

   | Kind | Name | Value |
   | --- | --- | --- |
   | Secret | `AZURE_CLIENT_ID` | Managed identity **Client ID**, not Object/Principal ID |
   | Secret | `AZURE_TENANT_ID` | Directory/tenant ID |
   | Secret | `AZURE_SUBSCRIPTION_ID` | Subscription containing the new app |
   | Variable | `AZURE_FUNCTIONAPP_NAME` | `fapp-ungerfall-chatgpt-telegram-flex` |
   | Variable | `AZURE_RESOURCE_GROUP` | `rg-chatgpt` |

   Azure OpenAI, Telegram, Cosmos, and Service Bus credentials stay in Azure
   app settings/Key Vault, never GitHub Actions. Local settings are not
   uploaded. The existing portal integration-test instructions still apply.
5. In **Settings > Secrets and variables > Actions > Variables**, create or
   change the **repository** variable `AZURE_DEPLOYMENT_ENABLED` to `true`
   only after the app settings, identity, network access, and test recipients
   are ready. Run **Build, test and deploy** manually on `master` for the
   initial deployment; subsequent merges deploy without another click.

See [Microsoft's OIDC setup](https://learn.microsoft.com/azure/azure-functions/functions-how-to-github-actions)
and [Flex configuration differences](https://learn.microsoft.com/azure/azure-functions/functions-app-settings#flex-consumption-plan).

### Deployment sequence and recovery

CD verifies the app's isolated naming, Flex runtime metadata, and Azure
hostname **before** uploading code. It deploys the exact tested ZIP, then polls
anonymous `GET /api/health` for the embedded SHA, with a bounded readiness wait.
The separately published announcer has its own verified checksum and must carry
the same embedded SHA. The one-shot Actions step reads hosted credentials via
OIDC and performs announcement generation/delivery directly on the runner.
It does not call an activation endpoint or fetch Function keys.

Health verifies worker startup and artifact identity, not full Telegram/Service
Bus/model functionality. The announcement step additionally exercises GitHub,
the existing Azure summary deployment, Cosmos, and real Telegram delivery.
It makes a paid summary request for actual release notes, not a test message or
image request. Model/network/storage/delivery failures make the job fail even
if the code has already deployed successfully.

If health fails, inspect Azure logs, runtime/configuration and networking.
A mismatched embedded SHA deliberately makes health fail. If announcement
delivery fails, check the sanitized job errors, app-scoped IAM, hosted settings,
and Cosmos receipts. A rerun skips confirmed deliveries. An interrupted send can
have an uncertain outcome, so inspect the approval/receipts rather than
assuming no message was sent. Fix or revert through a new fork PR and merge:
the corrected `master` is rebuilt and deployed. There is no automatic
rollback/zero-downtime promise on this single Flex app. Disable the repository
deployment variable to pause future deployments and their announcements.
