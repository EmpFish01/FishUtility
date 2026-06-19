using ZeroReflect;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using FishUtility;

namespace FishUtility;

public class float01
{
    public float value { get => Mathf.Clamp01(m_value); set => m_value = value; }
    [SerializeField]
    private float m_value;
    public static implicit operator float01(float value) => new float01 { value = value };
    public static implicit operator float(float01 float01) => float01.value;
}

public struct EffectSetting
{
    public float Speed;
    public float StartProgress;
    public bool shouldStart;
    public LimbBehaviour[] startBody;
    public EffectSetting(float speed, float when, bool start, params LimbBehaviour[] startBody)
    {
        this.startBody = startBody;
        this.shouldStart = start;
        this.Speed = speed;
        this.StartProgress = when;
    }
}

[RequireComponent(typeof(AntiDestroy))]
[DisallowMultipleComponent]
[SkipSerialisation]
public class AntiDestroy : MonoBehaviour
{
    private void OnDisable() => enabled = true;
    private void OnDestroy() => OnDisable();
}

[RequireComponent(typeof(BeBeing), typeof(HingeJoint2D), typeof(PhysicalBehaviour))]
[SkipSerialisation]
public class BBHelper : AntiDestroy { }

[RequireComponent(typeof(BBHelper), typeof(SpriteRenderer), typeof(Collider2D))]
[SkipSerialisation]
public class BeBeing : AntiDestroy { }

[RequireComponent(typeof(PersonBehaviour), typeof(AntiDestroyPersonBeh))]
[SkipSerialisation]
public class AntiDestroyPersonBeh : MonoBehaviour { }

public class ObjectInfo
{
    public GameObject Instance { get; private set; }
    private Vector3 localSacle;
    public bool antiGravity { get; private set; } = true;
    public bool IsAndroid { get; private set; } = true;
    private Vector2 localGravity = new Vector2(0f, -9.81f);
    private PhysicalProperties Properties = ModAPI.FindPhysicalProperties("Incredible");
    private Dictionary<LimbBehaviour, BodyInfo> BodiesInfo = new Dictionary<LimbBehaviour, BodyInfo>();
    private List<Type> except = null;
    private Action<GameObject> OnIntegrate = null;
    private bool _isStanding = false, _coolEffect = false;
    private EffectSetting? Effect = null;
    private PersonBehaviour Person = null;
    private float minDistance;
    public List<LimbBehaviour> Limbs => Instance.GetLimbs();
    public ObjectInfo(GameObject Instance, bool antiGravity = true, bool IsAndroid = true, Vector2? gravity = null, EffectSetting? Effect = null, List<Type> except = null, Action<GameObject> OnIntegrate = null, bool DontFlipJoint = false)
    {
        this.Instance = Instance;
        Instance.GetOrAddComponent<AntiDestroyPersonBeh>();
        this.localSacle = Instance.transform.localScale;
        this.minDistance = 20f / localSacle.average();
        this.antiGravity = antiGravity;
        this.localGravity = gravity.HasValue ? gravity.Value : localGravity;
        this.IsAndroid = IsAndroid;
        this.Effect = Effect;
        this._coolEffect = Effect.HasValue;
        this.OnIntegrate = OnIntegrate;
        this.Person = Instance.GetPerson();
        this.Person?.RemoveFromDictionary();
        Limbs.ForEach(limb =>
        {
            limb.gameObject.GetOrAddComponent<BeBeing>();
            limb.gameObject.GetOrAddComponent<BBHelper>();
            limb.gameObject.BetterDestroy<ShatteredObjectGenerator>();
            limb.gameObject.BetterDestroy<ShatteredObjectSpriteInitialiser>();
            limb.gameObject.BetterDestroy<ConnectedNodeBehaviour>();
            limb.gameObject.BetterDestroy<GoreStringBehaviour>();
            if (limb.TryGetComponent(out HingeJoint2D Joint))
            {
                Joint.autoConfigureConnectedAnchor = false;
                limb.Joint = Joint;
                limb.HasJoint = Joint.connectedBody != null;
            }
            BodiesInfo[limb] = new BodyInfo(Instance, limb, DontFlipJoint);
            if (_coolEffect)
            {
                BodiesInfo[limb]._progress = Effect.Value.shouldStart ? 1f : 0f;
                BodiesInfo[limb].speed = Effect.Value.Speed;
                BodiesInfo[limb].Loop = !Effect.Value.shouldStart;
            }
            Utility.NextFrameCoroutine(() => BodiesInfo[limb].Attach());
            LimbBehaviourManager.Limbs.Remove(limb);
        });
        this.Properties = UnityEngine.Object.Instantiate(Limbs[0].PhysicalBehaviour.Properties);
        Properties.Softness = 0;
        Properties.Flammability = 0;
        Properties.MagneticAttractionIntensity = 0;
        Properties.BurningTemperatureThreshold = Utility.NaN;
        Properties.Burnrate = 0;
        Properties.Conducting = false;
        Properties.ShotImpact = IsAndroid ? null : new GameObject("NULL");
        this.except = except;
        if (Instance.TryGetComponent(out DisintegrationCounterBehaviour cnter))
            cnter.Destroy();
        if (_coolEffect && Effect.Value.shouldStart)
            Utility.NextFrameCoroutine(() =>
            {
                var start = Effect.Value.startBody ?? new[] { Limbs[0]?.Person?.GetHead() ?? Limbs[0] };
                Limbs.ForEach(l => l.SkinMaterialHandler.renderer.material.SetFloat("_AcidProgress", 1f));
                var visited = new List<LimbBehaviour>();
                visited.AddRange(start);
                start.ForEach(l => BodiesInfo[l].Loop = true);
                start.ForEach(x => x.ConnectedLimbs.ForEach(y => y.StartCoroutine(simpleDFS(y, () => BodiesInfo[x]._progress < Effect.Value.StartProgress))));
                IEnumerator simpleDFS(LimbBehaviour limb, Func<bool> action)
                {
                    if (visited.Contains(limb))
                        yield break;
                    visited.Add(limb);
                    yield return new WaitUntil(() => action());
                    BodiesInfo[limb].Loop = true;
                    limb.ConnectedLimbs.ForEach(x => x.StartCoroutine(simpleDFS(x, () => BodiesInfo[limb]._progress < Effect.Value.StartProgress)));
                    limb.StopAllCoroutines();
                    yield break;
                }
            });

    }

