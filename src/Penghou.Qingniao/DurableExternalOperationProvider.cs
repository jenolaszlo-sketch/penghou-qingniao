namespace Penghou.Qingniao;

/// <summary>Recovery classification for one provider start attempt.</summary>
public enum ExternalOperationRecoveryState
{
    /// <summary>A durable start intent exists, but no provider handle was captured.</summary>
    StartIntent = 0,
    /// <summary>A previous start may have happened, but no handle was durably captured.</summary>
    UnknownAfterRecovery = 1,
    /// <summary>A handle is known and the provider can safely observe it after recovery.</summary>
    KnownObservable = 2,
    /// <summary>A handle is known but the provider cannot safely reconnect after recovery.</summary>
    KnownNotReconnectable = 3,
    /// <summary>A terminal provider result was durably recorded.</summary>
    Completed = 4,
}

/// <summary>Immutable, bounded intent to start one exact external operation.</summary>
public sealed record ExternalOperationRecoveryIntent
{
    /// <summary>Creates a durable start intent.</summary>
    public ExternalOperationRecoveryIntent(ExternalOperationStartRequest request, DateTimeOffset createdAt)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Identity.Validate();
        request.Correlation.Validate();
        if (createdAt == default)
        {
            throw new ArgumentException("A recovery intent must have a timestamp.", nameof(createdAt));
        }

        Request = request;
        CreatedAt = createdAt;
    }

    /// <summary>The exact provider start request, including the semantic fingerprint inputs.</summary>
    public ExternalOperationStartRequest Request { get; }

    /// <summary>The time the intent was durably created.</summary>
    public DateTimeOffset CreatedAt { get; }
}

/// <summary>Immutable journal record for one external operation attempt.</summary>
public sealed record ExternalOperationRecoveryRecord
{
    /// <summary>Creates a recovery record snapshot.</summary>
    public ExternalOperationRecoveryRecord(
        ExternalOperationRecoveryIntent intent,
        ExternalOperationHandleCapture? capture = null,
        ExternalOperationObservation? observation = null,
        ExternalOperationResult? result = null,
        ExternalOperationRecoveryState recoveryState = ExternalOperationRecoveryState.StartIntent)
    {
        ArgumentNullException.ThrowIfNull(intent);
        if (!Enum.IsDefined(recoveryState))
        {
            throw new ArgumentOutOfRangeException(nameof(recoveryState));
        }

        if (capture is not null)
        {
            DurableOperationIdentityValidation.RequireMatchingIdentity(intent.Request.Identity, capture.Handle.Correlation);
            if (!DurableOperationIdentityValidation.SameCorrelation(intent.Request.Correlation, capture.Handle.Correlation))
            {
                throw new ArgumentException("A recovery capture must match the original start correlation.", nameof(capture));
            }
        }

        if (observation is not null && (capture is null || !SameHandle(capture.Handle, observation.Handle)))
        {
            throw new ArgumentException("A recovery observation must belong to the captured provider handle.", nameof(observation));
        }

        if (result is not null && (capture is null || !SameHandle(capture.Handle, result.Handle)))
        {
            throw new ArgumentException("A recovery result must belong to the captured provider handle.", nameof(result));
        }

        Intent = intent;
        Capture = capture;
        Observation = observation;
        Result = result;
        RecoveryState = recoveryState;
    }

    /// <summary>The original start intent.</summary>
    public ExternalOperationRecoveryIntent Intent { get; }

    /// <summary>The provider handle captured before forwarding its callback, if any.</summary>
    public ExternalOperationHandleCapture? Capture { get; }

    /// <summary>The latest durable provider observation, if any.</summary>
    public ExternalOperationObservation? Observation { get; }

    /// <summary>The durable terminal result, if any.</summary>
    public ExternalOperationResult? Result { get; }

    /// <summary>The current recovery classification.</summary>
    public ExternalOperationRecoveryState RecoveryState { get; }

    private static bool SameHandle(ExternalOperationHandle left, ExternalOperationHandle right) => left == right;
}

/// <summary>Host-owned journal for durable external-operation recovery records.</summary>
public interface IExternalOperationRecoveryJournal
{
    /// <summary>Atomically creates the exact start intent, or returns false if its attempt key already exists.</summary>
    ValueTask<bool> TryCreateIntentAsync(ExternalOperationRecoveryIntent intent, CancellationToken cancellationToken = default);

