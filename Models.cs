namespace AnimalHospitalOverlay;

public enum RoomPatientState
{
    Neutral,
    Safe,
    Anomaly
}

public enum RoomEventState
{
    Clear,
    Active
}

public sealed class RoomState
{
    public int Number { get; set; }
    public RoomPatientState Patient { get; set; }
    public RoomEventState Event { get; set; }
}

public sealed class ShiftRecord
{
    public DateTime CompletedAt { get; set; }
    public TimeSpan Duration { get; set; }
}

public sealed class OverlayState
{
    public List<RoomState> Rooms { get; set; } =
        Enumerable.Range(1, 8).Select(number => new RoomState { Number = number }).ToList();

    public List<ShiftRecord> ShiftHistory { get; set; } = [];
    public bool TallCoffeeUnlocked { get; set; }
    public int CurrentShiftNumber { get; set; } = 1;
    public double RobloxMouseSensitivity { get; set; } = 0.2;
}
