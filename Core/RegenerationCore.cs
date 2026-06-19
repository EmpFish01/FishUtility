using HarmonyHelper;
using DebugTool;
using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.EventSystems;
using UnityEngine.Rendering;
using Debug = UnityEngine.Debug;
using ZeroReflect;
using DurabilityUtility;
#pragma warning disable CS0618
#pragma warning disable CS0612

#region RE
namespace RegenerationCore;

public enum RegenAnimationMode
{
    Standard = 0,
    BoneFirst = 1,
    BoneFirstInstant = 2,
}

public enum SeveredLimbHandling
{
    Crush = 0,
    PhysicalReattach = 1,
}

[Serializable]
public struct LimbJointInfo
{
    public Vector2 anchor;
    public Vector2 connectedAnchor;
    public JointAngleLimits2D limits;
    public bool useLimits;
    public float BreakingThreshold;
    public bool anchorsOnly;

    public LimbJointInfo(Vector2 anchor, Vector2 connectedAnchor)
    {
        this.anchor = anchor;
        this.connectedAnchor = connectedAnchor;
        limits = new JointAngleLimits2D { min = 0, max = 0 };
        useLimits = false;
        BreakingThreshold = 0;
        anchorsOnly = true;
    }

    public LimbJointInfo(HingeJoint2D joint, LimbBehaviour limb)
    {
        anchor = joint.anchor;
        connectedAnchor = joint.connectedAnchor;
        limits = joint.limits;
        useLimits = joint.useLimits;
        BreakingThreshold = limb.BreakingThreshold;
        anchorsOnly = false;
    }

    public void ApplyInfo(LimbBehaviour limb)
    {
        limb.Joint.autoConfigureConnectedAnchor = false;
        limb.Joint.anchor = anchor;
        limb.Joint.connectedAnchor = connectedAnchor;
        if (anchorsOnly) return;
        limb.Joint.limits = limits;
        limb.Joint.useLimits = useLimits;
        limb.BreakingThreshold = BreakingThreshold;
    }

    public void Reverse()
    {
        limits = new JointAngleLimits2D() { min = -limits.min, max = -limits.max };
    }

    public void ApplyInfo(HingeJoint2D joint, LimbBehaviour limb)
    {
        joint.anchor = anchor;
        joint.autoConfigureConnectedAnchor = false;
        joint.connectedAnchor = connectedAnchor;
        if (anchorsOnly) return;
        joint.limits = limits;
        joint.useLimits = useLimits;
        limb.BreakingThreshold = BreakingThreshold;
    }
}

[Serializable]
public class RegenDescriptor
{
    public string Name;
    public Guid Self;
    public Guid AttachTo = Guid.Empty;
    public Guid[] AttachBy;
    public Guid CirculationSource;
    public Guid NearestLimbToBrain = Guid.Empty;
    public Guid NearestLimbToSource = Guid.Empty;
    public bool FlowToAnchor = false;
    public bool IsSource = false;
    public bool HasJoint;
    public LimbJointInfo JointDescriptor;
    public bool IsHead = false;
    public Vector3 Scale;
    public float TrueInitialMass;

    public int NeighborCount => AttachBy.Length + (HasJoint ? 1 : 0);

    public IEnumerable<Guid> Neighbors
    {
        get
        {
            foreach (Guid guid in AttachBy) yield return guid;
            if (HasJoint) yield return AttachTo;
        }
    }

    public IEnumerable<Guid> PushesTo
    {
        get
        {
            foreach (Guid guid in Neighbors)
            {
                if (guid == CirculationSource) continue;
                yield return guid;
            }
        }
    }

    public RegenDescriptor Copy()
    {
        return (RegenDescriptor)this.MemberwiseClone();
    }
}

public class RagdollPoseRecord
{
    public PoseState State;
    public Dictionary<Guid, RagdollPose.LimbPose> Angles = new();
}

public class LimbRegenData
{
    public static readonly Action<LimbBehaviour, Vector2> s_Joint = Access.CreateFieldSetter<LimbBehaviour, Vector2>("originalJointLimits");
    public bool IsHidden = false;
    public bool FlowToAnchor;
    public bool IsSource;
    public bool IsConnectedToSource;
    public LimbRegenData NearestLimbToSource;
    public LimbRegenData anchorOn;
    public PersonRegenData parent;

    [SkipSerialisation]
    public LimbBehaviour limb;
    public Guid guid;

    public bool HasJoint => Descriptor.HasJoint;
    public bool IsOriginalLimb = true;
    public bool Hidding = false;
    public bool Disconnected = false;
    public bool FromOriginalCopy = false;

    private RegenDescriptor _cachedDescriptor;
    public RegenDescriptor Descriptor
    {
        get
        {
            if (_cachedDescriptor == null)
            {
                if (parent != null && parent.Descriptors.TryGetValue(guid, out var desc))
                {
                    _cachedDescriptor = desc;
                }
                else
                {
                    _cachedDescriptor = new RegenDescriptor
                    {
                        Self = guid,
                        Name = limb?.name ?? "Unknown",
                        AttachBy = new Guid[0],
                        JointDescriptor = new LimbJointInfo { anchorsOnly = true }
                    };
                }
            }
            return _cachedDescriptor;
        }
    }
    public void InvalidateDescriptorCache() => _cachedDescriptor = null;

    private int _cachedNeighborCount = -1;
    public int NeighborCount
    {
        get
        {
            if (_cachedNeighborCount < 0)
            {
                int n = 0;
                foreach (var _ in NeighborLimbs) n++;
                _cachedNeighborCount = n;
            }
            return _cachedNeighborCount;
        }
    }
    public void InvalidateNeighborCache() => _cachedNeighborCount = -1;

    [SkipSerialisation]
    public List<LimbRegenData> AnchorBy = new();
    public Coroutine BornCoroutine;
    public bool RecoverOnDestroy = true;

    [NonSerialized]
    private bool destroyEventProcessed = false;

    public struct NeighborEnumerator
    {
        private readonly List<LimbRegenData> _anchorBy;
        private readonly LimbRegenData _anchorOn;
        private int _index;
        public LimbRegenData Current { get; private set; }
        public NeighborEnumerator(LimbRegenData owner)
        {
            _anchorBy = owner.AnchorBy;
            _anchorOn = owner.anchorOn;
            _index = -1;
            Current = null;
        }
        public bool MoveNext()
        {
            _index++;
            if (_index < _anchorBy.Count) { Current = _anchorBy[_index]; return true; }
            if (_index == _anchorBy.Count && _anchorOn != null) { Current = _anchorOn; return true; }
            Current = null; return false;
        }
    }
    public struct NeighborEnumerable
    {
        private readonly LimbRegenData _owner;
        public NeighborEnumerable(LimbRegenData owner) => _owner = owner;
        public NeighborEnumerator GetEnumerator() => new NeighborEnumerator(_owner);
    }
    public NeighborEnumerable NeighborLimbs => new NeighborEnumerable(this);


    [SkipSerialisation]
    public IEnumerable<Guid> AttachingGuid
    {
        get
        {
            foreach (var l in AnchorBy) yield return l.guid;
        }
    }

    public bool RegenAble = true;

    public bool IsSeekingReattachment = false;
    [NonSerialized] public float seekingEndTime;

    public float AttachSqrDistance
    {
        get
        {
            LimbJointInfo info = JointDescriptor;
            return (limb.gameObject.transform.TransformPoint(info.anchor)
                    - anchorOn.limb.transform.TransformPoint(info.connectedAnchor)).sqrMagnitude;
        }
    }

    public LimbJointInfo JointDescriptor => Descriptor.JointDescriptor;

    public void Initialize()
    {
        limb = limb.GetComponent<LimbBehaviour>();
        limb.HasJoint = limb.Joint;
    }

    public void SubscribeDisintegration()
    {
        limb.PhysicalBehaviour.OnDisintegration += OnDisintegrate;
    }

    public void UnsubscribeDisintegration()
    {
        if (limb && limb.PhysicalBehaviour)
            limb.PhysicalBehaviour.OnDisintegration -= OnDisintegrate;
    }

    public void PrepareForPermanentDestroy()
    {
        RecoverOnDestroy = false;
        destroyEventProcessed = true;
    }

    public void GenerateGuid()
    {
        guid = Guid.NewGuid();
    }

    public IEnumerator GrowLimb()
    {
        RegenAble = false;
        var mode = parent.AnimationMode;
        float hiddenRandomMultiplier = 1f;
        float growRandomMultiplier = 1f;
        float timeStamp = Time.time;

        Hidding = true;
        limb.transform.localScale = new Vector2(limb.transform.localScale.x, 0f);
        limb.gameObject.SetLayer(LayerMask.NameToLayer("Default"));
        yield return new WaitWhile(() => Time.time - timeStamp < parent.regenHiddenTime * hiddenRandomMultiplier);
        limb.gameObject.SetLayer(LayerMask.NameToLayer("Objects"));
        Hidding = false;

        float y = Descriptor.Scale.y;
        limb.PhysicalBehaviour.TrueInitialMass = Descriptor.TrueInitialMass;

        switch (mode)
        {
            default:
            case RegenAnimationMode.Standard:
                {
                    float progress = 0f;
                    while (progress < 1f)
                    {
                        progress += Mathf.Clamp01(Time.deltaTime / (growRandomMultiplier * parent.regenTime));
                        limb.transform.localScale = new Vector3(Descriptor.Scale.x, y * progress);
                        limb.SkinMaterialHandler.AcidProgress = 1 - progress;
                        limb.PhysicalBehaviour.RecalculateMassBasedOnSize();
                        yield return new WaitForEndOfFrame();
                    }
                    break;
                }

            case RegenAnimationMode.BoneFirst:
                {
                    float boneProgress = 0f;
                    limb.SkinMaterialHandler.AcidProgress = 1f;
                    while (boneProgress < 1f)
                    {
                        boneProgress += Mathf.Clamp01(Time.deltaTime / (growRandomMultiplier * parent.regenTime));
                        limb.transform.localScale = new Vector3(Descriptor.Scale.x, y * boneProgress);
                        limb.PhysicalBehaviour.RecalculateMassBasedOnSize();
                        yield return new WaitForEndOfFrame();
                    }
                    limb.transform.localScale = Descriptor.Scale;
                    float fleshProgress = 0f;
                    while (fleshProgress < 1f)
                    {
                        fleshProgress += Mathf.Clamp01(Time.deltaTime / (growRandomMultiplier * parent.regenTime));
                        limb.SkinMaterialHandler.AcidProgress = 1f - fleshProgress;
                        yield return new WaitForEndOfFrame();
                    }
                    break;
                }

            case RegenAnimationMode.BoneFirstInstant:
                {
                    limb.transform.localScale = Descriptor.Scale;
                    limb.SkinMaterialHandler.AcidProgress = 1f;
                    limb.PhysicalBehaviour.RecalculateMassBasedOnSize();
                    yield return null;
                    float fleshProgress = 0f;
                    while (fleshProgress < 1f)
                    {
                        fleshProgress += Mathf.Clamp01(Time.deltaTime / (growRandomMultiplier * parent.regenTime));
                        limb.SkinMaterialHandler.AcidProgress = 1f - fleshProgress;
                        yield return new WaitForEndOfFrame();
                    }
                    break;
                }
        }

        limb.transform.localScale = Descriptor.Scale;
        limb.SkinMaterialHandler.AcidProgress = 0f;
        limb.PhysicalBehaviour.RecalculateMassBasedOnSize();
        RegenAble = true;
        BornCoroutine = null;
    }

