using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.EventSystems;
using UnityEngine.Rendering;
using UnityEngine.UI;
using FishUtility;

namespace FishUtility;

[DisallowMultipleComponent]
public class Preserver : MonoBehaviour
{
    public bool Conducting;
    public void Start()
    {
        var phys = gameObject.GetPhysicalBehaviour();
        var newProp = Instantiate(phys.Properties);
        newProp.Conducting = Conducting;
        phys.Properties = newProp;
    }
}

[RequireComponent(typeof(RectTransform))]
public class CustomTooltipController : MonoBehaviour
{
    public static CustomTooltipController Instance { get; private set; }

    private Canvas _parentCanvas;
    private RectTransform _rectTransform;
    private RectTransform _canvasRect;
    private CanvasGroup _canvasGroup;

    public Image backgroundImage;
    public Image glowImage;
    public Image borderImage;
    public TextMeshProUGUI nameText;
    public TextMeshProUGUI descText;

    private Vector2 _targetSize;
    private Vector2 _targetPosition;
    private Vector2 _targetNamePos;
    private Vector2 _targetDescPos;
    private bool _isVisible;

    private bool _hasCustomBgImage = false;
    private Vector2 _customBgImageSize = Vector2.zero;

    [Header("Appearance")]
    public int CornerRadius = 16;
    public float GlobalScale = 0.65f;
    public Color GlowColor = new Color(0.2f, 0.6f, 1f, 0.5f);

    private const float MAX_WIDTH = 650f;
    private const float PADDING_X = 24f;
    private const float PADDING_Y = 24f;
    private const float SPACING = 16f;
    private const int BORDER_THICKNESS = 4;
    private const float MOUSE_OFFSET_X = 20f;
    private const float MOUSE_OFFSET_Y = -20f;

    private const float GLOW_SPEED = 3.0f;
    private const float GLOW_MIN_ALPHA = 0.2f;
    private const float GLOW_MAX_ALPHA = 0.7f;
    private const float GLOW_EXPAND = 1.0f;

