using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;
using FishUtility;

namespace FishUtility;

[DefaultExecutionOrder(-32000)]
public class ActionInvoker : MonoBehaviour
{
    public static bool quitting;

    private static readonly List<Action> updateActions = new List<Action>();
    private static readonly List<Action> lateUpdateActions = new List<Action>();
    private static readonly List<Action> fixedUpdateActions = new List<Action>();
    private static readonly List<Action> quitActions = new List<Action>();

    public static readonly Dictionary<string, List<Coroutine>> coroutines = new Dictionary<string, List<Coroutine>>();

    void Awake() => DontDestroyOnLoad(gameObject);
    void Update()
    {
        if (!quitting)
            InvokeList(updateActions);
    }
    void LateUpdate()
    {
        if (!quitting)
            InvokeList(lateUpdateActions);
    }
    void FixedUpdate()
    {
        if (!quitting)
            InvokeList(fixedUpdateActions);
    }

    public void AddUpdateAction(Action a) => Add(updateActions, a);
    public void AddLateUpdateAction(Action a) => Add(lateUpdateActions, a);
    public void AddFixedUpdateAction(Action a) => Add(fixedUpdateActions, a);
    public void AddQuitAction(Action a) => Add(quitActions, a);
    public void RemoveUpdateAction(Action a, Action b = null) => Remove(updateActions, a, b);
    public void RemoveLateUpdateAction(Action a, Action b = null) => Remove(lateUpdateActions, a, b);
    public void RemoveFixedUpdateAction(Action a, Action b = null) => Remove(fixedUpdateActions, a, b);
    public void RemoveQuitAction(Action a, Action b = null) => Remove(quitActions, a, b);

    private void Add(List<Action> list, Action a)
    {
        if (a != null && !list.Contains(a))
            list.Add(a);
    }
    private void Remove(List<Action> list, Action a, Action b = null)
    {
        if (a != null) list.Remove(a);
        b?.Invoke();
    }
    private void InvokeList(List<Action> list)
    {
        for (int i = 0; i < list.Count; i++)
        {
            try { list[i]?.Invoke(); }
            catch (Exception ex)
            {
                Debug.LogError($"[ActionInvoker] Exception in {list[i]?.Method.Name}: {ex.Message}\n{ex.StackTrace}");
            }
        }
    }

    public void StartNamedCoroutine(string name, IEnumerator routine)
    {
        if (routine == null || quitting) return;
        if (!coroutines.TryGetValue(name, out var list))
            coroutines[name] = list = new List<Coroutine>();
        list.Add(StartCoroutine(routine));
    }

    public void StopNamedCoroutines(string name)
    {
        if (!coroutines.TryGetValue(name, out var list)) return;
        for (int i = list.Count - 1; i >= 0; i--)
            if (list[i] != null) StopCoroutine(list[i]);
        coroutines.Remove(name);
    }

    public void StopAllNamedCoroutines()
    {
        foreach (var list in coroutines.Values)
            for (int i = list.Count - 1; i >= 0; i--)
                if (list[i] != null) StopCoroutine(list[i]);
        coroutines.Clear();
    }

    private void OnApplicationQuit()
    {
        quitting = true;
        InvokeList(quitActions);
        StopAllCoroutines();
        StopAllNamedCoroutines();
    }

    private void OnDestroy() => StopAllNamedCoroutines();
}

public class ChainDestroy : MonoBehaviour
{
    public static ChainDestroy Connecter { get; private set; }
    public static Dictionary<GameObject, List<GameObject>> ChainRelation = new Dictionary<GameObject, List<GameObject>>();

    private void Awake()
    {
        Connecter = this;
        DontDestroyOnLoad(base.gameObject);
        DontDestroyOnLoad(this);
    }

    public void Contact(GameObject contactRoot, GameObject contactChild)
    {
        if (contactRoot == null || contactChild == null) return;

        if (!ChainRelation.TryGetValue(contactRoot, out var children))
            ChainRelation[contactRoot] = new List<GameObject> { contactChild };
        else if (!children.Contains(contactChild))
            children.Add(contactChild);
    }

    public void Disassociate(GameObject contactRoot, GameObject contactChild)
    {
        if (contactRoot == null || contactChild == null) return;

        ChainRelation[contactRoot]?.Remove(contactChild);
    }

    public void RemoveAllContact(GameObject contactRoot)
    {
        if (contactRoot != null)
            ChainRelation.Remove(contactRoot);
    }

    private void LateUpdate() => Sync();

    private void Sync()
    {
        ChainRelation.ForEach(chainInfo =>
        {
            chainInfo.Value.RemoveAll(child => child == null);
            if (chainInfo.Key == null)
                chainInfo.Value.DestroyMult();
        });
        ChainRelation.RemoveAll(x => x.Key == null);
    }

    void OnDisable() => gameObject.Destroy();
    void OnDestroy() => Utility.m_connecter = Utility.CreateHelper<ChainDestroy>("ChainConnecter");
}

