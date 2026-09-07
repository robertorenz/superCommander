using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace SuperCommander.Services.Ftp;

public sealed class FtpException : Exception
{
    public FtpException(string message, int code = 0) : base(message) => Code = code;

    /// <summary>FTP reply code, or 0 when the failure was local.</summary>
    public int Code { get; }
}

public sealed record FtpReply(int Code, string Text)
{
    public bool IsPositive => Code is >= 200 and < 400;
    public bool IsIntermediate => Code is >= 300 and < 400;
    public override string ToString() => $"{Code} {Text}";
}

public enum FtpSecurity
{
    None,

    /// <summary>Plain connect, then AUTH TLS to upgrade (RFC 4217). The usual choice.</summary>
    ExplicitTls,

    /// <summary>TLS from the first byte, historically on port 990.</summary>
    ImplicitTls
}

/// <summary>
/// A minimal but complete FTP / FTPS client written directly on sockets.
///
/// Deliberately not FtpWebRequest: that type is obsolete from .NET 6 (SYSLIB0014)
/// and offers no way to report byte-level progress, which the copy dialog needs.
/// Passive mode only, which is what works through NAT and firewalls in practice.
///
/// Not thread safe. One instance belongs to one pane and every call is awaited.
/// </summary>
public sealed class FtpClient : IAsyncDisposable
{
    private const int TransferBufferSize = 128 * 1024;

    private TcpClient? _control;
    private Stream? _stream;
    private StreamReader? _reader;
    private Encoding _encoding = Encoding.UTF8;
    private bool _protectData;

    public FtpClient(FtpConnectionInfo info) => Info = info;

    public FtpConnectionInfo Info { get; }

    public bool IsConnected => _control?.Connected == true && _stream is not null;

    /// <summary>Raw protocol log, newest last. Surfaced in the connection dialog.</summary>
    public List<string> Log { get; } = new();

    /// <summary>Set when the server presented a certificate that failed validation.</summary>
    public string? CertificateWarning { get; private set; }

    // ---------------------------------------------------------------- connect

    public async Task ConnectAsync(CancellationToken token = default)
    {
        Close();

        _control = new TcpClient { SendTimeout = Info.TimeoutMs, ReceiveTimeout = Info.TimeoutMs };

        try
        {
            await _control.ConnectAsync(Info.Host, Info.Port, token);
        }
        catch (Exception ex)
        {
            throw new FtpException($"Cannot reach {Info.Host}:{Info.Port} - {ex.Message}");
        }

        _stream = _control.GetStream();

        if (Info.Security == FtpSecurity.ImplicitTls)
            _stream = await AuthenticateTlsAsync(_stream, token);

        _reader = new StreamReader(_stream, _encoding, false, 4096, leaveOpen: true);

        var greeting = await ReadReplyAsync(token);
        if (greeting.Code is not (220 or 120))
            throw new FtpException($"Unexpected greeting: {greeting}", greeting.Code);

        if (Info.Security == FtpSecurity.ExplicitTls)
        {
            var auth = await SendAsync("AUTH TLS", token);
            if (!auth.IsPositive)
            {
                auth = await SendAsync("AUTH SSL", token);
                if (!auth.IsPositive)
                    throw new FtpException($"The server refused TLS: {auth}", auth.Code);
            }

            _stream = await AuthenticateTlsAsync(_stream!, token);
            _reader = new StreamReader(_stream, _encoding, false, 4096, leaveOpen: true);
        }

        await LoginAsync(token);

        if (Info.Security != FtpSecurity.None)
        {
            // Protect the data channel too, otherwise only credentials are encrypted.
            await SendAsync("PBSZ 0", token);
            var prot = await SendAsync("PROT P", token);
            _protectData = prot.IsPositive;
        }

        // UTF-8 is not the default in the protocol; ask for it when advertised.
        var features = await SendAsync("FEAT", token);
        if (features.Text.Contains("UTF8", StringComparison.OrdinalIgnoreCase))
            await SendAsync("OPTS UTF8 ON", token);

        SupportsMlsd = features.Text.Contains("MLSD", StringComparison.OrdinalIgnoreCase);
        SupportsSize = features.Text.Contains("SIZE", StringComparison.OrdinalIgnoreCase);
        SupportsEpsv = features.Text.Contains("EPSV", StringComparison.OrdinalIgnoreCase);

        await SendAsync("TYPE I", token);
    }

