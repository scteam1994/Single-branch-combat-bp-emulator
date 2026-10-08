using System.Text.Json.Serialization;

namespace BPTrainer;

public enum BpActionType
{
    Ban,
    Pick
}

public enum BpSide
{
    Blue,
    Red
}

public class BpAction
{
    [JsonPropertyName("side")]
    public BpSide Side { get; set; }

    [JsonPropertyName("type")]
    public BpActionType Type { get; set; }

    [JsonPropertyName("heroId")]
    public string HeroId { get; set; } = "";

    [JsonPropertyName("timestamp")]
    public long Timestamp { get; set; }
}

public class BpState
{
    public List<string> BlueBans { get; set; } = new();
    public List<string> RedBans { get; set; } = new();
    public List<string> BluePicks { get; set; } = new();
    public List<string> RedPicks { get; set; } = new();
    public List<string> SystemBans { get; set; } = new();

    public BpSide CurrentSide { get; set; } = BpSide.Red;
    public int CurrentStep { get; set; } = 0;

    public List<BpAction> ActionHistory { get; set; } = new();

    public bool IsFinished { get; set; } = false;

    private readonly BpConfig _config;

    public BpState(BpConfig config)
    {
        _config = config;
    }

    public (bool valid, string error) ValidateAction(BpAction action)
    {
        if (IsFinished)
            return (false, "对局已结束");

        if (action.Side != CurrentSide)
            return (false, "非当前操作方");

        var expectedType = GetCurrentActionType();
        if (action.Type != expectedType)
            return (false, $"应为{expectedType}操作");

        if (action.Type == BpActionType.Ban)
        {
            var ownBans = action.Side == BpSide.Blue ? BlueBans : RedBans;
            if (ownBans.Contains(action.HeroId) || SystemBans.Contains(action.HeroId))
                return (false, "该单位已被禁用");
        }
        else
        {
            if (IsHeroUnavailableForSide(action.HeroId, action.Side))
                return (false, "该单位不可选用");
        }

        var list = GetTargetList(action);
        if (list.Count >= GetMaxCount(action))
            return (false, "该槽位已满");

        return (true, "");
    }

    public void ApplyAction(BpAction action)
    {
        var list = GetTargetList(action);
        list.Add(action.HeroId);
        ActionHistory.Add(action);
        CurrentStep++;

        if (CurrentStep >= _config.TotalSteps)
        {
            IsFinished = true;
        }
        else
        {
            CurrentSide = _config.SideSequence[CurrentStep];
        }
    }

    public BpActionType GetCurrentActionType()
    {
        return _config.ActionSequence[CurrentStep];
    }

    private List<string> GetTargetList(BpAction action)
    {
        return (action.Side, action.Type) switch
        {
            (BpSide.Blue, BpActionType.Ban) => BlueBans,
            (BpSide.Red, BpActionType.Ban) => RedBans,
            (BpSide.Blue, BpActionType.Pick) => BluePicks,
            (BpSide.Red, BpActionType.Pick) => RedPicks,
            _ => throw new InvalidOperationException()
        };
    }

    private int GetMaxCount(BpAction action)
    {
        return (action.Side, action.Type) switch
        {
            (BpSide.Blue, BpActionType.Ban) => _config.BlueBanCount,
            (BpSide.Red, BpActionType.Ban) => _config.RedBanCount,
            (BpSide.Blue, BpActionType.Pick) => _config.BluePickCount,
            (BpSide.Red, BpActionType.Pick) => _config.RedPickCount,
            _ => 0
        };
    }

    public bool IsHeroUnavailableForSide(string heroId, BpSide side)
    {
        if (SystemBans.Contains(heroId)) return true;
        var opponentBans = side == BpSide.Blue ? RedBans : BlueBans;
        if (opponentBans.Contains(heroId)) return true;
        var ownPicks = side == BpSide.Blue ? BluePicks : RedPicks;
        return ownPicks.Contains(heroId);
    }

    public bool IsHeroActionable(string heroId, BpSide side, BpActionType type)
    {
        if (type == BpActionType.Ban)
        {
            var ownBans = side == BpSide.Blue ? BlueBans : RedBans;
            return !ownBans.Contains(heroId) && !SystemBans.Contains(heroId);
        }
        return !IsHeroUnavailableForSide(heroId, side);
    }

    public BpStateSnapshot GetSnapshot()
    {
        return new BpStateSnapshot
        {
            BlueBans = new List<string>(BlueBans),
            RedBans = new List<string>(RedBans),
            BluePicks = new List<string>(BluePicks),
            RedPicks = new List<string>(RedPicks),
            SystemBans = new List<string>(SystemBans),
            CurrentSide = CurrentSide,
            CurrentStep = CurrentStep,
            IsFinished = IsFinished,
            ActionHistory = new List<BpAction>(ActionHistory)
        };
    }

    public void LoadSnapshot(BpStateSnapshot snapshot)
    {
        BlueBans = new List<string>(snapshot.BlueBans);
        RedBans = new List<string>(snapshot.RedBans);
        BluePicks = new List<string>(snapshot.BluePicks);
        RedPicks = new List<string>(snapshot.RedPicks);
        SystemBans = new List<string>(snapshot.SystemBans);
        CurrentSide = snapshot.CurrentSide;
        CurrentStep = snapshot.CurrentStep;
        IsFinished = snapshot.IsFinished;
        ActionHistory = new List<BpAction>(snapshot.ActionHistory);
    }
}

public class BpStateSnapshot
{
    public List<string> BlueBans { get; set; } = new();
    public List<string> RedBans { get; set; } = new();
    public List<string> BluePicks { get; set; } = new();
    public List<string> RedPicks { get; set; } = new();
    public List<string> SystemBans { get; set; } = new();
    public BpSide CurrentSide { get; set; }
    public int CurrentStep { get; set; }
    public bool IsFinished { get; set; }
    public List<BpAction> ActionHistory { get; set; } = new();
}

public class BpConfig
{
    public int BlueBanCount { get; set; } = 2;
    public int RedBanCount { get; set; } = 2;
    public int BluePickCount { get; set; } = 5;
    public int RedPickCount { get; set; } = 5;
    public int SystemBanCount { get; set; } = 8;

    public List<BpActionType> ActionSequence { get; set; } = new()
    {
        BpActionType.Pick,
        BpActionType.Pick,
        BpActionType.Pick,
        BpActionType.Pick,
        BpActionType.Ban,
        BpActionType.Ban,
        BpActionType.Pick,
        BpActionType.Pick,
        BpActionType.Pick,
        BpActionType.Pick,
        BpActionType.Ban,
        BpActionType.Ban,
        BpActionType.Pick,
        BpActionType.Pick
    };

    public List<BpSide> SideSequence { get; set; } = new()
    {
        BpSide.Red,
        BpSide.Blue,
        BpSide.Blue,
        BpSide.Red,
        BpSide.Blue,
        BpSide.Red,
        BpSide.Red,
        BpSide.Blue,
        BpSide.Blue,
        BpSide.Red,
        BpSide.Blue,
        BpSide.Red,
        BpSide.Red,
        BpSide.Blue
    };

    public int TotalSteps => ActionSequence.Count;

    public int ActionTimerSeconds { get; set; } = 45;
    public int BackupTimerSeconds { get; set; } = 45;
}
