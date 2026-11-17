using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Platform;
namespace Tests;

public class StorageTests
{
    [Fact]
    public async Task DynamoAtomicStateAndConflict()
    {
        var endpoint = Environment.GetEnvironmentVariable("TEST_AWS_ENDPOINT") ?? "http://localhost:4566";
        using var db = new AmazonDynamoDBClient(new Amazon.Runtime.BasicAWSCredentials("test", "test"),
            new AmazonDynamoDBConfig { ServiceURL = endpoint, AuthenticationRegion = "us-east-1" });
        var table = "test-execution-" + Guid.NewGuid().ToString("N");
        await db.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            BillingMode = BillingMode.PAY_PER_REQUEST,
            AttributeDefinitions = [new("id", ScalarAttributeType.S)],
            KeySchema = [new("id", KeyType.HASH)]
        });
        try { await Verify(new DynamoStore(db, table)); }
        finally { await db.DeleteTableAsync(table); }
    }
    static async Task Verify(IStateStore store)
    {
        var id = Guid.NewGuid(); Assert.Null(await store.Get(id));
        var state = new State { Id = id, Status = "Waiting" };
        state.Outbox.Add(Message.Create(id, "Test", "os", new()));
        await store.Save(state, 0);
        Assert.Equal("Waiting", (await store.Get(id))!.Status);
        await Assert.ThrowsAsync<ConflictException>(() => store.Save(new State { Id = id }, 0));
        var saved = (await store.Get(id))!; saved.Move("Ready");
        await store.Save(saved, 1);
        await Assert.ThrowsAsync<ConflictException>(() => store.Save(new State { Id = id }, 1));
        Assert.Contains(await store.Pending(), x => x.Id == id);
        var clean = (await store.Get(id))!;
        clean.Outbox.Clear();
        await store.Save(clean, clean.Version);
    }
}