    public bool SupportsMlsd { get; private set; }
    public bool SupportsSize { get; private set; }
    public bool SupportsEpsv { get; private set; }

    private async Task LoginAsync(CancellationToken token)
    {
        var user = await SendAsync($"USER {Info.Username}", token);

        if (user.Code == 331)
        {
            var pass = await SendAsync($"PASS {Info.Password}", token);
            if (!pass.IsPositive)
                throw new FtpException("The user name or password was rejected.", pass.Code);
        }
        else if (!user.IsPositive)
        {
            throw new FtpException($"Login failed: {user}", user.Code);
        }

        if (!string.IsNullOrWhiteSpace(Info.Account))
            await SendAsync($"ACCT {Info.Account}", token);
    }

    private async Task<Stream> AuthenticateTlsAsync(Stream inner, CancellationToken token)
    {
        var ssl = new SslStream(inner, leaveInnerStreamOpen: false, ValidateCertificate);

        try
        {
            await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = Info.Host,
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                CertificateRevocationCheckMode = X509RevocationMode.NoCheck
            }, token);
        }
        catch (AuthenticationException ex)
        {
            throw new FtpException($"TLS handshake failed: {ex.Message}");
        }

        return ssl;
    }

    private bool ValidateCertificate(object sender, X509Certificate? certificate,
        X509Chain? chain, SslPolicyErrors errors)
    {
        if (errors == SslPolicyErrors.None) return true;

        CertificateWarning = errors.ToString();
        Add($"! certificate warning: {errors}");

        // Self-signed certificates are the norm on private FTP servers, so this
        // is opt-in per site rather than a blanket bypass.
        return Info.AcceptInvalidCertificate;
    }

    // ------------------------------------------------------------- protocol IO

    private void Add(string line)
    {
        Log.Add(line);
        if (Log.Count > 500) Log.RemoveRange(0, 200);
    }

    public async Task<FtpReply> SendAsync(string command, CancellationToken token = default)
    {
        if (_stream is null) throw new FtpException("Not connected.");

        // Never write a password into the log.
        Add(command.StartsWith("PASS ", StringComparison.Ordinal) ? "PASS ****" : command);

        var bytes = _encoding.GetBytes(command + "\r\n");
        await _stream.WriteAsync(bytes, token);
        await _stream.FlushAsync(token);

        return await ReadReplyAsync(token);
    }

    private async Task<FtpReply> ReadReplyAsync(CancellationToken token)
    {
        if (_reader is null) throw new FtpException("Not connected.");

        var first = await _reader.ReadLineAsync(token) ?? throw new FtpException("The server closed the connection.");
        Add(first);

        if (first.Length < 4 || !int.TryParse(first.AsSpan(0, 3), out int code))
            throw new FtpException($"Malformed reply: {first}");

        var text = new StringBuilder(first.Length > 4 ? first[4..] : string.Empty);

        // "123-" opens a multi-line reply that ends with "123 ".
        if (first.Length > 3 && first[3] == '-')
        {
            var terminator = code.ToString(CultureInfo.InvariantCulture) + " ";
            while (true)
            {
                var line = await _reader.ReadLineAsync(token)
                           ?? throw new FtpException("The server closed the connection mid-reply.");
                Add(line);
                text.Append('\n').Append(line);

                if (line.StartsWith(terminator, StringComparison.Ordinal)) break;
            }
        }

        return new FtpReply(code, text.ToString());
    }

    private async Task<FtpReply> ExpectAsync(string command, CancellationToken token, params int[] accepted)
    {
        var reply = await SendAsync(command, token);
        if (accepted.Length == 0 ? reply.IsPositive : accepted.Contains(reply.Code)) return reply;
        throw new FtpException($"{command.Split(' ')[0]} failed: {reply.Text.Trim()}", reply.Code);
    }

    // ------------------------------------------------------- data connections

    private async Task<Stream> OpenDataAsync(string command, CancellationToken token, long restartAt = 0)
    {
        var (host, port) = await EnterPassiveAsync(token);

        var data = new TcpClient { SendTimeout = Info.TimeoutMs, ReceiveTimeout = Info.TimeoutMs };
        await data.ConnectAsync(host, port, token);

        if (restartAt > 0) await SendAsync($"REST {restartAt}", token);

        // The command must be issued after the data socket is open, or the server
        // may fill its send buffer and stall.
        var reply = await SendAsync(command, token);
        if (reply.Code is not (150 or 125))
        {
            data.Dispose();
            throw new FtpException($"{command.Split(' ')[0]} failed: {reply.Text.Trim()}", reply.Code);
        }

        Stream stream = data.GetStream();
        if (_protectData) stream = await AuthenticateTlsAsync(stream, token);

        return new FtpDataStream(stream, data, this);
    }

    private async Task<(string Host, int Port)> EnterPassiveAsync(CancellationToken token)
    {
        if (SupportsEpsv)
        {
            var epsv = await SendAsync("EPSV", token);
            if (epsv.Code == 229)
            {
                // 229 Entering Extended Passive Mode (|||51234|)
                int open = epsv.Text.IndexOf('(');
                int close = epsv.Text.IndexOf(')', open + 1);
                if (open >= 0 && close > open)
                {
                    var parts = epsv.Text[(open + 1)..close].Split('|');
                    if (parts.Length >= 4 && int.TryParse(parts[3], out int eport))
                        return (Info.Host, eport);
                }
            }
        }

        var pasv = await ExpectAsync("PASV", token, 227);

        // 227 Entering Passive Mode (h1,h2,h3,h4,p1,p2)
        int start = pasv.Text.IndexOf('(');
        int end = pasv.Text.IndexOf(')', start + 1);
        if (start < 0 || end < 0) throw new FtpException($"Cannot parse PASV reply: {pasv.Text}");

        var numbers = pasv.Text[(start + 1)..end].Split(',');
        if (numbers.Length < 6) throw new FtpException($"Cannot parse PASV reply: {pasv.Text}");

        if (!byte.TryParse(numbers[4].Trim(), out byte hi) || !byte.TryParse(numbers[5].Trim(), out byte lo))
            throw new FtpException($"Cannot parse PASV port: {pasv.Text}");

        // Servers behind NAT often advertise their private address here, so we
        // keep the host we already reached and take only the port.
        return (Info.Host, hi * 256 + lo);
    }

    /// <summary>Reads the trailing 226/250 that closes a data transfer.</summary>
    internal async Task CompleteTransferAsync(CancellationToken token = default)
    {
        var reply = await ReadReplyAsync(token);
        if (reply.Code is not (226 or 250))
            throw new FtpException($"Transfer did not complete: {reply.Text.Trim()}", reply.Code);
    }

    // ------------------------------------------------------------- operations

    public async Task<string> GetWorkingDirectoryAsync(CancellationToken token = default)
    {
        var reply = await ExpectAsync("PWD", token, 257);

        // 257 "/some/path" is the current directory
        int first = reply.Text.IndexOf('"');
        int last = reply.Text.LastIndexOf('"');
        if (first >= 0 && last > first) return reply.Text[(first + 1)..last].Replace("\"\"", "\"");

        return "/";
    }

    public async Task ChangeDirectoryAsync(string path, CancellationToken token = default) =>
        await ExpectAsync($"CWD {path}", token, 250, 200);

    public async Task<List<FtpEntry>> ListAsync(string path, CancellationToken token = default)
    {
        var command = SupportsMlsd ? $"MLSD {path}" : $"LIST {path}";

        var lines = new List<string>();
        await using (var data = await OpenDataAsync(command, token))
        using (var reader = new StreamReader(data, _encoding))
        {
            string? line;
            while ((line = await reader.ReadLineAsync(token)) is not null)
                if (line.Length > 0) lines.Add(line);
        }

        await CompleteTransferAsync(token);

        return SupportsMlsd
            ? FtpListParser.ParseMlsd(lines, path)
            : FtpListParser.ParseList(lines, path);
    }

    public async Task DownloadAsync(string remotePath, Stream destination,
        IProgress<long>? progress, CancellationToken token = default, long resumeFrom = 0)
    {
        await using var data = await OpenDataAsync($"RETR {remotePath}", token, resumeFrom);

        var buffer = new byte[TransferBufferSize];
        long total = resumeFrom;
        int read;

        while ((read = await data.ReadAsync(buffer, token)) > 0)
        {
            await destination.WriteAsync(buffer.AsMemory(0, read), token);
            total += read;
            progress?.Report(total);
        }

        await destination.FlushAsync(token);
        await data.DisposeAsync();
        await CompleteTransferAsync(token);
    }

    public async Task UploadAsync(Stream source, string remotePath,
        IProgress<long>? progress, CancellationToken token = default, bool append = false)
    {
        var command = append ? $"APPE {remotePath}" : $"STOR {remotePath}";
        await using var data = await OpenDataAsync(command, token);

        var buffer = new byte[TransferBufferSize];
        long total = 0;
        int read;

        while ((read = await source.ReadAsync(buffer, token)) > 0)
        {
            await data.WriteAsync(buffer.AsMemory(0, read), token);
            total += read;
            progress?.Report(total);
        }

        await data.FlushAsync(token);
        await data.DisposeAsync();
        await CompleteTransferAsync(token);
    }

    public async Task DeleteFileAsync(string remotePath, CancellationToken token = default) =>
        await ExpectAsync($"DELE {remotePath}", token, 250, 200);

    public async Task CreateDirectoryAsync(string remotePath, CancellationToken token = default) =>
        await ExpectAsync($"MKD {remotePath}", token, 257, 250);

    public async Task RemoveDirectoryAsync(string remotePath, CancellationToken token = default) =>
        await ExpectAsync($"RMD {remotePath}", token, 250, 200);

    public async Task RenameAsync(string from, string to, CancellationToken token = default)
    {
        await ExpectAsync($"RNFR {from}", token, 350);
        await ExpectAsync($"RNTO {to}", token, 250, 200);
    }

    public async Task<long> GetSizeAsync(string remotePath, CancellationToken token = default)
    {
        if (!SupportsSize) return -1;

        var reply = await SendAsync($"SIZE {remotePath}", token);
        return reply.Code == 213 && long.TryParse(reply.Text.Trim(), out long size) ? size : -1;
    }

    public async Task<bool> ExistsAsync(string remotePath, CancellationToken token = default)
    {
        if (await GetSizeAsync(remotePath, token) >= 0) return true;

        // SIZE fails on directories, so fall back to a probe with CWD.
        var current = await GetWorkingDirectoryAsync(token);
        var probe = await SendAsync($"CWD {remotePath}", token);
        if (probe.IsPositive) await SendAsync($"CWD {current}", token);
        return probe.IsPositive;
    }

    // ------------------------------------------------------------- teardown

    public async Task DisconnectAsync()
    {
        try
        {
            if (IsConnected) await SendAsync("QUIT", CancellationToken.None);
        }
        catch (Exception)
        {
            // The socket is going away regardless.
        }
        finally
        {
            Close();
        }
    }

    private void Close()
    {
        _reader?.Dispose();
        _stream?.Dispose();
        _control?.Dispose();

        _reader = null;
        _stream = null;
        _control = null;
        _protectData = false;
    }

    public async ValueTask DisposeAsync() => await DisconnectAsync();

    /// <summary>
    /// Wraps a data-channel stream so disposing it closes only that socket and
    /// leaves the control connection intact.
    /// </summary>
    private sealed class FtpDataStream : Stream
    {
        private readonly Stream _inner;
        private readonly TcpClient _client;
        private readonly FtpClient _owner;
        private bool _closed;

        public FtpDataStream(Stream inner, TcpClient client, FtpClient owner)
        {
            _inner = inner;
            _client = client;
            _owner = owner;
        }

        public override bool CanRead => _inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => _inner.CanWrite;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() => _inner.Flush();
        public override Task FlushAsync(CancellationToken token) => _inner.FlushAsync(token);
        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
        public override void Write(byte[] buffer, int offset, int count) => _inner.Write(buffer, offset, count);

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default) =>
            _inner.ReadAsync(buffer, token);

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken token = default) =>
            _inner.WriteAsync(buffer, token);

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (_closed) return;
            _closed = true;

            if (disposing)
            {
                try { _inner.Dispose(); } catch (Exception) { /* socket already gone */ }
                try { _client.Dispose(); } catch (Exception) { /* socket already gone */ }
            }

            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            if (_closed) return;
            _closed = true;

            try { await _inner.DisposeAsync(); } catch (Exception) { /* socket already gone */ }
            try { _client.Dispose(); } catch (Exception) { /* socket already gone */ }

            GC.SuppressFinalize(this);
        }
    }
}