    IEnumerator CleanArtifactsCoroutine()
    {
        yield return null;
        CleanArtifacts();
    }

    public void PostInitialize()
    {
        ApplyDescriptor();
        PersonRegenData.s_MyStatus_Get(limb).transform.localScale = Vector3.one;
        if (limb.Joint)
        {
            limb.Joint.autoConfigureConnectedAnchor = false;
        }
    }

    public void CheckConnections()
    {
        if (!RegenAble || parent._cascadeCount > 0 || !parent.regenEnable || !IsConnectedToSource)
            return;

        for (int i = AnchorBy.Count - 1; i >= 0; i--)
        {
            var child = AnchorBy[i];
            if (child == null || child.limb == null || child.limb.PhysicalBehaviour == null || child.limb.PhysicalBehaviour.isDisintegrated)
            {
                AnchorBy.RemoveAt(i);
                InvalidateNeighborCache();
            }
        }

        var desc = Descriptor;
        if (desc == null) return;

        if (parent.SetSourceToAssignedGuid && guid == parent.ForceAssignGuid && !IsSource)
            SetSource();

        desc.Scale = limb.transform.localScale;

        if (desc.HasJoint && anchorOn != null
            && (anchorOn.limb == null
                || anchorOn.limb.PhysicalBehaviour == null
                || anchorOn.limb.PhysicalBehaviour.isDisintegrated))
        {
            anchorOn = null;
            InvalidateNeighborCache();
        }

        if (NeighborCount == desc.NeighborCount)
            return;

        if (desc.HasJoint && anchorOn == null)
        {
            var attached = parent.InstantiateLimbInstance(desc.AttachTo);
            if (attached != null)
            {
                if (parent.SeveredLimbHandling == SeveredLimbHandling.PhysicalReattach && desc.AttachTo != Guid.Empty)
                {
                    this.anchorOn = attached;
                    this.IsSeekingReattachment = true;
                    if (!this.IsSource) this.IsConnectedToSource = false;
                    if (this.seekingEndTime < Time.time) this.seekingEndTime = Time.time;
                }
                else if (!this.IsSeekingReattachment)
                {
                    AttachLimb();
                }
            }
        }

        int len = desc.AttachBy.Length;
        if (len > AnchorBy.Count)
        {
            for (int i = 0; i < len; i++)
            {
                Guid g = desc.AttachBy[i];
                bool found = false;
                for (int j = 0; j < AnchorBy.Count; j++) { if (AnchorBy[j].guid == g) { found = true; break; } }

                if (!found)
                {
                    var regen = parent.InstantiateLimbInstance(g);
                    if (regen != null)
                    {
                        if (parent.SeveredLimbHandling == SeveredLimbHandling.PhysicalReattach && regen.Descriptor.HasJoint)
                        {
                            regen.anchorOn = this;
                            regen.IsSeekingReattachment = true;
                            // 保护飞行状态
                            if (regen.seekingEndTime < Time.time) regen.seekingEndTime = Time.time;
                        }
                        else if (!regen.IsSeekingReattachment)
                        {
                            regen.AttachLimb();
                        }
                    }
                }
            }
        }
    }

    public void SetSource()
    {
        parent.SetSource(guid);
    }

    public void HandleDestroy()
    {
        UnsubscribeDisintegration();

        if (!RecoverOnDestroy || parent == null || guid == Guid.Empty || destroyEventProcessed)
            return;

        // If the Person root has been destroyed, there is no body to rebuild
        // onto — the character is truly dead. Skip all regeneration cascade to
        // avoid side effects (e.g. severed-limb Crush particles).
        if (parent._disposed || parent._personDestroyed || !parent.PersonAlive)
        {
            destroyEventProcessed = true;
            RecoverOnDestroy = false;
            return;
        }

        destroyEventProcessed = true;
        RegenAble = false;

        bool wasSource = IsSource;

        parent._cascadeCount++;
        try
        {
            if (wasSource && parent.RedistributeSource)
            {
                LimbRegenData best = null;
                int bestCount = -1;
                for (int ni = 0; ni < AnchorBy.Count; ni++)
                {
                    var n = AnchorBy[ni];
                    if (n.RegenAble)
                    {
                        int cnt = n.Whole().Count;
                        if (cnt > bestCount) { bestCount = cnt; best = n; }
                    }
                }
                if (anchorOn != null && anchorOn.RegenAble)
                {
                    int cnt = anchorOn.Whole().Count;
                    if (cnt > bestCount) { bestCount = cnt; best = anchorOn; }
                }
                best?.SetSource();
            }

            if (anchorOn != null)
                LimbJointBreak();

            var regens = _tempRegenList ??= new();
            regens.Clear();
            regens.AddRange(AnchorBy);
            for (int ri = 0; ri < regens.Count; ri++)
            {
                regens[ri].LimbJointBreak();
            }
        }
        finally
        {
            parent._cascadeCount--;
        }

        bool isPhysical = parent.SeveredLimbHandling == SeveredLimbHandling.PhysicalReattach;
        foreach (LimbRegenData d in parent.Entries)
        {
            if (d.anchorOn == this)
            {
                if (!isPhysical)
                {
                    d.anchorOn = null;
                    d.IsSeekingReattachment = false;
                }
            }
        }

        if (wasSource && parent.SourceGuid != Guid.Empty
            && parent.Guid2Regen.TryGetValue(parent.SourceGuid, out var newSrc))
        {
            foreach (LimbRegenData d in newSrc.Whole())
            {
                d.IsConnectedToSource = true;
                d.IsSeekingReattachment = false;
            }
        }

        parent.HandleDestroyedLimb(this);
    }

    private void OnDisintegrate(object sender, EventArgs e)
    {
        if (destroyEventProcessed) return;
        destroyEventProcessed = true;
        if (guid == Guid.Empty) return;

        if (parent._disposed || parent._personDestroyed || !parent.PersonAlive)
        {
            RecoverOnDestroy = false;
            return;
        }

        if (parent.SeveredLimbHandling != SeveredLimbHandling.PhysicalReattach)
            RegenAble = false;

        bool wasSource = IsSource;

        parent._cascadeCount++;
        try
        {
            if (wasSource && parent.RedistributeSource)
            {
                LimbRegenData best = null;
                int bestCount = -1;
                for (int ni = 0; ni < AnchorBy.Count; ni++)
                {
                    var n = AnchorBy[ni];
                    if (n.RegenAble)
                    {
                        int cnt = n.Whole().Count;
                        if (cnt > bestCount) { bestCount = cnt; best = n; }
                    }
                }
                if (anchorOn != null && anchorOn.RegenAble)
                {
                    int cnt = anchorOn.Whole().Count;
                    if (cnt > bestCount) { bestCount = cnt; best = anchorOn; }
                }
                best?.SetSource();
            }

            if (anchorOn != null)
                LimbJointBreak();

            var regens = _tempRegenList ??= new();
            regens.Clear();
            regens.AddRange(AnchorBy);
            for (int ri = 0; ri < regens.Count; ri++)
            {
                regens[ri].LimbJointBreak();
            }
        }
        finally
        {
            parent._cascadeCount--;
        }

        bool isPhysical = parent.SeveredLimbHandling == SeveredLimbHandling.PhysicalReattach;
        foreach (LimbRegenData d in parent.Entries)
        {
            if (d.anchorOn == this)
            {
                if (!isPhysical)
                {
                    d.anchorOn = null;
                    d.IsSeekingReattachment = false;
                }
            }
        }

        if (wasSource && parent.SourceGuid != Guid.Empty
            && parent.Guid2Regen.TryGetValue(parent.SourceGuid, out var newSrc))
        {
            foreach (LimbRegenData d in newSrc.Whole())
            {
                d.IsConnectedToSource = true;
                d.IsSeekingReattachment = false;
            }
        }

        if (!Disconnected)
            parent.Disconnect(this);
    }

    public void HandleJointBreak(Joint2D brokenJoint = null)
    {
        if (brokenJoint != null && brokenJoint != limb.Joint) return;
        LimbJointBreak();
    }

    public void LimbJointBreak()
    {
        if (Disconnected || parent._disposed)
            return;

        if (anchorOn != null)
            anchorOn.AnchorBy.Remove(this);
        LimbRegenData AnchorOn = anchorOn;

        if (parent.SeveredLimbHandling == SeveredLimbHandling.PhysicalReattach)
        {
            IsConnectedToSource = false;
            IsSeekingReattachment = true;
            seekingEndTime = Time.time + parent.severedDelay;

            LockChildJointAnchors();

            InvalidateNeighborCache();
            ApplyNeighbors();
            if (AnchorOn != null) { AnchorOn.InvalidateNeighborCache(); AnchorOn.ApplyNeighbors(); }
            limb.gameObject.BroadcastMessage("OnLimbJointBreak");
            return;
        }

        anchorOn = null;

        if (IsConnectedToSource)
        {
            if (FlowToAnchor)
            {
                foreach (LimbRegenData regen in Whole())
                {
                    regen.IsConnectedToSource = false;
                    parent.Disconnect(regen);
                }
            }
            else if (AnchorOn != null)
            {
                foreach (LimbRegenData regen in AnchorOn.Whole())
                {
                    regen.IsConnectedToSource = false;
                    parent.Disconnect(regen);
                }
            }
        }

        InvalidateNeighborCache();
        ApplyNeighbors();
        if (AnchorOn != null) { AnchorOn.InvalidateNeighborCache(); AnchorOn.ApplyNeighbors(); }
        limb.gameObject.BroadcastMessage("OnLimbJointBreak");
    }

    public void SpreadConnection()
    {
        if (!limb.Joint || limb.PhysicalBehaviour.isDisintegrated) return;

        anchorOn = limb.Joint.connectedBody.gameObject.GetComponent<LimbBehaviour>() != null
            ? parent.GetDataForLimb(limb.Joint.connectedBody.gameObject.GetComponent<LimbBehaviour>())
            : null;

        if (anchorOn != null && anchorOn.limb.PhysicalBehaviour.isDisintegrated)
        {
            anchorOn = null;
            return;
        }
        if (anchorOn != null)
            anchorOn.AnchorBy.Add(this);
    }

    public void AttachLimb(float Angle = 0f, bool skipReposition = false)
    {
        limb.gameObject.BroadcastMessage("LimbJointCreate");

        if (!HasJoint) return;

        anchorOn = parent.Guid2Regen[Descriptor.AttachTo];
        anchorOn.AnchorBy.Add(this);

        if (!skipReposition)
        {
            if (FlowToAnchor)
                Reposition();
            else
                InverseReposition();
        }

        Rigidbody2D rb = limb.gameObject.GetComponent<Rigidbody2D>();
        Rigidbody2D anchorRb = anchorOn.limb.gameObject.GetComponent<Rigidbody2D>();

        limb.Joint = limb.gameObject.AddComponent<HingeJoint2D>();
        limb.Joint.autoConfigureConnectedAnchor = false;
        limb.Joint.anchor = JointDescriptor.anchor;
        limb.Joint.connectedAnchor = JointDescriptor.connectedAnchor;
        float direction = parent.Direction;
        limb.Joint.limits = new JointAngleLimits2D() { min = JointDescriptor.limits.min * direction, max = JointDescriptor.limits.max * direction };
        limb.Joint.useLimits = JointDescriptor.useLimits;
        limb.BreakingThreshold = JointDescriptor.BreakingThreshold;
        limb.Joint.enableCollision = false;
        limb.Joint.connectedBody = anchorRb;

        limb.Broken = false;
        limb.IsDismembered = false;
        limb.CirculationBehaviour.IsDisconnected = false;
        limb.HasJoint = true;

        if (FlowToAnchor)
            IsConnectedToSource = anchorOn.IsConnectedToSource;
        else
            anchorOn.IsConnectedToSource = IsConnectedToSource;

        if (limb.gameObject.TryGetComponent<GoreStringBehaviour>(out var GoreString))
        {
            GoreString.Other = anchorOn.limb.gameObject.GetComponent<Rigidbody2D>();
            GoreString.DestroyJoint();
        }

        limb.Joint.useMotor = true;
        limb.Joint.motor = new JointMotor2D
        {
            maxMotorTorque = limb.MotorStrength,
            motorSpeed = 0f
        };

        s_Joint(limb, new Vector2(JointDescriptor.limits.min, JointDescriptor.limits.max));

        ApplyDescriptor();
        anchorOn.ApplyDescriptor();
    }