    private const float ALPHA_SPEED = 20f;
    private const float POS_SPEED = 18f;
    private const float SIZE_SPEED = 14f;

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }
        Instance = this;

        _parentCanvas = GetComponentInParent<Canvas>();
        _canvasRect = _parentCanvas.GetComponent<RectTransform>();
        SetupUI();
    }

    private void SetupUI()
    {
        _rectTransform = GetComponent<RectTransform>();

        var canvas = gameObject.GetComponent<Canvas>() ?? gameObject.AddComponent<Canvas>();
        canvas.overrideSorting = true;
        canvas.sortingOrder = 9999;

        if (gameObject.GetComponent<GraphicRaycaster>() == null) gameObject.AddComponent<GraphicRaycaster>();

        _rectTransform.pivot = new Vector2(0, 1);
        _rectTransform.anchorMin = new Vector2(0.5f, 0.5f);
        _rectTransform.anchorMax = new Vector2(0.5f, 0.5f);

        _canvasGroup = gameObject.GetComponent<CanvasGroup>() ?? gameObject.AddComponent<CanvasGroup>();
        _canvasGroup.alpha = 0f;
        _canvasGroup.blocksRaycasts = false;

        int blurSize = 20;
        Sprite glowSprite = GenerateGlowSprite(CornerRadius, blurSize);
        Sprite maskSprite = GenerateRoundedRectSprite(CornerRadius, 0, Color.white, Color.clear, true);
        Sprite borderSprite = GenerateRoundedRectSprite(CornerRadius, BORDER_THICKNESS, Color.clear, Color.white, false);

        var glowObj = new GameObject("GlowLayer");
        var glowRect = glowObj.AddComponent<RectTransform>();
        glowRect.SetParent(_rectTransform, false);
        glowRect.anchorMin = Vector2.zero;
        glowRect.anchorMax = Vector2.one;
        glowRect.offsetMin = new Vector2(-blurSize, -blurSize);
        glowRect.offsetMax = new Vector2(blurSize, blurSize);
        glowImage = glowObj.AddComponent<Image>();
        glowImage.type = Image.Type.Sliced;
        glowImage.sprite = glowSprite;
        glowImage.color = GlowColor;
        glowImage.raycastTarget = false;

        var maskObj = new GameObject("MaskLayer");
        var maskRect = maskObj.AddComponent<RectTransform>();
        maskRect.SetParent(_rectTransform, false);
        maskRect.anchorMin = Vector2.zero;
        maskRect.anchorMax = Vector2.one;
        maskRect.sizeDelta = Vector2.zero;
        var maskImage = maskObj.AddComponent<Image>();
        maskImage.type = Image.Type.Sliced;
        maskImage.sprite = maskSprite;
        var maskComp = maskObj.AddComponent<Mask>();
        maskComp.showMaskGraphic = false;

        var bgObj = new GameObject("Background");
        var bgRect = bgObj.AddComponent<RectTransform>();
        bgRect.SetParent(maskRect, false);
        bgRect.anchorMin = Vector2.zero;
        bgRect.anchorMax = Vector2.one;
        bgRect.sizeDelta = Vector2.zero;
        backgroundImage = bgObj.AddComponent<Image>();
        backgroundImage.type = Image.Type.Sliced;
        backgroundImage.color = new Color(0.05f, 0.05f, 0.05f, 0.95f);
        backgroundImage.raycastTarget = false;

        var nameObj = new GameObject("NameText");
        var nameRect = nameObj.AddComponent<RectTransform>();
        nameRect.SetParent(maskRect, false);
        nameRect.anchorMin = new Vector2(0, 1);
        nameRect.anchorMax = new Vector2(0, 1);
        nameRect.pivot = new Vector2(0.5f, 1);
        nameText = nameObj.AddComponent<TextMeshProUGUI>();
        nameText.alignment = TextAlignmentOptions.Top;
        nameText.fontSize = 40;
        nameText.color = Color.white;
        nameText.fontStyle = FontStyles.Bold;
        nameText.richText = true;
        nameText.enableWordWrapping = true;
        nameText.overflowMode = TextOverflowModes.Truncate;
        nameText.raycastTarget = false;

        var descObj = new GameObject("DescText");
        var descRect = descObj.AddComponent<RectTransform>();
        descRect.SetParent(maskRect, false);
        descRect.anchorMin = new Vector2(0, 1);
        descRect.anchorMax = new Vector2(0, 1);
        descRect.pivot = new Vector2(0, 1);
        descText = descObj.AddComponent<TextMeshProUGUI>();
        descText.alignment = TextAlignmentOptions.TopLeft;
        descText.fontSize = 24;
        descText.color = new Color(0.9f, 0.9f, 0.9f, 1f);
        descText.enableWordWrapping = true;
        descText.richText = true;
        descText.overflowMode = TextOverflowModes.Truncate;
        descText.raycastTarget = false;

        var borderObj = new GameObject("Border");
        var borderRect = borderObj.AddComponent<RectTransform>();
        borderRect.SetParent(_rectTransform, false);
        borderRect.anchorMin = Vector2.zero;
        borderRect.anchorMax = Vector2.one;
        borderRect.sizeDelta = Vector2.zero;
        borderImage = borderObj.AddComponent<Image>();
        borderImage.type = Image.Type.Sliced;
        borderImage.sprite = borderSprite;
        borderImage.color = new Color(0.7f, 0.7f, 0.7f, 1f);
        borderImage.raycastTarget = false;
    }

    private Sprite GenerateGlowSprite(int radius, int blurSize)
    {
        int size = (radius + blurSize) * 2 + 2;
        Texture2D tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
        Color[] pixels = new Color[size * size];

        float center = (size - 1) / 2f;

        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                float dx = Mathf.Max(0, Mathf.Abs(x - center) - 0.5f);
                float dy = Mathf.Max(0, Mathf.Abs(y - center) - 0.5f);
                float dist = Mathf.Sqrt(dx * dx + dy * dy);

                float alpha = 0f;
                if (dist > radius && dist <= radius + blurSize)
                {
                    float glowDist = dist - radius;
                    alpha = Mathf.Pow(1.0f - (glowDist / blurSize), 2.2f);
                }
                pixels[y * size + x] = new Color(1, 1, 1, alpha);
            }
        }
        tex.SetPixels(pixels); tex.Apply();

        int border = radius + blurSize;
        return Sprite.Create(tex, new Rect(0, 0, size, size), Vector2.one * 0.5f, 100, 0, SpriteMeshType.FullRect, new Vector4(border, border, border, border));
    }

    private Sprite GenerateRoundedRectSprite(int radius, int borderThickness, Color fillColor, Color borderColor, bool isMask)
    {
        int padding = 2;
        int size = (radius + padding) * 2 + 2;
        Texture2D tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
        Color[] pixels = new Color[size * size];
        float center = (size - 1) / 2f;

        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                float dx = Mathf.Max(0, Mathf.Abs(x - center) - 0.5f);
                float dy = Mathf.Max(0, Mathf.Abs(y - center) - 0.5f);
                float dist = Mathf.Sqrt(dx * dx + dy * dy);

                Color col = Color.clear;
                if (isMask)
                {
                    col = dist <= radius - 0.5f ? fillColor : Color.clear;
                }
                else
                {
                    if (dist <= radius)
                    {
                        col = (borderThickness > 0 && dist >= radius - borderThickness) ? borderColor : fillColor;
                    }
                    else if (dist < radius + 1)
                    {
                        float aa = 1f - (dist - radius);
                        col = (borderThickness > 0) ? borderColor : fillColor;
                        col.a *= aa;
                    }
                }
                pixels[y * size + x] = col;
            }
        }
        tex.SetPixels(pixels); tex.Apply();

        int border = radius + padding;
        return Sprite.Create(tex, new Rect(0, 0, size, size), Vector2.one * 0.5f, 100, 0, SpriteMeshType.FullRect, new Vector4(border, border, border, border));
    }

    public void SetUserSettings(Sprite customBgSprite, Color customGlowColor, float scale = 1.0f)
    {
        GlobalScale = scale;
        _rectTransform.localScale = new Vector3(GlobalScale, GlobalScale, 1f);
        GlowColor = customGlowColor;
        glowImage.color = GlowColor;

        if (customBgSprite != null)
        {
            backgroundImage.sprite = customBgSprite;
            _hasCustomBgImage = true;
            _customBgImageSize = new Vector2(customBgSprite.rect.width, customBgSprite.rect.height);
        }
    }

    public void Show(string rawText)
    {
        ParseText(rawText, out string nameStr, out string descStr);
        nameText.text = nameStr;
        descText.text = descStr;

        bool hasName = !string.IsNullOrEmpty(nameStr);
        bool hasDesc = !string.IsNullOrEmpty(descStr);
        nameText.gameObject.SetActive(hasName);
        descText.gameObject.SetActive(hasDesc);

        CalculateLayout(hasName, hasDesc);

        if (!_isVisible || _canvasGroup.alpha < 0.01f)
            UpdatePosition(true);

        _isVisible = true;
        _rectTransform.SetAsLastSibling();
    }

    public void Hide()
    {
        _isVisible = false;
        nameText.gameObject.SetActive(_isVisible);
        descText.gameObject.SetActive(_isVisible);
    }

    private void ParseText(string rawText, out string nameStr, out string descStr)
    {
        nameStr = string.Empty; descStr = string.Empty;
        if (string.IsNullOrEmpty(rawText)) return;
        int newlineIdx = rawText.IndexOf('\n');
        if (newlineIdx >= 0)
        {
            nameStr = rawText.Substring(0, newlineIdx).Trim();
            descStr = rawText.Substring(newlineIdx + 1).Trim();
        }
        else descStr = rawText.Trim();
    }

    private void CalculateLayout(bool hasName, bool hasDesc)
    {
        float boxWidth = 0f, boxHeight = 0f;
        float maxTextWidth = MAX_WIDTH - (PADDING_X * 2);

        if (_hasCustomBgImage && _customBgImageSize.x > 0)
        {
            boxWidth = MAX_WIDTH;
            boxHeight = MAX_WIDTH * (_customBgImageSize.y / _customBgImageSize.x);
        }

        Vector2 namePref = hasName ? nameText.GetPreferredValues(nameText.text, maxTextWidth, Mathf.Infinity) : Vector2.zero;
        Vector2 descPref = hasDesc ? descText.GetPreferredValues(descText.text, maxTextWidth, Mathf.Infinity) : Vector2.zero;

        if (!_hasCustomBgImage)
        {
            float contentWidth = Mathf.Max(Mathf.Min(namePref.x, maxTextWidth), Mathf.Min(descPref.x, maxTextWidth));
            boxWidth = contentWidth + (PADDING_X * 2);
            boxHeight = PADDING_Y * 2 + (hasName ? namePref.y : 0) + (hasDesc ? descPref.y : 0) + (hasName && hasDesc ? SPACING : 0);
        }

        _targetSize = new Vector2(boxWidth, boxHeight);
        if (hasName)
        {
            nameText.rectTransform.sizeDelta = new Vector2(maxTextWidth, namePref.y);
            _targetNamePos = new Vector2(boxWidth / 2f, -PADDING_Y);
        }
        if (hasDesc)
        {
            descText.rectTransform.sizeDelta = new Vector2(maxTextWidth, descPref.y);
            _targetDescPos = new Vector2(PADDING_X, -PADDING_Y - (hasName ? (namePref.y + SPACING) : 0));
        }
    }

    private void Update()
    {
        UpdatePosition(false);
        UpdateAnimations();
    }

    private void UpdatePosition(bool forceSnap)
    {
        if (!_isVisible && _canvasGroup.alpha <= 0.001f) return;

        Camera cam = _parentCanvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : _parentCanvas.worldCamera;
        RectTransformUtility.ScreenPointToLocalPointInRectangle(_canvasRect, Input.mousePosition, cam, out Vector2 localPos);

        float canvasScale = _parentCanvas.scaleFactor == 0 ? 1f : _parentCanvas.scaleFactor;
        float currentScale = canvasScale * GlobalScale;

        float pivotX = (Input.mousePosition.x + (_targetSize.x * currentScale) + 20f * canvasScale) > Screen.width ? 1f : 0f;
        float pivotY = (Input.mousePosition.y - (_targetSize.y * currentScale) - 20f * canvasScale) < 0 ? 0f : 1f;

        Vector2 newPivot = new Vector2(pivotX, pivotY);
        if (_rectTransform.pivot != newPivot)
        {
            _rectTransform.pivot = newPivot;
            forceSnap = true;
        }

        float offsetX = pivotX == 1f ? -MOUSE_OFFSET_X : MOUSE_OFFSET_X;
        float offsetY = pivotY == 0f ? -MOUSE_OFFSET_Y : MOUSE_OFFSET_Y;
        _targetPosition = localPos + new Vector2(offsetX, offsetY);

        if (forceSnap) _rectTransform.anchoredPosition = _targetPosition;
    }

    private void UpdateAnimations()
    {
        float udt = Time.unscaledDeltaTime;
        float ut = Time.unscaledTime;
        _canvasGroup.alpha = Mathf.Lerp(_canvasGroup.alpha, _isVisible ? 1f : 0f, udt * ALPHA_SPEED);

        if (_canvasGroup.alpha > 0.001f)
        {
            _rectTransform.anchoredPosition = Vector2.Lerp(_rectTransform.anchoredPosition, _targetPosition, udt * POS_SPEED);
            _rectTransform.sizeDelta = Vector2.Lerp(_rectTransform.sizeDelta, _targetSize, udt * SIZE_SPEED);

            float wave = (Mathf.Sin(ut * GLOW_SPEED) + 1f) * 0.5f;
            glowImage.color = new Color(GlowColor.r, GlowColor.g, GlowColor.b, Mathf.Lerp(GLOW_MIN_ALPHA, GLOW_MAX_ALPHA, wave) * _canvasGroup.alpha);

            glowImage.rectTransform.localScale = Vector3.one * Mathf.Lerp(1.0f, GLOW_EXPAND, wave);

            if (nameText.gameObject.activeSelf)
                nameText.rectTransform.anchoredPosition = Vector2.Lerp(nameText.rectTransform.anchoredPosition, new Vector2(_rectTransform.sizeDelta.x / 2f, _targetNamePos.y), udt * SIZE_SPEED);

            if (descText.gameObject.activeSelf)
                descText.rectTransform.anchoredPosition = Vector2.Lerp(descText.rectTransform.anchoredPosition, _targetDescPos, udt * SIZE_SPEED);
        }
    }
}