    /// <summary>Reads the record for an exact attempt identity.</summary>
    ValueTask<ExternalOperationRecoveryRecord?> ReadAsync(ExternalOperationStartIdentity identity, CancellationToken cancellationToken = default);

    /// <summary>Persists a provider handle before the provider callback is forwarded.</summary>
    ValueTask CaptureAsync(ExternalOperationStartIdentity identity, ExternalOperationHandleCapture capture, CancellationToken cancellationToken = default);

    /// <summary>Persists the newest valid provider observation.</summary>
    ValueTask RecordObservationAsync(ExternalOperationStartIdentity identity, ExternalOperationObservation observation, CancellationToken cancellationToken = default);

    /// <summary>Persists a terminal provider result.</summary>
    ValueTask RecordResultAsync(ExternalOperationStartIdentity identity, ExternalOperationResult result, CancellationToken cancellationToken = default);

    /// <summary>Persists that a recovered handle cannot be safely reconnected to its provider.</summary>
    ValueTask MarkKnownNotReconnectableAsync(ExternalOperationStartIdentity identity, CancellationToken cancellationToken = default);
}

/// <summary>Raised when durable provider recovery must stop for operator resolution.</summary>
public sealed class ExternalOperationRecoveryException : InvalidOperationException
{
    /// <summary>Creates a recovery failure without exposing opaque provider handles.</summary>
    public ExternalOperationRecoveryException(ExternalOperationRecoveryState state)
        : base(MessageFor(state))
    {
        if (!Enum.IsDefined(state))
        {
            throw new ArgumentOutOfRangeException(nameof(state));
        }

        State = state;
    }

    /// <summary>The safe recovery classification.</summary>
    public ExternalOperationRecoveryState State { get; }

    private static string MessageFor(ExternalOperationRecoveryState state) => state switch
    {
        ExternalOperationRecoveryState.UnknownAfterRecovery => "The provider start outcome is unknown because no handle was durably captured; automatic restart is blocked.",
        ExternalOperationRecoveryState.KnownNotReconnectable => "The provider handle is known, but safe reconnection is unavailable; operator resolution is required.",
        _ => "The external operation cannot be safely recovered.",
    };
}

/// <summary>
/// Decorates a provider with a durable start journal. It records intent before
/// invoking the provider and records every handle before forwarding capture.
/// </summary>
public sealed class DurableExternalOperationProvider : IExternalOperationProvider
{
    private readonly IExternalOperationProvider inner;
    private readonly IExternalOperationRecoveryJournal journal;
    private readonly IExternalOperationSemanticFingerprintVerifier fingerprintVerifier;
    private readonly Func<DateTimeOffset> now;
    private readonly bool supportsRecoveryObservation;
    private readonly HashSet<ExternalOperationAttemptKey> startedHere = [];
    private readonly Dictionary<ExternalOperationAttemptKey, ExternalOperationStartIdentity> startIdentities = [];
    private readonly object gate = new();

    /// <summary>Creates a provider decorator backed by a host-owned recovery journal.</summary>
    public DurableExternalOperationProvider(
        IExternalOperationProvider inner,
        IExternalOperationRecoveryJournal journal,
        IExternalOperationSemanticFingerprintVerifier fingerprintVerifier,
        bool supportsRecoveryObservation = true,
        Func<DateTimeOffset>? now = null)
    {
        this.inner = inner ?? throw new ArgumentNullException(nameof(inner));
        this.journal = journal ?? throw new ArgumentNullException(nameof(journal));
        this.fingerprintVerifier = fingerprintVerifier ?? throw new ArgumentNullException(nameof(fingerprintVerifier));
        this.supportsRecoveryObservation = supportsRecoveryObservation;
        this.now = now ?? (() => DateTimeOffset.UtcNow);
    }

