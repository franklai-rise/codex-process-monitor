namespace Codex.ProcessMonitor.Core;

/// <summary>Read-only view of one rule/target state, useful for a status panel or report.</summary>
public sealed record AlertStateSnapshot
{
    public string RuleId { get; init; } = string.Empty;
    public string TargetKey { get; init; } = string.Empty;
    public AlertLifecycleState State { get; init; }
    public DateTimeOffset? ViolationStartedAtUtc { get; init; }
    public DateTimeOffset? ClearStartedAtUtc { get; init; }
    public DateTimeOffset? CooldownUntilUtc { get; init; }
    public DateTimeOffset? LastObservedAtUtc { get; init; }
    public double LastValue { get; init; }
}

/// <summary>
/// Stateful threshold evaluator. A raise is emitted only after the condition has
/// remained true for the rule duration; clear and cooldown use the same monotonic
/// observation timestamps.
/// </summary>
public sealed class AlertEngine : IAlertEngine
{
    private readonly AlertRule[] _rules;
    private readonly Dictionary<string, RuleState> _states = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    public AlertEngine(IEnumerable<AlertRule> rules)
    {
        ArgumentNullException.ThrowIfNull(rules);
        _rules = rules.Where(rule => rule is not null).ToArray();
        if (_rules.Any(rule => string.IsNullOrWhiteSpace(rule.Id)))
        {
            throw new ArgumentException("Every alert rule must have a non-empty Id.", nameof(rules));
        }

        if (_rules.Select(rule => rule.Id).Distinct(StringComparer.Ordinal).Count() != _rules.Length)
        {
            throw new ArgumentException("Alert rule IDs must be unique.", nameof(rules));
        }
    }

    public IReadOnlyList<AlertRule> Rules => Array.AsReadOnly(_rules);

    public IReadOnlyList<AlertEvent> Evaluate(
        DateTimeOffset timestampUtc,
        IEnumerable<AlertObservation> observations)
    {
        ArgumentNullException.ThrowIfNull(observations);
        var events = new List<AlertEvent>();
        lock (_gate)
        {
            // A duplicate observation in one batch must not create duplicate transitions.
            var evaluatedKeys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var observation in observations)
            {
                ArgumentNullException.ThrowIfNull(observation);
                foreach (var rule in _rules)
                {
                    if (!rule.Enabled || rule.Metric != observation.Metric || !Matches(rule, observation))
                    {
                        continue;
                    }

                    var deduplicationKey = GetDeduplicationKey(rule, observation);
                    var stateKey = $"{rule.Id}\u001f{deduplicationKey}";
                    if (!evaluatedKeys.Add(stateKey))
                    {
                        continue;
                    }

                    var state = GetOrCreateState(rule, deduplicationKey);
                    if (state.LastObservedAtUtc is { } last && timestampUtc < last)
                    {
                        // Counters and durations are monotonic; an out-of-order sample cannot move them back.
                        continue;
                    }

                    state.LastObservedAtUtc = timestampUtc;
                    state.LastValue = observation.Value;
                    EvaluateOne(rule, observation, timestampUtc, state, events, deduplicationKey);
                }
            }
        }

