using LinqToDB;
using LinqToDB.Async;
using GW2RaidStats.Core;
using GW2RaidStats.Core.Roles;
using GW2RaidStats.Infrastructure.Database;
using GW2RaidStats.Infrastructure.Database.Entities;

namespace GW2RaidStats.Infrastructure.Services;

/// <summary>
/// Pure-compute service that builds a 10-person squad composition for a set of bosses.
///
/// Slot model (10 total):
///   Sub 1: AlacHeal, QuickBoonDps, PureDps×3
///   Sub 2: QuickHeal, AlacBoonDps, PureDps×3
///
/// Algorithm: greedy hardest-first base-role assignment (heals before boon-dps before dps),
/// then per-boss mechanic layering. Mechanic conflicts are reported, not auto-resolved —
/// the UI prompts the user to accept a reset, which calls BuildAsync again with a tail
/// boss set + a forced mechanic requirement.
///
/// "Minimize swaps" is satisfied by structure: base roles are solved once for the whole set.
/// </summary>
public class SquadRandomizerService
{
    private readonly RaidStatsDb _db;

    public SquadRandomizerService(RaidStatsDb db)
    {
        _db = db;
    }

    private const int MaxAttempts = 20;

    public async Task<SquadBuildResult> BuildAsync(SquadBuildRequest req, CancellationToken ct = default)
    {
        if (req.PlayerIds.Count + req.PugCount > 10)
        {
            throw new ArgumentException("Player count + pug count cannot exceed 10");
        }
        if (req.PlayerIds.Count == 0 && req.PugCount == 0)
        {
            throw new ArgumentException("At least one player or pug must be selected");
        }

        // Load data once; per-attempt work is pure in-memory compute.
        var players = await _db.Players
            .Where(p => req.PlayerIds.Contains(p.Id))
            .Select(p => new { p.Id, p.AccountName })
            .ToListAsync(ct);

        var nameById = players.ToDictionary(p => p.Id, p => p.AccountName);

        var capRows = await _db.PlayerRoleCapabilities
            .Where(c => req.PlayerIds.Contains(c.PlayerId))
            .ToListAsync(ct);

        var allMechanics = await _db.MechanicRoles.ToListAsync(ct);
        var mechanicsByBoss = allMechanics
            .GroupBy(m => m.TriggerId)
            .ToDictionary(g => g.Key, g => g.OrderBy(m => m.SortOrder).ThenBy(m => m.Name).ToList());

        var capability = new CapabilityIndex(capRows);

        // Run multiple attempts with different RNG seeds, score each, return the best.
        // Score = (unfilled mechanic slots, total Maybe fallbacks); lower is better; lex compare.
        // Early-exit on a perfect result (zero conflicts, zero Maybe fallbacks).
        var baseSeed = req.Seed ?? Environment.TickCount;
        SquadBuildResult? best = null;
        int bestUnfilled = int.MaxValue;
        int bestMaybes = int.MaxValue;

        for (int i = 0; i < MaxAttempts; i++)
        {
            var rng = new Random(unchecked(baseSeed + i * 31));
            var (result, totalMaybes) = SolveOnce(req, capability, allMechanics, mechanicsByBoss, nameById, rng);
            var unfilled = result.Conflicts.Sum(c => c.Required - c.Filled);

            var isBetter = unfilled < bestUnfilled
                || (unfilled == bestUnfilled && totalMaybes < bestMaybes);

            if (best == null || isBetter)
            {
                best = result;
                bestUnfilled = unfilled;
                bestMaybes = totalMaybes;
            }

            if (unfilled == 0 && totalMaybes == 0) break;
        }

        return best!;
    }