public class MaterialPropertiesMenu : MonoBehaviour
{
    private int currentPage = 0, propertiesPerPage = 5;
    public static MaterialPropertiesMenu MaterialMenu { get; private set; }
    private void Awake()
    {
        MaterialMenu = this;
        DontDestroyOnLoad(gameObject);
    }

    public void ShowHierarchySpriteRenderers(GameObject rootObject)
    {
        var spriteRenderers = new List<(string path, SpriteRenderer sr)>();
        void CollectSpriteRenderers(GameObject obj, string path)
        {
            var sr = obj.GetComponent<SpriteRenderer>();
            if (sr != null && sr.material != null)
                spriteRenderers.Add((string.IsNullOrEmpty(path) ? obj.name : $"{path}/{obj.name}", sr));
            foreach (Transform child in obj.transform)
                CollectSpriteRenderers(child.gameObject, string.IsNullOrEmpty(path) ? obj.name : $"{path}/{obj.name}");
        }
        CollectSpriteRenderers(rootObject, "");
        if (spriteRenderers.Count == 0)
        {
            DialogBoxManager.Dialog("Sprite Renderer Properties",
                new DialogButton("No SpriteRenderers found", false),
                new DialogButton("Close", true, () => currentPage = 0));
            return;
        }
        int totalPages = (spriteRenderers.Count + propertiesPerPage - 1) / propertiesPerPage;
        var buttons = new List<DialogButton>();
        int start = currentPage * propertiesPerPage, end = Math.Min(start + propertiesPerPage, spriteRenderers.Count);
        for (int i = start; i < end; i++)
        {
            var (p, sr) = spriteRenderers[i];
            buttons.Add(new DialogButton($"{p}", true, () => ShowMaterialPropertiesMenu(sr.material, rootObject)));
        }
        if (currentPage > 0)
            buttons.Add(new DialogButton("Previous", true, () => { currentPage--; ShowHierarchySpriteRenderers(rootObject); }));
        if (currentPage < totalPages - 1)
            buttons.Add(new DialogButton("Next", true, () => { currentPage++; ShowHierarchySpriteRenderers(rootObject); }));
        buttons.Add(new DialogButton("Close", true, () => currentPage = 0));

        var dialog = DialogBoxManager.Dialog($"Sprite Renderers in {rootObject.name}", buttons.ToArray());

        var layout = dialog.DialogButtonHolder.gameObject;
        if (layout.TryGetComponent(out HorizontalLayoutGroup hlg)) UnityEngine.Object.DestroyImmediate(hlg);
        if (layout.TryGetComponent(out VerticalLayoutGroup vlg)) UnityEngine.Object.DestroyImmediate(vlg);
        GridLayoutGroup grid = layout.AddComponent<GridLayoutGroup>();
        grid.cellSize = new Vector2(150, 150);
        grid.spacing = new Vector2(15, 15);
        grid.constraint = GridLayoutGroup.Constraint.FixedColumnCount;
        grid.constraintCount = 3;
        int rows = Mathf.CeilToInt(buttons.Count / 3f);
        layout.GetComponent<LayoutElement>().preferredHeight = rows * 150 + (rows - 1) * 15;

    }

