using static Bast.Domain.PublicationState;

namespace Bast.Domain;

/// <summary>
/// Transições permitidas entre estados de publicação.
/// Regra central: UNKNOWN nunca volta direto para UPLOADING; só sai por reconciliação
/// (PUBLISHED, PROCESSING, RETRY_WAIT ou FAILED), o que impede publicação duplicada.
/// </summary>
public static class PublicationStateMachine
{
    private static readonly Dictionary<PublicationState, PublicationState[]> Allowed = new()
    {
        [Pending] = [Validating, Cancelled],
        [Validating] = [Scheduled, Failed, Cancelled],
        [Scheduled] = [Uploading, Validating, Cancelled],
        [Uploading] = [Processing, Published, RetryWait, Failed, Unknown],
        [Processing] = [Published, RetryWait, Failed, Unknown],
        [RetryWait] = [Uploading, Failed, Cancelled],
        [Unknown] = [Published, Processing, RetryWait, Failed],
        [Failed] = [Scheduled, Cancelled],
        [Cancelled] = [Scheduled],
        [Published] = [],
    };

    public static bool CanTransition(PublicationState from, PublicationState to) =>
        Allowed[from].Contains(to);

    public static bool IsTerminal(PublicationState state) =>
        state is Published or Failed or Cancelled;

    public static void EnsureCanTransition(PublicationState from, PublicationState to)
    {
        if (!CanTransition(from, to))
        {
            throw new InvalidOperationException($"Transição de publicação inválida: {from} -> {to}.");
        }
    }
}
