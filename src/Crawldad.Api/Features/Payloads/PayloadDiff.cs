using System.Text.Json;
using Crawldad.Contracts.Payloads;

namespace Crawldad.Api.Features.Payloads;

/// <summary>A minimal structural diff between two payload scripts: walks the JSON in parallel and emits one entry per
/// <b>deepest</b> changed location (an unchanged subtree yields nothing). Paths are JSON Pointers; payload keys are
/// simple identifiers so tokens need no escaping. Values are cloned so they outlive the parsed source documents.</summary>
internal static class PayloadDiff
{
    /// <summary>Computes the structural changes turning <paramref name="from"/> into <paramref name="to"/>.</summary>
    /// <returns>The changes (empty when the two documents are identical).</returns>
    public static IReadOnlyList<PayloadDiffEntry> Compute(JsonElement from, JsonElement to)
    {
        var changes = new List<PayloadDiffEntry>();
        Diff("", from, to, changes);
        return changes;
    }

    private static void Diff(string path, JsonElement a, JsonElement b, List<PayloadDiffEntry> changes)
    {
        if (a.ValueKind != b.ValueKind)
        {
            changes.Add(Changed(path, a, b));
            return;
        }

        switch (a.ValueKind)
        {
            case JsonValueKind.Object:
                DiffObject(path, a, b, changes);
                break;
            case JsonValueKind.Array:
                DiffArray(path, a, b, changes);
                break;
            default:
                if (!string.Equals(a.GetRawText(), b.GetRawText(), StringComparison.Ordinal))
                {
                    changes.Add(Changed(path, a, b));
                }

                break;
        }
    }

    private static void DiffObject(string path, JsonElement a, JsonElement b, List<PayloadDiffEntry> changes)
    {
        foreach (var prop in a.EnumerateObject())
        {
            if (b.TryGetProperty(prop.Name, out var bValue))
            {
                Diff($"{path}/{prop.Name}", prop.Value, bValue, changes);
            }
            else
            {
                changes.Add(new PayloadDiffEntry($"{path}/{prop.Name}", PayloadDiffKind.Removed, prop.Value.Clone(), null));
            }
        }

        foreach (var prop in b.EnumerateObject())
        {
            if (!a.TryGetProperty(prop.Name, out _))
            {
                changes.Add(new PayloadDiffEntry($"{path}/{prop.Name}", PayloadDiffKind.Added, null, prop.Value.Clone()));
            }
        }
    }

    private static void DiffArray(string path, JsonElement a, JsonElement b, List<PayloadDiffEntry> changes)
    {
        var aItems = a.EnumerateArray().ToList();
        var bItems = b.EnumerateArray().ToList();
        var shared = Math.Min(aItems.Count, bItems.Count);
        for (var i = 0; i < shared; i++)
        {
            Diff($"{path}/{i}", aItems[i], bItems[i], changes);
        }

        for (var i = shared; i < aItems.Count; i++)
        {
            changes.Add(new PayloadDiffEntry($"{path}/{i}", PayloadDiffKind.Removed, aItems[i].Clone(), null));
        }

        for (var i = shared; i < bItems.Count; i++)
        {
            changes.Add(new PayloadDiffEntry($"{path}/{i}", PayloadDiffKind.Added, null, bItems[i].Clone()));
        }
    }

    private static PayloadDiffEntry Changed(string path, JsonElement a, JsonElement b) =>
        new(path, PayloadDiffKind.Changed, a.Clone(), b.Clone());
}