    public void ShowMaterialPropertiesMenu(Material material, GameObject root = null)
    {
        var properties = GetMaterialProperties(material);
        int totalPages = (properties.Count + propertiesPerPage - 1) / propertiesPerPage;
        var buttons = new List<DialogButton>();
        int start = currentPage * propertiesPerPage, end = Math.Min(start + propertiesPerPage, properties.Count);
        for (int i = start; i < end; i++)
        {
            var prop = properties[i];
            var formatName = FormatPropertyName(prop.name);
            string text = "";
            switch (prop.type)
            {
                case ShaderPropertyType.Color:
                    {
                        string hex = ColorToHex(material.GetColor(prop.name));
                        text = $"{formatName} ({prop.type}): <b><color=#{hex}>VIEW</color></b>";
                        buttons.Add(new DialogButton(text, true, () => EditProperty(material, prop.name, prop.type)));
                        break;
                    }
                case ShaderPropertyType.Texture:
                    text = $"{formatName} ({prop.type}): {prop.value}";
                    buttons.Add(new DialogButton(text, false));
                    break;
                default:
                    text = $"{formatName} ({prop.type}): {prop.value}";
                    buttons.Add(new DialogButton(text, true, () => EditProperty(material, prop.name, prop.type)));
                    break;
            }
        }
        if (currentPage > 0)
            buttons.Add(new DialogButton("Previous", true, () => { currentPage--; ShowMaterialPropertiesMenu(material); }));
        if (currentPage < totalPages - 1)
            buttons.Add(new DialogButton("Next", true, () => { currentPage++; ShowMaterialPropertiesMenu(material); }));
        buttons.Add(new DialogButton("Close", true, () => { currentPage = 0; if (root) ShowHierarchySpriteRenderers(root); }));
        var dialog = DialogBoxManager.Dialog("Material Properties", buttons.ToArray());

        var layout = dialog.DialogButtonHolder.gameObject;
        if (layout.TryGetComponent(out HorizontalLayoutGroup hlg)) UnityEngine.Object.DestroyImmediate(hlg);
        if (layout.TryGetComponent(out VerticalLayoutGroup vlg)) UnityEngine.Object.DestroyImmediate(vlg);
        GridLayoutGroup grid = layout.AddComponent<GridLayoutGroup>();
        grid.cellSize = new Vector2(150, 150);
        grid.spacing = new Vector2(15, 15);
        grid.constraint = GridLayoutGroup.Constraint.FixedColumnCount;
        grid.constraintCount = 3;
        int rows = Mathf.CeilToInt(buttons.Count / 3f);
        layout.GetComponent<LayoutElement>().preferredHeight = rows * 150 + (rows - 1) * 15;

    }


