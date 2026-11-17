using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Options;
using System.Net;
using Ungerfall.ChatGpt.TelegramBot.Configuration;
using Ungerfall.ChatGpt.TelegramBot.Database;

namespace Ungerfall.ChatGpt.TelegramBot.Tests;

public class ReleaseAnnouncementRepositoryTests
{
    private const string Version = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    [Fact]
    public async Task ConcurrentClaimsHaveOnlyOneOwner()
    {
        var storage = new Storage();
        var claims = await Task.WhenAll(Enumerable.Range(0, 10)
            .Select(_ => Task.Run(() => storage.Repository.Begin(-1, Version, default))));
        Assert.Single(claims.OfType<ReleaseAnnouncementReceipt>());
    }

    [Fact]
    public async Task DeploymentApprovalIsPersistentIdempotentAndVersionSpecific()
    {
        var storage = new Storage();
        Assert.False(await storage.Repository.IsDeploymentApproved(Version, default));
        await storage.Repository.ApproveDeployment(Version, default);
        await storage.Repository.ApproveDeployment(Version, default);
        Assert.True(await storage.Repository.IsDeploymentApproved(Version, default));
        Assert.False(await storage.Repository.IsDeploymentApproved(new string('b', 40), default));
    }

    [Theory]
    [InlineData(ReleaseAnnouncementReceipt.Sending)]
    [InlineData(ReleaseAnnouncementReceipt.Completed)]
    public async Task DeliveryReceiptsPersistAcrossExpiredLeases(string stage)
    {
        var storage = new Storage();
        var receipt = (await storage.Repository.Begin(-1, Version, default))!;
        receipt.Stage = stage;
        receipt.LeaseUntilUtc = DateTime.UtcNow.AddDays(-2);
        await storage.Repository.Save(receipt, default);
        Assert.Null(await storage.Repository.Begin(-1, Version, default));
        Assert.Equal(-1, storage.Read(receipt).Ttl);
    }

    [Fact]
    public async Task ExpiredOwnerCannotOverwriteOrReleaseNewOwnersClaim()
    {
        var storage = new Storage();
        var first = (await storage.Repository.Begin(-1, Version, default))!;
        first.LeaseUntilUtc = DateTime.UtcNow.AddSeconds(-1);
        await storage.Repository.Save(first, default);
        var second = (await storage.Repository.Begin(-1, Version, default))!;
        Assert.NotEqual(first.LeaseId, second.LeaseId);
        first.Stage = ReleaseAnnouncementReceipt.Sending;
        var error = await Assert.ThrowsAsync<CosmosException>(() => storage.Repository.Save(first, default));
        Assert.Equal(HttpStatusCode.PreconditionFailed, error.StatusCode);
        await storage.Repository.Release(first, default);
        Assert.Equal(second.LeaseId, storage.Read(second).LeaseId);
        Assert.Null(await storage.Repository.Begin(-1, Version, default));
    }

    [Fact]
    public async Task ReleasedProcessingLeaseCanRetry_AndNextVersionUsesLastDeliveredBaseline()
    {
        var storage = new Storage();
        var first = (await storage.Repository.Begin(-1, Version, default))!;
        first.Summary = "Cached summary";
        await storage.Repository.Save(first, default);
        await storage.Repository.Release(first, default);
        Assert.Null(await storage.Repository.Begin(-1, Version, default));
        Assert.True(storage.Read(first).LeaseUntilUtc > DateTime.UtcNow.AddMinutes(14));
        storage.Expire(first);
        var retry = (await storage.Repository.Begin(-1, Version, default))!;
        Assert.Equal(first.Summary, retry.Summary);
        retry.Stage = ReleaseAnnouncementReceipt.Completed;
        await storage.Repository.Save(retry, default);
        var next = (await storage.Repository.Begin(-1, new string('b', 40), default))!;
        Assert.Equal(Version, next.PreviousVersion);
        var anotherGroup = (await storage.Repository.Begin(-2, new string('b', 40), default))!;
        Assert.Null(anotherGroup.PreviousVersion);
    }

    private sealed class Storage
    {
        private readonly object _gate = new();
        private readonly Dictionary<(string Id, string Partition), (ReleaseAnnouncementReceipt Document, string ETag)> _items = [];
        private readonly Dictionary<string, ReleaseDeploymentApproval> _approvals = [];
        private readonly CosmosSystemTextJsonSerializer _serializer = new();
        public ReleaseAnnouncementRepository Repository { get; }

