using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace AnimalHospitalOverlay;

public sealed record VisionCandidate(string Location, double Confidence, double ProcessingMilliseconds);

public sealed class VisionProbeService : IDisposable
{
    private Process? _process;
    public event Action<VisionCandidate>? CandidateReceived;
    public event Action<string>? StatusChanged;
    public bool IsRunning => _process is { HasExited: false };

    public bool Start()
    {
        if (IsRunning) return true;
        var root = FindProjectRoot();
        if (root is null)
        {
            StatusChanged?.Invoke("Vision files not found");
            return false;
        }
        var script = Path.Combine(root, "tools", "live_location_probe.py");
        var model = Path.Combine(root, "dataset", "analysis", "location_model.npz");
        if (!File.Exists(script) || !File.Exists(model))
        {
            StatusChanged?.Invoke("Vision model not built");
            return false;
        }
        try
        {
            _process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "python",
                    Arguments = $"\"{script}\"",
                    WorkingDirectory = root,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                },
                EnableRaisingEvents = true
            };
            _process.OutputDataReceived += (_, args) =>
            {
                if (!string.IsNullOrWhiteSpace(args.Data)) ParseLine(args.Data);
            };
            _process.ErrorDataReceived += (_, args) =>
            {
                if (!string.IsNullOrWhiteSpace(args.Data)) StatusChanged?.Invoke("Vision process error");
            };
            _process.Exited += (_, _) => StatusChanged?.Invoke("Vision stopped");
            _process.Start();
            _process.BeginOutputReadLine();
            _process.BeginErrorReadLine();
            StatusChanged?.Invoke("Vision starting…");
            return true;
        }
        catch
        {
            StatusChanged?.Invoke("Could not start vision");
            DisposeProcess();
            return false;
        }
    }

    private void ParseLine(string line)
    {
        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            var status = root.GetProperty("status").GetString();
            if (status == "ready")
            {
                StatusChanged?.Invoke("Vision ready");
                return;
            }
            if (status == "prediction")
            {
                CandidateReceived?.Invoke(new VisionCandidate(
                    root.GetProperty("location").GetString() ?? "Unknown",
                    root.GetProperty("confidence").GetDouble(),
                    root.GetProperty("processing_ms").GetDouble()));
            }
        }
        catch
        {
            StatusChanged?.Invoke("Vision output unreadable");
        }
    }

    public void Stop()
    {
        DisposeProcess();
        StatusChanged?.Invoke("Vision disabled");
    }

    private static string? FindProjectRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "tools", "live_location_probe.py")))
                return directory.FullName;
            directory = directory.Parent;
        }
        return null;
    }

    private void DisposeProcess()
    {
        if (_process is null) return;
        try
        {
            if (!_process.HasExited) _process.Kill(entireProcessTree: true);
        }
        catch { }
        _process.Dispose();
        _process = null;
    }

    public void Dispose() => DisposeProcess();
}
