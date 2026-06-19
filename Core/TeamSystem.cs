using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyHelper;
using HarmonyLib;
using UnityEngine;
using FishUtility;

namespace MainTeamSystem;
// ============================================================
// Team / Group System
// ============================================================
// Groups PersonBehaviour instances into teams.
// Members of the same team have their "wholes" ignore collisions
// with each other. A person's "whole" includes:
//   1. The person itself (root GameObject + all children/limbs)
//   2. Objects grabbed by any GripBehaviour on the person
//   3. Objects that the person's joints point TO (connectedBody)
//   4. Objects that have joints pointing TO any part of the person
// ============================================================

#region Team

/// <summary>
/// A team of Persons whose extended bodies (wholes) ignore collisions
/// with each other.
/// </summary>
public class Team
{
    public string Name { get; set; }
    public Color TeamColor { get; set; } = Color.white;

    /// <summary>
    /// All PersonBehaviour members of this team.
    /// </summary>
    public HashSet<PersonBehaviour> Members { get; } = new HashSet<PersonBehaviour>();

    /// <summary>
    /// Number of members in this team.
    /// </summary>
    public int Count => Members.Count;

    /// <summary>
    /// Tracks all (colliderA, colliderB) pairs currently set to ignore.
    /// Cleared and rebuilt on each RefreshCollisions to handle changing wholes.
    /// </summary>
    public readonly Dictionary<Collider2D, HashSet<Collider2D>> _ignoredPairs = new();

    public Team(string name)
    {
        Name = name ?? "Unnamed Team";
    }

    // ---- Member management ----

    /// <summary>
    /// Add a person to this team. Automatically sets up collision ignoring
    /// with all existing members.
    /// </summary>
    public void AddMember(PersonBehaviour person)
    {
        if (person == null || Members.Contains(person))
            return;

        // Set up collision ignoring against every existing member
        foreach (var existing in Members)
        {
            if (existing != null && existing != person)
                TeamSystem.SetCollisionBetweenWholes(person, existing, true);
        }

        Members.Add(person);
        TeamSystem.Register(person, this);
    }

    /// <summary>
    /// Remove a person from this team. Restores collisions with remaining members.
    /// </summary>
    public void RemoveMember(PersonBehaviour person)
    {
        if (person == null || !Members.Contains(person))
            return;

        // Restore collisions with remaining members
        foreach (var remaining in Members)
        {
            if (remaining != null && remaining != person)
                TeamSystem.SetCollisionBetweenWholes(person, remaining, false);
        }

        Members.Remove(person);
        TeamSystem.Unregister(person);
    }

    /// <summary>
    /// Returns true if the given person is a member of this team.
    /// </summary>
    public bool Contains(PersonBehaviour person)
    {
        return person != null && Members.Contains(person);
    }

    /// <summary>
    /// Remove all members from this team and clean up collision state.
    /// </summary>
    public void Clear()
    {
        // Copy because RemoveMember modifies the collection
        var snapshot = Members.ToArray();
        foreach (var member in snapshot)
            RemoveMember(member);
    }

    /// <summary>
    /// Refresh collision ignoring for all member pairs in this team.
    ///
    /// First restores all previously-ignored collider pairs (handling objects
    /// that have left a person's whole due to joint breaks / drops),
    /// then re-calculates current wholes and applies fresh ignores.
    /// </summary>
    public void RefreshCollisions()
    {
        // ── Step 1: undo all previously-tracked ignores ──
        foreach (var kvp in _ignoredPairs)
        {
            var ca = kvp.Key;
            if (!ca) continue;
            foreach (var cb in kvp.Value)
            {
                if (cb && ca != cb)
                {
                    try { Physics2D.IgnoreCollision(ca, cb, false); }
                    catch { /* best-effort */ }
                }
            }
        }
        _ignoredPairs.Clear();

        // ── Step 2: re-apply with current wholes, tracking new pairs ──
        var members = Members.Where(m => m != null).ToArray();
        for (int i = 0; i < members.Length; i++)
        {
            for (int j = i + 1; j < members.Length; j++)
            {
                TeamSystem.SetCollisionBetweenWholes(
                    members[i], members[j], true, _ignoredPairs);
            }
        }

        // ── Step 3: sync object→team mapping for bullet filter ──
        TeamSystem.SyncTeamRoots(this);
    }
}

#endregion

#region TeamSystem

/// <summary>
/// Static manager for all teams and person-whole collision logic.
/// </summary>
public static class TeamSystem
{
    // ---- State ----

    /// <summary>
    /// All currently active teams.
    /// </summary>
    public static List<Team> Teams { get; } = new List<Team>();

    /// <summary>
    /// Maps a PersonBehaviour to its current Team.
    /// Uses ConditionalWeakTable so entries are automatically removed when
    /// the PersonBehaviour is garbage collected (destroyed by the game).
    /// </summary>
    private static readonly ConditionalWeakTable<PersonBehaviour, Team> _personTeamMap = new();

    /// <summary>
    /// Maps root GameObjects (guns, held items, joint-connected objects)
    /// to their owning team. Populated during RefreshCollisions.
    /// Key is transform.root.gameObject.
    /// </summary>
    private static readonly Dictionary<GameObject, Team> _rootTeamMap = new();

    // ---- Incoming-joint cache (event-driven, no polling) ----

    /// <summary>
    /// Maps a Rigidbody2D to all Joint2Ds in the scene whose connectedBody points to it.
    /// Maintained incrementally by Harmony patches — never requires FindObjectsOfType
    /// after initialisation.
    /// </summary>
    private static readonly Dictionary<Rigidbody2D, HashSet<Joint2D>> _incomingJointCache =
        new Dictionary<Rigidbody2D, HashSet<Joint2D>>();

    private static bool _cacheInitialized;

    // ---- Team lifecycle ----

    /// <summary>
    /// Create a new team and register it with the system.
    /// </summary>
    public static Team CreateTeam(string name)
    {
        var team = new Team(name);
        Teams.Add(team);
        return team;
    }

    /// <summary>
    /// Remove a team entirely, clearing all members first.
    /// </summary>
    public static void RemoveTeam(Team team)
    {
        if (team == null || !Teams.Contains(team))
            return;
        team.Clear();
        Teams.Remove(team);
    }

    /// <summary>
    /// Get the team a person belongs to, or null.
    /// </summary>
    public static Team GetTeam(PersonBehaviour person)
    {
        if (person == null) return null;
        _personTeamMap.TryGetValue(person, out var team);
        return team;
    }

    /// <summary>
    /// Returns true if two persons are on the same team (and not null).
    /// </summary>
    public static bool AreSameTeam(PersonBehaviour a, PersonBehaviour b)
    {
        if (a == null || b == null) return false;
        if (a == b) return true;
        var teamA = GetTeam(a);
        var teamB = GetTeam(b);
        return teamA != null && teamA == teamB;
    }

