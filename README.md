# Ungerfall ChatGPT

Telegram bot using Azure OpenAI for chat and optional image generation.
The bot can be run as a polling background service or as an Azure Function using Telegram bot webhooks.
It stores conversation history in Cosmos DB.

## Features

- Automatically responds to messages mentioning the bot.
- Stores conversation history in Cosmos DB for data persistence and retrieval.
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

## Contributing

Feel free to create issues or submit pull requests for any improvements or bug fixes you would like to contribute.
