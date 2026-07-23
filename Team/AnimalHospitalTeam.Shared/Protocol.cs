namespace AnimalHospitalTeam.Shared;

public enum PatientState { Neutral, Safe, Anomaly }
public enum EventState { Clear, Active }

public sealed class SharedRoomState
{
    public int Number { get; set; }
    public PatientState Patient { get; set; }
    public EventState Event { get; set; }
    public string? UpdatedBy { get; set; }
}

public sealed class TeamState
{
    public long Revision { get; set; }
    public int ShiftNumber { get; set; } = 1;
    public bool ShiftRunning { get; set; }
    public DateTime? ShiftStartedAtUtc { get; set; }
    public DateTime? SmallCoffeeReadyAtUtc { get; set; }
    public DateTime? TallCoffeeReadyAtUtc { get; set; }
    public bool TallCoffeeUnlocked { get; set; }
    public List<SharedRoomState> Rooms { get; set; } =
        Enumerable.Range(1, 8).Select(n => new SharedRoomState { Number = n }).ToList();
    public List<string> Members { get; set; } = [];
    public string? LastChangedBy { get; set; }
    public string? LastAction { get; set; }
}

public sealed class ClientAction
{
    public string ActionId { get; set; } = Guid.NewGuid().ToString("N");
    public string Type { get; set; } = "";
    public int? Room { get; set; }
    public string? Value { get; set; }
}

public sealed class ServerEnvelope
{
    public string Type { get; set; } = "state";
    public TeamState? State { get; set; }
    public string? Message { get; set; }
}

public sealed class CreateRoomResponse
{
    public string Code { get; set; } = "";
    public string Secret { get; set; } = "";
}