    /// <summary>
    /// Get the team that an arbitrary GameObject belongs to.
    /// Checks person ownership (if the object is a Person or part of one),
    /// then the root-to-team map (gripped items, joint-connected objects).
    /// </summary>
    public static Team GetTeamForObject(GameObject obj)
    {
        if (obj == null) return null;

        var root = obj.transform.root.gameObject;

        // 1. Is the object part of a Person?
        var person = root.GetComponent<PersonBehaviour>();
        if (person != null)
            return GetTeam(person);

        // 2. Is the object registered as a team-owned item?
        _rootTeamMap.TryGetValue(root, out var team);
        return team;
    }

    /// <summary>
    /// Directly register any GameObject as belonging to a team.
    /// Also sets up collision ignoring against all existing team members' wholes.
    /// </summary>
    public static void AddObjectToTeam(GameObject obj, Team team)
    {
        if (obj == null || team == null) return;
        var root = obj.GetRoot();
        if (root == null) return;
        _rootTeamMap[root] = team;

        // Also ignore collisions with team members' wholes
        var myColliders = root.GetComponentsInChildren<Collider2D>();
        foreach (var person in team.Members)
        {
            if (person != null)
                Utility.IgnoreCollision(myColliders,
                    GetWholeColliders(person), true);
        }
    }

    /// <summary>
    /// Remove a GameObject from its team.
    /// </summary>
    public static void RemoveObjectFromTeam(GameObject obj)
    {
        if (obj == null) return;
        var root = obj.GetRoot();
        if (root != null)
            _rootTeamMap.Remove(root);
    }

    /// <summary>
    /// Register a root GameObject as belonging to a team (public — no collision setup).
    /// </summary>
    public static void RegisterRootForTeam(GameObject root, Team team)
    {
        if (root == null || team == null) return;
        _rootTeamMap[root] = team;
    }

    /// <summary>
    /// Sync the root-to-team map for gripped items. Only gripped objects
    /// count as "team possessions" — joint-connected objects (e.g. chainsaw
    /// friction joints) must NOT be registered, or they'd "defect" to the
    /// wrong team.
    /// </summary>
    public static void SyncTeamRoots(Team team)
    {
        if (team == null) return;

        // Remove all old entries for this team
        var toRemove = new List<GameObject>();
        foreach (var kvp in _rootTeamMap)
            if (kvp.Value == team)
                toRemove.Add(kvp.Key);
        foreach (var key in toRemove)
            _rootTeamMap.Remove(key);

        // Register only gripped items (not joint-connected objects)
        foreach (var person in team.Members)
        {
            if (person == null) continue;
            foreach (var grip in person.GetComponentsInChildren<GripBehaviour>())
            {
                if (grip.CurrentlyHolding == null) continue;
                var root = grip.CurrentlyHolding.transform.root.gameObject;
                // Don't claim another team's person
                if (root.GetComponent<PersonBehaviour>() != null) continue;
                _rootTeamMap[root] = team;
            }
        }
    }

    // ---- Registration (called by Team) ----

    public static void Register(PersonBehaviour person, Team team)
    {
        if (person == null || team == null) return;
        _personTeamMap.Remove(person);
        _personTeamMap.Add(person, team);
    }

    public static void Unregister(PersonBehaviour person)
    {
        if (person == null) return;
        _personTeamMap.Remove(person);
    }

    // ================================================================
    //  Core: determine a Person's "whole"
    // ================================================================

    /// <summary>
    /// Gets the extended "whole" of a person — every GameObject considered
    /// part of this person for team-collision purposes.
    ///
    /// Includes:
    ///   1. The person's root and all descendants
    ///   2. Objects held by any GripBehaviour on the person
    ///   3. Objects the person's Joint2Ds point TO (connectedBody)
    ///   4. Objects whose Joint2Ds point TO any Rigidbody2D in the whole
    ///
    /// When includeIncomingJoints is false, step 4 is skipped (faster,
    /// use when the caller already refreshed the cache).
    /// </summary>
    public static HashSet<GameObject> GetPersonWhole(
        PersonBehaviour person,
        bool includeIncomingJoints = true)
    {
        var whole = new HashSet<GameObject>();
        if (person == null) return whole;

        var root = person.transform.root.gameObject;
        if (root == null) return whole;

        // 1. Add the person's own tree
        AddTree(root, whole);

        // Track all Rigidbody2Ds in the whole (for joint lookups)
        var wholeRigidbodies = new HashSet<Rigidbody2D>();
        foreach (var go in whole)
        {
            var rb = go.GetComponent<Rigidbody2D>();
            if (rb != null) wholeRigidbodies.Add(rb);
        }

        // 2. Add grabbed objects
        var grips = root.GetComponentsInChildren<GripBehaviour>();
        foreach (var grip in grips)
        {
            if (grip.CurrentlyHolding == null) continue;
            var held = grip.CurrentlyHolding.gameObject;
            if (held != null && !whole.Contains(held))
            {
                AddTree(held, whole);
                var rb = held.GetComponent<Rigidbody2D>();
                if (rb != null) wholeRigidbodies.Add(rb);
            }
        }

        // 3. Add objects the person's own joints point TO
        var ownedJoints = root.GetComponentsInChildren<Joint2D>();
        foreach (var joint in ownedJoints)
        {
            if (joint.connectedBody == null) continue;
            var target = joint.connectedBody.gameObject;
            if (target != null && !whole.Contains(target))
            {
                AddTree(target, whole);
                var rb = target.GetComponent<Rigidbody2D>();
                if (rb != null) wholeRigidbodies.Add(rb);
            }
        }

        // 4. Add objects that have joints pointing INTO the person's whole
        if (includeIncomingJoints)
        {
            foreach (var rb in wholeRigidbodies)
            {
                if (_incomingJointCache.TryGetValue(rb, out var incoming))
                {
                    foreach (var joint in incoming)
                    {
                        if (joint == null) continue;
                        var owner = joint.gameObject;
                        if (owner == null || whole.Contains(owner)) continue;
                        // Walk to the root of the connected object
                        var connectedRoot = owner.transform.root.gameObject;
                        if (connectedRoot != null && !whole.Contains(connectedRoot))
                        {
                            AddTree(connectedRoot, whole);
                        }
                    }
                }
            }
        }

        return whole;
    }

    /// <summary>
    /// Gets all Collider2D components belonging to a person's whole.
    /// </summary>
    public static Collider2D[] GetWholeColliders(
        PersonBehaviour person,
        bool includeIncomingJoints = true)
    {
        var whole = GetPersonWhole(person, includeIncomingJoints);
        var colliders = new List<Collider2D>();
        foreach (var go in whole)
        {
            if (go == null) continue;
            // GetComponents (not InChildren) because each child is already
            // a separate entry in the whole HashSet
            var comps = go.GetComponents<Collider2D>();
            foreach (var c in comps)
            {
                if (c != null)
                    colliders.Add(c);
            }
        }
        return colliders.ToArray();
    }

