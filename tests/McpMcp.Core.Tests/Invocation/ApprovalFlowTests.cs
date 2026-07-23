using System.Collections.Concurrent;
using AwesomeAssertions;
using McpMcp.Abstractions;
using Xunit;

namespace McpMcp.Core.Tests.Invocation;

/// <summary>
/// FR-32 / ADR-0012 durch die Pipeline: blockieren → freigeben → derselbe Call läuft EINMALIG,
/// ein anderer nicht, und ohne Seiteneffekt beim Blockieren.
/// </summary>
public class ApprovalFlowTests
{
    private readonly InvokerTestWorld _w = new();

    private sealed class FakePolicy : IApprovalPolicy
    {
        private readonly HashSet<NamespacedToolName> _tools;
        public FakePolicy(params NamespacedToolName[] tools) => _tools = [.. tools];
        public bool RequiresApproval(NamespacedToolName tool) => _tools.Contains(tool);
        public IReadOnlyCollection<NamespacedToolName> All => _tools;
        public Task SetAsync(NamespacedToolName tool, bool required, CancellationToken ct) => Task.CompletedTask;
        public event EventHandler? Changed { add { } remove { } }
    }

    /// <summary>Minimaler In-Memory-Store: genau das Verhalten aus ADR-0012, ohne DB.</summary>
    private sealed class FakeStore : IApprovalStore
    {
        private readonly ConcurrentDictionary<string, ApprovalState> _byKey = new();
        public int Enqueued { get; private set; }

        private static string Key(IdentityId c, NamespacedToolName t, string fp) => $"{c.Value:N}|{t.Value}|{fp}";

        public Task<bool> TryConsumeApprovalAsync(IdentityId caller, NamespacedToolName tool, string fp, CancellationToken ct)
        {
            var key = Key(caller, tool, fp);
            if (_byKey.TryGetValue(key, out var s) && s == ApprovalState.Approved)
            {
                _byKey[key] = ApprovalState.Consumed; // einmalig
                return Task.FromResult(true);
            }

            return Task.FromResult(false);
        }

        public Task<Guid> EnqueueAsync(ApprovalRequest r, CancellationToken ct)
        {
            Enqueued++;
            _byKey.TryAdd(Key(r.Caller, r.Tool, r.ArgumentFingerprint), ApprovalState.Pending);
            return Task.FromResult(Guid.NewGuid());
        }

        public void Approve(IdentityId caller, NamespacedToolName tool, string fp)
            => _byKey[Key(caller, tool, fp)] = ApprovalState.Approved;

        public Task<IReadOnlyList<ApprovalRequest>> ListAsync(ApprovalState? state, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<ApprovalRequest>>([]);
        public Task DecideAsync(Guid id, bool approved, CancellationToken ct) => Task.CompletedTask;
    }

    [Fact]
    public async Task Approval_required_tool_is_blocked_without_side_effect()
    {
        var admin = _w.RegisterAdmin();
        var invoker = _w.WithApproval(new FakePolicy(_w.Echo), new FakeStore());

        var result = await invoker.InvokeAsync(
            InvokerTestWorld.Request(admin, _w.Echo, new { message = "hi" }),
            TestContext.Current.CancellationToken);

        result.Status.Should().Be(InvocationStatus.ApprovalRequired);
        result.ErrorMessage.Should().Contain("Freigabe").And.Contain("erneut absetzen");
        _w.Connection.LastToolName.Should().BeNull("der Call darf nicht ausgeführt worden sein");
    }

    [Fact]
    public async Task After_approval_the_same_call_runs_exactly_once()
    {
        var admin = _w.RegisterAdmin();
        var store = new FakeStore();
        var invoker = _w.WithApproval(new FakePolicy(_w.Echo), store);

        // 1) Blockiert, Anfrage in der Queue.
        var first = await invoker.InvokeAsync(
            InvokerTestWorld.Request(admin, _w.Echo, new { message = "hi" }),
            TestContext.Current.CancellationToken);
        first.Status.Should().Be(InvocationStatus.ApprovalRequired);

        // 2) Mensch gibt genau diesen Aufruf frei (gleicher Fingerprint).
        var redacted = _w.Redaction.RedactArguments(
            _w.Echo, System.Text.Json.JsonSerializer.SerializeToElement(new { message = "hi" }));
        var fp = McpMcp.Core.Approvals.ApprovalFingerprint.Compute(admin, _w.Echo, redacted);
        store.Approve(admin, _w.Echo, fp);

        // 3) Retry desselben Calls läuft durch.
        var second = await invoker.InvokeAsync(
            InvokerTestWorld.Request(admin, _w.Echo, new { message = "hi" }),
            TestContext.Current.CancellationToken);
        second.Status.Should().Be(InvocationStatus.Success);

        // 4) Ein weiterer identischer Call ist wieder blockiert — Freigabe war einmalig.
        var third = await invoker.InvokeAsync(
            InvokerTestWorld.Request(admin, _w.Echo, new { message = "hi" }),
            TestContext.Current.CancellationToken);
        third.Status.Should().Be(InvocationStatus.ApprovalRequired);
    }

    [Fact]
    public async Task Approval_does_not_transfer_to_a_different_argument()
    {
        var admin = _w.RegisterAdmin();
        var store = new FakeStore();
        var invoker = _w.WithApproval(new FakePolicy(_w.Echo), store);

        // Freigabe für message="a" …
        var redacted = _w.Redaction.RedactArguments(
            _w.Echo, System.Text.Json.JsonSerializer.SerializeToElement(new { message = "a" }));
        store.Approve(admin, _w.Echo, McpMcp.Core.Approvals.ApprovalFingerprint.Compute(admin, _w.Echo, redacted));

        // … deckt einen Call mit message="b" NICHT ab.
        var result = await invoker.InvokeAsync(
            InvokerTestWorld.Request(admin, _w.Echo, new { message = "b" }),
            TestContext.Current.CancellationToken);

        result.Status.Should().Be(InvocationStatus.ApprovalRequired,
            "eine Freigabe bindet an die konkreten Argumente, nicht an das Tool");
    }

    [Fact]
    public async Task Tool_without_approval_policy_runs_normally()
    {
        var admin = _w.RegisterAdmin();
        var invoker = _w.WithApproval(new FakePolicy(/* leer */), new FakeStore());

        var result = await invoker.InvokeAsync(
            InvokerTestWorld.Request(admin, _w.Echo, new { message = "hi" }),
            TestContext.Current.CancellationToken);

        result.Status.Should().Be(InvocationStatus.Success);
    }
}
