using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using TMPro;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using UnityEngine.Video;
using FishUtility;

// ── Modular value types ──
namespace FishUtility;

// ── Small utility MonoBehaviours ──

public class PointDebuggerCollider : MonoBehaviour
{
    public Collider2D Collider;
    private void Update() => ModAPI.Draw.Collider(Collider);
}


// ── Visual components ──

public class MotionBlur2D : MonoBehaviour
{
    private TrailRenderer trailRenderer;
    private SpriteRenderer spriteRenderer;
    private Material trailMaterial;
    private Quaternion lastRotation;

    void Start()
    {
        spriteRenderer = GetComponent<SpriteRenderer>();
        if (spriteRenderer == null)
        {
            Debug.LogError("物体上没有SpriteRenderer组件！");
            return;
        }

        trailRenderer = gameObject.AddComponent<TrailRenderer>();
        trailRenderer.time = 0.1f;

        float maxDimension = Mathf.Max(spriteRenderer.bounds.size.x, spriteRenderer.bounds.size.y);
        trailRenderer.startWidth = maxDimension;
        trailRenderer.endWidth = 0.0f;

        bool isWidthDominant = spriteRenderer.bounds.size.x > spriteRenderer.bounds.size.y;
        Texture2D spriteTexture = CreateSpriteTexture(spriteRenderer.sprite, isWidthDominant);
        if (spriteTexture == null)
        {
            Debug.LogError("无法创建Sprite纹理！");
            return;
        }
        trailMaterial = new Material(Shader.Find("Sprites/Default"));
        trailMaterial.mainTexture = spriteTexture;
        trailRenderer.material = trailMaterial;

        var gradient = new UnityEngine.Gradient();
        gradient.SetKeys(
            new GradientColorKey[] { new GradientColorKey(Color.white, 0.0f), new GradientColorKey(Color.white, 1.0f) },
            new GradientAlphaKey[] { new GradientAlphaKey(1.0f, 0.0f), new GradientAlphaKey(0.0f, 1.0f) }
        );
        trailRenderer.colorGradient = gradient;

        lastRotation = transform.rotation;
    }

    private Texture2D CreateSpriteTexture(Sprite sprite, bool isWidthDominant)
    {
        try
        {
            Rect rect = sprite.textureRect;
            Texture2D sourceTex = sprite.texture;

            int texWidth = (int)rect.width;
            int texHeight = (int)rect.height;
            RenderTexture rt = RenderTexture.GetTemporary(
                texWidth,
                texHeight,
                0,
                RenderTextureFormat.ARGB32
            );

            Graphics.Blit(sourceTex, rt, new Vector2(
                rect.width / sourceTex.width,
                rect.height / sourceTex.height
            ), new Vector2(
                rect.x / sourceTex.width,
                rect.y / sourceTex.height
            ));

            Texture2D newTexture = new Texture2D(texWidth, texHeight, TextureFormat.RGBA32, false);
            RenderTexture.active = rt;
            newTexture.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
            newTexture.Apply();

            if (isWidthDominant)
            {
                Texture2D rotatedTexture = new Texture2D(texHeight, texWidth, TextureFormat.RGBA32, false);
                for (int y = 0; y < texHeight; y++)
                {
                    for (int x = 0; x < texWidth; x++)
                    {
                        rotatedTexture.SetPixel(y, texWidth - 1 - x, newTexture.GetPixel(x, y));
                    }
                }
                rotatedTexture.Apply();
                Destroy(newTexture);
                newTexture = rotatedTexture;
            }

            RenderTexture.active = null;
            RenderTexture.ReleaseTemporary(rt);

            return newTexture;
        }
        catch (System.Exception e)
        {
            Debug.LogError($"创建Sprite纹理失败: {e.Message}");
            return null;
        }
    }

    void OnDestroy()
    {
        if (trailMaterial != null)
        {
            Destroy(trailMaterial.mainTexture);
            Destroy(trailMaterial);
        }
    }
}

public class AttractParticle : MonoBehaviour
{
    ParticleSystem CoolParticle;
    ParticleSystemForceField forcefield;

    public void Awake()
    {
        InitCoolParticle();
        InitForceField();
    }

    public void InitForceField()
    {
        forcefield = base.gameObject.AddComponent<ParticleSystemForceField>();
        forcefield.gravity = 2f;
        forcefield.endRange = 6f;
        forcefield.drag = 0.2f;
    }