    /// <inheritdoc />
    public async ValueTask<ExternalOperationStartReceipt> StartAsync(
        ExternalOperationStartRequest request,
        IExternalOperationHandleCaptureSink handleSink,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(handleSink);
        request.VerifySemanticFingerprint(fingerprintVerifier);
        var identity = request.Identity;
        var key = ExternalOperationAttemptKey.Create(identity);
        lock (gate)
        {
            startIdentities[key] = identity;
        }

        var existing = await journal.ReadAsync(identity, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            EnsureSameIntent(existing.Intent, request);
            if (existing.Capture is null)
            {
                throw new ExternalOperationRecoveryException(ExternalOperationRecoveryState.UnknownAfterRecovery);
            }

            await ReplayCaptureAsync(existing.Capture, handleSink, cancellationToken).ConfigureAwait(false);
            lock (gate)
            {
                if (startedHere.Contains(key))
                {
                    return Receipt(request, existing, ExternalOperationStartDisposition.Existing);
                }
            }

            if (existing.Result is not null)
            {
                return Receipt(request, existing, ExternalOperationStartDisposition.Existing);
            }

            if (!supportsRecoveryObservation)
            {
                await journal.MarkKnownNotReconnectableAsync(identity, cancellationToken).ConfigureAwait(false);
            }

            return Receipt(request, existing, ExternalOperationStartDisposition.Existing);
        }

        var intent = new ExternalOperationRecoveryIntent(request, now());
        if (!await journal.TryCreateIntentAsync(intent, cancellationToken).ConfigureAwait(false))
        {
            existing = await journal.ReadAsync(identity, cancellationToken).ConfigureAwait(false);
            if (existing is null)
            {
                throw new InvalidOperationException("The recovery journal reported an existing attempt but could not read it.");
            }

            EnsureSameIntent(existing.Intent, request);
            if (existing.Capture is null)
            {
                throw new ExternalOperationRecoveryException(ExternalOperationRecoveryState.UnknownAfterRecovery);
            }

            await ReplayCaptureAsync(existing.Capture, handleSink, cancellationToken).ConfigureAwait(false);
            if (!supportsRecoveryObservation && existing.Result is null)
            {
                await journal.MarkKnownNotReconnectableAsync(identity, cancellationToken).ConfigureAwait(false);
            }

            return Receipt(request, existing, ExternalOperationStartDisposition.Existing);
        }

        lock (gate)
        {
            startedHere.Add(key);
        }

        var durableSink = new JournalCaptureSink(journal, identity, request.Correlation, handleSink);
        var receipt = await inner.StartAsync(request, durableSink, cancellationToken).ConfigureAwait(false);
        ArgumentNullException.ThrowIfNull(receipt);
        DurableOperationIdentityValidation.RequireMatchingIdentity(identity, receipt.Handle.Correlation);
        if (!DurableOperationIdentityValidation.SameCorrelation(request.Correlation, receipt.Handle.Correlation))
        {
            throw new InvalidOperationException("The provider start receipt returned a different operation correlation.");
        }
        var persisted = await journal.ReadAsync(identity, cancellationToken).ConfigureAwait(false);
        if (persisted?.Capture is null)
        {
            await durableSink.CaptureAsync(new ExternalOperationHandleCapture(receipt.Handle, receipt.AcceptedAt), cancellationToken).ConfigureAwait(false);
        }
        else if (persisted.Capture.Handle != receipt.Handle)
        {
            throw new InvalidOperationException("The provider start receipt conflicts with its earlier durable handle capture.");
        }

        return receipt;
    }

    /// <inheritdoc />
    public async ValueTask<ExternalOperationObservation> ObserveAsync(
        ExternalOperationHandle handle,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(handle);
        var identity = FindIdentity(handle.Correlation);
        var record = await journal.ReadAsync(identity, cancellationToken).ConfigureAwait(false)
            ?? throw new ExternalOperationRecoveryException(ExternalOperationRecoveryState.UnknownAfterRecovery);
        EnsureReconnectable(record);
        if (record.Result is not null)
        {
            return record.Observation is { State: ExternalOperationState.Succeeded
                or ExternalOperationState.Failed
                or ExternalOperationState.Cancelled
                or ExternalOperationState.TimedOut
                or ExternalOperationState.Rejected } durable
                ? durable : ObservationFromResult(record.Result, record.Observation);
        }

        var observation = await inner.ObserveAsync(handle, cancellationToken).ConfigureAwait(false);
        if (!SameHandle(handle, observation.Handle))
        {
            throw new InvalidOperationException("The provider observation returned a different external handle.");
        }

        if (record.Observation is not null)
        {
            ExternalOperationObservationRules.ValidateProgression(record.Observation, observation);
        }

        await journal.RecordObservationAsync(identity, observation, cancellationToken).ConfigureAwait(false);
        return observation;
    }

