namespace MoneroMarketCap.Data.Constants;

public static class RoleNames
{
    public const string Admin = "Admin";
    public const string Moderator = "Moderator";
    public const string User = "User";

    public static readonly IReadOnlyList<string> All = new[] { Admin, Moderator, User };
}