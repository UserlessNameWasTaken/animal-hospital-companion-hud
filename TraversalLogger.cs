using System.Globalization;
using System.IO;
using System.Text;

namespace AnimalHospitalOverlay;

public sealed record TraversalRecord(
    DateTime StartedAt,
    DateTime CompletedAt,
    string From,
    string To,
    double EstimatedDeltaX,
    double EstimatedDeltaY,
    double DirectDistance,
    double PathDistance,
    double ElapsedSeconds,
    double MovingSeconds,
    double StartHeading,
    double EndHeading,
    double AbsoluteMouseCounts,
    double Sensitivity);

public sealed class TraversalLogger
{
    private readonly string _path;
    public int Count { get; private set; }
    public string Path => _path;

    public TraversalLogger()
    {
        var directory = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AnimalHospitalOverlay",
            "traversals");
        Directory.CreateDirectory(directory);
        _path = System.IO.Path.Combine(directory, $"session_{DateTime.Now:yyyyMMdd_HHmmss}.csv");
    }

    public void Append(TraversalRecord record)
    {
        if (!File.Exists(_path))
        {
            File.WriteAllText(_path,
                "started_at,completed_at,from,to,estimated_dx,estimated_dy,direct_distance," +
                "path_distance,elapsed_seconds,moving_seconds,start_heading,end_heading," +
                "absolute_mouse_counts,sensitivity\r\n",
                Encoding.UTF8);
        }

        var values = new[]
        {
            record.StartedAt.ToString("O"),
            record.CompletedAt.ToString("O"),
            Escape(record.From),
            Escape(record.To),
            Number(record.EstimatedDeltaX),
            Number(record.EstimatedDeltaY),
            Number(record.DirectDistance),
            Number(record.PathDistance),
            Number(record.ElapsedSeconds),
            Number(record.MovingSeconds),
            Number(record.StartHeading),
            Number(record.EndHeading),
            Number(record.AbsoluteMouseCounts),
            Number(record.Sensitivity)
        };
        File.AppendAllText(_path, string.Join(",", values) + "\r\n", Encoding.UTF8);
        Count++;
    }

    private static string Number(double value) =>
        value.ToString("0.######", CultureInfo.InvariantCulture);

    private static string Escape(string value) =>
        value.Contains(',') || value.Contains('"')
            ? $"\"{value.Replace("\"", "\"\"")}\""
            : value;
}