    public void Invincible()
    {
        if (!Instance.activeSelf)
            Instance.NoChildCollide();
        Instance.SetActive(true);

        Instance.transform.localScale = localSacle;

        if (Instance.TryGetComponent(out DisintegrationCounterBehaviour cnter))
            cnter.Destroy();

        if (Person != null)
        {
            Person.enabled = true;
            Person.Heartbeat = Utility.IntMax;
            Person.SetPrivate("dead", false);
            Person.Braindead = false;
            Person.BrainDamaged = false;
            Person.BrainDamagedTime = 0;
            Person.SeizureTime = 0;
            Person.ShockLevel = 0;
            Person.PainLevel = 0;
            Person.AdrenalineLevel = 1;
            Person.Consciousness = 1;
            Person.ImpactEffectShotDamageThreshold = 0;
            Person.OxygenLevel = 1;
            Person.Limbs = Limbs.ToArray();
            var Distance = ((Vector2)Limbs.GetAveragePosition()).GetDistanceFromGround(Instance.transform).distance;
            if ((Person.ActivePose.State == PoseState.Protective || Person.ActivePose.State == PoseState.Flailing || Person.ActivePose.State == PoseState.Stumbling) && Distance > minDistance)
            {
                Person.OverridePoseIndex = 0;
                _isStanding = true;
            }
            else if (_isStanding && Distance < minDistance)
            {
                Person.OverridePoseIndex = -1;
                _isStanding = false;
            }
            Utility.CheckComponent(Person.gameObject, except);
        }

        foreach (var limb in Limbs)
        {
            limb.enabled = true;
            limb.SpeciesIdentity = null;
            var bodyInfo = BodiesInfo[limb];

            if (limb.PhysicalBehaviour.isDisintegrated || !limb.gameObject.activeSelf)
            {
                limb.PhysicalBehaviour.Integrate();
                OnIntegrate(limb.gameObject);
                bodyInfo.limbStatus.Status.SetActive(Global.main.ShowLimbStatus);
                if (_coolEffect)
                {
                    BodiesInfo[limb]._progress = 1f;
                    var visited = new List<LimbBehaviour>();
                    void dfs(LimbBehaviour l, int depth = 1)
                    {
                        if (visited.Contains(l))
                            return;
                        visited.Add(l);
                        l.ConnectedLimbs.ForEach(x =>
                        {
                            if (BodiesInfo[x]._progress < 1f / Mathf.Pow(1.35f, depth))
                                BodiesInfo[x]._progress = 1f / Mathf.Pow(1.35f, depth);
                            dfs(x, depth + 1);
                        });
                    }
                    dfs(limb);
                }
                BodiesInfo.Values.Where(x => x.connectedLimb == limb).ForEach(x => Utility.NextFrameCoroutine(() => x.Attach()));
                Instance.NoChildCollide();
            }

            if (!limb.transform.parent.gameObject.activeSelf)
            {
                var Parent = limb.transform.parent;
                while (Parent != null)
                {
                    Parent.gameObject.SetActive(true);
                    Parent = Parent.parent;
                }
                limb.transform.parent.gameObject.ForEach<LimbBehaviour>(l => BodiesInfo.Values.Where(x => x.connectedLimb == l).ForEach(x => Utility.NextFrameCoroutine(() => x.Attach())));
                Instance.NoChildCollide();
            }

            if (limb.TryGetComponent(out HingeJoint2D Joint))
            {
                Joint.breakForce = Utility.Inf;
                Joint.breakTorque = Utility.Inf;
                Joint.enabled = Joint.connectedBody != null;
            }
            else Utility.NextFrameCoroutine(() => BodiesInfo[limb].Attach());


            if (limb.PhysicalBehaviour.Properties != Properties)
                limb.PhysicalBehaviour.Properties = Properties;

            limb.PhysicalBehaviour.SimulateTemperature = false;
            limb.FreezingTemperature = Utility.NaN;
            limb.DiscomfortingHeatTemperature = Utility.NaN;
            limb.BodyTemperature = Utility.NaN;
            limb.InternalTemperature = Utility.NaN;
            limb.PhysicalBehaviour.Temperature = Utility.NaN;
            limb.PhysicalBehaviour.InitialGravityScale = 1;
            limb.PhysicalBehaviour.rigidbody.gravityScale = 1;
            limb.PhysicalBehaviour.rigidbody.drag = 0.05f;
            limb.PhysicalBehaviour.rigidbody.angularDrag = 0.05f;
            limb.PhysicalBehaviour.Disintegratable = false;
            limb.PhysicalBehaviour.Deletable = false;
            limb.PhysicalBehaviour.Resizable = false;
            limb.PhysicalBehaviour.Selectable = true;
            limb.PhysicalBehaviour.BurnIntensity = 0;
            limb.PhysicalBehaviour.Extinguish();
            limb.PhysicalBehaviour.ChargeBurns = false;
            limb.PhysicalBehaviour.ForceNoCharge = true;
            limb.PhysicalBehaviour.ForceNoChargeParticles = true;
            limb.PhysicalBehaviour.EnergyWireResistance = Utility.Inf;
            limb.CirculationBehaviour.BleedingRate = 0;
            limb.CirculationBehaviour.InternalBleedingIntensity = 0;
            limb.PhysicalBehaviour.ReflectsLasers = true;
            limb.PhysicalBehaviour.BulletPenetration = false;
            limb.PhysicalBehaviour.ConductOverride = false;

            limb.SkinMaterialHandler.intensityMultiplier = 0;
            limb.SkinMaterialHandler.enabled = true;
            limb.SkinMaterialHandler.ShouldRot = false;
            limb.PhysicalBehaviour.BurnProgress = 0;
            limb.SkinMaterialHandler.renderer.material.SetFloat("_AcidProgress", BodiesInfo[limb]._progress);
            limb.SkinMaterialHandler.renderer.material.SetFloat("_RottenProgress", 0);
            for (int i = 0; i < limb.SkinMaterialHandler.damagePoints.Length; i++)
                limb.SkinMaterialHandler.damagePoints[i] = DamagePoint.None;
            limb.SkinMaterialHandler.currentDamagePointCount = 0;
            limb.SkinMaterialHandler.ClearAllDamage();
            limb.SkinMaterialHandler.Sync();

            limb.CirculationBehaviour.HealBleeding();
            limb.CirculationBehaviour.AddLiquid(limb.GetOriginalBloodType(), Utility.Inf);
            limb.CirculationBehaviour.ForceSetAllLiquid(Utility.Inf);
            limb.CirculationBehaviour.ImmuneToDamage = true;
            limb.CirculationBehaviour.IsDisconnected = false;
            limb.CirculationBehaviour.GunshotWoundCount = 0;
            limb.CirculationBehaviour.StabWoundCount = 0;
            limb.CirculationBehaviour.IsPump = limb.CirculationBehaviour.WasInitiallyPumping;
            limb.CirculationBehaviour.BloodFlow = Utility.Inf;
            limb.CirculationBehaviour.InternalBleedingIntensity = -Utility.Inf;
            limb.CirculationBehaviour.ArtificialHeartbeat = Utility.Inf;

            limb.Collider.enabled = !_coolEffect || BodiesInfo[limb]._progress < 1f;

            limb.FakeUprightForce = limb.RoughClassification != LimbBehaviour.BodyPart.Arms ? 0.002f : 0.001f;
            limb.RegenerationSpeed = Utility.IntMax;
            limb.InitialHealth = Utility.FloatMax;
            limb.Health = Utility.Inf;
            limb.BreakingThreshold = Utility.Inf;
            limb.IsDismembered = false;
            limb.ImmuneToDamage = true;
            limb.VitalParts = new Bounds[] { };
            limb.SetNode(true);
            limb.ShotDamageMultiplier = 0;
            limb.ImpactDamageMultiplier = 0;
            limb.DoStumble = false;
            limb.BloodDecal = null;
            limb.LungsPunctured = false;
            limb.IsLethalToBreak = false;
            limb.NodeBehaviour.IsRoot = true;
            limb.Numbness = 0;
            limb.HasBrain = false;
            limb.HasLungs = false;
            limb.IsAndroid = IsAndroid;
            limb.IsZombie = false;
            limb.Frozen = false;
            limb.Vitality = 0f;
            limb.BruiseCount = 0;
        }

        HealBone();

        BodiesInfo.Values.ForEach(x => x.Locked());

        Instance.ForEach<Rigidbody2D>(rigidbody =>
        {
            if (rigidbody.bodyType != RigidbodyType2D.Dynamic && !rigidbody.GetComponent<FreezeBehaviour>() && !rigidbody.GetComponent<Optout>())
                rigidbody.bodyType = RigidbodyType2D.Dynamic;
        });

        Utility.CheckComponent(Instance, except);
    }

