using System.Threading;
using System.Threading.Tasks;
using Ungerfall.ChatGpt.TelegramBot.Database;

namespace Ungerfall.ChatGpt.TelegramBot.Abstractions;

public interface IReleaseAnnouncementRepository
{
    Task<ReleaseAnnouncementReceipt?> Begin(long chatId, string version, CancellationToken cancellation);
    Task Save(ReleaseAnnouncementReceipt receipt, CancellationToken cancellation);
    Task Release(ReleaseAnnouncementReceipt receipt, CancellationToken cancellation);
}