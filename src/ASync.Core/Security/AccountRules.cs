using ASync.Core.Models;

namespace ASync.Core.Security;

/// <summary>Rules for changing and removing accounts. Each returns a message for the person, or null when allowed.</summary>
public static class AccountRules
{
    public const int MinPasswordLength = 8;

    public static string? ValidatePassword(string? password) =>
        password is null || password.Length < MinPasswordLength ? $"Passwords must be at least {MinPasswordLength} characters long." : null;

    /// <summary>
    /// An admin deletes <paramref name="target"/>. Nobody deletes their own account (an admin would lock themselves out
    /// half-way), and the last admin stays, so the server always has someone who can manage it.
    /// </summary>
    public static string? CanDelete(int actorId, AppUser target, int adminCount)
    {
        if (target.Id == actorId) return "You cannot delete your own account.";
        if (target.IsAdmin && adminCount <= 1) return "This is the last admin. Make someone else an admin first.";
        return null;
    }

    /// <summary>
    /// A person changes their own password. Accounts that only use single sign-on have no password to check and cannot
    /// set one here: a stolen browser session must not turn into a password that outlives the provider's sign-in.
    /// </summary>
    public static string? CanChangeOwnPassword(AppUser user, string? currentPasswordError, string? newPassword)
    {
        if (user.PasswordHash is null)
            return "This account signs in with single sign-on and has no password. Ask an admin to set one if you need it.";
        if (currentPasswordError is not null) return currentPasswordError;
        return ValidatePassword(newPassword);
    }
}