    // ================================================================
    //  Collision management
    // ================================================================

    /// <summary>
    /// Set or clear collision ignoring between the entire wholes of two persons.
    /// When ignore=true, every collider in A is set to ignore every collider in B.
    /// When ignore=false, that relationship is restored.
    /// Pass a non-null tracker dictionary to record which pairs were ignored
    /// (used by Team.RefreshCollisions to undo stale pairs later).
    /// </summary>
    public static void SetCollisionBetweenWholes(
        PersonBehaviour a,
        PersonBehaviour b,
        bool ignore,
        Dictionary<Collider2D, HashSet<Collider2D>> tracker = null)
    {
        if (a == null || b == null || a == b) return;

        var collidersA = GetWholeColliders(a);
        var collidersB = GetWholeColliders(b);

        foreach (var ca in collidersA)
        {
            if (ca == null) continue;
            foreach (var cb in collidersB)
            {
                if (cb == null || ca == cb) continue;
                try { Physics2D.IgnoreCollision(ca, cb, ignore); }
                catch { /* best-effort */ }

                if (ignore && tracker != null)
                {
                    if (!tracker.TryGetValue(ca, out var set))
                        tracker[ca] = set = new HashSet<Collider2D>();
                    set.Add(cb);
                }
            }
        }
    }

    /// <summary>
    /// Re-apply collision ignoring for every pair in a team.
    /// Call this after grips or joints have changed.
    /// </summary>
    public static void RefreshTeamCollisions(Team team)
    {
        if (team == null) return;
        team.RefreshCollisions();
    }

    /// <summary>
    /// Re-apply collision ignoring for all teams.
    /// </summary>
    public static void RefreshAllCollisions()
    {
        foreach (var team in Teams)
            team.RefreshCollisions();
    }

    // ================================================================
    //  Incoming-joint cache (event-driven)
    // ================================================================

    /// <summary>
    /// One-time initialisation: scan all existing Joint2Ds in the scene.
    /// Called after Harmony patches are installed. Subsequent changes are
    /// tracked incrementally by the patches themselves.
    /// </summary>
    public static void InitJointCache()
    {
        if (_cacheInitialized) return;
        _cacheInitialized = true;

        _incomingJointCache.Clear();
        var allJoints = UnityEngine.Object.FindObjectsOfType<Joint2D>();
        foreach (var joint in allJoints)
        {
            if (joint != null && joint.connectedBody != null)
                RegisterJointInCache(joint);
        }
    }

    /// <summary>
    /// Add a joint to the incoming-joint cache.
    /// </summary>
    private static void RegisterJointInCache(Joint2D joint)
    {
        if (joint == null || joint.connectedBody == null) return;
        if (!_incomingJointCache.TryGetValue(joint.connectedBody, out var set))
        {
            set = new HashSet<Joint2D>();
            _incomingJointCache[joint.connectedBody] = set;
        }
        set.Add(joint);
    }

    /// <summary>
    /// Update the cache when a joint's connectedBody changes.
    /// Removes the joint from any old entry and adds it under the new connectedBody.
    /// </summary>
    private static void UpdateJointInCache(Joint2D joint, Rigidbody2D newConnectedBody)
    {
        if (joint == null) return;
        // Remove from all existing entries (covers the old connectedBody)
        foreach (var kvp in _incomingJointCache)
            kvp.Value.Remove(joint);
        // Add under new connectedBody if non-null
        if (newConnectedBody != null)
        {
            if (!_incomingJointCache.TryGetValue(newConnectedBody, out var set))
            {
                set = new HashSet<Joint2D>();
                _incomingJointCache[newConnectedBody] = set;
            }
            set.Add(joint);
        }
    }

    // ---- public helpers ----

    /// <summary>
    /// Add a GameObject and its entire Transform hierarchy to a HashSet.
    /// </summary>
    private static void AddTree(GameObject root, HashSet<GameObject> set)
    {
        if (root == null || set == null) return;
        if (!set.Add(root)) return; // already present → skip subtree

        var t = root.transform;
        for (int i = 0; i < t.childCount; i++)
        {
            var child = t.GetChild(i);
            if (child != null)
                AddTree(child.gameObject, set);
        }
    }

    // ================================================================
    //  Event-driven refresh (Harmony patches)
    // ================================================================

    private static DynamicHarmonyManager _harmony;
    private static bool _patchesInstalled;
    private static readonly HashSet<Team> _dirtyTeams = new HashSet<Team>();
    private static bool _refreshScheduled;