    public void InitCoolParticle()
    {
        GameObject CoolParticleObject = new GameObject("Cool Particles");
        CoolParticle = CoolParticleObject.AddComponent<ParticleSystem>();
        CoolParticle.transform.parent = base.transform;

        CoolParticle.transform.localPosition = Vector3.zero;
        CoolParticle.transform.localEulerAngles = Vector3.zero;

        ParticleSystem.ShapeModule shape = CoolParticle.shape;
        shape.enabled = true;
        shape.shapeType = ParticleSystemShapeType.Circle;
        shape.scale = Vector3.one * 0.3f;
        shape.randomDirectionAmount = 1f;

        ParticleSystem.MainModule main = CoolParticle.main;
        main.startLifetime = new ParticleSystem.MinMaxCurve(0.1f, 0.2f);
        main.startSpeed = new ParticleSystem.MinMaxCurve(0.2f, 0.6f);

        ParticleSystem.EmissionModule emission = CoolParticle.emission;
        emission.enabled = true;
        emission.rateOverTime = 256f;

        ParticleSystem.TrailModule trails = CoolParticle.trails;
        trails.enabled = true;
        trails.lifetime = new ParticleSystem.MinMaxCurve(0.3f, 0.4f);
        trails.ratio = 0.3f;
        trails.minVertexDistance = 0.05f;
        trails.sizeAffectsWidth = false;
        trails.colorOverTrail = new Color(0.08f, 0.08f, 0.08f);

        AnimationCurve sizeCurve = new AnimationCurve();
        sizeCurve.AddKey(0.0f, 0f);
        sizeCurve.AddKey(0.8f, 1f);
        sizeCurve.AddKey(1f, 0f);

        trails.widthOverTrail = new ParticleSystem.MinMaxCurve(0.03f, sizeCurve);
        trails.inheritParticleColor = false;
        trails.dieWithParticles = false;

        ParticleSystem.ExternalForcesModule externalForces = CoolParticle.externalForces;
        externalForces.enabled = true;

        ParticleSystemRenderer renderer = CoolParticleObject.GetOrAddComponent<ParticleSystemRenderer>();
        renderer.renderMode = ParticleSystemRenderMode.None;
        renderer.sortingLayerName = "Bubbles";
        renderer.trailMaterial = ModAPI.FindMaterial("VeryBright");
    }

    public IEnumerator End()
    {
        ParticleSystem.EmissionModule emission = CoolParticle.emission;
        emission.enabled = false;

        yield return new WaitForSeconds(0.2f);

        Destroy(base.gameObject);
    }
}

[SkipSerialisation]
[RequireComponent(typeof(Image))]
public class AnimatedImage : MonoBehaviour
{
    public Sprite[] Sprites = new Sprite[] { };
    public Image Image;
    private int Local = 0;
    private float T = 0;
    public float TimeFrame = 0.125f;
    private void Start() { Image = gameObject.GetComponent<Image>(); }
    private void FixedUpdate()
    {
        T += Time.fixedDeltaTime;
        if (T > TimeFrame)
        {
            Local += Local >= Sprites.Count() - 1 ? -Local : 1;
            Image.sprite = Sprites[Local];
            T = 0;
        }
    }
}

public class VideoPlayerScript : MonoBehaviour
{
    private VideoPlayer videoPlayer;
    private Camera mainCamera;
    public string videoFilePath;

    public void Start()
    {
        mainCamera = GameObject.Find("Main Camera").GetComponent<Camera>();
        videoPlayer = mainCamera.gameObject.GetComponent<VideoPlayer>() ?? mainCamera.gameObject.AddComponent<VideoPlayer>();
        videoPlayer.playOnAwake = false;
        videoPlayer.renderMode = VideoRenderMode.CameraNearPlane;
        videoPlayer.url = "file://" + videoFilePath;
        videoPlayer.source = VideoSource.Url;
        videoPlayer.targetCamera = mainCamera;
        videoPlayer.audioOutputMode = VideoAudioOutputMode.Direct;
        videoPlayer.isLooping = true;
        videoPlayer.Play();
        StartCoroutine(CheckVideoState());
    }

    public IEnumerator CheckVideoState()
    {
        if (!videoPlayer.isPlaying && GameObject.Find("WORLD") == null)
        {
            videoPlayer.url = "file://" + videoFilePath;
            videoPlayer.Play();
        }
        else
            videoPlayer.Stop();
        yield return new WaitForSeconds(1);
    }

    private void OnDisable()
    {
        Destroy(gameObject);
        var newVideoPlayer = new GameObject("NewVideoPlayer");
        newVideoPlayer.AddComponent<VideoPlayerScript>().videoFilePath = videoFilePath;
    }
}

// ── Stats ──

