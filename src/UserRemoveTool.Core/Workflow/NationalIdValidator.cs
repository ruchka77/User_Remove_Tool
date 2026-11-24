namespace UserRemoveTool.Core.Workflow;

public enum NationalIdStatus { Valid, Missing, Invalid }

/// <summary>
/// Checks the value of the AD attribute extensionAttribute1 ("NationalId") as an Israeli ID number:
/// up to 9 digits, left-padded with zeros, with the standard check digit.
/// </summary>
public static class NationalIdValidator
{
    public static NationalIdStatus Check(string? value)
    {
        var id = (value ?? "").Trim().Replace("-", "").Replace(" ", "");
        if (id.Length == 0) return NationalIdStatus.Missing;
        if (id.Length > 9 || !id.All(char.IsAsciiDigit)) return NationalIdStatus.Invalid;
        id = id.PadLeft(9, '0');
        var sum = 0;
        for (var i = 0; i < 9; i++)
        {
            var product = (id[i] - '0') * (i % 2 == 0 ? 1 : 2);
            sum += product > 9 ? product - 9 : product;
        }
        return sum % 10 == 0 ? NationalIdStatus.Valid : NationalIdStatus.Invalid;
    }
}