        public Storage()
        {
            var container = Substitute.For<Container>();
            var cosmos = Substitute.For<CosmosClient>();
            cosmos.GetContainer("db", "tasks").Returns(container);
            Repository = new ReleaseAnnouncementRepository(cosmos,
                Options.Create(new CosmosDbOptions { DatabaseId = "db", TimedTasksContainerId = "tasks" }),
                new ReleaseAnnouncementSettings());
            container.ReadItemAsync<ReleaseDeploymentApproval>(Arg.Any<string>(), Arg.Any<PartitionKey>(),
                Arg.Any<ItemRequestOptions>(), Arg.Any<CancellationToken>()).Returns(call =>
                {
                    if (!_approvals.TryGetValue(call.Arg<string>(), out var approval))
                        throw Failure(HttpStatusCode.NotFound);
                    Assert.Equal(new PartitionKey(0), call.Arg<PartitionKey>());
                    var response = Substitute.For<ItemResponse<ReleaseDeploymentApproval>>();
                    response.Resource.Returns(approval);
                    return Task.FromResult(response);
                });
            container.CreateItemAsync(Arg.Any<ReleaseDeploymentApproval>(), Arg.Any<PartitionKey?>(),
                Arg.Any<ItemRequestOptions>(), Arg.Any<CancellationToken>()).Returns(call =>
                {
                    var approval = call.Arg<ReleaseDeploymentApproval>();
                    Assert.Equal<PartitionKey?>(new PartitionKey(0), call.Arg<PartitionKey?>());
                    var copy = _serializer.FromStream<ReleaseDeploymentApproval>(_serializer.ToStream(approval));
                    Assert.Equal(-1, copy.Ttl);
                    if (!_approvals.TryAdd(copy.Id, copy)) throw Failure(HttpStatusCode.Conflict);
                    return Task.FromResult(Substitute.For<ItemResponse<ReleaseDeploymentApproval>>());
                });
            container.ReadItemAsync<ReleaseAnnouncementReceipt>(Arg.Any<string>(), Arg.Any<PartitionKey>(),
                Arg.Any<ItemRequestOptions>(), Arg.Any<CancellationToken>()).Returns(call =>
                {
                    lock (_gate)
                    {
                        if (!_items.TryGetValue((call.Arg<string>(), call.Arg<PartitionKey>().ToString()), out var item))
                            throw Failure(HttpStatusCode.NotFound);
                        return Task.FromResult(Response(item.Document, item.ETag));
                    }
                });
            container.CreateItemAsync(Arg.Any<ReleaseAnnouncementReceipt>(), Arg.Any<PartitionKey?>(),
                Arg.Any<ItemRequestOptions>(), Arg.Any<CancellationToken>()).Returns(call =>
                {
                    lock (_gate)
                    {
                        var receipt = call.Arg<ReleaseAnnouncementReceipt>();
                        var key = (receipt.Id, new PartitionKey(receipt.ChatId).ToString());
                        if (_items.ContainsKey(key)) throw Failure(HttpStatusCode.Conflict);
                        var etag = Guid.NewGuid().ToString();
                        _items.Add(key, (Clone(receipt), etag));
                        return Task.FromResult(Response(receipt, etag));
                    }
                });
            container.ReplaceItemAsync(Arg.Any<ReleaseAnnouncementReceipt>(), Arg.Any<string>(), Arg.Any<PartitionKey?>(),
                Arg.Any<ItemRequestOptions>(), Arg.Any<CancellationToken>()).Returns(call =>
                {
                    lock (_gate)
                    {
                        var receipt = call.Arg<ReleaseAnnouncementReceipt>();
                        var key = (receipt.Id, new PartitionKey(receipt.ChatId).ToString());
                        if (!_items.TryGetValue(key, out var item)
                            || item.ETag != call.Arg<ItemRequestOptions>().IfMatchEtag)
                            throw Failure(HttpStatusCode.PreconditionFailed);
                        var etag = Guid.NewGuid().ToString();
                        _items[key] = (Clone(receipt), etag);
                        return Task.FromResult(Response(receipt, etag));
                    }
                });
            container.GetItemQueryIterator<ReleaseAnnouncementReceipt>(Arg.Any<QueryDefinition>(),
                Arg.Any<string>(), Arg.Any<QueryRequestOptions>()).Returns(call =>
                {
                    ReleaseAnnouncementReceipt[] documents;
                    lock (_gate)
                    {
                        documents = _items.Where(x => x.Key.Partition == call.Arg<QueryRequestOptions>().PartitionKey!.Value.ToString())
                            .Select(x => x.Value.Document)
                            .Where(x => x.Stage is ReleaseAnnouncementReceipt.Sending or ReleaseAnnouncementReceipt.Completed)
                            .OrderByDescending(x => x.CreatedUtc).Take(1).Select(Clone).ToArray();
                    }
                    var page = Substitute.For<FeedResponse<ReleaseAnnouncementReceipt>>();
                    page.GetEnumerator().Returns(_ => documents.AsEnumerable().GetEnumerator());
                    var iterator = Substitute.For<FeedIterator<ReleaseAnnouncementReceipt>>();
                    iterator.HasMoreResults.Returns(true, false);
                    iterator.ReadNextAsync(Arg.Any<CancellationToken>()).Returns(page);
                    return iterator;
                });
        }

        public ReleaseAnnouncementReceipt Read(ReleaseAnnouncementReceipt receipt)
        {
            lock (_gate) return Clone(_items[(receipt.Id, new PartitionKey(receipt.ChatId).ToString())].Document);
        }

        public void Expire(ReleaseAnnouncementReceipt receipt)
        {
            lock (_gate)
                _items[(receipt.Id, new PartitionKey(receipt.ChatId).ToString())].Document.LeaseUntilUtc = DateTime.UtcNow.AddSeconds(-1);
        }

        private ReleaseAnnouncementReceipt Clone(ReleaseAnnouncementReceipt receipt) =>
            _serializer.FromStream<ReleaseAnnouncementReceipt>(_serializer.ToStream(receipt));

        private ItemResponse<ReleaseAnnouncementReceipt> Response(ReleaseAnnouncementReceipt document, string etag)
        {
            var response = Substitute.For<ItemResponse<ReleaseAnnouncementReceipt>>();
            response.Resource.Returns(Clone(document));
            response.ETag.Returns(etag);
            return response;
        }

        private static CosmosException Failure(HttpStatusCode status) => new("Simulated storage response", status, 0, "test", 0);
    }
}
