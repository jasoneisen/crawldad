using System.Text.Json;
using Crawldad.Contracts.Payloads;
using Crawldad.Web.Features.Payloads;

namespace Crawldad.Tests.Unit;

/// <summary>The structural revision diff (<see cref="PayloadDiff"/>): every change kind (added/removed/changed),
/// object and array recursion, kind-mismatch, and unchanged subtrees producing nothing — exercised with one crafted pair.</summary>
public class PayloadDiffTests
{
    private static IReadOnlyList<PayloadDiffEntry> Diff(string from, string to)
    {
        using var a = JsonDocument.Parse(from);
        using var b = JsonDocument.Parse(to);
        return PayloadDiff.Compute(a.RootElement, b.RootElement);
    }

    [Fact]
    public void Identical_documents_produce_no_changes() =>
        Diff("""{ "a": 1, "b": [1, 2] }""", """{ "a": 1, "b": [1, 2] }""").ShouldBeEmpty();

    [Fact]
    public void Covers_every_structural_change_kind()
    {
        const string From = """{ "same": "x", "changed": "a", "removedKey": 1, "kindShift": "s", "nested": { "n": 1, "gone": 2 }, "shrinkArr": [1, 2, 3], "growArr": [1] }""";
        const string To = """{ "same": "x", "changed": "b", "addedKey": 9, "kindShift": 5, "nested": { "n": 2 }, "shrinkArr": [1, 2], "growArr": [1, 7, 8] }""";
        var changes = Diff(From, To).ToDictionary(c => c.Path, StringComparer.Ordinal);

        changes.ShouldNotContainKey("/same"); // an unchanged leaf yields nothing

        changes["/changed"].Kind.ShouldBe(PayloadDiffKind.Changed); // scalar value differs
        changes["/changed"].From!.Value.GetString().ShouldBe("a");
        changes["/changed"].To!.Value.GetString().ShouldBe("b");

        changes["/removedKey"].Kind.ShouldBe(PayloadDiffKind.Removed); // object key present only in `from`
        changes["/removedKey"].To.ShouldBeNull();
        changes["/addedKey"].Kind.ShouldBe(PayloadDiffKind.Added);     // object key present only in `to`
        changes["/addedKey"].From.ShouldBeNull();

        changes["/kindShift"].Kind.ShouldBe(PayloadDiffKind.Changed);  // string → number, a JSON-kind mismatch

        changes["/nested/n"].Kind.ShouldBe(PayloadDiffKind.Changed);   // object recursion
        changes["/nested/gone"].Kind.ShouldBe(PayloadDiffKind.Removed);

        changes["/shrinkArr/2"].Kind.ShouldBe(PayloadDiffKind.Removed); // array tail dropped
        changes.ShouldNotContainKey("/shrinkArr/0");                    // shared elements match
        changes["/growArr/1"].Kind.ShouldBe(PayloadDiffKind.Added);     // array tail added
        changes["/growArr/2"].Kind.ShouldBe(PayloadDiffKind.Added);
    }
}
