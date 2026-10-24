namespace Playkitu.Issuer;

// Count caps are owner choices, never inferred from a client. Null disables a
// count cap; bounded rate/retry/request limits still apply. Pooled compatibility
// continues using its historical limits until the explicit state migration.
public sealed class RecordingLimits
{
    public int? ConcurrentSessions { get; init; }
    public int? AdmissionsPerDay { get; init; }
    public int? AdmissionsPerMonth { get; init; }
    public int? TesterAdmissionsPerDay { get; init; }
    public int AdmissionAttemptsPerMinute { get; init; } = 120;
    public int SessionRequestsPerMinute { get; init; } = 30;
    public void Validate()
    {
        foreach (var value in new[] { ConcurrentSessions, AdmissionsPerDay, AdmissionsPerMonth, TesterAdmissionsPerDay })
            if (value is <= 0 or > 100000000) throw new InvalidOperationException("Count limits must be positive or null.");
        if (AdmissionAttemptsPerMinute is < 1 or > 3000 || SessionRequestsPerMinute is < 1 or > 120)
            throw new InvalidOperationException("Invalid bounded request rate.");
    }
}