    /// <summary>
    /// Install Harmony patches that detect grip/joint changes and
    /// trigger automatic team collision refresh.
    ///
    /// Patched methods (6 total):
    ///   - GripBehaviour.Attach             → grip created
    ///   - GripBehaviour.DropObject         → grip dropped
    ///   - Joint2D.set_connectedBody        → joint connection changed (+ cache)
    ///   - Behaviour.set_enabled            → joint enabled/disabled (physics breaks)
    ///   - Collider2D.OverlapCollider[]     → melee overlap (compact array)
    ///   - Collider2D.OverlapCollider(List) → melee overlap (list remove)
    /// </summary>
    public static void InstallPatches()
    {
        if (_patchesInstalled) return;
        _patchesInstalled = true;

        _harmony = new DynamicHarmonyManager("com.helper.group-system");

        // ── Grip: item picked up ──
        _harmony.AddPostfix("group-grip-attach",
            typeof(GripBehaviour), "Attach",
            ctx =>
            {
                var grip = ctx.Instance as GripBehaviour;
                if (!grip) return;
                var person = grip.transform.root.GetComponent<PersonBehaviour>();
                if (person == null) return;

                // Eagerly register the held item's root so bullet filter
                // picks it up immediately (before next RefreshCollisions cycle)
                if (grip.CurrentlyHolding != null)
                {
                    var team = GetTeam(person);
                    if (team != null)
                        RegisterRootForTeam(
                            grip.CurrentlyHolding.transform.root.gameObject, team);
                }

                ScheduleRefreshForPerson(person);
            });

        // ── Grip: item dropped ──
        _harmony.AddPostfix("group-grip-drop",
            typeof(GripBehaviour), "DropObject",
            ctx =>
            {
                var grip = ctx.Instance as GripBehaviour;
                if (!grip) return;
                var person = grip.transform.root.GetComponent<PersonBehaviour>();
                if (person != null)
                    ScheduleRefreshForPerson(person);
                // Root-map cleanup is handled by SyncTeamRoots during refresh
            });

        // ── Joint: connectedBody changed (creation / reassign / explicit null) ──
        _harmony.AddPostfix("group-joint-connect",
            typeof(Joint2D), "set_connectedBody",
            ctx =>
            {
                var joint = ctx.Instance as Joint2D;
                if (!joint) return;

                var newBody = ctx.Args.Length > 0 ? ctx.Args[0] as Rigidbody2D : null;

                // ── Maintain incoming-joint cache ──
                UpdateJointInCache(joint, newBody);

                // ── Schedule team refresh ──
                // Refresh the person that owns this joint
                var root = joint.transform.root;
                if (root != null)
                {
                    var person = root.GetComponent<PersonBehaviour>();
                    if (person != null)
                        ScheduleRefreshForPerson(person);
                }

                // Also refresh the person being connected to (if connecting, not nullifying)
                if (newBody != null)
                {
                    var connectedRoot = newBody.transform.root;
                    if (connectedRoot != null)
                    {
                        var connectedPerson = connectedRoot.GetComponent<PersonBehaviour>();
                        if (connectedPerson != null)
                            ScheduleRefreshForPerson(connectedPerson);
                    }
                }
            });

        // ── Joint: enabled/disabled (physics-force break sets enabled=false) ──
        _harmony.AddPostfix("group-joint-enabled",
            typeof(Behaviour), "set_enabled",
            ctx =>
            {
                // Fast path: only act on Joint2D components
                if (!(ctx.Instance is Joint2D joint)) return;

                // Refresh the person that owns this joint
                var person = joint.transform.root.GetComponent<PersonBehaviour>();
                if (person != null)
                    ScheduleRefreshForPerson(person);

                // Also refresh the connected body's person
                if (joint.connectedBody != null)
                {
                    var connectedPerson = joint.connectedBody.transform.root
                        .GetComponent<PersonBehaviour>();
                    if (connectedPerson != null)
                        ScheduleRefreshForPerson(connectedPerson);
                }
            });

        // ── Collider2D.OverlapCollider → filter same-team hits ──
        // Covers chainsaws, drills, and any melee weapon using overlap queries.
        // Array overload: compact the buffer and update the return count.
        _harmony.AddPostfix("group-overlapcollider-array",
            typeof(Collider2D), "OverlapCollider",
            ctx =>
            {
                var collider = ctx.Instance as Collider2D;
                if (!collider) return;
                var team = GetTeamForObject(collider.gameObject);
                if (team == null) return;

                var results = ctx.Args.Length > 1 ? ctx.Args[1] as Collider2D[] : null;
                if (results == null) return;

                var originalCount = (int)(ctx.Result ?? 0);
                int writeIdx = 0;
                for (int readIdx = 0; readIdx < originalCount; readIdx++)
                {
                    var hit = results[readIdx];
                    if (hit != null && GetTeamForObject(hit.gameObject) == team)
                        continue;
                    results[writeIdx++] = hit;
                }
                for (int i = writeIdx; i < originalCount; i++)
                    results[i] = null;
                ctx.Result = writeIdx;
            },
            new Type[] { typeof(ContactFilter2D), typeof(Collider2D[]) });

        // List overload: remove same-team entries from the list.
        _harmony.AddPostfix("group-overlapcollider-list",
            typeof(Collider2D), "OverlapCollider",
            ctx =>
            {
                var collider = ctx.Instance as Collider2D;
                if (!collider) return;
                var team = GetTeamForObject(collider.gameObject);
                if (team == null) return;

                var list = ctx.Args.Length > 1 ? ctx.Args[1] as List<Collider2D> : null;
                if (list == null) return;

                list.RemoveAll(hit => hit != null && GetTeamForObject(hit.gameObject) == team);
                ctx.Result = list.Count;
            },
            new Type[] { typeof(ContactFilter2D), typeof(List<Collider2D>) });

        // ── One-time initial population ──
        InitJointCache();

        // ── Bolt spawn registration: capture weapon team ──
        // AcceleratorGun → Object.Instantiate
        _harmony.AddPrefix("group-bolt-spawn-acc",
            typeof(AcceleratorGunBehaviour), "SpawnProjectile",
            ctx =>
            {
                var gun = ctx.Instance as AcceleratorGunBehaviour;
                if (gun) _pendingBoltTeam = GetTeamForObject(gun.gameObject);
            });
        _harmony.AddPostfix("group-bolt-spawn-acc-cleanup",
            typeof(AcceleratorGunBehaviour), "SpawnProjectile",
            ctx => _pendingBoltTeam = null);

        // BlasterBehaviour → Object.Instantiate in Shoot()
        _harmony.AddPrefix("group-bolt-spawn-blaster",
            typeof(BlasterBehaviour), "Shoot",
            ctx =>
            {
                var blaster = ctx.Instance as BlasterBehaviour;
                if (blaster) _pendingBoltTeam = GetTeamForObject(blaster.gameObject);
            });
        _harmony.AddPostfix("group-bolt-spawn-blaster-cleanup",
            typeof(BlasterBehaviour), "Shoot",
            ctx => _pendingBoltTeam = null);

        // RocketLauncherBehaviour → Object.Instantiate in Use()
        _harmony.AddPrefix("group-bolt-spawn-rocket",
            typeof(RocketLauncherBehaviour), "Use",
            ctx =>
            {
                var rl = ctx.Instance as RocketLauncherBehaviour;
                if (rl) _pendingBoltTeam = GetTeamForObject(rl.gameObject);
            });
        _harmony.AddPostfix("group-bolt-spawn-rocket-cleanup",
            typeof(RocketLauncherBehaviour), "Use",
            ctx => _pendingBoltTeam = null);

        // ArchelixCaster → PoolGenerator.RequestPrefab
        _harmony.AddPostfix("group-bolt-pool-register",
            typeof(PoolGenerator), "RequestPrefab",
            ctx =>
            {
                if (_pendingBoltTeam == null) return;
                var go = ctx.Result as GameObject;
                if (!go) return;
                var bolt = go.GetComponent<BaseBoltBehaviour>();
                if (bolt != null)
                    BaseBoltBehaviour_DoHitCheck_Patch.RegisterBoltTeam(bolt, _pendingBoltTeam);
            });

        // ── Bullet & bolt team filters (raw Harmony for Transpiler support) ──
        var bh = new Harmony("com.helper.group-system.bullets");
        bh.CreateClassProcessor(typeof(BallisticsEmitter_BallisticIteration_Patch)).Patch();
        bh.CreateClassProcessor(typeof(BaseBoltBehaviour_DoHitCheck_Patch)).Patch();
        bh.CreateClassProcessor(typeof(BoltInstantiateRegistration)).Patch();
        bh.CreateClassProcessor(typeof(ArchelixCaster_BurstShot_Patch)).Patch();
        bh.CreateClassProcessor(typeof(ProjectileLauncher_LaunchRoutine_Patch)).Patch();
        bh.CreateClassProcessor(typeof(BlasterboltBehaviour_Update_Patch)).Patch();
        bh.CreateClassProcessor(typeof(IonBoltBehaviour_DoHitCheck_Patch)).Patch();
        bh.CreateClassProcessor(typeof(StunnerBehaviour_Update_Patch)).Patch();
        bh.CreateClassProcessor(typeof(LaunchedRocketBehaviour_Update_Patch)).Patch();
        bh.CreateClassProcessor(typeof(LightningGunBehaviour_PerformLightning_Patch)).Patch();
        bh.CreateClassProcessor(typeof(TemperatureRayGunBehaviour_FixedUpdate_Patch)).Patch();
        bh.CreateClassProcessor(typeof(GenericScifiWeapon40_Fire_Patch)).Patch();
        bh.CreateClassProcessor(typeof(Beamformer_DoLaserDamage_Patch)).Patch();
        bh.CreateClassProcessor(typeof(Flamethrower_AffectCollider_Patch)).Patch();
    }