    public void Reposition(float angle = 0f)
    {
        Rigidbody2D rb0 = limb.PhysicalBehaviour.rigidbody;
        Rigidbody2D rb1 = anchorOn.limb.PhysicalBehaviour.rigidbody;

        limb.transform.rotation = anchorOn.limb.transform.rotation * Quaternion.AngleAxis(angle, Vector3.forward);
        rb0.rotation = rb1.rotation + angle;

        LimbJointInfo info = JointDescriptor;
        rb0.position -= rb0.GetRelativePoint(info.anchor) - rb1.GetRelativePoint(info.connectedAnchor);
        limb.transform.position -= limb.transform.TransformPoint(info.anchor) - anchorOn.limb.transform.TransformPoint(info.connectedAnchor);
    }

    public void InverseReposition(float angle = 0f)
    {
        Rigidbody2D rb0 = anchorOn.limb.PhysicalBehaviour.rigidbody;
        Rigidbody2D rb1 = limb.PhysicalBehaviour.rigidbody;

        anchorOn.limb.transform.rotation = limb.transform.rotation * Quaternion.AngleAxis(angle, Vector3.forward);
        rb0.rotation = rb1.rotation + angle;

        LimbJointInfo info = parent.Descriptors[anchorOn.guid].JointDescriptor;
        rb0.position += rb1.GetRelativePoint(info.connectedAnchor) - rb0.GetRelativePoint(info.anchor);
        anchorOn.limb.transform.position += limb.transform.TransformPoint(info.anchor) - anchorOn.limb.transform.TransformPoint(info.connectedAnchor);
    }

    void LockChildJointAnchors()
    {
        for (int i = 0; i < AnchorBy.Count; i++)
        {
            LimbRegenData child = AnchorBy[i];
            if (child == null || child.limb == null || !child.limb.Joint) continue;

            child.limb.Joint.autoConfigureConnectedAnchor = false;

            child.LockChildJointAnchors();
        }
    }

    public bool PhysicalAttractStep()
    {
        if (anchorOn == null || !IsSeekingReattachment || limb == null || anchorOn.limb == null)
            return false;

        if (limb.PhysicalBehaviour.isDisintegrated || anchorOn.limb.PhysicalBehaviour.isDisintegrated)
            return false;

        var rb = limb.PhysicalBehaviour.rigidbody;
        var anchorRb = anchorOn.limb.PhysicalBehaviour.rigidbody;

        if (Time.time < seekingEndTime)
        {
            rb.angularVelocity = Mathf.MoveTowards(rb.angularVelocity, 0f, Time.deltaTime * 120f);
            if (rb.angularVelocity > 360f) rb.angularVelocity = 180f;
            return false;
        }

        var info = JointDescriptor;
        Vector2 anchorPoint = limb.transform.TransformPoint(info.anchor);
        Vector2 targetPoint = anchorOn.limb.transform.TransformPoint(info.connectedAnchor);
        Vector2 displacement = anchorPoint - targetPoint;
        if (displacement == Vector2.zero) displacement = new Vector2(0.01f, 0.01f);
        Vector2 direction = displacement.normalized;


        rb.angularVelocity = Mathf.MoveTowards(rb.angularVelocity, 0f, Time.deltaTime * 120f);
        anchorRb.angularVelocity = Mathf.MoveTowards(anchorRb.angularVelocity, 0f, Time.deltaTime * 120f);
        if (rb.angularVelocity > 360f) rb.angularVelocity = 180f;
        if (anchorRb.angularVelocity > 360f) anchorRb.angularVelocity = 180f;

        if (rb.velocity.magnitude < 20f)
            rb.AddForceAtPosition(direction * Time.deltaTime * -5000f * rb.mass, anchorPoint);
        else
            rb.velocity = 22f * direction * -1f;

        if (anchorRb.velocity.magnitude < 20f)
            anchorRb.AddForceAtPosition(direction * Time.deltaTime * 5000f * anchorRb.mass, targetPoint);
        else
            anchorRb.velocity = 22f * direction;


        if (displacement.sqrMagnitude < 0.05f)
        {
            AttachLimb();
            LockChildJointAnchors();
            IsSeekingReattachment = false;
            anchorOn.ApplyNeighbors();

            if (parent != null && parent.SourceGuid != Guid.Empty)
            {
                parent.SetSource(parent.SourceGuid);
            }

            return true;
        }
        return false;
    }

    private static Queue<LimbRegenData> _bfsQueue;
    private static HashSet<LimbRegenData> _bfsVisited;
    private static List<LimbRegenData> _bfsResult;
    private static List<LimbRegenData> _tempRegenList;

    public List<LimbRegenData> Whole()
    {
        var q = _bfsQueue ??= new(); q.Clear();
        var visited = _bfsVisited ??= new(); visited.Clear();
        var result = _bfsResult ??= new(); result.Clear();

        q.Enqueue(this);
        visited.Add(this);

        int max = parent.Entries.Count;
        while (q.Count > 0 && max-- > 0)
        {
            var current = q.Dequeue();
            result.Add(current);
            foreach (var neighbor in current.NeighborLimbs)
            {
                if (visited.Add(neighbor))
                    q.Enqueue(neighbor);
            }
        }
        return result;
    }

    public void Reset(bool newlimb = true)
    {
        limb.SkinMaterialHandler.ClearAllDamage();
        limb.Health = limb.InitialHealth;
        limb.CirculationBehaviour.IsPump = limb.CirculationBehaviour.WasInitiallyPumping;
        limb.CirculationBehaviour.BloodFlow = 1f;
        limb.HasJoint = false;
        limb.CirculationBehaviour.HealBleeding();
        limb.CirculationBehaviour.ClearLiquid();
        limb.CirculationBehaviour.AddLiquid(Liquid.GetLiquid(limb.BloodLiquidType), 1f);
        if (Descriptor.IsHead)
        {
            parent.Person.BrainDamaged = false;
            parent.Person.Braindead = false;
        }
        AnchorBy.Clear();
        anchorOn = null;
        RegenManager.Instance.StartManagedCoroutine(CleanArtifactsCoroutine());
        BornCoroutine = RegenManager.Instance.StartManagedCoroutine(GrowLimb());
    }

    public void CleanArtifacts()
    {
        ParticleSystem[] particlesystems = limb.gameObject.GetComponentsInChildren<ParticleSystem>();

        foreach (AudioSource audio in limb.gameObject.GetComponentsInChildren<AudioSource>())
        {
            Global.main.AddAudioSource(audio, true);
        }

        foreach (ParticleSystem particlesystem in particlesystems)
        {
            ParticleSystem.EmissionModule emissionModule = particlesystem.emission;
            emissionModule.rateOverTimeMultiplier = 0f;
        }

        for (int i = 0; i < limb.gameObject.transform.childCount; i++)
        {
            if (limb.gameObject.transform.GetChild(i).name.Contains("Outline"))
            {
                limb.gameObject.transform.GetChild(i).gameObject.SetActive(false);
            }
        }

        if (limb.gameObject.TryGetComponent<FreezeBehaviour>(out FreezeBehaviour freeze))
        {
            UnityEngine.Object.Destroy(freeze);
        }
    }

    public void ApplyDescriptorForSourceConnection()
    {
        var desc = Descriptor;
        FlowToAnchor = desc.FlowToAnchor;
        IsSource = desc.IsSource;
        var nsGuid = desc.NearestLimbToSource;
        if (nsGuid != Guid.Empty && parent.Guid2Regen.TryGetValue(nsGuid, out var ns))
            NearestLimbToSource = ns;
    }

    public void ApplyNeighbors()
    {
        InvalidateNeighborCache();
        var connected = limb.ConnectedLimbs;
        connected.Clear();
        for (int ni = 0; ni < AnchorBy.Count; ni++)
            connected.Add(AnchorBy[ni].limb);
        if (anchorOn != null)
            connected.Add(anchorOn.limb);

        var connArr = new ConnectedNodeBehaviour[connected.Count];
        for (int ci = 0; ci < connected.Count; ci++)
            connArr[ci] = connected[ci].NodeBehaviour;
        limb.NodeBehaviour.Connections = connArr;

        var adjArr = new SkinMaterialHandler[connected.Count];
        for (int ci2 = 0; ci2 < connected.Count; ci2++)
            adjArr[ci2] = connected[ci2].SkinMaterialHandler;
        limb.SkinMaterialHandler.adjacentLimbs = adjArr;
    }

    public void ApplyDescriptor()
    {
        ApplyNeighbors();
        var desc = Descriptor;

        if (desc.CirculationSource != Guid.Empty && parent.Guid2Regen.TryGetValue(desc.CirculationSource, out var src))
            limb.CirculationBehaviour.Source = src.limb.CirculationBehaviour;
        else
            limb.CirculationBehaviour.Source = null;

        var pushes = new List<CirculationBehaviour>();
        foreach (Guid g in desc.PushesTo)
        {
            if (parent.Guid2Regen.TryGetValue(g, out var pushData))
                pushes.Add(pushData.limb.CirculationBehaviour);
        }
        limb.CirculationBehaviour.PushesTo = pushes.ToArray();

        ApplyDescriptorForSourceConnection();

        if (desc.NearestLimbToBrain != Guid.Empty && parent.Guid2Regen.TryGetValue(desc.NearestLimbToBrain, out var brainData))
            limb.NearestLimbToBrain = brainData.limb;
        else
            limb.NearestLimbToBrain = null;
    }
}

public class PersonRegenData
{
    public static readonly Action<PersonBehaviour, LimbBehaviour> s_Head = Access.CreateFieldSetter<PersonBehaviour, LimbBehaviour>("Head");
    public static readonly Action<SkinMaterialHandler, Material> s_Mat = Access.CreateFieldSetter<SkinMaterialHandler, Material>("material");
    public static readonly Func<LimbBehaviour, GameObject> s_MyStatus_Get = Access.CreateFieldGetter<LimbBehaviour, GameObject>("myStatus");
    public static readonly Action<LimbBehaviour, GameObject> s_MyStatus_Set = Access.CreateFieldSetter<LimbBehaviour, GameObject>("myStatus");

    public const float Epsilon = 0.01f;

    public float HealRate = 2f;
    public bool PainedEmotion = false;
    public bool AddContextMenuOptions = true;
    public PersonBehaviour Person;

