namespace Crawldad.Portal.Components.Pages.App;

/// <summary>The payload-editor form model shared by the revise (PayloadDetail) and create (PayloadNew) static-SSR forms.
/// Every field is nullable so a blank submit round-trips as empty rather than tripping the framework binder. The page
/// parses <see cref="Script"/> to JSON and delegates the crawldad-DSL validation to the API — the portal never
/// re-implements the schema/semantic gate. <see cref="Intent"/> is set by the clicked submit button
/// (<c>save</c> vs <c>validate</c>); an unset intent is treated as the non-destructive <c>validate</c>.</summary>
public sealed class PayloadEditorInput
{
    /// <summary>The edited payload document as raw JSON text (the logical <c>name</c> lives inside the document).</summary>
    public string? Script { get; set; }

    /// <summary>An optional revision note (revise only; the draft endpoint carries no note).</summary>
    public string? Note { get; set; }

    /// <summary>Which submit button was pressed: <c>save</c> persists via the API; anything else validates only.</summary>
    public string? Intent { get; set; }
}
