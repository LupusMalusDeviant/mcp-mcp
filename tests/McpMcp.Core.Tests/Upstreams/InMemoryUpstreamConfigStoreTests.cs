using FluentAssertions;
using McpMcp.Abstractions;
using McpMcp.Core.Upstreams;
using Xunit;

namespace McpMcp.Core.Tests.Upstreams;

public class InMemoryUpstreamConfigStoreTests
{
    private readonly InMemoryUpstreamConfigStore _store = new();
    private readonly ServerId _id = ServerId.New();

    [Fact]
    public async Task Append_assigns_sequential_versions()
    {
        var v1 = await _store.AppendVersionAsync(_id, TestData.StdioConfig("a"), CancellationToken.None);
        var v2 = await _store.AppendVersionAsync(_id, TestData.StdioConfig("b"), CancellationToken.None);

        v1.Should().Be(new ConfigVersionId(1));
        v2.Should().Be(new ConfigVersionId(2));
    }

    [Fact]
    public async Task GetVersion_returns_exact_config_or_null()
    {
        await _store.AppendVersionAsync(_id, TestData.StdioConfig("a"), CancellationToken.None);
        await _store.AppendVersionAsync(_id, TestData.StdioConfig("b"), CancellationToken.None);

        (await _store.GetVersionAsync(_id, new ConfigVersionId(1), CancellationToken.None))!.Slug.Should().Be("a");
        (await _store.GetVersionAsync(_id, new ConfigVersionId(3), CancellationToken.None)).Should().BeNull();
        (await _store.GetVersionAsync(ServerId.New(), new ConfigVersionId(1), CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task History_is_ordered_and_isolated_per_server()
    {
        var other = ServerId.New();
        await _store.AppendVersionAsync(_id, TestData.StdioConfig("a"), CancellationToken.None);
        await _store.AppendVersionAsync(other, TestData.StdioConfig("x"), CancellationToken.None);
        await _store.AppendVersionAsync(_id, TestData.StdioConfig("b"), CancellationToken.None);

        var history = await _store.GetHistoryAsync(_id, CancellationToken.None);

        history.Select(h => h.Config.Slug).Should().ContainInOrder("a", "b");
        (await _store.GetHistoryAsync(other, CancellationToken.None)).Should().HaveCount(1);
    }

    [Fact]
    public async Task Remove_clears_history()
    {
        await _store.AppendVersionAsync(_id, TestData.StdioConfig(), CancellationToken.None);

        await _store.RemoveAsync(_id, CancellationToken.None);

        (await _store.GetHistoryAsync(_id, CancellationToken.None)).Should().BeEmpty();
    }
}
