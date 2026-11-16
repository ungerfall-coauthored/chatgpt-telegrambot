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
   compatible with the 2024-10-21 Azure API (for example `gpt-4o-mini`, if
   available). Record the **deployment name**, not just the model name. Optionally
   deploy `gpt-4o` separately for daily summaries; otherwise summaries use the
   chat deployment, unlike the upstream default.
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
Token budgeting currently uses the `gpt-4o-mini` tokenizer, so different model
families may need different budgeting. The optional upstream Decisions feature
is a **separate OpenAI API** integration, not Azure: leave `DECISIONS_API_KEY`
unset and `DecisionsEnabled`, `ContinuationEnabled`, and `AutonomousEnabled`
false unless you deliberately configure and accept that separate service.

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

The test makes one short paid chat request per distinct deployment and, if
configured, one paid image request. It does not test Telegram or Cosmos DB.
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
