using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text.Json;

namespace PowerModeToggle;

/// <summary>
/// Keeps the tray process unelevated while a same-user helper performs the
/// privileged Armoury Crate service and machine power-profile changes.
/// </summary>
internal sealed class PowerProfileBroker : IDisposable
{
    private readonly SemaphoreSlim _requestLock = new(1, 1);
    private readonly string _pipeName = $"{AppIdentity.PowerHelperPipePrefix}.{Environment.ProcessId}.{Guid.NewGuid():N}";

    private NamedPipeServerStream? _pipe;
    private StreamReader? _reader;
    private StreamWriter? _writer;
    private Process? _helperProcess;
    private bool _disposed;

    public async Task<PowerProfileApplyResult> ApplyAsync(LaptopPowerMode mode)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (IsElevated())
        {
            return await Task.Run(() => PowerProfileService.Apply(mode));
        }

        await _requestLock.WaitAsync();
        try
        {
            await EnsureHelperConnectedAsync();
            await _writer!.WriteLineAsync(mode.ToString());

            var response = await _reader!.ReadLineAsync();
            if (string.IsNullOrWhiteSpace(response))
            {
                ResetConnection();
                throw new InvalidOperationException("The elevated power-profile helper stopped unexpectedly.");
            }

            return JsonSerializer.Deserialize<PowerProfileApplyResult>(response)
                   ?? throw new InvalidOperationException("The elevated power-profile helper returned an invalid response.");
        }
        catch (IOException ex)
        {
            ResetConnection();
            throw new InvalidOperationException("Communication with the elevated power-profile helper failed.", ex);
        }
        finally
        {
            _requestLock.Release();
        }
    }

    public static bool TryRunElevatedHelper(string[] arguments)
    {
        if (arguments.Length != 2
            || !string.Equals(arguments[0], "--power-helper", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!IsElevated())
        {
            return true;
        }

        try
        {
            using var pipe = new NamedPipeClientStream(
                ".",
                arguments[1],
                PipeDirection.InOut,
                PipeOptions.Asynchronous);
            pipe.Connect(60_000);

            using var reader = new StreamReader(pipe);
            using var writer = new StreamWriter(pipe) { AutoFlush = true };
            while (reader.ReadLine() is { } request)
            {
                if (!Enum.TryParse<LaptopPowerMode>(request, ignoreCase: true, out var mode))
                {
                    writer.WriteLine(JsonSerializer.Serialize(new PowerProfileApplyResult(
                        LaptopPowerMode.LowPower,
                        PowerProfileService.ReadState(),
                        ["The requested power profile was not recognized."])));
                    continue;
                }

                writer.WriteLine(JsonSerializer.Serialize(PowerProfileService.Apply(mode)));
            }
        }
        catch
        {
            // The tray process reports a disconnected helper; this helper has no UI.
        }

        return true;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        ResetConnection();
        _requestLock.Dispose();
    }

    private async Task EnsureHelperConnectedAsync()
    {
        if (_pipe?.IsConnected == true)
        {
            return;
        }

        ResetConnection();
        _pipe = new NamedPipeServerStream(
            _pipeName,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

        var executablePath = Environment.ProcessPath
                             ?? throw new InvalidOperationException($"The {AppIdentity.DisplayName} executable path is unavailable.");

        try
        {
            _helperProcess = Process.Start(new ProcessStartInfo
            {
                FileName = executablePath,
                Arguments = $"--power-helper {_pipeName}",
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden
            }) ?? throw new InvalidOperationException("Windows did not start the elevated power-profile helper.");
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            ResetConnection();
            throw new InvalidOperationException("Administrator approval was canceled; the power profile was not changed.", ex);
        }
        catch
        {
            ResetConnection();
            throw;
        }

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        try
        {
            await _pipe.WaitForConnectionAsync(timeout.Token);
        }
        catch (OperationCanceledException ex)
        {
            ResetConnection();
            throw new TimeoutException("The elevated power-profile helper did not connect in time.", ex);
        }

        _reader = new StreamReader(_pipe);
        _writer = new StreamWriter(_pipe) { AutoFlush = true };
    }

    private void ResetConnection()
    {
        _writer?.Dispose();
        _reader?.Dispose();
        _pipe?.Dispose();
        _helperProcess?.Dispose();
        _writer = null;
        _reader = null;
        _pipe = null;
        _helperProcess = null;
    }

    private static bool IsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }
}