public class FreezeController : MonoBehaviour
{
    public static FreezeController Controller { get; private set; }
    private void Awake()
    {
        Controller = this;
        DontDestroyOnLoad(base.gameObject);
        DontDestroyOnLoad(this);
    }
    public class FreezeInfo
    {
        public Vector2 velocity, pos, scale;
        public Quaternion rot;
        public float angularVelocity;
        public FreezeInfo(Rigidbody2D rb)
        {
            velocity = rb.velocity;
            angularVelocity = rb.angularVelocity;
            pos = rb.transform.position;
            rot = rb.transform.rotation;
            scale = rb.transform.localScale;
        }
    }
    public readonly Dictionary<Rigidbody2D, FreezeInfo> freeze = new Dictionary<Rigidbody2D, FreezeInfo>();

    public void Freeze(Rigidbody2D rb)
    {
        if (rb == null || freeze.ContainsKey(rb)) return;

        freeze[rb] = new FreezeInfo(rb);
        rb.bodyType = RigidbodyType2D.Static;
    }

    public bool OnFreezing(Rigidbody2D rb) => freeze.ContainsKey(rb);

    public void UnFreeze(Rigidbody2D rb)
    {
        if (rb == null || !freeze.TryGetValue(rb, out var info)) return;

        rb.bodyType = RigidbodyType2D.Dynamic;
        rb.velocity = info.velocity;
        rb.angularVelocity = info.angularVelocity;
        freeze.Remove(rb);
    }

    public void Update() => CleanAndApply();

    public void FixedUpdate() => CleanAndApply();

    private void CleanAndApply()
    {
        var toRemove = new List<Rigidbody2D>();
        freeze.ForEach(entry =>
        {
            if (entry.Key == null)
                toRemove.Add(entry.Key);
            else
            {
                var obj = entry.Key;
                var info = entry.Value;
                obj.bodyType = RigidbodyType2D.Static;
                obj.transform.position = info.pos;
                obj.transform.rotation = info.rot;
                obj.transform.localScale = info.scale;
            }
        });
        foreach (var rb in toRemove)
            freeze.Remove(rb);
    }

    void OnDisable() => gameObject.Destroy();
    void OnDestroy() => Utility.m_controller = Utility.CreateHelper<FreezeController>("ActionInvoker");
}

public class SlowBehaviour : MonoBehaviour
{
    public float TimeScaleMultiplier;
    public static SlowBehaviour Slower { get; private set; }

    public readonly Dictionary<GameObject, SlowInfo> slowedObjects = new Dictionary<GameObject, SlowInfo>();
    public readonly Dictionary<ParticleSystem, SlowInfo> slowedParticles = new Dictionary<ParticleSystem, SlowInfo>();
    public readonly Dictionary<AudioSource, SlowInfo> slowedAudio = new Dictionary<AudioSource, SlowInfo>();

    private void Awake()
    {
        Slower = this;
        DontDestroyOnLoad(base.gameObject);
        DontDestroyOnLoad(this);
    }

    public void Clear()
    {
        foreach (var particleSystem in slowedParticles.Keys.ToList())
            RemoveSlowParticle(particleSystem);

        foreach (var audioSource in slowedAudio.Keys.ToList())
            RemoveSlowAudio(audioSource);

        foreach (var gameObject in slowedObjects.Keys.ToList())
            RemoveSlowEffect(gameObject);
    }

    public void ApplySlowParticle(ParticleSystem particleSystem)
    {
        if (particleSystem == null || slowedParticles.ContainsKey(particleSystem)) return;

        var main = particleSystem.main;
        slowedParticles[particleSystem] = new SlowInfo(main.simulationSpeed);
        main.simulationSpeed /= TimeScaleMultiplier;
    }

    public void RemoveSlowParticle(ParticleSystem particleSystem)
    {
        if (particleSystem == null || !slowedParticles.TryGetValue(particleSystem, out var slowInfo)) return;

        var main = particleSystem.main;
        main.simulationSpeed = slowInfo.SimulationSpeed;
        slowedParticles.Remove(particleSystem);
    }

    public void ApplySlowAudio(AudioSource audioSource)
    {
        if (audioSource == null || slowedAudio.ContainsKey(audioSource)) return;

        slowedAudio[audioSource] = new SlowInfo(audioSource, TimeScaleMultiplier);
    }

    public void RemoveSlowAudio(AudioSource audioSource)
    {
        if (audioSource == null || !slowedAudio.TryGetValue(audioSource, out var slowInfo)) return;

        audioSource.pitch = slowInfo.OriginalSound;
        slowedAudio.Remove(audioSource);
    }

    private void Update()
    {
        foreach (var audioSource in slowedAudio.Keys.ToList())
        {
            if (audioSource == null)
                slowedAudio.Remove(audioSource);
            else
                slowedAudio[audioSource].UpdateSoundPitch();
        }
    }