    [ThreadStatic]
    private static Team _pendingBoltTeam;

    /// <summary>
    /// Intercepts Object.Instantiate to tag newly-created BaseBoltBehaviour
    /// instances with the pending team. Zero overhead when _pendingBoltTeam is null.
    /// </summary>
    [HarmonyPatch(typeof(UnityEngine.Object), "Instantiate",
        typeof(UnityEngine.Object), typeof(Vector3), typeof(Quaternion))]
    public static class BoltInstantiateRegistration
    {
        [HarmonyPostfix]
        public static void Postfix(UnityEngine.Object __result)
        {
            if (_pendingBoltTeam == null) return;
            var go = __result as GameObject;
            if (!go) return;

            var bolt = go.GetComponent<BaseBoltBehaviour>();
            if (bolt != null)
            {
                BaseBoltBehaviour_DoHitCheck_Patch.RegisterBoltTeam(bolt, _pendingBoltTeam);
                return;
            }

            // Blaster / Ion / Stunner / Rocket — register by component
            if (go.GetComponent<BlasterboltBehaviour>() != null ||
                go.GetComponent<IonBoltBehaviour>() != null ||
                go.GetComponent<StunnerBehaviour>() != null ||
                go.GetComponent<LaunchedRocketBehaviour>() != null)
            {
                TeamSystem.RegisterRootForTeam(go.transform.root.gameObject, _pendingBoltTeam);
                return;
            }

            // Launched projectile (PhysicalBehaviour, not a Person)
            var phys = go.GetComponent<PhysicalBehaviour>();
            if (phys != null && go.GetComponent<PersonBehaviour>() == null)
            {
                try
                {
                    var root = go.transform?.root?.gameObject;
                    if (root == null) return;

                    var team = _pendingBoltTeam;
                    if (team == null) return;

                    var myColliders = root.GetComponentsInChildren<Collider2D>();
                    foreach (var kvp in _rootTeamMap)
                    {
                        if (kvp.Value == team && kvp.Key != root)
                            Utility.IgnoreCollision(myColliders,
                                kvp.Key.GetComponentsInChildren<Collider2D>(), true);
                    }

                    foreach (var person in team.Members)
                    {
                        if (person != null)
                            Utility.IgnoreCollision(myColliders,
                                TeamSystem.GetWholeColliders(person), true);
                    }

                    RegisterRootForTeam(root, team);
                }
                catch { /* best-effort — prefab may be mid-destruction */ }
            }
        }
    }

    /// <summary>
    /// Sets _pendingBoltTeam for ArchelixCasterBehaviour bolts.
    /// BurstShot is a coroutine (IEnumerator); the prefix fires each MoveNext
    /// and extracts the caster from the state machine's &lt;&gt;4__this field.
    /// </summary>
    [HarmonyPatch(typeof(ArchelixCasterBehaviour), "BurstShot", MethodType.Enumerator)]
    public static class ArchelixCaster_BurstShot_Patch
    {
        private static Func<object, ArchelixCasterBehaviour> _casterAccessor;

        [HarmonyPrefix]
        public static void Prefix(object __instance)
        {
            if (_casterAccessor == null)
            {
                var t = __instance.GetType();
                var field = t.GetField("<>4__this",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                var param = Expression.Parameter(typeof(object), "instance");
                var body = Expression.Convert(
                    Expression.Field(Expression.Convert(param, t), field),
                    typeof(ArchelixCasterBehaviour));
                _casterAccessor = Expression.Lambda<Func<object, ArchelixCasterBehaviour>>(body, param).Compile();
            }

            var caster = _casterAccessor(__instance);
            if (caster != null)
                _pendingBoltTeam = TeamSystem.GetTeamForObject(caster.gameObject);
        }
    }

    /// <summary>
    /// Sets _pendingBoltTeam for ProjectileLauncherBehaviour projectiles.
    /// LaunchRoutine is an IEnumerator that calls Object.Instantiate.
    /// </summary>
    [HarmonyPatch(typeof(ProjectileLauncherBehaviour), "LaunchRoutine", MethodType.Enumerator)]
    public static class ProjectileLauncher_LaunchRoutine_Patch
    {
        private static Func<object, ProjectileLauncherBehaviour> _launcherAccessor;

        [HarmonyPrefix]
        public static void Prefix(object __instance)
        {
            if (_launcherAccessor == null)
            {
                var t = __instance.GetType();
                var field = t.GetField("<>4__this",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                var param = Expression.Parameter(typeof(object), "instance");
                var body = Expression.Convert(
                    Expression.Field(Expression.Convert(param, t), field),
                    typeof(ProjectileLauncherBehaviour));
                _launcherAccessor = Expression.Lambda<Func<object, ProjectileLauncherBehaviour>>(body, param).Compile();
            }

            var launcher = _launcherAccessor(__instance);
            if (launcher != null)
                _pendingBoltTeam = TeamSystem.GetTeamForObject(launcher.gameObject);
        }
    }

    /// <summary>
    /// Mark a person's team as needing a collision refresh.
    /// The actual refresh is debounced by 2 frames to batch rapid changes
    /// (e.g. many joints during spawn) and let Unity state settle.
    /// </summary>
    private static void ScheduleRefreshForPerson(PersonBehaviour person)
    {
        if (person == null) return;

        var team = GetTeam(person);
        if (team != null)
            _dirtyTeams.Add(team);

        if (!_refreshScheduled)
        {
            _refreshScheduled = true;
            ProcessDirtyTeams();
        }
    }

    /// <summary>
    /// Immediately process all dirty teams. Called after the debounce window.
    /// </summary>
    private static void ProcessDirtyTeams()
    {
        _refreshScheduled = false;

        if (_dirtyTeams.Count == 0) return;

        foreach (var team in _dirtyTeams)
        {
            if (team != null && team.Count > 0)
                team.RefreshCollisions();
        }
        _dirtyTeams.Clear();
    }

