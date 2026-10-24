namespace Playkitu.Issuer;

public sealed class GlobalState
{
    public Dictionary<Guid, PendingReference> Pending { get; set; } = [];
    public Dictionary<Guid, ActiveReference> Active { get; set; } = [];
    public StreamSlot[] Slots { get; set; } = Enumerable.Range(0, Deployment.DefaultVideoStreams).Select(_ => new StreamSlot()).ToArray();
    public string Day { get; set; } = "";
    public string Month { get; set; } = "";
    public int DailyAdmissions { get; set; }
    public int MonthlyAdmissions { get; set; }
    public Dictionary<string, int> TesterDailyAdmissions { get; set; } = [];
    public long AdmissionWindow { get; set; }
    public long AuthorizeWindow { get; set; }
    public int AdmissionAttempts { get; set; }
    public int AuthorizeRequests { get; set; }

    // Application deadlines reclaim scarce admission/stream capacity even if DynamoDB TTL
    // is delayed. The bounded maps mean this cleanup never scans a growing session table.
    public void Reclaim(DateTimeOffset now)
    {
        foreach (var id in Pending.Where(p => p.Value.Until <= now).Select(p => p.Key).ToArray()) Pending.Remove(id);
        // Four-hour media expiry is immutable. Only opted-in diagnostics retain
        // the bounded active/slot binding for final metadata; close and inactivity
        // still reclaim early, with the original credential quarantine intact.
        foreach (var id in Active.Where(p => (p.Value.MetadataUntilUtc ?? p.Value.EndsAt) <= now || p.Value.LastSeen.AddSeconds(IssuerOptions.IdleSeconds) <= now).Select(p => p.Key).ToArray()) End(id, now);
        foreach (var slot in Slots.Where(s => s.SessionId is null && s.QuarantineUntil <= now))
        {
            slot.Generation = ""; slot.MaxCredentialExpiry = null; slot.QuarantineUntil = DateTimeOffset.MinValue;
        }
        var day = now.UtcDateTime.ToString("yyyy-MM-dd"); var month = day[..7];
        if (Day != day) { Day = day; DailyAdmissions = 0; TesterDailyAdmissions.Clear(); }
        if (Month != month) { Month = month; MonthlyAdmissions = 0; }
    }
    public void End(Guid id, DateTimeOffset now)
    {
        Active.Remove(id);
        foreach (var slot in Slots.Where(s => s.SessionId == id))
        {
            slot.SessionId = null;
            slot.QuarantineUntil = slot.MaxCredentialExpiry is { } e ? e.AddMinutes(50) : now;
        }
    }
}
public sealed record PendingReference(int Generation, DateTimeOffset Until);
public sealed class ActiveReference
{
    public string TesterHash { get; set; } = "";
    public DateTimeOffset LastSeen { get; set; }
    public DateTimeOffset EndsAt { get; set; }
    public DateTimeOffset? MetadataUntilUtc { get; set; }
    public long RequestWindow { get; set; }
    public int Requests { get; set; }
}
public sealed class StreamSlot
{
    public Guid? SessionId { get; set; }
    public string Generation { get; set; } = "";
    public DateTimeOffset QuarantineUntil { get; set; } = DateTimeOffset.MinValue;
    public DateTimeOffset? MaxCredentialExpiry { get; set; }
}
public sealed class SessionState
{
    public Guid SessionId { get; set; }
    public string AdmissionId { get; set; } = "";
    public string Status { get; set; } = "pending";
    public string ProofHash { get; set; } = "";
    public string BindingHash { get; set; } = "";
    public string TicketHash { get; set; } = "";
    public BuildDeclaration Build { get; set; } = new("", null);
    public ConsentDeclaration Consent { get; set; } = new(1, true, false, DateTimeOffset.MinValue);
    public int Generation { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ReservationUntil { get; set; }
    public DateTimeOffset AdmissionDeadline { get; set; }
    public DateTimeOffset? AdmittedAt { get; set; }
    public DateTimeOffset? EndsAt { get; set; }
    public int MetadataFinalizationSeconds { get; set; }
    [System.Text.Json.Serialization.JsonIgnore]
    public DateTimeOffset? MetadataUntilUtc => MetadataFinalizationSeconds == Playkitu.Client.RecordingDiagnosticsLimits.MetadataFinalizationSeconds
        ? EndsAt?.AddSeconds(MetadataFinalizationSeconds) : null;
    [System.Text.Json.Serialization.JsonIgnore]
    public long DiagnosticByteLimit => MetadataFinalizationSeconds == 0 ? IssuerOptions.MaxBytes : Playkitu.Client.RecordingDiagnosticsLimits.StoredBytesPerSession;
    [System.Text.Json.Serialization.JsonIgnore]
    public int DiagnosticObjectLimit => MetadataFinalizationSeconds == 0 ? 4096 : Playkitu.Client.RecordingDiagnosticsLimits.ObjectsPerSession;
    public DateTimeOffset? LastMediaIssued { get; set; }
    public DateTimeOffset? LastMediaAttempt { get; set; }
    public long PurgeAt { get; set; }
    public long BytesReserved { get; set; }
    public int ObjectsReserved { get; set; }
    public int Slot { get; set; } = -1;
    public string SlotGeneration { get; set; } = "";
    public string Prefix => $"sessions/{AdmittedAt:yyyy-MM-dd}/{SessionId:N}/";
}
public sealed record DbKey(string Pk, string Sk = "META")
{
    public static readonly DbKey Control = new("CONTROL");
    public static DbKey Session(Guid id) => new($"SESSION#{id:N}");
    public static DbKey Object(Guid id, ObjectDescriptor item) => new($"SESSION#{id:N}", $"OBJECT#{item.Kind}#{item.Sequence:D6}");
}
public sealed record Stored<T>(T Value, long Version) where T : class;
public sealed record Control(Stored<GlobalState> State, bool AdmissionsEnabled, bool UploadsEnabled);
public sealed record StoredObject(ObjectDescriptor Descriptor, string Key, long PurgeAt, string AdmissionId);