    /// <inheritdoc />
    public async ValueTask<ExternalOperationResult> GetResultAsync(
        ExternalOperationHandle handle,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(handle);
        var identity = FindIdentity(handle.Correlation);
        var record = await journal.ReadAsync(identity, cancellationToken).ConfigureAwait(false)
            ?? throw new ExternalOperationRecoveryException(ExternalOperationRecoveryState.UnknownAfterRecovery);
        EnsureReconnectable(record);
        if (record.Result is not null)
        {
            return record.Result;
        }

        var result = await inner.GetResultAsync(handle, cancellationToken).ConfigureAwait(false);
        if (!SameHandle(handle, result.Handle))
        {
            throw new InvalidOperationException("The provider result returned a different external handle.");
        }

        await journal.RecordResultAsync(identity, result, cancellationToken).ConfigureAwait(false);
        return result;
    }

    /// <inheritdoc />
    public ValueTask<ExternalOperationCancellationReceipt> CancelAsync(
        ExternalOperationCancelRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        // M1 supports observation and result fencing. Cancellation/resume need their own durable
        // authority and command receipts; never forward an unjournaled mutation accidentally.
        return ValueTask.FromException<ExternalOperationCancellationReceipt>(
            new ExternalOperationRecoveryException(ExternalOperationRecoveryState.KnownNotReconnectable));
    }

    /// <inheritdoc />
    public ValueTask<ExternalOperationResumeReceipt> ResumeAsync(
        ExternalOperationResumeRequest request,
        IExternalOperationHandleCaptureSink handleSink,
        CancellationToken cancellationToken = default) =>
        ValueTask.FromException<ExternalOperationResumeReceipt>(
            new ExternalOperationRecoveryException(ExternalOperationRecoveryState.KnownNotReconnectable));

    private ExternalOperationStartIdentity FindIdentity(ExternalOperationCorrelation correlation)
    {
        lock (gate)
        {
            return startIdentities.TryGetValue(ExternalOperationAttemptKey.Create(correlation), out var identity)
                ? identity
                : throw new ExternalOperationRecoveryException(ExternalOperationRecoveryState.UnknownAfterRecovery);
        }
    }

    private static ExternalOperationStartReceipt Receipt(
        ExternalOperationStartRequest request,
        ExternalOperationRecoveryRecord record,
        ExternalOperationStartDisposition disposition)
    {
        var capture = record.Capture!;
        var state = record.Observation?.State ?? record.Result?.State ?? ExternalOperationState.Unknown;
        if (!Enum.IsDefined(state))
        {
            state = ExternalOperationState.Unknown;
        }

        return new ExternalOperationStartReceipt(request.Identity, capture.Handle, disposition, state, record.Intent.CreatedAt);
    }

    private static ExternalOperationObservation ObservationFromResult(ExternalOperationResult result, ExternalOperationObservation? prior) =>
        new(result.Handle, prior is null ? 0 : prior.Revision + 1, result.State,
            prior is not null && prior.ObservedAt > result.CompletedAt ? prior.ObservedAt : result.CompletedAt,
            failure: result.Failure, resultAvailable: true);

    private static void EnsureSameIntent(ExternalOperationRecoveryIntent intent, ExternalOperationStartRequest request)
    {
        if (!SameIdentity(intent.Request.Identity, request.Identity)
            || !string.Equals(intent.Request.Capability, request.Capability, StringComparison.Ordinal)
            || !intent.Request.InputArtifacts.SequenceEqual(request.InputArtifacts)
            || intent.Request.Budget != request.Budget
            || intent.Request.Deadline != request.Deadline
            || intent.Request.Correlation.Task != request.Correlation.Task
            || intent.Request.ExecutionAttachment != request.ExecutionAttachment
            || !DurableOperationIdentityValidation.SameCorrelation(intent.Request.Correlation, request.Correlation))
        {
            throw new InvalidOperationException("A durable provider attempt key was replayed with conflicting semantic inputs.");
        }
    }

