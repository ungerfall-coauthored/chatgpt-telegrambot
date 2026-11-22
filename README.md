# Ungerfall ChatGPT

Telegram bot using Azure OpenAI for chat and optional image generation.
The bot can be run as a polling background service or as an Azure Function using Telegram bot webhooks.
It stores conversation history in Cosmos DB.

## Features

- Automatically responds to messages mentioning the bot.
- Stores conversation history in Cosmos DB for data persistence and retrieval.
- `/systemprompt` shows the normal chat system prompt in an allowed group.
- Sends typing events to indicate when the bot is processing a message.
- Supports two deployment methods:
  - Polling background service.
  - Azure Functions with serverless deployment and scaling using Telegram bot webhooks.
  
## Sequence diagram
![plantuml](docs/sequence-diagram.svg)

## Fork setup

See [Azure setup, live tests and fork deployment](docs/azure-setup.md).
The fork's CI runs offline tests; deployment is manual, restricted to the fork's
`master` branch, and requires a separately configured Azure test environment.

Model instructions are configured in `src/application/prompts.json`. The
file is published with each executable and validated at startup; changing it
requires a new deployment. Group authorization is managed separately in
`Whitelist.cs`. Summary personas are independent of normal chat personas.

## Docking game

Register a game for the bot with [@BotFather](https://t.me/BotFather), then set
`DOCKING_GAME_SHORT_NAME` to its short name. Authorized users can send
`/docking` and tap Play to open the existing
[Docking site](https://ungerfall-coauthored.github.io/docking-game/). The
game runs locally in the browser: two-player mode shares one device; it is
not online multiplayer. With no short name configured, the command reports
that the game is not configured.

The opt-in live test uses the dedicated `@chatgpt_ungerfall_testing_bot`,
requires an existing private chat with it, and consumes that bot's incoming
updates. Stop any other poller and ensure the test bot has no webhook. In
`src/presentation.azure-function/local.settings.json`, privately configure
`IntegrationTests_TELEGRAM_BOT_TOKEN` and `BOOK_QUIZ_TEST_CHAT` (the private
test chat ID). With `docking` registered for the test bot, run from the repo:

```sh
python - <<'PY'
import json, os, subprocess
values = json.load(open('src/presentation.azure-function/local.settings.json'))['Values']
env = os.environ.copy()
env.update({
    'DOCKING_LIVE_TEST': '1',
    'DOCKING_GAME_SHORT_NAME': 'docking',
    'DOCKING_TEST_CHAT': values['BOOK_QUIZ_TEST_CHAT'],
    'IntegrationTests_TELEGRAM_BOT_TOKEN': values['IntegrationTests_TELEGRAM_BOT_TOKEN'],
})
raise SystemExit(subprocess.call([
    'dotnet', 'test', 'tests/Ungerfall.ChatGpt.TelegramBot.Tests/Ungerfall.ChatGpt.TelegramBot.Tests.csproj',
    '-c', 'Release', '--filter', 'Category=Integration&FullyQualifiedName~DockingGameLiveTests',
], env=env))
PY
```

In the test chat send `/docking`, then tap Play within five minutes. The test
checks the real command and callback through the bot's Telegram client, but
visually confirming the game opens and plays is a separate manual check.

## The Linux Programming Interface quiz series

The Azure Function sends one ordered quiz at 06:00 UTC to each chat listed in
`BookQuizChats` (comma-separated numeric IDs). The series is disabled when
that setting is absent or empty; it is separate from the existing quiz queues.
The worker does not schedule this task. To import the small pilot, run:

```sh
dotnet run --project src/utils.quiz-ingestion/from-file -- --file data/linux-programming-interface-pilot.json --book-series
```

The importer prompts for the Cosmos connection string and chat IDs and uses
the existing `telegram-bot` database and `timedTaskExecutions` container. It
validates contiguous order within each batch and safely accepts an identical
re-import; changed entries are not overwritten. Append later batches with
continuing sequence numbers. Each quiz needs a printed page and PDF page;
optional `imageReference` (Telegram file ID or HTTPS URL) requires
`imageDescription`. Images appear in the poll description.

A missing next sequence stops the series instead of skipping ahead. An
unconfirmed send also stops it: after checking Telegram, an operator must
disable the series, reconcile the `book-quiz-progress:linux-programming-interface`
document in that chat's Cosmos partition, then re-enable it. If the poll was
delivered, increment `nextSequence` and set `lastSentUtc` to the delivery time;
if it was definitely not delivered, leave `nextSequence` unchanged. In either
case set `sending` to false using an ETag-conditional update. Never retry
blindly: Telegram delivery and Cosmos updates cannot be atomic.

The opt-in live test sends a real image poll and writes test data. It removes
its own records after success; a failure can leave them behind for diagnosis.
Use a dedicated empty test container and chat, and privately set
`BOOK_QUIZ_LIVE_TEST=1`, `BOOK_QUIZ_TEST_CHAT`, `BOOK_QUIZ_TEST_IMAGE`,
`IntegrationTests_TELEGRAM_BOT_TOKEN`, `CosmosDbConnectionString`,
`CosmosDatabase`, and `CosmosTimedTasksContainer`. Run:

```sh
dotnet test tests/Ungerfall.ChatGpt.TelegramBot.Tests/Ungerfall.ChatGpt.TelegramBot.Tests.csproj -c Release --filter 'Category=Integration&FullyQualifiedName~BookQuizLiveTests'
```

## Contributing

Feel free to create issues or submit pull requests for any improvements or bug fixes you would like to contribute.
