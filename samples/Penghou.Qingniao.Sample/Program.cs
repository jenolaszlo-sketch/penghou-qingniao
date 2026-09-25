// Minimal end-to-end walk through the public Penghou.Qingniao surface only:
// register one fake provider, delegate one bounded unit of work, pump the
// deterministic coordinator to a terminal state, and read the immutable
// result. If Marang can do everything this file does, the runtime is
// consumable. Provider selection, budgets, supervision, and evidence live in
// the runtime; transport, auth, and product policy live in the host.

using Penghou.Qingniao;

var provider = new SampleProvider();
var descriptor = new ProviderDescriptor("sample-provider", [new CapabilityDescriptor("agent.execute", 1)]);
var providers = new InMemoryProviderRegistry();
providers.Register(descriptor);
var adapters = new InMemoryExternalOperationProviderCatalog();
adapters.Register(descriptor, provider);

var runtime = new DelegationRuntime(
    new InMemoryDelegationAcceptanceRegistry(),
    admissionVerifier: null,
    providers,
    adapters);

var caller = new DelegationCallerScope("sample-caller");
var request = new DelegationRequest(
    "sample-run",
    "Do the delegated work",
    "sample-provider",
    new WorkspaceReference("local", "workspace", "revision"),
    ["Done"],
    [],
    new DelegationBudget(MaximumWorkerCalls: 8, MaximumRetries: 2));

var handle = await runtime.DelegateAsync(caller, request);
Console.WriteLine($"accepted {handle.DelegationId.Value:D} in state {handle.State}");

var snapshot = await runtime.GetAsync(handle.DelegationId);
for (var index = 0; index < 10 && !DelegationLifecycle.IsTerminal(snapshot.Progress.State); index++)
{
    snapshot = await runtime.PumpAsync(handle.DelegationId, snapshot.Progress.Revision);
}

Console.WriteLine($"terminal state: {snapshot.Progress.State}");
Console.WriteLine($"result: {snapshot.Result?.Summary ?? "<none>"}");

if (!DelegationLifecycle.IsTerminal(snapshot.Progress.State))
{
    Console.Error.WriteLine("The delegation did not reach a terminal state.");
    return 1;
}

return 0;

internal sealed class SampleProvider : IExternalOperationProvider
{
    private static readonly DateTimeOffset Start = new(2026, 3, 1, 0, 0, 0, TimeSpan.Zero);
    private ExternalOperationHandle? handle;

    public async ValueTask<ExternalOperationStartReceipt> StartAsync(
        ExternalOperationStartRequest request,
        IExternalOperationHandleCaptureSink handleSink,
        CancellationToken cancellationToken = default)
    {
        var correlation = new ExternalOperationCorrelation(
            request.Correlation.DelegationId,
            request.Correlation.WorkflowRun,
            request.Correlation.StructuralNode,
            request.Correlation.NodeGeneration,
            request.Correlation.ExecutionAttemptId,
            request.Correlation.Agent,
            new ExternalTaskReference(request.Correlation.Agent.Provider, "sample-task"));
        handle = new ExternalOperationHandle(
            request.Correlation.Agent.Provider,
            "sample-handle",
            request.Correlation.Agent.ProtocolVersion,
            correlation);
        await handleSink.CaptureAsync(
            new ExternalOperationHandleCapture(handle, Start.AddMinutes(1)),
            cancellationToken);
        return new ExternalOperationStartReceipt(
            request.Identity, handle, ExternalOperationStartDisposition.Created,
            ExternalOperationState.Running, Start.AddMinutes(1));
    }

    public ValueTask<ExternalOperationObservation> ObserveAsync(
        ExternalOperationHandle operationHandle,
        CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(new ExternalOperationObservation(
            operationHandle, 1, ExternalOperationState.Succeeded, Start.AddMinutes(2),
            resultAvailable: true));

    public ValueTask<ExternalOperationResult> GetResultAsync(
        ExternalOperationHandle operationHandle,
        CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(new ExternalOperationResult(
            operationHandle, ExternalOperationState.Succeeded, Start.AddMinutes(3),
            "done", []));

    public ValueTask<ExternalOperationCancellationReceipt> CancelAsync(
        ExternalOperationCancelRequest request,
        CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(new ExternalOperationCancellationReceipt(
            request.Handle, request.CancellationKey,
            ExternalOperationCancellationDisposition.Requested,
            ExternalOperationState.CancellationRequested, Start.AddMinutes(4)));

    public ValueTask<ExternalOperationResumeReceipt> ResumeAsync(
        ExternalOperationResumeRequest request,
        IExternalOperationHandleCaptureSink handleSink,
        CancellationToken cancellationToken = default) => throw new NotSupportedException();
}
