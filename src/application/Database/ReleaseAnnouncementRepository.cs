using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Options;
using System;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Ungerfall.ChatGpt.TelegramBot.Abstractions;
using Ungerfall.ChatGpt.TelegramBot.Configuration;

namespace Ungerfall.ChatGpt.TelegramBot.Database;

public sealed class ReleaseAnnouncementRepository : IReleaseAnnouncementRepository
{
    private readonly Container _container;

    public ReleaseAnnouncementRepository(CosmosClient cosmos, IOptions<CosmosDbOptions> options)
    {
        _container = cosmos.GetContainer(options.Value.DatabaseId, options.Value.TimedTasksContainerId);
    }

    public async Task<bool> IsCompleted(long chatId, string version, CancellationToken cancellation)
    {
        var receipt = await Read("release-announcement:" + version, chatId, cancellation);
        return receipt?.Stage == ReleaseAnnouncementReceipt.Completed && receipt.TelegramMessageId != null;
    }

    public async Task<bool> IsDeploymentApproved(string version, CancellationToken cancellation)
    {
        try
        {
            var response = await _container.ReadItemAsync<ReleaseDeploymentApproval>(
                "release-deployment:" + version, new PartitionKey(0), cancellationToken: cancellation);
            return response.Resource.Version == version;
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound) { return false; }
    }

    public async Task ApproveDeployment(string version, CancellationToken cancellation)
    {
        if (!ReleaseAnnouncementSettings.IsCommit(version))
            throw new ArgumentException("Deployment approval requires a full commit SHA.");
        var approval = new ReleaseDeploymentApproval { Id = "release-deployment:" + version, Version = version };
        try
        {
            await _container.CreateItemAsync(approval, new PartitionKey(0), cancellationToken: cancellation);
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.Conflict) { }
    }

    public async Task<ReleaseAnnouncementReceipt?> Begin(long chatId, string version, CancellationToken cancellation)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var id = "release-announcement:" + version;
            var receipt = await Read(id, chatId, cancellation);
            if (receipt != null && (receipt.Stage != ReleaseAnnouncementReceipt.Processing || receipt.LeaseUntilUtc > DateTime.UtcNow))
                return null;
            receipt ??= new ReleaseAnnouncementReceipt
            {
                Id = id,
                ChatId = chatId,
                Version = version,
                CreatedUtc = DateTime.UtcNow,
                PreviousVersion = await PreviousVersion(chatId, cancellation),
            };
            receipt.LeaseId = Guid.NewGuid().ToString();
            receipt.LeaseUntilUtc = DateTime.UtcNow.AddMinutes(5);
            try
            {
                await Save(receipt, cancellation);
                return receipt;
            }
            catch (CosmosException ex) when (ex.StatusCode is HttpStatusCode.Conflict or HttpStatusCode.PreconditionFailed) { }
        }
        return null;
    }

    public async Task Save(ReleaseAnnouncementReceipt receipt, CancellationToken cancellation)
    {
        var response = receipt.ETag == null
            ? await _container.CreateItemAsync(receipt, new PartitionKey(receipt.ChatId), cancellationToken: cancellation)
            : await _container.ReplaceItemAsync(receipt, receipt.Id, new PartitionKey(receipt.ChatId),
                new ItemRequestOptions { IfMatchEtag = receipt.ETag }, cancellation);
        receipt.ETag = response.ETag;
    }

    public async Task Release(ReleaseAnnouncementReceipt receipt, CancellationToken cancellation)
    {
        var current = await Read(receipt.Id, receipt.ChatId, cancellation);
        if (current?.LeaseId != receipt.LeaseId || current.Stage != ReleaseAnnouncementReceipt.Processing)
            return;
        current.LeaseUntilUtc = DateTime.UtcNow;
        await Save(current, cancellation);
    }

    private async Task<ReleaseAnnouncementReceipt?> Read(string id, long chatId, CancellationToken cancellation)
    {
        try
        {
            var response = await _container.ReadItemAsync<ReleaseAnnouncementReceipt>(id, new PartitionKey(chatId),
                cancellationToken: cancellation);
            response.Resource.ETag = response.ETag;
            return response.Resource;
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound) { return null; }
    }

    private async Task<string?> PreviousVersion(long chatId, CancellationToken cancellation)
    {
        var query = new QueryDefinition("""
            SELECT TOP 1 * FROM c
            WHERE c.type = "release-announcement" AND (c.stage = "sending" OR c.stage = "completed")
            ORDER BY c.createdUtc DESC
            """);
        using var iterator = _container.GetItemQueryIterator<ReleaseAnnouncementReceipt>(query,
            requestOptions: new QueryRequestOptions { PartitionKey = new PartitionKey(chatId), MaxItemCount = 1 });
        while (iterator.HasMoreResults)
        {
            var page = await iterator.ReadNextAsync(cancellation);
            if (page.FirstOrDefault() is { } receipt) return receipt.Version;
        }
        return null;
    }
}