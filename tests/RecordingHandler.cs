using System.Net;
using System.Text;

namespace Snail.Toolkit.Nomad.Tests;

/// <summary>Answers every request with a canned response and keeps what was sent.</summary>
internal sealed class RecordingHandler(HttpStatusCode status, string body) : HttpMessageHandler
{
    public List<HttpRequestMessage> Requests { get; } = [];
    public List<string> Bodies { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        Bodies.Add(request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken));
        return new HttpResponseMessage(status)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
    }

    public static NomadClient Client(RecordingHandler handler, Action<NomadOptions>? configure = null)
    {
        var options = new NomadOptions { Address = "http://nomad.test:4646" };
        configure?.Invoke(options);

        var http = new HttpClient(handler) { BaseAddress = new Uri(options.Address + "/") };
        if (options.Token is not null)
            http.DefaultRequestHeaders.Add("X-Nomad-Token", options.Token);

        return new NomadClient(http, Microsoft.Extensions.Options.Options.Create(options));
    }
}