    /// <summary>
    /// Force an immediate refresh for a specific person's team (bypasses debounce).
    /// </summary>
    public static void RefreshPersonNow(PersonBehaviour person)
    {
        if (person == null) return;
        var team = GetTeam(person);
        if (team != null)
            team.RefreshCollisions();
    }
}

#endregion

#region TeamSystemDriver

/// <summary>
/// MonoBehaviour driver for the team system.
///
/// Refresh is entirely event-driven via Harmony patches
/// (see TeamSystem.InstallPatches). This driver exists only to
/// provide a singleton anchor for patch lifetime.
///
/// Auto-created by TeamSystemDriver.EnsureExists() — call once from mod init.
/// </summary>
public class TeamSystemDriver : MonoBehaviour
{
    private static TeamSystemDriver _instance;

    /// <summary>
    /// Ensure the driver exists and Harmony patches are installed.
    /// Call once from mod init (e.g. in Mod.OnLoad).
    /// </summary>
    public static TeamSystemDriver EnsureExists()
    {
        TeamSystem.InstallPatches();

        if (_instance != null) return _instance;

        var go = new GameObject("TeamSystemDriver");
        DontDestroyOnLoad(go);
        _instance = go.AddComponent<TeamSystemDriver>();
        return _instance;
    }

    /// <summary>
    /// The singleton instance, or null if not yet created.
    /// </summary>
    public static TeamSystemDriver Instance => _instance;

    private void Awake()
    {
        if (_instance != null && _instance != this)
        {
            Destroy(gameObject);
            return;
        }
        _instance = this;
        DontDestroyOnLoad(gameObject);
    }

    private void OnDestroy()
    {
        if (_instance == this)
            _instance = null;
    }
}

#endregion

#region TeamSystemExtensions

/// <summary>
/// Convenience extension methods.
/// </summary>
public static class TeamSystemExtensions
{
    /// <summary>
    /// Get the team this person belongs to (null if none).
    /// </summary>
    public static Team GetTeam(this PersonBehaviour person) =>
        TeamSystem.GetTeam(person);

    /// <summary>
    /// Check if this person is on the same team as another.
    /// </summary>
    public static bool IsSameTeamAs(this PersonBehaviour self, PersonBehaviour other) =>
        TeamSystem.AreSameTeam(self, other);

    /// <summary>
    /// Get the entire "whole" of this person as GameObjects.
    /// </summary>
    public static HashSet<GameObject> GetWhole(this PersonBehaviour person) =>
        TeamSystem.GetPersonWhole(person);

    /// <summary>
    /// Get all colliders in this person's whole.
    /// </summary>
    public static Collider2D[] GetWholeColliders(this PersonBehaviour person) =>
        TeamSystem.GetWholeColliders(person);
}

#endregion

#region BulletTeamFilter

/// <summary>
/// Harmony Transpiler that intercepts BallisticsEmitter.BallisticIteration
/// to prevent same-team bullet hits. After OverlapPoint / Raycast,
/// filters out results where the hit target belongs to the shooter's team.
/// </summary>
[HarmonyPatch(typeof(BallisticsEmitter), "BallisticIteration", MethodType.Enumerator)]
public static class BallisticsEmitter_BallisticIteration_Patch
{
    // ── Compiled field accessor (expression-tree delegate, zero reflection overhead) ──
    //     Equivalent to Access.CreateFieldGetter but works with runtime-only
    //     compiler-generated state machine types.
    private static Func<object, BallisticsEmitter> _emitterAccessor;

    // ── Current shooter's team (set by MoveNext prefix, read by filters) ──
    [ThreadStatic]
    private static Team _shooterTeam;

    /// <summary>
    /// Extracts BallisticsEmitter from the state machine's &lt;&gt;4__this field
    /// via a compiled expression-tree delegate (no FieldInfo.GetValue overhead),
    /// then resolves the shooter's team via the forward object→team mapping.
    /// </summary>
    [HarmonyPrefix]
    public static void MoveNext_Prefix(object __instance)
    {
        if (_emitterAccessor == null)
        {
            var t = __instance.GetType();
            var field = t.GetField("<>4__this",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            var param = Expression.Parameter(typeof(object), "instance");
            var body = Expression.Convert(
                Expression.Field(Expression.Convert(param, t), field),
                typeof(BallisticsEmitter));
            _emitterAccessor = Expression.Lambda<Func<object, BallisticsEmitter>>(body, param).Compile();
        }

        var emitter = _emitterAccessor(__instance);
        _shooterTeam = emitter?.ConnectedBehaviour != null
            ? TeamSystem.GetTeamForObject(emitter.ConnectedBehaviour.gameObject)
            : null;
    }

    [HarmonyTranspiler]
    public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        var codes = new List<CodeInstruction>(instructions);

        var overlapPointMethod = AccessTools.Method(
            typeof(Physics2D), nameof(Physics2D.OverlapPoint),
            new[] { typeof(Vector2), typeof(int), typeof(float), typeof(float) });

        var raycastMethod = AccessTools.Method(
            typeof(Physics2D), nameof(Physics2D.Raycast),
            new[] { typeof(Vector2), typeof(Vector2), typeof(float), typeof(int), typeof(float), typeof(float) });

        var filterColliderMethod = AccessTools.Method(
            typeof(BallisticsEmitter_BallisticIteration_Patch), nameof(FilterCollider));

        var filterRaycastMethod = AccessTools.Method(
            typeof(BallisticsEmitter_BallisticIteration_Patch), nameof(FilterRaycast));

        for (int i = 0; i < codes.Count; i++)
        {
            if (codes[i].Calls(overlapPointMethod))
            {
                codes.Insert(i + 1, new CodeInstruction(OpCodes.Call, filterColliderMethod));
                i++;
            }
            else if (codes[i].Calls(raycastMethod))
            {
                codes.Insert(i + 1, new CodeInstruction(OpCodes.Call, filterRaycastMethod));
                i++;
            }
        }
        return codes;
    }

    public static Collider2D FilterCollider(Collider2D hit)
    {
        if (hit != null && ShouldIgnoreHit(hit.gameObject))
            return null;
        return hit;
    }

    public static RaycastHit2D FilterRaycast(RaycastHit2D hit)
    {
        if (hit.collider != null && ShouldIgnoreHit(hit.collider.gameObject))
            return default;
        return hit;
    }

    private static bool ShouldIgnoreHit(GameObject hitObj)
    {
        if (_shooterTeam == null || hitObj == null)
            return false;

        var hitTeam = TeamSystem.GetTeamForObject(hitObj);
        return hitTeam != null && hitTeam == _shooterTeam;
    }
}

#endregion

#region BoltTeamFilter

[HarmonyPatch(typeof(BaseBoltBehaviour), "DoHitCheck")]
public static class BaseBoltBehaviour_DoHitCheck_Patch
{
    public static readonly ConditionalWeakTable<BaseBoltBehaviour, Team> BoltTeamMap = new();

