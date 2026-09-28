# Live composition evaluation

This manually invoked utility reads existing shots and exact image crops, calls the project's configured Production model (or its Shots model), and validates responses with the application composition contract. It currently supports the Codex provider. It does not generate videos, enqueue application jobs, accept prompts or edit project data.

The request sends private screenplay context, reference crops and guidance to the configured Codex service. Run it only when that use is authorized. Choose a new output folder; existing evaluations are never overwritten.

```powershell
dotnet run --project tools/production_smoke -c Release -- . PROJECT_GUID artifacts/production-live-YYYYMMDD 0 6
```

The final arguments are zero-based source-shot indices. Outputs include the request, exact inspection PNGs, raw response, validated prompt and reference-use explanation, or a validation error. Review the images alongside the prompt to assess meaningful use of visible details, conflicting roles, appearance transitions, camera staging and story preservation. Report those qualitative findings separately from the mocked test results.

Revalidate captured responses after a validator change without reading project settings or making any model calls:

```powershell
dotnet run --project tools/production_smoke -c Release -- --revalidate artifacts/production-live-YYYYMMDD artifacts/production-live-YYYYMMDD-validated
```

This writes prompts, reference-use explanations or validation errors to a new directory, preserving the original requests, images and responses.
