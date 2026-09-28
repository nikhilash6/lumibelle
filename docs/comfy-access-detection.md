# Access-aware ComfyUI connection check

The normal ComfyUI connection card is the entry point. Its Check connection action runs
model discovery followed by the existing HTTP/WebSocket probe. A typed Access failure
can open the token editor once per explicit check. The manual Authentication button
uses the same editor without asserting that Access was detected.

`ComfyAccessResponses` classifies headers/status/Location without touching content.
It distinguishes Access login routes, a rejected saved Access pair, generic HTTP
redirects/denials/HTML and Cloudflare browser challenges. Vendor CDN headers alone are
not sufficient. Login destinations are diagnostic input only; credentials are always
bound to the originally configured HTTPS origin.

`ComfyAccessEditor` is rendered inside the page's MudDialog. It fetches only credential
metadata, pins the original destination, and never reloads plaintext credentials.
Save connection and retry first persists/reuses the token, then invokes the parent
page's existing revision-checked settings write to save the selected URL. After a
successful save it closes and retries once with automatic prompting disabled.
Partial writes have explicit saved-token/failed-URL feedback and a retry path. Existing
credential-store and general AI-settings formats are unchanged.

`ComfyAccessException` includes typed failure information. The prompt submission helper
only releases durable intent for local pre-dispatch credential failure or an HTTP
401/403 rejection. It never uses the UI's login-detection heuristic as proof that a
possibly accepted generation is safe to repeat. History/download authentication failure
retains the existing recovery behavior. The direct monitor also handles an Access-denied
remote cancellation as unconfirmed, completing its local output stream.

New regression suites:

- `ComfyAccessDetectionTests`: positive/negative detection, malicious or unrelated
  redirect hosts, WAF distinction, safe diagnostics, actual handler and WebSocket probe.
- `ComfyAccessModalTests`: normal check to modal to save/retry using the real registry
  and authentication handler; cancellation, no retry loop, partial saves, rotation,
  blank-secret preservation, removal confirmation and pinned destination.
- `ComfyAccessLifecycleTests`: durable submission rejection/admission, ambiguous receipt
  preservation, accepted-job polling/recovery and direct-monitor cancellation completion.

The existing Access transport/storage/page suites remain applicable. No production
credential or Cloudflare account is necessary for automated tests. Build/test execution
status for the overwrite package is recorded in `COMFY_ACCESS_MODAL_BUNDLE.md`.

Primary references for response conventions and authentication:

- https://developers.cloudflare.com/cloudflare-one/access-controls/authenticate-agents/
- https://developers.cloudflare.com/cloudflare-one/access-controls/service-credentials/service-tokens/
- https://developers.cloudflare.com/cloudflare-challenges/challenge-types/challenge-pages/detect-response/
- https://github.com/cloudflare/cloudflared/issues/1637 (example Access login Location)
- https://bunit.dev/docs/migrations/1to2.html (Render API used by the new tests)