    public List<LimbRegenData> Entries = new();
    public List<Collider2D> DisableCollider = new();
    public PersonBehaviour FakeDetachPerson;
    public LimbRegenData Source
    {
        get
        {
            if (Guid2Regen.ContainsKey(SourceGuid))
                return Guid2Regen[SourceGuid];
            return null;
        }
    }
    public Guid SourceGuid;
    public Dictionary<PoseState, RagdollPoseRecord> Poses = new();
    public Dictionary<Guid, RegenDescriptor> Descriptors = new();
    public Dictionary<Guid, LimbRegenData> Guid2Regen = new();
    public Dictionary<Guid, GameObject> Duplicates = new();
    public Dictionary<string, Guid> Name2Guid = new();
    public bool regenEnable = true;
    public float regenTime = 0.1f;
    public float regenHiddenTime = 0.1f;
    public float severedDelay = 0.3f;
    public RegenAnimationMode AnimationMode = RegenAnimationMode.Standard;
    public bool RedistributeSource = true;
    public SeveredLimbHandling SeveredLimbHandling = SeveredLimbHandling.Crush;
    public Guid RootGuid = Guid.Empty;

    [NonSerialized] public HashSet<Guid> PendingSeeking = new();

    [NonSerialized] public Dictionary<Guid, float> _destroyedTimestamps = new();

    [NonSerialized] public Vector3 _lastLimbPosition;
    [NonSerialized] public bool _hasLastLimbPosition;

    public SpawnableAsset CachedSpawnableAsset;

    [NonSerialized] public bool _disposed;

    [NonSerialized] public bool _personDestroyed;

    [NonSerialized] public int _cascadeCount;

    public bool PersonAlive => Person && Person.gameObject;

    public LimbStatData LinkedStatData;

    [NonSerialized]
    public bool SetSourceToAssignedGuid = false;
    public Guid ForceAssignGuid;


    public float Direction => (Person.transform.localScale.x < 0) ? -1f : 1f;

    public Dictionary<LimbBehaviour, LimbRegenData> _byLimb = new();

    public LimbRegenData GetDataForLimb(LimbBehaviour lb)
    {
        _byLimb.TryGetValue(lb, out var d);
        return d;
    }

    public void Setup()
    {
        Entries.Clear();
        Guid2Regen.Clear();
        Name2Guid.Clear();
        _byLimb.Clear();

        foreach (LimbBehaviour limb in Person.Limbs)
        {
            LimbRegenData data = new LimbRegenData
            {
                limb = limb,
                parent = this
            };
            data.Initialize();
            data.SubscribeDisintegration();
            data.GenerateGuid();

            if (!Name2Guid.ContainsKey(limb.name))
                Name2Guid.Add(limb.name, data.guid);

            Guid2Regen.Add(data.guid, data);
            Entries.Add(data);
            _byLimb[limb] = data;
        }
    }

    public void SecureRegens()
    {
        foreach (Guid guid in Guid2Regen.Keys)
        {
            Guid2Regen[guid].parent = this;
            Guid2Regen[guid].guid = guid;
            _byLimb[Guid2Regen[guid].limb] = Guid2Regen[guid];
        }
    }

    public void PostSetup()
    {
        if (AddContextMenuOptions)
        {
            foreach (LimbBehaviour limb in Person.Limbs)
            {
                AddContextMenuToLimb(limb);
            }
        }

        Person.RandomisedSize = false;

        if (RootGuid == Guid.Empty)
        {
            LimbRegenData root = null;
            foreach (LimbRegenData d in Entries)
            {
                if (d != null && d.limb && d.limb.NodeBehaviour && d.limb.NodeBehaviour.IsRoot)
                {
                    root = d;
                    break;
                }
            }
            if (root != null)
                RootGuid = root.guid;
            else if (SourceGuid != Guid.Empty)
                RootGuid = SourceGuid;
        }

        foreach (LimbRegenData d in Entries)
        {
            DisablepublicCollision(d.limb.gameObject.GetComponent<Collider2D>());
        }

        if (SourceGuid != Guid.Empty)
            return;

        LimbRegenData rootRegen = null;
        foreach (LimbRegenData d in Entries)
        {
            if (!d.limb.Joint)
            {
                rootRegen = d;
                break;
            }
        }
        if (rootRegen == null)
        {
            foreach (LimbRegenData d in Entries)
            {
                if (d.limb.NodeBehaviour.IsRoot)
                {
                    rootRegen = d;
                    break;
                }
            }
        }
        if (rootRegen != null)
            rootRegen.SetSource();
    }

    public void AddContextMenuToLimb(LimbBehaviour limb)
    {
        LimbRegenData regenData = GetDataForLimb(limb);
        if (regenData == null) return;

        limb.PhysicalBehaviour.ContextMenuOptions.Buttons.Add(new ContextMenuButton(
            () => regenData.IsConnectedToSource,
            "setRegeneration", "Set regeneration rate", "Set regeneration rate",
            () =>
            {
                Utils.OpenFloatInputDialog(HealRate, RegenManager.Instance, delegate (RegenManager menu, float rate)
                {
                    HealRate = rate;
                }, "Set regeneration rate", "Target regeneration rate");
            }));

        limb.PhysicalBehaviour.ContextMenuOptions.Buttons.Add(new ContextMenuButton(
            () => regenData.IsConnectedToSource,
            "cycleRegenAnim",
            () => $"Regen animation: {AnimationMode}",
            "Cycle regeneration animation mode (Standard / BoneFirst / BoneFirstInstant)",
            () =>
            {
                AnimationMode = (RegenAnimationMode)(((int)AnimationMode + 1) % 3);
                ModAPI.Notify($"Regen animation: {AnimationMode}");
            }));

        limb.PhysicalBehaviour.ContextMenuOptions.Buttons.Add(new ContextMenuButton(
            () => regenData.IsConnectedToSource,
            "cycleSeveredHandling",
            () => $"Severed limbs: {SeveredLimbHandling}",
            "Cycle severed limb handling mode (Crush / PhysicalReattach)",
            () =>
            {
                SeveredLimbHandling = (SeveredLimbHandling)(((int)SeveredLimbHandling + 1) % 2);
                ModAPI.Notify($"Severed limbs: {SeveredLimbHandling}");
            }));

        limb.PhysicalBehaviour.ContextMenuOptions.Buttons.Add(new ContextMenuButton(
            () => regenData.IsConnectedToSource,
            "setSource", "Regenerate from here", "Regenerate from here",
            () =>
            {
                ForceAssignGuid = regenData.guid;
                regenData.SetSource();
                ModAPI.Notify($"Set {regenData.limb.name} as regeneration core");
            }));
    }

    public void NotifyOnRegen(LimbRegenData regenData)
    {
        if (AddContextMenuOptions)
            AddContextMenuToLimb(regenData.limb);
    }

    public void Regenerate(float delta)
    {
        if (_disposed || _personDestroyed || !PersonAlive)
            return;
        LimbBehaviour limb;
        bool HasBrain = false;
        foreach (LimbRegenData data in Entries)
        {
            limb = data.limb;
            if (!limb) continue;

            limb.Numbness = 0f;
            limb.Health = Mathf.Max(0f, limb.Health);
            limb.Health = Mathf.MoveTowards(limb.Health, limb.InitialHealth, Mathf.Max(5f, limb.InitialHealth) * delta * 0.4f);
            limb.HealBone();
            limb.IsZombie = false;
            limb.LungsPunctured = false;
            limb.CirculationBehaviour.IsPump = limb.CirculationBehaviour.WasInitiallyPumping;
            limb.CirculationBehaviour.HealBleeding();
            limb.CirculationBehaviour.AddLiquid(limb.GetOriginalBloodType(), Mathf.Max(0f, 1f - limb.CirculationBehaviour.GetAmount(limb.GetOriginalBloodType())));
            limb.PhysicalBehaviour.BurnProgress = Mathf.MoveTowards(limb.PhysicalBehaviour.BurnProgress, 0f, delta * 0.05f);
            limb.SkinMaterialHandler.RottenProgress = Mathf.MoveTowards(limb.SkinMaterialHandler.RottenProgress, 0f, delta * 0.05f);
            limb.SkinMaterialHandler.AcidProgress = Mathf.MoveTowards(limb.SkinMaterialHandler.AcidProgress, 0f, delta * 0.05f);

            if (limb.PhysicalBehaviour.OnFire)
            {
                limb.BodyTemperature = Mathf.MoveTowards(limb.PhysicalBehaviour.Temperature, limb.BodyTemperature, delta * 300f);
                if (limb.PhysicalBehaviour.Temperature < limb.PhysicalBehaviour.Properties.BurningTemperatureThreshold)
                    limb.PhysicalBehaviour.Extinguish();
            }

            if (data.Descriptor.IsHead)
            {
                HasBrain = true;
                limb.Person.Consciousness = Mathf.MoveTowards(limb.Person.Consciousness, 1f, delta * 0.05f);
                limb.Person.Braindead = false;
                limb.Person.BrainDamaged = false;
                if (!PainedEmotion)
                {
                    limb.Person.OxygenLevel = 1f;
                    limb.Person.ShockLevel = 0f;
                    limb.Person.PainLevel = 0f;
                }
            }

            int damagePointsCount = 0;
            ushort brusieCount = 0;
            ushort stabCount = 0;
            ushort shotCount = 0;

            float factor = Mathf.Pow(0.8f, delta);
            for (int j = 0; j < limb.SkinMaterialHandler.currentDamagePointCount; j++)
            {
                Vector4 damagePoint = limb.SkinMaterialHandler.damagePoints[j];
                damagePoint.z = damagePoint.z * factor;
                limb.SkinMaterialHandler.damagePoints[damagePointsCount] = damagePoint;
                if (damagePoint.z < Epsilon) continue;
                damagePointsCount += 1;
                if (damagePoint.z < 0.6f) continue;

                switch (damagePoint.w)
                {
                    case (float)DamageType.Blunt: brusieCount += 1; break;
                    case (float)DamageType.Stab: stabCount += 1; break;
                    case (float)DamageType.Bullet: shotCount += 1; break;
                }
            }

            limb.BruiseCount = brusieCount;
            limb.CirculationBehaviour.StabWoundCount = stabCount;
            limb.CirculationBehaviour.GunshotWoundCount = shotCount;
            limb.SkinMaterialHandler.currentDamagePointCount = damagePointsCount;
            limb.SkinMaterialHandler.Sync();
        }
        if (!HasBrain)
            Person.Consciousness = 0f;
    }

    public void TickRegeneration()
    {
        if (_disposed || _personDestroyed || !PersonAlive)
            return;

        if (LinkedStatData != null)
        {
            bool ignoreDeath = (bool)LinkedStatData.Settings.IgnoreDeath;
            if (!ignoreDeath && Person && !Person.IsAlive())
                return;
        }

        float delta = Time.deltaTime * HealRate;
        if (LinkedStatData == null)
            Regenerate(delta);
        regenTime = 10f / Mathf.Max(0.001f, HealRate);
        regenHiddenTime = 1f / Mathf.Max(0.001f, HealRate);
    }

