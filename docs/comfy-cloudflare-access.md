# ComfyUI through Cloudflare Access

## Architecture

`AddLumibelleCore` calls `AddComfyAccess`. That registers an installation-level credential store, the named `ComfyUI` HTTP pipeline, `AccessComfyWebSocketFactory`, and a side-effect-free connectivity probe. There are no new external packages or changes to generation/snapshot schemas.

`FileComfyAccessCredentialStore` protects the Client ID and Secret together with their canonical HTTPS origin. Metadata returned to the UI contains only origin, update time, and the file revision. Writes use the existing atomic JSON publication and per-path lock. Optimistic revision checks stop a stale settings window from overwriting another window's token rotation. The protected payload repeats the origin so editing the outer JSON cannot redirect an encrypted credential to another host. Unrelated records are preserved without decrypting them during a save.

`ComfyAccessHandler` resolves credentials for each outgoing request, removes any pre-existing Access headers, attaches the exact-origin pair, and removes the temporary pair again when the send completes or fails. It never puts credentials in `DefaultRequestHeaders`. The production `SocketsHttpHandler` has redirects and cookies disabled and retains normal certificate validation. Named-client HTTP header logging is redacted. Access-style redirects, 401/403 responses, and HTML login pages become fixed diagnostic messages rather than provider HTML or redirected requests. Other ComfyUI API errors retain their existing handling.

`AccessComfyWebSocketFactory` supplies the same named HTTP client to the real `ClientWebSocket.ConnectAsync(Uri, HttpMessageInvoker, CancellationToken)` overload. This is deliberate: setting headers on an otherwise independent socket would not inherit the HTTP client's redirect/authentication policy. No browser JavaScript WebSocket or persistent browser authentication storage is involved.

`ComfyAccessProbe` requests `system_stats`, bounds its diagnostic document to 1 MiB, checks the expected JSON shape, and then attempts `/ws` with a disposable client identity. It does not upload media, enqueue workflows, or call interrupt. HTTP and socket outcomes are reported independently. Cancellation from the page is propagated rather than reported as invalid credentials.

The settings UI is a separate page linked from the existing ComfyUI connection card, avoiding any addition of secrets to the generation settings model. Credentials are saved separately; the regular connection form remains the owner of the selected ComfyUI URL. The token form never fetches plaintext stored credentials and clears entered values after a successful save or an explicit origin selection. Both values are required to replace a pair.

## Test map

- `ComfyAccessCredentialTests.cs`: origin canonicalization/isolation, input validation, encrypted persistence, non-serialization, stale writes, rotation, removal, bound-origin tampering, corruption, and key-ring failure.
- `ComfyAccessTransportTests.cs`: authentication on different API operations, original request-body preservation, independent concurrent origins, current credentials on a reused client, safe errors and disposal, streaming, cancellation, actual production registration policy, and real .NET WebSocket negotiation over a synthetic upgrade response.
- `ComfyAccessProbeTests.cs`: HTTP/socket success and failure separation, bounded diagnostics, non-Comfy JSON, caller cancellation, and a no-generation request sequence.
- `ComfyAccessPageTests.cs`: blank secret fields on reopening, successful save/clear, independent generation URL, stale-save recovery, explicit removal confirmation, and saved-versus-unsaved credentials during checking.

Tests do not require Cloudflare credentials or Internet access. See the bundle guide for the compilation/execution status and commands. Production TLS and Cloudflare behavior still require a live smoke test.

## Operating notes

Use one Access service token per trusted installation when independent revocation is needed. Tokens are sent on each HTTP request and WebSocket handshake; no JWT/cookie exchange is required by this integration. Rotating the stored pair affects subsequent requests and reconnects, not an already-open socket. Revoking/removing credentials does not cancel a generation already accepted by ComfyUI.

The origin is the credential boundary, not a URL path. Use separate hostnames for services requiring different tokens. Reusing an origin for an unrelated service is an administrative trust change. Store only ComfyUI service tokens here, and protect access to the Lumibelle installation itself. Credential ciphertext and key rings should not be checked into source control or distributed with project packages.

The supported token value check deliberately accepts opaque printable ASCII rather than requiring a fixed hexadecimal length. Cloudflare supports both its older secrets and the newer prefixed format. Browser SSO/interactive login and Cloudflare account-management API tokens are different mechanisms and are not implemented here.

## Primary references

- Cloudflare Access service tokens and headers: https://developers.cloudflare.com/cloudflare-one/access-controls/service-credentials/service-tokens/
- Service Auth policy for noninteractive clients: https://developers.cloudflare.com/cloudflare-one/access-controls/authenticate-agents/
- .NET custom WebSocket invoker overload: https://learn.microsoft.com/en-us/dotnet/api/system.net.websockets.clientwebsocket.connectasync?view=net-10.0
- .NET redirect behavior and custom headers: https://learn.microsoft.com/en-us/dotnet/api/system.net.http.socketshttphandler.allowautoredirect?view=net-10.0
- .NET implementation uses ResponseHeadersRead when a supplied WebSocket invoker is an HttpClient: https://github.com/dotnet/runtime/blob/v10.0.0/src/libraries/System.Net.WebSockets.Client/src/System/Net/WebSockets/WebSocketHandle.Managed.cs
