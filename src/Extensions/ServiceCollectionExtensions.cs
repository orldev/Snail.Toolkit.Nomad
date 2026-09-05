using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Snail.Toolkit.Nomad.Health;

namespace Snail.Toolkit.Nomad.Extensions;

/// <summary>Registers the Nomad client over IHttpClientFactory.</summary>
public static class ServiceCollectionExtensions
{
    private static readonly TimeSpan DefaultProbeTimeout = TimeSpan.FromSeconds(5);

    /// <summary>The one place the settings are judged, whichever overload supplied them.</summary>
    /// <remarks>
    /// Bound and coded settings used to be checked differently and at different moments — eagerly
    /// against the raw section in one overload, lazily inside the client factory in the other, and
    /// not at all in between. Validation runs at start instead, so a bad address stops the host
    /// rather than the first call.
    /// </remarks>
    private static void Validated(OptionsBuilder<NomadOptions> builder) =>
        builder
            .Validate(
                settings => Uri.TryCreate(settings.Address, UriKind.Absolute, out var address)
                            && (address.Scheme == Uri.UriSchemeHttp || address.Scheme == Uri.UriSchemeHttps),
                $"'{NomadOptions.SectionName}:{nameof(NomadOptions.Address)}' has to be an absolute http or https address.")
            .Validate(
                settings => settings.Timeout > TimeSpan.Zero,
                $"'{NomadOptions.SectionName}:{nameof(NomadOptions.Timeout)}' has to be positive.")
            .ValidateOnStart();

    extension(IServiceCollection services)
    {
        /// <summary>Binds <see cref="NomadOptions"/> from the "Nomad" section and registers <see cref="INomadClient"/> as a typed HTTP client.</summary>
        public IServiceCollection AddNomad(IConfiguration configuration)
        {
            ArgumentNullException.ThrowIfNull(services);
            ArgumentNullException.ThrowIfNull(configuration);

            Validated(services.AddOptions<NomadOptions>().Bind(configuration.GetSection(NomadOptions.SectionName)));

            return services.AddNomadClient();
        }

        /// <summary>Configures <see cref="NomadOptions"/> in code and registers <see cref="INomadClient"/> as a typed HTTP client.</summary>
        public IServiceCollection AddNomad(Action<NomadOptions> configure)
        {
            ArgumentNullException.ThrowIfNull(services);
            ArgumentNullException.ThrowIfNull(configure);

            Validated(services.AddOptions<NomadOptions>().Configure(configure));

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

        /// <remarks>
        /// Each half of the contract is forwarded to the same typed client, so a caller that only
        /// reads logs or only lists nodes injects that half instead of the whole of Nomad.
        /// </remarks>
        private IServiceCollection AddNomadClient()
        {
            services.AddHttpClient<INomadClient, NomadClient>((provider, client) =>
            {
                var options = provider.GetRequiredService<IOptions<NomadOptions>>().Value;

                client.BaseAddress = new Uri(options.Address.TrimEnd('/') + "/");
                client.Timeout = options.Timeout;
                if (!string.IsNullOrEmpty(options.Token))
                    client.DefaultRequestHeaders.Add("X-Nomad-Token", options.Token);
            });

            services.TryAddTransient<INomadStatus>(provider => provider.GetRequiredService<INomadClient>());
            services.TryAddTransient<INomadJobs>(provider => provider.GetRequiredService<INomadClient>());
            services.TryAddTransient<INomadScheduling>(provider => provider.GetRequiredService<INomadClient>());
            services.TryAddTransient<INomadNodes>(provider => provider.GetRequiredService<INomadClient>());
            services.TryAddTransient<INomadLogs>(provider => provider.GetRequiredService<INomadClient>());

            return services;
        }
    }
}
