using Ungerfall.ChatGpt.TelegramBot.Configuration;

namespace Ungerfall.ChatGpt.TelegramBot.Tests;

public class PromptCatalogTests
{
    [Fact]
    public void GroupPersonaChangesWithoutChangingTheSummaryOrOtherGroup()
    {
        var catalog = PromptCatalog.Load();
        var whitelist = new Whitelist(catalog);
        const long group = -1001034436662;
        const long food = -149593031;

        Assert.Contains("участник общего Telegram-чата друзей", whitelist.GetSystemRoleMessage(group));
        Assert.Contains("Отвечай как специалист", whitelist.GetSummarySystemRoleMessage(group));
        Assert.NotEqual(whitelist.GetSystemRoleMessage(group), whitelist.GetSummarySystemRoleMessage(group));
        Assert.Equal(whitelist.GetSummarySystemRoleMessage(food), whitelist.GetSystemRoleMessage(food));
        Assert.False(whitelist.IsGroupAllowedToUseBot(-123));
    }

    [Fact]
    public void MissingInstructionsAreRejected()
    {
        var file = Path.GetTempFileName();
        try
        {
            File.WriteAllText(file, "{}");
            Assert.Throws<InvalidDataException>(() => PromptCatalog.Load(file));
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void MissingAllowedGroupPromptIsRejected()
    {
        var catalog = PromptCatalog.Load();
        catalog.Chat.Groups.Remove(-1001034436662);
        Assert.Throws<InvalidOperationException>(() => new Whitelist(catalog));
    }
}
