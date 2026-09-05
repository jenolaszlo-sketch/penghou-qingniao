# Initial specification review

> **Superseded product boundary:** the semantic findings remain useful, but
> delegation-kernel ownership has moved to Qingniao and Marang is now the
> ASP.NET Core MVC/MCP service. See
> [ADR 0015](decisions/0015-qingniao-extraction-and-marang-service-boundary.md).

> Historical review note: this document records the initial fixed-slice
> decision. The revised product direction keeps that slice as the simple
> `marang_delegate` preset and now plans advanced supervisor-selected or
> supervisor-authored Fuwen workflows, durable waiting, intervention, and
> selective re-execution. See [product direction](product-direction.md) for
> the current authority.

## Verdict

The proposal is a good product boundary and likely a much shorter route to
useful dogfooding than another end-user code-generation application. Its best
decision is keeping the supervising model responsible for intent and judgment
while delegating repeatable subordinate work.

The original fixed `Inspect -> Plan -> Implement -> Test/Review -> Fix -> Result`
slice was sufficient to prove the idea. The agent-execution addendum improves it
further by allowing a capable provider to own inspection and planning while
Marang retains independent Test, Review, Evaluate, and bounded Correction gates.
That slice remains the simple preset. A workflow DSL is still not a Marang
responsibility, but Fuwen now provides the planned semantic/compiler boundary
for supervisor-selected or supervisor-authored artifact-driven workflows.

## Changes made while curating the design

### Hongxian is temporal evidence, not worker execution

The current Hongxian package is a durable session kernel. It owns temporal
continuity, correlation, decisions, incidents, recovery evidence, and
projections. It does not currently own repository access, shell execution, or
sandboxing. Marang therefore starts with provider-neutral execution and durable
external-operation contracts. Hongxian is the session/correlation authority for
the real durable supervisory slice, although pure core/in-memory tests and
simple preset policy evaluation can use fakes. Hongxian is not an executor.

### Workspace inputs are capabilities, not paths

Accepting `workingDirectory` directly over MCP creates an avoidable authority
problem. The public contract now uses `WorkspaceReference`; the host resolves
that reference under configured policy. Local paths can remain an internal
adapter detail.

### Submission needs idempotency

MCP and process boundaries create ambiguous failures. Without a request key, a
caller retry could launch duplicate workflows and duplicate model cost. The
initial contract makes idempotent acceptance mandatory.

### Parallel validation needs a sealed revision

Running tests and review concurrently is safe only if both observe the same
candidate. Candidate revision identity is therefore part of artifact and
worker design. Repair produces another revision and another pair of reports.

### “Completed” must say what was completed

Marang should initially create a validated candidate or patch. It must not
silently merge, commit, push, publish, or modify a supervisor's active checkout.
Those are explicit capabilities and disposition decisions.

### Independence and confidence require evidence

A separate review call is useful, but “independent” has degrees. Marang should
record invocation, model, provider, context, and profile separation and let
policy decide what counts. Numeric model confidence alone must not authorize
unsafe mutation.

## Open design gates

Before the first durable adapter, decide:

1. the normalized request fingerprint used with `RequestKey`;
2. the minimum durable execution-provider protocol and normalized receipt
   format;
3. artifact persistence and immutable candidate revision representation;
4. which Zhinu workflow/result APIs are stable enough to adapt directly;
5. the MCP authentication and workspace authorization responsibility of the
   hosting process;
6. how the planned `WaitingForSupervisor` state, wake policy, and
   revision-fenced intervention should coexist with terminal `NeedsSupervisor`.

## Agent-execution addendum

The addendum's central correction is accepted: mature coding agents are
execution substrates, not mechanisms Marang should recreate. Marang owns the
durable and accountable outcome surrounding their work.

The proposed single blocking `IExecutionProvider.ExecuteAsync` is too weak for
durability. The final provider contract must expose an idempotent start and a
durable external handle that can be observed, cancelled, and resumed. Otherwise
a Zhinu retry after an ambiguous failure could duplicate an agent run and its
cost.

`ExecutionMode` is retained as a useful semantic category, not a closed routing
enum. Capability routing is the extensible contract. `HongxianExecutionProvider`
is rejected because Hongxian is not an executor. A Codex provider is feasible
through the documented Codex SDK/app server or, for the initial spike,
non-interactive JSONL mode.
