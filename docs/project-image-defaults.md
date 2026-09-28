# Project image models

Each project can choose its default image workflow: Krea 2, FLUX.2 Klein 9B KV, or Codex Images. The default applies to both image creation and editing; it does not affect reel generation or text assistance.

In **Assets**, use the default link beneath **Image workflow**, or go to **Project settings → Images**. Choose a model and **Save default**. **Use global default** removes the project override. Closing the dialog without saving leaves the preference unchanged.

The ordinary **Image workflow** selector changes the current request only. Saving a default from Assets also selects it for the current request and checks its configuration. Other existing drafts retain their selected workflow. Reopening Assets starts with the saved project default. The AI settings page still configures each workflow's underlying models and the global default.

Unavailable models show their existing readiness error; Lumibelle does not silently switch providers. New requests resolve an explicit workflow first, then the project default, then the global default. Captured requests, retries and One more keep their captured workflow and settings, regardless of later default changes.

Project preferences are saved atomically with a separate image-default revision, so text-model and LoRA-preference changes are retained. Conflicting default changes require review and another explicit save. Failed saves retain the selection for retry.
