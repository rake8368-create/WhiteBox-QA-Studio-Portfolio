using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace WhiteBoxQA.Infrastructure;

internal sealed class GitProcess(Action<ProcessStartInfo>? configureProcess)
{
    public int Run(string home, string directory, string[] args, CancellationToken token,
        Action<Process, Stream>? read = null, bool redirectInput = false)
    {
        token.ThrowIfCancellationRequested();
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = directory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = redirectInput,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        if (redirectInput)
            start.StandardInputEncoding = Encoding.ASCII;

        foreach (var key in start.Environment.Keys.Where(key => key.StartsWith("GIT_", StringComparison.OrdinalIgnoreCase)).ToArray())
            start.Environment.Remove(key);

        start.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
        start.Environment["GIT_CONFIG_GLOBAL"] = OperatingSystem.IsWindows() ? "NUL" : "/dev/null";
        start.Environment["GIT_ALLOW_PROTOCOL"] = "https";
        start.Environment["GIT_TERMINAL_PROMPT"] = "0";
        // libcurl may consult a netrc file even when Git credential helpers are disabled.
        start.Environment.Remove("NETRC");
        foreach (var variable in new[] { "HOME", "USERPROFILE", "XDG_CONFIG_HOME", "CURL_HOME" })
            start.Environment[variable] = home;

        foreach (var arg in new[] { "-c", "http.followRedirects=false", "-c", "credential.helper=" })
            start.ArgumentList.Add(arg);
        foreach (var arg in args)
            start.ArgumentList.Add(arg);

        configureProcess?.Invoke(start); // Used for local Git fixtures and deterministic cancellation tests.
        using var process = Process.Start(start) ?? throw new IOException();
        var discardError = process.StandardError.BaseStream.CopyToAsync(Stream.Null);
        using var termination = token.Register(() => Kill(process));

        try
        {
            using var output = new BufferedStream(process.StandardOutput.BaseStream);
            if (read is not null)
                read(process, output);
            else
                output.CopyTo(Stream.Null);

            process.WaitForExit();
            discardError.GetAwaiter().GetResult();
            token.ThrowIfCancellationRequested();
            return process.ExitCode;
        }
        catch
        {
            Kill(process);
            process.WaitForExit();
            discardError.GetAwaiter().GetResult();
            token.ThrowIfCancellationRequested();
            throw;
        }
    }

    public static byte[]? ReadRecord(Stream input, byte delimiter, int maxBytes, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var record = new MemoryStream();
        int next;
        while ((next = input.ReadByte()) != -1 && next != delimiter)
        {
            if (record.Length == maxBytes)
                throw new InvalidDataException();
            record.WriteByte((byte)next);
        }
        token.ThrowIfCancellationRequested();

        return next == delimiter ? record.ToArray()
            : record.Length == 0 ? null : throw new EndOfStreamException();
    }

    private static void Kill(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException) when (process.HasExited) { }
        catch (Win32Exception) when (process.HasExited) { }
    }
}
