using Telegram.Bot;
using Telegram.Bot.Requests;
using Telegram.Bot.Types;
using Ungerfall.ChatGpt.TelegramBot.TimedTasks;

namespace Ungerfall.ChatGpt.TelegramBot.Tests;

public class OnThisDayTests
{
    [Theory]
    [InlineData(2025, 2, 28, 2024, "2024-02-27T21:00:00", "2024-02-29T21:00:00")]
    [InlineData(2025, 2, 28, 2023, "2023-02-27T21:00:00", "2023-02-28T21:00:00")]
    [InlineData(2024, 2, 28, 2020, "2020-02-27T21:00:00", "2020-02-28T21:00:00")]
    [InlineData(2024, 1, 1, 2022, "2021-12-31T21:00:00", "2022-01-01T21:00:00")]
    public void PeriodUsesLocalDayAndRequestedLeapDayPolicy(
        int currentYear, int month, int day, int previousYear, string start, string end)
    {
        var period = OnThisDay.Period(new DateOnly(currentYear, month, day), previousYear);
        Assert.NotNull(period);
        Assert.Equal((start, end), period.Value);
    }

    [Fact]
    public void February29HasNoEquivalentInNonLeapYears()
    {
        Assert.Null(OnThisDay.Period(new DateOnly(2024, 2, 29), 2023));
    }

    [Fact]
    public async Task ConsentIsCheckedBeforeEverySplitMessage()
    {
        var bot = Substitute.For<ITelegramBotClient>();
        bot.SendRequest<Message>(Arg.Any<SendMessageRequest>(), Arg.Any<CancellationToken>())
            .Returns(new Message());
        var checks = 0;
        await Assert.ThrowsAsync<InvalidOperationException>(() => bot.SendResponse(-1, new string('a', 5000),
            beforeSend: _ => ++checks == 2 ? Task.FromException(new InvalidOperationException()) : Task.CompletedTask));
        Assert.Equal(2, checks);
        await bot.Received(1).SendRequest<Message>(Arg.Any<SendMessageRequest>(), Arg.Any<CancellationToken>());
    }
}