    private string ColorToHex(Color c) => $"{(int)(c.r * 255):X2}{(int)(c.g * 255):X2}{(int)(c.b * 255):X2}";
    private void EditProperty(Material material, string propertyName, ShaderPropertyType propertyType)
    {
        string currentValue = "", formatName = FormatPropertyName(propertyName);
        switch (propertyType)
        {
            case ShaderPropertyType.Color:
                currentValue = ColorToSimpleString(material.GetColor(propertyName));
                break;
            case ShaderPropertyType.Vector:
                currentValue = VectorToSimpleString(material.GetVector(propertyName));
                break;
            case ShaderPropertyType.Float:
            case ShaderPropertyType.Range:
                currentValue = material.GetFloat(propertyName).ToString();
                break;
        }
        DialogBox dialog = null;
        dialog = DialogBoxManager.TextEntry($"Edit {formatName} ({propertyType})", currentValue,
            new DialogButton("OK", true, () =>
            {
                if (!string.IsNullOrEmpty(dialog.InputField.text))
                {
                    try
                    {
                        switch (propertyType)
                        {
                            case ShaderPropertyType.Color:
                                Color newColor = ParseSimpleColor(dialog.InputField.text);
                                material.SetColor(propertyName, newColor);
                                break;
                            case ShaderPropertyType.Vector:
                                Vector4 newVector = ParseSimpleVector(dialog.InputField.text);
                                material.SetVector(propertyName, newVector);
                                break;
                            case ShaderPropertyType.Float:
                            case ShaderPropertyType.Range:
                                float newFloat = float.Parse(dialog.InputField.text);
                                material.SetFloat(propertyName, newFloat);
                                break;
                        }
                        ShowMaterialPropertiesMenu(material);
                    }
                    catch (Exception ex)
                    {
                        Debug.LogError($"Failed to set property {formatName}: {ex.Message}");
                    }
                }
            }),
            new DialogButton("Cancel", true, () => ShowMaterialPropertiesMenu(material))
        );
        dialog.ShowTextBox = true;
    }

