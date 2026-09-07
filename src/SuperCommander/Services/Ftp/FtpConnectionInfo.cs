namespace SuperCommander.Services.Ftp;

/// <summary>Everything needed to open one FTP session.</summary>
public sealed class FtpConnectionInfo
{
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; } = 21;
    public string Username { get; set; } = "anonymous";
    public string Password { get; set; } = string.Empty;

    /// <summary>Rarely used ACCT value; sent after login when set.</summary>
    public string Account { get; set; } = string.Empty;

    public FtpSecurity Security { get; set; } = FtpSecurity.None;

    /// <summary>Allows self-signed or otherwise untrusted server certificates.</summary>
    public bool AcceptInvalidCertificate { get; set; }

    public string InitialPath { get; set; } = "/";

    public int TimeoutMs { get; set; } = 20000;

    public static FtpConnectionInfo FromSite(Models.FtpSite site) => new()
    {
        Host = site.Host,
        Port = site.Port,
        Username = string.IsNullOrWhiteSpace(site.Username) ? "anonymous" : site.Username,
        Password = site.Password,
        Account = site.Account,
        Security = site.Security,
        AcceptInvalidCertificate = site.AcceptInvalidCertificate,
        InitialPath = string.IsNullOrWhiteSpace(site.RemotePath) ? "/" : site.RemotePath
    };
}

/// <summary>Path helpers - FTP paths are always '/' separated and case sensitive.</summary>
public static class FtpPath
{
    public const char Separator = '/';

    public static string Combine(string directory, string name)
    {
        if (string.IsNullOrEmpty(directory) || directory == "/") return "/" + name.TrimStart(Separator);
        return directory.TrimEnd(Separator) + Separator + name.TrimStart(Separator);
    }

    public static string GetParent(string path)
    {
        var trimmed = path.TrimEnd(Separator);
        if (trimmed.Length <= 1) return "/";

        int slash = trimmed.LastIndexOf(Separator);
        return slash <= 0 ? "/" : trimmed[..slash];
    }

    public static string GetName(string path)
    {
        var trimmed = path.TrimEnd(Separator);
        int slash = trimmed.LastIndexOf(Separator);
        return slash < 0 ? trimmed : trimmed[(slash + 1)..];
    }

    public static string Normalise(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "/";

        var normalised = path.Replace('\\', Separator);
        if (!normalised.StartsWith(Separator)) normalised = Separator + normalised;

        // Collapse any doubled separators without losing the leading one.
        while (normalised.Contains("//", StringComparison.Ordinal))
            normalised = normalised.Replace("//", "/", StringComparison.Ordinal);

        return normalised.Length > 1 ? normalised.TrimEnd(Separator) : "/";
    }
}
