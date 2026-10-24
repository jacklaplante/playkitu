using System.Globalization;
using System.Text.Json;

namespace Playkitu.Issuer;

public sealed class ValveVerifier(HttpClient http, PublisherKeyProvider keys, int steamAppId) : IValveVerifier
{
    public async Task<string> VerifyAsync(string ticket, string identity, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(TimeSpan.FromSeconds(6));
        try
        {
            var key = await keys.GetAsync(deadline.Token);
            using var authentication = await GetAsync("ISteamUserAuth/AuthenticateUserTicket/v1/", key,
                $"appid={steamAppId}&ticket={Uri.EscapeDataString(ticket)}&identity={Uri.EscapeDataString(identity)}", deadline.Token);
            // Preserve the authentication checks and their original evaluation order:
            // malformed containers/non-string result still become steam_unavailable (503).
            // These optional admission diagnostics distinguish the existing 401 branches;
            // they neither authenticate a ticket nor retain upstream text or identifiers.
            if (!authentication.RootElement.TryGetProperty("response", out var response))
                throw new IssuerError(401, "fresh_ticket_required", 1, SteamAuthFailure.MissingResponse);
            if (!response.TryGetProperty("params", out var parameters))
            {
                // Inspect error only when params is absent: valid params retain precedence
                // even if Valve also includes an error property. Description observation
                // returns only fixed whole-word flags, never the upstream text itself.
                bool hasError = response.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object;
                int? valveCode = hasError && error.TryGetProperty("errorcode", out var number)
                    && number.ValueKind == JsonValueKind.Number && number.TryGetInt32(out var value) && value is >= 0 and <= 65535 ? value : null;
                throw new IssuerError(401, "fresh_ticket_required", 1,
                    hasError ? SteamAuthFailure.ValveError : SteamAuthFailure.MissingParams, valveCode,
                    hasError ? ObserveErrorTerms(error) : null,
                    // Shape is a heuristic on the already-held publisher key, not a
                    // validity check. Never trim, unwrap, log or change the sent value.
                    hasError ? key.Length == 32 && key.All(char.IsAsciiHexDigit)
                        ? PublisherKeyShape.ExpectedShape : PublisherKeyShape.UnexpectedShape : null);
            }
            if (!parameters.TryGetProperty("result", out var result) || result.GetString() != "OK")
                throw new IssuerError(401, "fresh_ticket_required", 1, SteamAuthFailure.ResultNotOk);
            if (!parameters.TryGetProperty("steamid", out var steam) || steam.ValueKind != JsonValueKind.String)
                throw new IssuerError(401, "fresh_ticket_required", 1, SteamAuthFailure.MissingSteamId);
            var steamId = steam.GetString()!;
            if (steamId.Length != 17 || !ulong.TryParse(steamId, NumberStyles.None, CultureInfo.InvariantCulture, out _)) throw new IssuerError(401, "invalid_steam_ticket");
            using var ownership = await GetAsync("ISteamUser/CheckAppOwnership/v4/", key, $"appid={steamAppId}&steamid={steamId}", deadline.Token);
            // Valve explicitly defines ownsapp as an active license (including expiry).
            // Do not invent a purchaser/permanent-license requirement or infer a license
            // from client fullGame/build/SteamID fields. timeexpires is informational;
            // eligibility follows Valve's current active-license verdict, not client time.
            if (!ownership.RootElement.TryGetProperty("appownership", out var owned) || !owned.TryGetProperty("ownsapp", out var owns)
                || owns.ValueKind != JsonValueKind.True || (owned.TryGetProperty("usercanceled", out var canceled) && canceled.ValueKind == JsonValueKind.True))
                throw new IssuerError(403, "playtest_license_required");
            return steamId;
        }
        catch (HttpRequestException) { throw new IssuerError(503, "steam_unavailable", 2); }
        catch (JsonException) { throw new IssuerError(503, "steam_unavailable", 2); }
        catch (InvalidOperationException) { throw new IssuerError(503, "steam_unavailable", 2); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new IssuerError(503, "steam_unavailable", 2); }
    }
    private static ValveErrorTerms? ObserveErrorTerms(JsonElement error)
    {
        if (!error.TryGetProperty("errordesc", out var field) || field.ValueKind != JsonValueKind.String) return null;
        string description = field.GetString()!;
        if (description.Length > 512) return null;
        // Empty/unmatched inspected descriptions return None (wire []); absent,
        // wrong-type or oversized descriptions return null (wire field omitted).
        // ASCII digits/underscore belong to tokens, so identity123, appid_hash and
        // ticketing cannot manufacture a match inside a longer token.
        var observed = ValveErrorTerms.None;
        for (int start = 0, end = 0; end <= description.Length; end++)
        {
            if (end < description.Length && (description[end] is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '_')) continue;
            if (end > start)
                foreach (var term in SteamRejectionDiagnostics.Terms)
                    if (description.AsSpan(start, end - start).Equals(term.Name.AsSpan(), StringComparison.OrdinalIgnoreCase)) observed |= term.Term;
            start = end + 1;
        }
        return observed;
    }
    private async Task<JsonDocument> GetAsync(string path, string key, string query, CancellationToken ct)
    {
        // Fixed origin, verified HTTPS and a header for the publisher key. Never log URI,
        // exception details, response bodies, tickets or keys: Valve's GET ticket is secret.
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://partner.steam-api.com/" + path + "?" + query);
        request.Headers.Add("x-webapi-key", key);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if ((int)response.StatusCode != 200 || response.Content.Headers.ContentLength > 32768) throw new IssuerError(503, "steam_unavailable", 2);
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream(); var chunk = new byte[4096]; int read;
        while ((read = await stream.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > 32768) throw new IssuerError(503, "steam_unavailable", 2);
            buffer.Write(chunk, 0, read);
        }
        return JsonDocument.Parse(buffer.ToArray(), new JsonDocumentOptions { AllowDuplicateProperties = false, MaxDepth = 16 });
    }
}
