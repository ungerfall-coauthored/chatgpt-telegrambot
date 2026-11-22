using Microsoft.Extensions.Logging.Abstractions;
using Telegram.Bot;
using Telegram.Bot.Requests;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Ungerfall.ChatGpt.TelegramBot.Commands;
using Ungerfall.ChatGpt.TelegramBot.Configuration;

namespace Ungerfall.ChatGpt.TelegramBot.Tests;

[Collection("Conversation policy environment")]
public class DockingGameTests
{
    [Theory]
    [InlineData("/docking")]
    [InlineData("/docking@chatgpt_ungerfall_bot")]
    public async Task CommandSendsRegisteredGameInSameThread(string command)
    {
        var old = Environment.GetEnvironmentVariable("DOCKING_GAME_SHORT_NAME");
        Environment.SetEnvironmentVariable("DOCKING_GAME_SHORT_NAME", "docking");
        try
        {
            using var http = new HttpClient();
            using var scenario = new NaturalRequestScenario(new DecisionsClient(
                http, NullLogger<DecisionsClient>.Instance, new AzureDecisionsSettings()));
            var update = scenario.Update(1, command);
            update.Message!.MessageThreadId = 42;
            scenario.Bot.SendRequest<Message>(Arg.Any<SendGameRequest>(), Arg.Any<CancellationToken>())
                .Returns(new Message());

            await scenario.Handler.Handle(update, default);

            await scenario.Bot.Received(1).SendRequest<Message>(Arg.Is<SendGameRequest>(r =>
                r.GameShortName == "docking" && r.MessageThreadId == 42
                && r.ReplyParameters != null && r.ReplyParameters.MessageId == 1), Arg.Any<CancellationToken>());
        }
        finally { Environment.SetEnvironmentVariable("DOCKING_GAME_SHORT_NAME", old); }
    }

    [Theory]
    [InlineData(123, false, true)]
    [InlineData(124, false, false)]
    [InlineData(123, true, false)]
    public async Task GameCallbackOnlyOpensInAllowedChats(long chatId, bool isBot, bool allowed)
    {
        var old = Environment.GetEnvironmentVariable("DOCKING_GAME_SHORT_NAME");
        Environment.SetEnvironmentVariable("DOCKING_GAME_SHORT_NAME", "docking");
        try
        {
            using var http = new HttpClient();
            using var scenario = new NaturalRequestScenario(new DecisionsClient(
                http, NullLogger<DecisionsClient>.Instance, new AzureDecisionsSettings()));
            var query = new CallbackQuery
            {
                Id = "callback",
                From = new User { Id = 1, FirstName = "Player", IsBot = isBot },
                GameShortName = "docking",
                Message = new Message { Chat = new Chat { Id = chatId, Type = ChatType.Supergroup } },
            };
            scenario.Bot.SendRequest<bool>(Arg.Any<AnswerCallbackQueryRequest>(), Arg.Any<CancellationToken>()).Returns(true);

            await scenario.Handler.Handle(new Update { CallbackQuery = query }, default);

            await scenario.Bot.Received(1).SendRequest<bool>(Arg.Is<AnswerCallbackQueryRequest>(r =>
                r.CallbackQueryId == "callback" && r.Url == (allowed ? DockingGame.GameUrl : null)
                && r.ShowAlert == !allowed), Arg.Any<CancellationToken>());
        }
        finally { Environment.SetEnvironmentVariable("DOCKING_GAME_SHORT_NAME", old); }
    }

    [Fact]
    public async Task UnconfiguredGameDoesNotSendAGameMessage()
    {
        var old = Environment.GetEnvironmentVariable("DOCKING_GAME_SHORT_NAME");
        Environment.SetEnvironmentVariable("DOCKING_GAME_SHORT_NAME", null);
        try
        {
            using var http = new HttpClient();
            using var scenario = new NaturalRequestScenario(new DecisionsClient(
                http, NullLogger<DecisionsClient>.Instance, new AzureDecisionsSettings()));
            scenario.Bot.SendRequest<Message>(Arg.Any<SendMessageRequest>(), Arg.Any<CancellationToken>())
                .Returns(new Message());
            await scenario.Handler.Handle(scenario.Update(1, "/docking"), default);
            await scenario.Bot.DidNotReceive().SendRequest<Message>(Arg.Any<SendGameRequest>(), Arg.Any<CancellationToken>());
            await scenario.Bot.Received(1).SendRequest<Message>(
                Arg.Is<SendMessageRequest>(r => r.Text == "Игра пока не настроена."), Arg.Any<CancellationToken>());
        }
        finally { Environment.SetEnvironmentVariable("DOCKING_GAME_SHORT_NAME", old); }
    }

    [Fact]
    public async Task OtherGamesAreNotRedirectedToDocking()
    {
        var old = Environment.GetEnvironmentVariable("DOCKING_GAME_SHORT_NAME");
        Environment.SetEnvironmentVariable("DOCKING_GAME_SHORT_NAME", "docking");
        try
        {
            using var http = new HttpClient();
            using var scenario = new NaturalRequestScenario(new DecisionsClient(
                http, NullLogger<DecisionsClient>.Instance, new AzureDecisionsSettings()));
            await scenario.Handler.Handle(new Update
            {
                CallbackQuery = new CallbackQuery
                {
                    Id = "callback", GameShortName = "another_game",
                    From = new User { Id = 1, FirstName = "Player" },
                    Message = new Message { Chat = new Chat { Id = 123, Type = ChatType.Supergroup } },
                },
            }, default);
            await scenario.Bot.DidNotReceive().SendRequest<bool>(Arg.Any<AnswerCallbackQueryRequest>(), Arg.Any<CancellationToken>());
        }
        finally { Environment.SetEnvironmentVariable("DOCKING_GAME_SHORT_NAME", old); }
    }
}