    private string ColorToSimpleString(Color c) => $"{c.r} {c.g} {c.b} {c.a}";
    private string VectorToSimpleString(Vector4 v) => $"{v.x} {v.y} {v.z} {v.w}";

    private Color ParseSimpleColor(string s)
    {
        var parts = s.Split(' ');
        if (parts.Length != 4)
            throw new FormatException("Color must have 4 values (r g b a) separated by spaces.");
        return new Color(float.Parse(parts[0].Trim()), float.Parse(parts[1].Trim()),
                         float.Parse(parts[2].Trim()), float.Parse(parts[3].Trim()));
    }

    private Vector4 ParseSimpleVector(string s)
    {
        var parts = s.Split(' ');
        if (parts.Length != 4)
            throw new FormatException("Vector must have 4 values (x y z w) separated by spaces.");
        return new Vector4(float.Parse(parts[0].Trim()), float.Parse(parts[1].Trim()),
                           float.Parse(parts[2].Trim()), float.Parse(parts[3].Trim()));
    }

    private string FormatPropertyName(string name)
    {
        if (string.IsNullOrEmpty(name))
            return "";
        name = name.TrimStart('_');
        if (name.Length == 0)
            return "";
        var sb = new StringBuilder();
        sb.Append(name[0]);
        for (int i = 1; i < name.Length; i++)
            sb.Append(char.IsUpper(name[i]) ? " " + char.ToLower(name[i]) : name[i].ToString());
        return sb.ToString();
    }