    private (SquadBuildResult Result, int TotalMaybeFallbacks) SolveOnce(
        SquadBuildRequest req,
        CapabilityIndex capability,
        List<MechanicRoleEntity> allMechanics,
        Dictionary<int, List<MechanicRoleEntity>> mechanicsByBoss,
        Dictionary<Guid, string> nameById,
        Random rng)
    {
        // If reset flow asked for a mechanic to be coverable, pre-pin capable players to compatible
        // base roles before the greedy solver runs.
        var locks = new Dictionary<Guid, GenericRole>(req.Locks ?? new());
        if (req.ForceCoverableMechanicId.HasValue)
        {
            var mech = allMechanics.FirstOrDefault(m => m.Id == req.ForceCoverableMechanicId.Value);
            if (mech != null)
            {
                AddForceCoverageLocks(mech, req.PlayerIds, capability, locks);
            }
        }

        // Solve base roles (locks honored, hardest-first greedy with random tie-break)
        var (subGroups, leftoverPlayers, baseMaybes) = SolveBaseRoles(
            req.PlayerIds, capability, locks, nameById, rng);

        var pugDpsCount = Math.Min(req.PugCount, 10 - req.PlayerIds.Count + leftoverPlayers.Count);

        // Layer mechanics per boss
        var perBoss = new List<BossAssignmentDto>();
        var conflicts = new List<SquadConflictDto>();
        var warnings = new List<string>();
        var mechMaybes = 0;

        var assignedSquad = subGroups
            .SelectMany(s => s.Slots)
            .Where(s => s.PlayerId.HasValue && s.Role.HasValue)
            .ToList();

        foreach (var triggerId in req.BossTriggerIds)
        {
            if (!mechanicsByBoss.TryGetValue(triggerId, out var bossMechanics) || bossMechanics.Count == 0)
            {
                continue;
            }

            var bossName = bossMechanics[0].BossName;
            var bossWing = WingMapping.GetWing(triggerId);
            var bossOrder = WingMapping.GetEncounterOrder(triggerId);

            var alreadyAssignedOnThisBoss = new HashSet<Guid>();
            var mechanicAssignments = new List<MechanicAssignmentDto>();

            foreach (var mechanic in bossMechanics)
            {
                var (assignedSlots, blocked) = AssignMechanic(
                    mechanic, assignedSquad, capability, alreadyAssignedOnThisBoss, nameById, rng);

                foreach (var slot in assignedSlots)
                {
                    if (slot.PlayerId.HasValue) alreadyAssignedOnThisBoss.Add(slot.PlayerId.Value);
                    if (slot.StatusUsed == RoleCapabilityStatus.Maybe) mechMaybes++;
                }

                mechanicAssignments.Add(new MechanicAssignmentDto(
                    mechanic.Id,
                    mechanic.Name,
                    (MechanicConstraint)mechanic.SlotConstraint,
                    mechanic.MinCount,
                    mechanic.MaxCount,
                    assignedSlots));

                var filled = assignedSlots.Count(s => s.PlayerId.HasValue);
                if (filled < mechanic.MinCount)
                {
                    conflicts.Add(new SquadConflictDto(
                        triggerId,
                        bossName,
                        mechanic.Id,
                        mechanic.Name,
                        mechanic.MinCount,
                        filled,
                        blocked));
                }
            }

            // Conjured Amalgamate warning: both Sword and Shield assigned to healers
            if (triggerId == 43974)
            {
                var swordShield = mechanicAssignments
                    .Where(m => m.Name.Equals("Sword", StringComparison.OrdinalIgnoreCase)
                                || m.Name.Equals("Shield", StringComparison.OrdinalIgnoreCase))
                    .SelectMany(m => m.AssignedPlayers)
                    .Where(p => p.PlayerId.HasValue)
                    .Select(p => assignedSquad.First(s => s.PlayerId == p.PlayerId).Role!.Value)
                    .ToList();
                if (swordShield.Count >= 2 && swordShield.All(r => r.GetSlot() == RoleSlot.Heal))
                {
                    warnings.Add("Conjured Amalgamate: both Sword and Shield are healers — review composition.");
                }
            }

            perBoss.Add(new BossAssignmentDto(
                triggerId,
                bossName,
                bossWing ?? 99,
                bossOrder,
                mechanicAssignments));
        }

        var result = new SquadBuildResult(
            new SquadAssignmentDto(subGroups, pugDpsCount, perBoss),
            conflicts,
            warnings);

        return (result, baseMaybes + mechMaybes);
    }

    // --- Force-coverage helper ---

