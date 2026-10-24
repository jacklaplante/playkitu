using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using System.Globalization;
using System.Text.Json;

namespace Playkitu.Issuer;

public sealed class UnknownCommitException : Exception;

// Low-level DynamoDB transactions retain one atomic boundary for admission quotas,
// stream leases, replay receipts and upload reservations. No runtime resource creation.
public sealed class DynamoIssuerStore(IAmazonDynamoDB client, string table)
{
    public static Dictionary<string, AttributeValue> Key(DbKey key) => new() { ["pk"] = new() { S = key.Pk }, ["sk"] = new() { S = key.Sk } };
    public async Task<Stored<T>?> GetAsync<T>(DbKey key, CancellationToken ct) where T : class
    {
        var response = await client.GetItemAsync(new GetItemRequest { TableName = table, Key = Key(key), ConsistentRead = true }, ct);
        if (response.Item is null || response.Item.Count == 0) return null;
        return Decode<T>(response.Item);
    }
    private static Stored<T> Decode<T>(Dictionary<string, AttributeValue> item) where T : class =>
        new(JsonSerializer.Deserialize<T>(item["data"].S, Wire.Json) ?? throw new InvalidOperationException("Invalid issuer state."), long.Parse(item["v"].N, CultureInfo.InvariantCulture));

    public async Task<Control> GetControlAsync(CancellationToken ct)
    {
        var response = await client.GetItemAsync(new GetItemRequest { TableName = table, Key = Key(DbKey.Control), ConsistentRead = true }, ct);
        if (response.Item is null || !response.Item.TryGetValue("admissionsEnabled", out var ae) || !response.Item.TryGetValue("uploadsEnabled", out var ue))
            throw new IssuerError(503, "issuer_not_configured");
        var state = Decode<GlobalState>(response.Item);
        if (state.Value.SchemaVersion is not (1 or 2)
            || state.Value.SchemaVersion == 1 && (state.Value.Slots.Length is < 1 or > Deployment.MaxVideoStreams || state.Value.Pending.Count > 8 || state.Value.Active.Count > 8)
            || state.Value.SchemaVersion == 2 && (state.Value.Slots.Length != 0 || state.Value.Pending.Count != 0 || state.Value.Active.Count != 0))
            throw new IssuerError(503, "invalid_issuer_state");
        return new(state, ae.BOOL == true, ue.BOOL == true);
    }

    public async Task<IReadOnlyList<StreamWork>> DueStreamsAsync(DateTimeOffset now, CancellationToken ct)
    {
        var due = new List<StreamWork>();
        // Bounded fair work per shard. GSI reads may be stale: Reconcile always
        // strongly rereads the ledger/version before any lifecycle transition.
        for (var shard = 0; shard < 16; shard++) {
            var result = await client.QueryAsync(new QueryRequest { TableName = table, IndexName = "stream-work-by-due",
                KeyConditionExpression = "workShard = :shard AND workDue <= :due", ScanIndexForward = true, Limit = 32,
                ExpressionAttributeValues = new() { [":shard"] = new() { S = shard.ToString("X") },
                    [":due"] = new() { N = now.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture) } } }, ct);
            foreach (var item in result.Items ?? []) {
                if (!item.TryGetValue("pk", out var pk) || !pk.S.StartsWith("STREAM#", StringComparison.Ordinal))
                    throw new IssuerError(503, "invalid_stream_work");
                due.Add(Decode<StreamWork>(item).Value);
            }
        }
        return due;
    }

    public Put Write<T>(DbKey key, T value, long? expectedVersion, long? purgeAt = null)
    {
        var item = Key(key);
        item["v"] = new() { N = ((expectedVersion ?? 0) + 1).ToString(CultureInfo.InvariantCulture) };
        item["data"] = new() { S = JsonSerializer.Serialize(value, Wire.Json) };
        if (purgeAt is { } ttl) item["purgeAt"] = new() { N = ttl.ToString(CultureInfo.InvariantCulture) };
        // Only admitted session metadata enters this sparse index; object reservations do not.
        if (value is SessionState { AdmittedAt: { } admitted } session)
        {
            item["sessionDay"] = new() { S = admitted.UtcDateTime.ToString("yyyy-MM-dd") };
            item["sessionOrder"] = new() { S = $"{admitted:O}#{session.SessionId:N}" };
        }
        if (value is StreamWork work && !work.Deleted) {
            item["workShard"] = new() { S = Convert.ToInt32(work.AdmissionId[..2], 16).ToString("X2")[1..] };
            item["workDue"] = new() { N = work.Due.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture) };
        }
        return new Put { TableName = table, Item = item,
            ConditionExpression = expectedVersion is null ? "attribute_not_exists(#pk)" : "#v = :v",
            ExpressionAttributeNames = expectedVersion is null ? new() { ["#pk"] = "pk" } : new() { ["#v"] = "v" },
            ExpressionAttributeValues = expectedVersion is null ? null : new() { [":v"] = new() { N = expectedVersion.Value.ToString(CultureInfo.InvariantCulture) } } };
    }
    public Put WriteControl(Control control)
    {
        var put = Write(DbKey.Control, control.State.Value, control.State.Version);
        put.Item["admissionsEnabled"] = new() { BOOL = control.AdmissionsEnabled };
        put.Item["uploadsEnabled"] = new() { BOOL = control.UploadsEnabled };
        put.ConditionExpression += " AND #ae = :ae AND #ue = :ue";
        put.ExpressionAttributeNames["#ae"] = "admissionsEnabled"; put.ExpressionAttributeNames["#ue"] = "uploadsEnabled";
        put.ExpressionAttributeValues[":ae"] = new() { BOOL = control.AdmissionsEnabled };
        put.ExpressionAttributeValues[":ue"] = new() { BOOL = control.UploadsEnabled };
        return put;
    }
    public async Task<bool> CommitAsync(IEnumerable<Put> writes, CancellationToken ct, Control? unchangedControl = null)
    {
        var items = writes.ToArray();
        if (items.Length < 1 || items.Length + (unchangedControl is null ? 0 : 1) > 100
            || items.Select(p => (p.Item["pk"].S, p.Item["sk"].S)).Distinct().Count() != items.Length
            || unchangedControl is not null && items.Any(p => p.Item["pk"].S == "CONTROL"))
            throw new InvalidOperationException("Invalid issuer transaction boundary.");
        try
        {
            var actions = items.Select(p => new TransactWriteItem { Put = p }).ToList();
            if (unchangedControl is not null) {
                // Fence maintenance/kill-switch changes atomically without a global
                // write for every heartbeat. Independent sessions only write their rows.
                var expected = WriteControl(unchangedControl);
                actions.Add(new() { ConditionCheck = new() { TableName = table, Key = Key(DbKey.Control),
                    ConditionExpression = expected.ConditionExpression, ExpressionAttributeNames = expected.ExpressionAttributeNames,
                    ExpressionAttributeValues = expected.ExpressionAttributeValues } });
            }
            await client.TransactWriteItemsAsync(new TransactWriteItemsRequest { ClientRequestToken = Guid.NewGuid().ToString("N"), TransactItems = actions }, ct);
            return true;
        }
        catch (TransactionCanceledException e) when (e.CancellationReasons?.Any(r => r.Code == "ConditionalCheckFailed" || r.Code == "TransactionConflict") == true) { return false; }
        catch (TransactionConflictException) { return false; }
        catch (AmazonDynamoDBException e) when ((int)e.StatusCode >= 500) { throw new UnknownCommitException(); }
        catch (HttpRequestException) { throw new UnknownCommitException(); }
    }
}
