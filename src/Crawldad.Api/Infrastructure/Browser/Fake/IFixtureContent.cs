using System.Text;

namespace Crawldad.Api.Infrastructure.Browser.Fake;

/// <summary>Where a <see cref="FakeManifest"/> reads its states' HTML (and a download transition's bytes): a shipped
/// fixture directory for the internal acceptance fixtures, or an in-memory content-addressed page map for a tenant's
/// recorded fixture set. The manifest holds the source so both record/replay paths share one replay engine.</summary>
internal interface IFixtureContent
{
    /// <summary>Reads the text (a state's HTML document, or a frame's document) stored under <paramref name="key"/>.</summary>
    string ReadText(string key);

    /// <summary>Reads the raw bytes stored under <paramref name="key"/> — the body a download transition serves.</summary>
    byte[] ReadBytes(string key);
}

/// <summary>Reads fixture content from a shipped fixture directory — the internal acceptance fixtures' backing store,
/// where a manifest's <c>html</c>/<c>file</c> values are paths relative to the directory. Behaviour is byte-for-byte the
/// pre-existing <see cref="FakeManifest"/> file reads.</summary>
internal sealed class DirectoryFixtureContent(string fixtureDir) : IFixtureContent
{
    public string ReadText(string key) => File.ReadAllText(Path.Combine(fixtureDir, key));

    public byte[] ReadBytes(string key) => File.ReadAllBytes(Path.Combine(fixtureDir, key));
}

/// <summary>Reads fixture content from an in-memory, content-addressed page map — a tenant's recorded fixture set, where
/// a manifest's <c>html</c> value is the page's SHA-256 key into <paramref name="pages"/>. A recorded set never carries
/// download bytes (record mode does not capture downloads), so <see cref="ReadBytes"/> is the UTF-8 encoding of the
/// keyed text — a total, sensible fallback rather than an unreachable throw.</summary>
internal sealed class InMemoryFixtureContent(IReadOnlyDictionary<string, string> pages) : IFixtureContent
{
    public string ReadText(string key) => pages[key];

    public byte[] ReadBytes(string key) => Encoding.UTF8.GetBytes(pages[key]);
}