    /// <summary>
    /// When the user accepts a reset for a conflict mechanic, pre-pin up to MinCount capable
    /// players to base roles whose slot satisfies the mechanic constraint. Skips players already
    /// locked, and prefers Can over Maybe.
    /// </summary>
    private static void AddForceCoverageLocks(
        MechanicRoleEntity mech,
        List<Guid> playerIds,
        CapabilityIndex capability,
        Dictionary<Guid, GenericRole> locks)
    {
        var constraint = (MechanicConstraint)mech.SlotConstraint;
        var availableRoles = Enum.GetValues<GenericRole>()
            .Where(r => constraint.IsSlotAllowed(r.GetSlot()))
            .ToList();

        if (availableRoles.Count == 0) return;

        // Rank candidates: Can-on-mechanic before Maybe; players with the fewest compatible base roles first
        var candidates = playerIds
            .Where(pid => !locks.ContainsKey(pid))
            .Select(pid => new
            {
                PlayerId = pid,
                MechStatus = capability.MechanicStatus(pid, mech.Id),
                BaseOptions = availableRoles
                    .Where(r =>
                    {
                        var s = capability.GenericStatus(pid, r);
                        return s == RoleCapabilityStatus.Can || s == RoleCapabilityStatus.Maybe;
                    })
                    .OrderBy(r =>
                    {
                        var s = capability.GenericStatus(pid, r);
                        return s == RoleCapabilityStatus.Can ? 0 : 1;
                    })
                    .ToList()
            })
            .Where(x => x.MechStatus == RoleCapabilityStatus.Can || x.MechStatus == RoleCapabilityStatus.Maybe)
            .Where(x => x.BaseOptions.Count > 0)
            .OrderBy(x => x.MechStatus == RoleCapabilityStatus.Can ? 0 : 1)
            .ThenBy(x => x.BaseOptions.Count)
            .ToList();

        var pinned = 0;
        foreach (var c in candidates)
        {
            if (pinned >= mech.MinCount) break;
            // Pick the player's first viable base role; SolveBaseRoles will route them to a free slot
            locks[c.PlayerId] = c.BaseOptions[0];
            pinned++;
        }
    }

    // --- Base role solving ---

    private (List<SubGroupDto> SubGroups, List<Guid> Leftover, int MaybeFallbacks) SolveBaseRoles(
        List<Guid> playerIds,
        CapabilityIndex capability,
        Dictionary<Guid, GenericRole> locks,
        Dictionary<Guid, string> nameById,
        Random rng)
    {
        // Slot definitions in priority order (hardest-to-fill first).
        // Each slot belongs to a sub-group (1 or 2). Sub 1: AlacHeal + QuickBoonDps. Sub 2: QuickHeal + AlacBoonDps.
        var slotDefs = new List<SlotDef>
        {
            new(1, "Heal",     new[] { GenericRole.AlacHeal }),
            new(2, "Heal",     new[] { GenericRole.QuickHeal }),
            new(1, "BoonDps",  new[] { GenericRole.QuickDpsPower, GenericRole.QuickDpsCondi }),
            new(2, "BoonDps",  new[] { GenericRole.AlacDpsPower, GenericRole.AlacDpsCondi }),
            new(1, "Dps",      new[] { GenericRole.DpsPower, GenericRole.DpsCondi }),
            new(1, "Dps",      new[] { GenericRole.DpsPower, GenericRole.DpsCondi }),
            new(1, "Dps",      new[] { GenericRole.DpsPower, GenericRole.DpsCondi }),
            new(2, "Dps",      new[] { GenericRole.DpsPower, GenericRole.DpsCondi }),
            new(2, "Dps",      new[] { GenericRole.DpsPower, GenericRole.DpsCondi }),
            new(2, "Dps",      new[] { GenericRole.DpsPower, GenericRole.DpsCondi }),
        };

        var assignments = new List<(SlotDef Def, Guid? PlayerId, GenericRole? Role)>();
        var unassigned = new HashSet<Guid>(playerIds);
        var maybeFallbacks = 0;

        // Pre-apply locks
        var lockedSlotIndices = new HashSet<int>();
        foreach (var (playerId, lockedRole) in locks)
        {
            if (!unassigned.Contains(playerId)) continue;
            // Find the first slot whose role list includes lockedRole
            var idx = slotDefs.FindIndex(s => s.AcceptsRole(lockedRole) && !lockedSlotIndices.Contains(slotDefs.IndexOf(s)));
            if (idx < 0) continue;
            assignments.Add((slotDefs[idx], playerId, lockedRole));
            lockedSlotIndices.Add(idx);
            unassigned.Remove(playerId);
        }

        // Solve remaining slots in order
        for (int i = 0; i < slotDefs.Count; i++)
        {
            if (lockedSlotIndices.Contains(i)) continue;

            var def = slotDefs[i];
            var slotIsForGuildie = assignments.Count(a => a.PlayerId.HasValue) < playerIds.Count;
            if (!slotIsForGuildie)
            {
                // Pug slot — only DPS slots can be filled by pugs
                if (def.Kind == "Dps") assignments.Add((def, null, null));
                else assignments.Add((def, null, null));
                continue;
            }

            // Try Can first
            var (chosenPlayerId, chosenRole) = PickCandidate(unassigned, def, capability, RoleCapabilityStatus.Can, rng);
            if (chosenPlayerId == null)
            {
                (chosenPlayerId, chosenRole) = PickCandidate(unassigned, def, capability, RoleCapabilityStatus.Maybe, rng);
                if (chosenPlayerId.HasValue) maybeFallbacks++;
            }

            if (chosenPlayerId.HasValue)
            {
                assignments.Add((def, chosenPlayerId, chosenRole));
                unassigned.Remove(chosenPlayerId.Value);
            }
            else
            {
                // Empty (will appear as unfilled slot — UI surfaces this)
                assignments.Add((def, null, null));
            }
        }

        // Fallback pass: any guildie we couldn't seat (no Can/Maybe data for any slot they
        // could've taken) gets placed into an empty DPS slot as a pug-equivalent — their name
        // is recorded but no specific role variant. This handles the common case of a member
        // who hasn't filled out their roles at all.
        if (unassigned.Count > 0)
        {
            var leftoverShuffled = unassigned.OrderBy(_ => rng.Next()).ToList();
            foreach (var pid in leftoverShuffled)
            {
                var emptyDpsIdx = -1;
                for (int j = 0; j < assignments.Count; j++)
                {
                    if (assignments[j].Def.Kind == "Dps" && assignments[j].PlayerId == null)
                    {
                        emptyDpsIdx = j;
                        break;
                    }
                }
                if (emptyDpsIdx < 0) break;
                var existing = assignments[emptyDpsIdx];
                assignments[emptyDpsIdx] = (existing.Def, pid, null);  // null Role = generic DPS
                unassigned.Remove(pid);
            }
        }

        // Build sub-groups
        var subGroups = new[] { new SubGroupBuilder(1), new SubGroupBuilder(2) };
        foreach (var (def, playerId, role) in assignments)
        {
            var sub = subGroups[def.SubGroup - 1];
            sub.Slots.Add(new SlotAssignmentDto(
                def.Kind,
                role,
                playerId,
                playerId.HasValue ? nameById.GetValueOrDefault(playerId.Value) : null));
        }

        var result = subGroups.Select(s => new SubGroupDto(s.Index, s.Slots)).ToList();
        return (result, unassigned.ToList(), maybeFallbacks);
    }

