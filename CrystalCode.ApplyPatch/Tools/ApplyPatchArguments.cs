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

            var hasPatchText = false;
            var hasInput = false;
            foreach (var property in root.EnumerateObject())
            {
                if (string.Equals(property.Name, "patchText", StringComparison.Ordinal))
                {
                    hasPatchText = true;
                    continue;
                }

                if (string.Equals(property.Name, "input", StringComparison.Ordinal))
                {
                    hasInput = true;
                    continue;
                }

                error = "Tool arguments must contain only patchText or input.";
                return false;
            }

            if (hasPatchText && hasInput)
            {
                error = "Provide only one of patchText or input.";
                return false;
            }

            if (!hasPatchText && !hasInput)
            {
                error = "Argument 'patchText' must be a non-empty string.";
                return false;
            }

            var name = hasPatchText ? "patchText" : "input";
            if (!root.TryGetProperty(name, out var value)
                || value.ValueKind != JsonValueKind.String)
            {
                error = $"Argument '{name}' must be a non-empty string.";
                return false;
            }

            var text = value.GetString()!.Trim();
            if (text.Length == 0)
            {
                error = $"Argument '{name}' must be a non-empty string.";
                return false;
            }

            patchText = text;
            return true;
        }
    }
}
