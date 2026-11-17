using System.Text.Json;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
namespace Platform;
public sealed class DynamoStore(IAmazonDynamoDB db, string table) : IStateStore
{
    private static Dictionary<string, AttributeValue> Key(Guid id) => new() { ["id"] = new AttributeValue { S = id.ToString() } };
    public async Task<State?> Get(Guid id, CancellationToken ct = default)
    {
        var result = await db.GetItemAsync(new GetItemRequest { TableName = table, Key = Key(id), ConsistentRead = true }, ct);
        return result.Item?.TryGetValue("document", out var json) == true ? JsonSerializer.Deserialize<State>(json.S, JsonDefaults.Options) : null;
    }
    public async Task Save(State state, long expectedVersion, CancellationToken ct = default)
    {
        state.Version = expectedVersion + 1;
        var item = Key(state.Id);
        item["version"] = new() { N = state.Version.ToString() };
        item["pending"] = new() { N = state.Outbox.Count.ToString() };
        item["document"] = new() { S = JsonSerializer.Serialize(state, JsonDefaults.Options) };
        try
        {
            await db.PutItemAsync(new PutItemRequest
            {
                TableName = table, Item = item,
                ConditionExpression = expectedVersion == 0 ? "attribute_not_exists(id)" : "#v = :expected",
                ExpressionAttributeNames = expectedVersion == 0 ? null : new() { ["#v"] = "version" },
                ExpressionAttributeValues = expectedVersion == 0 ? null : new() { [":expected"] = new() { N = expectedVersion.ToString() } }
            }, ct);
        }
        catch (ConditionalCheckFailedException) { throw new ConflictException(); }
    }
    public async Task<IReadOnlyList<State>> Pending(CancellationToken ct = default)
    {
        var states = new List<State>();
        Dictionary<string, AttributeValue>? cursor = null;
        do
        {
            var page = await db.ScanAsync(new ScanRequest
            {
                TableName = table, FilterExpression = "pending > :zero",
                ExpressionAttributeValues = new() { [":zero"] = new() { N = "0" } },
                ExclusiveStartKey = cursor, ConsistentRead = true
            }, ct);
            states.AddRange((page.Items ?? []).Select(item => JsonSerializer.Deserialize<State>(item["document"].S, JsonDefaults.Options)!));
            cursor = page.LastEvaluatedKey;
        } while (cursor?.Count > 0 && states.Count < 50);
        return states;
    }
}
