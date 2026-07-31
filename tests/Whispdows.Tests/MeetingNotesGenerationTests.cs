using System.Net;
using System.Text;
using System.Text.Json;
using Whispdows;
using Xunit;

namespace Whispdows.Tests;

public sealed class MeetingNotesGenerationTests
{
    private const string ValidNotesJson =
        """
        {
          "summary": ["One", "Two", "Three", "Four", "Five"],
          "decisions": ["Ship on Friday"],
          "actionItems": [{"owner": "Alex", "task": "Publish the release"}]
        }
        """;

    [Fact]
    public void Parser_requires_exactly_five_summary_bullets()
    {
        var exception = Assert.Throws<MeetingNotesGenerationException>(
            () => MeetingNotesResponseParser.Parse(
                """
                {
                  "summary": ["One"],
                  "decisions": [],
                  "actionItems": []
                }
                """));

        Assert.Contains("exactly five", exception.Message);
    }

    [Fact]
    public void Parser_accepts_json_code_fences_and_normalizes_unknown_owners()
    {
        var notes = MeetingNotesResponseParser.Parse(
            "```json\n" +
            """
            {
              "summary": ["One", "Two", "Three", "Four", "Five"],
              "decisions": [],
              "actionItems": [{"owner": "", "task": "Follow up"}]
            }
            """ +
            "\n```");

        Assert.Equal(5, notes.Summary.Count);
        Assert.Empty(notes.Decisions);
        Assert.Equal("Unassigned", notes.ActionItems[0].Owner);
    }

    [Fact]
    public async Task Openai_compatible_generator_sends_structured_prompt()
    {
        var handler = new RecordingHandler(
            JsonSerializer.Serialize(new
            {
                choices = new[]
                {
                    new
                    {
                        message = new { content = ValidNotesJson }
                    }
                }
            }));
        using var client = new HttpClient(handler);
        using var generator = new OpenAiCompatibleMeetingNotesGenerator(
            CloudProviderDefinition.Create(
                "openai",
                new ProviderSecrets(new Dictionary<string, string>
                {
                    ["OPENAI_API_KEY"] = "secret"
                })),
            "gpt-test",
            client);

        var notes = await generator.GenerateAsync(
            "Alex agreed to publish the release on Friday.",
            CancellationToken.None);

        Assert.Equal("Ship on Friday", Assert.Single(notes.Decisions));
        Assert.Equal("Alex", Assert.Single(notes.ActionItems).Owner);
        Assert.Equal(
            "https://api.openai.com/v1/chat/completions",
            handler.Uri?.AbsoluteUri);
        Assert.Equal("Bearer", handler.AuthorizationScheme);
        Assert.Equal("secret", handler.AuthorizationParameter);
        Assert.Contains("exactly five", handler.Body);
        Assert.Contains("Alex agreed", handler.Body);
        Assert.Contains("\"type\":\"json_object\"", handler.Body);
    }

    private sealed class RecordingHandler(string responseJson) : HttpMessageHandler
    {
        public Uri? Uri { get; private set; }

        public string? AuthorizationScheme { get; private set; }

        public string? AuthorizationParameter { get; private set; }

        public string Body { get; private set; } = string.Empty;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Uri = request.RequestUri;
            AuthorizationScheme = request.Headers.Authorization?.Scheme;
            AuthorizationParameter = request.Headers.Authorization?.Parameter;
            Body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    responseJson,
                    Encoding.UTF8,
                    "application/json")
            };
        }
    }
}
