using System.Text.Json;
using FluentAssertions;
using McpMcp.Abstractions;
using McpMcp.Core.Upstreams;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace McpMcp.Core.Tests.Upstreams;

public class GuardedUpstreamConnectionTests
{
    private readonly FakeTimeProvider _time = new();

    [Fact]
    public async Task Call_passes_through_and_tracks_inflight()
    {
        var inner = new FakeUpstreamConnection { Id = ServerId.New() };
        var gate = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        inner.CallGate = gate;
        var guarded = new GuardedUpstreamConnection(inner, TimeSpan.FromSeconds(30), _time);

        var call = guarded.CallToolAsync("echo", TestData.EmptySchema(), CancellationToken.None);
        await TestData.WaitUntilAsync(() => guarded.InFlightCount == 1);

        gate.TrySetResult(JsonSerializer.SerializeToElement(new { ok = true }));
        var result = await call;

        result.GetProperty("ok").GetBoolean().Should().BeTrue();
        guarded.InFlightCount.Should().Be(0);
    }

    [Fact]
    public async Task Call_exceeding_timeout_throws_TimeoutException_and_resets_inflight()
    {
        var inner = new FakeUpstreamConnection
        {
            Id = ServerId.New(),
            CallGate = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously),
        };
        var guarded = new GuardedUpstreamConnection(inner, TimeSpan.FromSeconds(1), _time);

        var call = guarded.CallToolAsync("hang", TestData.EmptySchema(), CancellationToken.None);
        await TestData.WaitUntilAsync(() => guarded.InFlightCount == 1);
        _time.Advance(TimeSpan.FromSeconds(2));

        await ((Func<Task>)(() => call)).Should().ThrowAsync<TimeoutException>();
        guarded.InFlightCount.Should().Be(0);
    }

    [Fact]
    public async Task Caller_cancellation_surfaces_as_cancellation_not_timeout()
    {
        var inner = new FakeUpstreamConnection
        {
            Id = ServerId.New(),
            CallGate = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously),
        };
        var guarded = new GuardedUpstreamConnection(inner, TimeSpan.FromSeconds(30), _time);
        using var cts = new CancellationTokenSource();

        var call = guarded.CallToolAsync("hang", TestData.EmptySchema(), cts.Token);
        await TestData.WaitUntilAsync(() => guarded.InFlightCount == 1);
        await cts.CancelAsync();

        await ((Func<Task>)(() => call)).Should().ThrowAsync<OperationCanceledException>();
        guarded.InFlightCount.Should().Be(0);
    }

    [Fact]
    public async Task Ping_timeout_throws_TimeoutException()
    {
        var inner = new HangingPingConnection { Id = ServerId.New() };
        var guarded = new GuardedUpstreamConnection(inner, TimeSpan.FromSeconds(1), _time);

        var ping = guarded.PingAsync(CancellationToken.None);
        _time.Advance(TimeSpan.FromSeconds(2));

        await ((Func<Task>)(() => ping)).Should().ThrowAsync<TimeoutException>();
    }

    [Fact]
    public async Task WaitForIdle_returns_true_immediately_when_idle()
    {
        var guarded = new GuardedUpstreamConnection(
            new FakeUpstreamConnection { Id = ServerId.New() }, TimeSpan.FromSeconds(30), _time);

        (await guarded.WaitForIdleAsync(TimeSpan.FromSeconds(1), CancellationToken.None)).Should().BeTrue();
    }

    [Fact]
    public async Task WaitForIdle_returns_false_when_grace_expires_with_busy_call()
    {
        var inner = new FakeUpstreamConnection
        {
            Id = ServerId.New(),
            CallGate = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously),
        };
        var guarded = new GuardedUpstreamConnection(inner, TimeSpan.FromMinutes(5), _time);
        _ = guarded.CallToolAsync("hang", TestData.EmptySchema(), CancellationToken.None);
        await TestData.WaitUntilAsync(() => guarded.InFlightCount == 1);

        var wait = guarded.WaitForIdleAsync(TimeSpan.FromSeconds(1), CancellationToken.None);
        _time.Advance(TimeSpan.FromSeconds(2));

        (await wait).Should().BeFalse();
    }

    private sealed class HangingPingConnection : IUpstreamConnection
    {
        public ServerId Id { get; init; }

        public event EventHandler<UpstreamNotificationEventArgs>? NotificationReceived
        {
            add { }
            remove { }
        }

        public Task<UpstreamInventory> DiscoverAsync(CancellationToken ct)
            => Task.FromResult(TestData.InventoryWithTools());

        public Task<JsonElement> CallToolAsync(string toolName, JsonElement args, CancellationToken ct)
            => Task.FromResult(TestData.EmptySchema());

        public async Task PingAsync(CancellationToken ct) => await Task.Delay(Timeout.InfiniteTimeSpan, ct);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
