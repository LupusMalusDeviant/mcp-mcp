using FluentAssertions;
using McpMcp.Abstractions;
using McpMcp.Core.Upstreams;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace McpMcp.Core.Tests.Upstreams;

public sealed class UpstreamSupervisorTests : IAsyncDisposable
{
    private readonly FakeTimeProvider _time = new();
    private readonly FakeUpstreamConnector _connector = new();
    private readonly InMemoryUpstreamConfigStore _store;
    private readonly UpstreamSupervisor _supervisor;
    private readonly List<UpstreamChangedEventArgs> _events = [];

    private static readonly RestartPolicy TestPolicy =
        new(MaxRetries: 2, InitialBackoff: TimeSpan.FromSeconds(1), BackoffMultiplier: 2.0, MaxBackoff: TimeSpan.FromSeconds(10));

    public UpstreamSupervisorTests()
    {
        _store = new InMemoryUpstreamConfigStore(_time);
        _supervisor = new UpstreamSupervisor(
            [_connector],
            _store,
            new SupervisorOptions
            {
                HealthCheckInterval = TimeSpan.FromSeconds(1),
                HealthyResetWindow = TimeSpan.FromSeconds(60),
                DefaultCallTimeout = TimeSpan.FromSeconds(30),
                DefaultDrainGrace = TimeSpan.FromSeconds(5),
                DefaultRestartPolicy = TestPolicy,
            },
            _time);
        _supervisor.Changed += (_, e) =>
        {
            lock (_events)
            {
                _events.Add(e);
            }
        };
    }

    public async ValueTask DisposeAsync() => await _supervisor.DisposeAsync();

    private IReadOnlyList<UpstreamChangedEventArgs> Events
    {
        get
        {
            lock (_events)
            {
                return [.. _events];
            }
        }
    }

    [Fact]
    public async Task Add_connects_discovers_and_becomes_healthy()
    {
        var id = await _supervisor.AddAsync(TestData.StdioConfig(), CancellationToken.None);

        await TestData.WaitUntilAsync(() => _supervisor.GetStatus(id)?.State == UpstreamState.Healthy);

        _supervisor.GetStatus(id)!.ToolCount.Should().Be(1);
        _supervisor.GetInventory(id)!.Tools.Should().ContainSingle(t => t.Name == "echo");
        _supervisor.GetConnection(id).Should().NotBeNull();
        Events.Should().Contain(e => e.Kind == UpstreamChangeKind.Added && e.Server == id);
        Events.Should().Contain(e => e.Kind == UpstreamChangeKind.InventoryChanged && e.Server == id);
    }

    [Fact]
    public async Task Add_disabled_config_stays_stopped_without_connecting()
    {
        var id = await _supervisor.AddAsync(TestData.StdioConfig(enabled: false), CancellationToken.None);

        await Task.Delay(100);

        _supervisor.GetStatus(id)!.State.Should().Be(UpstreamState.Stopped);
        _connector.ConnectCalls.Should().Be(0);
        _supervisor.GetConnection(id).Should().BeNull();
    }