    public void ApplySlowEffect(GameObject other)
    {
        if (other == null || slowedObjects.ContainsKey(other)) return;

        var rb = other.GetComponent<Rigidbody2D>();
        if (rb == null || rb.bodyType == RigidbodyType2D.Static) return;

        var phys = other.GetComponent<PhysicalBehaviour>();
        var slowInfo = phys != null ? new SlowInfo(phys.rigidbody.gravityScale, phys.TrueInitialMass, TimeScaleMultiplier) : new SlowInfo(rb.gravityScale, rb.mass, TimeScaleMultiplier);
        slowedObjects[other] = slowInfo;

        float slowMultiplier = TimeScaleMultiplier;

        if (phys != null)
        {
            ApplySlowAudio(phys.MainAudioSource);

            phys.InitialMass *= slowMultiplier;
            phys.TrueInitialMass *= slowMultiplier;
            rb.mass *= slowMultiplier;
            rb.velocity /= slowMultiplier;
            rb.angularVelocity /= slowMultiplier;
            phys.rigidbody.gravityScale /= slowMultiplier * 6f;
            phys.InitialGravityScale /= slowMultiplier * 6f;

            if (other.TryGetComponent(out LimbBehaviour limb))
            {
                limb.ImpactDamageMultiplier /= slowMultiplier * 2f;
                limb.GForceDamageThreshold *= slowMultiplier;
                limb.GForcePassoutThreshold *= slowMultiplier;
                limb.BaseStrength /= slowMultiplier;
            }

            if (other.TryGetComponent(out DestroyableBehaviour destroyable))
                destroyable.MinimumImpactForce /= slowMultiplier;
        }
        else
        {
            rb.mass *= slowMultiplier;
            rb.velocity /= slowMultiplier;
            rb.angularVelocity /= slowMultiplier;
            rb.gravityScale /= slowMultiplier * 6f;
        }
    }

    public void RemoveSlowEffect(GameObject other)
    {
        if (other == null || !slowedObjects.TryGetValue(other, out var slowInfo)) return;

        var rb = other.GetComponent<Rigidbody2D>();
        if (rb == null || rb.bodyType == RigidbodyType2D.Static) return;

        var phys = other.GetComponent<PhysicalBehaviour>();

        if (phys != null)
        {
            phys.InitialMass = slowInfo.InitialMassScale;
            phys.TrueInitialMass = slowInfo.InitialMassScale;
            rb.mass = slowInfo.InitialMassScale;
            phys.rigidbody.gravityScale = slowInfo.InitialGravityScale;
            phys.InitialGravityScale = slowInfo.InitialGravityScale;

            rb.velocity *= 1.6f * TimeScaleMultiplier;
            rb.angularVelocity *= 1.6f * TimeScaleMultiplier;

            if (other.TryGetComponent(out LimbBehaviour limb))
            {
                limb.ImpactDamageMultiplier *= TimeScaleMultiplier * 2f;
                limb.GForceDamageThreshold /= TimeScaleMultiplier;
                limb.GForcePassoutThreshold /= TimeScaleMultiplier;
                limb.BaseStrength *= TimeScaleMultiplier;
            }

            if (other.TryGetComponent(out DestroyableBehaviour destroyable))
                destroyable.MinimumImpactForce *= TimeScaleMultiplier;

            RemoveSlowAudio(phys.MainAudioSource);
        }
        else
        {
            rb.mass = slowInfo.InitialMassScale;
            rb.velocity *= 1.6f * TimeScaleMultiplier;
            rb.angularVelocity *= 1.6f * TimeScaleMultiplier;
            rb.gravityScale = slowInfo.InitialGravityScale;
        }

        slowedObjects.Remove(other);
    }

    void OnDisable() => gameObject.Destroy();
    void OnDestroy() => Utility.m_slower = Utility.CreateHelper<SlowBehaviour>("Slower");
}

public class SlowInfo
{
    public AudioSource audioSource;
    public float InitialGravityScale;
    public float InitialMassScale;
    public float OriginalSound;
    public float TimeScaleMultiplier;
    public float SimulationSpeed;
    public float LifeTime;

    public SlowInfo(float initialGravityScale, float initialMassScale, float timeScaleMultiplier)
    {
        InitialGravityScale = initialGravityScale;
        InitialMassScale = initialMassScale;
        TimeScaleMultiplier = timeScaleMultiplier;
    }

    public SlowInfo(float simulationSpeed) => SimulationSpeed = simulationSpeed;

    public SlowInfo(AudioSource audioSource, float timeScaleMultiplier)
    {
        this.audioSource = audioSource;
        TimeScaleMultiplier = timeScaleMultiplier;
        OriginalSound = audioSource.pitch;
    }

    public void UpdateSoundPitch() => audioSource.pitch = OriginalSound / TimeScaleMultiplier;
}

public class PseudoChild : MonoBehaviour
{
    [SkipSerialisation]
    public Transform Parent;
    public bool RotationSync = true;
    public bool ScaleSync = true;
    public float DeleteAfterParentDestroy
    {
        set
        {
            needDestroy = true;
            m_deleteAfterParentDestroy = value;
        }
    }
    private float m_deleteAfterParentDestroy;
    private bool destroyed = false;
    private bool needDestroy = false;
    private void Update()
    {
        if (Parent == null)
        {
            if (!destroyed)
            {
                StartCoroutine(DestroyAction());
            }
        }
        else
        {
            transform.position = Parent.position;
            if (ScaleSync)
                transform.localScale = Parent.localScale;
            if (RotationSync)
                transform.rotation = Parent.rotation;
        }
    }
    private IEnumerator DestroyAction()
    {
        destroyed = true;
        if (needDestroy)
        {
            yield return new WaitForSeconds(m_deleteAfterParentDestroy);
            gameObject.Destroy();
        }
    }
}

