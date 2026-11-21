using Microsoft.Extensions.Logging.Abstractions;
using Telegram.Bot;
using Telegram.Bot.Requests;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Ungerfall.ChatGpt.TelegramBot.Configuration;

namespace Ungerfall.ChatGpt.TelegramBot.Tests;

[Collection("Conversation policy environment")]
public class SystemPromptCommandTests
{
    [Theory]
    [InlineData("/systemprompt")]
    [InlineData("/systemprompt@chatgpt_ungerfall_bot")]
    public async Task AllowedGroupReceivesItsPromptAsPlainText(string command)
    {
        using var http = new HttpClient();
        using var scenario = new NaturalRequestScenario(new DecisionsClient(
            http, NullLogger<DecisionsClient>.Instance, new AzureDecisionsSettings()));
        scenario.Bot.SendRequest<Message>(Arg.Any<SendMessageRequest>(), Arg.Any<CancellationToken>())
            .Returns(new Message { Id = 10 });

        await scenario.Handler.Handle(scenario.Update(1, command), default);

        await scenario.Bot.Received(1).SendRequest<Message>(
            Arg.Is<SendMessageRequest>(request =>
                request.Text == "Answer the user's cooking question."
                && request.ParseMode == ParseMode.None
                && request.ReplyParameters != null && request.ReplyParameters.MessageId == 1),
            Arg.Any<CancellationToken>());
        await scenario.Bot.DidNotReceive().SendRequest<Message>(Arg.Any<SendRichMessageRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PrivateChatDoesNotReceiveGroupPrompt()
    {
        using var http = new HttpClient();
        using var scenario = new NaturalRequestScenario(new DecisionsClient(
            http, NullLogger<DecisionsClient>.Instance, new AzureDecisionsSettings()));
        var update = scenario.Update(1, "/systemprompt");
        update.Message!.Chat.Type = ChatType.Private;

        await scenario.Handler.Handle(update, default);

        await scenario.Bot.DidNotReceive().SendRequest<Message>(Arg.Any<SendMessageRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UnapprovedGroupDoesNotReceivePrompt()
    {
        var previous = Environment.GetEnvironmentVariable("TestUsers");
        Environment.SetEnvironmentVariable("TestUsers", "999");
        try
        {
            using var http = new HttpClient();
            using var scenario = new NaturalRequestScenario(new DecisionsClient(
                http, NullLogger<DecisionsClient>.Instance, new AzureDecisionsSettings()));
            var update = scenario.Update(1, "/systemprompt");
            update.Message!.Chat.Id = 124;
            scenario.Bot.SendRequest<Message>(Arg.Any<SendMessageRequest>(), Arg.Any<CancellationToken>())
                .Returns(new Message { Id = 10 });

            await scenario.Handler.Handle(update, default);

            await scenario.Bot.DidNotReceive().SendRequest<Message>(
                Arg.Is<SendMessageRequest>(request => request.Text.Contains("Answer the user's cooking question.")),
                Arg.Any<CancellationToken>());
        }
        finally
        {
            Environment.SetEnvironmentVariable("TestUsers", previous);
        }
    }
}
