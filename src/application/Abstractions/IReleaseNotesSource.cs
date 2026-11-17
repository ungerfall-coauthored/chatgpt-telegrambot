using System.Threading;
using System.Threading.Tasks;

namespace Ungerfall.ChatGpt.TelegramBot.Abstractions;

public interface IReleaseNotesSource
{
    Task<string[]> GetTitles(string? previousVersion, string version, CancellationToken cancellation);
}