    public void AcidRegen()
    {
        BodiesInfo.ForEach(d =>
        {
            var info = d.Value;
            info._progress = info.Loop ? Mathf.SmoothStep(info._progress, 0f, info.speed) : info._progress;
        });
    }

    public void FakeInvincible()
    {
        if (Instance.TryGetComponent<PersonBehaviour>(out var Person))
            Person.SetPrivate("dead", false);
        BodiesInfo.ForEach(status =>
        {
            var data = status.Value.limbStatus;
            data.Status.SetActive(Global.main.ShowLimbStatus);
            data.Status.transform.localScale = Vector3.Scale(status.Key.transform.localScale, localSacle);
            var Beh = data.Beh;
            Beh.enabled = false;
            if (Beh.SpriteRenderer.isVisible)
            {
                Beh.SpriteRenderer.sprite = Utility.BarSprite;
                var Bar = Beh.Bar;
                Bar.localScale = new Vector3(9f, 1f) * ModAPI.PixelSize;
                Bar.localPosition = Vector3.zero;
                Bar.gameObject.SetActive(true);
            }
        });
    }

    public void HealBone() => Limbs.Where(x => x.Broken).ForEach(x => x.HealBone());

    public void Update() => Limbs.ForEach(l => l.ManagedUpdate());
    public void LateUpdate() => Limbs.ForEach(l => l.ManagedLateUpdate());
    public void FixedUpdate() => Limbs.ForEach(l => l.ManagedFixedUpdate());