public class StatManager : MonoBehaviour
{
    private static StatManager m_instance;
    public static StatManager Instance
    {
        get
        {
            if (m_instance == null)
                m_instance = new GameObject("StatManagerInstance", typeof(StatManager)) { hideFlags = HideFlags.HideAndDontSave }.GetComponent<StatManager>();
            return m_instance;
        }
    }

    public class CustomStatInfo
    {
        public string StatID;
        public string DisplayName;
        public Sprite Icon;
    }

    private Dictionary<string, CustomStatInfo> _registeredStats = new Dictionary<string, CustomStatInfo>();

    private Type _managerType;
    private FieldInfo _statsStaticField;
    private FieldInfo _dictInternalField;

    private const string TARGET_SCENE = "Main";
    private const string UI_PATH = "Canvas/Pause screen/Stats/body/Scroll View/Viewport/Content";
    private bool _isInitialized = false;

    private void Awake()
    {
        if (m_instance != null && m_instance != this)
        {
            Destroy(gameObject);
            return;
        }
        m_instance = this;
        DontDestroyOnLoad(gameObject);

        InitReflection();
        SceneManager.sceneLoaded += OnSceneLoaded;
    }

    private void InitReflection()
    {
        if (_isInitialized) return;

        try
        {
            _managerType = Type.GetType("NonSteamStatManager, Assembly-CSharp");
            if (_managerType == null) return;

            _statsStaticField = _managerType.GetField("Stats", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            Type collectionType = _statsStaticField.FieldType;
            _dictInternalField = collectionType.GetField("Stats", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

        }
        catch
        {
        }
    }

    private Dictionary<string, float> GetCurrentDict()
    {
        if (_statsStaticField == null || _dictInternalField == null)
            InitReflection();

        object currentCollection = _statsStaticField?.GetValue(null);
        if (currentCollection == null) return null;

        return _dictInternalField?.GetValue(currentCollection) as Dictionary<string, float>;
    }

    public void RegisterStat(string statID, string displayName, Sprite icon = null)
    {
        if (!_registeredStats.ContainsKey(statID))
        {
            _registeredStats.Add(statID, new CustomStatInfo
            {
                StatID = statID,
                DisplayName = displayName,
                Icon = icon
            });
        }
        else
        {
            _registeredStats[statID].DisplayName = displayName;
            _registeredStats[statID].Icon = icon;
        }
    }

    private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        if (scene.name == TARGET_SCENE)
        {
            TryInjectCustomStats();
        }
    }

    public void TryInjectCustomStats()
    {
        GameObject contentObj = GameObject.Find(UI_PATH);
        if (contentObj == null || contentObj.transform.childCount == 0)
            return;

        foreach (var stat in _registeredStats.Values)
            CreateStatUI(contentObj.transform, stat.StatID, stat.DisplayName, stat.Icon);
    }

    private void CreateStatUI(Transform parent, string statID, string statName, Sprite icon)
    {
        if (parent.Find($"StatView_{statID}") != null) return;

        GameObject template = parent.GetChild(0).gameObject;
        bool originalState = template.activeSelf;
        template.SetActive(false);

        GameObject clone = Instantiate(template, parent);
        clone.name = $"StatView_{statID}";
        template.SetActive(originalState);

        if (!HasStat(statID)) SetStat(statID, 0f);

        var behaviour = clone.GetComponent<UIStatViewBehaviour>();
        if (behaviour != null)
        {
            behaviour.StatID = statID;
            behaviour.StatName = statName;
            behaviour.Icon = icon;
            behaviour.GetFromSteam = false;
            behaviour.RoundToInt = true;

            if (behaviour.NameDisplay != null)
                behaviour.NameDisplay.text = statName;
            if (behaviour.IconImage != null && icon != null)
                behaviour.IconImage.sprite = icon;
            if (behaviour.ValueDisplay != null)
                behaviour.ValueDisplay.text = GetStat(statID).ToString();
        }

        clone.SetActive(true);
        clone.transform.SetAsLastSibling();
    }

    public void Increment(string key, float delta = 1f)
    {
        var dict = GetCurrentDict();
        if (dict == null) return;

        if (dict.ContainsKey(key))
            dict[key] += delta;
        else
            dict[key] = delta;
    }

    public void SetStat(string key, float value)
    {
        var dict = GetCurrentDict();
        if (dict == null) return;

        dict[key] = value;
    }

    public float GetStat(string key, float fallback = 0f)
    {
        var dict = GetCurrentDict();
        if (dict != null && dict.TryGetValue(key, out float val))
            return val;
        return fallback;
    }

    public bool HasStat(string key)
    {
        var dict = GetCurrentDict();
        return dict != null && dict.ContainsKey(key);
    }

    public bool Remove(string key)
    {
        var dict = GetCurrentDict();
        if (dict == null) return false;
        bool removed = dict.Remove(key);
        if (removed)
        {
            GameObject uiItem = GameObject.Find($"{UI_PATH}/StatView_{key}");
            if (uiItem != null)
                Destroy(uiItem);
        }
        return removed;
    }

    private void OnDisable()
    {
        SceneManager.sceneLoaded -= OnSceneLoaded;
    }

    private void OnDestroy()
    {
        if (m_instance == this) m_instance = null;
    }
}

// ── Cape physics compute jobs ──

[Serializable]
public struct CapeBoneWeight
{
    public float4 Weights;
    public int4 Indices;
}

public struct BoundaryMaskJob : IJobParallelFor
{
    [ReadOnly] public NativeArray<byte> Alpha;
    [WriteOnly] public NativeArray<byte> Boundary;
    public int Width;
    public int Height;
    public byte Threshold;