    public static List<(string name, ShaderPropertyType type, string value)> GetMaterialProperties(Material material)
    {
        var props = new List<(string, ShaderPropertyType, string)>();
        if (material == null) { props.Add(("Error", ShaderPropertyType.Float, "Material is null")); return props; }
        var shader = material.shader;
        if (shader == null) { props.Add(("Error", ShaderPropertyType.Float, "Shader is null")); return props; }
        int count = shader.GetPropertyCount();
        for (int i = 0; i < count; i++)
        {
            string n = shader.GetPropertyName(i);
            ShaderPropertyType t = shader.GetPropertyType(i);
            string val = "";
            switch (t)
            {
                case ShaderPropertyType.Color:
                    val = material.GetColor(n).ToString();
                    break;
                case ShaderPropertyType.Vector:
                    val = material.GetVector(n).ToString();
                    break;
                case ShaderPropertyType.Float:
                case ShaderPropertyType.Range:
                    val = material.GetFloat(n).ToString();
                    break;
                case ShaderPropertyType.Texture:
                    var tex = material.GetTexture(n);
                    val = tex != null ? tex.name : "null";
                    break;
                default:
                    val = "Unsupported type";
                    break;
            }
            props.Add((n, t, val));
        }
        return props;
    }
}

// ── Utility partial ──
public static partial class Utility
{
    private static MaterialPropertiesMenu m_menu;

    public static MaterialPropertiesMenu MaterialMenu
    {
        get
        {
            if (m_menu == null)
                m_menu = CreateHelper<MaterialPropertiesMenu>("MaterialMenu");
            return m_menu;
        }
    }

    public static string GetMaterialPropertiesString(Material material)
    {
        if (material == null)
            return "Material is null";
        Shader shader = material.shader;
        if (shader == null)
            return "Shader is null";
        StringBuilder sb = new StringBuilder();
        sb.AppendLine($"Material: {material.name}");
        sb.AppendLine($"Shader: {shader.name}");
        int propertyCount = shader.GetPropertyCount();
        for (int i = 0; i < propertyCount; i++)
        {
            string propertyName = shader.GetPropertyName(i);
            ShaderPropertyType propertyType = shader.GetPropertyType(i);
            string valueStr = "";
            switch (propertyType)
            {
                case ShaderPropertyType.Color:
                    valueStr = material.GetColor(propertyName).ToString();
                    break;
                case ShaderPropertyType.Vector:
                    valueStr = material.GetVector(propertyName).ToString();
                    break;
                case ShaderPropertyType.Float:
                case ShaderPropertyType.Range:
                    valueStr = material.GetFloat(propertyName).ToString();
                    break;
                case ShaderPropertyType.Texture:
                    valueStr = (material.GetTexture(propertyName)?.name) ?? "null";
                    break;
                default:
                    valueStr = "Unsupported type";
                    break;
            }
            sb.AppendLine($"{propertyName} ({propertyType}): {valueStr}");
        }
        return sb.ToString();
    }
}