    public void AntiGravity() => Instance.ForEach<Rigidbody2D>(rigidbody => rigidbody.AddForce(rigidbody.mass * (localGravity - Physics2D.gravity) * (antiGravity ? 1f : 0f)));

    public class BodyInfo
    {
        private static readonly Action<LimbStatusBehaviour> s_Start = Access.CreateAction<LimbStatusBehaviour>("Start");
        private static readonly Func<LimbBehaviour, GameObject> g_GetStatus = Access.CreateFieldGetter<LimbBehaviour, GameObject>("myStatus");
        private GameObject Instance;
        public (GameObject Status, LimbStatusBehaviour Beh) limbStatus;
        public LimbBehaviour limb, connectedLimb;
        public Vector3 localScale;
        public Vector2 anchor, connectedAnchor;
        public Vector2 limits;
        public Color color;
        public bool useLimits, Loop = true, flipJoint;
        public float mass, strength;
        [Range(0f, 1f)]
        public float _progress = 0f;
        [Range(0f, 1f)]
        public float speed = 0.08f;

        public BodyInfo(GameObject Instance, LimbBehaviour limb, bool Flip)
        {
            this.Instance = Instance;
            this.limb = limb;
            Utility.TryCatchAction(() =>
            {
                var status = g_GetStatus(limb);
                var behaviour = status.GetComponent<LimbStatusBehaviour>();
                s_Start(behaviour);
                limbStatus = (status, behaviour);
            });
            this.mass = limb.PhysicalBehaviour.TrueInitialMass;
            this.strength = limb.BaseStrength;
            this.color = limb.Color;
            this.localScale = limb.transform.localScale;
            this.connectedLimb = limb.Joint.connectedBody?.GetComponent<LimbBehaviour>();
            this.anchor = limb.Joint.anchor;
            this.connectedAnchor = limb.Joint.connectedAnchor;
            this.flipJoint = Flip;
            this.limits.x = IsFlipped ? -limb.Joint.limits.max : limb.Joint.limits.min;
            this.limits.y = IsFlipped ? -limb.Joint.limits.min : limb.Joint.limits.max;
            this.useLimits = limb.Joint.useLimits;
            this.limb.SetPrivate("originalJointLimits", this.limits);
        }