    public void Execute(int index)
    {
        int x = index % Width;
        int y = index / Width;
        byte center = Alpha[index];
        Boundary[index] = 0;
        if (center <= Threshold) return;

        for (int dy = -1; dy <= 1; dy++)
        {
            for (int dx = -1; dx <= 1; dx++)
            {
                if (dx == 0 && dy == 0) continue;
                int nx = x + dx, ny = y + dy;
                if (nx < 0 || nx >= Width || ny < 0 || ny >= Height) continue;
                if (Alpha[ny * Width + nx] <= Threshold) { Boundary[index] = 1; return; }
            }
        }
    }
}

public struct PointInsideJob : IJobParallelFor
{
    [ReadOnly] public NativeArray<float2> Contour;
    [ReadOnly] public NativeArray<float2> Points;
    [WriteOnly] public NativeArray<byte> Inside;
    public float EdgeToleranceSq;

    public void Execute(int index)
    {
        float2 p = Points[index];
        int count = Contour.Length;
        bool inside = false;
        for (int i = 0, j = count - 1; i < count; j = i++)
        {
            float2 a = Contour[i];
            float2 b = Contour[j];
            if ((a.y > p.y) != (b.y > p.y))
            {
                float intersectX = (b.x - a.x) * (p.y - a.y) / (b.y - a.y) + a.x;
                if (p.x < intersectX) inside = !inside;
            }
        }

        if (!inside)
        {
            Inside[index] = 0;
            return;
        }

        float minDistSq = float.MaxValue;
        for (int i = 0; i < count; i++)
        {
            float2 a = Contour[i];
            float2 b = Contour[(i + 1) % count];
            float2 ab = b - a;
            float t = math.dot(p - a, ab) / math.dot(ab, ab);
            t = math.clamp(t, 0f, 1f);
            float2 closest = a + t * ab;
            float distSq = math.distancesq(p, closest);
            minDistSq = math.min(minDistSq, distSq);
        }
        Inside[index] = (byte)(minDistSq > EdgeToleranceSq ? 1 : 0);
    }
}

public struct KMeansJob : IJob
{
    [ReadOnly] public NativeArray<float2> Points;
    public NativeArray<float2> Centers;
    public NativeArray<float2> Sums;
    public NativeArray<int> Counts;
    public int Iterations;

    public void Execute()
    {
        int k = Centers.Length;
        int n = Points.Length;
        for (int iter = 0; iter < Iterations; iter++)
        {
            for (int j = 0; j < k; j++) { Sums[j] = float2.zero; Counts[j] = 0; }
            for (int i = 0; i < n; i++)
            {
                float2 p = Points[i];
                int best = 0;
                float bestDist = math.distancesq(p, Centers[0]);
                for (int j = 1; j < k; j++)
                {
                    float d = math.distancesq(p, Centers[j]);
                    if (d < bestDist) { bestDist = d; best = j; }
                }
                Sums[best] += p;
                Counts[best]++;
            }
            for (int j = 0; j < k; j++)
                if (Counts[j] > 0)
                    Centers[j] = Sums[j] / Counts[j];
        }
    }
}

public struct BoneWeightJob : IJobParallelFor
{
    [ReadOnly] public NativeArray<float3> Vertices;
    [ReadOnly] public NativeArray<float2> BonePositions;
    [WriteOnly] public NativeArray<CapeBoneWeight> Output;