    [Fact]
    public async Task Add_rejects_duplicate_slug()
    {
        await _supervisor.AddAsync(TestData.StdioConfig("dup"), CancellationToken.None);

        var act = () => _supervisor.AddAsync(TestData.StdioConfig("dup"), CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentException>().WithMessage("*dup*");
    }

    [Fact]
    public async Task Connect_failures_retry_with_backoff_then_recover()
    {
        _connector.EnqueueConnectFailure();
        _connector.EnqueueConnectFailure();

        var id = await _supervisor.AddAsync(TestData.StdioConfig(), CancellationToken.None);

        await TestData.WaitUntilAdvancingAsync(
            _time,
            () => _supervisor.GetStatus(id)?.State == UpstreamState.Healthy,
            because: "nach 2 Fehlversuchen verbindet der dritte Versuch");

        _connector.ConnectCalls.Should().Be(3, "2 skriptete Fehlversuche + 1 erfolgreicher Versuch");
        Events.Should().Contain(
            e => e.Kind == UpstreamChangeKind.StateChanged && e.Server == id && e.State == UpstreamState.Failed,
            "die Fehlversuche müssen als Failed sichtbar gewesen sein");
    }

    [Fact]
    public void Backoff_formula_is_exponential_and_capped()
    {
        UpstreamSupervisor.ComputeBackoff(TestPolicy, 1).Should().Be(TimeSpan.FromSeconds(1));
        UpstreamSupervisor.ComputeBackoff(TestPolicy, 2).Should().Be(TimeSpan.FromSeconds(2));
        UpstreamSupervisor.ComputeBackoff(TestPolicy, 3).Should().Be(TimeSpan.FromSeconds(4));
        UpstreamSupervisor.ComputeBackoff(TestPolicy, 10).Should().Be(TimeSpan.FromSeconds(10), "MaxBackoff deckelt");
    }

    [Fact]
    public async Task Permanent_failure_after_exhausted_retries_stops_restarting()
    {
        _connector.DefaultBehavior = static (_, _) => throw new IOException("dauerhaft kaputt (Test)");

        var id = await _supervisor.AddAsync(TestData.StdioConfig(), CancellationToken.None);

        await TestData.WaitUntilAdvancingAsync(
            _time,
            () => _connector.ConnectCalls == 3,
            step: TimeSpan.FromMilliseconds(250),
            because: "1 Erstversuch + 2 Retries (MaxRetries=2)");

        for (var i = 0; i < 20; i++)
        {
            _time.Advance(TimeSpan.FromSeconds(5));
            await Task.Delay(10);
        }

        _connector.ConnectCalls.Should().Be(3, "nach Erschöpfen der RestartPolicy darf kein weiterer Versuch folgen");
        _supervisor.GetStatus(id)!.State.Should().Be(UpstreamState.Failed);
        _supervisor.GetStatus(id)!.LastError.Should().Contain("dauerhaft kaputt");
    }

    [Fact]
    public async Task Ping_failure_marks_failed_and_auto_restarts_to_healthy()
    {
        var id = await _supervisor.AddAsync(TestData.StdioConfig(), CancellationToken.None);
        await TestData.WaitUntilAsync(() => _supervisor.GetStatus(id)?.State == UpstreamState.Healthy);

        _connector.Connections[0].FailPing = true;

        await TestData.WaitUntilAdvancingAsync(
            _time,
            () => _connector.Connections.Count == 2 && _supervisor.GetStatus(id)?.State == UpstreamState.Healthy,
            because: "Ping-Fehler → Failed → Auto-Restart mit frischer Verbindung");

        Events.Should().Contain(
            e => e.Kind == UpstreamChangeKind.StateChanged && e.Server == id && e.State == UpstreamState.Failed,
            "der Verbindungsverlust muss als Failed sichtbar gewesen sein");
        _connector.Connections[0].DisposeCount.Should().BeGreaterThan(0, "alte Verbindung wird aufgeräumt");
    }

    [Fact]
    public async Task Remove_disposes_connection_clears_entry_and_history()
    {
        var id = await _supervisor.AddAsync(TestData.StdioConfig(), CancellationToken.None);
        await TestData.WaitUntilAsync(() => _supervisor.GetStatus(id)?.State == UpstreamState.Healthy);

        await _supervisor.RemoveAsync(id, DrainPolicy.Immediate, CancellationToken.None);

        _supervisor.GetStatus(id).Should().BeNull();
        _supervisor.GetConnection(id).Should().BeNull();
        (await _store.GetHistoryAsync(id, CancellationToken.None)).Should().BeEmpty();
        _connector.Connections[0].DisposeCount.Should().BeGreaterThan(0);
        Events.Should().Contain(e => e.Kind == UpstreamChangeKind.Removed && e.Server == id);
    }

    [Fact]
    public async Task Remove_with_graceful_drain_waits_for_inflight_call()
    {
        var id = await _supervisor.AddAsync(TestData.StdioConfig(), CancellationToken.None);
        await TestData.WaitUntilAsync(() => _supervisor.GetStatus(id)?.State == UpstreamState.Healthy);

        var gate = new TaskCompletionSource<System.Text.Json.JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _connector.Connections[0].CallGate = gate;
        var connection = (GuardedUpstreamConnection)_supervisor.GetConnection(id)!;
        var call = connection.CallToolAsync("echo", TestData.EmptySchema(), CancellationToken.None);
        await TestData.WaitUntilAsync(() => connection.InFlightCount == 1);

        var remove = _supervisor.RemoveAsync(id, DrainPolicy.Graceful(TimeSpan.FromSeconds(5)), CancellationToken.None);
        await Task.Delay(100);
        remove.IsCompleted.Should().BeFalse("Drain wartet auf den laufenden Call");

        gate.TrySetResult(TestData.EmptySchema());
        await call;
        _time.Advance(TimeSpan.FromMilliseconds(50));

        await TestData.WaitUntilAsync(() => remove.IsCompleted, because: "nach Call-Ende muss Remove abschließen");
        _supervisor.GetStatus(id).Should().BeNull();
    }

    [Fact]
    public async Task Remove_with_expired_drain_completes_anyway()
    {
        var id = await _supervisor.AddAsync(TestData.StdioConfig(), CancellationToken.None);
        await TestData.WaitUntilAsync(() => _supervisor.GetStatus(id)?.State == UpstreamState.Healthy);

        _connector.Connections[0].CallGate = new TaskCompletionSource<System.Text.Json.JsonElement>();
        var connection = (GuardedUpstreamConnection)_supervisor.GetConnection(id)!;
        _ = connection.CallToolAsync("echo", TestData.EmptySchema(), CancellationToken.None);
        await TestData.WaitUntilAsync(() => connection.InFlightCount == 1);

        var remove = _supervisor.RemoveAsync(id, DrainPolicy.Graceful(TimeSpan.FromSeconds(2)), CancellationToken.None);
        await Task.Delay(50);
        _time.Advance(TimeSpan.FromSeconds(3));

        await TestData.WaitUntilAsync(() => remove.IsCompleted, because: "abgelaufene Drain-Frist darf Remove nicht blockieren");
        _supervisor.GetStatus(id).Should().BeNull();
    }

    [Fact]
    public async Task SetEnabled_toggles_and_versions_config()
    {
        var id = await _supervisor.AddAsync(TestData.StdioConfig(), CancellationToken.None);
        await TestData.WaitUntilAsync(() => _supervisor.GetStatus(id)?.State == UpstreamState.Healthy);

        await _supervisor.SetEnabledAsync(id, false, CancellationToken.None);
        _supervisor.GetStatus(id)!.State.Should().Be(UpstreamState.Stopped);
        _supervisor.GetConnection(id).Should().BeNull();

        await _supervisor.SetEnabledAsync(id, true, CancellationToken.None);
        await TestData.WaitUntilAsync(() => _supervisor.GetStatus(id)?.State == UpstreamState.Healthy);

        var history = await _store.GetHistoryAsync(id, CancellationToken.None);
        history.Should().HaveCount(3, "Initial + Disable + Enable");
        history[^1].Config.Enabled.Should().BeTrue();
    }

    [Fact]
    public async Task Reconfigure_restarts_with_new_config_and_returns_new_version()
    {
        var id = await _supervisor.AddAsync(TestData.StdioConfig("alt"), CancellationToken.None);
        await TestData.WaitUntilAsync(() => _supervisor.GetStatus(id)?.State == UpstreamState.Healthy);

        var version = await _supervisor.ReconfigureAsync(id, TestData.StdioConfig("neu"), CancellationToken.None);

        version.Should().Be(new ConfigVersionId(2));
        await TestData.WaitUntilAsync(
            () => _supervisor.GetStatus(id)?.State == UpstreamState.Healthy && _connector.LastConfig?.Slug == "neu");
        _connector.Connections[0].DisposeCount.Should().BeGreaterThan(0, "alte Verbindung wird beim Reconfigure geschlossen");
    }

    [Fact]
    public async Task Rollback_restores_previous_config_as_new_version()
    {
        var id = await _supervisor.AddAsync(TestData.StdioConfig("v1"), CancellationToken.None);
        await TestData.WaitUntilAsync(() => _supervisor.GetStatus(id)?.State == UpstreamState.Healthy);
        await _supervisor.ReconfigureAsync(id, TestData.StdioConfig("v2"), CancellationToken.None);
        await TestData.WaitUntilAsync(() => _connector.LastConfig?.Slug == "v2");

        await _supervisor.RollbackAsync(id, new ConfigVersionId(1), CancellationToken.None);

        await TestData.WaitUntilAsync(
            () => _supervisor.GetStatus(id)?.State == UpstreamState.Healthy && _connector.LastConfig?.Slug == "v1");
        var history = await _store.GetHistoryAsync(id, CancellationToken.None);
        history.Should().HaveCount(3);
        history[^1].Config.Slug.Should().Be("v1", "Rollback erzeugt eine neue Version mit altem Inhalt");
    }

    [Fact]
    public async Task Rollback_to_unknown_version_throws()
    {
        var id = await _supervisor.AddAsync(TestData.StdioConfig(), CancellationToken.None);

        var act = () => _supervisor.RollbackAsync(id, new ConfigVersionId(99), CancellationToken.None);

        await act.Should().ThrowAsync<KeyNotFoundException>();
    }

    [Fact]
    public async Task Operations_on_unknown_server_throw()
    {
        var unknown = ServerId.New();

        await ((Func<Task>)(() => _supervisor.RemoveAsync(unknown, DrainPolicy.Immediate, CancellationToken.None)))
            .Should().ThrowAsync<KeyNotFoundException>();
        await ((Func<Task>)(() => _supervisor.SetEnabledAsync(unknown, false, CancellationToken.None)))
            .Should().ThrowAsync<KeyNotFoundException>();
        _supervisor.GetStatus(unknown).Should().BeNull();
    }

    [Fact]
    public async Task Crash_of_one_upstream_does_not_affect_the_other()
    {
        var idA = await _supervisor.AddAsync(TestData.StdioConfig("stable"), CancellationToken.None);
        var idB = await _supervisor.AddAsync(TestData.StdioConfig("flaky"), CancellationToken.None);
        await TestData.WaitUntilAsync(() =>
            _supervisor.GetStatus(idA)?.State == UpstreamState.Healthy &&
            _supervisor.GetStatus(idB)?.State == UpstreamState.Healthy);

        var flakyConnection = _connector.Connections.Single(c => c.Id == idB);
        flakyConnection.FailPing = true;
        await TestData.WaitUntilAdvancingAsync(
            _time,
            () => Events.Any(e =>
                e.Kind == UpstreamChangeKind.StateChanged && e.Server == idB && e.State == UpstreamState.Failed),
            because: "Ping-Fehler von B muss als Failed sichtbar werden");

        _supervisor.GetStatus(idA)!.State.Should().Be(UpstreamState.Healthy, "Fault Isolation: Nachbar bleibt unberührt");
        var stableResult = await _supervisor.GetConnection(idA)!
            .CallToolAsync("echo", TestData.EmptySchema(), CancellationToken.None);
        stableResult.ValueKind.Should().Be(System.Text.Json.JsonValueKind.Object);
    }
}