        return events.AsReadOnly();
    }

    public IReadOnlyList<AlertEvent> Evaluate(
        DateTimeOffset timestampUtc,
        string targetKey,
        AlertMetric metric,
        double value,
        string? displayName = null,
        int? processId = null,
        string? processName = null,
        ProcessRole? role = null,
        string? deduplicationKey = null)
    {
        return Evaluate(
            timestampUtc,
            new[]
            {
                new AlertObservation(
                    targetKey,
                    metric,
                    value,
                    displayName,
                    processId,
                    processName,
                    role,
                    deduplicationKey),
            });
    }

    public IReadOnlyList<AlertEvent> Evaluate(
        DateTimeOffset timestampUtc,
        AlertObservation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);
        return Evaluate(timestampUtc, new[] { observation });
    }

    public IReadOnlyList<AlertStateSnapshot> GetStates()
    {
        lock (_gate)
        {
            return _states.Values
                .Select(state => state.ToSnapshot())
                .OrderBy(state => state.RuleId, StringComparer.Ordinal)
                .ThenBy(state => state.TargetKey, StringComparer.Ordinal)
                .ToArray();
        }
    }

    public AlertStateSnapshot? GetState(string ruleId, string targetKey)
    {
        ArgumentNullException.ThrowIfNull(ruleId);
        ArgumentNullException.ThrowIfNull(targetKey);
        lock (_gate)
        {
            return _states.TryGetValue($"{ruleId}\u001f{targetKey}", out var state)
                ? state.ToSnapshot()
                : null;
        }
    }

    public void Reset()
    {
        lock (_gate)
        {
            _states.Clear();
        }
    }

    private static void EvaluateOne(
        AlertRule rule,
        AlertObservation observation,
        DateTimeOffset timestampUtc,
        RuleState state,
        ICollection<AlertEvent> events,
        string deduplicationKey)
    {
        if (state.IsActive)
        {
            if (!IsClearConditionSatisfied(rule, observation.Value))
            {
                state.ClearStartedAtUtc = null;
                return;
            }

            state.ClearStartedAtUtc ??= timestampUtc;
            if (timestampUtc - state.ClearStartedAtUtc.Value < rule.ClearDuration)
            {
                return;
            }

            state.IsActive = false;
            state.ClearStartedAtUtc = null;
            state.ViolationStartedAtUtc = null;
            state.CooldownUntilUtc = rule.Cooldown > TimeSpan.Zero
                ? timestampUtc + rule.Cooldown
                : null;
            events.Add(CreateEvent(rule, observation, AlertEventKind.Cleared, timestampUtc, deduplicationKey));
            return;
        }

        if (state.CooldownUntilUtc is { } cooldownUntil)
        {
            if (timestampUtc < cooldownUntil)
            {
                // Keep accumulating a continuous violation while notifications are cooling down.
                if (!IsTriggerConditionSatisfied(rule, observation.Value))
                {
                    state.ViolationStartedAtUtc = null;
                }
                else
                {
                    state.ViolationStartedAtUtc ??= timestampUtc;
                }

                return;
            }

            state.CooldownUntilUtc = null;
        }

        if (!IsTriggerConditionSatisfied(rule, observation.Value))
        {
            state.ViolationStartedAtUtc = null;
            return;
        }

        state.ViolationStartedAtUtc ??= timestampUtc;
        if (timestampUtc - state.ViolationStartedAtUtc.Value < rule.Duration)
        {
            return;
        }

        state.IsActive = true;
        state.ClearStartedAtUtc = null;
        state.ViolationStartedAtUtc = null;
        events.Add(CreateEvent(rule, observation, AlertEventKind.Raised, timestampUtc, deduplicationKey));
    }

    private RuleState GetOrCreateState(AlertRule rule, string deduplicationKey)
    {
        var stateKey = $"{rule.Id}\u001f{deduplicationKey}";
        if (_states.TryGetValue(stateKey, out var state))
        {
            return state;
        }

        state = new RuleState(rule.Id, deduplicationKey);
        _states.Add(stateKey, state);
        return state;
    }

    private static string GetDeduplicationKey(AlertRule rule, AlertObservation observation)
    {
        return rule.DeduplicationKey ?? observation.DeduplicationKey ?? observation.TargetKey;
    }

    private static bool Matches(AlertRule rule, AlertObservation observation)
    {
        if (rule.TargetKey is not null &&
            !rule.TargetKey.Equals(observation.TargetKey, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (rule.TargetProcessId is { } processId && observation.ProcessId != processId)
        {
            return false;
        }

        if (rule.TargetProcessName is not null &&
            !rule.TargetProcessName.Equals(observation.ProcessName, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return rule.TargetRole is null || rule.TargetRole == observation.Role;
    }

    private static bool IsTriggerConditionSatisfied(AlertRule rule, double value)
    {
        return Compare(value, rule.Threshold, rule.Comparison);
    }

    private static bool IsClearConditionSatisfied(AlertRule rule, double value)
    {
        var threshold = rule.EffectiveClearThreshold;
        return rule.Comparison switch
        {
            AlertComparison.GreaterThan or AlertComparison.GreaterThanOrEqual => value <= threshold,
            AlertComparison.LessThan or AlertComparison.LessThanOrEqual => value >= threshold,
            AlertComparison.Equal => value != threshold,
            AlertComparison.NotEqual => value == threshold,
            _ => false,
        };
    }

    private static bool Compare(double value, double threshold, AlertComparison comparison)
    {
        return comparison switch
        {
            AlertComparison.GreaterThan => value > threshold,
            AlertComparison.GreaterThanOrEqual => value >= threshold,
            AlertComparison.LessThan => value < threshold,
            AlertComparison.LessThanOrEqual => value <= threshold,
            AlertComparison.Equal => value == threshold,
            AlertComparison.NotEqual => value != threshold,
            _ => false,
        };
    }

    private static AlertEvent CreateEvent(
        AlertRule rule,
        AlertObservation observation,
        AlertEventKind kind,
        DateTimeOffset timestampUtc,
        string deduplicationKey)
    {
        var stateText = kind == AlertEventKind.Raised ? "raised" : "cleared";
        var displayName = observation.DisplayName ?? observation.TargetKey;
        var message = rule.Message ?? $"Alert '{rule.Name}' {stateText} for {displayName}.";
        return new AlertEvent(
            rule.Id,
            observation.TargetKey,
            kind,
            rule.Severity,
            timestampUtc,
            observation.Value,
            message,
            deduplicationKey);
    }

    private sealed class RuleState
    {
        public RuleState(string ruleId, string targetKey)
        {
            RuleId = ruleId;
            TargetKey = targetKey;
        }

        public string RuleId { get; }
        public string TargetKey { get; }
        public bool IsActive { get; set; }
        public DateTimeOffset? ViolationStartedAtUtc { get; set; }
        public DateTimeOffset? ClearStartedAtUtc { get; set; }
        public DateTimeOffset? CooldownUntilUtc { get; set; }
        public DateTimeOffset? LastObservedAtUtc { get; set; }
        public double LastValue { get; set; }

        public AlertStateSnapshot ToSnapshot()
        {
            var state = CooldownUntilUtc is not null
                ? AlertLifecycleState.CoolingDown
                : IsActive
                    ? AlertLifecycleState.Active
                    : ViolationStartedAtUtc is not null
                        ? AlertLifecycleState.Pending
                        : AlertLifecycleState.Inactive;
            return new AlertStateSnapshot
            {
                RuleId = RuleId,
                TargetKey = TargetKey,
                State = state,
                ViolationStartedAtUtc = ViolationStartedAtUtc,
                ClearStartedAtUtc = ClearStartedAtUtc,
                CooldownUntilUtc = CooldownUntilUtc,
                LastObservedAtUtc = LastObservedAtUtc,
                LastValue = LastValue,
            };
        }
    }
}