    public static void RegisterBoltTeam(BaseBoltBehaviour bolt, Team team)
    {
        if (bolt != null && team != null)
            BoltTeamMap.Add(bolt, team);
    }

    [HarmonyTranspiler]
    public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        var codes = new List<CodeInstruction>(instructions);

        var raycastMethod = AccessTools.Method(typeof(Physics2D), nameof(Physics2D.Raycast),
            new[] { typeof(Vector2), typeof(Vector2), typeof(float), typeof(int) });

        var filterMethod = AccessTools.Method(
            typeof(BaseBoltBehaviour_DoHitCheck_Patch), nameof(FilterRaycastResult));

        for (int i = 0; i < codes.Count; i++)
        {
            if (codes[i].Calls(raycastMethod))
            {
                // Stack after Raycast: [RaycastHit2D]
                // Need: [RaycastHit2D, instance] for filter(hit, instance)
                codes.Insert(i + 1, new CodeInstruction(OpCodes.Ldarg_0));
                codes.Insert(i + 2, new CodeInstruction(OpCodes.Call, filterMethod));
                break;
            }
        }

        return codes;
    }

    // Parameters: (hit on stack first/deeper, instance on top)
    public static RaycastHit2D FilterRaycastResult(RaycastHit2D hit, BaseBoltBehaviour instance)
    {
        if (hit.collider == null) return hit;

        if (!BoltTeamMap.TryGetValue(instance, out var boltTeam))
            boltTeam = TeamSystem.GetTeamForObject(instance.gameObject);
        if (boltTeam == null) return hit;

        var hitTeam = TeamSystem.GetTeamForObject(hit.collider.gameObject);
        return hitTeam == boltTeam ? default : hit;
    }
}

/// <summary>
/// Harmony Transpiler for BlasterboltBehaviour.Update.
/// BlasterboltBehaviour uses Utils.MaterialAwareRaycast instead of BaseBoltBehaviour.DoHitCheck.
/// </summary>
[HarmonyPatch(typeof(BlasterboltBehaviour), "Update")]
public static class BlasterboltBehaviour_Update_Patch
{
    [HarmonyTranspiler]
    public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        var codes = new List<CodeInstruction>(instructions);

        var filterMethod = AccessTools.Method(
            typeof(BlasterboltBehaviour_Update_Patch), nameof(FilterLaserHit));

        for (int i = 0; i < codes.Count; i++)
        {
            // Match by name — avoids AccessTools signature issues
            if (codes[i].opcode == OpCodes.Call &&
                codes[i].operand is MethodInfo mi &&
                mi.Name == "MaterialAwareRaycast")
            {
                codes.Insert(i + 1, new CodeInstruction(OpCodes.Ldarg_0));
                codes.Insert(i + 2, new CodeInstruction(OpCodes.Call, filterMethod));
                break;
            }
        }

        return codes;
    }

    public static Utils.LaserHit FilterLaserHit(Utils.LaserHit laserHit, BlasterboltBehaviour instance)
    {
        if (!laserHit.hit.HasValue || laserHit.physicalBehaviour == null)
            return laserHit;

        var boltTeam = TeamSystem.GetTeamForObject(instance.gameObject);
        if (boltTeam == null) return laserHit;

        var hitTeam = TeamSystem.GetTeamForObject(
            laserHit.physicalBehaviour.gameObject);
        if (hitTeam == boltTeam)
            return new Utils.LaserHit { hit = null, physicalBehaviour = null };

        return laserHit;
    }

    /// <summary>
    /// Fallback: skip Impact() for same-team hits in case the Transpiler misses.
    /// </summary>
    [HarmonyPrefix]
    [HarmonyPatch("Impact")]
    public static bool Impact_Prefix(BlasterboltBehaviour __instance, RaycastHit2D hit)
    {
        var boltTeam = TeamSystem.GetTeamForObject(__instance.gameObject);
        if (boltTeam == null) return true;

        var hitTeam = TeamSystem.GetTeamForObject(hit.collider?.gameObject);
        return hitTeam != boltTeam;
    }
}

/// <summary>
/// Shared filter for projectile raycast hits. Returns default if same team.
/// </summary>
public static class ProjectileRaycastFilter
{
    public static RaycastHit2D FilterRaycastHit(RaycastHit2D hit, MonoBehaviour projectile)
    {
        if (hit.collider == null) return hit;
        var team = TeamSystem.GetTeamForObject(projectile.gameObject);
        if (team == null) return hit;
        var hitTeam = TeamSystem.GetTeamForObject(hit.collider.gameObject);
        return hitTeam == team ? default : hit;
    }
}

[HarmonyPatch(typeof(IonBoltBehaviour), "DoHitCheck")]
public static class IonBoltBehaviour_DoHitCheck_Patch
{
    [HarmonyTranspiler]
    public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> ins)
    {
        var codes = new List<CodeInstruction>(ins);
        var m = AccessTools.Method(typeof(Physics2D), "Raycast",
            new[] { typeof(Vector2), typeof(Vector2), typeof(float), typeof(int) });
        var f = AccessTools.Method(typeof(ProjectileRaycastFilter), "FilterRaycastHit");
        if (m == null) return codes;
        for (int i = 0; i < codes.Count; i++)
            if (codes[i].Calls(m)) { codes.Insert(i + 1, new CodeInstruction(OpCodes.Ldarg_0)); codes.Insert(i + 2, new CodeInstruction(OpCodes.Call, f)); break; }
        return codes;
    }
}

[HarmonyPatch(typeof(StunnerBehaviour), "Update")]
public static class StunnerBehaviour_Update_Patch
{
    [HarmonyTranspiler]
    public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> ins)
    {
        var codes = new List<CodeInstruction>(ins);
        var m = AccessTools.Method(typeof(Physics2D), "Raycast",
            new[] { typeof(Vector2), typeof(Vector2), typeof(float), typeof(int) });
        var f = AccessTools.Method(typeof(ProjectileRaycastFilter), "FilterRaycastHit");
        if (m == null) return codes;
        for (int i = 0; i < codes.Count; i++)
            if (codes[i].Calls(m)) { codes.Insert(i + 1, new CodeInstruction(OpCodes.Ldarg_0)); codes.Insert(i + 2, new CodeInstruction(OpCodes.Call, f)); break; }
        return codes;
    }
}

[HarmonyPatch(typeof(LaunchedRocketBehaviour), "Update")]
public static class LaunchedRocketBehaviour_Update_Patch
{
    [HarmonyTranspiler]
    public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> ins)
    {
        var codes = new List<CodeInstruction>(ins);
        var m = AccessTools.Method(typeof(Physics2D), "Raycast",
            new[] { typeof(Vector2), typeof(Vector2), typeof(float), typeof(int) });
        var f = AccessTools.Method(typeof(ProjectileRaycastFilter), "FilterRaycastHit");
        if (m == null) return codes;
        for (int i = 0; i < codes.Count; i++)
            if (codes[i].Calls(m)) { codes.Insert(i + 1, new CodeInstruction(OpCodes.Ldarg_0)); codes.Insert(i + 2, new CodeInstruction(OpCodes.Call, f)); break; }
        return codes;
    }
}

