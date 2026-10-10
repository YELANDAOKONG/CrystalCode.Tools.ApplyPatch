using System.Text.Json;

namespace CrystalCode.ApplyPatch.Tools;

/// <summary>
/// Reads the apply_patch arguments.
/// </summary>
internal static class ApplyPatchArguments
{
    public static bool TryRead(string json, out string patchText, out string? error)
    {
        patchText = "";
        error = null;
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            error = "Tool arguments are not valid JSON.";
            return false;
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                error = "Tool arguments must be a JSON object.";
                return false;
            }

            foreach (var property in root.EnumerateObject())
            {
                if (!string.Equals(property.Name, "patchText", StringComparison.Ordinal))
                {
                    error = "Tool arguments must contain only patchText.";
                    return false;
                }
            }

            if (!root.TryGetProperty("patchText", out var value)
                || value.ValueKind != JsonValueKind.String)
            {
                error = "Argument 'patchText' must be a non-empty string.";
                return false;
            }

            var text = value.GetString()!.Trim();
            if (text.Length == 0)
            {
                error = "Argument 'patchText' must be a non-empty string.";
                return false;
            }

            patchText = text;
            return true;
        }
    }
}
