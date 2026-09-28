using Ganss.Xss;
using Markdig;

namespace lumibelle.Services.Story;

public sealed class MarkdownRenderer
{
    private readonly MarkdownPipeline _pipeline = new MarkdownPipelineBuilder().DisableHtml().Build();
    private readonly HtmlSanitizer _sanitizer = new(new HtmlSanitizerOptions
    {
        AllowedTags = new HashSet<string> { "p", "br", "h1", "h2", "h3", "h4", "h5", "h6", "strong", "em", "blockquote", "ul", "ol", "li", "pre", "code", "hr", "a" },
        AllowedAttributes = new HashSet<string> { "href", "title" },
        AllowedSchemes = new HashSet<string> { "http", "https", "mailto" },
        UriAttributes = new HashSet<string> { "href" }
    });
    public string Render(string markdown) => _sanitizer.Sanitize(Markdown.ToHtml(markdown, _pipeline));
}