// ── Utility partial ──
public static partial class Utility
{
    public static SlowBehaviour m_slower;
    public static FreezeController m_controller;
    public static ActionInvoker m_invoker;
    public static ChainDestroy m_connecter;

    public static SlowBehaviour Slower
    {
        get
        {
            if (m_slower == null)
                m_slower = CreateHelper<SlowBehaviour>("SlowBehaviour");
            return m_slower;
        }
    }

    public static FreezeController Controller
    {
        get
        {
            if (m_controller == null)
                m_controller = CreateHelper<FreezeController>("FreezeController");
            return m_controller;
        }
    }

    private static readonly object _lock = new object();
    public static ActionInvoker Invoker
    {
        get
        {
            if (m_invoker != null) return m_invoker;
            lock (_lock)
            {
                if (m_invoker == null)
                    m_invoker = CreateHelper<ActionInvoker>("ActionInvoker");
            }
            return m_invoker;
        }
    }

    public static ChainDestroy Connecter
    {
        get
        {
            if (m_connecter == null)
                m_connecter = CreateHelper<ChainDestroy>("ChainConnecter");
            return m_connecter;
        }
    }

    private static List<GameObject> sliceForeverList = new List<GameObject>(), breakForeverList = new List<GameObject>(), disForeverList = new List<GameObject>();

    public static void NextFrameCoroutine(Action action, int frames = 1)
    {
        if (frames <= 0)
            TryCatchAction(action);
        else
            Invoker.StartCoroutine(Utils.NextFrameCoroutine(() => NextFrameCoroutine(action, frames - 1)));
    }
    public static void NextFixedUpdateCoroutine(Action action) => Invoker.StartCoroutine(NextFixedFrameCoroutine(() => TryCatchAction(action)));

    private static IEnumerator NextFixedFrameCoroutine(Action action)
    {
        yield return new WaitForFixedUpdate();
        action();
    }
    public static IEnumerator DestroyTask(GameObject @object, Func<bool> func)
    {
        yield return new WaitUntil(func);
        @object.Destroy();
    }

    public static void AddButtonInCatalog(string name, string desc, string categoryName, Sprite sprite, int targetSibilingIndex, UnityAction action)
    {
        var newButton = GameObject.Instantiate(CatalogBehaviour.Main.ItemButtonPrefab, CatalogBehaviour.Main.ItemContainer);
        newButton.GetComponent<ItemButtonBehaviour>().Destroy();
        var toolTip = newButton.GetComponent<HasTooltipBehaviour>();
        newButton.transform.Find("Outdated").gameObject.Destroy();
        newButton.transform.Find("Remove").gameObject.Destroy();
        newButton.transform.Find("button row").gameObject.SetActive(false);
        toolTip.TooltipText = CatalogBehaviour.Main.TooltipText;
        toolTip.Text = $"<b>{name}</b>\n{desc}";
        newButton.GetComponent<Image>().sprite = sprite;
        var button = newButton.GetComponent<Button>();
        button.onClick.RemoveAllListeners();
        //button.onClick.SetPersistentListenerState(0, UnityEventCallState.Off);
        button.onClick.AddListener(action);
        Action act = null;
        act = () =>
        {
            if (newButton == null)
                Invoker.RemoveUpdateAction(act);
            if (CatalogBehaviour.Main.SelectedCategory.name == categoryName && newButton?.activeInHierarchy == false)
                newButton?.SetActive(true);
            else if (CatalogBehaviour.Main.SelectedCategory.name != categoryName && newButton?.activeInHierarchy == true)
                newButton?.SetActive(false);
        };
        Invoker.AddUpdateAction(act);
        newButton.transform.SetSiblingIndex(targetSibilingIndex);
    }

    public static GameObject CreateVaporiseEffect(Vector3 pos, (Bounds? _bounds, float? scale)? info = null, GameObject propEffect = null, Rigidbody2D rigid = null, Vector2? velocity = null)
    {
        float scale = info?.scale ?? 1f;
        var bounds = info?._bounds ?? new Bounds(pos, new Vector3(1.2f, 1.2f, 0.2f) * scale);
        var Effect = UnityEngine.Object.Instantiate(propEffect ?? Resources.Load<GameObject>("Prefabs/VaporiseEffect"), pos, Quaternion.identity);
        float num = Mathf.Clamp(10 * Mathf.Sqrt(bounds.extents.sqrMagnitude * 4f), 2f, 250f);
        if (Effect.TryGetComponent(out ParticleSystem particleSystem))
        {
            for (int num2 = 0; num2 < num; num2++)
            {
                var randomPos = new Vector3(UnityEngine.Random.Range(bounds.min.x, bounds.max.x), UnityEngine.Random.Range(bounds.min.y, bounds.max.y), 0);
                particleSystem.Emit(new ParticleSystem.EmitParams
                {
                    position = randomPos,
                    velocity = velocity.HasValue ? velocity.Value : (2f * UnityEngine.Random.value * (rigid != null ? rigid.GetPointVelocity(randomPos) : Vector2.zero))
                }, 1);
            }
            Invoker.StartCoroutine(DestroyTask(Effect, () => particleSystem.isStopped));
        }
        return Effect;
    }

