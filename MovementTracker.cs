using System.Windows.Input;

namespace AnimalHospitalOverlay;

public sealed class MovementTracker
{
    private static readonly Dictionary<string, (double X, double Y)> Landmarks = new()
    {
        // Doorway-based geometry fitted from the refined traversal session.
        // Hall Center is the origin; Reception is the vertical spine and
        // Office branches west from that spine.
        ["Hall Center"] = (0.000, 0.000),
        ["Office"] = (-0.278, 0.444),
        ["Reception"] = (-0.084, 1.231),

        // Wing anchors are corridor thresholds, not room centers.
        ["Emergency Bay"] = (-0.478, -0.389),
        ["Room 6"] = (-1.235, -0.815),
        ["Room 7"] = (-1.308, -0.088),
        ["Room 8"] = (-2.604, -0.503),

        ["Medical Bay"] = (0.455, -0.364),
        ["Room 1"] = (0.996, -0.687),
        ["Room 2"] = (0.953, 0.082),
        ["Room 3"] = (2.152, -0.560),
        ["Room 4"] = (2.116, 0.206),
        ["Room 5"] = (2.570, -0.198)
    };

    private readonly object _gate = new();
    private readonly HashSet<Key> _down = [];
    private double _pendingMouseX;
    private double _distanceSinceAnchor;
    private double _movingSeconds;
    private double _absoluteMouseCounts;
    private double _anchorStartX;
    private double _anchorStartY;
    private double _anchorStartHeading;
    private DateTime _lastAnchorAt;

    public bool Enabled { get; set; }
    public double X { get; private set; }
    public double Y { get; private set; }
    public double HeadingDegrees { get; private set; }
    public string Anchor { get; private set; } = "Unknown";
    public double MouseSensitivity { get; set; } = 0.2;

    public void SetKey(Key key, bool isDown)
    {
        if (key is not (Key.W or Key.A or Key.S or Key.D or Key.LeftShift or Key.RightShift))
            return;
        lock (_gate)
        {
            if (isDown) _down.Add(key);
            else _down.Remove(key);
        }
    }

    public void AddMouseDelta(int dx)
    {
        if (!Enabled)
            return;
        lock (_gate)
        {
            _pendingMouseX += dx;
            _absoluteMouseCounts += Math.Abs(dx);
        }
    }

    public TraversalRecord? AnchorAt(string location)
    {
        if (!Landmarks.TryGetValue(location, out var point))
            return null;
        lock (_gate)
        {
            TraversalRecord? completed = null;
            var now = DateTime.Now;
            if (Anchor != "Unknown" && Anchor != location && _distanceSinceAnchor >= 0.05)
            {
                var deltaX = X - _anchorStartX;
                var deltaY = Y - _anchorStartY;
                completed = new TraversalRecord(
                    _lastAnchorAt,
                    now,
                    Anchor,
                    location,
                    deltaX,
                    deltaY,
                    Math.Sqrt(deltaX * deltaX + deltaY * deltaY),
                    _distanceSinceAnchor,
                    (now - _lastAnchorAt).TotalSeconds,
                    _movingSeconds,
                    _anchorStartHeading,
                    HeadingDegrees,
                    _absoluteMouseCounts,
                    MouseSensitivity);
            }

            X = point.X;
            Y = point.Y;
            Anchor = location;
            _distanceSinceAnchor = 0;
            _movingSeconds = 0;
            _absoluteMouseCounts = 0;
            _anchorStartX = X;
            _anchorStartY = Y;
            _anchorStartHeading = HeadingDegrees;
            _lastAnchorAt = now;
            return completed;
        }
    }

    public void SetHeading(double degrees)
    {
        lock (_gate)
        {
            HeadingDegrees = (degrees % 360.0 + 360.0) % 360.0;
            _pendingMouseX = 0;
            _anchorStartHeading = HeadingDegrees;
            _absoluteMouseCounts = 0;
        }
    }

    public void Update(TimeSpan elapsed, bool robloxForeground)
    {
        lock (_gate)
        {
            if (!Enabled || !robloxForeground)
            {
                _down.Clear();
                _pendingMouseX = 0;
                return;
            }

            // Roblox's default camera uses roughly half a degree per raw mouse
            // count at sensitivity 1.0, scaled linearly by UserGameSettings.
            HeadingDegrees = (HeadingDegrees + _pendingMouseX * 0.5 * MouseSensitivity + 360.0) % 360.0;
            _pendingMouseX = 0;

            var forward = (_down.Contains(Key.W) ? 1.0 : 0.0) - (_down.Contains(Key.S) ? 1.0 : 0.0);
            var strafe = (_down.Contains(Key.D) ? 1.0 : 0.0) - (_down.Contains(Key.A) ? 1.0 : 0.0);
            if (forward == 0 && strafe == 0)
                return;

            var magnitude = Math.Sqrt(forward * forward + strafe * strafe);
            forward /= magnitude;
            strafe /= magnitude;
            var radians = HeadingDegrees * Math.PI / 180.0;
            var speed = (_down.Contains(Key.LeftShift) || _down.Contains(Key.RightShift)) ? 0.8 : 0.48;
            var dx = (Math.Sin(radians) * forward + Math.Cos(radians) * strafe) * speed * elapsed.TotalSeconds;
            var dy = (-Math.Cos(radians) * forward + Math.Sin(radians) * strafe) * speed * elapsed.TotalSeconds;
            X += dx;
            Y += dy;
            _distanceSinceAnchor += Math.Sqrt(dx * dx + dy * dy);
            _movingSeconds += elapsed.TotalSeconds;
        }
    }

    public (string Location, int Confidence, string Facing, string Nearest) Snapshot()
    {
        lock (_gate)
        {
            if (Anchor == "Unknown")
                return ("Unknown — set an anchor", 0, Compass(HeadingDegrees), "Unknown");

            var nearest = Landmarks.MinBy(item =>
                Math.Pow(item.Value.X - X, 2) + Math.Pow(item.Value.Y - Y, 2));
            var nearestDistance = Math.Sqrt(
                Math.Pow(nearest.Value.X - X, 2) + Math.Pow(nearest.Value.Y - Y, 2));
            var agePenalty = Math.Max(0, (DateTime.Now - _lastAnchorAt).TotalMinutes - 2) * 4;
            var confidence = (int)Math.Clamp(100 - _distanceSinceAnchor * 18 - agePenalty, 0, 100);
            var location = nearestDistance <= 1.15 ? nearest.Key : $"Near {Anchor}";
            return (location, confidence, Compass(HeadingDegrees), nearest.Key);
        }
    }

    private static string Compass(double heading) => heading switch
    {
        >= 337.5 or < 22.5 => "N",
        < 67.5 => "NE", < 112.5 => "E", < 157.5 => "SE",
        < 202.5 => "S", < 247.5 => "SW", < 292.5 => "W", _ => "NW"
    };
}
