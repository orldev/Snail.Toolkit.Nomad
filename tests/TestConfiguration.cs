using Microsoft.Extensions.Configuration;

namespace Snail.Toolkit.Nomad.Tests;

public static class TestConfiguration
{
    public static IConfiguration Get(string? address = "http://localhost:4646", string? token = null, string? ns = null)
    {
        var settings = new Dictionary<string, string?>
        {
            [$"{NomadOptions.SectionName}:Address"] = address,
            [$"{NomadOptions.SectionName}:Token"] = token,
            [$"{NomadOptions.SectionName}:Namespace"] = ns
        };

        return new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
    }
}