    public void TickConnections()
    {
        if (_disposed || _personDestroyed || !PersonAlive)
            return;

        if (Entries.Count == 0)
        {
            // Full-body regeneration from root: when every limb is gone,
            // regrow the jointless root limb and rebuild the entire body from it.
            if (RootGuid != Guid.Empty && Duplicates.ContainsKey(RootGuid))
            {
                _destroyedTimestamps.Clear();
                var rootRegen = InstantiateLimbInstance(RootGuid);
                if (rootRegen != null)
                {
                    if (_hasLastLimbPosition)
                    {
                        rootRegen.limb.transform.position = _lastLimbPosition;
                        rootRegen.limb.PhysicalBehaviour.rigidbody.position = _lastLimbPosition;
                    }
                    rootRegen.SetSource();
                    return;
                }
            }
            RegenManager.Instance.Unregister(Person);
            return;
        }

        bool hasSource = false;
        for (int i = 0; i < Entries.Count; i++)
        {
            if (Entries[i].IsConnectedToSource || Entries[i].IsSource)
            {
                hasSource = true;
                break;
            }
        }

        if (!hasSource && Entries.Count > 0)
        {
            if (Guid2Regen.TryGetValue(SourceGuid, out var currentSource) && Entries.Contains(currentSource))
            {
                currentSource.SetSource();
            }
            else
            {
                LimbRegenData fallbackSource = Entries.Find(d => d.guid == RootGuid) ?? Entries[0];
                fallbackSource?.SetSource();
                Debug.Log($"[RegenManager] 警告：全肢体失去再生源，已强制重启核心节点 ({fallbackSource?.limb?.name})");
            }
        }

        for (int i = 0; i < Entries.Count; i++)
        {
            LimbRegenData data = Entries[i];
            if (data == null) continue;

            try
            {
                data.CheckConnections();
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[RegenManager] CheckConnections 异常被捕获 ({data.limb?.name}): {ex.Message}");
            }
        }

        if (SeveredLimbHandling == SeveredLimbHandling.PhysicalReattach)
        {
            for (int i = 0; i < Entries.Count; i++)
            {
                LimbRegenData data = Entries[i];
                if (data == null || !data.IsSeekingReattachment) continue;
                if (!data.limb || data.limb.PhysicalBehaviour.isDisintegrated) continue;
                try
                {
                    if (data.anchorOn == null || !data.anchorOn.limb || data.anchorOn.limb.PhysicalBehaviour.isDisintegrated)
                    {
                        if (data.Descriptor.HasJoint && data.Descriptor.AttachTo != Guid.Empty)
                        {
                            if (Guid2Regen.TryGetValue(data.Descriptor.AttachTo, out var existingAnchor)
                                && existingAnchor != null && existingAnchor.limb != null && !existingAnchor.limb.PhysicalBehaviour.isDisintegrated)
                            {
                                data.anchorOn = existingAnchor;
                            }
                            else
                            {
                                var regenerated = InstantiateLimbInstance(data.Descriptor.AttachTo);
                                if (regenerated != null)
                                    data.anchorOn = regenerated;
                            }
                        }
                        else
                        {
                            // 【修改点 3B：破除幻影追踪！如果节点在寻路，但根本没有有效目标，强行结束它的寻路状态】
                            data.IsSeekingReattachment = false;
                        }
                        continue;
                    }

                    data.PhysicalAttractStep();
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[RegenManager] PhysicalAttractStep 异常被捕获 ({data.limb?.name}): {ex.Message}");
                }
            }
        }
    }

    public void OnBeforeSerialise()
    {
        foreach (LimbRegenData data in Entries)
            data.FromOriginalCopy = true;
    }

    public void OnAfterDeserialise()
    {
        LimbStatEntry._isRegenSystemCrush = true;
        try
        {
            foreach (LimbRegenData data in Entries)
            {
                if (!data.FromOriginalCopy)
                    data.limb.PhysicalBehaviour.Disintegrate();
            }
        }
        finally { LimbStatEntry._isRegenSystemCrush = false; }

        SecureRegens();
        LoadDataFromPrefab();

        if (RootGuid == Guid.Empty)
        {
            LimbRegenData root = null;
            foreach (LimbRegenData d in Entries)
            {
                if (d != null && d.limb && d.limb.NodeBehaviour && d.limb.NodeBehaviour.IsRoot)
                {
                    root = d;
                    break;
                }
            }
            if (root != null)
                RootGuid = root.guid;
        }

        RegenManager.Instance.StartManagedCoroutine(PostDeserialiseCleanup());
    }

    IEnumerator PostDeserialiseCleanup()
    {
        yield return null;
        for (int i = Entries.Count - 1; i >= 0; i--)
        {
            LimbRegenData d = Entries[i];
            if (d != null && !d.RegenAble && d.BornCoroutine == null)
            {
                DestroyLimb(d.limb);
                Disconnect(d);
            }
        }
    }

    public void RecordState()
    {
        RefreshLimbsConnection();
        RecordPose();
        InitDescriptors();
    }

    public bool LoadDataFromPrefab()
    {
        SerialiseInstructions si = Person.gameObject.GetComponent<SerialiseInstructions>();
        if (!si) return false;

        SpawnableAsset spawnableAsset = si.OriginalSpawnableAsset;
        if (!spawnableAsset || !spawnableAsset.Prefab) return false;

        CachedSpawnableAsset = spawnableAsset;

        GameObject PrefabInstance = UnityEngine.Object.Instantiate(spawnableAsset.Prefab);
        CatalogBehaviour.PerformAfterSpawn(spawnableAsset, PrefabInstance);

        PersonBehaviour prefabPerson = PrefabInstance.GetComponent<PersonBehaviour>();
        LimbRegenData SourceRegen = null;
        foreach (LimbRegenData d in Entries)
        {
            if (d.IsSource) { SourceRegen = d; break; }
        }

        TransferNodeDataFromPrefab(prefabPerson, PrefabInstance);

        UnityEngine.Object.Destroy(PrefabInstance);

        if (SourceRegen != null)
            SourceRegen.SetSource();

        return true;
    }

    void TransferNodeDataFromPrefab(PersonBehaviour prefabPerson, GameObject prefabInstance)
    {
        var tempDescriptors = new Dictionary<Guid, RegenDescriptor>();
        var tempName2Guid = new Dictionary<string, Guid>();
        var tempGuid2Limb = new Dictionary<Guid, LimbBehaviour>();
        var tempAnchorOn = new Dictionary<LimbBehaviour, LimbBehaviour>();
        var tempAnchorBy = new Dictionary<LimbBehaviour, List<LimbBehaviour>>();

        foreach (LimbBehaviour lb in prefabPerson.Limbs)
        {
            Guid g = Guid.NewGuid();
            tempName2Guid[lb.name] = g;
            tempGuid2Limb[g] = lb;
            tempAnchorBy[lb] = new List<LimbBehaviour>();
            tempAnchorOn[lb] = null;
        }

        foreach (LimbBehaviour lb in prefabPerson.Limbs)
        {
            if (lb.Joint && lb.Joint.connectedBody)
            {
                LimbBehaviour connectedLimb = lb.Joint.connectedBody.gameObject.GetComponent<LimbBehaviour>();
                if (connectedLimb != null && tempGuid2Limb.ContainsKey(tempName2Guid[connectedLimb.name]))
                {
                    tempAnchorOn[lb] = connectedLimb;
                    tempAnchorBy[connectedLimb].Add(lb);
                }
            }
        }

        bool Flipped = false;
        if (prefabInstance.TryGetComponent<HingeJointLimitAutofixBehaviour>(out var fixer) && fixer.IsFlipped)
            Flipped = true;

        foreach (LimbBehaviour lb in prefabPerson.Limbs)
        {
            Guid g = tempName2Guid[lb.name];
            var desc = new RegenDescriptor
            {
                Self = g,
                HasJoint = lb.Joint,
                IsHead = lb.HasBrain,
                Name = lb.name,
                Scale = lb.transform.localScale,
                TrueInitialMass = lb.PhysicalBehaviour.TrueInitialMass,
                CirculationSource = lb.CirculationBehaviour.Source
                    ? tempName2Guid[lb.CirculationBehaviour.Source.GetComponent<LimbBehaviour>().name]
                    : Guid.Empty,
                NearestLimbToBrain = lb.NearestLimbToBrain
                    ? tempName2Guid[lb.NearestLimbToBrain.GetComponent<LimbBehaviour>().name]
                    : Guid.Empty,
                AttachBy = tempAnchorBy[lb].Select(l => tempName2Guid[l.name]).ToArray()
            };

            if (lb.Joint && tempAnchorOn[lb] != null)
            {
                desc.JointDescriptor = new LimbJointInfo(lb.Joint, lb);
                desc.AttachTo = tempName2Guid[tempAnchorOn[lb].name];
                if (Flipped) desc.JointDescriptor.Reverse();
            }
            tempDescriptors[g] = desc;
        }

        foreach (string name in tempName2Guid.Keys)
        {
            if (Name2Guid.ContainsKey(name))
            {
                if (Guid2Regen.ContainsKey(Name2Guid[name]))
                    Guid2Regen[Name2Guid[name]].guid = tempName2Guid[name];
                Name2Guid[name] = tempName2Guid[name];
            }
        }

        Descriptors.Clear();
        foreach (var kv in tempDescriptors)
            Descriptors.Add(kv.Key, kv.Value.Copy());

        Poses.Clear();
        foreach (RagdollPose pose in prefabPerson.Poses)
        {
            if (Poses.ContainsKey(pose.State)) continue;
            var angles = new Dictionary<Guid, RagdollPose.LimbPose>();
            foreach (var limbPose in pose.Angles)
            {
                string limbName = limbPose.Limb.name;
                if (tempName2Guid.TryGetValue(limbName, out var poseGuid))
                    angles[poseGuid] = limbPose;
            }
            Poses.Add(pose.State, new RagdollPoseRecord { State = pose.State, Angles = angles });
        }

        Guid2Regen.Clear();
        foreach (LimbRegenData d in Entries)
        {
            Guid2Regen.Add(d.guid, d);
            _byLimb[d.limb] = d;
        }

        foreach (GameObject go in Duplicates.Values)
            UnityEngine.Object.Destroy(go);
        Duplicates.Clear();

        foreach (LimbBehaviour lb in prefabPerson.Limbs)
        {
            Guid guid = tempName2Guid[lb.name];
            if (!Name2Guid.ContainsKey(lb.name)) continue;
            Guid realGuid = Name2Guid[lb.name];
            if (!Guid2Regen.ContainsKey(realGuid)) continue;

            Transform parent = Person.transform.Find(Utils.GetHierachyPath(lb.transform.parent));
            if (parent == null) parent = Person.transform;

            GameObject dupeGO = UnityEngine.Object.Instantiate(lb.gameObject, parent);
            LimbBehaviour dupeLimb = dupeGO.GetComponent<LimbBehaviour>();

            dupeLimb.Person = Person;
            dupeLimb.PhysicalBehaviour.SpawnSpawnParticles = false;
            dupeLimb.CirculationBehaviour.PushesTo = new CirculationBehaviour[0];
            dupeLimb.ConnectedLimbs = new List<LimbBehaviour>();

            foreach (Joint2D joint in dupeGO.GetComponentsInChildren<Joint2D>())
                UnityEngine.Object.Destroy(joint);
            foreach (WireBehaviour wire in dupeGO.GetComponentsInChildren<WireBehaviour>())
                UnityEngine.Object.Destroy(wire);
            if (dupeGO.TryGetComponent<GoreStringBehaviour>(out var gs))
                gs.DestroyJoint();
            UnityEngine.Object.Destroy(s_MyStatus_Get(dupeLimb));

            dupeLimb.SkinMaterialHandler.ClearAllDamage();
            dupeLimb.Health = dupeLimb.InitialHealth;
            dupeLimb.CirculationBehaviour.IsPump = dupeLimb.CirculationBehaviour.WasInitiallyPumping;
            dupeLimb.CirculationBehaviour.BloodFlow = 1f;
            dupeLimb.CirculationBehaviour.HealBleeding();
            dupeLimb.CirculationBehaviour.ClearLiquid();
            dupeLimb.CirculationBehaviour.AddLiquid(Liquid.GetLiquid(dupeLimb.BloodLiquidType), 1f);

            dupeLimb.SkinMaterialHandler.renderer.material =
                Guid2Regen[realGuid].limb.SkinMaterialHandler.renderer.material;
            s_Mat(dupeLimb.SkinMaterialHandler, dupeLimb.SkinMaterialHandler.renderer.material);

            dupeGO.name = lb.name;

            dupeGO.AddComponent<Optout>();
            dupeGO.SetActive(false);

            Duplicates[realGuid] = dupeGO;
        }

        foreach (LimbRegenData d in Entries)
        {
            d.Descriptor.Scale = d.limb.transform.localScale;
        }

        RefreshLimbsConnection();
    }

