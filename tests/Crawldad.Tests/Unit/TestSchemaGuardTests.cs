using Crawldad.Tests.Support;

namespace Crawldad.Tests.Unit;

/// <summary>The drop-before-boot safety guard. <see cref="TestSchema.Drop"/> issues a <c>DROP SCHEMA ... CASCADE</c>
/// against the developer's shared Postgres, so the one thing that must never happen is a misconfigured test aiming it at
/// real data: the guard admits ONLY per-fixture test schemas and refuses everything else — most of all the production
/// <c>crawldad</c>/<c>portal</c> schemas and Postgres's <c>public</c> — before a connection is ever opened.</summary>
public class TestSchemaGuardTests
{
    [Theory]
    [InlineData("crawldad_test")]
    [InlineData("crawldad_slotq_restart")]
    [InlineData("crawldad_iso_pr7_rl")]
    [InlineData("crawldad_portal_test")]
    [InlineData("crawldad_0")]
    public void A_per_fixture_test_schema_is_droppable(string schemaName) =>
        Should.NotThrow(() => TestSchema.EnsureDroppable(schemaName));

    [Theory]
    [InlineData("crawldad")]          // the API's production schema
    [InlineData("CRAWLDAD")]          // ...however it is cased
    [InlineData("portal")]            // the portal's production schema
    [InlineData("Portal")]
    [InlineData("public")]            // Postgres's default schema
    [InlineData("PUBLIC")]
    [InlineData("crawldad_")]         // the prefix alone names no fixture
    [InlineData("crawldad_Test")]     // uppercase is not a schema this suite creates
    [InlineData("crawldad-test")]     // nor a hyphen
    [InlineData("other_crawldad_x")]  // must START at the prefix, not merely contain it
    [InlineData("crawldad_x\"; drop schema crawldad cascade; --")]
    [InlineData("")]
    public void Anything_but_a_test_schema_is_refused(string schemaName)
    {
        var refusal = Should.Throw<ArgumentException>(() => TestSchema.EnsureDroppable(schemaName));

        refusal.ParamName.ShouldBe("schemaName");
        refusal.Message.ShouldContain("not a droppable test schema");
    }
}
