using Alba;
using Crawldad.Contracts.Payloads;
using Crawldad.Tests.Support;
using Crawldad.Web.Features.Payloads;
using Marten;
using Microsoft.Extensions.DependencyInjection;

namespace Crawldad.Tests.Integration;

/// <summary>
/// The revision resolver (<see cref="PayloadRevisions"/>, §14.1): folds a payload stream into every revision's script.
/// Exercises all four event types (a rename/archive carries the prior script forward), the head revision, the
/// out-of-range guards, and the unknown-payload null.
/// </summary>
[Collection(IntegrationCollection.Name)]
public class PayloadRevisionsTests(AppFixture fixture)
{
    private IAlbaHost Host => fixture.Host;

    [Fact]
    public async Task LoadAsync_returns_null_for_an_unknown_payload()
    {
        var store = Host.Services.GetRequiredService<IDocumentStore>();
        await using var session = store.LightweightSession(TestTenants.PrimaryId);
        (await PayloadRevisions.LoadAsync(session, Guid.NewGuid(), CancellationToken.None)).ShouldBeNull();
    }

    [Fact]
    public async Task LoadAsync_folds_every_event_type_and_exposes_each_revision()
    {
        var id = Guid.NewGuid();
        var at = FakeClock.Fixed;
        var store = Host.Services.GetRequiredService<IDocumentStore>();
        await using (var writer = store.LightweightSession(TestTenants.PrimaryId))
        {
            writer.Events.StartStream<Payload>(
                id,
                new PayloadDrafted("demo", """{ "s": 1 }""", "h1", at, TestTenants.PrimaryActor),
                new PayloadRevised("""{ "s": 2 }""", "h2", "note", at, TestTenants.PrimaryActor),
                new PayloadRenamed("renamed", at, TestTenants.PrimaryActor), // metadata: carries the script forward
                new PayloadArchived(at, TestTenants.PrimaryActor));          // terminal: carries the script forward, marks archived
            await writer.SaveChangesAsync();
        }

        await using var session = store.LightweightSession(TestTenants.PrimaryId);
        var resolved = await PayloadRevisions.LoadAsync(session, id, CancellationToken.None);

        resolved.ShouldNotBeNull();
        resolved.Status.ShouldBe(PayloadStatus.Archived);
        resolved.HeadRevision.ShouldBe(4);

        resolved.At(1)!.ScriptHash.ShouldBe("h1");
        resolved.At(2)!.ScriptHash.ShouldBe("h2");
        resolved.At(3)!.ScriptHash.ShouldBe("h2");           // rename carried the script forward
        resolved.At(4)!.Script.ShouldBe("""{ "s": 2 }""");   // archive carried it forward too

        resolved.At(0).ShouldBeNull(); // below range
        resolved.At(5).ShouldBeNull(); // above range
    }
}