    public void RebuildLimbAfterExternalDestroy(Guid guid, bool restoreSource)
    {
        if (guid == Guid.Empty) return;
        if (!Duplicates.ContainsKey(guid)) return;
        InstantiateLimbInstance(guid);
        if (restoreSource) SetSource(guid);
    }

    public void DisconnectDetached()
    {
        LimbRegenData[] all = Entries.ToArray();
        foreach (LimbRegenData d in all)
        {
            if (!d.IsConnectedToSource && !d.RegenAble)
                Disconnect(d);
        }
    }

    public void Teardown()
    {
        if (_disposed) return;
        _disposed = true;
        _personDestroyed = true;

        regenEnable = false;

        foreach (var d in Entries)
        {
            if (d?.BornCoroutine != null)
                RegenManager.Instance.StopManagedCoroutine(d.BornCoroutine);
        }

        foreach (var d in Entries)
            d?.UnsubscribeDisintegration();

        foreach (var d in Entries)
        {
            if (d?.limb)
            {
                RegenManager._byLimb.Remove(d.limb);
                _byLimb.Remove(d.limb);
            }
        }

        Entries.Clear();
        Guid2Regen.Clear();
        Name2Guid.Clear();
        Descriptors.Clear();
        Poses.Clear();
        _byLimb.Clear();

        foreach (var go in Duplicates.Values)
        {
            if (go)
                UnityEngine.Object.Destroy(go);
        }
        Duplicates.Clear();

        if (FakeDetachPerson)
        {
            for (int i = FakeDetachPerson.transform.childCount - 1; i >= 0; i--)
            {
                var lb = FakeDetachPerson.transform.GetChild(i).GetComponent<LimbBehaviour>();
                if (lb && lb.PhysicalBehaviour && !lb.PhysicalBehaviour.isDisintegrated)
                {
                    LimbStatEntry._isRegenSystemCrush = true;
                    try { lb.PhysicalBehaviour.Disintegrate(); }
                    finally { LimbStatEntry._isRegenSystemCrush = false; }
                }
            }
            UnityEngine.Object.Destroy(FakeDetachPerson.gameObject);
            FakeDetachPerson = null;
        }

        if (LinkedStatData != null)
        {
            LimbStatManager.Instance?.Unregister(Person);
            LinkedStatData = null;
        }

        if (Person)
        {
            Person.Limbs = Array.Empty<LimbBehaviour>();
            var si = Person.gameObject.GetComponent<SerialiseInstructions>();
            if (si)
                si.RelevantTransforms = Array.Empty<Transform>();
        }

        UnityEngine.Object.Destroy(Person.gameObject);
    }

    public void DestroyLimb(LimbBehaviour limb)
    {
        if (!limb || !limb.PhysicalBehaviour) return;
        LimbStatEntry._isRegenSystemCrush = true;
        try { limb.Crush(); }
        finally { LimbStatEntry._isRegenSystemCrush = false; }
    }

    public void PruneDeadRegens()
    {
        for (int i = Entries.Count - 1; i >= 0; i--)
        {
            if (Entries[i] == null)
                Entries.RemoveAt(i);
        }

        List<Guid> removeKeys = null;
        foreach (var pair in Guid2Regen)
        {
            if (pair.Value != null) continue;
            removeKeys ??= new List<Guid>();
            removeKeys.Add(pair.Key);
        }
        if (removeKeys != null)
        {
            foreach (Guid key in removeKeys)
                Guid2Regen.Remove(key);
        }
    }

    public void HandleDestroyedLimb(LimbRegenData data)
    {
        if (_disposed || data == null || data.guid == Guid.Empty) return;

        if (data.anchorOn != null)
            data.anchorOn.AnchorBy.Remove(data);

        foreach (LimbRegenData child in data.AnchorBy.ToArray())
        {
            if (child == null) continue;
            if (child.anchorOn == data)
            {
                if (SeveredLimbHandling != SeveredLimbHandling.PhysicalReattach)
                    child.anchorOn = null;
            }
            if (!child.RegenAble)
                child.IsConnectedToSource = false;
        }

        if (data.limb)
        {
            _lastLimbPosition = data.limb.transform.position;
            _hasLastLimbPosition = true;
        }

        data.AnchorBy.Clear();
        data.Disconnected = true;
        if (SeveredLimbHandling != SeveredLimbHandling.PhysicalReattach)
            _destroyedTimestamps[data.guid] = Time.time;
        Entries.Remove(data);

        if (Guid2Regen.TryGetValue(data.guid, out LimbRegenData mapped) && mapped == data)
            Guid2Regen.Remove(data.guid);

        if (data.limb)
            _byLimb.Remove(data.limb);

        ApplyWhole();
    }

    public void RecordPose()
    {
        Poses.Clear();
        foreach (RagdollPose pose in Person.Poses)
        {
            if (Poses.ContainsKey(pose.State)) continue;
            var angles = new Dictionary<Guid, RagdollPose.LimbPose>();
            foreach (var limbPose in pose.Angles)
            {
                LimbRegenData d = GetDataForLimb(limbPose.Limb);
                if (d != null)
                    angles[d.guid] = limbPose;
            }
            Poses.Add(pose.State, new RagdollPoseRecord { State = pose.State, Angles = angles });
        }
    }

    public void SeperateWholeAsPerson() { }

    public void CreateDuplicatePrefab()
    {
        foreach (LimbRegenData data in Entries)
        {
            GameObject dupeGO = UnityEngine.Object.Instantiate(data.limb.gameObject);
            dupeGO.name = data.limb.name + "_Dupe";
            LimbBehaviour dupeLimb = dupeGO.GetComponent<LimbBehaviour>();
            dupeLimb.PhysicalBehaviour.SpawnSpawnParticles = false;
            dupeLimb.CirculationBehaviour.PushesTo = new CirculationBehaviour[0];

            foreach (Joint2D joint in dupeGO.GetComponentsInChildren<Joint2D>())
                UnityEngine.Object.Destroy(joint);
            foreach (WireBehaviour wire in dupeGO.GetComponentsInChildren<WireBehaviour>())
                UnityEngine.Object.Destroy(wire);
            if (dupeGO.TryGetComponent<GoreStringBehaviour>(out var gs))
                gs.DestroyJoint();

            UnityEngine.Object.Destroy(s_MyStatus_Get(dupeLimb));

            dupeLimb.SkinMaterialHandler.ClearAllDamage();
            dupeLimb.Health = dupeLimb.InitialHealth;
            dupeLimb.CirculationBehaviour.IsPump = dupeLimb.CirculationBehaviour.WasInitiallyPumping;
            dupeLimb.CirculationBehaviour.BloodFlow = 1f;
            dupeLimb.CirculationBehaviour.HealBleeding();
            dupeLimb.CirculationBehaviour.ClearLiquid();
            dupeLimb.CirculationBehaviour.AddLiquid(Liquid.GetLiquid(dupeLimb.BloodLiquidType), 1f);

            dupeGO.transform.SetParent(data.limb.transform.parent);
            dupeGO.AddComponent<Optout>();
            dupeGO.SetActive(false);
            CopyActionTrigger(dupeGO, data.limb.gameObject);
            Duplicates[data.guid] = dupeGO;
        }
    }

    public void RefreshLimbsConnection()
    {
        foreach (LimbRegenData d in Entries)
        {
            d.AnchorBy.Clear();
            d.anchorOn = null;
        }
        foreach (LimbRegenData d in Entries)
        {
            d.SpreadConnection();
        }
    }

    public void InitDescriptors()
    {
        bool Flipped = false;
        if (Person.gameObject.TryGetComponent<HingeJointLimitAutofixBehaviour>(out var fixer) && fixer.IsFlipped)
            Flipped = true;

        Descriptors.Clear();
        foreach (LimbRegenData d in Entries)
        {
            var descriptor = new RegenDescriptor
            {
                Self = d.guid,
                HasJoint = d.limb.Joint,
                IsHead = d.limb.HasBrain,
                Name = d.limb.name,
            };

            if (d.limb.Joint)
            {
                descriptor.JointDescriptor = new LimbJointInfo(d.limb.Joint, d.limb);
                descriptor.AttachTo = d.anchorOn.guid;
                if (Flipped) descriptor.JointDescriptor.Reverse();
            }
            descriptor.AttachBy = d.AnchorBy.Select(d2 => d2.guid).ToArray();
            descriptor.Scale = d.limb.transform.localScale;
            descriptor.TrueInitialMass = d.limb.PhysicalBehaviour.TrueInitialMass;
            descriptor.CirculationSource = d.limb.CirculationBehaviour.Source
                ? GetDataForLimb(d.limb.CirculationBehaviour.Source.GetComponent<LimbBehaviour>())?.guid ?? Guid.Empty
                : Guid.Empty;
            descriptor.NearestLimbToBrain = d.limb.NearestLimbToBrain
                ? GetDataForLimb(d.limb.NearestLimbToBrain.GetComponent<LimbBehaviour>())?.guid ?? Guid.Empty
                : Guid.Empty;

            Descriptors.Add(d.guid, descriptor);
        }
    }

    public void InitDetached()
    {
        FakeDetachPerson = UnityEngine.Object.Instantiate(ModAPI.FindSpawnable("Human").Prefab.GetComponent<PersonBehaviour>());
        FakeDetachPerson.gameObject.name = "Detached Limbs";
        FakeDetachPerson.RandomisedSize = false;
        if (FakeDetachPerson.TryGetComponent(out DisintegrationCounterBehaviour cnter))
            UnityEngine.Object.Destroy(cnter);
        foreach (LimbBehaviour limb in FakeDetachPerson.Limbs)
            limb.PhysicalBehaviour.Disintegrate();
        // Clear the Limbs array so AttachedLimbLoopChecks has nothing to iterate,
        // and disable the component so Update() is never called by Unity.
        FakeDetachPerson.Limbs = new LimbBehaviour[0];
        FakeDetachPerson.enabled = false;
    }

    private static readonly Action<object, UserSpawnEventArgs> s_InvokeItemSpawned =
        Access.CreateStaticDelegate<Action<object, UserSpawnEventArgs>>(
            typeof(ModAPI), "InvokeItemSpawned");

