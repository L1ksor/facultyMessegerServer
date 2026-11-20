using System.Security.Cryptography;

namespace FacultyMessenger.Api.Services.Auth;

public static class PasswordGenerator
{
    private const string Uppercase = "ABCDEFGHIJKLMNOPQRSTUVWXYZ";
    private const string Lowercase = "abcdefghijklmnopqrstuvwxyz";
    private const string Digits = "0123456789";
    private const string Specials = "!@#$%^&*_-+=";
    private const string AllChars = Uppercase + Lowercase + Digits + Specials;

    public static string Generate(int length = 14)
    {
        if (length < 8)
            throw new ArgumentOutOfRangeException(nameof(length), "Длина пароля должна быть не менее 8 символов.");

        var chars = new char[length];
        chars[0] = Uppercase[RandomNumberGenerator.GetInt32(Uppercase.Length)];
        chars[1] = Lowercase[RandomNumberGenerator.GetInt32(Lowercase.Length)];
        chars[2] = Digits[RandomNumberGenerator.GetInt32(Digits.Length)];
        chars[3] = Specials[RandomNumberGenerator.GetInt32(Specials.Length)];

        for (var i = 4; i < length; i++)
        {
            chars[i] = AllChars[RandomNumberGenerator.GetInt32(AllChars.Length)];
        }

        // Перемешивание Фишера—Йетса с криптостойким ГСЧ
        for (var i = chars.Length - 1; i > 0; i--)
        {
            var j = RandomNumberGenerator.GetInt32(i + 1);
            (chars[i], chars[j]) = (chars[j], chars[i]);
        }

        return new string(chars);
    }
}
