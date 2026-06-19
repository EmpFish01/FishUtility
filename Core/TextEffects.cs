using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using TMPro;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.EventSystems;
using FishUtility;

// ── Data types ──
namespace FishUtility;

public class TextInfo
{
    public string text;
    public bool colorful;
    public int? length;
    public double compression = 0d;
    public Func<double> time;
    public Func<double, double> trans;
    public TextInfo(string text, bool colorful, int? length = null, Func<double> time = null, Func<double, double> trans = null, double compression = 0d)
    {
        this.text = text;
        this.colorful = colorful;
        this.length = length;
        this.time = time;
        this.trans = trans;
        this.compression = compression;
    }
}

public enum HighlightStyle
{
    SoftGlow,
    Metallic,
    NeonVibe
}

public enum EffectMode
{
    VerticalLine,
    RadialWave,
    Spiral
}

// ── Fast text rendering utilities ──

public static class DynamicTextEffectUtils
{
    private static readonly string[] HexTable;
    [ThreadStatic] private static StringBuilder _sharedSb;
    [ThreadStatic] private static char[] _numBuffer;

    static DynamicTextEffectUtils()
    {
        HexTable = new string[256];
        for (int i = 0; i < 256; i++) HexTable[i] = i.ToString("X2");
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static StringBuilder GetSharedSb(int capacity)
    {
        if (_sharedSb == null) _sharedSb = new StringBuilder(Math.Max(capacity, 1024));
        else { _sharedSb.Length = 0; if (_sharedSb.Capacity < capacity) _sharedSb.EnsureCapacity(capacity); }
        return _sharedSb;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void AppendColorHexFast(StringBuilder sb, float r, float g, float b)
    {
        int ri = (int)(r * 255f); if (ri < 0) ri = 0; else if (ri > 255) ri = 255;
        int gi = (int)(g * 255f); if (gi < 0) gi = 0; else if (gi > 255) gi = 255;
        int bi = (int)(b * 255f); if (bi < 0) bi = 0; else if (bi > 255) bi = 255;
        sb.Append(HexTable[ri]);
        sb.Append(HexTable[gi]);
        sb.Append(HexTable[bi]);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void AppendFloatFast(StringBuilder sb, double val, int precision)
    {
        if (double.IsNaN(val)) return;
        if (val < 0) { sb.Append('-'); val = -val; }

        val += 0.000001;
        int intPart = (int)val;
        AppendIntFast(sb, intPart);

        if (precision > 0)
        {
            sb.Append('.');
            double frac = val - intPart;
            for (int i = 0; i < precision; i++)
            {
                frac *= 10;
                int digit = (int)frac;
                sb.Append((char)('0' + digit));
                frac -= digit;
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void AppendIntFast(StringBuilder sb, int val)
    {
        if (val == 0) { sb.Append('0'); return; }
        if (_numBuffer == null) _numBuffer = new char[16];
        int pos = 0;
        while (val > 0)
        {
            _numBuffer[pos++] = (char)('0' + (val % 10));
            val /= 10;
        }
        for (int i = pos - 1; i >= 0; i--) sb.Append(_numBuffer[i]);
    }
}

// ── Text formatting utilities ──

public static class TextFormatting
{
    public static Color[] GetColors() => Enumerable.Range(0, 14).Select(i => Color.HSVToRGB(Mathf.SmoothStep(0f, 1f, Mathf.PingPong(Time.time + i / 7f, 1)), 1, 1)).ToArray();

    public static Color[] Create(Color baseColor, HighlightStyle style, int steps = 5)
    {
        if (steps < 3) steps = 3;
        Color[] results = new Color[steps];
        Color.RGBToHSV(baseColor, out float h, out float s, out float v);

        for (int i = 0; i < steps; i++)
        {
            float distToCenter = 1f - Math.Abs((i / (float)(steps - 1)) - 0.5f) * 2f;

            float finalH = h;
            float finalS = s;
            float finalV = v;

            switch (style)
            {
                case HighlightStyle.SoftGlow:
                    finalV = Mathf.Lerp(v * 0.5f, 1.0f, distToCenter);
                    break;

                case HighlightStyle.Metallic:
                    float sharpDist = Mathf.Pow(distToCenter, 2);
                    finalV = Mathf.Lerp(0f, 1.0f, sharpDist);
                    finalS = Mathf.Lerp(s, s * 0.2f, sharpDist);
                    break;

                case HighlightStyle.NeonVibe:
                    finalH = (h + distToCenter * 0.05f) % 1f;
                    finalS = Mathf.Lerp(s, 1.0f, distToCenter);
                    finalV = Mathf.Lerp(v * 0.3f, 1.0f, distToCenter);
                    break;
            }

            results[i] = Color.HSVToRGB(finalH, finalS, finalV);
        }

        return results;
    }

    public static string GradientText(string text, Color[] colors, int? _length = null, double offset = 0d)
    {
        if (string.IsNullOrEmpty(text) || colors == null || colors.Length == 0) return text;

        int length = _length ?? colors.Length;
        if (length <= 0) return text;

        var sb = DynamicTextEffectUtils.GetSharedSb(text.Length * 20);

        var currentColors = new Color[length];
        int colorCount = colors.Length;
        double baseFrac = offset - Math.Floor(offset);
        int baseIdx = ((int)Math.Floor(offset) % colorCount + colorCount) % colorCount;
        float f = (float)baseFrac;

        for (int i = 0; i < length; i++)
        {
            int idx1 = (baseIdx + i); if (idx1 >= colorCount) idx1 -= colorCount;
            int idx2 = idx1 + 1; if (idx2 == colorCount) idx2 = 0;

            if (f < 0.001f) currentColors[i] = colors[idx1];
            else
            {
                Color c1 = colors[idx1], c2 = colors[idx2];
                currentColors[i].r = c1.r + (c2.r - c1.r) * f;
                currentColors[i].g = c1.g + (c2.g - c1.g) * f;
                currentColors[i].b = c1.b + (c2.b - c1.b) * f;
            }
        }

        int pureLen = 0;
        for (int i = 0; i < text.Length; i++) if (text[i] != ' ' && text[i] != '<') pureLen++;

        double normFactor = pureLen > 1 ? 1.0 / (pureLen - 1) : 0;
        int windowRadius = length > 1 ? (int)Math.Ceiling(0.56 * (length - 1)) + 1 : 0;
        const float inv2Sigma2 = 1.0f / (2.0f * 0.035f);
        double invLenMinus1 = length > 1 ? 1.0 / (length - 1) : 0;

        int plainIndex = 0;
        int len = text.Length;

        for (int i = 0; i < len; i++)
        {
            char c = text[i];
            if (c == '<')
            {
                int end = text.IndexOf('>', i);
                if (end >= 0) { sb.Append(text, i, end - i + 1); i = end; continue; }
                sb.Append(c); continue;
            }
            if (c == ' ') { sb.Append(' '); continue; }

            double x = plainIndex * normFactor;
            int centerJ = (int)(x * (length - 1) + 0.5);
            int startJ = centerJ - windowRadius; if (startJ < 0) startJ = 0;
            int endJ = centerJ + windowRadius; if (endJ >= length) endJ = length - 1;

            float r = 0, g = 0, b = 0, totalW = 0;

            for (int j = startJ; j <= endJ; j++)
            {
                double diff = x - (j * invLenMinus1);
                if (diff > 0.5 || diff < -0.5) continue;
                float w = (float)Math.Exp(-diff * diff * inv2Sigma2);

                Color col = currentColors[j];
                r += col.r * w; g += col.g * w; b += col.b * w;
                totalW += w;
            }

            if (totalW > 0.0001f)
            {
                float invW = 1f / totalW;
                sb.Append("<color=#");
                DynamicTextEffectUtils.AppendColorHexFast(sb, r * invW, g * invW, b * invW);
                sb.Append('>');
                sb.Append(c);
                sb.Append("</color>");
            }
            else
            {
                sb.Append(c);
            }
            plainIndex++;
        }

        return sb.ToString();
    }

    public static string SetCharacterTransform(string text, Func<double, Vector2> positionFunc, Func<double, double> sizeFunc, double offset = 0.0, bool changeX = false)
    {
        if (string.IsNullOrEmpty(text) || (positionFunc == null && sizeFunc == null)) return text;
        var sb = DynamicTextEffectUtils.GetSharedSb(text.Length * 10);

        int plainIndex = 0;
        int len = text.Length;
        bool hasPos = positionFunc != null;
        bool hasSize = sizeFunc != null;

        for (int i = 0; i < len; i++)
        {
            char c = text[i];
            if (c == '<')
            {
                int end = text.IndexOf('>', i);
                if (end >= 0) { sb.Append(text, i, end - i + 1); i = end; continue; }
                sb.Append(c); continue;
            }
            if (c == ' ') { sb.Append(' '); continue; }

            double n = plainIndex + offset;
            float px = 0, py = 0;
            if (hasPos)
            {
                Vector2 v = positionFunc(n);
                px = v.x; py = v.y;
            }

            bool doX = changeX && (px > 0.001f || px < -0.001f);
            bool doY = (py > 0.001f || py < -0.001f);
            bool doSize = hasSize;
            double sizeVal = 0;
            if (doSize) sizeVal = sizeFunc(n);

            if (doX) { sb.Append("<pos="); DynamicTextEffectUtils.AppendFloatFast(sb, px, 2); sb.Append("px>"); }
            if (doSize) { sb.Append("<size="); DynamicTextEffectUtils.AppendFloatFast(sb, sizeVal, 2); sb.Append("%>"); }
            if (doY) { sb.Append("<voffset="); DynamicTextEffectUtils.AppendFloatFast(sb, py, 2); sb.Append("px>"); }

            sb.Append(c);

            if (doY) sb.Append("</voffset>");
            if (doSize) sb.Append("</size>");

            plainIndex++;
        }
        return sb.ToString();
    }
}

// ═══════════════════════════════════════════════════════════════
// Appended from FishUtility.cs — extracted to top-level classes
// ═══════════════════════════════════════════════════════════════

// ── DynamicTextShaderDriver ──
[RequireComponent(typeof(TMP_Text))]
public class DynamicTextShaderDriver : MonoBehaviour
{
    public enum EffectMode { VerticalLine = 0, RadialWave = 1, Spiral = 2 }

    public List<Color> colors = new List<Color>() { Color.red, Color.green, Color.blue };
    [Range(64, 512)] public int lutResolution = 256;

    [Range(0.5f, 2.0f)] public float lightnessSCurveStrength = 1.45f;
    [Range(0.5f, 2.0f)] public float chromaBoostFactor = 1.5f;
    [Range(0.5f, 2.0f)] public float postCurveChromaBoost = 1.05f;

    public EffectMode mode = EffectMode.VerticalLine;
    public float speed = 1.0f;
    public float cycleCompression = 1.0f;

    private TMP_Text _textComponent;
    private Texture2D _colorLUT;

    // Shader 属性 ID
    private static readonly int _effectModeId = Shader.PropertyToID("_EffectMode");
    private static readonly int _colorLUTId = Shader.PropertyToID("_ColorLUT");
    private static readonly int _effectCenterId = Shader.PropertyToID("_EffectCenter");
    private static readonly int _effectBoundsId = Shader.PropertyToID("_EffectBounds");
    private static readonly int _effectCompressionId = Shader.PropertyToID("_EffectCompression");
    private static readonly int _effectOffsetId = Shader.PropertyToID("_EffectOffset");

    private bool _isDirty = true;
    private float _currentOffset = 0f;

    private void Awake()
    {
        _textComponent = GetComponent<TMP_Text>();
        TMPro_EventManager.TEXT_CHANGED_EVENT.Add(OnTextChanged);
        MarkDirty();
    }

    private void OnDestroy()
    {
        TMPro_EventManager.TEXT_CHANGED_EVENT.Remove(OnTextChanged);
        if (_colorLUT != null)
        {
            if (Application.isPlaying) Destroy(_colorLUT);
            else DestroyImmediate(_colorLUT);
        }
    }

    private void OnValidate() { MarkDirty(); }

    private void OnTextChanged(UnityEngine.Object obj)
    {
        if (obj == (UnityEngine.Object)_textComponent) MarkDirty();
    }

    public void MarkDirty() { _isDirty = true; }

    private void LateUpdate()
    {
        if (_textComponent == null) return;

        Material targetMat = Application.isPlaying ? _textComponent.fontMaterial : _textComponent.fontSharedMaterial;
        if (targetMat == null) return;

        if (_isDirty)
        {
            GenerateLUT();
            UpdateStaticShaderVariables(targetMat);
            _isDirty = false;
        }

        if (Application.isPlaying)
        {
            _currentOffset += speed * Time.deltaTime;
            if (_currentOffset > 1000f || _currentOffset < -1000f) _currentOffset %= 1f;
        }

        targetMat.SetFloat(_effectOffsetId, _currentOffset);
    }

    private void UpdateStaticShaderVariables(Material mat)
    {
        _textComponent.ForceMeshUpdate();
        Bounds bounds = _textComponent.textBounds;

        Vector2 min = bounds.min;
        Vector2 max = bounds.max;
        Vector2 center = bounds.center;
        float rangeX = Mathf.Max(0.001f, max.x - min.x);
        float rangeY = Mathf.Max(0.001f, max.y - min.y);

        mat.SetVector(_effectBoundsId, new Vector4(min.x, min.y, rangeX, rangeY));
        mat.SetVector(_effectCenterId, new Vector4(center.x, center.y, 0, 0));

        mat.SetFloat(_effectModeId, (float)mode);
        mat.SetFloat(_effectCompressionId, cycleCompression);

        if (_colorLUT != null)
        {
            mat.SetTexture(_colorLUTId, _colorLUT);
        }
    }

    private void GenerateLUT()
    {
        int colorCnt = colors == null ? 0 : colors.Count;
        if (colorCnt == 0) return;

        if (_colorLUT == null || _colorLUT.width != lutResolution)
        {
            _colorLUT = new Texture2D(lutResolution, 1, TextureFormat.RGBA32, false, true);
            _colorLUT.wrapMode = TextureWrapMode.Repeat;
            _colorLUT.filterMode = FilterMode.Bilinear;
        }

        float[] keyL = new float[colorCnt];
        float[] keyC = new float[colorCnt];
        float[] keyH = new float[colorCnt];
        bool doBoost = colorCnt > 1;

        for (int i = 0; i < colorCnt; i++)
        {
            RGBToOklab(colors[i], out float L, out float A, out float B);
            float C = Mathf.Sqrt(A * A + B * B);

            if (doBoost)
            {
                if (C > 0.0001f) { A *= chromaBoostFactor; B *= chromaBoostFactor; }

                // Lightness S-Curve
                float contrastL = Mathf.Clamp01((L - 0.5f) * lightnessSCurveStrength + 0.5f);
                L = contrastL * contrastL * (3f - 2f * contrastL);

                C = Mathf.Sqrt(A * A + B * B);
                if (C > 0.0001f)
                {
                    C *= postCurveChromaBoost;
                    A = C * Mathf.Cos(Mathf.Atan2(B, A));
                    B = C * Mathf.Sin(Mathf.Atan2(B, A));
                }
            }

            float h = Mathf.Atan2(B, A);
            if (h < 0f) h += 2f * Mathf.PI;

            keyL[i] = L;
            keyC[i] = C;
            keyH[i] = h;
        }

        int stepsPerColor = 28;
        int ringLen = colorCnt * stepsPerColor;
        float[] ringL = new float[ringLen];
        float[] ringA = new float[ringLen];
        float[] ringB = new float[ringLen];
        double invSteps = 1.0 / stepsPerColor;

        for (int i = 0; i < ringLen; i++)
        {
            double p = i * invSteps;
            p -= colorCnt * System.Math.Floor(p / colorCnt);
            int idx1 = (int)p;
            if (idx1 >= colorCnt) idx1 = colorCnt - 1;
            float f = (float)(p - idx1);
            int idx2 = (idx1 + 1 < colorCnt) ? idx1 + 1 : 0;

            float L = Mathf.Lerp(keyL[idx1], keyL[idx2], f);
            float C = Mathf.Lerp(keyC[idx1], keyC[idx2], f);
            float h1 = keyH[idx1];
            float h2 = keyH[idx2];

            float deltaH = h2 - h1;
            if (deltaH > Mathf.PI) deltaH -= 2f * Mathf.PI;
            else if (deltaH < -Mathf.PI) deltaH += 2f * Mathf.PI;
            float h = h1 + f * deltaH;

            ringL[i] = L;
            ringA[i] = C * Mathf.Cos(h);
            ringB[i] = C * Mathf.Sin(h);
        }

        int windowRingLen = ringLen;
        double normSigma2 = 0.042;
        double sigmaIdx = System.Math.Sqrt(normSigma2) * (windowRingLen - 1);
        if (sigmaIdx < 1.0) sigmaIdx = 1.0;
        double invSigma2x2 = 1.0 / (2.0 * sigmaIdx * sigmaIdx);
        int kernelRadius = System.Math.Max(1, (int)System.Math.Ceiling(3.0 * sigmaIdx));

        float[] kernelWeights = new float[kernelRadius * 2 + 1];
        for (int dd = -kernelRadius; dd <= kernelRadius; dd++)
        {
            kernelWeights[dd + kernelRadius] = (float)System.Math.Exp(-(dd * dd) * invSigma2x2);
        }

        double invOversample = (double)windowRingLen / lutResolution;
        Color[] pixels = new Color[lutResolution];

        for (int s = 0; s < lutResolution; s++)
        {
            double center = s * invOversample;
            int iCenter = (int)System.Math.Round(center);
            float totalW = 0f, L = 0f, A = 0f, B = 0f;

            for (int d = -kernelRadius; d <= kernelRadius; d++)
            {
                int raw = iCenter + d;
                int j = raw % windowRingLen;
                if (j < 0) j += windowRingLen;
                if (j >= ringLen) j %= ringLen;

                float w = kernelWeights[d + kernelRadius];
                if (w > 0.0005f)
                {
                    L += ringL[j] * w;
                    A += ringA[j] * w;
                    B += ringB[j] * w;
                    totalW += w;
                }
            }

            if (totalW > 0f)
            {
                float inv = 1f / totalW;
                L *= inv; A *= inv; B *= inv;
            }

            pixels[s] = OklabToColor(L, A, B);
        }

        _colorLUT.SetPixels(pixels);
        _colorLUT.Apply();
    }

    private void RGBToOklab(Color c, out float L, out float A, out float B)
    {
        float r = c.r > 0.04045f ? Mathf.Pow((c.r + 0.055f) / 1.055f, 2.4f) : c.r / 12.92f;
        float g = c.g > 0.04045f ? Mathf.Pow((c.g + 0.055f) / 1.055f, 2.4f) : c.g / 12.92f;
        float b = c.b > 0.04045f ? Mathf.Pow((c.b + 0.055f) / 1.055f, 2.4f) : c.b / 12.92f;

        float l = 0.4122214708f * r + 0.5363325363f * g + 0.0514459929f * b;
        float m = 0.2119034982f * r + 0.6806995451f * g + 0.1073969566f * b;
        float s = 0.0883024619f * r + 0.2817188376f * g + 0.6299787005f * b;

        l = Mathf.Pow(Mathf.Max(0, l), 1f / 3f);
        m = Mathf.Pow(Mathf.Max(0, m), 1f / 3f);
        s = Mathf.Pow(Mathf.Max(0, s), 1f / 3f);

        L = 0.2104542553f * l + 0.7936177850f * m - 0.0040720468f * s;
        A = 1.9779984951f * l - 2.4285922050f * m + 0.4505937099f * s;
        B = 0.0259040371f * l + 0.7827717662f * m - 0.8086757660f * s;
    }

    private Color OklabToColor(float L, float A, float B)
    {
        float l_ = L + 0.3963377774f * A + 0.2158037573f * B;
        float m_ = L - 0.1055613458f * A - 0.0638541728f * B;
        float s_ = L - 0.0894841775f * A - 1.2914855480f * B;

        l_ = l_ * l_ * l_;
        m_ = m_ * m_ * m_;
        s_ = s_ * s_ * s_;

        float rLinear = 4.0767416621f * l_ - 3.3077363322f * m_ + 0.2309101289f * s_;
        float gLinear = -1.2684380046f * l_ + 2.6097574011f * m_ - 0.3413193761f * s_;
        float bLinear = -0.0041960863f * l_ - 0.7034186147f * m_ + 1.7076147010f * s_;

        rLinear = Mathf.Max(rLinear, 0f);
        gLinear = Mathf.Max(gLinear, 0f);
        bLinear = Mathf.Max(bLinear, 0f);

        const float highlightK = 0.25f;
        if (rLinear > 1f) rLinear = rLinear / (1f + (rLinear - 1f) * highlightK);
        if (gLinear > 1f) gLinear = gLinear / (1f + (gLinear - 1f) * highlightK);
        if (bLinear > 1f) bLinear = bLinear / (1f + (bLinear - 1f) * highlightK);

        float r = Mathf.Clamp01(LinearToSrgb(rLinear));
        float g = Mathf.Clamp01(LinearToSrgb(gLinear));
        float b = Mathf.Clamp01(LinearToSrgb(bLinear));

        return new Color(r, g, b, 1f);
    }

    private float LinearToSrgb(float linear)
    {
        if (linear <= 0.0031308f)
            return linear * 12.92f;
        else
            return 1.055f * Mathf.Pow(linear, 1f / 2.4f) - 0.055f;
    }
}

// ── DynamicColorText ──
public class DynamicColorText
{
    public float lightnessSCurveStrength = 1.45f;
    public float postCurveChromaBoost = 1.05f;
    public float chromaBoostFactor = 1.5f;
    public double MinCharsPerColorSegment { get; private set; }
    public double CharsPerFullCycle { get; private set; }
    public double CycleCompressionFactor { get; private set; }

    public string Value
    {
        get
        {
            double time = _timeTransform(_timeProvider());
            if (time != _lastTime) { _lastTime = time; UpdateFrame(time); _valueDirty = true; }
            if (_valueDirty) { _value = new string(_buffer, 0, _bufferLen); _valueDirty = false; }
            return _value;
        }
    }
    private readonly Func<double> _timeProvider;
    private readonly Func<double, double> _timeTransform;
    private readonly ColorF[] _baseColors;
    private readonly float[] _keyL;
    private readonly float[] _keyC;
    private readonly float[] _keyH;
    private float[] _staticWrapL;
    private float[] _staticWrapA;
    private float[] _staticWrapB_oklab;
    private float[] _slotSmoothPosBase;
    private readonly int[] _slotHexOffsets;
    private readonly int _slotCount;
    private readonly int _ringLen;
    private readonly int _stepsPerColor;
    private readonly int _windowRingLen;
    private int _smoothLen;
    private int _smoothMask;
    private double _smoothShiftPerOffset;
    private char[] _buffer;
    private int _bufferLen;
    private double _lastTime = double.NaN;
    private bool _valueDirty = true;
    private string _value = string.Empty;
    private static readonly char[] HexChars = BuildHexTable();
    private static readonly char[] ColorTagOpen = { '<', 'c', 'o', 'l', 'o', 'r', '=', '#' };
    private static readonly char[] ColorTagClose = { '<', '/', 'c', 'o', 'l', 'o', 'r', '>' };
    public override string ToString() => Value;
    public static implicit operator string(DynamicColorText d) => d?.Value;
    private struct ColorF
    {
        public float r, g, b;
        public ColorF(Color c) { r = c.r; g = c.g; b = c.b; }
    }
    public static DynamicColorText Create(string text, Color[] colors, int? length = null,
            Func<double> timeProvider = null, Func<double, double> timeTransform = null,
            double cycleCompressionFactor = 0.0)
    {
        return new DynamicColorText(text, colors, length ?? colors.Length,
            timeProvider ?? (() => Time.unscaledTimeAsDouble),
            timeTransform ?? (t => t),
            cycleCompressionFactor);
    }
    private DynamicColorText(string text, Color[] colors, int gradLen,
        Func<double> timeProvider, Func<double, double> timeTransform,
        double cycleCompressionFactor)
    {
        int colorCount = colors.Length;
        _timeProvider = timeProvider;
        _timeTransform = timeTransform;
        _baseColors = new ColorF[colorCount];
        for (int i = 0; i < colorCount; i++) _baseColors[i] = new ColorF(colors[i]);

        _keyL = new float[colorCount];
        _keyC = new float[colorCount];
        _keyH = new float[colorCount];
        bool doBoost = (colorCount > 1);
        float chromaMult = doBoost ? chromaBoostFactor : 1.0f;
        float sCurveStrength = doBoost ? lightnessSCurveStrength : 1.0f;
        float postChromaMult = doBoost ? postCurveChromaBoost : 1.0f;
        for (int i = 0; i < colorCount; i++)
        {
            ColorF col = _baseColors[i];
            float L, A, B_ok;
            SrgbToOklab(col.r, col.g, col.b, out L, out A, out B_ok);
            float C = (float)Math.Sqrt(A * A + B_ok * B_ok);
            if (doBoost)
            {
                if (C > 0.0001f)
                {
                    A *= chromaMult;
                    B_ok *= chromaMult;
                }
                float contrastL = (L - 0.5f) * sCurveStrength + 0.5f;
                contrastL = Math.Max(0f, Math.Min(1f, contrastL));
                L = contrastL * contrastL * (3f - 2f * contrastL);
                C = (float)Math.Sqrt(A * A + B_ok * B_ok);
                if (C > 0.0001f)
                {
                    C *= postChromaMult;
                    float h_temp = (float)Math.Atan2(B_ok, A);
                    A = C * (float)Math.Cos(h_temp);
                    B_ok = C * (float)Math.Sin(h_temp);
                }
            }
            float h = (float)Math.Atan2(B_ok, A);
            if (h < 0f) h += 2f * (float)Math.PI;
            _keyL[i] = L;
            _keyC[i] = C;
            _keyH[i] = h;
        }

        CycleCompressionFactor = (cycleCompressionFactor > 0.0) ? cycleCompressionFactor : colorCount;
        double adaptiveMin = 10.0;
        if (colorCount > 1)
        {
            float maxDeltaE = 0f;
            float sumDeltaE = 0f;

            for (int i = 0; i < colorCount; i++)
            {
                int j = (i + 1) % colorCount;

                float dL = _keyL[i] - _keyL[j];
                float dC = _keyC[i] - _keyC[j];
                float dH = _keyH[i] - _keyH[j];
                if (dH > (float)Math.PI) dH -= 2f * (float)Math.PI;
                else if (dH < -(float)Math.PI) dH += 2f * (float)Math.PI;

                float avgC = (_keyC[i] + _keyC[j]) * 0.5f;
                float weightedDH = avgC * dH;
                float deltaE = (float)Math.Sqrt(dL * dL + dC * dC + weightedDH * weightedDH);

                maxDeltaE = Math.Max(maxDeltaE, deltaE);
                sumDeltaE += deltaE;
            }

            float avgDeltaE = sumDeltaE / colorCount;

            double jumpFactor = Math.Max(0.0, (maxDeltaE - 0.12f) * 12.0);
            adaptiveMin = 7.0 + jumpFactor + (colorCount * 0.6);

            adaptiveMin = Math.Max(6.0, Math.Min(22.0, adaptiveMin));

            if (avgDeltaE > 0.25f) adaptiveMin += 2.0;
        }
        MinCharsPerColorSegment = adaptiveMin;

        const int STEPS_PER_COLOR = 28;
        _stepsPerColor = STEPS_PER_COLOR;
        _ringLen = colorCount * _stepsPerColor;
        _windowRingLen = _ringLen;

        const int OVERSAMPLE = 6;
        int smoothLen_ = RoundPowerOfTwo(_windowRingLen * OVERSAMPLE);
        int smoothMask_ = smoothLen_ - 1;
        double invOversample = (double)_windowRingLen / smoothLen_;

        const double normSigma2 = 0.035;
        double sigmaIdx = Math.Sqrt(normSigma2) * (_windowRingLen - 1);
        if (sigmaIdx < 1.0) sigmaIdx = 1.0;
        double invSigma2x2 = 1.0 / (2.0 * sigmaIdx * sigmaIdx);
        int kernelRadius = Math.Max(1, (int)Math.Ceiling(3.0 * sigmaIdx));

        float[] kernelWeights = new float[kernelRadius * 2 + 1];
        for (int dd = -kernelRadius; dd <= kernelRadius; dd++)
        {
            double dist = dd;
            kernelWeights[dd + kernelRadius] = (float)Math.Exp(-dist * dist * invSigma2x2);
        }

        double invSteps = 1.0 / _stepsPerColor;
        float[] ringL_raw = new float[_ringLen];
        float[] ringA_raw = new float[_ringLen];
        float[] ringB_raw = new float[_ringLen];
        for (int i = 0; i < _ringLen; i++)
        {
            double p = i * invSteps;
            p -= colorCount * Math.Floor(p / colorCount);
            int idx1 = (int)p;
            if (idx1 >= colorCount) idx1 = colorCount - 1;
            float f = (float)(p - idx1);
            int idx2 = idx1 + 1 < colorCount ? idx1 + 1 : 0;

            float L = _keyL[idx1] * (1f - f) + _keyL[idx2] * f;
            float C = _keyC[idx1] * (1f - f) + _keyC[idx2] * f;
            float h1 = _keyH[idx1];
            float h2 = _keyH[idx2];
            float deltaH = h2 - h1;
            if (deltaH > Math.PI) deltaH -= 2f * (float)Math.PI;
            else if (deltaH < -Math.PI) deltaH += 2f * (float)Math.PI;
            float h = h1 + f * deltaH;
            float AA = C * (float)Math.Cos(h);
            float BB = C * (float)Math.Sin(h);

            ringL_raw[i] = L;
            ringA_raw[i] = AA;
            ringB_raw[i] = BB;
        }

        float[] staticL = new float[smoothLen_];
        float[] staticA = new float[smoothLen_];
        float[] staticB = new float[smoothLen_];
        unsafe
        {
            fixed (float* pRingL = ringL_raw, pRingA = ringA_raw, pRingB = ringB_raw,
                   pKW = kernelWeights, pOutL = staticL, pOutA = staticA, pOutB = staticB)
            {
                TextEffectsNative.rs_build_static_wrap_table(
                    pRingL, pRingA, pRingB, pKW,
                    _windowRingLen, _ringLen, kernelRadius, smoothLen_,
                    invOversample, invSigma2x2,
                    pOutL, pOutA, pOutB);
            }
        }

        _staticWrapL = staticL;
        _staticWrapA = staticA;
        _staticWrapB_oklab = staticB;
        _smoothLen = smoothLen_;
        _smoothMask = smoothMask_;
        _smoothShiftPerOffset = (double)smoothLen_ / _ringLen * _stepsPerColor;

        int textLen = text.Length;
        _buffer = new char[textLen * 24];
        var hexOffsets = new int[textLen];
        int slotCount = 0;
        int pos = 0;
        for (int i = 0; i < textLen; i++)
        {
            char c = text[i];
            if (c == '\r' || c == '\n')
            {
                _buffer[pos++] = c;
                if (c == '\r' && i + 1 < textLen && text[i + 1] == '\n')
                    _buffer[pos++] = text[++i];
                continue;
            }
            if (c == '<')
            {
                int end = text.IndexOf('>', i);
                if (end >= 0)
                {
                    if (IsBreakTag(text, i, end))
                    {
                        for (int j = i; j <= end; j++) _buffer[pos++] = text[j];
                        i = end;
                        continue;
                    }
                    for (int j = i; j <= end; j++) _buffer[pos++] = text[j];
                    i = end;
                    continue;
                }
            }
            if (c == ' ')
            {
                _buffer[pos++] = ' ';
            }
            else
            {
                Array.Copy(ColorTagOpen, 0, _buffer, pos, 8);
                int hOff = pos + 8;
                _buffer[hOff] = '0'; _buffer[hOff + 1] = '0';
                _buffer[hOff + 2] = '0'; _buffer[hOff + 3] = '0';
                _buffer[hOff + 4] = '0'; _buffer[hOff + 5] = '0';
                pos = hOff + 6;
                _buffer[pos++] = '>';
                _buffer[pos++] = c;
                Array.Copy(ColorTagClose, 0, _buffer, pos, 8);
                pos += 8;
                hexOffsets[slotCount] = hOff;
                slotCount++;
            }
        }
        _bufferLen = pos;
        _slotCount = slotCount;
        _slotHexOffsets = new int[slotCount];
        if (slotCount > 0)
        {
            Array.Copy(hexOffsets, _slotHexOffsets, slotCount);
        }
        if (slotCount > 0)
        {
            double minCycle = colorCount * MinCharsPerColorSegment;
            if (slotCount < 20) minCycle *= 1.25;
            double baseCycle = Math.Max(minCycle, (double)slotCount);

            double finalCycle = baseCycle / CycleCompressionFactor;

            CharsPerFullCycle = Math.Max(8.0, finalCycle);

            double stepPerChar = _windowRingLen / CharsPerFullCycle;
            var slotRingPositions = new double[slotCount];
            for (int s = 0; s < slotCount; s++)
                slotRingPositions[s] = (s * stepPerChar) % _windowRingLen;

            _slotSmoothPosBase = new float[slotCount];
            double scale = (double)_smoothLen / _windowRingLen;
            for (int s = 0; s < slotCount; s++)
                _slotSmoothPosBase[s] = (float)(slotRingPositions[s] * scale);
        }
        else
        {
            CharsPerFullCycle = 30.0;
            MinCharsPerColorSegment = 6.0;
            CycleCompressionFactor = 1.0;
            _slotSmoothPosBase = Array.Empty<float>();
        }
    }
    private void UpdateFrame(double offset)
    {
        int colorCount = _baseColors.Length;
        if (colorCount == 0 || _slotCount == 0) return;

        double baseOffset = offset - colorCount * Math.Floor(offset / colorCount);
        double totalShift = baseOffset * _smoothShiftPerOffset;
        int shiftInt = (int)totalShift;
        float shiftFrac = (float)(totalShift - shiftInt);

        var buf = _buffer;
        var hex = HexChars;
        var ho = _slotHexOffsets;

        for (int s = 0; s < _slotCount; s++)
        {
            float smoothPos = _slotSmoothPosBase[s];
            int baseIdx = (int)smoothPos;
            float frac = smoothPos - baseIdx;

            int idx = (baseIdx - shiftInt) & _smoothMask;
            float f = frac - shiftFrac;
            if (f < 0f)
            {
                f += 1.0f;
                idx = (idx - 1) & _smoothMask;
            }
            int idxNext = (idx + 1) & _smoothMask;

            float oneMinusF = 1.0f - f;
            float L = _staticWrapL[idx] * oneMinusF + _staticWrapL[idxNext] * f;
            float A = _staticWrapA[idx] * oneMinusF + _staticWrapA[idxNext] * f;
            float B_oklab = _staticWrapB_oklab[idx] * oneMinusF + _staticWrapB_oklab[idxNext] * f;

            ColorF final = OklabToColorF(L, A, B_oklab);

            int ri = (int)(final.r * 255f + 0.5f);
            int gi = (int)(final.g * 255f + 0.5f);
            int bi = (int)(final.b * 255f + 0.5f);

            int h = ho[s];
            buf[h] = hex[ri << 1]; buf[h + 1] = hex[(ri << 1) | 1];
            buf[h + 2] = hex[gi << 1]; buf[h + 3] = hex[(gi << 1) | 1];
            buf[h + 4] = hex[bi << 1]; buf[h + 5] = hex[(bi << 1) | 1];
        }
    }
    private static char[] BuildHexTable()
    {
        var t = new char[512];
        const string h = "0123456789ABCDEF";
        for (int i = 0; i < 256; i++) { t[i << 1] = h[i >> 4]; t[(i << 1) | 1] = h[i & 0xF]; }
        return t;
    }
    private static bool IsBreakTag(string text, int start, int end)
    {
        int len = end - start + 1;
        if (len == 4) return text[start + 1] == 'b' && text[start + 2] == 'r';
        if (len == 5) return text[start + 1] == 'b' && text[start + 2] == 'r' && text[start + 3] == '/';
        if (len == 6) return text[start + 1] == 'b' && text[start + 2] == 'r' && text[start + 3] == ' ' && text[start + 4] == '/';
        return false;
    }
    private static int RoundPowerOfTwo(int x)
    {
        if (x <= 0) return 1;
        x--;
        x |= x >> 1;
        x |= x >> 2;
        x |= x >> 4;
        x |= x >> 8;
        x |= x >> 16;
        return x + 1;
    }

    private static void SrgbToOklab(float r, float g, float b, out float L, out float A, out float B_oklab)
    {
        r = r > 0.04045f ? (float)Math.Pow((r + 0.055f) / 1.055f, 2.4f) : r / 12.92f;
        g = g > 0.04045f ? (float)Math.Pow((g + 0.055f) / 1.055f, 2.4f) : g / 12.92f;
        b = b > 0.04045f ? (float)Math.Pow((b + 0.055f) / 1.055f, 2.4f) : b / 12.92f;

        float l = 0.4122214708f * r + 0.5363325363f * g + 0.0514459929f * b;
        float m = 0.2119034982f * r + 0.6806995451f * g + 0.1073969566f * b;
        float s = 0.0883024619f * r + 0.2817188376f * g + 0.6299787005f * b;

        l = (float)Math.Pow(l, 1f / 3f);
        m = (float)Math.Pow(m, 1f / 3f);
        s = (float)Math.Pow(s, 1f / 3f);

        L = 0.2104542553f * l + 0.7936177850f * m - 0.0040720468f * s;
        A = 1.9779984951f * l - 2.4285922050f * m + 0.4505937099f * s;
        B_oklab = 0.0259040371f * l + 0.7827717662f * m - 0.8086757660f * s;
    }

    private static float LinearToSrgb(float linear)
    {
        if (linear <= 0.0031308f)
            return linear * 12.92f;
        else
            return 1.055f * (float)Math.Pow(linear, 1f / 2.4f) - 0.055f;
    }

    private static ColorF OklabToColorF(float L, float A, float B_oklab)
    {
        float l_ = L + 0.3963377774f * A + 0.2158037573f * B_oklab;
        float m_ = L - 0.1055613458f * A - 0.0638541728f * B_oklab;
        float s_ = L - 0.0894841775f * A - 1.2914855480f * B_oklab;

        l_ = l_ * l_ * l_;
        m_ = m_ * m_ * m_;
        s_ = s_ * s_ * s_;

        float rLinear = 4.0767416621f * l_ - 3.3077363322f * m_ + 0.2309101289f * s_;
        float gLinear = -1.2684380046f * l_ + 2.6097574011f * m_ - 0.3413193761f * s_;
        float bLinear = -0.0041960863f * l_ - 0.7034186147f * m_ + 1.7076147010f * s_;

        rLinear = Math.Max(rLinear, 0f);
        gLinear = Math.Max(gLinear, 0f);
        bLinear = Math.Max(bLinear, 0f);

        const float highlightK = 0.25f;
        if (rLinear > 1f) rLinear = rLinear / (1f + (rLinear - 1f) * highlightK);
        if (gLinear > 1f) gLinear = gLinear / (1f + (gLinear - 1f) * highlightK);
        if (bLinear > 1f) bLinear = bLinear / (1f + (bLinear - 1f) * highlightK);

        rLinear = LinearToSrgb(rLinear);
        gLinear = LinearToSrgb(gLinear);
        bLinear = LinearToSrgb(bLinear);

        float r = rLinear < 0f ? 0f : (rLinear > 1f ? 1f : rLinear);
        float g = gLinear < 0f ? 0f : (gLinear > 1f ? 1f : gLinear);
        float b = bLinear < 0f ? 0f : (bLinear > 1f ? 1f : bLinear);

        return new ColorF { r = r, g = g, b = b };
    }
}

// ── Rust Native Bridge ──
// Replaces Unity IJobParallelFor structs with native Rust rayon-parallelized compute.
// The native DLL (text_effects_rs.dll) is preloaded by Main.cs OnLoad() via LoadLibrary.

public static unsafe class TextEffectsNative
{
    private static bool _preloaded;

    /// <summary>Call once at startup with the mod's root path.</summary>
    public static void Preload(string modPath)
    {
        if (_preloaded) return;
        string dllPath = modPath + "\\lib\\text_effects_rs.dll";
        if (System.IO.File.Exists(dllPath))
        {
            LoadLibrary(dllPath);
            _preloaded = true;
        }
    }

    [DllImport("kernel32", SetLastError = true)]
    private static extern IntPtr LoadLibrary(string lpFileName);

    [DllImport("text_effects_rs", CallingConvention = CallingConvention.Cdecl)]
    public static extern void rs_batch_srgb_to_oklab(
        float* r, float* g, float* b,
        float* l, float* a, float* b_ok,
        int count);

    [DllImport("text_effects_rs", CallingConvention = CallingConvention.Cdecl)]
    public static extern void rs_build_static_wrap_table(
        float* ring_l, float* ring_a, float* ring_b,
        float* kernel_weights,
        int window_ring_len, int ring_len, int kernel_radius,
        int smooth_len, double inv_oversample, double inv_sigma2x2,
        float* out_l, float* out_a, float* out_b);

    // ── Batched: all mesh groups in a single P/Invoke ──
    [StructLayout(LayoutKind.Sequential)]
    public struct MeshGroupHeader
    {
        public int VertStart;
        public int VertCount;
        public IntPtr Colors;
    }

    [DllImport("text_effects_rs", CallingConvention = CallingConvention.Cdecl)]
    public static extern void rs_apply_all_groups(
        float* static_wrap_l, float* static_wrap_a, float* static_wrap_b,
        int* vert_index, float* normalized_pos, int* config_idx,
        int* config_wrap_starts, int* config_smooth_lens, int* config_masks,
        int* config_shift_ints, float* config_shift_fracs, float* config_cycle_comps,
        int num_configs,
        MeshGroupHeader* groups,
        int num_groups);
}

// ── DynamicColorTextEffect ──
public class DynamicColorTextEffect : IDisposable
{
    public struct LineSetting
    {
        public Color[] colors;
        public EffectMode mode;
        public float extraValue;
        public double cycleCompressionFactor;
        public int autoDisplayColorCount;
    }

    private LineSetting[] _lineSettings;
    private int _numConfigs;

    public float lightnessSCurveStrength = 1.45f;
    public float postCurveChromaBoost = 1.05f;
    public float chromaBoostFactor = 1.5f;

    private const int OVERSAMPLE = 6;
    private List<TMP_Text> _texts = new List<TMP_Text>();
    private bool _layoutDirty = true;
    private bool _wasActive;
    private int _lastTotalChars = -1, _lastTotalLines = -1;
    private int _verticalLineTotalVerts;
    private int[] _verticalLineVertIndex;
    private float[] _verticalLineNormalizedPos;
    private int[] _verticalLineConfigIdx;
    private struct MeshGroup
    {
        public TMP_Text Text;
        public int MeshIndex;
        public int VertStart, VertCount;
    }
    private bool _disposed = false;
    private MeshGroup[] _meshGroups;
    private NativeArray<float> _allStaticWrapL;
    private NativeArray<float> _allStaticWrapA;
    private NativeArray<float> _allStaticWrapB_oklab;
    private NativeArray<int> _configWrapStarts;
    private NativeArray<int> _configSmoothLens;
    private NativeArray<int> _configMasks;
    private NativeArray<int> _configColorCounts;
    private NativeArray<double> _configSmoothShiftPerOffsets;
    private NativeArray<float> _configCycleCompressionFactors;
    private NativeArray<int> _nativeVerticalLineVertIndex;
    private NativeArray<float> _nativeVerticalLineNormalizedPos;
    private NativeArray<int> _nativeVerticalLineConfigIdx;
    private NativeArray<Color32>[] _persistentMeshColors;
    private List<int> _activeGroupIndices;
    private JobHandle _currentJobHandle;
    private Func<double> _customTimeProvider;
    private Func<double, double> _customTimeTransform;
    public Func<double> TimeProvider
    {
        get => _customTimeProvider;
        set => _customTimeProvider = value;
    }
    public Func<double, double> TimeTransform
    {
        get => _customTimeTransform;
        set => _customTimeTransform = value;
    }
    public bool Active
    {
        get
        {
            if (_texts == null || _texts.Count == 0) return false;
            foreach (var t in _texts)
            {
                if (t != null && t.isActiveAndEnabled)
                    return true;
            }
            return false;
        }
    }
    public bool AutoDisposeWhenEmpty { get; set; } = false;
    private struct GlobalVertData
    {
        public TMP_Text Text;
        public int MeshIndex;
        public int VertLocalIndex;
        public Vector3 WorldPos;
        public int LineNumber;
        public int ConfigIdx;
    }
    private struct VertData
    {
        public TMP_Text Text;
        public int MeshIndex;
        public int VertIndex;
        public float NormalizedPos;
        public int ConfigIdx;
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        TMPro_EventManager.TEXT_CHANGED_EVENT.Remove(OnTextChanged);
        DisposeNativeArrays();
        Utility.Effecter.UnregisterEffect(this);
    }
    private void DisposeNativeArrays()
    {
        _currentJobHandle.Complete();
        if (_allStaticWrapL.IsCreated) _allStaticWrapL.Dispose();
        if (_allStaticWrapA.IsCreated) _allStaticWrapA.Dispose();
        if (_allStaticWrapB_oklab.IsCreated) _allStaticWrapB_oklab.Dispose();
        if (_configWrapStarts.IsCreated) _configWrapStarts.Dispose();
        if (_configSmoothLens.IsCreated) _configSmoothLens.Dispose();
        if (_configMasks.IsCreated) _configMasks.Dispose();
        if (_configColorCounts.IsCreated) _configColorCounts.Dispose();
        if (_configSmoothShiftPerOffsets.IsCreated) _configSmoothShiftPerOffsets.Dispose();
        if (_configCycleCompressionFactors.IsCreated) _configCycleCompressionFactors.Dispose();
        if (_nativeVerticalLineVertIndex.IsCreated) _nativeVerticalLineVertIndex.Dispose();
        if (_nativeVerticalLineNormalizedPos.IsCreated) _nativeVerticalLineNormalizedPos.Dispose();
        if (_nativeVerticalLineConfigIdx.IsCreated) _nativeVerticalLineConfigIdx.Dispose();
        if (_persistentMeshColors != null)
        {
            for (int i = 0; i < _persistentMeshColors.Length; i++)
            {
                if (_persistentMeshColors[i].IsCreated) _persistentMeshColors[i].Dispose();
            }
            _persistentMeshColors = null;
        }
    }
    private void CleanupDestroyedTexts()
    {
        bool changed = false;
        for (int i = _texts.Count - 1; i >= 0; i--)
        {
            if (_texts[i] == null)
            {
                _texts.RemoveAt(i);
                changed = true;
            }
        }
        if (changed)
            _layoutDirty = true;
    }
    private void OnTextChanged(UnityEngine.Object obj)
    {
        if (_disposed || _texts == null) return;
        foreach (var t in _texts)
        {
            if (ReferenceEquals(obj, t))
            {
                int chars = 0, lines = 0;
                foreach (var tx in _texts)
                {
                    if (tx?.textInfo == null) continue;
                    chars += tx.textInfo.characterCount;
                    lines += tx.textInfo.lineCount;
                }
                bool linesChanged = lines != _lastTotalLines;
                bool charsDrastic = _lastTotalChars == 0
                    || Math.Abs(chars - _lastTotalChars) > _lastTotalChars * 0.2f;
                if (linesChanged || charsDrastic)
                {
                    _lastTotalChars = chars;
                    _lastTotalLines = lines;
                    _layoutDirty = true;
                }
                return;
            }
        }
    }
    public void UpdateFrame(float offset)
    {
        if (_disposed || _lineSettings == null || _lineSettings.Length == 0) return;

        CleanupDestroyedTexts();

        bool isActive = Active;
        if (!isActive)
        {
            _wasActive = false;
            return;
        }
        if (!_wasActive)
        {
            _wasActive = true;
            _layoutDirty = true;
        }

        if (_texts.Count == 0)
        {
            if (AutoDisposeWhenEmpty)
            {
                Dispose();
                return;
            }
            _verticalLineTotalVerts = 0;
            return;
        }

        bool hasValidText = false;
        bool isAllDataReady = true;

        for (int i = _texts.Count - 1; i >= 0; i--)
        {
            var t = _texts[i];
            if (t == null)
            {
                _texts.RemoveAt(i);
                _layoutDirty = true;
                continue;
            }
            if (t.textInfo == null || t.textInfo.meshInfo == null || t.textInfo.meshInfo.Length == 0)
            {
                isAllDataReady = false;
                break;
            }
            if (t.textInfo.characterCount == 0 && _layoutDirty)
            {
                t.ForceMeshUpdate();
                if (t.textInfo.characterCount == 0)
                {
                    isAllDataReady = false;
                    break;
                }
            }
            hasValidText = true;
        }
        if (!hasValidText)
        {
            _verticalLineTotalVerts = 0;
            return;
        }
        if (!isAllDataReady)
        {
            _layoutDirty = true;
            return;
        }
        if (_layoutDirty)
        {
            _layoutDirty = false;
            Rebuild();
        }
        if (_verticalLineTotalVerts == 0) return;
        double time = _customTimeTransform != null ? _customTimeTransform(offset) : offset;
        ApplyColorsWithJobsVerticalLine((float)time);
    }

    private void Rebuild()
    {
        CleanupDestroyedTexts();
        for (int i = _texts.Count - 1; i >= 0; i--)
        {
            var t = _texts[i];
            if (t == null)
                _texts.RemoveAt(i);
            t.ForceMeshUpdate();
        }
        if (_texts.Count == 0)
        {
            _verticalLineTotalVerts = 0;
            return;
        }
        _numConfigs = _lineSettings.Length;
        if (_numConfigs == 0)
        {
            _verticalLineTotalVerts = 0;
            return;
        }
        var textLineOffset = new Dictionary<TMP_Text, int>(_texts.Count);
        int currentLineId = 0;
        foreach (var text in _texts)
        {
            if (text != null && text.textInfo != null)
            {
                textLineOffset[text] = currentLineId;
                currentLineId += text.textInfo.lineCount;
            }
        }
        int totalLines = currentLineId;
        if (totalLines == 0)
        {
            _verticalLineTotalVerts = 0;
            return;
        }
        if (_numConfigs > totalLines && totalLines > 0)
        {
            var trimmed = new LineSetting[totalLines];
            Array.Copy(_lineSettings, trimmed, totalLines);
            _lineSettings = trimmed;
            _numConfigs = totalLines;
        }
        BuildAllLineColorData();
        float globalMinX = float.MaxValue, globalMaxX = float.MinValue, globalMinY = float.MaxValue, globalMaxY = float.MinValue;
        float[] blockMinX = new float[_numConfigs];
        float[] blockMaxX = new float[_numConfigs];
        float[] blockMinY = new float[_numConfigs];
        float[] blockMaxY = new float[_numConfigs];
        for (int c = 0; c < _numConfigs; c++)
        {
            blockMinX[c] = float.MaxValue;
            blockMaxX[c] = float.MinValue;
            blockMinY[c] = float.MaxValue;
            blockMaxY[c] = float.MinValue;
        }
        int totalVertCount = 0;
        foreach (var text in _texts)
        {
            if (text == null || !text.enabled || text.textInfo == null || !text.gameObject.activeInHierarchy || !text.gameObject.activeSelf) continue;
            var textInfo = text.textInfo;
            if (textInfo.characterCount == 0) continue;
            for (int i = 0; i < textInfo.characterCount; i++)
            {
                ref var ci = ref textInfo.characterInfo[i];
                if (!ci.isVisible) continue;
                char ch = ci.character;
                if (ch == ' ' || ch == '\u00A0' || ch == '\t') continue;
                int globalLineId = textLineOffset[text] + ci.lineNumber;
                int settingIdx;
                if (_numConfigs >= totalLines)
                    settingIdx = globalLineId;
                else
                {
                    int blockSize = totalLines / _numConfigs;
                    settingIdx = globalLineId / blockSize;
                    if (settingIdx >= _numConfigs) settingIdx = _numConfigs - 1;
                }
                int mi = ci.materialReferenceIndex;
                int vi = ci.vertexIndex;
                var vertsArr = textInfo.meshInfo[mi].vertices;
                if (vi + 3 >= vertsArr.Length) continue;
                totalVertCount += 4;
                for (int v = 0; v < 4; v++)
                {
                    Vector3 worldPos = text.transform.TransformPoint(vertsArr[vi + v]);
                    globalMinX = Mathf.Min(globalMinX, worldPos.x);
                    globalMaxX = Mathf.Max(globalMaxX, worldPos.x);
                    globalMinY = Mathf.Min(globalMinY, worldPos.y);
                    globalMaxY = Mathf.Max(globalMaxY, worldPos.y);
                    blockMinX[settingIdx] = Mathf.Min(blockMinX[settingIdx], worldPos.x);
                    blockMaxX[settingIdx] = Mathf.Max(blockMaxX[settingIdx], worldPos.x);
                    blockMinY[settingIdx] = Mathf.Min(blockMinY[settingIdx], worldPos.y);
                    blockMaxY[settingIdx] = Mathf.Max(blockMaxY[settingIdx], worldPos.y);
                }
            }
        }
        if (totalVertCount == 0)
        {
            _verticalLineTotalVerts = 0;
            return;
        }
        float globalRangeX = globalMaxX - globalMinX; if (globalRangeX < 0.0001f) globalRangeX = 1f;
        float globalRangeY = globalMaxY - globalMinY; if (globalRangeY < 0.0001f) globalRangeY = 1f;
        float[] blockCenterX = new float[_numConfigs];
        float[] blockCenterY = new float[_numConfigs];
        for (int c = 0; c < _numConfigs; c++)
        {
            float bx = blockMaxX[c] - blockMinX[c]; if (bx < 0.0001f) bx = 1f;
            float by = blockMaxY[c] - blockMinY[c]; if (by < 0.0001f) by = 1f;
            blockCenterX[c] = (blockMinX[c] + blockMaxX[c]) * 0.5f;
            blockCenterY[c] = (blockMinY[c] + blockMaxY[c]) * 0.5f;
        }
        GlobalVertData[] globalVerts = new GlobalVertData[totalVertCount];
        float[] blockMaxDist = new float[_numConfigs];
        int vertCounter = 0;
        foreach (var text in _texts)
        {
            if (text == null || !text.enabled || text.textInfo == null || !text.gameObject.activeInHierarchy || !text.gameObject.activeSelf) continue;
            var textInfo = text.textInfo;
            if (textInfo.characterCount == 0) continue;
            for (int i = 0; i < textInfo.characterCount; i++)
            {
                ref var ci = ref textInfo.characterInfo[i];
                if (!ci.isVisible) continue;
                char ch = ci.character;
                if (ch == ' ' || ch == '\u00A0' || ch == '\t') continue;
                int globalLineId = textLineOffset[text] + ci.lineNumber;
                int settingIdx;
                if (_numConfigs >= totalLines)
                    settingIdx = globalLineId;
                else
                {
                    int blockSize = totalLines / _numConfigs;
                    settingIdx = globalLineId / blockSize;
                    if (settingIdx >= _numConfigs) settingIdx = _numConfigs - 1;
                }
                int mi = ci.materialReferenceIndex;
                int vi = ci.vertexIndex;
                var vertsArr = textInfo.meshInfo[mi].vertices;
                if (vi + 3 >= vertsArr.Length) continue;
                for (int v = 0; v < 4; v++)
                {
                    Vector3 localPos = vertsArr[vi + v];
                    Vector3 worldPos = text.transform.TransformPoint(localPos);
                    globalVerts[vertCounter] = new GlobalVertData
                    {
                        Text = text,
                        MeshIndex = mi,
                        VertLocalIndex = vi + v,
                        WorldPos = worldPos,
                        LineNumber = ci.lineNumber,
                        ConfigIdx = settingIdx
                    };
                    float dx = worldPos.x - blockCenterX[settingIdx];
                    float dy = worldPos.y - blockCenterY[settingIdx];
                    float dist = Mathf.Sqrt(dx * dx + dy * dy);
                    if (dist > blockMaxDist[settingIdx]) blockMaxDist[settingIdx] = dist;
                    vertCounter++;
                }
            }
        }
        for (int c = 0; c < _numConfigs; c++)
        {
            if (blockMaxDist[c] < 0.0001f) blockMaxDist[c] = 1f;
        }
        VertData[] effectVerts = new VertData[totalVertCount];
        for (int i = 0; i < totalVertCount; i++)
        {
            var gv = globalVerts[i];
            int cfg = gv.ConfigIdx;
            var lineCfg = _lineSettings[cfg];
            float normalizedPos = ComputeNormalizedPos(lineCfg.mode, lineCfg.extraValue, ref gv, blockCenterX, blockCenterY, blockMaxDist, cfg, globalMinX, globalRangeX, globalMinY, globalRangeY);
            effectVerts[i] = new VertData
            {
                Text = gv.Text,
                MeshIndex = gv.MeshIndex,
                VertIndex = gv.VertLocalIndex,
                NormalizedPos = normalizedPos,
                ConfigIdx = cfg
            };
        }
        FlattenData(effectVerts);
        // Snapshot post-rebuild totals so OnTextChanged only re-triggers on real structural change
        _lastTotalChars = 0; _lastTotalLines = 0;
        foreach (var t in _texts)
        {
            if (t?.textInfo == null) continue;
            _lastTotalChars += t.textInfo.characterCount;
            _lastTotalLines += t.textInfo.lineCount;
        }
    }
    private float ComputeNormalizedPos(EffectMode mode, float extraValue, ref GlobalVertData gv, float[] blockCenterX, float[] blockCenterY, float[] blockMaxDist, int cfg, float globalMinX, float globalRangeX, float globalMinY, float globalRangeY)
    {
        if (mode == EffectMode.VerticalLine)
        {
            float normX = (gv.WorldPos.x - globalMinX) / globalRangeX;
            float normY = (gv.WorldPos.y - globalMinY) / globalRangeY;
            float angleRad = extraValue * Mathf.Deg2Rad;
            float cosA = Mathf.Cos(angleRad);
            float sinA = Mathf.Sin(angleRad);
            float rotatedPos = normX * cosA + normY * sinA;
            return Mathf.Clamp01(rotatedPos * 0.5f + 0.5f);
        }
        else if (mode == EffectMode.RadialWave)
        {
            float dx = gv.WorldPos.x - blockCenterX[cfg];
            float dy = gv.WorldPos.y - blockCenterY[cfg];
            float dist = Mathf.Sqrt(dx * dx + dy * dy);
            return dist / blockMaxDist[cfg];
        }
        else if (mode == EffectMode.Spiral)
        {
            float dx = gv.WorldPos.x - blockCenterX[cfg];
            float dy = gv.WorldPos.y - blockCenterY[cfg];
            float dist = Mathf.Sqrt(dx * dx + dy * dy);
            float radiusNorm = dist / blockMaxDist[cfg];
            float angle = Mathf.Atan2(dy, dx);
            float angleNorm = (angle + Mathf.PI) / (Mathf.PI * 2f);
            return Mathf.Repeat(angleNorm + extraValue * radiusNorm, 1f);
        }
        return 0f;
    }
    private void FlattenData(VertData[] verts)
    {
        Array.Sort(verts, (a, b) =>
        {
            int idCmp = a.Text.GetInstanceID().CompareTo(b.Text.GetInstanceID());
            if (idCmp != 0) return idCmp;
            return a.MeshIndex.CompareTo(b.MeshIndex);
        });
        _verticalLineTotalVerts = verts.Length;
        _verticalLineVertIndex = new int[_verticalLineTotalVerts];
        _verticalLineNormalizedPos = new float[_verticalLineTotalVerts];
        _verticalLineConfigIdx = new int[_verticalLineTotalVerts];
        for (int i = 0; i < _verticalLineTotalVerts; i++)
        {
            _verticalLineVertIndex[i] = verts[i].VertIndex;
            _verticalLineNormalizedPos[i] = verts[i].NormalizedPos;
            _verticalLineConfigIdx[i] = verts[i].ConfigIdx;
        }
        var groups = new List<MeshGroup>();
        int vIdx = 0;
        while (vIdx < _verticalLineTotalVerts)
        {
            TMP_Text currText = verts[vIdx].Text;
            int currMesh = verts[vIdx].MeshIndex;
            int start = vIdx;
            while (vIdx < _verticalLineTotalVerts && verts[vIdx].Text == currText && verts[vIdx].MeshIndex == currMesh)
            {
                vIdx++;
            }
            groups.Add(new MeshGroup
            {
                Text = currText,
                MeshIndex = currMesh,
                VertStart = start,
                VertCount = vIdx - start
            });
        }
        _meshGroups = groups.ToArray();
        InitializeNativeArraysVerticalLine();
        InitializePersistentMeshColors();
        if (_activeGroupIndices == null)
            _activeGroupIndices = new List<int>(32);
        _activeGroupIndices.Clear();
    }
    private void BuildAllLineColorData()
    {
        _currentJobHandle.Complete();
        if (_allStaticWrapL.IsCreated) _allStaticWrapL.Dispose();
        if (_allStaticWrapA.IsCreated) _allStaticWrapA.Dispose();
        if (_allStaticWrapB_oklab.IsCreated) _allStaticWrapB_oklab.Dispose();
        if (_configWrapStarts.IsCreated) _configWrapStarts.Dispose();
        if (_configSmoothLens.IsCreated) _configSmoothLens.Dispose();
        if (_configMasks.IsCreated) _configMasks.Dispose();
        if (_configColorCounts.IsCreated) _configColorCounts.Dispose();
        if (_configSmoothShiftPerOffsets.IsCreated) _configSmoothShiftPerOffsets.Dispose();
        if (_configCycleCompressionFactors.IsCreated) _configCycleCompressionFactors.Dispose();

        var wrapStartList = new List<int>(_numConfigs);
        var smoothLenList = new List<int>(_numConfigs);
        var maskList = new List<int>(_numConfigs);
        var shiftPerOffsetList = new List<double>(_numConfigs);
        var colorCountList = new List<int>(_numConfigs);
        var compressionList = new List<float>(_numConfigs);
        var tempStaticLs = new List<NativeArray<float>>(_numConfigs);
        var tempStaticAs = new List<NativeArray<float>>(_numConfigs);
        var tempStaticBoks = new List<NativeArray<float>>(_numConfigs);
        int cumulativeSize = 0;
        const int stepsPerColor = 28;
        for (int c = 0; c < _numConfigs; c++)
        {
            var cfg = _lineSettings[c];
            int colorCnt = cfg.colors != null ? cfg.colors.Length : 0;
            if (colorCnt == 0) colorCnt = 1;
            double compFactor = cfg.cycleCompressionFactor;
            int groupSize = cfg.autoDisplayColorCount;
            if (groupSize > 0 && compFactor <= 0.0)
                compFactor = (double)groupSize / (double)colorCnt;
            else if (compFactor <= 0.0)
                compFactor = 1.0;
            compressionList.Add((float)compFactor);
            colorCountList.Add(colorCnt);
            int ringLen = colorCnt * stepsPerColor;
            int windowRingLen = ringLen;
            int smoothLen_ = RoundPowerOfTwo(windowRingLen * OVERSAMPLE);
            int smoothMask = smoothLen_ - 1;
            double smoothShiftPerOffset_ = (double)smoothLen_ / ringLen * stepsPerColor;
            shiftPerOffsetList.Add(smoothShiftPerOffset_);
            smoothLenList.Add(smoothLen_);
            maskList.Add(smoothMask);
            wrapStartList.Add(cumulativeSize);
            cumulativeSize += smoothLen_;
            var baseR = new NativeArray<float>(colorCnt, Allocator.TempJob);
            var baseG = new NativeArray<float>(colorCnt, Allocator.TempJob);
            var baseB = new NativeArray<float>(colorCnt, Allocator.TempJob);
            for (int i = 0; i < colorCnt; i++)
            {
                baseR[i] = cfg.colors[i].r;
                baseG[i] = cfg.colors[i].g;
                baseB[i] = cfg.colors[i].b;
            }
            var baseL = new NativeArray<float>(colorCnt, Allocator.TempJob);
            var baseA = new NativeArray<float>(colorCnt, Allocator.TempJob);
            var baseB_oklab = new NativeArray<float>(colorCnt, Allocator.TempJob);
            unsafe
            {
                TextEffectsNative.rs_batch_srgb_to_oklab(
                    (float*)NativeArrayUnsafeUtility.GetUnsafeBufferPointerWithoutChecks(baseR),
                    (float*)NativeArrayUnsafeUtility.GetUnsafeBufferPointerWithoutChecks(baseG),
                    (float*)NativeArrayUnsafeUtility.GetUnsafeBufferPointerWithoutChecks(baseB),
                    (float*)NativeArrayUnsafeUtility.GetUnsafeBufferPointerWithoutChecks(baseL),
                    (float*)NativeArrayUnsafeUtility.GetUnsafeBufferPointerWithoutChecks(baseA),
                    (float*)NativeArrayUnsafeUtility.GetUnsafeBufferPointerWithoutChecks(baseB_oklab),
                    colorCnt);
            }
            NativeArray<float> keyL = new NativeArray<float>(colorCnt, Allocator.TempJob);
            NativeArray<float> keyC = new NativeArray<float>(colorCnt, Allocator.TempJob);
            NativeArray<float> keyH = new NativeArray<float>(colorCnt, Allocator.TempJob);
            bool doBoost = (colorCnt > 1);
            float chromaMult = doBoost ? chromaBoostFactor : 1.0f;
            float sCurveStrength = doBoost ? lightnessSCurveStrength : 1.0f;
            float postChromaMult = doBoost ? postCurveChromaBoost : 1.0f;
            for (int i = 0; i < colorCnt; i++)
            {
                float L = baseL[i];
                float A = baseA[i];
                float B = baseB_oklab[i];
                float C = Mathf.Sqrt(A * A + B * B);
                if (doBoost)
                {
                    if (C > 0.0001f)
                    {
                        A *= chromaMult;
                        B *= chromaMult;
                    }
                    float contrastL = (L - 0.5f) * sCurveStrength + 0.5f;
                    contrastL = Mathf.Clamp01(contrastL);
                    L = contrastL * contrastL * (3f - 2f * contrastL);
                    C = Mathf.Sqrt(A * A + B * B);
                    if (C > 0.0001f)
                    {
                        C *= postChromaMult;
                        A = C * Mathf.Cos(Mathf.Atan2(B, A));
                        B = C * Mathf.Sin(Mathf.Atan2(B, A));
                    }
                }
                float h = Mathf.Atan2(B, A);
                if (h < 0f) h += 2f * Mathf.PI;
                keyL[i] = L;
                keyC[i] = C;
                keyH[i] = h;
            }
            baseR.Dispose();
            baseG.Dispose();
            baseB.Dispose();
            baseL.Dispose();
            baseA.Dispose();
            baseB_oklab.Dispose();
            double invSteps = 1.0 / stepsPerColor;
            var ringL = new NativeArray<float>(ringLen, Allocator.TempJob);
            var ringA = new NativeArray<float>(ringLen, Allocator.TempJob);
            var ringB_oklab = new NativeArray<float>(ringLen, Allocator.TempJob);
            for (int i = 0; i < ringLen; i++)
            {
                double p = i * invSteps;
                p -= colorCnt * System.Math.Floor(p / colorCnt);
                int idx1 = (int)p;
                if (idx1 >= colorCnt) idx1 = colorCnt - 1;
                float f = (float)(p - idx1);
                int idx2 = (idx1 + 1 < colorCnt) ? idx1 + 1 : 0;
                float L = keyL[idx1] * (1f - f) + keyL[idx2] * f;
                float C = keyC[idx1] * (1f - f) + keyC[idx2] * f;
                float h1 = keyH[idx1];
                float h2 = keyH[idx2];
                float deltaH = h2 - h1;
                if (deltaH > Mathf.PI) deltaH -= 2f * Mathf.PI;
                else if (deltaH < -Mathf.PI) deltaH += 2f * Mathf.PI;
                float h = h1 + f * deltaH;
                float A = C * Mathf.Cos(h);
                float B = C * Mathf.Sin(h);
                ringL[i] = L;
                ringA[i] = A;
                ringB_oklab[i] = B;
            }
            keyL.Dispose();
            keyC.Dispose();
            keyH.Dispose();
            const double normSigma2 = 0.042;
            double sigmaIdx = System.Math.Sqrt(normSigma2) * (windowRingLen - 1);
            if (sigmaIdx < 1.0) sigmaIdx = 1.0;
            double invSigma2x2 = 1.0 / (2.0 * sigmaIdx * sigmaIdx);
            int kernelRadius = System.Math.Max(1, (int)System.Math.Ceiling(3.0 * sigmaIdx));
            var kernelWeights = new NativeArray<float>(kernelRadius * 2 + 1, Allocator.TempJob);
            for (int dd = -kernelRadius; dd <= kernelRadius; dd++)
            {
                double dist = dd;
                kernelWeights[dd + kernelRadius] = (float)System.Math.Exp(-dist * dist * invSigma2x2);
            }
            double invOversample = (double)windowRingLen / smoothLen_;
            var staticL = new NativeArray<float>(smoothLen_, Allocator.TempJob);
            var staticA = new NativeArray<float>(smoothLen_, Allocator.TempJob);
            var staticBok = new NativeArray<float>(smoothLen_, Allocator.TempJob);
            unsafe
            {
                TextEffectsNative.rs_build_static_wrap_table(
                    (float*)NativeArrayUnsafeUtility.GetUnsafeBufferPointerWithoutChecks(ringL),
                    (float*)NativeArrayUnsafeUtility.GetUnsafeBufferPointerWithoutChecks(ringA),
                    (float*)NativeArrayUnsafeUtility.GetUnsafeBufferPointerWithoutChecks(ringB_oklab),
                    (float*)NativeArrayUnsafeUtility.GetUnsafeBufferPointerWithoutChecks(kernelWeights),
                    windowRingLen, ringLen, kernelRadius, smoothLen_,
                    invOversample, invSigma2x2,
                    (float*)NativeArrayUnsafeUtility.GetUnsafeBufferPointerWithoutChecks(staticL),
                    (float*)NativeArrayUnsafeUtility.GetUnsafeBufferPointerWithoutChecks(staticA),
                    (float*)NativeArrayUnsafeUtility.GetUnsafeBufferPointerWithoutChecks(staticBok));
            }
            kernelWeights.Dispose();
            ringL.Dispose();
            ringA.Dispose();
            ringB_oklab.Dispose();
            tempStaticLs.Add(staticL);
            tempStaticAs.Add(staticA);
            tempStaticBoks.Add(staticBok);
        }
        if (cumulativeSize > 0)
        {
            _allStaticWrapL = new NativeArray<float>(cumulativeSize, Allocator.Persistent);
            _allStaticWrapA = new NativeArray<float>(cumulativeSize, Allocator.Persistent);
            _allStaticWrapB_oklab = new NativeArray<float>(cumulativeSize, Allocator.Persistent);
            for (int c = 0; c < _numConfigs; c++)
            {
                int start = wrapStartList[c];
                int len = smoothLenList[c];
                NativeArray<float>.Copy(tempStaticLs[c], 0, _allStaticWrapL, start, len);
                NativeArray<float>.Copy(tempStaticAs[c], 0, _allStaticWrapA, start, len);
                NativeArray<float>.Copy(tempStaticBoks[c], 0, _allStaticWrapB_oklab, start, len);
                tempStaticLs[c].Dispose();
                tempStaticAs[c].Dispose();
                tempStaticBoks[c].Dispose();
            }
        }
        _configWrapStarts = new NativeArray<int>(wrapStartList.ToArray(), Allocator.Persistent);
        _configSmoothLens = new NativeArray<int>(smoothLenList.ToArray(), Allocator.Persistent);
        _configMasks = new NativeArray<int>(maskList.ToArray(), Allocator.Persistent);
        _configSmoothShiftPerOffsets = new NativeArray<double>(shiftPerOffsetList.ToArray(), Allocator.Persistent);
        _configColorCounts = new NativeArray<int>(colorCountList.ToArray(), Allocator.Persistent);
        _configCycleCompressionFactors = new NativeArray<float>(compressionList.ToArray(), Allocator.Persistent);
    }
    private void InitializeNativeArraysVerticalLine()
    {
        _currentJobHandle.Complete();
        if (_nativeVerticalLineVertIndex.IsCreated) _nativeVerticalLineVertIndex.Dispose();
        if (_nativeVerticalLineNormalizedPos.IsCreated) _nativeVerticalLineNormalizedPos.Dispose();
        if (_nativeVerticalLineConfigIdx.IsCreated) _nativeVerticalLineConfigIdx.Dispose();
        _nativeVerticalLineVertIndex = new NativeArray<int>(_verticalLineVertIndex, Allocator.Persistent);
        _nativeVerticalLineNormalizedPos = new NativeArray<float>(_verticalLineNormalizedPos, Allocator.Persistent);
        _nativeVerticalLineConfigIdx = new NativeArray<int>(_verticalLineConfigIdx, Allocator.Persistent);
    }
    private void InitializePersistentMeshColors()
    {
        if (_persistentMeshColors != null)
        {
            for (int i = 0; i < _persistentMeshColors.Length; i++)
            {
                if (_persistentMeshColors[i].IsCreated) _persistentMeshColors[i].Dispose();
            }
        }
        int groupCount = _meshGroups.Length;
        _persistentMeshColors = new NativeArray<Color32>[groupCount];
        for (int g = 0; g < groupCount; g++)
        {
            var group = _meshGroups[g];
            if (group.Text == null || group.Text.textInfo == null || group.MeshIndex >= group.Text.textInfo.meshInfo.Length)
            {
                _persistentMeshColors[g] = new NativeArray<Color32>(0, Allocator.Persistent);
                continue;
            }
            var meshInfo = group.Text.textInfo.meshInfo[group.MeshIndex];
            var colors = meshInfo.colors32;
            if (colors == null || colors.Length == 0)
            {
                _persistentMeshColors[g] = new NativeArray<Color32>(0, Allocator.Persistent);
                continue;
            }
            _persistentMeshColors[g] = new NativeArray<Color32>(colors.Length, Allocator.Persistent);
            NativeArray<Color32>.Copy(colors, _persistentMeshColors[g]);
        }
    }
    private void ApplyColorsWithJobsVerticalLine(float offset)
    {
        _currentJobHandle.Complete();
        int groupCount = _meshGroups.Length;
        int numCfgs = _numConfigs;
        var configShiftInts = new NativeArray<int>(numCfgs, Allocator.TempJob);
        var configShiftFracs = new NativeArray<float>(numCfgs, Allocator.TempJob);
        for (int c = 0; c < numCfgs; c++)
        {
            int colorCount = _configColorCounts[c];
            double baseOffset = offset - colorCount * System.Math.Floor(offset / colorCount);
            double totalShift = baseOffset * _configSmoothShiftPerOffsets[c];
            configShiftInts[c] = (int)totalShift;
            configShiftFracs[c] = (float)(totalShift - configShiftInts[c]);
        }
        // Collect active groups and copy current mesh colors — single P/Invoke via rs_apply_all_groups
        _activeGroupIndices.Clear();
        int activeCount = 0;
        var headers = new TextEffectsNative.MeshGroupHeader[groupCount];
        unsafe
        {
            for (int g = 0; g < groupCount; g++)
            {
                var group = _meshGroups[g];
                if (group.Text == null || !group.Text.enabled || group.Text.gameObject == null || !group.Text.gameObject.activeInHierarchy || !group.Text.gameObject.activeSelf || group.Text.textInfo == null)
                    continue;
                var meshInfos = group.Text.textInfo.meshInfo;
                if (group.MeshIndex >= meshInfos.Length)
                    continue;
                var targetColors = meshInfos[group.MeshIndex].colors32;
                if (targetColors == null || targetColors.Length == 0 || group.VertCount <= 0)
                    continue;
                var nativeColors = _persistentMeshColors[g];
                NativeArray<Color32>.Copy(targetColors, nativeColors);
                headers[activeCount] = new TextEffectsNative.MeshGroupHeader
                {
                    VertStart = group.VertStart,
                    VertCount = group.VertCount,
                    Colors = (IntPtr)NativeArrayUnsafeUtility.GetUnsafeBufferPointerWithoutChecks(nativeColors)
                };
                _activeGroupIndices.Add(g);
                activeCount++;
            }
            if (activeCount > 0)
            {
                fixed (TextEffectsNative.MeshGroupHeader* pHeaders = headers)
                {
                    TextEffectsNative.rs_apply_all_groups(
                        (float*)NativeArrayUnsafeUtility.GetUnsafeBufferPointerWithoutChecks(_allStaticWrapL),
                        (float*)NativeArrayUnsafeUtility.GetUnsafeBufferPointerWithoutChecks(_allStaticWrapA),
                        (float*)NativeArrayUnsafeUtility.GetUnsafeBufferPointerWithoutChecks(_allStaticWrapB_oklab),
                        (int*)NativeArrayUnsafeUtility.GetUnsafeBufferPointerWithoutChecks(_nativeVerticalLineVertIndex),
                        (float*)NativeArrayUnsafeUtility.GetUnsafeBufferPointerWithoutChecks(_nativeVerticalLineNormalizedPos),
                        (int*)NativeArrayUnsafeUtility.GetUnsafeBufferPointerWithoutChecks(_nativeVerticalLineConfigIdx),
                        (int*)NativeArrayUnsafeUtility.GetUnsafeBufferPointerWithoutChecks(_configWrapStarts),
                        (int*)NativeArrayUnsafeUtility.GetUnsafeBufferPointerWithoutChecks(_configSmoothLens),
                        (int*)NativeArrayUnsafeUtility.GetUnsafeBufferPointerWithoutChecks(_configMasks),
                        (int*)NativeArrayUnsafeUtility.GetUnsafeBufferPointerWithoutChecks(configShiftInts),
                        (float*)NativeArrayUnsafeUtility.GetUnsafeBufferPointerWithoutChecks(configShiftFracs),
                        (float*)NativeArrayUnsafeUtility.GetUnsafeBufferPointerWithoutChecks(_configCycleCompressionFactors),
                        numCfgs,
                        pHeaders,
                        activeCount);
                }
            }
        }
        if (activeCount > 0)
        {
            for (int i = 0; i < activeCount; i++)
            {
                int g = _activeGroupIndices[i];
                var group = _meshGroups[g];
                var targetColorsArr = group.Text.textInfo.meshInfo[group.MeshIndex].colors32;
                NativeArray<Color32>.Copy(_persistentMeshColors[g], targetColorsArr);
            }
        }
        configShiftInts.Dispose();
        configShiftFracs.Dispose();
        foreach (var text in _texts)
        {
            if (text != null && text.enabled && text.gameObject != null && text.gameObject.activeInHierarchy && text.gameObject.activeSelf)
                text.UpdateVertexData(TMP_VertexDataUpdateFlags.Colors32);
        }
    }
    private int RoundPowerOfTwo(int x)
    {
        if (x <= 0) return 1;
        x--;
        x |= x >> 1;
        x |= x >> 2;
        x |= x >> 4;
        x |= x >> 8;
        x |= x >> 16;
        return x + 1;
    }
    public static DynamicColorTextEffect Create(List<TMP_Text> targets, Color[] colors, EffectMode mode = EffectMode.VerticalLine, float extraValue = 0f, double cycleCompressionFactor = 0.0, int autoDisplayColorCount = 0, Func<double> timeProvider = null, Func<double, double> timeTransform = null, LineSetting[] lineSettings = null)
    {
        if (targets == null || targets.Count == 0) return null;
        var effect = new DynamicColorTextEffect();
        effect._texts = targets.Where(t => t != null).ToList();
        if (effect._texts.Count == 0) return null;

        if (lineSettings == null || lineSettings.Length == 0)
            lineSettings = new[] { new LineSetting { colors = colors, mode = mode, extraValue = extraValue, cycleCompressionFactor = cycleCompressionFactor, autoDisplayColorCount = autoDisplayColorCount } };

        effect._lineSettings = lineSettings;
        effect._customTimeProvider = timeProvider;
        effect._customTimeTransform = timeTransform;
        effect._layoutDirty = true;

        TMPro_EventManager.TEXT_CHANGED_EVENT.Add(effect.OnTextChanged);
        effect.Rebuild();
        return effect;
    }

    public static DynamicColorTextEffect Create(List<TMP_Text> targets, Color[] colors, EffectMode mode = EffectMode.VerticalLine, float extraValue = 0f, double cycleCompressionFactor = 0.0, Func<double> timeProvider = null, Func<double, double> timeTransform = null, LineSetting[] lineSettings = null)
        => Create(targets, colors, mode, extraValue, cycleCompressionFactor, 0, timeProvider, timeTransform, lineSettings);

    public static DynamicColorTextEffect Create(List<TMP_Text> targets, Color[] colors, EffectMode mode = EffectMode.VerticalLine, float extraValue = 0f, int autoDisplayColorCount = 0, Func<double> timeProvider = null, Func<double, double> timeTransform = null, LineSetting[] lineSettings = null)
        => Create(targets, colors, mode, extraValue, 0.0, autoDisplayColorCount, timeProvider, timeTransform, lineSettings);

    public static DynamicColorTextEffect Create(List<TMP_Text> targets, Color[] colors, EffectMode mode = EffectMode.VerticalLine, float extraValue = 0f, Func<double> timeProvider = null, Func<double, double> timeTransform = null, LineSetting[] lineSettings = null)
        => Create(targets, colors, mode, extraValue, 0.0, 0, timeProvider, timeTransform, lineSettings);

    public static DynamicColorTextEffect Create(TMP_Text target, Color[] colors, EffectMode mode = EffectMode.VerticalLine, float extraValue = 0f, double cycleCompressionFactor = 0.0, int autoDisplayColorCount = 0, Func<double> timeProvider = null, Func<double, double> timeTransform = null, LineSetting[] lineSettings = null)
        => Create(new List<TMP_Text> { target }, colors, mode, extraValue, cycleCompressionFactor, autoDisplayColorCount, timeProvider, timeTransform, lineSettings);

    public static DynamicColorTextEffect Create(TMP_Text target, Color[] colors, EffectMode mode = EffectMode.VerticalLine, float extraValue = 0f, double cycleCompressionFactor = 0.0, Func<double> timeProvider = null, Func<double, double> timeTransform = null, LineSetting[] lineSettings = null)
        => Create(new List<TMP_Text> { target }, colors, mode, extraValue, cycleCompressionFactor, 0, timeProvider, timeTransform, lineSettings);

    public static DynamicColorTextEffect Create(TMP_Text target, Color[] colors, EffectMode mode = EffectMode.VerticalLine, float extraValue = 0f, int autoDisplayColorCount = 0, Func<double> timeProvider = null, Func<double, double> timeTransform = null, LineSetting[] lineSettings = null)
        => Create(new List<TMP_Text> { target }, colors, mode, extraValue, 0.0, autoDisplayColorCount, timeProvider, timeTransform, lineSettings);

    public static DynamicColorTextEffect Create(TMP_Text target, Color[] colors, EffectMode mode = EffectMode.VerticalLine, float extraValue = 0f, Func<double> timeProvider = null, Func<double, double> timeTransform = null, LineSetting[] lineSettings = null)
        => Create(new List<TMP_Text> { target }, colors, mode, extraValue, 0.0, 0, timeProvider, timeTransform, lineSettings);

    public static DynamicColorTextEffect Create(TMP_Text[] targets, Color[] colors, EffectMode mode = EffectMode.VerticalLine, float extraValue = 0f, double cycleCompressionFactor = 0.0, int autoDisplayColorCount = 0, Func<double> timeProvider = null, Func<double, double> timeTransform = null, LineSetting[] lineSettings = null)
        => Create(new List<TMP_Text>(targets), colors, mode, extraValue, cycleCompressionFactor, autoDisplayColorCount, timeProvider, timeTransform, lineSettings);

    public static DynamicColorTextEffect Create(TMP_Text[] targets, Color[] colors, EffectMode mode = EffectMode.VerticalLine, float extraValue = 0f, double cycleCompressionFactor = 0.0, Func<double> timeProvider = null, Func<double, double> timeTransform = null, LineSetting[] lineSettings = null)
        => Create(new List<TMP_Text>(targets), colors, mode, extraValue, cycleCompressionFactor, 0, timeProvider, timeTransform, lineSettings);

    public static DynamicColorTextEffect Create(TMP_Text[] targets, Color[] colors, EffectMode mode = EffectMode.VerticalLine, float extraValue = 0f, int autoDisplayColorCount = 0, Func<double> timeProvider = null, Func<double, double> timeTransform = null, LineSetting[] lineSettings = null)
        => Create(new List<TMP_Text>(targets), colors, mode, extraValue, 0.0, autoDisplayColorCount, timeProvider, timeTransform, lineSettings);

    public static DynamicColorTextEffect Create(TMP_Text[] targets, Color[] colors, EffectMode mode = EffectMode.VerticalLine, float extraValue = 0f, Func<double> timeProvider = null, Func<double, double> timeTransform = null, LineSetting[] lineSettings = null)
        => Create(new List<TMP_Text>(targets), colors, mode, extraValue, 0.0, 0, timeProvider, timeTransform, lineSettings);

    public static DynamicColorTextEffect Create(List<GameObject> targets, Color[] colors, EffectMode mode = EffectMode.VerticalLine, float extraValue = 0f, double cycleCompressionFactor = 0.0, int autoDisplayColorCount = 0, Func<double> timeProvider = null, Func<double, double> timeTransform = null, LineSetting[] lineSettings = null)
    {
        var tmpList = targets?.Where(go => go != null)
                              .Select(go => go.GetComponent<TMP_Text>())
                              .Where(t => t != null)
                              .ToList() ?? new List<TMP_Text>();
        return Create(tmpList, colors, mode, extraValue, cycleCompressionFactor, autoDisplayColorCount, timeProvider, timeTransform, lineSettings);
    }

    public static DynamicColorTextEffect Create(List<GameObject> targets, Color[] colors, EffectMode mode = EffectMode.VerticalLine, float extraValue = 0f, double cycleCompressionFactor = 0.0, Func<double> timeProvider = null, Func<double, double> timeTransform = null, LineSetting[] lineSettings = null)
        => Create(targets, colors, mode, extraValue, cycleCompressionFactor, 0, timeProvider, timeTransform, lineSettings);

    public static DynamicColorTextEffect Create(List<GameObject> targets, Color[] colors, EffectMode mode = EffectMode.VerticalLine, float extraValue = 0f, int autoDisplayColorCount = 0, Func<double> timeProvider = null, Func<double, double> timeTransform = null, LineSetting[] lineSettings = null)
        => Create(targets, colors, mode, extraValue, 0.0, autoDisplayColorCount, timeProvider, timeTransform, lineSettings);

    public static DynamicColorTextEffect Create(List<GameObject> targets, Color[] colors, EffectMode mode = EffectMode.VerticalLine, float extraValue = 0f, Func<double> timeProvider = null, Func<double, double> timeTransform = null, LineSetting[] lineSettings = null)
        => Create(targets, colors, mode, extraValue, 0.0, 0, timeProvider, timeTransform, lineSettings);

    public static DynamicColorTextEffect Create(GameObject target, Color[] colors, EffectMode mode = EffectMode.VerticalLine, float extraValue = 0f, double cycleCompressionFactor = 0.0, int autoDisplayColorCount = 0, Func<double> timeProvider = null, Func<double, double> timeTransform = null, LineSetting[] lineSettings = null)
        => Create(new List<GameObject> { target }, colors, mode, extraValue, cycleCompressionFactor, autoDisplayColorCount, timeProvider, timeTransform, lineSettings);

    public static DynamicColorTextEffect Create(GameObject target, Color[] colors, EffectMode mode = EffectMode.VerticalLine, float extraValue = 0f, double cycleCompressionFactor = 0.0, Func<double> timeProvider = null, Func<double, double> timeTransform = null, LineSetting[] lineSettings = null)
        => Create(new List<GameObject> { target }, colors, mode, extraValue, cycleCompressionFactor, 0, timeProvider, timeTransform, lineSettings);

    public static DynamicColorTextEffect Create(GameObject target, Color[] colors, EffectMode mode = EffectMode.VerticalLine, float extraValue = 0f, int autoDisplayColorCount = 0, Func<double> timeProvider = null, Func<double, double> timeTransform = null, LineSetting[] lineSettings = null)
        => Create(new List<GameObject> { target }, colors, mode, extraValue, 0.0, autoDisplayColorCount, timeProvider, timeTransform, lineSettings);

    public static DynamicColorTextEffect Create(GameObject target, Color[] colors, EffectMode mode = EffectMode.VerticalLine, float extraValue = 0f, Func<double> timeProvider = null, Func<double, double> timeTransform = null, LineSetting[] lineSettings = null)
        => Create(new List<GameObject> { target }, colors, mode, extraValue, 0.0, 0, timeProvider, timeTransform, lineSettings);

    public static DynamicColorTextEffect Create(GameObject[] targets, Color[] colors, EffectMode mode = EffectMode.VerticalLine, float extraValue = 0f, double cycleCompressionFactor = 0.0, int autoDisplayColorCount = 0, Func<double> timeProvider = null, Func<double, double> timeTransform = null, LineSetting[] lineSettings = null)
        => Create(new List<GameObject>(targets), colors, mode, extraValue, cycleCompressionFactor, autoDisplayColorCount, timeProvider, timeTransform, lineSettings);

    public static DynamicColorTextEffect Create(GameObject[] targets, Color[] colors, EffectMode mode = EffectMode.VerticalLine, float extraValue = 0f, double cycleCompressionFactor = 0.0, Func<double> timeProvider = null, Func<double, double> timeTransform = null, LineSetting[] lineSettings = null)
        => Create(new List<GameObject>(targets), colors, mode, extraValue, cycleCompressionFactor, 0, timeProvider, timeTransform, lineSettings);

    public static DynamicColorTextEffect Create(GameObject[] targets, Color[] colors, EffectMode mode = EffectMode.VerticalLine, float extraValue = 0f, int autoDisplayColorCount = 0, Func<double> timeProvider = null, Func<double, double> timeTransform = null, LineSetting[] lineSettings = null)
        => Create(new List<GameObject>(targets), colors, mode, extraValue, 0.0, autoDisplayColorCount, timeProvider, timeTransform, lineSettings);

    public static DynamicColorTextEffect Create(GameObject[] targets, Color[] colors, EffectMode mode = EffectMode.VerticalLine, float extraValue = 0f, Func<double> timeProvider = null, Func<double, double> timeTransform = null, LineSetting[] lineSettings = null)
        => Create(new List<GameObject>(targets), colors, mode, extraValue, 0.0, 0, timeProvider, timeTransform, lineSettings);

    public static DynamicColorTextEffect Create(IEnumerable<GameObject> targets, Color[] colors, EffectMode mode = EffectMode.VerticalLine, float extraValue = 0f, double cycleCompressionFactor = 0.0, int autoDisplayColorCount = 0, Func<double> timeProvider = null, Func<double, double> timeTransform = null, LineSetting[] lineSettings = null)
        => Create(targets.ToList(), colors, mode, extraValue, cycleCompressionFactor, autoDisplayColorCount, timeProvider, timeTransform, lineSettings);

    public static DynamicColorTextEffect Create(IEnumerable<GameObject> targets, Color[] colors, EffectMode mode = EffectMode.VerticalLine, float extraValue = 0f, double cycleCompressionFactor = 0.0, Func<double> timeProvider = null, Func<double, double> timeTransform = null, LineSetting[] lineSettings = null)
        => Create(targets.ToList(), colors, mode, extraValue, cycleCompressionFactor, 0, timeProvider, timeTransform, lineSettings);

    public static DynamicColorTextEffect Create(IEnumerable<GameObject> targets, Color[] colors, EffectMode mode = EffectMode.VerticalLine, float extraValue = 0f, int autoDisplayColorCount = 0, Func<double> timeProvider = null, Func<double, double> timeTransform = null, LineSetting[] lineSettings = null)
        => Create(targets.ToList(), colors, mode, extraValue, 0.0, autoDisplayColorCount, timeProvider, timeTransform, lineSettings);

    public static DynamicColorTextEffect Create(IEnumerable<GameObject> targets, Color[] colors, EffectMode mode = EffectMode.VerticalLine, float extraValue = 0f, Func<double> timeProvider = null, Func<double, double> timeTransform = null, LineSetting[] lineSettings = null)
        => Create(targets.ToList(), colors, mode, extraValue, 0.0, 0, timeProvider, timeTransform, lineSettings);

    public static DynamicColorTextEffect Create(List<TMP_Text> targets, LineSetting[] lineSettings, Func<double> timeProvider = null, Func<double, double> timeTransform = null)
        => Create(targets, null, EffectMode.VerticalLine, 0f, 0.0, 0, timeProvider, timeTransform, lineSettings);

    public static DynamicColorTextEffect Create(TMP_Text target, LineSetting[] lineSettings, Func<double> timeProvider = null, Func<double, double> timeTransform = null)
        => Create(new List<TMP_Text> { target }, lineSettings, timeProvider, timeTransform);

    public static DynamicColorTextEffect Create(List<GameObject> targets, LineSetting[] lineSettings, Func<double> timeProvider = null, Func<double, double> timeTransform = null)
    {
        var tmpList = targets?.Where(go => go != null)
                              .Select(go => go.GetComponent<TMP_Text>())
                              .Where(t => t != null)
                              .ToList() ?? new List<TMP_Text>();
        return Create(tmpList, lineSettings, timeProvider, timeTransform);
    }

    public static DynamicColorTextEffect Create(GameObject target, LineSetting[] lineSettings, Func<double> timeProvider = null, Func<double, double> timeTransform = null)
        => Create(new List<GameObject> { target }, lineSettings, timeProvider, timeTransform);

    public void Add(params TMP_Text[] objects)
    {
        CleanupDestroyedTexts();
        if (objects == null || objects.Length == 0) return;
        bool changed = false;
        foreach (var tmp in objects)
        {
            if (tmp == null || _texts.Contains(tmp)) continue;
            _texts.Add(tmp);
            changed = true;
        }
        if (changed)
        {
            _layoutDirty = true;
            Rebuild();
        }
    }

    public void Add(params GameObject[] objects)
    {
        CleanupDestroyedTexts();
        if (objects == null || objects.Length == 0) return;
        bool changed = false;
        foreach (var go in objects)
        {
            if (go == null) continue;
            var tmp = go.GetComponent<TMP_Text>();
            if (tmp == null || _texts.Contains(tmp)) continue;
            _texts.Add(tmp);
            changed = true;
        }
        if (changed)
        {
            _layoutDirty = true;
            Rebuild();
        }
    }

    public void Remove(params TMP_Text[] objects)
    {
        CleanupDestroyedTexts();
        if (objects == null || objects.Length == 0) return;
        bool changed = false;
        foreach (var tmp in objects)
        {
            if (tmp == null) continue;
            if (_texts.Remove(tmp))
                changed = true;
        }
        if (changed)
        {
            _layoutDirty = true;
            Rebuild();
        }
    }

    public void Remove(params GameObject[] objects)
    {
        CleanupDestroyedTexts();
        if (objects == null || objects.Length == 0) return;
        bool changed = false;
        foreach (var go in objects)
        {
            if (go == null) continue;
            var tmp = go.GetComponent<TMP_Text>();
            if (tmp == null) continue;
            if (_texts.Remove(tmp))
                changed = true;
        }
        if (changed)
        {
            _layoutDirty = true;
            Rebuild();
        }
    }
}

// ── DynamicColorTextEffectManager ──
public class DynamicColorTextEffectManager : MonoBehaviour
{
    private static int _targetFrameRate = 60;
    private static double _updateInterval;
    private static double _nextUpdateTime;
    private static double _accumulatedTime;

    private static List<DynamicColorTextEffect> _effects = new List<DynamicColorTextEffect>();

    public static DynamicColorTextEffectManager Instance { get; private set; }

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }
        Instance = this;
        DontDestroyOnLoad(gameObject);
    }

    private void OnEnable()
    {
        _updateInterval = 1.0 / _targetFrameRate;
        _nextUpdateTime = 0;
        _accumulatedTime = 0;
    }

    private void LateUpdate()
    {
        double currentTime = Time.unscaledTimeAsDouble;
        if (_nextUpdateTime < 0.0001)
            _nextUpdateTime = currentTime;

        if (currentTime < _nextUpdateTime)
            return;

        double timeBehind = currentTime - _nextUpdateTime;
        int framesToSkip = (int)(timeBehind / _updateInterval);

        _accumulatedTime += _updateInterval * (framesToSkip + 1);
        _nextUpdateTime += _updateInterval * (framesToSkip + 1);

        for (int i = _effects.Count - 1; i >= 0; i--)
        {
            var effect = _effects[i];
            if (effect != null)
            {
                effect.UpdateFrame((float)_accumulatedTime);
            }
            else
            {
                _effects.RemoveAt(i);
            }
        }
    }

    public void RegisterEffect(DynamicColorTextEffect effect)
    {
        if (effect != null && !_effects.Contains(effect))
            _effects.Add(effect);
    }

    public void UnregisterEffect(DynamicColorTextEffect effect)
    {
        _effects.Remove(effect);
    }

    public int TargetFrameRate
    {
        get => _targetFrameRate;
        set
        {
            int clampedValue = Mathf.Clamp(value, 1, 120);
            if (_targetFrameRate != clampedValue)
            {
                _targetFrameRate = clampedValue;
                _updateInterval = 1.0 / _targetFrameRate;
            }
        }
    }
}

[DisallowMultipleComponent]
public class TooltipTextChanger : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler, IEventSystemHandler
{
    public float refreshRate = 45f;
    private HasTooltipBehaviour tooltip;
    public bool canshow;
    public Func<string> CurrentText;
    private Coroutine refreshRoutine;
    void Start() => tooltip = GetComponent<HasTooltipBehaviour>();
    void OnEnable()
    {
        if (tooltip == null) tooltip = GetComponent<HasTooltipBehaviour>();
        if (refreshRate > 0f && refreshRoutine == null)
            refreshRoutine = StartCoroutine(RefreshLoop());
    }
    void OnDisable()
    {
        if (refreshRoutine != null)
        {
            StopCoroutine(refreshRoutine);
            refreshRoutine = null;
        }
    }
    IEnumerator RefreshLoop()
    {
        var wait = new WaitForSecondsRealtime(1f / refreshRate);
        while (true)
        {
            if (tooltip != null)
            {
                if (canshow && tooltip.TooltipText != null)
                {
                    tooltip.Text = CurrentText?.Invoke() ?? tooltip.Text;
                    tooltip.TooltipText.text = tooltip.Text;
                }
            }
            yield return wait;
        }
    }
    void IPointerEnterHandler.OnPointerEnter(PointerEventData e) => canshow = true;
    void IPointerExitHandler.OnPointerExit(PointerEventData e) => canshow = false;
}

// ── Utility partial ──
public static partial class Utility
{
    private static DynamicColorTextEffectManager m_effecter;

    public static DynamicColorTextEffectManager Effecter
    {
        get
        {
            if (m_effecter == null)
                m_effecter = CreateHelper<DynamicColorTextEffectManager>("EffecterManager");
            return m_effecter;
        }
    }

    public static string GradientText(string text, Color[] colors, int? _length = null, double offset = 0d) => TextFormatting.GradientText(text, colors, _length, offset);

    public static string RainbowText(string text, Color StartColor, Color EndColor) => string.Concat(text.Select((c, i) => $"<color=#{ColorUtility.ToHtmlStringRGB(Color.Lerp(StartColor, EndColor, (float)i / text.Length))}>{text[i]}</color>"));

    public static void DynamicColorToolip(SpawnableAsset asset, Color[] colors, TextInfo Name, TextInfo Desc, (string nameFmt, string descFmt) Struction)
    {
        var changer = FindTypesInWorld<ItemButtonBehaviour>().FirstOrDefault(x => x.Item == asset)
            ?.gameObject.AddComponent<TooltipTextChanger>();
        if (changer != null)
        {
            DynamicColorText colorName = null, colorDesc = null;
            string name = Name.text, desc = Desc.text;
            if (Name.colorful)
                colorName = DynamicColorText.Create(name, colors, Name.length, Name.time, Name.trans, Name.compression);
            if (Desc.colorful)
                colorDesc = DynamicColorText.Create(desc, colors, Desc.length, Desc.time, Desc.trans, Desc.compression);
            changer.CurrentText = () => $"<b>{string.Format(Struction.nameFmt, colorName ?? name)}</b>{Environment.NewLine}{string.Format(Struction.descFmt, colorDesc ?? desc)}";
        }
    }
}