    public LimbRegenData InstantiateLimbInstance(Guid guid)
    {
        if (_disposed || _personDestroyed || !PersonAlive)
            return null;
        PruneDeadRegens();

        if (Guid2Regen.TryGetValue(guid, out LimbRegenData existing))
        {
            if (existing != null && existing.limb && existing.limb.PhysicalBehaviour && !existing.limb.PhysicalBehaviour.isDisintegrated)
                return existing;

            if (existing != null && existing.limb != null)
            {
                if (!existing.Disconnected)
                    Disconnect(existing);
                if (FakeDetachPerson != null)
                    existing.limb.transform.SetParent(FakeDetachPerson.transform);
            }

            HandleDestroyedLimb(existing);
        }

        if (!Duplicates.ContainsKey(guid))
            return null;

        if (severedDelay > 0f && _destroyedTimestamps.TryGetValue(guid, out float ts)
            && Time.time - ts < severedDelay)
            return null;
        _destroyedTimestamps.Remove(guid);

        Debug.Log($"Instantiate {guid} named {Descriptors[guid].Name}");

        GameObject template = Duplicates[guid];
        GameObject newGO = UnityEngine.Object.Instantiate(template, template.transform.parent);
        if (Descriptors.TryGetValue(guid, out var desc) && desc.AttachTo != Guid.Empty && Guid2Regen.TryGetValue(desc.AttachTo, out var parentAnchor) && parentAnchor.limb != null)
        {
            newGO.transform.position = parentAnchor.limb.transform.position;
        }
        else if (_hasLastLimbPosition)
        {
            newGO.transform.position = _lastLimbPosition;
        }
        CopyActionTrigger(newGO, template);
        newGO.name = Descriptors[guid].Name;
        newGO.SetActive(true);

        if (CachedSpawnableAsset)
            s_InvokeItemSpawned(CatalogBehaviour.Main, new UserSpawnEventArgs(newGO, CachedSpawnableAsset));

        LimbBehaviour newLimb = newGO.GetComponent<LimbBehaviour>();
        if (newGO.TryGetComponent<Optout>(out var opt))
            UnityEngine.Object.Destroy(opt);

        LimbRegenData newData = new LimbRegenData
        {
            limb = newLimb,
            guid = guid,
            parent = this,
            RecoverOnDestroy = true
        };
        newData.Initialize();

        ReuseLimbStatus(newLimb);

        newData.SubscribeDisintegration();

        foreach (AudioSource audio in newGO.GetComponentsInChildren<AudioSource>())
            Global.main.AddAudioSource(audio, true);

        newGO.transform.localScale = newData.Descriptor.Scale;
        newLimb.PhysicalBehaviour.RecalculateMassBasedOnSize();
        newData.Reset();
        DisablepublicCollision(newGO.GetComponent<Collider2D>());

        bool shouldSeek = (SeveredLimbHandling == SeveredLimbHandling.PhysicalReattach)
                          && newData.HasJoint
                          && newData.Descriptor.AttachTo != Guid.Empty;

        if (shouldSeek)
        {
            newData.IsSeekingReattachment = true;
            newData.IsConnectedToSource = false;
            newData.seekingEndTime = Time.time;
        }
        PendingSeeking.Remove(guid);

        Connect(newData);

        // Fix up seeking limbs that were anchored to the old (now destroyed)
        // instance of this guid — point them to the newly regenerated limb so
        // PhysicalAttractStep can pull them into place.
        foreach (LimbRegenData d in Entries)
        {
            if (d.anchorOn != null && d.anchorOn.guid == guid && d.anchorOn != newData)
            {
                d.anchorOn = newData;
            }
        }

        NotifyOnRegen(newData);
        return newData;
    }

    void ReuseLimbStatus(LimbBehaviour newLimb)
    {
        if (!FakeDetachPerson) return;

        string limbName = newLimb.name;
        LimbBehaviour oldLimb = null;

        for (int i = FakeDetachPerson.transform.childCount - 1; i >= 0; i--)
        {
            var child = FakeDetachPerson.transform.GetChild(i);
            if (child.name == limbName)
            {
                oldLimb = child.GetComponent<LimbBehaviour>();
                if (oldLimb) break;
            }
        }

        if (!oldLimb) return;

        var oldStatus = s_MyStatus_Get(oldLimb);
        if (!oldStatus) { DestroyOldLimb(oldLimb); return; }

        var statusBehaviour = oldStatus.GetComponent<LimbStatusBehaviour>();
        if (!statusBehaviour) { DestroyOldLimb(oldLimb); return; }

        var newStatus = s_MyStatus_Get(newLimb);

        statusBehaviour.limb = newLimb;

        if (statusBehaviour.ParentConstraint && statusBehaviour.ParentConstraint.sourceCount > 0)
        {
            statusBehaviour.ParentConstraint.RemoveSource(0);
            statusBehaviour.ParentConstraint.AddSource(new UnityEngine.Animations.ConstraintSource
            {
                sourceTransform = newLimb.transform,
                weight = 1f
            });
        }

        s_MyStatus_Set(oldLimb, null);
        s_MyStatus_Set(newLimb, oldStatus);

        if (!newLimb.PhysicalBehaviour.isDisintegrated)
            oldStatus.SetActive(Global.main.ShowLimbStatus);

        if (newStatus)
            UnityEngine.Object.Destroy(newStatus);

        DestroyOldLimb(oldLimb);
    }

    void DestroyOldLimb(LimbBehaviour oldLimb)
    {
        if (!oldLimb) return;
        var leftoverStatus = s_MyStatus_Get(oldLimb);
        if (leftoverStatus)
        {
            s_MyStatus_Set(oldLimb, null);
            UnityEngine.Object.Destroy(leftoverStatus);
        }
        if (oldLimb.gameObject)
        {
            // 销毁前先禁用物理，防止下一帧物理结算出现幽灵碰撞
            if (oldLimb.PhysicalBehaviour)
                oldLimb.PhysicalBehaviour.enabled = false;

            UnityEngine.Object.Destroy(oldLimb.gameObject);
        }
    }

    public void CopyActionTrigger(GameObject dest, GameObject src)
    {
        UseEventTrigger[] srcTriggers = src.GetComponents<UseEventTrigger>();
        UseEventTrigger[] destTriggers = dest.GetComponents<UseEventTrigger>();
        for (int i = 0; i < destTriggers.Length; i++)
            destTriggers[i].Action = srcTriggers[i].Action;
    }

    public void Reposition(Vector3 position, RagdollPose pose, float angle = 0f)
    {
        float direction = Direction;
        var source = Source;
        if (source == null) return;

        source.limb.PhysicalBehaviour.rigidbody.position = position;
        source.limb.transform.rotation = Quaternion.AngleAxis(angle * direction, Vector3.forward);
        source.limb.PhysicalBehaviour.rigidbody.rotation = angle * direction;

        pose.ConstructDictionary();

        List<LimbRegenData> limbs1 = new(source.AnchorBy);
        for (int i = 0; i < limbs1.Count; i++)
        {
            LimbRegenData regen = limbs1[i];
            float poseAngle = 0f;
            if (pose.AngleDictionary.ContainsKey(regen.limb))
                poseAngle = pose.AngleDictionary[regen.limb].EvaluateAngleAt(0f);

            if (regen.FlowToAnchor)
                regen.Reposition(-poseAngle * direction);
            else
                regen.InverseReposition(-poseAngle * direction);

            for (int ni = 0; ni < regen.AnchorBy.Count; ni++)
                limbs1.Add(regen.AnchorBy[ni]);
            if (regen.anchorOn != null)
                limbs1.Add(regen.anchorOn);
            limbs1.Remove(regen.NearestLimbToSource);
            limbs1.Remove(regen);
            i--;
        }
    }

    public void Connect(LimbRegenData data)
    {
        data.limb.Person = Person;
        data.Disconnected = false;

        if (data.guid == RootGuid || (Descriptors.ContainsKey(data.guid) && Descriptors[data.guid].IsHead))
        {
            data.limb.NodeBehaviour.IsRoot = true;
        }

        foreach (RagdollPose pose in Person.Poses)
        {
            if (!Poses.TryGetValue(pose.State, out var record)) continue;
            if (!record.Angles.ContainsKey(data.guid)) continue;

            RagdollPose.LimbPose limbPose = record.Angles[data.guid];
            limbPose.Limb = data.limb;
            pose.Angles.Add(limbPose);
            pose.AngleDictionary.Add(data.limb, limbPose);
        }

        data.limb.IsActiveInCurrentPose = Person.ActivePose != null && Person.ActivePose.AngleDictionary != null
            && Person.ActivePose.AngleDictionary.ContainsKey(data.limb);

        Guid2Regen.Add(data.guid, data);
        Entries.Add(data);
        _byLimb[data.limb] = data;
        RegenManager._byLimb[data.limb] = data;
        ApplyWhole();

        foreach (Guid neighborGuid in Descriptors[data.guid].Neighbors)
        {
            if (Guid2Regen.ContainsKey(neighborGuid))
                Guid2Regen[neighborGuid].ApplyDescriptor();
        }
        data.ApplyDescriptor();

        if (Descriptors[data.guid].IsHead)
            s_Head(Person, data.limb);

        if (LinkedStatData != null)
            RegisterRegeneratedLimb(data);
    }

    void RegisterRegeneratedLimb(LimbRegenData regenData)
    {
        if (LinkedStatData == null) return;

        var lb = regenData.limb;
        if (!lb || LinkedStatData.Entries.ContainsKey(lb)) return;

        var entry = new LimbStatEntry
        {
            limb = lb,
            circ = lb.CirculationBehaviour
        };
        entry.Parent = LinkedStatData;
        entry.LinkToPerson(LinkedStatData);
        entry.SyncPhysicalProps();

        LinkedStatData.Entries[lb] = entry;
        LimbStatManager.Instance.RegisterLimbEntry(lb, entry);

        if (LinkedStatData.RegenPunchNames != null
            && LinkedStatData.RegenPunchNames.Contains(lb.name))
            entry.CanPunch.BaseValue = true;

        LinkedStatData.RefreshCache();
    }