    public static void UltimateVaporise(this PhysicalBehaviour phys, bool wait = false)
    {
        if (wait)
        {
            NextFrameCoroutine(() => phys.UltimateVaporise());
            return;
        }
        CreateVaporiseEffect(phys.transform.position, (phys.spriteRenderer.bounds, 1f), null, phys.rigidbody);
        phys.UltimateDisintegrate();
    }

    public static void UltimatePop(this IEnumerable<PhysicalBehaviour> physList)
    {
        var validPhys = physList?.Where(p => p && p.gameObject).ToArray();
        if (validPhys == null || validPhys.Length == 0) return;

        var objects = validPhys.Select(p => p.gameObject).Where(x => x).Distinct().ToArray();
        var allRenderers = objects.SelectMany(x => x.GetComponentsInChildren<Renderer>())
            .Where(r => r && r.enabled && r.gameObject.activeInHierarchy)
            .Where(r => !r.name.ToLower().Contains("lightsprite") && r.name != "light" && r.name != "Outline")
            .Where(r => !(r is SpriteRenderer sr && !sr.sprite))
            .Where(r => !Mathf.Approximately(r.transform.lossyScale.x, 0f) && !Mathf.Approximately(r.transform.lossyScale.y, 0f))
            .Where(r => !(r.sharedMaterial && r.sharedMaterial.name.ToLower().Contains("lightsprite")))
            .Where(r => !(r.material && r.material.name.ToLower().Contains("lightsprite")))
            .Where(r => !r.GetComponent<Light>())
            .Where(r => !r.GetComponent("LightSprite"))
            .Where(r => !(r is ParticleSystemRenderer))
            .Where(r => !(r is TrailRenderer))
            .Where(r => r.bounds.size != Vector3.zero)
            .ToArray();

        if (allRenderers.Length == 0) { validPhys.ForEach(p => p.UltimateDisintegrate()); return; }

        Bounds combined = allRenderers[0].bounds;
        for (int i = 1; i < allRenderers.Length; i++)
            combined.Encapsulate(allRenderers[i].bounds);
        Vector3 worldCenter = combined.center;
        Vector3 worldSize = combined.size;

        Texture2D texture = objects.CaptureMultipleObjects2D();
        if (!texture) { validPhys.ForEach(p => p.UltimateDisintegrate()); return; }

        Transform prefabChild = ModAPI.FindSpawnable("Balloon").Prefab.transform.GetChild(0);
        Transform obj = UnityEngine.Object.Instantiate(prefabChild);
        obj.gameObject.SetActive(true);
        obj.SetParent(null);
        obj.position = worldCenter;
        obj.rotation = Quaternion.identity;
        obj.localScale *= 1.1f;

        ParticleSystem ps = obj.GetComponent<ParticleSystem>();
        if (ps != null)
        {
            var shape = ps.shape;
            shape.enabled = true;
            shape.shapeType = ParticleSystemShapeType.Rectangle;
            shape.texture = texture;
            shape.scale = new Vector3(worldSize.x, worldSize.y, 0f);
            shape.rotation = Vector3.zero;

            var main = ps.main;
            float rawSize = Mathf.Pow((worldSize.x + worldSize.y) * 0.5f, 0.6f) * 0.03f;
            main.startSize = Mathf.Max(rawSize, 0.03f);

            int pixelArea = texture.width * texture.height;
            short burstCount = (short)Mathf.Min(pixelArea * 0.008f, 32767f);
            main.maxParticles = Mathf.Max(main.maxParticles, burstCount + 1000);
            var emission = ps.emission;
            emission.rateOverTime = 0f;
            emission.SetBursts(new[] { new ParticleSystem.Burst(0f, burstCount) });
        }

        obj.gameObject.Destroy(2.5f);
        UnityEngine.Object.Destroy(texture, 2.5f);
        validPhys.ForEach(p => p.UltimateDisintegrate());
    }


    public static IEnumerator PopSequence(List<PhysicalBehaviour> targets)
    {
        SelectionController.Main.ClearSelection();

        yield return new WaitForEndOfFrame();

        targets.UltimatePop();
    }

    public static void UltimateDisintegrate(this PhysicalBehaviour phys, bool wait = false, bool disintegrateForever = false)
    {
        if (wait)
        {
            NextFrameCoroutine(() => phys.UltimateDisintegrate(disintegrateForever: disintegrateForever));
            return;
        }

        DoOnce(phys);
        void DoOnce(PhysicalBehaviour p)
        {
            Action updateAction = null;
            updateAction = () => TryCatchAction(() =>
            {
                if (p.isDisintegrated)
                    return;
                p.Deletable = true;
                p.Disintegratable = true;
                p.Disintegrate();
            }, () => Invoker.RemoveUpdateAction(updateAction));
            updateAction();
            if (disintegrateForever && !disForeverList.Contains(phys.gameObject))
            {
                disForeverList.Add(phys.gameObject);
                Invoker.AddUpdateAction(updateAction);
            }
        }
    }

