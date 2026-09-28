namespace lumibelle.Services.AI;

public enum ComfyAccessFailureKind
{
    Unknown,
    AccessLogin,
    AccessRejected,
    CredentialsUnavailable,
    AuthenticationRejected,
    Redirect,
    UnexpectedHtml,
    BrowserChallenge
}

public sealed class ComfyAccessException(string message) : AiGenerationException(message)
{
    public ComfyAccessFailureKind Kind { get; init; }
    public int? HttpStatus { get; init; }
    public bool CredentialsWereSent { get; init; }
    public bool RequestWasNotSent { get; init; }
    public bool HttpRejected => HttpStatus is 401 or 403;
    public bool NeedsCredentials => ComfyAccessResponses.CanPrompt(Kind);

    // UI detection is a heuristic; it is deliberately NOT the evidence used to
    // release a durable submission. Match the pre-existing definitive 4xx rule.
    public bool DefinitelyNotSubmitted => RequestWasNotSent || HttpStatus is 401 or 403;
}

public static class ComfyAccessResponses
{
    public static bool CanPrompt(ComfyAccessFailureKind kind) => kind is
        ComfyAccessFailureKind.AccessLogin or ComfyAccessFailureKind.AccessRejected or ComfyAccessFailureKind.CredentialsUnavailable;

    // No response-body reads, redirect follows, token values or Location query
    // strings. A CDN's Server/CF-Ray header alone is not evidence of Access.
    public static ComfyAccessException? Classify(HttpResponseMessage response, Uri endpoint, bool credentialsSent)
    {
        var status = (int)response.StatusCode;
        var type = response.Content.Headers.ContentType?.MediaType;
        var kind = ComfyAccessFailureKind.Unknown;
        string? message = null;
        var redirect = status is >= 300 and <= 399 && status != 304;

        if (response.Headers.TryGetValues("cf-mitigated", out var mitigated) &&
            mitigated.Any(v => v.Equals("challenge", StringComparison.OrdinalIgnoreCase)))
        {
            kind = ComfyAccessFailureKind.BrowserChallenge;
            message = "Cloudflare returned a browser/bot challenge, not an Access sign-in request. Review the WAF or bot rules for this API; an Access service token alone may not solve this challenge.";
        }
        else if ((redirect || status is 401 or 403) && IsAccessLogin(response, endpoint))
        {
            kind = ComfyAccessFailureKind.AccessLogin;
            message = credentialsSent
                ? "Cloudflare Access still requires sign-in although saved service-token credentials were attached. Check token expiry and the application's Service Auth policy, or replace the token."
                : "Cloudflare Access sign-in was detected. No saved service-token credentials were attached for this server.";
        }
        else if (redirect)
        {
            kind = ComfyAccessFailureKind.Redirect;
            message = "ComfyUI redirected the request. Use its final HTTPS URL. This was not recognized as a Cloudflare Access login; redirects are not followed. Authentication can be configured manually.";
        }
        else if (status is 401 or 403)
        {
            kind = credentialsSent ? ComfyAccessFailureKind.AccessRejected : ComfyAccessFailureKind.AuthenticationRejected;
            message = credentialsSent
                ? "ComfyUI or Cloudflare rejected the saved Access credentials. Check the Client ID/Secret, expiry and Service Auth policy."
                : "ComfyUI denied access. The response alone does not identify Cloudflare Access. Use Authentication to configure a service token when this server is protected by Access.";
        }
        else if (type is not null && (type.Equals("text/html", StringComparison.OrdinalIgnoreCase) ||
            type.Equals("application/xhtml+xml", StringComparison.OrdinalIgnoreCase)))
        {
            kind = ComfyAccessFailureKind.UnexpectedHtml;
            message = "ComfyUI returned HTML instead of an API response. Check the server URL. Cloudflare Access cannot be identified from the content type alone; use Authentication if needed.";
        }

        return message is null ? null : new ComfyAccessException(message + " Endpoint: " + EndpointName(endpoint) + ".")
        {
            Kind = kind,
            HttpStatus = status,
            CredentialsWereSent = credentialsSent
        };
    }

    private static bool IsAccessLogin(HttpResponseMessage response, Uri endpoint)
    {
        var location = response.Headers.Location;
        if (location is null || !Uri.TryCreate(endpoint, location, out var target) ||
            target.Scheme != Uri.UriSchemeHttps || target.UserInfo.Length != 0)
            return false;
        const string login = "/cdn-cgi/access/login";
        if (target.AbsolutePath != login && !target.AbsolutePath.StartsWith(login + "/", StringComparison.Ordinal))
            return false;
        // The standard team login host, with a label boundary (not a substring).
        if (target.IdnHost.EndsWith(".cloudflareaccess.com", StringComparison.OrdinalIgnoreCase)) return true;
        // A same-origin/custom login needs the Access path AND a Cloudflare signal.
        return endpoint.Scheme is "https" or "wss" && target.IdnHost.Equals(endpoint.IdnHost, StringComparison.OrdinalIgnoreCase) &&
            target.Port == endpoint.Port && (response.Headers.Contains("CF-Ray") ||
            response.Headers.Server.Any(s => s.Product?.Name.Equals("cloudflare", StringComparison.OrdinalIgnoreCase) == true));
    }

    private static string EndpointName(Uri endpoint)
    {
        // Fixed labels only: even the original request path/query might contain
        // private identifiers. Never echo an arbitrary redirect or request URL.
        var parts = endpoint.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        foreach (var name in new[] { "object_info", "system_stats", "prompt", "history", "view", "ws", "queue", "interrupt", "upload" })
            if (parts.Contains(name, StringComparer.Ordinal)) return "/" + name;
        return "ComfyUI API";
    }
}
