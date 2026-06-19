using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Events;
using FishUtility;

namespace FishUtility;

public class ExtendedDoGEffect : MonoBehaviour
{

    private void Start()
    {
        if (this.DoGMaterial != null)
        {
            Debug.Log("DoGMaterial successfully assigned.");
            return;
        }
        Debug.LogError("Failed to assign DoGMaterial.");
    }

    private void OnRenderImage(RenderTexture source, RenderTexture destination)
    {
        if (this.DoGMaterial != null)
        {
            this.UpdateBlurCenter();
            Graphics.Blit(source, destination, this.DoGMaterial);
            return;
        }
        Debug.LogWarning("DoGMaterial is null during OnRenderImage.");
        Graphics.Blit(source, destination);
    }

    private void UpdateBlurCenter()
    {
        if (this.mainCamera != null)
        {
            Vector3 vector = this.mainCamera.WorldToScreenPoint(this.targetPosition);
            Vector2 vector2 = new Vector2(vector.x / (float)Screen.width, vector.y / (float)Screen.height);
            this.DoGMaterial.SetVector("_BlurCenter", new Vector4(vector2.x, vector2.y, 0f, 0f));
            return;
        }
        Debug.LogError("mainCamera is null.");
    }

    public Material DoGMaterial;

    public Camera mainCamera;

    public Vector3 targetPosition;
}

public static class SliceEffectCache
{
    public static Material VeryBrightMaterial;
    public static Material SpritesDefaultMaterial;
    public static readonly WaitForEndOfFrame WaitForEndOfFrame = new WaitForEndOfFrame();
    private static readonly Dictionary<float, WaitForSeconds> _waitForSecondsCache = new Dictionary<float, WaitForSeconds>();

    public static void Initialize()
    {
        VeryBrightMaterial = ModAPI.FindMaterial("VeryBright");
        SpritesDefaultMaterial = ModAPI.FindMaterial("Sprites-Default");
    }

    public static WaitForSeconds GetWaitForSeconds(float seconds)
    {
        if (!_waitForSecondsCache.TryGetValue(seconds, out var waitInstruction))
        {
            waitInstruction = new WaitForSeconds(seconds);
            _waitForSecondsCache[seconds] = waitInstruction;
        }
        return waitInstruction;
    }
}

public class Effects
{
    public static (GameObject main, (LineRenderer line_white, LineRenderer line_black) line, Vector3 startPos, Vector3 endPos, Vector3 midPos) SliceEffect(Vector3 startPos, Vector3 endPos, Color outside, Color inside, int midPointsCount = 1, float width = 0.125f, int type = 1, float wait = 1f, float maxTime = 1f, float speed = 1f)
    {
        if (SliceEffectCache.VeryBrightMaterial == null) SliceEffectCache.Initialize();

        var lineParent = new GameObject("Line_SliceEffect");

        var whiteWidthCurve = new AnimationCurve(new Keyframe(0f, 0f), new Keyframe(0.5f, width), new Keyframe(1f, 0f));
        var blackWidthCurve = new AnimationCurve(new Keyframe(0f, 0f), new Keyframe(0.5f, width * 0.8f), new Keyframe(1f, 0f));
        int positionCount = midPointsCount + 2;

        var doom = new Material(SliceEffectCache.SpritesDefaultMaterial);
        doom.SetFloat("_GlobalScale", 25f / (width * 8f));
        var line_white = CreateLine(lineParent, "LineWhite", SliceEffectCache.VeryBrightMaterial, outside, whiteWidthCurve, positionCount, null);
        var line_black = CreateLine(lineParent, "LineBlack", doom, inside, blackWidthCurve, positionCount, line_white);

        var positions = new Vector3[positionCount];
        var initialPositions = new Vector3[positionCount];
        positions[0] = startPos;
        positions[positionCount - 1] = endPos;

        for (int i = 1; i <= midPointsCount; i++)
        {
            float t = (float)i / (midPointsCount + 1);
            positions[i] = Vector3.Lerp(startPos, endPos, t);
        }

        System.Array.Copy(positions, initialPositions, positionCount);

        line_white.SetPositions(positions);
        line_black.SetPositions(positions);

        Vector3 midPos = Vector3.zero;
        if (midPointsCount > 0)
        {
            for (int i = 1; i <= midPointsCount; i++)
            {
                midPos += positions[i];
            }
            midPos /= midPointsCount;
        }
        else
        {
            midPos = (startPos + endPos) / 2f;
        }

        Global.main.StartCoroutine(AnimateAndDestroy());

        IEnumerator AnimateAndDestroy()
        {
            yield return SliceEffectCache.GetWaitForSeconds(wait);

            Vector3 dir = (endPos - startPos).normalized * 20 * width;
            Vector3 targetPos;

            switch (type)
            {
                case 2:
                    targetPos = endPos + dir;
                    break;
                case 3: // Assuming a type 3 for collapsing to the start
                    targetPos = startPos - dir;
                    break;
                default: // case 1
                    targetPos = midPos;
                    break;
            }

            float time = 0f;
            while (time < maxTime)
            {
                // Normalize time and apply easing curve for a predictable animation
                float t = Mathf.Pow(time / maxTime, 3);

                for (int i = 0; i < positionCount; i++)
                {
                    positions[i] = Vector3.LerpUnclamped(initialPositions[i], targetPos, t);
                }

                line_white.SetPositions(positions);
                line_black.SetPositions(positions);

                time += Time.deltaTime * speed;
                yield return SliceEffectCache.WaitForEndOfFrame;
            }

            // Use Destroy instead of DestroyImmediate for runtime objects
            UnityEngine.Object.Destroy(lineParent);
        }

        return (lineParent, (line_white, line_black), startPos, endPos, midPos);
    }

