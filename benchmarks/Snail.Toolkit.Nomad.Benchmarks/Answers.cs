using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.Extensions.Options;

namespace Snail.Toolkit.Nomad.Benchmarks;

/// <summary>What a Nomad agent answers, made up front; the client reads it from memory, never from a socket.</summary>
public static class Answers
{
    public static NomadClient Client(byte[] body, string mediaType)
    {
        var http = new HttpClient(new Canned(body, mediaType)) { BaseAddress = new Uri("http://nomad.local:4646/") };

        return new NomadClient(http, Options.Create(new NomadOptions { Address = "http://nomad.local:4646" }));
    }

    public static byte[] Jobs(int count)
    {
        var text = new StringBuilder("[");
        for (var i = 0; i < count; i++)
        {
            if (i > 0)
                text.Append(',');

            text.Append($"{{\"ID\":\"service-{i:D4}\",\"ParentID\":\"\",\"Name\":\"service-{i:D4}\",\"Namespace\":\"default\",\"Datacenters\":[\"dc1\",\"dc2\"],\"NodePool\":\"default\",\"Type\":\"service\",\"Priority\":50,\"Periodic\":false,\"ParameterizedJob\":false,\"Stop\":false,\"Status\":\"running\",\"StatusDescription\":\"\",")
                .Append($"\"JobSummary\":{{\"JobID\":\"service-{i:D4}\",\"Namespace\":\"default\",\"Summary\":{{\"web\":{{\"Queued\":0,\"Complete\":{i % 5},\"Failed\":0,\"Running\":3,\"Starting\":0,\"Lost\":0,\"Unknown\":0}}}},\"Children\":{{\"Pending\":0,\"Running\":0,\"Dead\":0}},\"CreateIndex\":{100 + i},\"ModifyIndex\":{200 + i}}},")
                .Append($"\"CreateIndex\":{100 + i},\"ModifyIndex\":{200 + i},\"JobModifyIndex\":{150 + i},\"SubmitTime\":{1760000000000000000L + (i * 1000000000L)}}}");
        }

        return Encoding.UTF8.GetBytes(text.Append(']').ToString());
    }

    public static byte[] Log(int bytes)
    {
        var line = "2026-10-12T10:00:00.123Z INFO  request handled path=/api/orders/42 status=200 elapsed=3.2ms\n"u8.ToArray();
        var log = new byte[bytes];
        for (var i = 0; i < bytes; i++)
            log[i] = line[i % line.Length];

        return log;
    }

    public static Dictionary<string, object?> Variables() => new()
    {
        ["image"] = "registry.example.com/team/web:2026.10.12-rc1",
        ["count"] = 3,
        ["cpu"] = 500,
        ["memory"] = 512,
        ["enabled"] = true,
        ["datacenters"] = new[] { "dc1", "dc2", "dc3" },
        ["ratio"] = 0.75,
        ["env"] = new Dictionary<string, object?> { ["LOG_LEVEL"] = "info", ["FEATURE_FLAGS"] = "a,b,c", ["GREETING"] = "Hello ${name}, \"quoted\"\n" },
        ["description"] = "A long description of the service that runs the public web front end of the shop and talks to the order API.",
        ["ports"] = new[] { 8080, 8443 },
        ["region"] = "eu-west",
        ["owner"] = "team-web",
        ["tier"] = "frontend",
        ["version"] = "2026.10.12",
        ["template"] = "{{ with secret \"kv/data/web\" }}{{ .Data.data.password }}{{ end }} %{ if true }x%{ endif }"
    };

    /// <summary>Answers every request with the same body, a fresh response each time as a socket would give.</summary>
    private sealed class Canned(byte[] body, string mediaType) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var content = new ByteArrayContent(body);
            content.Headers.ContentType = new MediaTypeHeaderValue(mediaType) { CharSet = "utf-8" };

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content, RequestMessage = request });
        }
    }
}