        public void Locked()
        {
            limb.Color = color;
            limb.transform.localScale = localScale;
            limb.PhysicalBehaviour.TrueInitialMass = limb.PhysicalBehaviour.rigidbody.mass = mass;
            limb.BaseStrength = strength;
            limb.PhysicalBehaviour.RecalculateMassBasedOnSize();
            mass = limb.PhysicalBehaviour.TrueInitialMass;
            strength = limb.BaseStrength;
        }

        private bool IsFlipped => Instance.transform.localScale.x < 0 && !flipJoint;

        public void Attach()
        {
            var joint = limb.gameObject.AddComponent<HingeJoint2D>();
            limb.Joint.Destroy();
            Rigidbody2D rigidbody = limb.PhysicalBehaviour.rigidbody, connectedBody = connectedLimb?.PhysicalBehaviour.rigidbody;
            rigidbody.rotation = connectedBody != null ? connectedBody.rotation : 0f;

            joint.connectedBody = connectedBody;
            joint.autoConfigureConnectedAnchor = false;
            joint.anchor = anchor;
            joint.connectedAnchor = connectedAnchor;
            joint.limits = limits.ToLimits();
            joint.useLimits = useLimits;
            joint.useMotor = true;
            joint.motor = new JointMotor2D { maxMotorTorque = limb.MotorStrength, motorSpeed = 0f };
            limb.Joint = joint;
            limb.SetPrivate("originalJointLimits", limits);
            limb.CirculationBehaviour.IsDisconnected = false;
            if (connectedLimb != null && connectedLimb.CirculationBehaviour != limb.CirculationBehaviour.Source)
                connectedLimb.CirculationBehaviour.IsDisconnected = false;
            limb.CirculationBehaviour.IsDisconnected = false;
            limb.HasJoint = connectedBody != null;
        }
    }
}

public class InvincibleHelper : MonoBehaviour
{
    public static InvincibleHelper Helper { get; private set; }

    public static Coroutine coroutine;

    private static readonly Dictionary<GameObject, ObjectInfo> InvincibleInstances = new Dictionary<GameObject, ObjectInfo>();

    private void Awake()
    {
        if (Helper != null && Helper != this)
        {
            Destroy(gameObject);
            return;
        }
        Helper = this;
        DontDestroyOnLoad(gameObject);
        if (coroutine == null)
            coroutine = StartCoroutine(Fixer());
    }

    public void Add(ObjectInfo info)
    {
        if (info == null || info.Instance == null) return;
        if (!InvincibleInstances.ContainsKey(info.Instance))
            InvincibleInstances.Add(info.Instance, info);
    }

    public void Remove(GameObject instance)
    {
        if (instance != null)
            InvincibleInstances.Remove(instance);
    }

    public bool HasInstance(GameObject obj)
    {
        if (obj == null) return false;
        return InvincibleInstances.ContainsKey(obj.transform.root.gameObject);
    }

    void CleanUp()
    {
        if (InvincibleInstances.Count == 0)
            return;
        var keysToRemove = new List<GameObject>();
        foreach (var kvp in InvincibleInstances)
            if (kvp.Key == null || kvp.Value.Instance == null || kvp.Value.Limbs.Count == 0)
                keysToRemove.Add(kvp.Key);

        foreach (var key in keysToRemove)
            InvincibleInstances.Remove(key);
    }
    void Update()
    {
        if (InvincibleInstances.Count == 0)
            return;

        if (Time.frameCount % 100 == 0)
            CleanUp();

        foreach (var info in InvincibleInstances.Values)
        {
            if (info.Instance == null) continue;
            info.Update();
            info.Invincible();
        }
    }