    public void Execute(int index)
    {
        float3 v = Vertices[index];
        float2 v2 = new float2(v.x, v.y);
        int boneCount = BonePositions.Length;
        float best0 = float.MaxValue, best1 = float.MaxValue, best2 = float.MaxValue, best3 = float.MaxValue;
        int idx0 = 0, idx1 = 0, idx2 = 0, idx3 = 0;

        for (int j = 0; j < boneCount; j++)
        {
            float d = math.distancesq(v2, BonePositions[j]);
            if (d < best0) { best3 = best2; idx3 = idx2; best2 = best1; idx2 = idx1; best1 = best0; idx1 = idx0; best0 = d; idx0 = j; }
            else if (d < best1) { best3 = best2; idx3 = idx2; best2 = best1; idx2 = idx1; best1 = d; idx1 = j; }
            else if (d < best2) { best3 = best2; idx3 = idx2; best2 = d; idx2 = j; }
            else if (d < best3) { best3 = d; idx3 = j; }
        }

        float total = best0 + best1 + best2 + best3;
        if (total < 0.0001f) total = 1f;
        Output[index] = new CapeBoneWeight
        {
            Weights = new float4(best0 / total, best1 / total, best2 / total, best3 / total),
            Indices = new int4(idx0, idx1, idx2, idx3)
        };
    }
}

public struct SkinningJob : IJobParallelFor
{
    [ReadOnly] public NativeArray<float3> BindVertices;
    [ReadOnly] public NativeArray<CapeBoneWeight> Weights;
    [ReadOnly] public NativeArray<float4x4> BoneMatrices;
    [WriteOnly] public NativeArray<float3> Output;

    public void Execute(int index)
    {
        var w = Weights[index];
        float3 v = BindVertices[index];
        float4 p = new float4(v.x, v.y, v.z, 1f);
        float3 result = float3.zero;
        result += (math.mul(BoneMatrices[w.Indices.x], p) * w.Weights.x).xyz;
        result += (math.mul(BoneMatrices[w.Indices.y], p) * w.Weights.y).xyz;
        result += (math.mul(BoneMatrices[w.Indices.z], p) * w.Weights.z).xyz;
        result += (math.mul(BoneMatrices[w.Indices.w], p) * w.Weights.w).xyz;
        Output[index] = result;
    }
}

public struct NativeList<T> : IDisposable where T : struct
{
    private List<T> _list;

    public NativeList(int capacity, Allocator _)
    {
        _list = new List<T>(capacity);
    }

    public NativeList(Allocator _)
    {
        _list = new List<T>();
    }

    public int Length => _list != null ? _list.Count : 0;
    public bool IsCreated => _list != null;

    public T this[int index]
    {
        get => _list[index];
        set => _list[index] = value;
    }

    public void Add(T item) => _list.Add(item);

    public void RemoveAt(int index) => _list.RemoveAt(index);

    public void Dispose()
    {
        _list = null;
    }

    public NativeArray<T> AsArray()
    {
        var arr = new NativeArray<T>(_list.Count, Allocator.Temp);
        for (int i = 0; i < _list.Count; i++)
            arr[i] = _list[i];
        return arr;
    }
}

public class NewFreezeBehaviour : MonoBehaviour
{
    private Rigidbody2D rb;

    private Vector2 velocity;
    private float angularVelocity;

    private void Awake()
    {
        rb = GetComponent<Rigidbody2D>();
        velocity = rb.velocity;
        angularVelocity = rb.angularVelocity;
        rb.bodyType = RigidbodyType2D.Static;
    }

    private void Stop()
    {
        if ((bool)rb)
        {
            rb.bodyType = RigidbodyType2D.Dynamic;
            rb.velocity = velocity;
            rb.angularVelocity = angularVelocity;
            this.Destroy();
        }
    }

    private void OnDestroy() => OnDisable();
    private void OnDisable() => Stop();
}

public class PID
{
    public float Kp, Ki, Kd;
    private float lastError;
    private float P, I, D;
    public PID() { Kp = 1f; Ki = 0; Kd = 0.2f; }
    public PID(float pFactor, float iFactor, float dFactor) { this.Kp = pFactor; this.Ki = iFactor; this.Kd = dFactor; }
    public float Update(float error, float dt)
    {
        P = error;
        I += error * dt;
        D = (error - lastError) / dt;
        lastError = error;
        float CO = P * Kp + I * Ki + D * Kd;
        return CO;
    }
}

// ── Utility partial ──
public static partial class Utility
{
    public static void CreatePlayer(string url) => new GameObject(GenerateRandomString(5)).AddComponent<VideoPlayerScript>().videoFilePath = url;
}