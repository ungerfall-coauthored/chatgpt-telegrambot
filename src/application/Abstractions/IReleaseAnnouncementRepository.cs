using System.Threading;
using System.Threading.Tasks;
using Ungerfall.ChatGpt.TelegramBot.Database;

namespace Ungerfall.ChatGpt.TelegramBot.Abstractions;

public interface IReleaseAnnouncementRepository
{
    Task<bool> IsDeploymentApproved(string version, CancellationToken cancellation);
    Task ApproveDeployment(string version, CancellationToken cancellation);
    Task<bool> IsCompleted(long chatId, string version, CancellationToken cancellation);
    Task<ReleaseAnnouncementReceipt?> Begin(long chatId, string version, CancellationToken cancellation);
    Task Save(ReleaseAnnouncementReceipt receipt, CancellationToken cancellation);
    Task Release(ReleaseAnnouncementReceipt receipt, CancellationToken cancellation);
}