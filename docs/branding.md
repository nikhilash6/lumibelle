# Lumibelle logo

The logo uses the supplied film-frame, flowing ribbon and sparkle identity. The application displays the symbol beside the Lumibelle wordmark, without a tagline or supporting copy. The gold and periwinkle belong to the logo; the application keeps its mauve interaction accent.

## Assets and use

- `src/Lumibelle.UI/wwwroot/branding/lumibelle-mark.svg`: header symbol, reusing the existing favicon's film/ribbon/sparkle vector paths with transparent framing. It stays sharp at browser zoom and different display densities.
- `src/Lumibelle.UI/wwwroot/branding/lumibelle-mark.png`: original transparent full-color symbol, 1561 × 1007, retained for larger illustrative use.
- `src/Lumibelle.UI/wwwroot/branding/lumibelle-wordmark.png`: transparent lettering, 2172 × 724. Rendered as an alpha mask in the header so its color follows `--lumi-text`, falling back to `--ink`. The original letterforms are independent of the UI typeface.
- `src/Lumibelle.UI/wwwroot/favicon.svg`: simplified vector companion for small sizes, on a quiet light tile. Both the web favicon and the existing MAUI app-icon pipeline use this file.

Keep the full-color symbol unchanged between themes. Change only the wordmark color with the surrounding UI. Keep the logo's proportions and its accessible **Lumibelle home** link; the artwork inside the link is decorative. The transparent source canvases include padding, so base sizing and the wordmark mask framing live in `app.css`; compact header dimensions and a -1px optical wordmark offset live in `design-system.css`. The offset was reduced from -2px after visual review.

## AI assistance icon

`Components/AI/AiAssistIcon.razor` is the shared decorative 24px vector icon for AI assistance. It uses the logo's flowing ribbon and four-point sparkle, without the film frame or lettering. Gold and periwinkle colors use theme-specific `--lumi-ai-gold` and `--lumi-ai-ribbon` tokens for contrast; filled primary buttons use their contrasting foreground color.

Use it for AI activity, all `TextRequestAction` entry points, the Script assistant heading, and the six composer submit buttons for extraction, prompt enhancement, guidance, shot planning, shot prompt composition, and reel composition. Script submits through `TextRequestAction`. Keep visible button labels and accessible names; the icon is `aria-hidden`. Running spinners, queued clocks, and attention indicators remain distinct. Assist entry buttons use a selected surface and accent border to improve visibility.

## Validation

The initial web Release build and Windows Debug/MSIX build passed with no warnings or errors. The header was visually checked in the isolated BrowserHost at 1280 × 720 and 390 × 844, and the generated Windows 256px app icon was inspected after the MSIX build regenerated its resources. After switching the header symbol to SVG and reducing the wordmark offset to -1px, the web Debug build passed with no warnings/errors. Read-only checks of the running app confirmed the SVG loads in light/dark at 1× and 2× device scale, the offset is applied, and desktop/phone pages have no horizontal overflow. Header screenshots are retained under `artifacts/polished-header-*.png`.

## Asset provenance

Prepared with the built-in image generation tool from a supplied brand sheet, 2026-09-16. These are generated transparent derivatives, not original vector masters. The small-size SVG is a simplified code-native adaptation. Neither source copy includes a tagline in the application artwork.

### Symbol prompt

Use case: background-extraction. Asset type: production app logo symbol on a truly transparent RGBA background. Edit the supplied Lumibelle brand sheet: extract ONLY the large top symbol, precisely preserving its existing silhouette, film-frame with three left perforations, flowing gold ribbon with pale blue/lavender underside, and the single gold four-point sparkle to the upper-right. No words, no wordmark, no tagline, no app tile, no rounded-square background, no brand sheet layout. Preserve the supplied logo's dark navy, warm gold and pale periwinkle colors and smooth gradients. Remove the paper/white background completely, including the open center, sprocket holes and spaces between ribbons; keep intentional cream highlights belonging to the ribbons. Do not redraw as a different logo. Make a clean large isolated usable logo asset, centered with narrow transparent padding (about 5 percent), wide aspect matching the original mark about 1.55:1. Sharp crisp contours, no added shadow, no glow, no exterior white matte. Transparent background is essential.

### Wordmark prompt

Use case: background-extraction. Asset type: production logo wordmark with actual transparent RGBA background. Extract ONLY the large central word 'Lumibelle' from this supplied brand sheet. Keep the original letterforms EXACTLY: capital L and lowercase umibelle, rounded geometric custom lettering and angled e crossbars. Remove everything else: no film symbol, no ribbons, no sparkle, no tagline, no descriptor, no smaller versions, no tile, no paper. The only visible pixels must be the word Lumibelle. Render the letters as a clean solid very dark navy #252A38; render the i dot in that SAME navy too, because this asset will be used as an alpha mask for theme-adaptive text. Preserve the exact silhouette and proportions of the reference large wordmark, not a newly chosen font. Wide horizontal composition about 5.4:1 with tight consistent 3 percent transparent padding. Clean crisp antialiased edges, no bevel, no shadow, no glow, no white rectangle. All letter counters and outside areas genuinely transparent. Text verbatim: 'Lumibelle'. No other text.