    public void Disconnect(LimbRegenData data)
    {
        if (_disposed || data == null || data.Disconnected) return;
        Debug.Log($"{data.limb.name} Disconnected");

        if (SeveredLimbHandling == SeveredLimbHandling.PhysicalReattach)
        {
            if (data.RegenAble && !data.Hidding && data.limb && data.limb.PhysicalBehaviour
                && !data.limb.PhysicalBehaviour.isDisintegrated)
            {
                data.IsSeekingReattachment = true;
                data.IsConnectedToSource = false;
                data.InvalidateNeighborCache();
                data.ApplyNeighbors();
                return;
            }
            PendingSeeking.Add(data.guid);
        }

        // Record the last known position of any departing limb so
        // full-body regeneration can place the root at the right spot.
        if (data.limb)
        {
            _lastLimbPosition = data.limb.transform.position;
            _hasLastLimbPosition = true;
        }

        var status = s_MyStatus_Get(data.limb);
        if (status)
            status.SetActive(false);

        if (!FakeDetachPerson)
            InitDetached();

        foreach (RagdollPose pose in Person.Poses)
        {
            if (pose.AngleDictionary == null) pose.ConstructDictionary();
            if (!pose.AngleDictionary.ContainsKey(data.limb)) continue;
            pose.Angles.Remove(pose.AngleDictionary[data.limb]);
            pose.AngleDictionary.Remove(data.limb);
        }

        if (data.limb.gameObject.TryGetComponent<GoreStringBehaviour>(out var GoreString))
            GoreString.DestroyJoint();

        foreach (Joint2D j in data.limb.gameObject.GetComponents<Joint2D>())
        {
            if (j != null) UnityEngine.Object.Destroy(j);
        }

        Rigidbody2D rb = data.limb.PhysicalBehaviour.rigidbody;
        Vector2 velocity = rb.velocity;
        Vector2 position = rb.position;
        data.limb.transform.SetParent(FakeDetachPerson.transform);
        rb.MovePosition(position);
        rb.velocity = velocity;
        data.limb.Person = FakeDetachPerson;
        data.limb.BaseStrength = 0f;
        data.limb.NodeBehaviour.IsRoot = false;
        data.limb.IsActiveInCurrentPose = false;
        data.limb.Health = 0f;
        data.limb.CirculationBehaviour.Source = null;
        data.limb.CirculationBehaviour.PushesTo = new CirculationBehaviour[0];

        Entries.Remove(data);
        Guid2Regen.Remove(data.guid);
        _byLimb.Remove(data.limb);
        RegenManager._byLimb.Remove(data.limb);
        ApplyWhole();
        data.Disconnected = true;

        if (LinkedStatData != null && LinkedStatData.Entries.ContainsKey(data.limb))
        {
            LinkedStatData.Entries.Remove(data.limb);
            LinkedStatData.RefreshCache();
        }
        if (LimbStatManager.Instance)
            LimbStatManager.Instance.UnregisterLimbEntry(data.limb);

        if (data.limb)
            data.limb.enabled = false;

        switch (SeveredLimbHandling)
        {
            case SeveredLimbHandling.Crush:
            default:
                data.PrepareForPermanentDestroy();
                // When the Person root is already destroyed, skip Crush to
                // avoid orphaned particles — the limb is going down with the
                // rest of the body and there is nothing to reattach to.
                if (!_disposed && !_personDestroyed && PersonAlive)
                    DestroyLimb(data.limb);
                if (data.BornCoroutine != null)
                    RegenManager.Instance.StopManagedCoroutine(data.BornCoroutine);
                return;

            case SeveredLimbHandling.PhysicalReattach:
                data.PrepareForPermanentDestroy();
                if (!_disposed && !_personDestroyed && PersonAlive)
                    DestroyLimb(data.limb);
                if (data.BornCoroutine != null)
                    RegenManager.Instance.StopManagedCoroutine(data.BornCoroutine);
                return;
        }
    }

    public void DebugDictionary()
    {
        foreach (var kv in Name2Guid)
            Debug.Log($"{kv.Key}: {kv.Value}");
        foreach (var kv in Guid2Regen)
            Debug.Log($"{kv.Key}:{kv.Value}");
        foreach (var kv in Descriptors)
            Debug.Log($"{kv.Key}");
    }

    public void SetSource(string sourceName)
    {
        if (!Name2Guid.ContainsKey(sourceName))
        {
            Debug.Log("Invalid name for source set!");
            return;
        }
        SetSource(Name2Guid[sourceName]);
    }

    public void SetSource(Guid sourceGuid)
    {
        if (_disposed) return;
        if (!Descriptors.ContainsKey(sourceGuid))
        {
            Debug.Log("Invalid guid for source set!");
            return;
        }
        if (!Guid2Regen.ContainsKey(sourceGuid))
        {
            Debug.Log("Source regen uninstantiated!");
            return;
        }

        foreach (RegenDescriptor descriptor in Descriptors.Values)
        {
            descriptor.IsSource = false;
            descriptor.NearestLimbToSource = Guid.Empty;
        }
        Descriptors[sourceGuid].NearestLimbToSource = Guid.Empty;
        Descriptors[sourceGuid].FlowToAnchor = false;
        Descriptors[sourceGuid].IsSource = true;
        SourceGuid = sourceGuid;

        int max = Descriptors.Count;
        var queue = new Queue<Guid>(max);
        var visited = new HashSet<Guid>();
        queue.Enqueue(sourceGuid);
        visited.Add(sourceGuid);

        while (queue.Count > 0)
        {
            Guid g = queue.Dequeue();
            foreach (Guid neighborGuid in Descriptors[g].Neighbors)
            {
                if (!visited.Add(neighborGuid)) continue;
                Descriptors[neighborGuid].NearestLimbToSource = g;
                Descriptors[neighborGuid].FlowToAnchor = g == Descriptors[neighborGuid].AttachTo;
                Descriptors[neighborGuid].IsSource = false;
                queue.Enqueue(neighborGuid);
            }
        }

        foreach (LimbRegenData d in Entries)
        {
            d.ApplyDescriptorForSourceConnection();
            d.IsConnectedToSource = false;
        }
        foreach (LimbRegenData d in Guid2Regen[sourceGuid].Whole())
        {
            d.IsConnectedToSource = true;
        }
        DisconnectDetached();
    }

    public void ApplyWhole()
    {
        SerialiseInstructions instruction = Person.gameObject.GetComponent<SerialiseInstructions>();
        instruction.RelevantTransforms = Entries.Where(d => d != null).Select(d => d.limb.transform).ToArray();
        Person.Limbs = Entries.Where(d => d != null).Select(d => d.limb).ToArray();
    }

    public void DisablepublicCollision(Collider2D coll, bool ignore = true)
    {
        if (!coll) return;
        PruneDeadRegens();

        for (int i = 0; i < Entries.Count; i++)
        {
            LimbRegenData d = Entries[i];
            if (d == null) continue;
            Collider2D other = d.limb.gameObject.GetComponent<Collider2D>();
            if (!other || other == coll) continue;
            Physics2D.IgnoreCollision(coll, other, ignore);
        }

        for (int i = 0; i < DisableCollider.Count; i++)
        {
            Collider2D dc = DisableCollider[i];
            if (!dc || dc == coll) continue;
            Physics2D.IgnoreCollision(coll, dc, ignore);
        }
    }
}

public class RegenManager : MonoBehaviour
{
    private static RegenManager _instance;
    public static RegenManager Instance
    {
        get
        {
            if (_instance == null)
                _instance = CreateNew();
            return _instance;
        }
        private set => _instance = value;
    }

    private static readonly Dictionary<PersonBehaviour, PersonRegenData> _persons = new();
    public static readonly Dictionary<LimbBehaviour, LimbRegenData> _byLimb = new();
    private static readonly List<PersonRegenData> _tickList = new();
    private static readonly List<PersonBehaviour> _pendingUnregister = new();
    private static bool _ticking;
    private static readonly List<Coroutine> _managedCoroutines = new();
    private static readonly List<PersonBehaviour> _cleanupDead = new();
    private static int _cleanupTimer = 100;
    private static DynamicHarmonyManager _harmony;
    private static bool _initialized;

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
        _instance = CreateNew();
    }

    private static RegenManager CreateNew()
    {
        return new GameObject("RegenManager", typeof(RegenManager))
        {
            hideFlags = HideFlags.HideAndDontSave
        }.GetComponent<RegenManager>();
    }

    public static void Initialize()
    {
        if (_initialized) return;
        _initialized = true;

        _harmony = new DynamicHarmonyManager("com.helper.regen-manager");

        _harmony.AddPostfix("regen-joint-break",
            typeof(LimbBehaviour), "OnJointBreak2D",
            ctx =>
            {
                var limb = ctx.Instance as LimbBehaviour;
                if (!limb) return;
                var brokenJoint = ctx.Args.Length > 0 ? ctx.Args[0] as Joint2D : null;
                if (_byLimb.TryGetValue(limb, out var data))
                    data.HandleJointBreak(brokenJoint);
            });

        _harmony.AddPrefix("regen-limb-destroy",
            typeof(LimbBehaviour), "OnDestroy",
            ctx =>
            {
                var limb = ctx.Instance as LimbBehaviour;
                if (!limb) return;
                if (_byLimb.TryGetValue(limb, out var data))
                    data.HandleDestroy();
            });

        _harmony.AddPrefix("regen-person-destroy",
            typeof(PersonBehaviour), "OnDestroy",
            ctx =>
            {
                var person = ctx.Instance as PersonBehaviour;
                if (!person) return;
                if (_persons.TryGetValue(person, out var data))
                {
                    data._disposed = true;
                    data._personDestroyed = true;
                    data.regenEnable = false;
                }
            });
    }

    public PersonRegenData Register(PersonBehaviour person)
    {
        if (!person || _persons.ContainsKey(person))
            return _persons.TryGetValue(person, out var existing) ? existing : null;

        if (person.gameObject.TryGetComponent(out DisintegrationCounterBehaviour cnter))
            Destroy(cnter);

        var data = new PersonRegenData { Person = person };
        data.Setup();

        if (!data.LoadDataFromPrefab())
            data.RecordState();

        data.PostSetup();

        foreach (var d in data.Entries)
        {
            if (d != null && d.limb)
                _byLimb[d.limb] = d;
        }

        _persons[person] = data;
        _tickList.Add(data);

        RuntimeDebug.LogInfo($"[RegenManager] Registered {person.name} with {data.Entries.Count} limbs");
        return data;
    }

    public void Unregister(PersonBehaviour person)
    {
        if (!person || !_persons.TryGetValue(person, out var data))
            return;

        data.Teardown();

        if (_ticking)
        {
            _pendingUnregister.Add(person);
            return;
        }

        _persons.Remove(person);
        _tickList.Remove(data);

        RuntimeDebug.LogDebug($"[RegenManager] Unregistered {person.name}");
    }

    public bool TryGetData(LimbBehaviour limb, out LimbRegenData data)
        => _byLimb.TryGetValue(limb, out data);

    public LimbRegenData GetData(LimbBehaviour limb)
    {
        _byLimb.TryGetValue(limb, out var d);
        return d;
    }

    public Coroutine StartManagedCoroutine(IEnumerator routine)
    {
        var c = StartCoroutine(routine);
        _managedCoroutines.Add(c);
        return c;
    }

    public void StopManagedCoroutine(Coroutine coroutine)
    {
        if (coroutine != null)
        {
            StopCoroutine(coroutine);
            _managedCoroutines.Remove(coroutine);
        }
    }

    void FixedUpdate()
    {
        if (_tickList.Count == 0) return;

        _ticking = true;
        for (int i = 0; i < _tickList.Count; i++)
        {
            var data = _tickList[i];
            if (data._disposed || !data.Person) continue;
            data.TickRegeneration();
            data.TickConnections();
        }
        _ticking = false;

        if (_pendingUnregister.Count > 0)
        {
            for (int i = 0; i < _pendingUnregister.Count; i++)
            {
                var person = _pendingUnregister[i];
                if (_persons.TryGetValue(person, out var data))
                {
                    _persons.Remove(person);
                    _tickList.Remove(data);
                }
            }
            _pendingUnregister.Clear();
        }
    }

    void Update()
    {
        if (--_cleanupTimer <= 0)
        {
            _cleanupTimer = 100;
            CleanUp();
        }
    }

    void CleanUp()
    {
        _cleanupDead.Clear();
        for (int i = 0; i < _tickList.Count; i++)
        {
            var data = _tickList[i];
            if (data._disposed || !data.Person)
                _cleanupDead.Add(data.Person);
        }
        for (int i = 0; i < _cleanupDead.Count; i++)
            Unregister(_cleanupDead[i]);
        for (int i = _managedCoroutines.Count - 1; i >= 0; i--)
            if (_managedCoroutines[i] == null)
                _managedCoroutines.RemoveAt(i);
    }
}
#pragma warning restore CS0612
#pragma warning restore CS0618
#endregion
