using CsDemoMap.Api.Models;

namespace CsDemoMap.Api.Services;

public sealed class RoundRosterTracker
{
    private readonly Dictionary<string, RosterObservation> members = [];
    private readonly HashSet<string> deaths = [];

    public void Reset() { members.Clear(); deaths.Clear(); }
    public void RecordDeath(string id, string phase = "live")
    {
        // Freeze-time disconnect/suicide can be followed by a respawn before live.
        // Only competitive live deaths are persistent evidence for this attempt.
        if (phase is "live" or "post-plant") deaths.Add(id);
    }

    public RosterQuality Observe(IEnumerable<RosterObservation> observations)
    {
        var seen = observations.Where(p => p.Team is "T" or "CT")
            .GroupBy(p => p.Id).Select(g => g.Last()).ToDictionary(p => p.Id);
        foreach (var id in members.Keys.ToArray())
            if (!seen.ContainsKey(id))
                members[id] = members[id] with
                {
                    Alive = deaths.Contains(id) || members[id].Alive == false ? false : null,
                    PositionKnown = false, EquipmentKnown = false
                };
        foreach (var p in seen.Values)
            members[p.Id] = p with
            {
                Alive = deaths.Contains(p.Id) ? false : p.Alive ??
                    (members.TryGetValue(p.Id, out var old) && old.Alive == false ? false : null)
            };

        var t = members.Values.Count(p => p.Team == "T");
        var ct = members.Values.Count(p => p.Team == "CT");
        var known = members.Values.Count(p => p.Alive is not null);
        var alive = members.Values.Where(p => p.Alive == true).ToArray();
        var positions = alive.Count(p => p.PositionKnown);
        var equipment = alive.Count(p => p.EquipmentKnown);
        var reasons = new List<string>();
        if (t != 5 || ct != 5 || members.ContainsKey("0")) reasons.Add("roster-unconfirmed");
        if (known != members.Count) reasons.Add("life-unknown");
        if (positions != alive.Length) reasons.Add("alive-position-unknown");
        if (equipment != alive.Length) reasons.Add("alive-equipment-unknown");
        var usable = !reasons.Contains("roster-unconfirmed") && !reasons.Contains("life-unknown");
        return new(t, ct, known, alive.Length, positions, equipment,
            t == 5 && ct == 5 && !members.ContainsKey("0"),
            !usable ? "unusable" : reasons.Count > 0 ? "partial" : "usable", reasons);
    }
}
