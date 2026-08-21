namespace WinTube.Core.Tests;

/// Test double for HttpClient. Reads each request body up front (the content is disposed
/// after SendAsync returns) and hands it to the responder alongside the message.
public sealed class StubHttpHandler(Func<HttpRequestMessage, string, HttpResponseMessage> respond)
    : HttpMessageHandler
{
    public List<(HttpRequestMessage Message, string Body)> Requests { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null
            ? ""
            : await request.Content.ReadAsStringAsync(cancellationToken);
        Requests.Add((request, body));
        return respond(request, body);
    }

    public static HttpResponseMessage JsonResponse(string json, int status = 200) => new()
    {
        StatusCode = (System.Net.HttpStatusCode)status,
        Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
    };
}