    void LateUpdate()
    {
        if (coroutine == null)
            coroutine = StartCoroutine(Fixer());
        if (InvincibleInstances.Count == 0)
            return;
        foreach (var info in InvincibleInstances.Values)
            if (info.Instance != null)
                info.LateUpdate();
    }

    void FixedUpdate()
    {
        if (InvincibleInstances.Count == 0)
            return;
        foreach (var info in InvincibleInstances.Values)
        {
            if (info.Instance == null) continue;
            info.HealBone();
            info.FixedUpdate();
            info.AcidRegen();
            if (info.antiGravity)
                info.AntiGravity();
        }
    }
    IEnumerator Fixer()
    {
        Debug.Log("Fixer coroutine start");
        while (true)
        {
            if (InvincibleInstances.Count == 0)
            {
                yield return null;
                continue;
            }

            foreach (var info in InvincibleInstances.Values)
            {
                if (info.Instance != null)
                    info.Invincible();
            }
            yield return null;
        }
    }
    void OnDisable() => gameObject.Destroy();
    void OnDestroy() => Utility.m_helper = Utility.CreateHelper<InvincibleHelper>("InvincibleHelper");
}

// ── Utility partial ──
public static partial class Utility
{
    public static InvincibleHelper m_helper;
    public static List<ObjectInfo> InvincibleTargets = new List<ObjectInfo>();
    public static List<Type> AllowedComponents = new List<Type>() { typeof(Dont), typeof(LineRenderer), typeof(BBHelper), typeof(BeBeing), typeof(NoCollide), typeof(EdgeCollider2D), typeof(UseEventTrigger), typeof(Transform), typeof(SpriteRenderer), typeof(BoxCollider2D), typeof(Rigidbody2D), typeof(PhysicalBehaviour), typeof(LimbBehaviour), typeof(Collider2D), typeof(SkinMaterialHandler), typeof(PolygonCollider2D), typeof(CirculationBehaviour), typeof(ShotMessagePropagator), typeof(ContextMenuOptionComponent), typeof(AudioSource), typeof(SerialisableIdentity), typeof(GripBehaviour), typeof(Hover), typeof(DistanceJoint2D), typeof(FixedJoint2D), typeof(HingeJoint2D), typeof(RelativeJoint2D), typeof(SliderJoint2D), typeof(ConfigurableJoint), typeof(SpringJoint2D), typeof(BloodWireBehaviour), typeof(UseWireBehaviour), typeof(CopperWireBehaviour), typeof(DistanceJointWireBehaviour), typeof(EnergyWireBehaviour), typeof(NewUseWireBehaviour), typeof(RigidWireBehaviour), typeof(SliderJointWireBehaviour), typeof(FixedJointWireBehaviour), typeof(FixedCableBehaviour), typeof(FreezeBehaviour), typeof(AliveBehaviour), typeof(PersonBehaviour), typeof(HingeJointLimitAutofixBehaviour), typeof(IncreaseStatOnStart), typeof(DisintegrationCounterBehaviour), typeof(DeregisterBehaviour), typeof(TexturePackApplier), typeof(Optout), typeof(AudioSourceTimeScaleBehaviour), typeof(SerialiseInstructions), typeof(SerialisableIdentity), typeof(AntiDestroyPersonBeh), typeof(AntiDestroy), typeof(TrailRenderer), typeof(BladeSharp) };

    public static void AddAllowedComponents(IEnumerable<Type> range) => AllowedComponents.AddRange(range.Where(t => !AllowedComponents.Contains(t)).ToList());

    public static InvincibleHelper Helper
    {
        get
        {
            if (m_helper == null)
                m_helper = CreateHelper<InvincibleHelper>("InvincibleHelper");
            return m_helper;
        }
    }

    public static void CheckComponent(GameObject target, List<Type> except = null, bool Wait = false)
    {
        if (target == null)
            return;
        if (Wait)
        {
            NextFrameCoroutine(() => CheckComponent(target, except));
            return;
        }
        foreach (var component in target.GetComponentsInChildren<Component>())
        {
            var type = component.GetType();
            if (!AllowedComponents.Contains(type) && (except?.Contains(type) != true))
                component.DestroyImmediate();
        }
    }
}
