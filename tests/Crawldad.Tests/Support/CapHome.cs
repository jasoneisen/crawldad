namespace Crawldad.Tests.Support;

/// <summary>Shared constants for the caphome-search fixture: selectors, URLs, the cancellation token, and the two
/// reusable step fragments. Public members on an internal helper (so the private-field underscore convention does not
/// apply) keep the many small tests terse and consistent.</summary>
internal static class CapHome
{
    public const string FormUrl = "https://aca-prod.accela.com/LJCMG/Cap/CapHome.aspx?module=Enforcement&TabName=Enforcement";
    public const string RequestPrefix = "https://aca-prod.accela.com/LJCMG/Cap/CapHome.aspx";
    public const string SearchButton = "#ctl00_PlaceHolderMain_btnNewSearch";
    public const string GridRows = "#ctl00_PlaceHolderMain_dgvPermitList_gdvPermitList tr";
    public const string StartDate = "#ctl00_PlaceHolderMain_generalSearchForm_txtGSStartDate";
    public const string EndDate = "#ctl00_PlaceHolderMain_generalSearchForm_txtGSEndDate";
    public const string Overlay = "#divGlobalLoading";

    /// <summary>Navigate + postback to the results grid — the shared prefix for grid-dependent interpreter tests.</summary>
    public const string ToResults =
        """{ "goto": { "url": "https://aca-prod.accela.com/LJCMG/Cap/CapHome.aspx?module=Enforcement&TabName=Enforcement" } },""" +
        """ { "waitForRequest": { "urlPrefix": "https://aca-prod.accela.com/LJCMG/Cap/CapHome.aspx", "method": "POST",""" +
        """ "trigger": [ { "click": { "selector": "#ctl00_PlaceHolderMain_btnNewSearch" } } ] } }""";

    public const string LocateRows =
        """{ "locate": { "var": "rows", "selector": "#ctl00_PlaceHolderMain_dgvPermitList_gdvPermitList tr" } }""";

    public static readonly CancellationToken Ct = CancellationToken.None;
}
