namespace EcencyApi.Infrastructure;

/// <summary>
/// The Hive account name grammar, as the chain checks it in
/// <c>is_valid_account_name</c>: 3 to 16 characters overall, split on dots into
/// labels of at least 3 characters each, every label starting with a letter,
/// ending with a letter or digit, and made of <c>a-z</c>, <c>0-9</c> and
/// <c>-</c> in between. Labels are the part a character-class regex cannot
/// express: <c>-ab</c>, <c>abc-</c>, <c>a..b</c>, <c>ab.cdef</c> and
/// <c>...</c> are all sixteen-or-fewer characters from the right alphabet and
/// none of them is a name.
/// </summary>
/// <remarks>
/// One deliberate difference from the chain routine: a trailing dot
/// (<c>abc.</c>) is rejected here. The chain loop stops when the next label
/// would start past the end of the string, so it never looks at the empty last
/// label; a name that ends in a dot has no owner on chain in practice and is
/// not worth an authenticated upstream call.
/// </remarks>
public static class HiveNames
{
    public const int MinLength = 3;
    public const int MaxLength = 16;

    public static bool IsAccountName(string? name)
    {
        if (name == null || name.Length is < MinLength or > MaxLength)
        {
            return false;
        }

        var begin = 0;
        while (true)
        {
            var end = name.IndexOf('.', begin);
            if (end < 0)
            {
                end = name.Length;
            }
            if (end - begin < MinLength)
            {
                return false;
            }
            if (!IsLetter(name[begin]) || !IsLetterOrDigit(name[end - 1]))
            {
                return false;
            }
            for (var i = begin + 1; i < end - 1; i++)
            {
                if (!IsLetterOrDigit(name[i]) && name[i] != '-')
                {
                    return false;
                }
            }
            if (end == name.Length)
            {
                return true;
            }
            // `end` sits on a dot; the next label starts after it. Reaching the
            // end of the string here is the trailing-dot case.
            begin = end + 1;
            if (begin >= name.Length)
            {
                return false;
            }
        }
    }

    private static bool IsLetter(char c) => c is >= 'a' and <= 'z';

    private static bool IsLetterOrDigit(char c) => IsLetter(c) || c is >= '0' and <= '9';
}
