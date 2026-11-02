using MikePDF.Perf.Documents;

namespace MikePDF.Perf.Metrics;

/// <summary>
/// The metrics of the page view modes (docs/features/page-view-modes.md PV-12 and PV-17). Neither is a gate: both are reported with <c>Gate</c> off and no budget id, so the budget file and the
/// comparison are untouched; <see cref="Scenarios.ViewModeScenarios"/> produces them and the tests of this harness run it.
/// </summary>
public static class ViewModeMetrics
{
    /// <summary>A 10,000-page text document, the largest of the harness: PV-12.</summary>
    public static DocumentSpec Text10000 { get; } = new("text10000", 10_000, 15, new Dictionary<int, int>());

    public static readonly MetricDefinition LargeFacingOpen = new("view.open.facing_cover.first_tile.text10000", "ms", "lower",
        "PV-12: open a 10,000-page PDF the way the app does (both engines, info, the first 32 page sizes), lay it out two pages with cover, continuous, and draw the first sharp tile. Layout is the time to build the layout from the first sizes; every later size chunk is in the details.",
        null, 8, Gate: false);

    public static readonly MetricDefinition PageTurnTiles = new("view.page_turn.engine_tiles.text20", "ms", "lower",
        "PV-17, the engine's share of a page turn in a presentation: every tile of one page at fit-page scale on a 1920 x 1080 screen (6 tiles), 3 requests in flight, a different page each time. The app's own turn time (with and without the page drawn ahead) is measured by the journey, with the app.",
        null, 2, Gate: false);

    public static IReadOnlyList<MetricDefinition> All { get; } = [LargeFacingOpen, PageTurnTiles];
}