/// <summary>
/// Transpiler for LightningGunBehaviour.PerformLightning (4-param Raycast).
/// </summary>
[HarmonyPatch(typeof(LightningGunBehaviour), "PerformLightning")]
public static class LightningGunBehaviour_PerformLightning_Patch
{
    [HarmonyTranspiler]
    public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> ins)
    {
        var codes = new List<CodeInstruction>(ins);
        var m = AccessTools.Method(typeof(Physics2D), "Raycast",
            new[] { typeof(Vector2), typeof(Vector2), typeof(float), typeof(int) });
        var f = AccessTools.Method(typeof(ProjectileRaycastFilter), "FilterRaycastHit");
        if (m == null) return codes;
        for (int i = 0; i < codes.Count; i++)
            if (codes[i].Calls(m)) { codes.Insert(i + 1, new CodeInstruction(OpCodes.Ldarg_0)); codes.Insert(i + 2, new CodeInstruction(OpCodes.Call, f)); break; }
        return codes;
    }
}

/// <summary>
/// Transpiler for TemperatureRayGunBehaviour.FixedUpdate (4-param Raycast).
/// </summary>
[HarmonyPatch(typeof(TemperatureRayGunBehaviour), "FixedUpdate")]
public static class TemperatureRayGunBehaviour_FixedUpdate_Patch
{
    [HarmonyTranspiler]
    public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> ins)
    {
        var codes = new List<CodeInstruction>(ins);
        var m = AccessTools.Method(typeof(Physics2D), "Raycast",
            new[] { typeof(Vector2), typeof(Vector2), typeof(float), typeof(int) });
        var f = AccessTools.Method(typeof(ProjectileRaycastFilter), "FilterRaycastHit");
        if (m == null) return codes;
        for (int i = 0; i < codes.Count; i++)
            if (codes[i].Calls(m)) { codes.Insert(i + 1, new CodeInstruction(OpCodes.Ldarg_0)); codes.Insert(i + 2, new CodeInstruction(OpCodes.Call, f)); break; }
        return codes;
    }
}

/// <summary>
/// Transpiler for GenericScifiWeapon40Behaviour.Fire (4-param Raycast).
/// </summary>
[HarmonyPatch(typeof(GenericScifiWeapon40Behaviour), "Fire")]
public static class GenericScifiWeapon40_Fire_Patch
{
    [HarmonyTranspiler]
    public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> ins)
    {
        var codes = new List<CodeInstruction>(ins);
        var m = AccessTools.Method(typeof(Physics2D), "Raycast",
            new[] { typeof(Vector2), typeof(Vector2), typeof(float), typeof(int) });
        var f = AccessTools.Method(typeof(ProjectileRaycastFilter), "FilterRaycastHit");
        if (m == null) return codes;
        for (int i = 0; i < codes.Count; i++)
            if (codes[i].Calls(m)) { codes.Insert(i + 1, new CodeInstruction(OpCodes.Ldarg_0)); codes.Insert(i + 2, new CodeInstruction(OpCodes.Call, f)); break; }
        return codes;
    }
}

/// <summary>
/// Prefix for BeamformerBehaviour.DoLaserDamage.
/// Skips same-team targets inside the CircleCastNonAlloc loop.
/// </summary>
[HarmonyPatch(typeof(BeamformerBehaviour), "DoLaserDamage")]
public static class Beamformer_DoLaserDamage_Patch
{
    [HarmonyPrefix]
    public static bool Prefix(Vector2 position, Vector2 forward, BeamformerBehaviour __instance)
    {
        var team = TeamSystem.GetTeamForObject(__instance.gameObject);
        if (team == null) return true;

        var layers = (int)__instance.LayersToHit;
        var buffer = new RaycastHit2D[1024];
        int count = Physics2D.CircleCastNonAlloc(
            position + forward * (0.02f + __instance.BeamWidth),
            __instance.BeamWidth, forward, buffer, layers);

        for (int i = 0; i < count; i++)
        {
            var hit = buffer[i];
            if (hit.transform.root == __instance.transform.root) continue;
            if (!Global.main.PhysicalObjectsInWorldByTransform.TryGetValue(hit.transform, out var value)) continue;
            if (TeamSystem.GetTeamForObject(value.gameObject) == team) continue;

            value.Ignite(UnityEngine.Random.value > 0.25f, hit.point);
            value.Temperature += 100f;
            value.SendMessage("Break", forward * __instance.DirectionalForce, SendMessageOptions.DontRequireReceiver);
            if (hit.transform.TryGetComponent<LimbBehaviour>(out var limb))
            {
                if (limb.HasJoint) limb.Slice();
                else limb.Crush();
            }
            else value.SendMessage("Slice", SendMessageOptions.DontRequireReceiver);

            ExplosionCreator.Explode(new ExplosionCreator.ExplosionParameters(
                5u, hit.point, 2f, __instance.ExplosionForce, createFx: true));
            value.rigidbody.AddForceAtPosition(
                __instance.DirectionalForce * value.rigidbody.mass * forward, hit.point, ForceMode2D.Impulse);
        }
        return false; // skip original
    }
}

/// <summary>
/// Prefix for FlamethrowerBehaviour.AffectCollider.
/// Skips same-team targets.
/// </summary>
[HarmonyPatch(typeof(FlamethrowerBehaviour), "AffectCollider")]
public static class Flamethrower_AffectCollider_Patch
{
    [HarmonyPrefix]
    public static bool Prefix(Collider2D collider, Vector2 dir, Vector2 point, FlamethrowerBehaviour __instance)
    {
        if (collider.transform.root == __instance.transform.root) return false;
        if (!Global.main.PhysicalObjectsInWorldByTransform.TryGetValue(collider.transform, out var value)) return false;

        var team = TeamSystem.GetTeamForObject(__instance.gameObject);
        if (team != null && TeamSystem.GetTeamForObject(value.gameObject) == team)
            return false; // skip same-team

        // Replicate AffectCollider logic
        switch (__instance.Effect)
        {
            case FlamethrowerBehaviour.SprayEffect.Ignite:
                value.Temperature += Time.deltaTime / value.GetHeatCapacity() * 2f;
                value.Ignite(point);
                if (value.OnFire) value.burnIntensity = 1f;
                break;
            case FlamethrowerBehaviour.SprayEffect.Extinguish:
                value.Extinguish();
                value.burnIntensity = 0f;
                break;
        }
        value.rigidbody.AddForce(dir * UnityEngine.Random.Range(0f, 0.2f), ForceMode2D.Force);
        return false; // skip original
    }
}

#endregion
