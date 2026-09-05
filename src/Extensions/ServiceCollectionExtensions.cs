using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Snail.Toolkit.Nomad.Health;

namespace Snail.Toolkit.Nomad.Extensions;

/// <summary>Registers the Nomad client over IHttpClientFactory.</summary>
public static class ServiceCollectionExtensions
{
    private static readonly TimeSpan DefaultProbeTimeout = TimeSpan.FromSeconds(5);

    extension(IServiceCollection services)
    {
        /// <summary>Binds <see cref="NomadOptions"/> from the "Nomad" section and registers <see cref="INomadClient"/> as a typed HTTP client.</summary>
        public IServiceCollection AddNomad(IConfiguration configuration)
        {
            ArgumentNullException.ThrowIfNull(services);
            ArgumentNullException.ThrowIfNull(configuration);

            var section = configuration.GetSection(NomadOptions.SectionName);
            if (string.IsNullOrWhiteSpace(section[nameof(NomadOptions.Address)]))
                throw new InvalidOperationException(
                    $"Configuration key '{NomadOptions.SectionName}:{nameof(NomadOptions.Address)}' is required.");

            services.AddOptions<NomadOptions>().Bind(section);
            return services.AddNomadClient();
        }

        /// <summary>Configures <see cref="NomadOptions"/> in code and registers <see cref="INomadClient"/> as a typed HTTP client.</summary>
        public IServiceCollection AddNomad(Action<NomadOptions> configure)
        {
            ArgumentNullException.ThrowIfNull(services);
            ArgumentNullException.ThrowIfNull(configure);

            services.AddOptions<NomadOptions>().Configure(configure);
            return services.AddNomadClient();
        }

        /// <summary>Registers a health check that asks the agent for its version and the cluster for its leader; the name is registered once even when called repeatedly.</summary>
        /// <remarks>
        /// The probe carries a budget of its own, five seconds unless <c>timeout</c> says otherwise,
        /// because it makes two calls: without one an unresponsive agent held the whole health
        /// endpoint for twice the client's timeout.
        /// </remarks>
        public IServiceCollection AddNomadHealthCheck(
            string name = "nomad",
            HealthStatus failureStatus = HealthStatus.Unhealthy,
            IEnumerable<string>? tags = null,
            TimeSpan? timeout = null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(name);

            if (services.All(descriptor => descriptor.ServiceType != typeof(INomadClient)))
                throw new InvalidOperationException($"{nameof(AddNomad)} has to be called before {nameof(AddNomadHealthCheck)}.");

            services.AddHealthChecks();
            services.Configure<HealthCheckServiceOptions>(options =>
            {
                if (options.Registrations.Any(registration => registration.Name == name))
                    return;

                options.Registrations.Add(new HealthCheckRegistration(
                    name,
                    provider => new NomadHealthCheck(provider.GetRequiredService<INomadClient>()),
                    failureStatus,
                    tags,
                    timeout ?? DefaultProbeTimeout));
            });

            return services;
        }

        private IServiceCollection AddNomadClient()
        {
            services.AddHttpClient<INomadClient, NomadClient>((provider, client) =>
            {
                var options = provider.GetRequiredService<IOptions<NomadOptions>>().Value;
                if (string.IsNullOrWhiteSpace(options.Address))
                    throw new InvalidOperationException($"{nameof(NomadOptions)}.{nameof(NomadOptions.Address)} is required.");

                client.BaseAddress = new Uri(options.Address.TrimEnd('/') + "/");
                client.Timeout = options.Timeout;
                if (!string.IsNullOrEmpty(options.Token))
                    client.DefaultRequestHeaders.Add("X-Nomad-Token", options.Token);
            });

            return services;
        }
    }
}
