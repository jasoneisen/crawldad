using System.Text;
using Crawldad.Api.Infrastructure.Storage;
using Crawldad.Tests.Support;

namespace Crawldad.Tests.Unit;

/// <summary>The failure-screenshot blob store and the download content-type guess: the store is content-addressed (identical
/// bytes ⇒ one blob, a credential-free hash ref), and the content-type helper maps a stored name's extension to a MIME type.</summary>
public class ScreenshotStoreTests
{
    [Fact]
    public async Task Save_returns_a_content_addressed_ref_and_holds_the_bytes()
    {
        var store = new InMemoryScreenshotStore();
        var png = Encoding.UTF8.GetBytes("fake-png-bytes");

        var reference = await store.SaveAsync(TestTenants.InterpreterTenant, png, CancellationToken.None);

        reference.ShouldStartWith("screenshots/");
        reference.ShouldEndWith(".png");
        store.Blobs.Keys.ShouldContain(reference);
        store.Blobs[reference].ShouldBe(png);
    }

    [Fact]
    public async Task Save_is_idempotent_for_identical_bytes()
    {
        var store = new InMemoryScreenshotStore();

        var first = await store.SaveAsync(TestTenants.InterpreterTenant, [1, 2, 3], CancellationToken.None);
        var second = await store.SaveAsync(TestTenants.InterpreterTenant, [1, 2, 3], CancellationToken.None);

        second.ShouldBe(first);
        store.Blobs.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Open_read_round_trips_a_saved_capture_from_its_own_partition()
    {
        var store = new InMemoryScreenshotStore();
        var png = Encoding.UTF8.GetBytes("fake-png-bytes");
        var reference = await store.SaveAsync(TestTenants.InterpreterTenant, png, CancellationToken.None);

        await using var stream = await store.OpenReadAsync(TestTenants.InterpreterTenant, reference, CancellationToken.None);
        using var buffer = new MemoryStream();
        await stream!.CopyToAsync(buffer, CancellationToken.None);
        buffer.ToArray().ShouldBe(png);
    }

    [Fact]
    public async Task Open_read_is_null_for_an_unknown_ref() =>
        (await new InMemoryScreenshotStore().OpenReadAsync(TestTenants.InterpreterTenant, "screenshots/deadbeef.png", CancellationToken.None)).ShouldBeNull();

    [Theory]
    [InlineData("a.pdf", "application/pdf")]
    [InlineData("a.jpg", "image/jpeg")]
    [InlineData("a.JPEG", "image/jpeg")]
    [InlineData("a.png", "image/png")]
    [InlineData("a.html", "text/html")]
    [InlineData("a.htm", "text/html")]
    [InlineData("a.csv", "text/csv")]
    [InlineData("a.json", "application/json")]
    [InlineData("a.txt", "text/plain")]
    [InlineData("a.bin", "application/octet-stream")]
    [InlineData("noext", "application/octet-stream")]
    public void Content_type_is_guessed_from_the_extension(string fileName, string expected) =>
        ContentTypes.ForFile(fileName).ShouldBe(expected);
}
