using System.Text.RegularExpressions;

namespace SDMate.Core;

public enum UserSearchInputKind
{
    FullName,
    EmailAddress,
    SamAccountName
}

public readonly record struct UserSearchInput(UserSearchInputKind Kind, string Value);

public static partial class UserSearchInputClassifier
{
    private static readonly Regex EmailPattern = new(
        @"^[^@\s]+@[^@\s]+\.[^@\s]+$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex FullNamePattern = new(
        @"^[\p{L}][\p{L}\p{M}'-]*(?:\s+[\p{L}][\p{L}\p{M}'-]*)+$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static bool TryClassify(string? input, out UserSearchInput result)
    {
        result = default;

        if (string.IsNullOrWhiteSpace(input))
        {
            return false;
        }

        var value = input.Trim();

        if (EmailPattern.IsMatch(value))
        {
            result = new UserSearchInput(UserSearchInputKind.EmailAddress, value);
            return true;
        }

        if (value.Contains('@'))
        {
            return false;
        }

        if (value.Any(char.IsWhiteSpace))
        {
            if (!FullNamePattern.IsMatch(value))
            {
                return false;
            }

            result = new UserSearchInput(UserSearchInputKind.FullName, value);
            return true;
        }

        result = new UserSearchInput(UserSearchInputKind.SamAccountName, value);
        return true;
    }
}