    public static void UltimateSlice(this GameObject target, bool Wait = false, bool sliceForever = false)
    {
        if (Wait)
        {
            NextFrameCoroutine(() => target.UltimateSlice(sliceForever: sliceForever));
            return;
        }

        DoOnce(target);
        void DoOnce(GameObject @object)
        {
            Action updateAction = null;
            updateAction = () => TryCatchAction(() =>
            {
                @object.transform.SendMessage("Slice", SendMessageOptions.DontRequireReceiver);
                if (@object.TryGetComponent(out LimbBehaviour limb))
                {
                    limb.ImmuneToDamage = false;
                    limb.BreakingThreshold = 0;
                    limb.Slice();
                }
                @object.ForEach<Joint2D>(joint =>
                {
                    @object.BroadcastMessage("OnJointBreak2D", joint);
                    joint.DestroyImmediate();
                });
            }, () => Invoker.RemoveUpdateAction(updateAction));
            updateAction();
            if (sliceForever && !sliceForeverList.Contains(target))
            {
                sliceForeverList.Add(target);
                Invoker.AddUpdateAction(updateAction);
            }
        }
    }


    public static void UltimateBreak(this GameObject target, bool Wait = false, bool breakForever = false, Vector2 velocity = default)
    {
        if (Wait)
        {
            NextFrameCoroutine(() => target.UltimateBreak(breakForever: breakForever, velocity: velocity));
            return;
        }
        DoOnce(target);
        void DoOnce(GameObject @object, bool checkMore = false)
        {
            Action updateAction = null;
            updateAction = () => TryCatchAction(() =>
            {
                if (@object.TryGetComponent(out DestroyableBehaviour destroyable))
                {
                    destroyable.OverallChance = float.MaxValue;
                    destroyable.Break();
                }

                if (@object.TryGetComponent(out DamagableMachineryBehaviour damagable))
                {
                    damagable.Indestructible = false;
                    damagable.BreakPermanently();
                }

                if (@object.TryGetComponent(out LimbBehaviour limb) && (!checkMore || !limb.Broken))
                    typeof(LimbBehaviour).GetMethod("BreakBonepublic", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(limb, null);

                @object.transform.SendMessage("Break", velocity, SendMessageOptions.DontRequireReceiver);
                @object.transform.SendMessage("OnEMPHit", SendMessageOptions.DontRequireReceiver);
            }, () => Invoker.RemoveUpdateAction(updateAction));
            updateAction();
            if (breakForever && !breakForeverList.Contains(target))
            {
                breakForeverList.Add(target);
                Invoker.AddUpdateAction(updateAction);
            }
        }
    }

    public static void UltimateCrush(this LimbBehaviour limb, bool Wait = false)
    {
        if (Wait)
        {
            NextFrameCoroutine(() => limb.UltimateCrush());
            return;
        }
        TryCatchAction(() =>
        {
            limb.PhysicalBehaviour.Deletable = true;
            limb.PhysicalBehaviour.Disintegratable = true;
            limb.ImmuneToDamage = false;
            limb.Crush();
            limb.PhysicalBehaviour.UltimateDisintegrate(disintegrateForever: true);
        });
    }

    public static void CreatePulseExplosion(Vector3 position, float force, float range, (bool canBreak, bool canSlice) mode, float chance = 0.25f, params Transform[] except)
    {
        CameraShakeBehaviour.main.Shake(force * range * 2f, position, 1f);
        var hits = new Collider2D[256];
        hits.Take(Physics2D.OverlapCircleNonAlloc(position, range, hits, LayerMask.GetMask("Objects", "CollidingDebris", "Debris"))).Where(hit => !except.Contains(hit.transform.root))
            .ForEach(hit =>
            {
                if (hit && hit.attachedRigidbody)
                {
                    var attachedRigidbody = hit.attachedRigidbody;
                    var a = hit.transform.position - position;
                    float sqrMagnitude = a.sqrMagnitude;
                    if (sqrMagnitude >= 1E-45f)
                    {
                        attachedRigidbody.AddForce((force / Mathf.Max(1f, sqrMagnitude / (range * range)) * 3f) * (a / Mathf.Sqrt(sqrMagnitude)) * Mathf.Min(attachedRigidbody.mass, 1f), ForceMode2D.Impulse);

                        if (UnityEngine.Random.value < chance && mode.canBreak)
                            hit.gameObject.UltimateBreak(true);

                        if (UnityEngine.Random.value < chance && mode.canSlice)
                            hit.gameObject.UltimateSlice();
                    }
                }
            });
    }

    public static void CreateGravityPoint(this Vector3 point, (bool All, IEnumerable<GameObject> Target) Settings, float attractionStrength, float dampingStrength, float stopDistance, float stopVelocity, float gravityCompensation)
    {
        if (GravityPoints.ContainsKey(point))
        {
            Invoker.RemoveFixedUpdateAction(GravityPoints[point]);
            GravityPoints.Remove(point);
        }
        GravityPoints[point] = () =>
        {
            if (!Settings.All)
                Settings.Target.ToList().RemoveAll(x => x == null);
            (Settings.All ? FindTypesInWorld<Rigidbody2D>().Where(x => x.gameObject.scene.IsValid()) : Settings.Target.SelectMany(x => x.GetComponentsInChildren<Rigidbody2D>())).Where(x => x.bodyType == RigidbodyType2D.Dynamic).ForEach(x =>
            {

                Vector2 direction = point - x.transform.position;
                float distance = direction.magnitude;

                Vector2 gravityForce = Physics2D.gravity * x.mass;

                if (distance > stopDistance || x.velocity.magnitude > stopVelocity)
                {
                    Vector2 attractionForce = direction.normalized * (attractionStrength * distance) * x.mass;
                    Vector2 dampingForce = -x.velocity * dampingStrength * x.mass;
                    Vector2 finalForce = attractionForce + dampingForce - gravityForce * gravityCompensation;
                    x.AddForce(finalForce);
                }
                else
                {
                    Vector2 attractionForce = direction.normalized * (attractionStrength * distance) * x.mass;
                    Vector2 finalForce = attractionForce - gravityForce * gravityCompensation;
                    x.AddForce(finalForce);
                }

            });
        };
        Invoker.AddFixedUpdateAction(GravityPoints[point]);
    }

    public static void RemoveGravityPoint(this Vector3 point)
    {
        if (!GravityPoints.ContainsKey(point))
            return;
        Invoker.RemoveFixedUpdateAction(GravityPoints[point]);
        GravityPoints.Remove(point);
    }

    public static void ApplyDynamicInstantRepulsion(Vector3 centerPoint, IEnumerable<Rigidbody2D> targets, float repulsionStrength)
    {
        var allRigids = targets
            .SelectMany(rb => rb.GetComponentsInChildren<Rigidbody2D>())
            .Distinct()
            .Where(rb => rb.bodyType == RigidbodyType2D.Dynamic)
            .ToList();

        if (allRigids.Count == 0) return;

        float maxDistance = allRigids.Max(rb => Vector2.Distance(centerPoint, rb.position));
        float effectiveRadius = maxDistance + 1.5f;

        allRigids.ForEach(rb =>
        {
            Vector2 pos2D = rb.position;
            Vector2 direction = pos2D - (Vector2)centerPoint;
            float distance = direction.magnitude;
            if (distance < 0.05f) distance = 0.05f;
            float forceFalloff = 1f - (distance / effectiveRadius);
            Vector2 finalForce = direction.normalized * (repulsionStrength * forceFalloff) * rb.mass;
            rb.AddForce(finalForce * Time.fixedDeltaTime, ForceMode2D.Impulse);
        });
    }

    public static void CreateSmoke(Transform parent, Vector3 pos, float life) => Global.main.StartCoroutine(CreateSmokeCor(parent, pos, life));

    public static GameObject CreateExSmoke(Vector3 pos, Color color, float speed = 1f, bool HasSource = false, UnityAction<GameObject> Action = null)
    {
        var Explosion = UnityEngine.Object.Instantiate<GameObject>(Resources.Load<GameObject>("Prefabs/Explosion"), pos, Quaternion.identity);
        Explosion.ForEach<ParticleSystem>(ExplosionParticle => { var main = ExplosionParticle.main; main.startColor = color; main.simulationSpeed *= speed; });
        if (!HasSource)
        {
            Explosion.GetComponentsInChildren<AudioBehaviour>().DestroyMult();
            Explosion.GetComponent<ExplosionSoundBehviour>().Destroy();
        }
        Explosion.transform.Find("Fire").gameObject.SetActive(false);
        Explosion.transform.Find("Flash").gameObject.SetActive(false);
        Explosion.transform.Find("Embers (1)").gameObject.SetActive(false);
        Invoker.StartCoroutine(DestroyTask(Explosion, () => Explosion.GetComponent<ParticleSystem>().isStopped));
        Action?.Invoke(Explosion);
        return Explosion;
    }

    public static void CreateLightningBolt(Vector3 startPos, Vector3 endPos, Color color, float width = 0.04f, float life = 0.1f) => Global.main.StartCoroutine(CreateLightningBoltCor(startPos, endPos, color, width, life));

    public static IEnumerator CreateLightningBoltCor(Vector3 startPos, Vector3 endPos, Color color, float width = 0.04f, float life = 0.1f)
    {
        var Lightning = UnityEngine.Object.Instantiate(UnityEngine.Object.FindObjectOfType<WeatherLightningBehaviour>());
        var SingleBolt = Lightning.LineRenderer;
        var vertices = new Vector3[SingleBolt.positionCount];
        SingleBolt.startColor = color;
        SingleBolt.endColor = color;
        SingleBolt.widthCurve = new AnimationCurve(new Keyframe(0f, width), new Keyframe(0.5f, width * 1.6f), new Keyframe(1f, width * 0.02f));
        Lightning.LightSprite.transform.position = startPos;
        float x = UnityEngine.Random.value * 10000f, num = Vector2.Distance(startPos, endPos) * 0.05f;
        Lightning.AudioSource.PlayOneShot(Lightning.Thunder.PickRandom<AudioClip>());
        int num2 = vertices.Length;
        //SingleBolt.startWidth = width;
        //SingleBolt.endWidth = width;
        SingleBolt.enabled = true;
        Lightning.LightSprite.enabled = true;
        float time = 0f;
        while (time < life)
        {
            for (int j = 0; j < num2; j++)
            {
                float num3 = (float)j / (float)num2;
                float d = 1f - Mathf.Abs(2f * num3 - 1f);
                Vector3 a = (Utils.GetPerlin2Mapped(x, num3 / num * 19f) + (Vector3)(UnityEngine.Random.insideUnitCircle * 0.2f)) * d;
                vertices[j] = Vector3.Lerp(startPos, endPos, num3) + num * a;
            }
            SingleBolt.SetPositions(vertices);
            CameraShakeBehaviour.main.Shake(150f, endPos, 0.1f);
            time += Time.deltaTime;
            yield return new WaitForFixedUpdate();
        }
        SingleBolt.enabled = false;
        Lightning.LightSprite.enabled = false;
        while (Lightning.AudioSource.isPlaying)
            yield return null;
        Lightning.gameObject.Destroy();
    }

    public static void ClearEverything(params GameObject[] expect)
    {
        EnvironmentalSettings settings = null;
        if ((bool)MapConfig.Instance)
        {
            settings = MapConfig.Instance.Settings.ShallowClone();
            MapLightBehaviour.StartEnabled = settings.Floodlights;
            PhysicalBehaviour.AmbientTemperature = settings.Ambient_temperature;
        }


        UnityEngine.Object.FindObjectsOfType<ObjectPoolBehaviour>().ForEach(x => x.Clear());

        UnityEngine.Object.FindObjectsOfType<GameObject>().ForEach(x =>
        {
            if (ToRemove.HasLayer(x.layer) && !expect.Contains(x))
                x.Destroy();
        });


        UnityEngine.Object.FindObjectsOfType<DecalControllerBehaviour>().ForEach(x => x.Clear());

        UndoControllerBehaviour.ClearHistory();
        if ((bool)AmbientTemperatureGridBehaviour.Instance)
            AmbientTemperatureGridBehaviour.Instance.World.Clear();

        UnityEngine.Object.FindObjectOfType<MapLoaderBehaviour>().Load();
        NextFrameCoroutine(() =>
        {
            if ((bool)MapConfig.Instance)
            {
                settings.CopyTo(MapConfig.Instance.Settings);
                MapConfig.Instance.ApplySettings(MapConfig.Instance.Settings);
            }

            if ((bool)EnvironmentSettingsController.Main)
                EnvironmentSettingsController.Main.Start();

            if ((bool)MapConfig.Instance)
            {
                UnityEngine.Object.FindObjectsOfType<MapLightBehaviour>().Where(x => x.enabled).ForEach(x =>
                {
                    if (MapConfig.Instance.Settings.Floodlights)
                        x.ActivateInstantly();
                    else
                        x.DeactivateInstantly();
                });
            }
        });
    }

    public static void DestroyImmediate(this UnityEngine.Object @object, bool wait = false)
    {
        if (wait)
        {
            NextFrameCoroutine(() => @object.DestroyImmediate());
            return;
        }
        TryCatchAction(() => UnityEngine.Object.DestroyImmediate(@object));
    }

    public static void InvokeAfterDelay(float delay, Action action)
    {
        Invoker.StartCoroutine(Task());
        IEnumerator Task()
        {
            yield return new WaitForSeconds(delay);
            action.Invoke();
        }
    }

    public static GameObject PlayClip(this AudioClip audioClip, Vector3 position, float volume, int scale, float deleteAfterSeconds = 10f, float min = 5, float max = 20, Transform followTo = null)
    {
        List<AudioSource> audioSources = new List<AudioSource>();
        var newObj = new GameObject("TempSource");
        newObj.transform.position = position;
        for (int i = 0; i < scale; i++)
        {
            var newSource = newObj.CreateAudioSource(min, max);
            newSource.volume = volume;
            newSource.clip = audioClip;
            Global.main.AddAudioSource(newSource);
            audioSources.Add(newSource);
        }
        audioSources.ForEach(x => x.Play());
        if (followTo != null)
        {
            var pc = newObj.AddComponent<PseudoChild>();
            pc.Parent = followTo;
            pc.ScaleSync = false;
            pc.RotationSync = false;
        }
        if (deleteAfterSeconds != -1)
            InvokeAfterDelay(deleteAfterSeconds, () => newObj.Destroy());
        return newObj;
    }

    public static IEnumerator CreateSmokeCor(Transform parent, Vector3 pos, float life)
    {
        var smoke = UnityEngine.Object.Instantiate<GameObject>(smokePrefab);
        smoke.transform.position = pos;
        var pseudoChild = smoke.AddComponent<PseudoChild>();
        pseudoChild.ScaleSync = false;
        pseudoChild.RotationSync = false;
        if (parent != null) pseudoChild.Parent = parent;
        pseudoChild.DeleteAfterParentDestroy = 0;
        var ps = smoke.GetComponent<ParticleSystem>();
        var main = ps.main;
        var emession = ps.emission;
        var shape = ps.shape;
        main.maxParticles = 5000;
        emession.rateOverDistanceMultiplier = 250f;
        shape.radius = 1;
        shape.angle = 90;
        shape.arc = 1;
        shape.randomDirectionAmount = 1f;
        shape.radiusSpeedMultiplier = 1;
        main.simulationSpeed = 20;
        main.simulationSpace = ParticleSystemSimulationSpace.Custom;
        ps.Play();
        yield return new WaitForSeconds(life);
        ps.Stop();
        yield return new WaitForSeconds(life);
        smoke.Destroy();
    }
}