    private static bool SameIdentity(ExternalOperationStartIdentity left, ExternalOperationStartIdentity right) =>
        ExternalOperationAttemptKey.Create(left) == ExternalOperationAttemptKey.Create(right)
        && string.Equals(left.IdempotencyKey, right.IdempotencyKey, StringComparison.Ordinal)
        && string.Equals(left.SemanticFingerprint, right.SemanticFingerprint, StringComparison.Ordinal);

    private static bool SameHandle(ExternalOperationHandle left, ExternalOperationHandle right) => left == right;

    private static void EnsureReconnectable(ExternalOperationRecoveryRecord record)
    {
        if (record.RecoveryState == ExternalOperationRecoveryState.KnownNotReconnectable)
        {
            throw new ExternalOperationRecoveryException(record.RecoveryState);
        }
    }

    private static async ValueTask ReplayCaptureAsync(
        ExternalOperationHandleCapture capture,
        IExternalOperationHandleCaptureSink handleSink,
        CancellationToken cancellationToken) =>
        await handleSink.CaptureAsync(capture, cancellationToken).ConfigureAwait(false);

    private sealed class JournalCaptureSink(
        IExternalOperationRecoveryJournal journal,
        ExternalOperationStartIdentity identity,
        ExternalOperationCorrelation correlation,
        IExternalOperationHandleCaptureSink forward) : IExternalOperationHandleCaptureSink
    {
        public async ValueTask CaptureAsync(ExternalOperationHandleCapture capture, CancellationToken cancellationToken = default)
        {
            DurableOperationIdentityValidation.RequireMatchingIdentity(identity, capture.Handle.Correlation);
            if (!DurableOperationIdentityValidation.SameCorrelation(correlation, capture.Handle.Correlation))
            {
                throw new InvalidOperationException("The provider captured a handle for a different operation correlation.");
            }
            await journal.CaptureAsync(identity, capture, cancellationToken).ConfigureAwait(false);
            await forward.CaptureAsync(capture, cancellationToken).ConfigureAwait(false);
        }
    }
}

internal readonly record struct ExternalOperationAttemptKey(
    Guid DelegationId,
    string WorkflowProvider,
    string WorkflowRunId,
    string ExecutionEpoch,
    string StructuralNode,
    Guid NodeGeneration,
    string ExecutionAttemptId,
    string AgentProvider,
    string AgentIdentifier)
{
    internal static ExternalOperationAttemptKey Create(ExternalOperationStartIdentity identity) => new(
        identity.DelegationId.Value,
        identity.WorkflowRun.Provider,
        identity.WorkflowRun.WorkflowRunId,
        identity.WorkflowRun.ExecutionEpoch,
        identity.StructuralNode.Identifier,
        identity.NodeGeneration.Value,
        identity.ExecutionAttemptId,
        string.Empty,
        string.Empty);

    internal static ExternalOperationAttemptKey Create(ExternalOperationCorrelation correlation) => new(
        correlation.DelegationId.Value,
        correlation.WorkflowRun.Provider,
        correlation.WorkflowRun.WorkflowRunId,
        correlation.WorkflowRun.ExecutionEpoch,
        correlation.StructuralNode.Identifier,
        correlation.NodeGeneration.Value,
        correlation.ExecutionAttemptId,
        string.Empty,
        string.Empty);
}

internal static class DurableOperationIdentityValidation
{
    internal static void RequireMatchingIdentity(
        ExternalOperationStartIdentity identity,
        ExternalOperationCorrelation correlation)
    {
        if (identity.DelegationId != correlation.DelegationId
            || identity.WorkflowRun != correlation.WorkflowRun
            || identity.StructuralNode != correlation.StructuralNode
            || identity.NodeGeneration != correlation.NodeGeneration
            || !string.Equals(identity.ExecutionAttemptId, correlation.ExecutionAttemptId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The provider handle does not match the exact external-operation attempt identity.");
        }
    }

    internal static bool SameCorrelation(ExternalOperationCorrelation left, ExternalOperationCorrelation right) =>
        ExternalOperationAttemptKey.Create(left) == ExternalOperationAttemptKey.Create(right)
        && left.Agent == right.Agent
        // The provider learns its task identity during start. A known prior task may never change.
        && (left.Task is null || left.Task == right.Task);
}