    private static LineRenderer CreateLine(GameObject parent, string name, Material material, Color color, AnimationCurve widthCurve, int positionCount, LineRenderer prev)
    {
        var lineGO = new GameObject(name);
        lineGO.transform.SetParent(parent.transform);
        var line = lineGO.AddComponent<LineRenderer>();
        line.material = material;
        line.positionCount = positionCount;
        line.startColor = line.endColor = color;
        line.widthCurve = widthCurve;
        if (prev != null)
        {
            line.sortingLayerName = prev.sortingLayerName;
            line.sortingOrder = prev.sortingOrder + 1;
        }
        return line;
    }
}

public class ShockwaveController : MonoBehaviour
{
    [Header("Animation Settings")]
    public float duration = 0.5f;

    [Header("Radius (0 to 1 based on mesh UV)")]
    [Range(0f, 0.5f)] public float maxRadius = 0.48f;

    [Header("Width / Thickness (0 to 1)")]
    [Range(0f, 1f)] public float initialThickness = 0.01f;
    [Range(0f, 1f)] public float maxThickness = 0.12f;

    [Header("Distortion Amplitude")]
    public float maxDistortion = 0.08f;
    private Material targetMaterial;
    private float timer = 0f;

    private static readonly int RadiusID = Shader.PropertyToID("_Radius");
    private static readonly int WidthID = Shader.PropertyToID("_Width");
    private static readonly int AmplitudeID = Shader.PropertyToID("_Amplitude");

    private void Awake()
    {
        Renderer renderComponent = GetComponent<Renderer>();
        if (renderComponent != null)
        {
            targetMaterial = renderComponent.material;
        }
    }

    private void OnEnable()
    {
        timer = 0f;
    }

    private void Update()
    {
        if (targetMaterial == null) return;

        timer += Time.deltaTime;
        float t = Mathf.Clamp01(timer / duration);

        float radiusT = 1f - Mathf.Pow(1f - t, 5f);
        float currentRadius = Mathf.Lerp(0f, maxRadius, radiusT);

        float currentThickness = Mathf.Lerp(initialThickness, maxThickness, t);

        float distortionT = 1f - (t * t);
        float currentDistortion = maxDistortion * distortionT;

        targetMaterial.SetFloat(RadiusID, currentRadius);
        targetMaterial.SetFloat(WidthID, currentThickness);
        targetMaterial.SetFloat(AmplitudeID, currentDistortion);

        if (timer >= duration)
        {
            Destroy(gameObject);
        }
    }

    private void OnDestroy()
    {
        if (targetMaterial != null)
        {
            Destroy(targetMaterial);
        }
    }
}

public class BurnableSpriteObject : MonoBehaviour
{
    public static Material burnMaterial = ModAPI.FindSpawnable("Crate").Prefab.GetComponent<SpriteRenderer>().material;
    public PhysicalBehaviour referencePhys
    {
        get => m_referencePhys;
        set
        {
            if (value == null)
            {
                isReference = false;
                return;
            }
            isReference = true;
            m_referencePhys = value;
        }
    }
    [SerializeField]
    private PhysicalBehaviour m_referencePhys;
    [SerializeField]
    private bool isReference = false;
    public float BurnProgress
    {
        get => isReference ? referencePhys.BurnProgress : m_burnProgress;
        set
        {
            if (isReference) return;
            m_burnProgress = value;
        }
    }
    [SerializeField]
    private float m_burnProgress;
    private SpriteRenderer spriteRenderer;
    private void Start()
    {
        spriteRenderer = gameObject.GetComponent<SpriteRenderer>();
        spriteRenderer.material = burnMaterial;
    }
    private void Update() => spriteRenderer.material.SetFloat("_Progress", BurnProgress);
}

public static partial class Utility
{
    public static SpriteRenderer CreateBurnableSpriteObject(this PhysicalBehaviour physicalBehaviour, Sprite Sprite, int order = -999)
    {
        var renderer = CreateSpriteObject(physicalBehaviour.transform, new Vector3(0, 0, 0), new Vector3(1, 1, 1), Sprite);
        renderer.gameObject.GetOrAddComponent<BurnableSpriteObject>().referencePhys = physicalBehaviour;
        if (order != -999)
            renderer.sortingOrder = order;
        return renderer;
    }
}