using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using SuperCommander.Interop;
using SuperCommander.Services.Ftp;

namespace SuperCommander.Models;

/// <summary>
/// A saved FTP connection. The password is persisted only as a DPAPI blob that
/// the current Windows user can decrypt - never in the clear.
///
/// Raises change notifications so the site list updates as fields are edited,
/// rather than the editor having to re-project rows (which reset the caret and
/// made typing appear backwards).
/// </summary>
public sealed class FtpSite : INotifyPropertyChanged
{
    private string _name = string.Empty;
    private string _host = string.Empty;
    private int _port = 21;
    private string _username = "anonymous";
    private string _remotePath = "/";
    private FtpSecurity _security = FtpSecurity.None;
    private bool _anonymous;
    private string? _password;

    public string Name
    {
        get => _name;
        set => Set(ref _name, value);
    }

    public string Host
    {
        get => _host;
        set => Set(ref _host, value);
    }

    public int Port
    {
        get => _port;
        set => Set(ref _port, value);
    }

    public string Username
    {
        get => _username;
        set => Set(ref _username, value);
    }

    public string RemotePath
    {
        get => _remotePath;
        set => Set(ref _remotePath, value);
    }

    public FtpSecurity Security
    {
        get => _security;
        set => Set(ref _security, value);
    }

    public bool Anonymous
    {
        get => _anonymous;
        set => Set(ref _anonymous, value);
    }

    public string Account { get; set; } = string.Empty;

    public bool AcceptInvalidCertificate { get; set; }

    public bool SavePassword { get; set; } = true;

    /// <summary>DPAPI-protected password. This is what goes into settings.json.</summary>
    public string ProtectedPassword { get; set; } = string.Empty;

    /// <summary>Plain password, never serialised.</summary>
    [JsonIgnore]
    public string Password
    {
        get => _password ??= DataProtection.Unprotect(ProtectedPassword);
        set
        {
            _password = value;
            ProtectedPassword = SavePassword ? DataProtection.Protect(value) : string.Empty;
        }
    }

    /// <summary>Re-encrypts, or clears, after SavePassword is toggled.</summary>
    public void ApplyPasswordPolicy()
    {
        if (!SavePassword) ProtectedPassword = string.Empty;
        else if (_password is not null) ProtectedPassword = DataProtection.Protect(_password);
    }

    [JsonIgnore]
    public string Display => string.IsNullOrWhiteSpace(Name) ? $"{Host}:{Port}" : Name;

    [JsonIgnore]
    public string Summary
    {
        get
        {
            var security = Security switch
            {
                FtpSecurity.ExplicitTls => "FTPS",
                FtpSecurity.ImplicitTls => "FTPS implicit",
                _ => "FTP"
            };
            var user = Anonymous ? "anonymous" : Username;
            return $"{security}  ·  {user}@{Host}:{Port}{RemotePath}";
        }
    }

    public FtpSite Clone() => new()
    {
        Name = Name,
        Host = Host,
        Port = Port,
        Username = Username,
        Account = Account,
        RemotePath = RemotePath,
        Security = Security,
        AcceptInvalidCertificate = AcceptInvalidCertificate,
        Anonymous = Anonymous,
        SavePassword = SavePassword,
        ProtectedPassword = ProtectedPassword,
        _password = _password
    };

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        // Both list labels are derived, so refresh them alongside every field.
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Display)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Summary)));
    }
}
