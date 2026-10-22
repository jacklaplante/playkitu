using System;
using System.Collections;
using System.Threading;
using Playreel.Client;

namespace Playreel.Steam
{
    // Retain the host's ticket owner through the issuer consumer's completion.
    // This adapter never initializes Steam, reads an account ID or destroys Steam.
    public sealed class SteamRecordingTickets : IRecordingTickets
    {
        private readonly SteamWebTicketClient owner;
        public SteamRecordingTickets(SteamWebTicketClient owner) { this.owner = owner ?? throw new ArgumentNullException(nameof(owner)); }
        public IEnumerator Begin(string identity, Func<string, IEnumerator> consumer, Action<string> failure, CancellationToken cancellation)
            => new SteamWebTicketFlow(owner.Request(identity, 5, cancellation), consumer, status => failure(status switch {
                SteamTicketStatus.InvalidRequest => "ticket_invalid_request", SteamTicketStatus.InvalidHandle => "ticket_invalid_handle",
                SteamTicketStatus.Rejected => "ticket_rejected", SteamTicketStatus.InvalidResponse => "ticket_invalid_response",
                SteamTicketStatus.TimedOut => "ticket_timed_out", SteamTicketStatus.Cancelled => "ticket_cancelled",
                SteamTicketStatus.Disposed => "ticket_disposed", SteamTicketStatus.TooManyRequests => "ticket_too_many_requests",
                SteamTicketStatus.ConsumerFailed => "admission_consumer_failed", _ => "ticket_unavailable"
            }));
    }
}
