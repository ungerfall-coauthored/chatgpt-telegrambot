namespace Ungerfall.ChatGpt.TelegramBot.Database;

public sealed class ReleaseDeploymentApproval
{
    public string Id { get; set; } = null!;
    public long ChatId { get; set; }
    public string Version { get; set; } = null!;
    public int Ttl { get; set; } = -1;
}