    private (Guid? PlayerId, GenericRole? Role) PickCandidate(
        HashSet<Guid> pool,
        SlotDef def,
        CapabilityIndex capability,
        RoleCapabilityStatus minStatus,
        Random rng)
    {
        var candidates = new List<(Guid PlayerId, GenericRole Role)>();
        foreach (var pid in pool)
        {
            foreach (var role in def.AcceptedRoles)
            {
                if (capability.GenericStatus(pid, role) == minStatus)
                {
                    candidates.Add((pid, role));
                    break; // one role per player per slot
                }
            }
        }

        if (candidates.Count == 0) return (null, null);
        var pick = candidates[rng.Next(candidates.Count)];
        return (pick.PlayerId, pick.Role);
    }

    // --- Mechanic assignment ---

    private (List<MechanicSlotDto> Assigned, List<BlockedCandidateDto> Blocked) AssignMechanic(
        MechanicRoleEntity mechanic,
        List<SlotAssignmentDto> assignedSquad,
        CapabilityIndex capability,
        HashSet<Guid> alreadyOnThisBoss,
        Dictionary<Guid, string> nameById,
        Random rng)
    {
        var constraint = (MechanicConstraint)mechanic.SlotConstraint;

        // Eligible: assigned guildie, not yet used on this boss, base role compatible, capability is Can or Maybe
        var eligible = new List<(SlotAssignmentDto Slot, RoleCapabilityStatus Status)>();
        foreach (var s in assignedSquad)
        {
            if (!s.PlayerId.HasValue || !s.Role.HasValue) continue;
            if (alreadyOnThisBoss.Contains(s.PlayerId.Value)) continue;
            var slot = s.Role.Value.GetSlot();
            if (!constraint.IsSlotAllowed(slot)) continue;
            var status = capability.MechanicStatus(s.PlayerId.Value, mechanic.Id);
            if (status == RoleCapabilityStatus.Can || status == RoleCapabilityStatus.Maybe)
            {
                eligible.Add((s, status.Value));
            }
        }

        // Sort: Can before Maybe; prefer slot matching preferred slot (if any); random tie-break
        var preferred = constraint.PreferredSlot();
        eligible = eligible
            .OrderBy(e => e.Status == RoleCapabilityStatus.Can ? 0 : 1)
            .ThenBy(e => preferred.HasValue && e.Slot.Role!.Value.GetSlot() == preferred.Value ? 0 : 1)
            .ThenBy(_ => rng.Next())
            .ToList();

        var assigned = new List<MechanicSlotDto>();
        var picked = eligible.Take(mechanic.MinCount).ToList();
        foreach (var p in picked)
        {
            assigned.Add(new MechanicSlotDto(
                p.Slot.PlayerId,
                p.Slot.AccountName,
                p.Status));
        }
        // Fill missing with empty slots so UI shows the gap
        while (assigned.Count < mechanic.MinCount)
        {
            assigned.Add(new MechanicSlotDto(null, null, null));
        }

        // Blocked candidates: have Can/Maybe for this mechanic but base role is incompatible
        var blocked = new List<BlockedCandidateDto>();
        if (assigned.Count(a => a.PlayerId.HasValue) < mechanic.MinCount)
        {
            foreach (var s in assignedSquad)
            {
                if (!s.PlayerId.HasValue || !s.Role.HasValue) continue;
                var slot = s.Role.Value.GetSlot();
                if (constraint.IsSlotAllowed(slot)) continue; // not blocked, just unavailable

                var status = capability.MechanicStatus(s.PlayerId.Value, mechanic.Id);
                if (status == RoleCapabilityStatus.Can || status == RoleCapabilityStatus.Maybe)
                {
                    blocked.Add(new BlockedCandidateDto(
                        s.PlayerId.Value,
                        s.AccountName ?? "",
                        s.Role.Value,
                        $"Currently on {s.Role.Value.DisplayName()}; mechanic requires {constraint.DisplayName()}"));
                }
            }
        }

        return (assigned, blocked);
    }

