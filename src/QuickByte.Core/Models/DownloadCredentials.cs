using System.Text.Json.Serialization;
using QuickByte.Core.Helpers;

namespace QuickByte.Core.Models;

/// <summary>
/// A user name and password presented to the server a download comes from —
/// an FTP login, or an HTTP <c>Basic</c> challenge.
///
/// The password never round-trips through JSON in the clear: <see cref="Password"/>
/// is the in-memory value and is <see cref="JsonIgnoreAttribute">ignored</see> by
/// the serializer, while <see cref="ProtectedPassword"/> is the property that
/// actually lands in <c>downloads.json</c>, encrypted by
/// <see cref="SecretProtector"/>. Keeping the conversion on the model rather
/// than in the repository means every path that persists a
/// <see cref="DownloadItem"/> gets it for free.
/// </summary>
public sealed class DownloadCredentials
{
    public string UserName { get; set; } = string.Empty;

    /// <summary>The live password. Deliberately not serialized — see the type summary.</summary>
    [JsonIgnore]
    public string Password { get; set; } = string.Empty;

    /// <summary>DPAPI-protected form of <see cref="Password"/>; this is what is written to disk.</summary>
    public string? ProtectedPassword
    {
        get => SecretProtector.Protect(Password);
        set => Password = SecretProtector.Unprotect(value);
    }

    [JsonIgnore]
    public bool IsEmpty => string.IsNullOrEmpty(UserName) && string.IsNullOrEmpty(Password);

    public DownloadCredentials Clone() => new() { UserName = UserName, Password = Password };
}
