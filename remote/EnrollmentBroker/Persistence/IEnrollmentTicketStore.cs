using StayActive.EnrollmentBroker.Domain;

namespace StayActive.EnrollmentBroker.Persistence;

public interface IEnrollmentTicketStore : IDisposable
{
    Task<EnrollmentTicket?> GetAsync(Guid ticketId, CancellationToken cancellationToken);

    Task CreateAsync(
        EnrollmentTicket ticket,
        string actorSubject,
        string correlationId,
        CancellationToken cancellationToken);

    Task<EnrollmentTicketTransitionResult> TransitionAsync(
        Guid ticketId,
        EnrollmentTicketStatus status,
        string actorSubject,
        string correlationId,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<EnrollmentTicketAuditEvent>> ReadAuditAsync(int take, CancellationToken cancellationToken);
}

public sealed record EnrollmentTicketTransitionResult(
    bool Found,
    bool Applied,
    EnrollmentTicket? Ticket)
{
    public static EnrollmentTicketTransitionResult Missing { get; } = new(false, false, null);
}