    // --- Helpers ---

    private record SlotDef(int SubGroup, string Kind, GenericRole[] AcceptedRoles)
    {
        public bool AcceptsRole(GenericRole r) => AcceptedRoles.Contains(r);
    }

    private class SubGroupBuilder
    {
        public int Index { get; }
        public List<SlotAssignmentDto> Slots { get; } = new();
        public SubGroupBuilder(int index) { Index = index; }
    }

    private class CapabilityIndex
    {
        private readonly Dictionary<(Guid playerId, int role), RoleCapabilityStatus> _generic;
        private readonly Dictionary<(Guid playerId, Guid mechId), RoleCapabilityStatus> _mechanic;

        public CapabilityIndex(List<PlayerRoleCapabilityEntity> rows)
        {
            _generic = new();
            _mechanic = new();
            foreach (var r in rows)
            {
                if (r.GenericRole.HasValue)
                    _generic[(r.PlayerId, r.GenericRole.Value)] = (RoleCapabilityStatus)r.Status;
                else if (r.MechanicRoleId.HasValue)
                    _mechanic[(r.PlayerId, r.MechanicRoleId.Value)] = (RoleCapabilityStatus)r.Status;
            }
        }

        public RoleCapabilityStatus? GenericStatus(Guid playerId, GenericRole role)
            => _generic.TryGetValue((playerId, (int)role), out var s) ? s : null;

        public RoleCapabilityStatus? MechanicStatus(Guid playerId, Guid mechId)
            => _mechanic.TryGetValue((playerId, mechId), out var s) ? s : null;
    }
}

// --- DTOs ---

public record SquadBuildRequest(
    List<Guid> PlayerIds,
    List<int> BossTriggerIds,
    int PugCount,
    Dictionary<Guid, GenericRole>? Locks = null,
    Guid? ForceCoverableMechanicId = null,
    int? Seed = null);

public record SquadBuildResult(
    SquadAssignmentDto Assignment,
    List<SquadConflictDto> Conflicts,
    List<string> Warnings);

public record SquadAssignmentDto(
    List<SubGroupDto> SubGroups,
    int PugDpsCount,
    List<BossAssignmentDto> PerBoss);

public record SubGroupDto(int Index, List<SlotAssignmentDto> Slots);

public record SlotAssignmentDto(
    string Kind,
    GenericRole? Role,
    Guid? PlayerId,
    string? AccountName);

public record BossAssignmentDto(
    int TriggerId,
    string BossName,
    int Wing,
    int EncounterOrder,
    List<MechanicAssignmentDto> Mechanics);

public record MechanicAssignmentDto(
    Guid MechanicRoleId,
    string Name,
    MechanicConstraint Constraint,
    int MinCount,
    int MaxCount,
    List<MechanicSlotDto> AssignedPlayers);

public record MechanicSlotDto(
    Guid? PlayerId,
    string? AccountName,
    RoleCapabilityStatus? StatusUsed);

public record SquadConflictDto(
    int BossTriggerId,
    string BossName,
    Guid MechanicRoleId,
    string MechanicName,
    int Required,
    int Filled,
    List<BlockedCandidateDto> BlockedCandidates);

public record BlockedCandidateDto(
    Guid PlayerId,
    string AccountName,
    GenericRole CurrentBaseRole,
    string ReasonBlocked);
