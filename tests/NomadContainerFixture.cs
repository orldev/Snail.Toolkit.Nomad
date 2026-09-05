using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;

namespace Snail.Toolkit.Nomad.Tests;

/// <summary>
/// Starts one server-only Nomad agent lazily and shares it across the tests of a class. A server without
/// clients accepts and schedules jobs (they stay queued), which is enough to exercise the whole job API.
/// </summary>
public sealed class NomadContainerFixture : IAsyncLifetime
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IContainer? _container;

    public async Task<string> GetAddressAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (_container is null)
            {
                _container = new ContainerBuilder("hashicorp/nomad:latest")
                    .WithCommand("agent", "-server", "-bootstrap-expect=1", "-data-dir=/tmp/nomad", "-bind=0.0.0.0")
                    .WithPortBinding(4646, true)
                    .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(request =>
                        request.ForPort(4646).ForPath("/v1/status/leader")
                            .ForResponseMessageMatching(async response => (await response.Content.ReadAsStringAsync()).Trim('"', '\n').Length > 0)))
                    .Build();
                await _container.StartAsync();
            }

            return $"http://{_container.Hostname}:{_container.GetMappedPublicPort(4646)}";
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        if (_container is not null)
            await _container.DisposeAsync();
    }
}
