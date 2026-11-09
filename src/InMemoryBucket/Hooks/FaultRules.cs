using InMemoryBucket.Core;

namespace InMemoryBucket.Hooks;

/// <summary>What a matching rule does to the request.</summary>
public abstract record FaultAction
{
    /// <summary>Answer with a real S3 error response; the SDK raises its usual exception for the code.</summary>
    public sealed record Error(S3ErrorCode Code, string? Message = null) : FaultAction;

    /// <summary>Wait before handling the request normally; later rules still apply.</summary>
    public sealed record Delay(TimeSpan Duration) : FaultAction;

    /// <summary>Throw from the HTTP handler, which the client sees as a transport failure, not an S3 error.</summary>
    public sealed record Throw(Exception Exception) : FaultAction;
}

/// <summary>
/// A predicate over the incoming request plus an action. <see cref="Remaining"/> counts down for rules that fire a limited
/// number of times; null means forever.
/// </summary>
public sealed class FaultRule
{
    private int _remaining;

    /// <summary>Creates a rule; add it with <see cref="FaultRules.Add"/>.</summary>
    /// <param name="predicate">Which requests the rule applies to.</param>
    /// <param name="action">What happens to a matching request.</param>
    /// <param name="times">How many matching requests the rule fires for; null means every one.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="times"/> is less than 1.</exception>
    public FaultRule(Func<RecordedRequest, bool> predicate, FaultAction action, int? times = null)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        ArgumentNullException.ThrowIfNull(action);
        if (times is < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(times), "A rule fires at least once.");
        }

        Predicate = predicate;
        Action = action;
        _remaining = times ?? -1;
    }

    /// <summary>Which requests the rule applies to.</summary>
    public Func<RecordedRequest, bool> Predicate { get; }

    /// <summary>What happens to a matching request.</summary>
    public FaultAction Action { get; }

    /// <summary>How many more times the rule fires; 0 when used up, null for a rule without a limit.</summary>
    public int? Remaining => _remaining < 0 ? null : _remaining;

    /// <summary>True when the rule matches and still has a firing left; consumes one firing for limited rules.</summary>
    internal bool TryFire(RecordedRequest request)
    {
        if (_remaining == 0 || !Predicate(request))
        {
            return false;
        }

        if (_remaining > 0)
        {
            var left = Interlocked.Decrement(ref _remaining);
            if (left < 0)
            {
                Interlocked.Exchange(ref _remaining, 0);
                return false;
            }
        }

        return true;
    }
}

/// <summary>
/// Fault injection. Rules are checked in the order they were added, before the operation touches the store.
/// A Delay rule waits and lets evaluation continue; the first Error or Throw rule ends it.
/// </summary>
public sealed class FaultRules
{
    private readonly object _gate = new();
    private List<FaultRule> _rules = [];

    /// <summary>A snapshot of the rules, in the order they are checked.</summary>
    public IReadOnlyList<FaultRule> Rules => _rules.AsReadOnly();

    /// <summary>Adds a rule after the existing ones.</summary>
    /// <param name="rule">The rule.</param>
    /// <returns>The same rule, to pass to <see cref="Remove"/> later.</returns>
    public FaultRule Add(FaultRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);
        lock (_gate)
        {
            _rules = [.. _rules, rule];
        }

        return rule;
    }

    /// <summary>Fail the first matching request once with an S3 error, then step aside.</summary>
    public FaultRule Once(Func<RecordedRequest, bool> predicate, S3ErrorCode code, string? message = null)
        => Add(new FaultRule(predicate, new FaultAction.Error(code, message), times: 1));

    /// <summary>Fail the first <paramref name="count"/> matching requests with an S3 error.</summary>
    public FaultRule Times(int count, Func<RecordedRequest, bool> predicate, S3ErrorCode code, string? message = null)
        => Add(new FaultRule(predicate, new FaultAction.Error(code, message), times: count));

    /// <summary>Fail every matching request with an S3 error until <see cref="Remove"/> or <see cref="Clear"/>.</summary>
    public FaultRule Always(Func<RecordedRequest, bool> predicate, S3ErrorCode code, string? message = null)
        => Add(new FaultRule(predicate, new FaultAction.Error(code, message)));

    /// <summary>Delay matching requests, every one or the first <paramref name="times"/>. The wait honours cancellation and <see cref="InMemoryBucketOptions.TimeProvider"/>.</summary>
    public FaultRule Delay(Func<RecordedRequest, bool> predicate, TimeSpan duration, int? times = null)
        => Add(new FaultRule(predicate, new FaultAction.Delay(duration), times));

    /// <summary>Throw <paramref name="exception"/> from the handler for matching requests, every one or the first <paramref name="times"/>: a connection reset, a timeout, anything the SDK treats as a transport failure.</summary>
    public FaultRule Throw(Func<RecordedRequest, bool> predicate, Exception exception, int? times = null)
        => Add(new FaultRule(predicate, new FaultAction.Throw(exception), times));

    /// <summary>Removes a rule.</summary>
    /// <param name="rule">A rule returned by one of the add methods.</param>
    /// <returns>True when the rule was present.</returns>
    public bool Remove(FaultRule rule)
    {
        lock (_gate)
        {
            if (!_rules.Contains(rule))
            {
                return false;
            }

            _rules = _rules.Where(r => !ReferenceEquals(r, rule)).ToList();
            return true;
        }
    }

    /// <summary>Removes every rule. <see cref="InMemoryBucketServer.Reset"/> does this too.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _rules = [];
        }
    }

    /// <summary>Applies matching rules: awaits delays, returns the terminal action if any.</summary>
    internal async ValueTask<FaultAction?> ApplyAsync(RecordedRequest request, TimeProvider clock, CancellationToken cancellationToken)
    {
        var rules = _rules;
        if (rules.Count == 0)
        {
            return null;
        }

        foreach (var rule in rules)
        {
            if (!rule.TryFire(request))
            {
                continue;
            }

            if (rule.Action is FaultAction.Delay delay)
            {
                await Task.Delay(delay.Duration, clock, cancellationToken).ConfigureAwait(false);
                continue;
            }

            return rule.Action;
        }

        return null;
    }
}
