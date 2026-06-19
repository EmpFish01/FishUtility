using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyHelper;
using HarmonyLib;
using DebugTool;
using RegenerationCore;
using ZeroReflect;
using UnityEngine;
using UnityEngine.Events;
using Object = UnityEngine.Object;
using Random = UnityEngine.Random;

namespace DurabilityUtility;

public class ModifierEntry<T> { public Func<T> Additive; public int Priority = 1; }

public class PriorityValue<T>
{
    private List<ModifierEntry<T>> Additives = new List<ModifierEntry<T>>();
    public T BaseValue;
    public T Value { get { if (Additives.Count == 0) return BaseValue; int max = Additives.Max(x => x.Priority); var list = Additives.Where(x => x.Priority == max).ToList(); return list.Count > 0 ? list.Last().Additive() : BaseValue; } }
    public PriorityValue() { }
    public PriorityValue(T v) { BaseValue = v; }
    public void AddAdditive(Func<T> a) => Additives.Add(new ModifierEntry<T> { Additive = a, Priority = 1 });
    public void AddAdditive(Func<T> a, int p) => Additives.Add(new ModifierEntry<T> { Additive = a, Priority = p });
    public void AddAdditive(ModifierEntry<T> a) => Additives.Add(a);
    public void RemoveAdditive(Func<T> a) => Additives.RemoveAll(x => x.Additive == a);
    public void RemoveAdditive(ModifierEntry<T> a) => Additives.RemoveAll(x => x == a);
    public static implicit operator T(PriorityValue<T> d) => d.Value;
}

public class WeightedBool
{
    public class Vote { public Func<bool> Additive; public int Priority = 1; }
    public bool BaseValue;
    private List<Vote> Additives = new List<Vote>();
    public bool Value => GetPriorityBool(Additives, BaseValue);
    public bool Additive => GetPriorityBool(Additives, fallback: false);
    public WeightedBool(bool v = false) { BaseValue = v; }
    private static bool GetPriorityBool(IEnumerable<Vote> votes, bool fallback) { int t = 0, f = 0; foreach (var v in votes) { if (v.Additive()) t += v.Priority; else f += v.Priority; } if (t > f) return true; if (f > t) return false; return fallback; }
    public void AddAdditive(Func<bool> a) => Additives.Add(new Vote { Additive = a, Priority = 1 });
    public void AddAdditive(Func<bool> a, int p) => Additives.Add(new Vote { Additive = a, Priority = p });
    public void AddAdditive(Vote v) => Additives.Add(v);
    public void RemoveAdditive(Func<bool> a) => Additives.RemoveAll(x => x.Additive == a);
    public void RemoveAdditive(Vote v) => Additives.RemoveAll(x => x == v);
    public static implicit operator bool(WeightedBool w) => w.Value;
}

public class ModularFloat
{
    public float BaseValue;
    private List<Func<float>> Multipliers = new List<Func<float>>();
    private List<Func<float>> Additives = new List<Func<float>>();
    public float Value => BaseValue * Mult + Additive;
    public float Mult { get { float n = 1f; foreach (var m in Multipliers) n *= m(); return n; } }
    public float Additive { get { float n = 0f; foreach (var a in Additives) n += a(); return n; } }
    public ModularFloat(float v = 0f) { BaseValue = v; }
    public void AddMultiplier(Func<float> m) => Multipliers.Add(m);
    public void RemoveMultiplier(Func<float> m) => Multipliers.RemoveAll(x => x == m);
    public void AddAdditive(Func<float> a) => Additives.Add(a);
    public void RemoveAdditive(Func<float> a) => Additives.RemoveAll(x => x == a);
    public static implicit operator float(ModularFloat m) => m.Value;
}

public class ModularInt
{
    public int BaseValue;
    private List<Func<int>> Multipliers = new List<Func<int>>();
    private List<Func<int>> Additives = new List<Func<int>>();
    public int Value => BaseValue * Mult + Additive;
    public int Mult { get { int n = 1; foreach (var m in Multipliers) n *= m(); return n; } }
    public int Additive { get { int n = 0; foreach (var a in Additives) n += a(); return n; } }
    public ModularInt(int v = 0) { BaseValue = v; }
    public void AddMultiplier(Func<int> m) => Multipliers.Add(m);
    public void RemoveMultiplier(Func<int> m) => Multipliers.RemoveAll(x => x == m);
    public void AddAdditive(Func<int> a) => Additives.Add(a);
    public void RemoveAdditive(Func<int> a) => Additives.RemoveAll(x => x == a);
    public static implicit operator int(ModularInt m) => m.Value;
}

public class ContextFloat<T>
{
    public float BaseValue;
    private List<Func<T, float>> Multipliers = new List<Func<T, float>>();
    private List<Func<T, float>> Additives = new List<Func<T, float>>();
    public ContextFloat(float v = 0f) { BaseValue = v; }
    public float GetValue(T ctx) => BaseValue * GetMult(ctx) + GetAdditive(ctx);
    public float GetMult(T ctx) { float n = 1f; foreach (var m in Multipliers) n *= m(ctx); return n; }
    public float GetAdditive(T ctx) { float n = 0f; foreach (var a in Additives) n += a(ctx); return n; }
    public void AddMultiplier(Func<T, float> m) => Multipliers.Add(m);
    public void RemoveMultiplier(Func<T, float> m) => Multipliers.RemoveAll(x => x == m);
    public void AddAdditive(Func<T, float> a) => Additives.Add(a);
    public void RemoveAdditive(Func<T, float> a) => Additives.RemoveAll(x => x == a);
    public void AddConstantMultiplier(float v) => AddMultiplier((T _) => v);
    public void AddConstantAdditive(float v) => AddAdditive((T _) => v);
}

public struct LimbStatParams
{
    public LimbStatParams() { }

    public bool Bleed = true;
    public float Consciousness = 1f;
    public float Bleeding = 1f;
    public float Health = 1f;
    public float Skin = 1f;
    public float Collision = 1f;
    public float PunchAdd;
    public float PunchMult = 1f;
    public float Damage = 1f;
    public float Reaction = 1f;
    public float VitalityFragmentForce = 5f;
    public float DamageRegenRate = 0.25f;
    public float LimbRegenRate = 0f;
    public int SplatBlood = 8;
    public float Shot = 1f;
    public float ShotCrush = 150f;
    public float Stab = 1f;
    public float Explosion = 1f;
    public float Mass = 1f;
    public float Strength = 1f;
    public float BreakingThreshold = 1f;
    public bool IgnoreEMP;
    public float Charge = 1f;
    public float Thermal = 1f;
    public bool ShotNotVitality = true;
    public bool StabNotVitality = true;
    public bool IgnoreSlice;
    public bool IgnoreCrush;
    public bool IgnoreBullets;
    public bool IgnoreExplosion;
    public bool IgnoreDeath;
    public bool LimbRegen;
    public int RegenMode;
    public int SeveredMode;
    public float RegenDelay;
}

/// <summary>
/// O(1)-lookup Transform-keyed dictionary. Uses a standard
/// <see cref="Dictionary{TKey,TValue}"/> internally; Unity-destroyed
/// Transforms (native object gone but managed wrapper still alive) are
/// cleaned up lazily every <see cref="CleanupInterval"/> operations.
/// </summary>
public class TransformDict<TValue>
{
    private Dictionary<Transform, TValue> _dict;
    private int _opCounter;
    private const int CleanupInterval = 200;

    public TransformDict(int initialCapacity = 16)
    {
        _dict = new Dictionary<Transform, TValue>(initialCapacity);
    }

    public TValue this[Transform key]
    {
        get
        {
            if (TryGetValue(key, out var v))
                return v;
            throw new KeyNotFoundException();
        }
        set
        {
            if (!key) return;
            _dict[key] = value;
            MaybeCleanup();
        }
    }

    public int Count => _dict.Count;

    /// <summary>Number of Unity-destroyed entries currently in the dictionary.</summary>
    public int NullSlots
    {
        get
        {
            int n = 0;
            foreach (var kv in _dict)
                if (!kv.Key) n++;
            return n;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGetValue(Transform key, out TValue value)
    {
        if (!key)
        {
            value = default;
            return false;
        }
        MaybeCleanup();
        return _dict.TryGetValue(key, out value);
    }

    public void Add(Transform key, TValue value)
    {
        if (!key) return;
        _dict[key] = value;
        MaybeCleanup();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool ContainsKey(Transform key)
    {
        if (!key) return false;
        MaybeCleanup();
        return _dict.ContainsKey(key);
    }

    public bool Remove(Transform key)
    {
        if (!key) return false;
        MaybeCleanup();
        return _dict.Remove(key);
    }

    /// <summary>Force immediate removal of all Unity-destroyed entries.</summary>
    public void Compact()
    {
        _opCounter = 0;
        var dead = new List<Transform>();
        foreach (var kv in _dict)
            if (!kv.Key)
                dead.Add(kv.Key);
        for (int i = 0; i < dead.Count; i++)
            _dict.Remove(dead[i]);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void MaybeCleanup()
    {
        if (++_opCounter >= CleanupInterval)
            Compact();
    }
}

public static class LimbStatPresets
{
    public static readonly LimbStatParams Default = new LimbStatParams
    {
        Bleed = true,
        Consciousness = 1.5f,
        Bleeding = 0.78f,
        Health = 2f,
        Skin = 1f,
        Collision = 0.78f,
        SplatBlood = 7,
        Shot = 0.9f,
        ShotCrush = 200f,
        Stab = 0.9f,
        Explosion = 0.9f,
        Mass = 2f,
        Strength = 1f,
        BreakingThreshold = 2.5f,
        Reaction = 1f,
        Damage = 0.7f,
        LimbRegen = false
    };

    public static readonly LimbStatParams Strongest = new LimbStatParams
    {
        Bleed = true,
        Bleeding = 0.06f,
        BreakingThreshold = 1f,
        Collision = 0.01f,
        Consciousness = 22f,
        Damage = 0.7f,
        Explosion = 0.9f,
        Health = 64f,
        IgnoreEMP = false,
        Mass = 30f,
        Shot = 0.01f,
        ShotCrush = 500f,
        Skin = 0.5f,
        SplatBlood = 200,
        Stab = 0.4f,
        Strength = 0.7f,
        Reaction = -5f,
        VitalityFragmentForce = 5f,
        ShotNotVitality = true,
        StabNotVitality = true,
        DamageRegenRate = 0.01f,
        PunchMult = 1f,
        Charge = 0f,
        Thermal = 0f,
        LimbRegen = false
    };
}

public class LimbStatSettings
{
    public WeightedBool Bleed = new(true);
    public ModularFloat Consciousness = new(1f);
    public ModularFloat Bleeding = new(1f);
    public ModularFloat Health = new(1f);
    public ModularFloat Skin = new(1f);
    public ModularFloat Collision = new(1f);
    public ModularFloat Damage = new(1f);
    public ModularFloat Reaction = new(1f);
    public ModularFloat PunchMult = new(1f);
    public ModularFloat PunchAdd = new(1f);
    public ModularFloat VitalityFragmentForce = new(1f);
    public ModularFloat DamageRegenRate = new(0.25f);
    public ModularFloat LimbRegenRate = new(0.25f);
    public ModularInt SplatBlood = new(1);
    public ModularFloat Shot = new(1f);
    public ModularFloat ShotCrush = new(1f);
    public ModularFloat Stab = new(1f);
    public ModularFloat Explosion = new(1f);
    public ModularFloat Mass = new(1f);
    public ModularFloat Strength = new(1f);
    public ModularFloat BreakingThreshold = new(1f);
    public WeightedBool IgnoreEMP = new();
    public ModularFloat Charge = new(1f);
    public ModularFloat Thermal = new(1f);
    public WeightedBool ShotNotVitality = new();
    public WeightedBool StabNotVitality = new();
    public WeightedBool IgnoreSlice = new();
    public WeightedBool IgnoreCrush = new();
    public WeightedBool IgnoreBullets = new();
    public WeightedBool IgnoreDeath = new();
    public WeightedBool LimbRegen = new();
    public ModularInt RegenMode = new(0);
    public ModularInt SeveredMode = new(0);
    public ModularFloat RegenDelay = new(0.3f);

    private LimbStatParams _Params = LimbStatPresets.Default;

    /// <summary>Compiled delegates: (settings, params) => settings.Field.BaseValue = params.Field</summary>
    private static readonly Action<LimbStatSettings, LimbStatParams>[] _updateActions = InitUpdateActions();

    public LimbStatParams Params
    {
        get => GetParams();
        set { _Params = value; Update(); }
    }

    private LimbStatParams GetParams()
    {
        return _Params = new LimbStatParams
        {
            Bleed = Bleed.BaseValue,
            Consciousness = Consciousness.BaseValue,
            Bleeding = Bleeding.BaseValue,
            Health = Health.BaseValue,
            Skin = Skin.BaseValue,
            Collision = Collision.BaseValue,
            Damage = Damage.BaseValue,
            Reaction = Reaction.BaseValue,
            PunchMult = PunchMult.BaseValue,
            PunchAdd = PunchAdd.BaseValue,
            VitalityFragmentForce = VitalityFragmentForce.BaseValue,
            DamageRegenRate = DamageRegenRate.BaseValue,
            LimbRegenRate = LimbRegenRate.BaseValue,
            SplatBlood = SplatBlood.BaseValue,
            Shot = Shot.BaseValue,
            ShotCrush = ShotCrush.BaseValue,
            Stab = Stab.BaseValue,
            Explosion = Explosion.BaseValue,
            Mass = Mass.BaseValue,
            Strength = Strength.BaseValue,
            BreakingThreshold = BreakingThreshold.BaseValue,
            IgnoreEMP = IgnoreEMP.BaseValue,
            Charge = Charge.BaseValue,
            Thermal = Thermal.BaseValue,
            ShotNotVitality = ShotNotVitality.BaseValue,
            StabNotVitality = StabNotVitality.BaseValue,
            IgnoreSlice = IgnoreSlice.BaseValue,
            IgnoreCrush = IgnoreCrush.BaseValue,
            IgnoreBullets = IgnoreBullets.BaseValue,
            IgnoreDeath = IgnoreDeath.BaseValue,
            LimbRegen = LimbRegen.BaseValue,
            RegenMode = RegenMode.BaseValue,
            SeveredMode = SeveredMode.BaseValue,
            RegenDelay = RegenDelay.BaseValue,
        };
    }

    public LimbStatParams GetScaledParams()
    {
        return new LimbStatParams
        {
            Bleed = Bleed,
            Consciousness = Consciousness,
            Bleeding = Bleeding,
            Health = Health,
            Skin = Skin,
            Collision = Collision,
            Damage = Damage,
            Reaction = Reaction,
            PunchMult = PunchMult,
            PunchAdd = PunchAdd,
            VitalityFragmentForce = VitalityFragmentForce,
            DamageRegenRate = DamageRegenRate,
            LimbRegenRate = LimbRegenRate,
            SplatBlood = SplatBlood,
            Shot = Shot,
            ShotCrush = ShotCrush,
            Stab = Stab,
            Explosion = Explosion,
            Mass = Mass,
            Strength = Strength,
            BreakingThreshold = BreakingThreshold,
            IgnoreEMP = IgnoreEMP,
            Charge = Charge,
            Thermal = Thermal,
            ShotNotVitality = ShotNotVitality,
            StabNotVitality = StabNotVitality,
            IgnoreSlice = IgnoreSlice,
            IgnoreCrush = IgnoreCrush,
            IgnoreBullets = IgnoreBullets,
            IgnoreDeath = IgnoreDeath,
            LimbRegen = LimbRegen,
            RegenMode = RegenMode,
            SeveredMode = SeveredMode,
            RegenDelay = RegenDelay,
        };
    }

    private static Action<LimbStatSettings, LimbStatParams>[] InitUpdateActions()
    {
        var actions = new List<Action<LimbStatSettings, LimbStatParams>>();
        var tSettings = typeof(LimbStatSettings);
        var tParams = typeof(LimbStatParams);
        var sParam = Expression.Parameter(tSettings, "s");
        var pParam = Expression.Parameter(tParams, "p");

        foreach (var paramFi in tParams.GetFields(BindingFlags.Instance | BindingFlags.Public))
        {
            var settingsFi = tSettings.GetField(paramFi.Name, BindingFlags.Instance | BindingFlags.Public);
            if (settingsFi == null) continue;

            var baseValueFi = settingsFi.FieldType.GetField("BaseValue",
                BindingFlags.Instance | BindingFlags.Public);
            if (baseValueFi == null) continue;

            // Build: s.SettingsField.BaseValue = p.ParamField
            var settingsField = Expression.Field(sParam, settingsFi);
            var baseValue = Expression.Field(settingsField, baseValueFi);
            var paramValue = Expression.Field(pParam, paramFi);
            var value = paramFi.FieldType == baseValueFi.FieldType
                ? (Expression)paramValue
                : Expression.Convert(paramValue, baseValueFi.FieldType);
            var assign = Expression.Assign(baseValue, value);
            actions.Add(Expression.Lambda<Action<LimbStatSettings, LimbStatParams>>(
                assign, sParam, pParam).Compile());
        }
        return actions.ToArray();
    }

    public void Update()
    {
        var actions = _updateActions;
        for (int i = 0; i < actions.Length; i++)
            actions[i](this, _Params);
    }
}


public class LimbResistStorage
{
    public Dictionary<int, ModularFloat> _resists;
    public ContextFloat<int> _globalMultiplier;
    public ContextFloat<int> _globalAdditive;
    public float AdaptationScale;
    public bool AdaptToAll;
    public List<int> AdaptToAffects = new();
    public UnityEvent<int> OnFullAdapt = new();

    public LimbResistStorage()
    {
        _resists = new();
        _globalMultiplier = new(1f);
        _globalAdditive = new();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ModularFloat GetResist(int id)
    {
        return _resists.TryGetValue(id, out var v) ? v : null;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ContextFloat<int> GetGlobalMultiplier() => _globalMultiplier;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ContextFloat<int> GetGlobalAdditive() => _globalAdditive;

    public ModularFloat GetOrCreateResist(int id, float bv = 1f)
    {
        if (!_resists.TryGetValue(id, out var v))
            _resists[id] = v = new(bv);
        return v;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool ShouldAdaptTo(int id)
        => AdaptationScale > 0f && (AdaptToAll || AdaptToAffects.Contains(id));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void AdaptTo(int id, float val)
    {
        if (!ShouldAdaptTo(id))
            return;
        float n = val * AdaptationScale;
        if (n <= 0f)
            return;
        var r = GetOrCreateResist(id);
        float old = r.BaseValue;
        r.BaseValue = Mathf.Max(0f, r.BaseValue - n);
        if (old > 0f && r.BaseValue <= 0f)
            OnFullAdapt.Invoke(id);
    }

    public void PasteInfo(LimbResistSerialized info)
    {
        _resists.Clear();
        _globalMultiplier = new(info.SerializedGlobalMultiplier);
        _globalAdditive = new(info.SerializedGlobalAdditive);

        if (info.SerializedAffects == null ||
            info.SerializedValues == null ||
            info.SerializedAffects.Length != info.SerializedValues.Length)
            return;

        for (int i = 0; i < info.SerializedAffects.Length; i++)
        {
            if (!string.IsNullOrEmpty(info.SerializedAffects[i]))
                _resists[LimbResist.GetAffectID(info.SerializedAffects[i])] = new(info.SerializedValues[i]);
        }

        AdaptationScale = info.AdaptationScale;
        AdaptToAffects = info.AdaptToAffects
            .Where(x => !string.IsNullOrEmpty(x))
            .Select(LimbResist.GetAffectID)
            .ToList();
        AdaptToAll = info.AdaptToAll;
    }
}

public struct LimbResistSerialized
{
    public string[] SerializedAffects;
    public float[] SerializedValues;
    public float SerializedGlobalMultiplier, SerializedGlobalAdditive, AdaptationScale;
    public bool AdaptToAll;
    public string[] AdaptToAffects;

    public LimbResistSerialized(LimbResistStorage s)
    {
        if (s == null)
        {
            SerializedAffects = new string[0];
            SerializedValues = new float[0];
            SerializedGlobalMultiplier = 1f;
            SerializedGlobalAdditive = 0f;
            AdaptToAll = false;
            AdaptToAffects = new string[0];
            AdaptationScale = 0f;
            return;
        }

        var ns = new List<string>();
        var vs = new List<float>();
        foreach (var kv in s._resists)
        {
            if (kv.Value != null)
            {
                var n = LimbResist.GetAffectName(kv.Key);
                if (!string.IsNullOrEmpty(n))
                {
                    ns.Add(n);
                    vs.Add(kv.Value.BaseValue);
                }
            }
        }
        SerializedAffects = ns.ToArray();
        SerializedValues = vs.ToArray();
        SerializedGlobalMultiplier = s._globalMultiplier?.BaseValue ?? 1f;
        SerializedGlobalAdditive = s._globalAdditive?.BaseValue ?? 0f;
        AdaptationScale = s.AdaptationScale;
        AdaptToAffects = s.AdaptToAffects.Select(LimbResist.GetAffectName).ToArray();
        AdaptToAll = s.AdaptToAll;
    }
}

public class LimbResistSerializationHelper : MonoBehaviour
{
    public LimbResistSerialized SerializedStorage;
    public bool BeingSerialized;
    public LimbResistStorage Storage = new();

    private void Awake()
        => Storage = LimbResist.GetResistStorage(transform, root: true);

    public void OnAfterDeserialise()
    {
        if (BeingSerialized)
            Storage.PasteInfo(SerializedStorage);
    }

    public void OnBeforeSerialise()
    {
        SerializedStorage = new(Storage);
        BeingSerialized = true;
    }
}

public static class LimbResist
{
    private static readonly List<string> _affects = new();
    private static readonly Dictionary<string, int> _idMap = new();
    private static readonly TransformDict<LimbResistStorage> _unique = new(100), _root = new(50);

    public static int RegisteredAffectsCount => _affects.Count;

    public static int GetAffectID(string name)
    {
        if (_idMap.TryGetValue(name, out var v))
            return v;
        int id = _affects.Count;
        _affects.Add(name);
        _idMap[name] = id;
        return id;
    }

    public static ModularFloat GetResist(int id, Transform t, bool root = false, float def = 1f)
    {
        var d = root ? _root : _unique;
        var k = root ? t.root : t;
        if (!d.TryGetValue(k, out var s))
            d[k] = s = new();
        return s.GetOrCreateResist(id, def);
    }

    public static float GetResist(Transform t, int id, bool root = false, float def = 1f)
        => GetResist(id, t, root, def);

    public static LimbResistStorage GetResistStorage(Transform t, bool root = false)
    {
        var d = root ? _root : _unique;
        var k = root ? t.root : t;
        if (!d.TryGetValue(k, out var s))
            d[k] = s = new();
        return s;
    }

    public static ContextFloat<int> GetGlobalResistMultiplier(Transform t, bool root = false)
        => GetResistStorage(t, root).GetGlobalMultiplier();

    public static ContextFloat<int> GetGlobalResistAdditive(Transform t, bool root = false)
        => GetResistStorage(t, root).GetGlobalAdditive();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float GetResistVolume(int id, Transform t, float adapt = 0.01f, bool withAdd = true)
    {
        float n1 = 1f, n2 = 1f, n3 = 0f;

        if (_unique.TryGetValue(t, out var u))
        {
            if (adapt > 0f)
                u.AdaptTo(id, adapt);
            n1 = (u.GetResist(id)?.Value ?? 1f) * u.GetGlobalMultiplier().GetValue(id);
            if (withAdd)
                n3 += u.GetGlobalAdditive().GetValue(id);
        }
        if (_root.TryGetValue(t.root, out var r))
        {
            if (adapt > 0f)
                r.AdaptTo(id, adapt);
            n2 = (r.GetResist(id)?.Value ?? 1f) * r.GetGlobalMultiplier().GetValue(id);
            if (withAdd)
                n3 += r.GetGlobalAdditive().GetValue(id);
        }

        float result = n1 * n2;
        if (withAdd)
            result += n3;
        return result;
    }

    public static float GetResistVolume(Transform t, int id, float adapt = 0.01f, bool withAdd = true)
        => GetResistVolume(id, t, adapt, withAdd);

    public static void SetAdaptationScale(Transform t, float s, bool root = false)
        => GetResistStorage(t, root).AdaptationScale = s;

    public static void SetAdaptToAll(Transform t, bool all, bool root = false)
        => GetResistStorage(t, root).AdaptToAll = all;

    public static void AddAdaptToAffect(Transform t, int id, bool root = false)
    {
        var s = GetResistStorage(t, root);
        if (!s.AdaptToAffects.Contains(id))
            s.AdaptToAffects.Add(id);
    }

    public static void RemoveAdaptToAffect(Transform t, int id, bool root = false)
        => GetResistStorage(t, root).AdaptToAffects.Remove(id);

    public static string GetAffectName(int id)
        => (id < 0 || id >= _affects.Count) ? null : _affects[id];

    public static void CompactStorages()
    {
        _unique.Compact();
        _root.Compact();
    }
}


public class LimbStatEntry
{
    public LimbBehaviour limb;
    public CirculationBehaviour circ;
    public LimbStatSettings Settings = new();
    public ModularFloat GlobalMult = new(1f);
    public float LocalRegenerationSpeed;
    public float LastFragmentForce;
    public LimbStatData Parent;
    public WeightedBool CanPunch = new();
    public bool PunchCooldown;
    public float shotHeat;
    private int _paramTick, _damagePointTick;
    private static ContactPoint2D[] _buf = new ContactPoint2D[8];

    [ThreadStatic]
    public static bool _isFragmentationRay;

    [ThreadStatic]
    public static bool _isRegenSystemCrush;

    [ThreadStatic]
    public static bool _isRegenSystemSlice;

    private struct PhysicalPropertiesSnapshot
    {
        public float Softness;
        public float BulletSpeedAbsorptionPower;
        public float Brittleness;
        public float Buoyancy;
        public float Flammability;
        public float Burnrate;
        public float BurningTemperatureThreshold;
        public float HeatTransferSpeedMultiplier;
        public float ImpactIntensityMutliplier;
        public float HitVolumeMultiplier;
        public bool Conducting;
        public bool SparksOnSlide;
    }

    private PhysicalPropertiesSnapshot _origPhysProps;
    private PhysicalProperties _physPropsClone;
    private bool _physPropsInitialized;

    // Compiled getters: field name → Func<LimbStatSettings, object> (zero-boxing read)
    private static readonly Dictionary<string, Func<LimbStatSettings, object>> _settingsFieldGetters = new();
    private static bool _fieldsCached;

    public void LinkToPerson(LimbStatData personData)
    {
        if (personData == null)
            return;

        var sType = typeof(LimbStatSettings);
        if (!_fieldsCached)
        {
            var p = Expression.Parameter(typeof(LimbStatSettings));
            foreach (var fi in sType.GetFields(BindingFlags.Instance | BindingFlags.Public))
            {
                if (fi.Name != "Params")
                {
                    _settingsFieldGetters[fi.Name] = Expression.Lambda<Func<LimbStatSettings, object>>(
                        Expression.Convert(Expression.Field(p, fi), typeof(object)), p).Compile();
                }
            }
            _fieldsCached = true;
        }

        // Capture once outside loop — avoids closing over personData in every lambda
        var pSettings = personData.Settings;
        var pGlobalMult = personData.GlobalMult;

        foreach (var kv in _settingsFieldGetters)
        {
            var getter = kv.Value;
            var val = getter(Settings);
            if (val is ModularFloat mf)
            {
                mf.AddMultiplier(() =>
                {
                    var v = getter(pSettings);
                    return v is ModularFloat mf2 ? mf2.Value : 1f;
                });
            }
            else if (val is ModularInt mi)
            {
                mi.AddMultiplier(() =>
                {
                    var v = getter(pSettings);
                    return v is ModularInt mi2 ? mi2.Value : 1;
                });
            }
            else if (val is WeightedBool wb)
            {
                wb.AddAdditive(() =>
                {
                    var v = getter(pSettings);
                    return v is WeightedBool wb2 && wb2.Value;
                });
            }
        }
        GlobalMult.AddMultiplier(() => pGlobalMult.Value);
    }

    public void ApplyParams(LimbStatParams p)
    {
        if (limb)
            Settings.Params = p;
    }

    private void ClonePhysicalProps()
    {
        if (!limb)
            return;
        var phys = limb.PhysicalBehaviour;
        if (!phys || !phys.Properties)
            return;

        var orig = phys.Properties;
        _origPhysProps.Softness = orig.Softness;
        _origPhysProps.BulletSpeedAbsorptionPower = orig.BulletSpeedAbsorptionPower;
        _origPhysProps.Brittleness = orig.Brittleness;
        _origPhysProps.Buoyancy = orig.Buoyancy;
        _origPhysProps.Flammability = orig.Flammability;
        _origPhysProps.Burnrate = orig.Burnrate;
        _origPhysProps.BurningTemperatureThreshold = orig.BurningTemperatureThreshold;
        _origPhysProps.HeatTransferSpeedMultiplier = orig.HeatTransferSpeedMultiplier;
        _origPhysProps.ImpactIntensityMutliplier = orig.ImpactIntensityMutliplier;
        _origPhysProps.HitVolumeMultiplier = orig.HitVolumeMultiplier;
        _origPhysProps.Conducting = orig.Conducting;
        _origPhysProps.SparksOnSlide = orig.SparksOnSlide;

        _physPropsClone = Object.Instantiate(orig);
        phys.Properties = _physPropsClone;
        _physPropsInitialized = true;
    }

    private void ApplyPhysicalProps()
    {
        if (!_physPropsInitialized || _physPropsClone == null)
            return;
        if (!limb)
            return;
        var phys = limb.PhysicalBehaviour;
        if (!phys)
            return;

        // 引用守护：防止外部系统替换 phys.Properties
        if (phys.Properties != _physPropsClone)
            phys.Properties = _physPropsClone;

        float stab = (float)Settings.Stab;
        float shot = (float)Settings.Shot;
        float collision = (float)Settings.Collision;
        float thermal = (float)Settings.Thermal;
        float mass = (float)Settings.Mass;
        float charge = (float)Settings.Charge;

        // ── Multiply: higher param = more vulnerable ──
        _physPropsClone.Softness = Mathf.Clamp01(_origPhysProps.Softness * stab);
        _physPropsClone.Brittleness = Mathf.Clamp01(_origPhysProps.Brittleness * collision);
        _physPropsClone.Flammability = Mathf.Clamp01(_origPhysProps.Flammability * thermal);
        _physPropsClone.Burnrate = Mathf.Clamp01(_origPhysProps.Burnrate * thermal);
        _physPropsClone.HeatTransferSpeedMultiplier = Mathf.Clamp01(_origPhysProps.HeatTransferSpeedMultiplier * thermal);
        _physPropsClone.ImpactIntensityMutliplier = _origPhysProps.ImpactIntensityMutliplier * collision;
        _physPropsClone.HitVolumeMultiplier = _origPhysProps.HitVolumeMultiplier * collision;

        // ── Divide: higher param = more resistant ──
        _physPropsClone.BulletSpeedAbsorptionPower = _origPhysProps.BulletSpeedAbsorptionPower / shot;
        _physPropsClone.BurningTemperatureThreshold = _origPhysProps.BurningTemperatureThreshold / thermal;
        _physPropsClone.Buoyancy = _origPhysProps.Buoyancy / mass;

        // ── Bool toggle at extreme (near-zero = full resistance) ──
        _physPropsClone.Conducting = _origPhysProps.Conducting && charge > 0.001f;
        _physPropsClone.SparksOnSlide = _origPhysProps.SparksOnSlide && collision > 0.001f;
    }

    public void SyncPhysicalProps()
    {
        if (!limb)
            return;

        if (!_physPropsInitialized)
            ClonePhysicalProps();
        ApplyPhysicalProps();

        float ratio = limb.Health / limb.InitialHealth;
        limb.InitialHealth = Mathf.Clamp(50f * (float)Settings.Health, 0, float.MaxValue);
        limb.Health = limb.InitialHealth * ratio;
        LocalRegenerationSpeed = Mathf.Lerp(Settings.Health, 1f, 0.88f) * 0.1f * (float)Settings.Health;
        limb.PhysicalBehaviour.TrueInitialMass = 0.08f * (float)Settings.Mass;
        limb.BreakingThreshold = 5f * (float)Settings.Mass * (float)Settings.BreakingThreshold;
        DurabilityReflectCache.SetJointFragility(limb);
        limb.BaseStrength = 8.5f * (float)Settings.Mass * (float)Settings.Strength / 0.7f;
        limb.SkinMaterialHandler.intensityMultiplier = Settings.Skin;
        limb.PhysicalBehaviour.RecalculateMassBasedOnSize();
        limb.GForcePassoutThreshold = 0f;
        limb.PhysicalBehaviour.Deletable = limb.PhysicalBehaviour.Disintegratable = !(bool)Settings.IgnoreCrush;
    }

    public void TickParams()
    {
        if (++_paramTick >= 30)
        {
            _paramTick = 0;
            SyncPhysicalProps();
        }
    }

    public void Recover(float deltaTime)
    {
        if (!limb)
            return;

        // ── Cache repeated ModularFloat / WeightedBool evaluations ──
        // Each (float)Settings.X / (bool)Settings.Y iterates all
        // modifiers; pull them into locals once at the top.
        bool ignoreDeath = Settings.IgnoreDeath;
        bool limbRegen = Settings.LimbRegen;
        float reaction = Settings.Reaction;
        float consciousness = Settings.Consciousness;

        if (!limbRegen && !ignoreDeath && (!limb.Person.IsAlive() || !limb.IsConsideredAlive))
            return;

        float r = Settings.DamageRegenRate;
        if (float.IsNaN(r)) r = 0f;
        if (float.IsNaN(deltaTime)) deltaTime = 0f;
        if (r <= 0f)
            return;

        float dtR = deltaTime * r;
        if (float.IsNaN(dtR)) dtR = 0f;
        bool changed = false;
        var smh = limb.SkinMaterialHandler;
        var phys = limb.PhysicalBehaviour;

        float recoverRate = dtR * 0.5f;
        var damagePoints = smh.damagePoints;
        int pointCount = smh.currentDamagePointCount;
        for (int i = 0; i < pointCount; i++)
        {
            if (damagePoints[i].z > 0f)
            {
                ref var dp = ref damagePoints[i];
                dp.z -= recoverRate;
                if (dp.z <= 0.01f)
                    dp = DamagePoint.None;
                changed = true;
            }
        }

        if (float.IsNaN(phys.Charge) || float.IsInfinity(phys.Charge) || float.IsNaN(phys.charge) || float.IsInfinity(phys.charge))
            phys.charge = phys.Charge = 0f;

        if (float.IsNaN(phys.Temperature) || float.IsInfinity(phys.Temperature))
            phys.Temperature = 0f;

        if (float.IsNaN(phys.BurnProgress) || float.IsInfinity(phys.BurnProgress))
            phys.BurnProgress = 0f;

        if (phys.BurnProgress > 0f)
        {
            phys.BurnProgress = Mathf.Clamp01(phys.BurnProgress - dtR * 0.16f);
            if (phys.BurnProgress < 0.05f)
                phys.Extinguish();
            changed = true;
        }

        if (float.IsNaN(smh.AcidProgress) || float.IsInfinity(smh.AcidProgress))
            smh.AcidProgress = 0f;

        if (smh.AcidProgress > 0f)
        {
            if (Parent.RegenData?.GetDataForLimb(limb)?.BornCoroutine == null)
            {
                smh.AcidProgress = Mathf.Clamp01(smh.AcidProgress - dtR * 0.8f);
                changed = true;
            }
        }

        if (float.IsNaN(smh.RottenProgress) || float.IsInfinity(smh.RottenProgress))
            smh.RottenProgress = 0f;

        if (!limb.IsZombie && smh.RottenProgress > 0f)
        {
            smh.RottenProgress = Mathf.Clamp01(smh.RottenProgress - dtR * 0.8f);
            changed = true;
        }
        if (changed)
            smh.Sync();

        if (limb.Broken)
            limb.HealBone();

        if (float.IsNaN(limb.Health) || float.IsInfinity(limb.Health))
            limb.Health = 0f;

        if (limb.Health < limb.InitialHealth)
            limb.Health = Mathf.Clamp(limb.Health + LocalRegenerationSpeed * (r + 1f) * deltaTime, 0f, limb.InitialHealth);

        if (reaction < 0f)
            limb.Numbness *= Mathf.Clamp01(1f + Mathf.Clamp(reaction, float.MinValue, 0f) / 10f);

        {
            var c = circ;
            float bleedClamp = 1f - Mathf.Clamp01(dtR * 2f);
            c.BleedingRate *= bleedClamp;
            c.InternalBleedingIntensity *= bleedClamp;
            if (c.BleedingRate < 0.01f && c.InternalBleedingIntensity < 0.01f && c.BleedingParticles.Count > 0)
                c.HealBleeding();

            c.BloodFlow += dtR;

            if (!c.IsPump && limb.Health > limb.InitialHealth * 0.25f && c.BloodFlow > 0.5f)
                c.IsPump = c.WasInitiallyPumping;

            if (limb.HasBrain && limb.Person.Braindead && limb.Health > 0.01f)
            {
                limb.Person.Braindead = false;
                limb.Person.BrainDamaged = false;
                limb.Person.Consciousness = Mathf.MoveTowards(
                    limb.Person.Consciousness, 1f,
                    r * consciousness * deltaTime * 0.0005f);
            }

            if (ignoreDeath && limb.HasBrain && limb.Person.Consciousness < 1f)
                limb.Person.Consciousness = Mathf.MoveTowards(
                    limb.Person.Consciousness, 1f,
                    r * consciousness * deltaTime * 0.0005f);

            c.AddLiquid(limb.GetOriginalBloodType(), (c.Limits.y - c.GetAmountOfBlood()) * r * 2f * deltaTime);
        }
    }

    private Vector4[] _compactPts;
    private float[] _compactTss;

    public void CompactDamagePoints()
    {
        if (!limb)
            return;
        if (++_damagePointTick < 600)
            return;

        _damagePointTick = 0;
        var smh = limb.SkinMaterialHandler;
        if (smh == null)
            return;

        if (_compactPts == null)
        {
            _compactPts = new Vector4[128];
            _compactTss = new float[128];
        }

        int count = 0;
        for (int i = 0; i < 128; i++)
        {
            if (smh.damagePoints[i].z > 0.01f)
            {
                _compactPts[count] = smh.damagePoints[i];
                _compactTss[count] = smh.damagePointTimeStamps[i];
                count++;
            }
        }

        if (count != smh.currentDamagePointCount)
        {
            smh.currentDamagePointCount = count;
            float t = Time.time;
            for (int j = 0; j < 128; j++)
            {
                smh.damagePoints[j] = DamagePoint.None;
                smh.damagePointTimeStamps[j] = t;
            }
            for (int k = 0; k < count; k++)
            {
                smh.damagePoints[k] = _compactPts[k];
                smh.damagePointTimeStamps[k] = _compactTss[k];
            }
            smh.Sync();
        }
    }


    public float MassRatio => limb.PhysicalBehaviour.rigidbody.mass / limb.PhysicalBehaviour.TrueInitialMass;

    public void Shot(Shot shot)
    {
        float shotVal = Settings.Shot;
        float globalMult = GlobalMult;
        if (shotVal == 0f || globalMult == 0f)
            return;

        bool bleed = Settings.Bleed;
        bool shotNotVitality = Settings.ShotNotVitality;
        float bleedingVal = Settings.Bleeding;
        float shotCrushVal = Settings.ShotCrush;
        float reactionVal = Settings.Reaction;
        float damageVal = Settings.Damage;
        float skinVal = Settings.Skin;

        shot.damage = shot.damage * 0.12f * shotVal
                      * globalMult
                      * UserPreferenceManager.Current.FragilityMultiplier
                      / MassRatio;
        if (shot.damage < 1f)
            return;

        shotHeat += 1f;

        if (bleed &&
            Random.value * shot.damage * 0.5f > limb.Health / limb.InitialHealth &&
            Random.value < limb.PhysicalBehaviour.Properties.Softness + 0.001f)
        {
            bool a = limb.CirculationBehaviour.IsWorldPointInArteryRect(shot.point);
            limb.CirculationBehaviour.BleedingRate += shot.damage / 3.5f
                * (float)(a ? 2 : 1)
                * bleedingVal;
            if (circ.BleedingPointCount < 8)
                CreateBleedingParticle(shot.point, shot.normal, a ? 1 : 0);
        }

        if (bleed &&
            !shotNotVitality &&
            circ.IsPump &&
            limb.Health < limb.InitialHealth * 0.3f * Random.value)
            circ.IsPump = false;

        circ.GunshotWoundCount++;

        if (bleed &&
            !shotNotVitality &&
            limb.HasLungs &&
            limb.Health < limb.InitialHealth * 0.45f * Random.value)
            limb.LungsPunctured = true;

        if ((bool)limb.Person.PoolableImpactEffect &&
            (double)limb.PhysicalBehaviour.BurnProgress < 0.6 &&
            limb.SkinMaterialHandler.AcidProgress < 0.7f &&
            shot.damage > limb.Person.ImpactEffectShotDamageThreshold &&
            !UserPreferenceManager.Current.GorelessMode &&
            UserPreferenceManager.Current.ChunkyShotParticles &&
            Random.value > 0.8f)
        {
            var go = PoolGenerator.Instance.RequestPrefab(limb.Person.PoolableImpactEffect, shot.point);
            if ((bool)go)
                go.transform.right = shot.normal;
        }

        if (bleed && limb.NodeBehaviour.IsConnectedToRoot)
        {
            float nr = Mathf.Clamp(reactionVal, 0f, float.MaxValue);
            limb.Person.AdrenalineLevel += shot.damage * Random.value * 0.025f * Mathf.Abs(reactionVal);
            limb.Person.ShockLevel += shot.damage * Random.value * 0.025f * (0f - reactionVal);
            limb.Person.Wince(shot.damage * 15f);
            limb.Numbness += shot.damage / 10f * nr;
            if (!limb.IsParalysed)
                limb.Person.AddPain(shot.damage * Random.value * 0.025f * nr);
        }

        if (shotCrushVal != 0f &&
            shot.CanCrush &&
            shot.damage > shotCrushVal &&
            UserPreferenceManager.Current.LimbCrushing)
        {
            if (UserPreferenceManager.Current.StopAnimationOnDamage)
                limb.Person.OverridePoseIndex = -1;
            limb.Crush();
        }

        if (shotHeat > 5f && Random.value > 0.8f)
        {
            if (limb.HasJoint && Random.value > 0.3f)
                HandleSlice();
            else if (shotCrushVal != 0f &&
                     shot.CanCrush &&
                     UserPreferenceManager.Current.LimbCrushing)
            {
                if (UserPreferenceManager.Current.StopAnimationOnDamage)
                    limb.Person.OverridePoseIndex = -1;
                limb.Crush();
            }
        }

        if (bleed)
            circ.InternalBleedingIntensity += shot.damage * bleedingVal;

        limb.Damage(shot.damage * 10f * damageVal);
        limb.SkinMaterialHandler.AddDamagePoint(
            DamageType.Bullet,
            shot.point,
            Mathf.Max(50f * shotVal, shot.damage * 10f) * skinVal
        );
    }

    public void ExitShot(Shot shot)
    {
        if ((float)Settings.Shot == 0f || (float)GlobalMult == 0f)
            return;

        shot.damage = 0.1f * shot.damage
                      * (float)Settings.Shot
                      * (float)GlobalMult
                      * UserPreferenceManager.Current.FragilityMultiplier
                      / MassRatio;
        if (shot.damage < 1f)
            return;

        limb.SkinMaterialHandler.AddDamagePoint(
            DamageType.Bullet,
            shot.point,
            shot.damage * 10f * (float)Settings.Skin
        );

        if ((bool)limb.Person.PoolableImpactEffect &&
            (double)limb.PhysicalBehaviour.BurnProgress < 0.6 &&
            limb.SkinMaterialHandler.AcidProgress < 0.7f &&
            shot.damage > limb.Person.ImpactEffectShotDamageThreshold &&
            !UserPreferenceManager.Current.GorelessMode &&
            UserPreferenceManager.Current.ChunkyShotParticles &&
            Random.value > 0.8f)
        {
            var go = PoolGenerator.Instance.RequestPrefab(limb.Person.PoolableImpactEffect, shot.point);
            if ((bool)go)
                go.transform.right = shot.normal;
        }

        bool a = circ.IsWorldPointInArteryRect(shot.point);
        if ((bool)Settings.Bleed &&
            shot.damage > 6f * limb.Health / limb.InitialHealth &&
            Random.value < limb.PhysicalBehaviour.Properties.Softness + 0.001f)
        {
            circ.BleedingRate += shot.damage / 15f
                * (float)(a ? 4 : 1)
                * (float)Settings.Bleeding;
            circ.CreateBleedingParticle(shot.point, shot.normal, a ? 1 : 0);
        }

        if ((bool)Settings.Bleed &&
            !limb.IsConsideredAlive &&
            !limb.KillShotParticlesEmitted &&
            (bool)(Object)(object)limb.KillShotParticles &&
            !UserPreferenceManager.Current.GorelessMode &&
            Random.value > 0.7f)
        {
            limb.KillShotParticles.transform.right = shot.normal;
            limb.KillShotParticles.Play();
            limb.PhysicalBehaviour.PlayClipOnce(limb.Person.DismembermentClips.PickRandom());
            limb.KillShotParticlesEmitted = true;
        }

        if (!Settings.Bleed || circ.GetAmountOfBlood() < 0.05f)
            return;

        var cc = circ.GetComputedColor(limb.GetOriginalBloodType().Color);
        for (int i = 0; i < Random.Range(1, 4); i++)
        {
            var rh = Physics2D.Raycast(
                shot.point,
                shot.normal + Random.insideUnitCircle * 0.4f,
                3f
            );
            if (rh && (bool)rh.transform)
                rh.transform.gameObject.SendMessage(
                    "Decal",
                    new DecalInstruction(limb.BloodDecal, rh.point, cc),
                    SendMessageOptions.DontRequireReceiver
                );
        }
    }

    public void Stabbed(Stabbing stab)
    {
        if (!stab.stabber.StabCausesWound ||
                (float)Settings.Stab == 0f ||
                (float)GlobalMult == 0f)
            return;

        float vel = Mathf.Clamp(
            (limb.PhysicalBehaviour.rigidbody.velocity - stab.stabber.rigidbody.velocity).magnitude,
            1f,
            float.MaxValue
        );
        float dmg = UserPreferenceManager.Current.FragilityMultiplier
                    / MassRatio
                    * (float)Settings.Stab
                    * (float)GlobalMult
                    * vel;
        if (dmg < 1f)
            return;

        circ.StabWoundCount++;
        limb.Damage(dmg * (float)Settings.Damage);

        if ((bool)Settings.Bleed)
        {
            circ.InternalBleedingIntensity += dmg * Random.value * 0.2f;
            if (circ.IsPump &&
                !Settings.StabNotVitality &&
                limb.Health < limb.InitialHealth * 0.3f * Random.value)
                circ.IsPump = false;
            if (circ.GetAmountOfBlood() > 0.2f)
                stab.stabber.SendMessage(
                    "Decal",
                    new DecalInstruction(limb.BloodDecal, stab.point,
                        circ.GetComputedColor(limb.GetOriginalBloodType().Color)),
                    SendMessageOptions.DontRequireReceiver
                );
            if (limb.HasLungs &&
                !Settings.StabNotVitality &&
                limb.Health < limb.InitialHealth * 0.35f * Random.value)
                limb.LungsPunctured = true;
            limb.Wince(dmg * 15f);
            if (limb.NodeBehaviour.IsConnectedToRoot)
            {
                limb.Person.ShockLevel += dmg * Random.value * 0.025f;
                limb.Person.AdrenalineLevel += dmg * Random.value * 0.025f;
            }
            limb.Numbness += dmg * Random.value * 0.025f;
        }
    }

    public void Unstabbed(Stabbing stab)
    {
        if (stab.stabber.StabCausesWound &&
            (float)Settings.Stab != 0f &&
            (float)GlobalMult != 0f)
        {
            float vel = Mathf.Clamp(
                (limb.PhysicalBehaviour.rigidbody.velocity - stab.stabber.rigidbody.velocity).magnitude,
                1f,
                float.MaxValue
            );
            float threshold = UserPreferenceManager.Current.FragilityMultiplier
                              / MassRatio
                              * (float)Settings.Stab
                              * (float)GlobalMult
                              * vel;
            if (!(threshold < 1f) && (bool)Settings.Bleed)
            {
                bool a = circ.IsWorldPointInArteryRect(stab.point);
                circ.BleedingRate += (float)(a ? 4 : 1) * (float)Settings.Bleeding;
                circ.CreateBleedingParticle(stab.point, stab.normal, a ? 1 : 0);
            }
        }
    }

    public void HandleShot(Shot shot)
    {
        if (limb.ImmuneToDamage)
            return;

        // Cache repeatedly-accessed settings (each (float) cast evaluates ModularFloat.Value)
        float shotVal = (float)Settings.Shot;
        bool shotNotVitality = (bool)Settings.ShotNotVitality;
        float reactionVal = (float)Settings.Reaction;
        float shotCrushVal = (float)Settings.ShotCrush;
        float bleedingVal = (float)Settings.Bleeding;
        float consciousnessVal = (float)Settings.Consciousness;
        float reactionClamped = reactionVal > 0f ? reactionVal : 0f;

        shot.damage /= MassRatio;
        shot.damage *= UserPreferenceManager.Current.FragilityMultiplier;
        shot.damage *= shotVal;

        if (limb.IsAndroid)
        {
            if (shot.damage < 40f)
                return;
            shot.damage *= 0.2f;
        }
        else
            shotHeat += 1f;

        if (limb.HasLungs &&
            !shotNotVitality &&
            !limb.IsAndroid &&
            Random.value > 0.9f)
            limb.LungsPunctured = true;

        bool vital = limb.IsWorldPointInVitalPart(shot.point) &&
                     Random.value > 0.05f &&
                     shotVal < 0.1f &&
                     !shotNotVitality;

        float dmg = (vital ? 7f : 0.1f) * shot.damage;

        if (!UserPreferenceManager.Current.GorelessMode &&
            UserPreferenceManager.Current.ChunkyShotParticles &&
            (bool)limb.Person.PoolableImpactEffect &&
            (double)limb.PhysicalBehaviour.BurnProgress < 0.6 &&
            limb.SkinMaterialHandler.AcidProgress < 0.7f &&
            dmg > limb.Person.ImpactEffectShotDamageThreshold &&
            Random.value > 0.8f)
        {
            var go = PoolGenerator.Instance.RequestPrefab(limb.Person.PoolableImpactEffect, shot.point);
            if ((bool)go)
                go.transform.right = shot.normal;
        }

        if (limb.ConnectedLimbs != null)
        {
            foreach (var cl in limb.ConnectedLimbs)
            {
                if ((bool)cl)
                    cl.Numbness += 0.5f * reactionClamped;
            }
        }

        if (limb.NodeBehaviour.IsConnectedToRoot)
        {
            limb.Person.AdrenalineLevel += Random.value;
            limb.Person.Consciousness -= Random.Range(0.02f, 0.1f)
                * (consciousnessVal / 1000f);

            if (!limb.IsAndroid && !limb.IsZombie)
            {
                limb.Person.ShockLevel += dmg * Random.value * 0.0025f * reactionClamped;
                limb.Person.Wince(300f);
                limb.Numbness = 1f * reactionClamped;
                if (Random.value * limb.Vitality > 0.5f &&
                    Random.value > 0.6f &&
                    !limb.IsParalysed)
                    limb.Person.AddPain(Random.value * 2f * reactionVal);
                if (dmg > 2f * Random.value)
                {
                    if (shot.normal.x > 0f == limb.Person.transform.localScale.x > 0f)
                        limb.Person.DesiredWalkingDirection -= Random.value * 3f * reactionClamped;
                    else
                        limb.Person.DesiredWalkingDirection += Random.value * 3f * reactionClamped;
                }
                limb.Person.SendMessage("Shot", shot);
            }

            if (limb.HasBrain && !limb.IsAndroid && !shotNotVitality)
            {
                if (limb.IsZombie && Random.value > 0.2f)
                    return;
                float th = vital ? 0.1f : 0.8f;
                if (Random.value > th)
                    limb.Health = 0f;
                if (Random.value > th)
                    limb.Person.Consciousness = 0f;
            }
        }

        if (shot.CanCrush &&
            UserPreferenceManager.Current.LimbCrushing &&
            shot.damage > shotCrushVal &&
            Mathf.Clamp((shot.damage - shotCrushVal) * 0.005f, 0.3f, 0.8f) > Random.value)
        {
            if (UserPreferenceManager.Current.StopAnimationOnDamage)
                limb.Person.OverridePoseIndex = -1;
            LimbStatManager.StartManagedCoroutine(CrushNextFrame());
        }

        if (limb.IsZombie || Random.value < 0.01f)
            limb.Damage(dmg * limb.ShotDamageMultiplier * 0.01f);
        else
        {
            if (!limb.IsAndroid)
            {
                circ.InternalBleedingIntensity += dmg;
                if (vital)
                    circ.InternalBleedingIntensity += 5f * bleedingVal;
            }
            limb.Damage(Mathf.Min(limb.InitialHealth / 2f, dmg * limb.ShotDamageMultiplier * 2f));
        }

        limb.SkinMaterialHandler.AddDamagePoint(
            DamageType.Bullet,
            shot.point,
            Mathf.Max(50f, shot.damage * 0.1f)
        );
    }

    public void HandleExitShot(Shot shot)
    {
        if (limb.ImmuneToDamage)
            return;

        shot.damage /= MassRatio;
        shot.damage *= UserPreferenceManager.Current.FragilityMultiplier;
        shot.damage *= Settings.Shot;

        limb.SkinMaterialHandler.AddDamagePoint(
            DamageType.Bullet,
            shot.point,
            Mathf.Max(60f, shot.damage * 0.4f) * (float)Settings.Skin
        );

        if (!UserPreferenceManager.Current.GorelessMode &&
            UserPreferenceManager.Current.ChunkyShotParticles &&
            (bool)limb.Person.PoolableImpactEffect &&
            (double)limb.PhysicalBehaviour.BurnProgress < 0.6 &&
            limb.SkinMaterialHandler.AcidProgress < 0.7f &&
            shot.damage > limb.Person.ImpactEffectShotDamageThreshold &&
            Random.value > 0.6f)
        {
            var go = PoolGenerator.Instance.RequestPrefab(limb.Person.PoolableImpactEffect, shot.point);
            if ((bool)go)
                go.transform.right = shot.normal;
        }

        if (limb.HasLungs &&
            !Settings.ShotNotVitality &&
            !limb.IsAndroid &&
            Random.value > 0.9f)
            limb.LungsPunctured = true;

        if (UserPreferenceManager.Current.StopAnimationOnDamage &&
            limb.NodeBehaviour.IsConnectedToRoot &&
            (float)Settings.Reaction > 0.6f)
            limb.Person.OverridePoseIndex = -1;

        if (!UserPreferenceManager.Current.GorelessMode &&
            !limb.KillShotParticlesEmitted &&
            limb.Health <= float.Epsilon &&
            (bool)(Object)(object)limb.KillShotParticles &&
            Random.value > 0.7f)
        {
            limb.KillShotParticles.transform.right = shot.normal;
            limb.KillShotParticles.Play();
            limb.PhysicalBehaviour.PlayClipOnce(limb.Person.DismembermentClips.PickRandom());
            limb.KillShotParticlesEmitted = true;
        }

        if (limb.IsAndroid || circ.GetAmountOfBlood() < 0.05f || !Settings.Bleed)
            return;

        var cc = circ.GetComputedColor(limb.GetOriginalBloodType().Color);
        for (int i = 0; i < Random.Range(1, 4); i++)
        {
            var rh = Physics2D.Raycast(
                shot.point,
                shot.normal + Random.insideUnitCircle * 0.4f,
                3f
            );
            if (rh && (bool)rh.transform)
                rh.transform.gameObject.SendMessage(
                    "Decal",
                    new DecalInstruction(limb.BloodDecal, rh.point, cc),
                    SendMessageOptions.DontRequireReceiver
                );
        }
    }

    public void HandleStabbed(Stabbing stab)
    {
        if (limb.ImmuneToDamage || !stab.stabber.StabCausesWound)
            return;
        if ((float)Settings.Stab == 0f || (float)GlobalMult == 0f)
            return;

        if (circ.GetAmountOfBlood() > 0.2f)
            stab.stabber.SendMessage(
                "Decal",
                new DecalInstruction(
                    limb.BloodDecal,
                    stab.point,
                    circ.GetComputedColor(limb.GetOriginalBloodType().Color)
                ),
                SendMessageOptions.DontRequireReceiver
            );

        if (limb.HasLungs &&
            !Settings.StabNotVitality &&
            !limb.IsAndroid &&
            Random.value > 0.9f)
            limb.LungsPunctured = true;

        bool vital = limb.IsWorldPointInVitalPart(stab.point) &&
                     Random.value > 0.05f &&
                     !Settings.StabNotVitality;

        limb.Damage(
            limb.Health * 0.5f
            * (limb.IsZombie ? 0.1f : 1f)
            * (float)(vital ? 2 : 1)
            * UserPreferenceManager.Current.FragilityMultiplier
            * (float)Settings.Stab
        );

        if (vital && !limb.IsZombie)
            circ.InternalBleedingIntensity += 5f * Random.value;

        limb.Wince(165f);

        if (!limb.IsZombie && limb.NodeBehaviour.IsConnectedToRoot)
            limb.Person.ShockLevel += Random.value * Mathf.Clamp(Settings.Reaction, 0f, float.MaxValue);

        limb.Numbness = 1f * Mathf.Clamp(Settings.Reaction, 0f, float.MaxValue);
        limb.Person.AdrenalineLevel += 1f;

        if (limb.HasBrain && vital && (!limb.IsZombie || !(Random.value > 0.5f)))
        {
            limb.Person.AddPain(90f * Mathf.Clamp(Settings.Reaction, 0f, float.MaxValue));
            circ.InternalBleedingIntensity += 5f * Random.value * (float)Settings.Bleeding;
            limb.Health = 0f;
            if (Random.value > 0.25f)
                limb.Person.Consciousness = 0f;
        }
    }

    public void HandleActOnImpact(float impulse, Vector3 pos)
    {
        if (Parent != null && !Parent.ZooiSystem)
            return;
        if (limb.ImmuneToDamage)
            return;

        float collisionVal = (float)Settings.Collision;
        float reactionVal = (float)Settings.Reaction;
        float consciousnessVal = (float)Settings.Consciousness;
        float bleedingVal = (float)Settings.Bleeding;
        bool bleed = (bool)Settings.Bleed;

        float t = limb.BreakingThreshold * MassRatio / limb.ImpactDamageMultiplier;
        t *= collisionVal;
        impulse *= collisionVal;
        if (impulse > t && Random.value > 0.2f)
            limb.BreakBone();

        float vit = Mathf.Max(1f, limb.Vitality) * collisionVal;
        if (impulse > t * 0.5f / vit &&
            Random.value > 0.8f / vit &&
            bleed)
            circ.InternalBleedingIntensity += Mathf.Clamp(impulse * vit, 0f, 1f) * bleedingVal;

        if (!limb.IsAndroid)
        {
            if (UserPreferenceManager.Current.BrainDamage &&
                limb.HasBrain &&
                Random.value > 0.991f &&
                impulse > 4f)
                limb.Person.BrainDamaged = true;
            else
                limb.Damage(impulse * impulse * impulse * 2.8f * limb.ImpactDamageMultiplier);
        }

        limb.SkinMaterialHandler.AddDamagePoint(DamageType.Blunt, pos, impulse * 4f);

        float n3 = impulse * (limb.Vitality + 1f) * 0.25f;
        if (!limb.Person.BrainDamaged &&
            limb.HasBrain &&
            n3 > 1f &&
            !limb.IsAndroid &&
            Random.value > 0.5f)
            limb.Person.Consciousness *= Mathf.Clamp01(
                Random.value * 0.8f
                + (0f - reactionVal) / 10f
                + consciousnessVal / 100f
            );

        if (limb.NodeBehaviour.IsConnectedToRoot &&
            n3 > 0.2f &&
            !limb.IsZombie)
            limb.Person.ShockLevel += n3 * 0.04f / (reactionVal * 0.1f);
    }

    public void HandleSlice()
    {
        if ((float)Settings.Collision == 0f || (!_isRegenSystemSlice && (bool)Settings.IgnoreSlice) || limb.ImmuneToDamage)
            return;

        limb.Health -= 350f * (float)Settings.Damage;

        if (!(limb.Health > limb.InitialHealth * 0.1f) &&
            (!CheckStackFor("FragmentationRay") ||
             !((float)Settings.VitalityFragmentForce > LastFragmentForce)))
        {
            if (UserPreferenceManager.Current.StopAnimationOnDamage &&
                limb.NodeBehaviour.IsConnectedToRoot &&
                (float)Settings.Reaction > 0.2f)
                limb.Person.OverridePoseIndex = -1;

            limb.Person.AdrenalineLevel += 1f * (0f - (float)Settings.Reaction);

            if (limb.HasJoint)
            {
                limb.RegenerationSpeed = 0f;
                limb.Health = 0f;
                DurabilityReflectCache.ActOnImpact(
                    limb,
                    15f,
                    limb.transform.TransformPoint(((AnchoredJoint2D)limb.Joint).anchor)
                );
                limb.BreakingThreshold = 0f;
            }
        }
    }

    public void HandleCrush()
    {
        if (limb.PhysicalBehaviour.isDisintegrated ||
                (!_isRegenSystemCrush && (bool)Settings.IgnoreCrush) ||
                limb.ImmuneToDamage ||
                (CheckStackFor("FragmentationRay") &&
                 (float)Settings.VitalityFragmentForce > LastFragmentForce))
            return;

        limb.Health -= 350f * (float)Settings.Damage;
        if (limb.Health > limb.InitialHealth * 0.1f)
            return;

        if (UserPreferenceManager.Current.StopAnimationOnDamage &&
            limb.NodeBehaviour.IsConnectedToRoot)
            limb.Person.OverridePoseIndex = -1;

        if (!UserPreferenceManager.Current.GorelessMode)
        {
            var go = Object.Instantiate(
                limb.Person.BloodExplosionPrefab,
                limb.transform.position,
                Quaternion.identity
            );
            if (!limb.IsAndroid)
                go.GetComponentInChildren<BloodExplosionBehaviour>()
                    .SetColor(circ.GetComputedColor(limb.GetOriginalBloodType().Color));

            DurabilityReflectCache.ShatterProcedurally(limb, LimbBehaviour.ShatterFlags.All);
        }

        if (!limb.PhysicalBehaviour.isDisintegrated)
            limb.PhysicalBehaviour.Disintegrate();
    }

    public void HandleDamage(float dmg)
    {
        dmg *= (float)Settings.Damage;
        if (UserPreferenceManager.Current.StopAnimationOnDamage &&
            !limb.IsZombie &&
            dmg > 15.5f &&
            limb.NodeBehaviour.IsConnectedToRoot)
            limb.Person.OverridePoseIndex = -1;

        limb.Health -= dmg;
        if (limb.Health <= 0f)
            circ.IsPump = false;
    }

    public void HandleWince(float intensity = 1f)
    {
        intensity *= Mathf.Clamp(Settings.Reaction, 0f, float.MaxValue);
        if (limb.HasJoint &&
            limb.NodeBehaviour.IsConnectedToRoot &&
            !(limb.Health < 0.1f))
        {
            float v = 60f * intensity * (Mathf.PerlinNoise(Time.time * 8f, limb.randomOffset) * 2f - 1f);
            limb.InfluenceMotorSpeed(Mathf.Clamp(v, -450f, 450f));
        }
    }

    public void HandleEMPHit()
    {
        if (limb.IsAndroid && !Settings.IgnoreEMP)
            limb.Health = 0f;
    }

    public void HandleBreakBone()
    {
        if (!limb.Broken)
            BreakBoneInternal();
    }

    public void HandleCirculationCut(Vector2 point, Vector2 dir)
    {
        if (!circ.ImmuneToDamage && (bool)Settings.Bleed)
        {
            bool a = circ.IsWorldPointInArteryRect(point);
            circ.BleedingRate += (a ? 1f : 0.25f) * (float)Settings.Bleeding;
            circ.CreateBleedingParticle(point, dir, a ? 1 : 0);
        }
    }

    public void HandleCirculationExitShot(Shot shot)
    {
        if (!circ.ImmuneToDamage && !circ.Limb.IsZombie && !circ.Limb.IsAndroid)
        {
            shot.damage *= circ.Limb.ShotDamageMultiplier * (float)Settings.Shot;
            bool a = circ.IsWorldPointInArteryRect(shot.point);
            circ.BleedingRate += Mathf.Max(0.5f, shot.damage / 10f)
                * (float)(a ? 4 : 1)
                * (float)Settings.Bleeding;
            circ.CreateBleedingParticle(shot.point, shot.normal, a ? 1 : 0);
            if (!circ.Limb.IsAndroid &&
                !Settings.ShotNotVitality &&
                Random.value > 0.2f &&
                circ.Limb.IsWorldPointInVitalPart(shot.point))
                circ.IsPump = false;
        }
    }

    public void HandleCirculationShot(Shot shot)
    {
        if (!circ.ImmuneToDamage &&
            (!circ.Limb.IsAndroid || !(shot.damage < 50f)))
        {
            shot.damage *= circ.Limb.ShotDamageMultiplier * (float)Settings.Shot;
            if (!circ.Limb.IsZombie &&
                Random.value < circ.Limb.PhysicalBehaviour.Properties.Softness + 0.001f)
            {
                bool a = circ.IsWorldPointInArteryRect(shot.point);
                circ.BleedingRate += Mathf.Max(0.5f, shot.damage / 3.5f)
                    * (float)(a ? 2 : 1)
                    * (float)Settings.Bleeding;
                circ.CreateBleedingParticle(shot.point, shot.normal, a ? 1 : 0);
            }
            if (!circ.Limb.IsAndroid &&
                !Settings.ShotNotVitality &&
                !circ.Limb.IsZombie &&
                Random.value > 0.2f &&
                circ.Limb.IsWorldPointInVitalPart(shot.point))
                circ.IsPump = false;
            circ.GunshotWoundCount++;
        }
    }

    public void HandleCirculationStabbed(Stabbing st)
    {
        if (!circ.ImmuneToDamage &&
            !circ.Limb.IsZombie &&
            st.stabber.StabCausesWound)
        {
            circ.StabWoundCount++;
            circ.InternalBleedingIntensity += 0.1f * (float)Settings.Bleeding;
            if (circ.IsPump &&
                !Settings.StabNotVitality &&
                Random.value < 0.6f &&
                circ.Limb.IsWorldPointInVitalPart(st.point))
                circ.IsPump = false;
        }
    }

    public void HandleCirculationUnstabbed(Stabbing st)
    {
        if (!circ.ImmuneToDamage && st.stabber.StabCausesWound)
        {
            bool a = circ.IsWorldPointInArteryRect(st.point);
            circ.BleedingRate += (float)(a ? 4 : 1) * (float)Settings.Bleeding;
            circ.CreateBleedingParticle(st.point, st.normal, a ? 1 : 0);
        }
    }

    public void CreateBleedingParticle(Vector2 pos, Vector2 dir, float laminarity = 0f, bool sound = false)
    {
        if ((bool)Settings.Bleed &&
            !circ.Limb.IsAndroid &&
            !circ.Limb.IsZombie &&
            circ.BleedingPointCount < 8)
        {
            pos = limb.PhysicalBehaviour.spriteRenderer.bounds.ClosestPoint(pos);
            var go = Object.Instantiate(
                limb.Person.BleedingParticlePrefab,
                pos - dir * 0.1f,
                Quaternion.identity,
                limb.transform
            );
            go.transform.up = dir;
            var c = go.GetComponent<BleedingParticleBehaviour>();
            c.ShouldBecomeSmokeInWater = circ.BleedingPointCount == 0;
            c.CirculationBehaviour = circ;
            c.Laminarity = laminarity;
            if (!sound)
                c.DripSounds = null;
            circ.BleedingParticles.Add(go);
            circ.BleedingPointCount++;
        }
    }

    public void OnFragmentHit(float f) => LastFragmentForce = f;

    public void RequestPunch(Collision2D col)
    {
        if (!CanPunch ||
            !limb ||
            PunchCooldown ||
            (float)Settings.PunchMult == 0f ||
            !limb.Person.IsAlive() ||
            !limb.IsConsideredAlive)
            return;

        Punch(Settings.PunchMult, Settings.PunchAdd, limb.transform, col);
        LimbStatManager.StartManagedCoroutine(PunchCooldownRoutine(0.03f, true));
        LimbStatManager.StartManagedCoroutine(PunchCooldownRoutine(0.1f, false));
    }

    private IEnumerator PunchCooldownRoutine(float delay, bool state)
    {
        yield return new WaitForSeconds(delay);
        PunchCooldown = state;
    }

    public static void Punch(
        float mult, float add, Transform t, Collision2D col, bool ignoreLogic = false)
    {
        if ((!ignoreLogic &&
             (col.gameObject.layer == 11 ||
              col.rigidbody.velocity.magnitude * 0.6f > col.otherRigidbody.velocity.magnitude ||
              2f > col.otherRigidbody.velocity.magnitude)) ||
            !Global.main.PhysicalObjectsInWorldByTransform.TryGetValue(col.transform, out var phys))
            return;

        float val = 1.1f * Mathf.Pow(
            Mathf.Clamp(col.relativeVelocity.magnitude, 8f, 100f), 1.25f) * mult + add;

        if ((ignoreLogic || !(val < 1f)) && val != 0f)
        {
            var b = phys.spriteRenderer.bounds;
            var pt = b.ClosestPoint(col.GetContact(0).point);

            if (col.collider.TryGetComponent(out LimbBehaviour lb))
            {
                lb.SkinMaterialHandler.AddDamagePoint(
                    DamageType.Bullet, pt, val / lb.transform.lossyScale.magnitude);
                if (!ignoreLogic)
                    lb.Damage(val);
                else
                    lb.Health -= val;
            }

            float n2 = 140f;
            if (val > 2100f)
            {
                n2 = val / 15f;
                float n3 = n2 / 140f;
                CameraShakeBehaviour.main.Shake(0.3f * n3, pt, 0.3f / n3);
                var o1 = ModAPI.CreateParticleEffect("Vapor", pt);
                o1.transform.localScale *= n3 * 1.2f;
                Object.Destroy(o1, 1f);
                var o2 = ModAPI.CreateParticleEffect("Vapor", pt);
                o2.transform.localScale *= n3;
                Object.Destroy(o2, 1f);
                col.transform.SendMessage("Slice", SendMessageOptions.DontRequireReceiver);
            }

            int cnt = (int)Math.Ceiling(val / n2);
            float per = val / cnt;
            var norm = ((Vector2)pt - (Vector2)t.position).normalized;
            CameraShakeBehaviour.main.Shake(0.05f, pt);
            for (int i = 0; i < cnt; i++)
                phys.gameObject.SendMessage(
                    "Shot",
                    new Shot(norm, pt, per, triggerExplosiveOverride: false),
                    SendMessageOptions.DontRequireReceiver
                );
        }
    }

    public void ApplyCollisionForce(Collision2D col)
    {
        RequestPunch(col);
        if ((float)Settings.Collision != 0f && (float)GlobalMult != 0f)
        {
            int cnt = col.GetContacts(_buf);
            float imp = Utils.GetAverageImpulseRemoveOutliers(_buf, cnt)
                        * (float)GlobalMult
                        * (float)Settings.Collision
                        * 0.8f
                        / MassRatio
                        * UserPreferenceManager.Current.FragilityMultiplier;
            ApplyContactForce(col.gameObject, imp, _buf[0].normal, _buf[0].point);
        }
    }

    public void EvaluateCrushingForce(Collision2D col)
    {
        if (!limb.IsAndroid)
        {
            int cnt = col.GetContacts(_buf);
            float msr = MassRatio;
            float minImpulse = Utils.GetMinImpulse(_buf, cnt)
                               * (float)Settings.Collision
                               * UserPreferenceManager.Current.CrushForceMultiplier
                               / msr
                               * UserPreferenceManager.Current.FragilityMultiplier;
            float threshold = Mathf.Max(10f, limb.BreakingThreshold)
                              * Mathf.Lerp(
                                  (float)Physics2D.positionIterations / 16f, 1f, 0.2f);

            if (!(minImpulse < threshold))
            {
                col.gameObject.SendMessage(
                    "Decal",
                    new DecalInstruction(
                        limb.BloodDecal,
                        limb.transform.position,
                        circ.GetComputedColor(limb.GetOriginalBloodType().Color)
                    ),
                    SendMessageOptions.DontRequireReceiver
                );
                limb.Crush();
            }
        }
    }

    public void ApplyContactForce(GameObject col, float num, Vector2 normal, Vector2 point)
    {
        if (num < 1.3f || (float)GlobalMult == 0f)
            return;

        limb.Numbness += num / 12.5f;
        circ.InternalBleedingIntensity += num * 0.01f;

        float breakThreshold = limb.BreakingThreshold
                               * MassRatio
                               * UserPreferenceManager.Current.FragilityMultiplier
                               / (float)Settings.Collision;
        if (num * 0.12f > breakThreshold)
            limb.BreakBone();

        if (num > 2.7f)
            limb.Damage(num * 4f * (float)Settings.Damage);

        float nr = Mathf.Clamp(Settings.Reaction, 0f, float.MaxValue);
        limb.SkinMaterialHandler.AddDamagePoint(DamageType.Blunt, point, num * 4f * (float)Settings.Collision);

        if (limb.NodeBehaviour.IsConnectedToRoot)
            limb.Person.ShockLevel += num * Random.value * 0.03f * nr;

        if (limb.NodeBehaviour.IsRoot)
        {
            limb.Person.Consciousness -= num * Random.value * 0.04f * nr;
            if (UserPreferenceManager.Current.BrainDamage &&
                Random.value > 0.991f &&
                num > 6f)
                limb.Person.BrainDamaged = true;
        }

        if ((float)Settings.Consciousness != 0f &&
            !(num < (float)(int)Settings.SplatBlood) &&
            !(circ.GetAmountOfBlood() < 0.2f) &&
            !(limb.Health < limb.InitialHealth * 0.2f))
        {
            limb.PhysicalBehaviour.CreateImpactEffect(
                point, normal, Mathf.Clamp(num / 4f, 1f, 2f));
            if ((bool)Settings.Bleed)
                col.gameObject.SendMessage(
                    "Decal",
                    new DecalInstruction(
                        limb.BloodDecal,
                        point,
                        circ.GetComputedColor(limb.GetOriginalBloodType().Color)
                    ),
                    SendMessageOptions.DontRequireReceiver
                );
        }
    }

    public void ApplyImpactReaction(Collision2D col)
    {
        RequestPunch(col);
        int cnt = col.GetContacts(_buf);
        float imp = Utils.GetAverageImpulseRemoveOutliers(_buf, cnt)
                    * (float)Settings.Collision
                    / MassRatio
                    * UserPreferenceManager.Current.FragilityMultiplier
                    * 0.8f;

        if (imp < 1.3f || (float)Settings.Collision == 0f)
            return;

        var norm = _buf[0].normal;
        var pt = _buf[0].point;

        if (limb.IsAndroid)
            imp *= 0.1f;
        else if (Global.main.PhysicalObjectsInWorldByTransform.TryGetValue(col.transform, out var v) &&
                 v.SimulateTemperature &&
                 v.Temperature >= 70f)
        {
            limb.Damage(v.Temperature / 140f);
            limb.SkinMaterialHandler.AddDamagePoint(DamageType.Burn, pt, v.Temperature * 0.01f * (float)Settings.Skin);
            if (limb.NodeBehaviour.IsConnectedToRoot && !limb.IsParalysed)
                limb.Person.AddPain(1f * (float)Settings.Reaction);
            limb.Wince(150f);
            if (v.Temperature >= 100f)
                circ.HealBleeding();
        }

        if (limb.HasBrain &&
            imp > 0.6f &&
            (double)Random.value > 0.8 &&
            (bool)Settings.Bleed)
            circ.InternalBleedingIntensity += imp * (float)Settings.Bleeding;

        if (imp < 2f)
            return;

        limb.BruiseCount++;
        PropagateImpact(imp, norm, pt, 0, limb);

        if (imp < 1f || limb.IsAndroid || circ.GetAmountOfBlood() < 0.2f)
            return;

        if (Random.value > 0.8f)
            circ.Cut(pt, norm);

        if ((!(imp < 3f) || !(limb.Health > limb.InitialHealth * 0.2f)) &&
            (int)Settings.SplatBlood != 0 &&
            !(imp < (float)(int)Settings.SplatBlood) &&
            !(circ.GetAmountOfBlood() < 0.2f) &&
            !(limb.Health < limb.InitialHealth * 0.2f))
        {
            limb.PhysicalBehaviour.CreateImpactEffect(
                pt, norm, Mathf.Clamp(imp / 4f, 1f, 2f));
            if ((bool)Settings.Bleed)
                col.gameObject.SendMessage(
                    "Decal",
                    new DecalInstruction(
                        limb.BloodDecal,
                        pt,
                        circ.GetComputedColor(limb.GetOriginalBloodType().Color)
                    ),
                    SendMessageOptions.DontRequireReceiver
                );
        }
    }

    public void EvaluateContactDamage(Collision2D col)
    {
        if (!limb.IsAndroid)
        {
            int cnt = col.GetContacts(_buf);
            var fc = Utils.GetFirstValidContact(_buf, cnt);
            float msr = MassRatio;

            float velocitySq = col.relativeVelocity.sqrMagnitude
                               * UserPreferenceManager.Current.FragilityMultiplier
                               * (float)Settings.Collision
                               / msr;

            if (velocitySq > limb.FrictionBurnWoundMinSpeedSqrd)
            {
                limb.Damage(1f * (float)Settings.Collision);
                limb.SkinMaterialHandler.AddDamagePoint(DamageType.Burn, fc.point, 2f);
            }

            if (UserPreferenceManager.Current.LimbCrushing)
            {
                float minImpulse = Utils.GetMinImpulse(_buf, cnt)
                                   * UserPreferenceManager.Current.CrushForceMultiplier
                                   / msr
                                   * UserPreferenceManager.Current.FragilityMultiplier
                                   * (float)Settings.Collision;
                float threshold = Mathf.Max(10f, limb.BreakingThreshold)
                                  * Mathf.Lerp(
                                      (float)Physics2D.positionIterations / 16f, 1f, 0.2f);

                if (!(minImpulse < threshold))
                {
                    col.gameObject.SendMessage(
                        "Decal",
                        new DecalInstruction(
                            limb.BloodDecal,
                            limb.transform.position,
                            circ.GetComputedColor(limb.GetOriginalBloodType().Color)
                        ),
                        SendMessageOptions.DontRequireReceiver
                    );
                    limb.Crush();
                }
            }
        }
    }

    private void PropagateImpact(
        float impulse, Vector2 dir, Vector3 pos, int iter,
        LimbBehaviour origin, HashSet<LimbBehaviour> visited = null)
    {
        if (visited == null)
            visited = new();
        if (!visited.Add(limb))
            return;

        DurabilityReflectCache.ActOnImpact(limb, impulse, pos);

        if (iter >= 8)
            return;

        for (int i = 0; i < limb.ConnectedLimbs.Count; i++)
        {
            var lb = limb.ConnectedLimbs[i];
            if (lb != origin && !visited.Contains(lb))
            {
                float dot = Vector2.Dot(
                    (limb.transform.position - lb.transform.position).normalized, dir);
                if (dot > 0f && LimbStatManager.TryGetEntry(lb, out var e))
                    e.PropagateImpact(dot * impulse * 0.9f, dir, pos, iter + 1, limb, visited);
            }
        }
    }

    private void BreakBoneInternal()
    {
        limb.Broken = true;
        if (!limb.HasJoint)
            return;

        if (limb.IsLethalToBreak)
            limb.Damage(limb.Health + 1f);

        if (limb.NodeBehaviour.IsConnectedToRoot)
        {
            limb.Person.ShockLevel += Random.value * 5f * (float)Settings.Reaction;
            limb.Person.Wince(Random.value * 150f);
        }

        if ((double)Random.value > 0.9 && (bool)Settings.Bleed)
            circ.InternalBleedingIntensity += Random.value * (float)Settings.Bleeding;

        limb.PhysicalBehaviour.PlayClipOnce(limb.Person.BoneBreakClips.PickRandom());

        if (limb.Joint.useLimits)
        {
            var l = limb.Joint.limits;
            l.max = Mathf.Lerp(l.max, 180f, 0.5f);
            l.min = Mathf.Lerp(l.min, -180f, 0.5f);
            limb.Joint.limits = l;
        }
    }

    private IEnumerator CrushNextFrame()
    {
        limb.SkinMaterialHandler.AddDamagePoint(
            DamageType.Dismemberment, limb.transform.position, 15f);
        yield return new WaitForEndOfFrame();
        yield return new WaitForFixedUpdate();
        limb.Crush();
    }

    private static bool CheckStackFor(string methodName)
    {
        if (methodName == "FragmentationRay")
            return _isFragmentationRay;
        return false;
    }
}


public class LimbStatData
{
    public PersonBehaviour Person;
    public LimbStatSettings Settings = new();
    public float MaxBonus = 3f, Bonus = 10000f;
    public bool ZooiSystem;
    public ModularFloat GlobalMult = new(1f);
    public Dictionary<LimbBehaviour, LimbStatEntry> Entries = new();
    public LimbStatEntry[] EntryCache;
    public PersonRegenData RegenData;
    public HashSet<string> RegenPunchNames;
    public int[] RegenPunchLimbs;

    public void RefreshCache() => EntryCache = Entries.Values.ToArray();

    public void UpdateConsciousness()
    {
        if (!Person || !Person.IsAlive())
            return;
        Bonus = Mathf.Clamp(Bonus, 0f, MaxBonus);

        if (Person.Consciousness > 0.8f)
            Bonus += 0.003f;
        if (Person.Consciousness != 1f && Bonus > 1f)
        {
            Bonus -= 1f - Person.Consciousness;
            Person.Consciousness = 1f;
        }
        if (Person.ShockLevel != 1f && Bonus > 1f)
        {
            Bonus -= Person.ShockLevel * 0.1f;
            Person.ShockLevel = 0f;
        }

        if (!(Bonus > 1f))
            return;

        foreach (var lb in Person.Limbs)
        {
            if (lb.Numbness != 0f)
            {
                lb.CirculationBehaviour.InternalBleedingIntensity *= 0.15f * (float)Settings.Bleeding;
                lb.Numbness *= 0.15f;
            }
        }
    }

    public void ProcessHealing()
    {
        if (!Person)
            return;

        float n = Mathf.Clamp(Settings.Reaction, float.MinValue, 0f) / 100f;
        Person.OxygenLevel += Settings.DamageRegenRate;
        Person.ShockLevel += n;
        Person.PainLevel += n;
        if ((float)Settings.Reaction < 0f)
        {
            Person.BrainDamaged = false;
            Person.PainLevel = 0f;
        }
    }

    public void OnParamsChanged()
    {
        MaxBonus = Settings.Consciousness;
        Bonus = MaxBonus;
    }
}


// Custom delegate for TryGetFreeCollisionBufferIndex (has ref int param)
public delegate bool TryGetFreeSlotDelegate(PhysicalBehaviour instance, ref int index);

/// <summary>Compiled field accessors for ballistics state machine types.</summary>
public struct BallisticsFieldAccessors
{
    public Func<object, Vector2> GetOrigin;
    public Func<object, BallisticsEmitter> GetEmitter;
    public Func<object, Vector2> GetDirection;
    public Func<object, LineRenderer> GetTracer;
    public Func<object, int> GetIteration;
}

public static class DurabilityReflectCache
{
    private const BindingFlags BF_INST = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

    // ── LimbBehaviour method delegates (via ZeroReflect.Access) ──
    public static readonly Action<LimbBehaviour> SetJointFragility;
    public static readonly Action<LimbBehaviour, float, Vector3> ActOnImpact;
    public static readonly Action<LimbBehaviour, LimbBehaviour.ShatterFlags> ShatterProcedurally;
    public static readonly Action<LimbBehaviour> CalculateJointStress;
    public static readonly Action<LimbBehaviour> FakeStandUpright;
    public static readonly Action<LimbBehaviour, float> SetMotorStrength;
    public static readonly Action<LimbBehaviour> SetMotorStrengthToMuscleStrength;
    public static readonly Func<LimbBehaviour, bool> ApplyPoseOverrides;
    /// <summary>Manual Expression — hardcodes trailing (float)1f args.</summary>
    public static readonly Action<LimbBehaviour, object> MoveIntoPose;
    public static readonly Func<LimbBehaviour, float> GetMassStrengthRatio;

    // ── PhysicalBehaviour method delegates ──
    public static readonly Action<PhysicalBehaviour> OnCollisionStayWithoutSleep;
    /// <summary>Manual Expression — fills trailing params with Default().</summary>
    public static readonly Action<PhysicalBehaviour, object> HandleStabRelease;
    public static readonly Action<PhysicalBehaviour> SetParticleEmission;
    public static readonly Action<PhysicalBehaviour> AffectSurroundings;
    public static readonly Action<PhysicalBehaviour> HandleSlidingSounds;
    public static readonly Action<PhysicalBehaviour, Collision2D, float> HandleSounds;
    public static readonly Action<PhysicalBehaviour, Collision2D, ContactPoint2D> HandleStabbing;

    // ── PhysicalBehaviour field accessors (ZeroReflect delegates) ──
    public static readonly Func<PhysicalBehaviour, float> GetPhysSeed;
    public static readonly Func<PhysicalBehaviour, AudioSource> GetPhysAudioSource;
    public static readonly Func<PhysicalBehaviour, float> GetSizzleHeat;
    public static readonly Action<PhysicalBehaviour, float> SetSizzleHeat;
    public static readonly Func<PhysicalBehaviour, float> GetAffectTimer;
    public static readonly Action<PhysicalBehaviour, float> SetAffectTimer;
    public static readonly Func<PhysicalBehaviour, float> GetOobDuration;
    public static readonly Action<PhysicalBehaviour, float> SetOobDuration;
    public static readonly Func<PhysicalBehaviour, HashSet<DecalControllerBehaviour>> GetDecalControllers;
    public static readonly Func<PhysicalBehaviour, bool> GetOnFire;
    public static readonly Action<PhysicalBehaviour, bool> SetOnFire;

    // ── LimbBehaviour field accessors ──
    public static readonly Action<LimbBehaviour, float> SetLimbShotHeat;
    public static readonly Func<LimbBehaviour, GForceMeasureBehaviour> GetLimbGForce;

    // ── Collision buffer accessors ──
    public static readonly Func<PhysicalBehaviour, Array> GetOnCollisionStayBuffer;
    public static readonly TryGetFreeSlotDelegate TryGetFreeSlot;
    public static readonly Type Phys_ColliderBoolPairType;
    public static readonly Func<ContactPoint2D[]> GetContactBuffer;         // static field
    public static readonly Func<object, bool> GetCBPActive;
    public static readonly Action<object, bool> SetCBPActive;
    public static readonly Func<object, Collider2D> GetCBPColl;

    // ── Ballistics (MethodInfo kept — delegate sig unknown for game-public type) ──
    public static readonly MethodInfo BallisticIteration_MI;
    public static readonly object[] BallisticIterArgs = new object[10];

    // ── Ballistics state-machine field cache ──
    private static readonly Dictionary<Type, BallisticsFieldAccessors> _ballisticsFieldCache = new();

    // ── Constructor args array (still needed for Activator.CreateInstance) ──
    public static readonly object[] CBP_CtorArgs = new object[2];

    static DurabilityReflectCache()
    {
        var tLB = typeof(LimbBehaviour);
        var tPB = typeof(PhysicalBehaviour);
        var bf = Access.DefaultInstance;

        // ── LimbBehaviour methods ──
        SetJointFragility = Access.CreateDelegate<Action<LimbBehaviour>>("SetJointFragility");
        ActOnImpact = Access.CreateDelegate<Action<LimbBehaviour, float, Vector3>>("ActOnImpact");
        ShatterProcedurally = Access.CreateDelegate<Action<LimbBehaviour, LimbBehaviour.ShatterFlags>>("ShatterProcedurally");
        CalculateJointStress = Access.CreateDelegate<Action<LimbBehaviour>>("CalculateJointStress");
        FakeStandUpright = Access.CreateDelegate<Action<LimbBehaviour>>("FakeStandUpright");
        SetMotorStrength = Access.CreateDelegate<Action<LimbBehaviour, float>>("SetMotorStrength");
        SetMotorStrengthToMuscleStrength = Access.CreateDelegate<Action<LimbBehaviour>>("SetMotorStrengthToMuscleStrength");
        ApplyPoseOverrides = Access.CreateDelegate<Func<LimbBehaviour, bool>>("ApplyPoseOverrides");
        GetMassStrengthRatio = Access.CreateDelegate<Func<LimbBehaviour, float>>("GetMassStrengthRatio");

        // MoveIntoPose: manual — hardcodes trailing 1f,1f args + object→poseType convert
        {
            var mi = tLB.GetMethod("MoveIntoPose", BF_INST);
            var poseType = mi.GetParameters()[0].ParameterType;
            var p = Expression.Parameter(tLB);
            var a0 = Expression.Parameter(typeof(object));
            MoveIntoPose = Expression.Lambda<Action<LimbBehaviour, object>>(
                Expression.Call(p, mi,
                    Expression.Convert(a0, poseType),
                    Expression.Constant(1f),
                    Expression.Constant(1f)),
                p, a0).Compile();
        }

        // ── PhysicalBehaviour methods ──
        OnCollisionStayWithoutSleep = Access.CreateDelegate<Action<PhysicalBehaviour>>("OnCollisionStayWithoutSleep");
        SetParticleEmission = Access.CreateDelegate<Action<PhysicalBehaviour>>("SetParticleEmission");
        AffectSurroundings = Access.CreateDelegate<Action<PhysicalBehaviour>>("AffectSurroundings");
        HandleSlidingSounds = Access.CreateDelegate<Action<PhysicalBehaviour>>("HandleSlidingSounds");
        HandleSounds = Access.CreateDelegate<Action<PhysicalBehaviour, Collision2D, float>>("HandleSounds");
        HandleStabbing = Access.CreateDelegate<Action<PhysicalBehaviour, Collision2D, ContactPoint2D>>("HandleStabbing");

        // HandleStabRelease: manual — first param is a game-public type, rest get Default()
        {
            var mi = tPB.GetMethod("HandleStabRelease", BF_INST);
            if (mi != null)
            {
                var penType = mi.GetParameters()[0].ParameterType;
                var p = Expression.Parameter(tPB);
                var a0 = Expression.Parameter(typeof(object));
                var pis = mi.GetParameters();
                var args = new Expression[pis.Length];
                args[0] = Expression.Convert(a0, penType);
                for (int i = 1; i < pis.Length; i++)
                    args[i] = Expression.Default(pis[i].ParameterType);
                HandleStabRelease = Expression.Lambda<Action<PhysicalBehaviour, object>>(
                    Expression.Call(p, mi, args), p, a0).Compile();
            }
        }

        // ── PhysicalBehaviour field accessors (ZeroReflect) ──
        GetPhysSeed = Access.CreateFieldGetter<PhysicalBehaviour, float>("seed", bf);
        GetPhysAudioSource = Access.CreateFieldGetter<PhysicalBehaviour, AudioSource>("audioSource", bf);
        GetSizzleHeat = Access.CreateFieldGetter<PhysicalBehaviour, float>("sizzleAudioHeat", bf);
        SetSizzleHeat = Access.CreateFieldSetter<PhysicalBehaviour, float>("sizzleAudioHeat", bf);
        GetAffectTimer = Access.CreateFieldGetter<PhysicalBehaviour, float>("affectSurroundingTimer", bf);
        SetAffectTimer = Access.CreateFieldSetter<PhysicalBehaviour, float>("affectSurroundingTimer", bf);
        GetOobDuration = Access.CreateFieldGetter<PhysicalBehaviour, float>("outOfBoundsDuration", bf);
        SetOobDuration = Access.CreateFieldSetter<PhysicalBehaviour, float>("outOfBoundsDuration", bf);
        GetDecalControllers = Access.CreateFieldGetter<PhysicalBehaviour, HashSet<DecalControllerBehaviour>>("decalControllers", bf);
        GetOnFire = Access.CreatePropertyGetter<PhysicalBehaviour, bool>("OnFire");
        SetOnFire = Access.CreatePropertySetter<PhysicalBehaviour, bool>("OnFire");

        // ── LimbBehaviour field accessors ──
        SetLimbShotHeat = Access.CreateFieldSetter<LimbBehaviour, float>("shotHeat", bf);
        GetLimbGForce = Access.CreateFieldGetter<LimbBehaviour, GForceMeasureBehaviour>("gforce", bf);

        // ── Collision buffer ──
        GetOnCollisionStayBuffer = Access.CreateFieldGetter<PhysicalBehaviour, Array>("onCollisionStayBuffer", bf);
        TryGetFreeSlot = Access.CreateDelegate<TryGetFreeSlotDelegate>(tPB, "TryGetFreeCollisionBufferIndex", bf);
        Phys_ColliderBoolPairType = tPB.GetNestedType("ColliderBoolPair", BindingFlags.NonPublic);
        GetContactBuffer = Access.CreateStaticFieldGetter<PhysicalBehaviour, ContactPoint2D[]>("contactBuffer");

        // CBP fields: nested struct — manually compile Expression trees
        if (Phys_ColliderBoolPairType != null)
        {
            var activeField = Phys_ColliderBoolPairType.GetField("Active");
            var collField = Phys_ColliderBoolPairType.GetField("Coll");
            var pObj = Expression.Parameter(typeof(object));
            var conv = Expression.Convert(pObj, Phys_ColliderBoolPairType);

            GetCBPActive = Expression.Lambda<Func<object, bool>>(
                Expression.Field(conv, activeField), pObj).Compile();

            var vParam = Expression.Parameter(typeof(bool), "v");
            SetCBPActive = Expression.Lambda<Action<object, bool>>(
                Expression.Assign(Expression.Field(conv, activeField), vParam),
                pObj, vParam).Compile();

            GetCBPColl = Expression.Lambda<Func<object, Collider2D>>(
                Expression.Field(conv, collField), pObj).Compile();
        }

        // ── Ballistics (MethodInfo — game-public type, signature unknown) ──
        BallisticIteration_MI = typeof(BallisticsEmitter).GetMethod(
            "BallisticIteration", BF_INST);
    }

    public static BallisticsFieldAccessors GetBallisticsFields(Type type)
    {
        if (!_ballisticsFieldCache.TryGetValue(type, out var f))
        {
            f = default;
            var bf = Access.DefaultInstance;

            f.GetOrigin = MakeBoxedGetter<Vector2>(type, "origin", bf);
            f.GetEmitter = MakeBoxedGetter<BallisticsEmitter>(type, "<>4__this", bf);
            f.GetDirection = MakeBoxedGetter<Vector2>(type, "direction", bf);
            f.GetTracer = MakeBoxedGetter<LineRenderer>(type, "tracer", bf);
            f.GetIteration = MakeBoxedGetter<int>(type, "iteration", bf);

            _ballisticsFieldCache[type] = f;
        }
        return f;
    }

    /// <summary>Builds a Func&lt;object, T&gt; that unboxes, reads a field, and returns the typed value.</summary>
    private static Func<object, T> MakeBoxedGetter<T>(Type type, string fieldName, BindingFlags flags)
    {
        var fi = type.GetField(fieldName, flags);
        var p = Expression.Parameter(typeof(object));
        var body = Expression.Field(Expression.Convert(p, type), fi);
        return Expression.Lambda<Func<object, T>>(
            typeof(T) != fi.FieldType ? Expression.Convert(body, typeof(T)) : body, p).Compile();
    }
}


public class LimbStatManager : MonoBehaviour
{
    private static LimbStatManager _instance;
    public static LimbStatManager Instance
    {
        get
        {
            if (_instance == null)
                _instance = CreateNew();
            return _instance;
        }
        private set => _instance = value;
    }
    private static readonly List<Coroutine> _managedCoroutines = new();

    private readonly Dictionary<PersonBehaviour, LimbStatData> _persons = new();
    private readonly Dictionary<LimbBehaviour, LimbStatEntry> _byLimb = new();
    /// <summary>Reverse lookup: PhysicalBehaviour → LimbStatEntry — avoids GetComponent in hot paths.</summary>
    private readonly Dictionary<PhysicalBehaviour, LimbStatEntry> _byPhys = new();
    private readonly List<PersonBehaviour> _cleanupDead = new();
    private int _cleanupTimer = 100;

    public static bool TryGetEntry(LimbBehaviour lb, out LimbStatEntry entry)
    {
        if (Instance != null && Instance._byLimb.TryGetValue(lb, out entry))
            return true;
        entry = null;
        return false;
    }

    /// <summary>Zero-GetComponent hot-path lookup from PhysicalBehaviour.</summary>
    public static bool TryGetEntry(PhysicalBehaviour phys, out LimbStatEntry entry)
    {
        if (Instance != null && phys && Instance._byPhys.TryGetValue(phys, out entry))
            return true;
        entry = null;
        return false;
    }

    public static bool TryGetEntry(CirculationBehaviour cb, out LimbStatEntry entry)
    {
        if (cb != null && cb.Limb != null)
            return TryGetEntry(cb.Limb, out entry);
        entry = null;
        return false;
    }

    public static void StartManagedCoroutine(IEnumerator routine)
    {
        if (Instance)
            _managedCoroutines.Add(Instance.StartCoroutine(routine));
    }

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
        => _instance = CreateNew();

    private static LimbStatManager CreateNew()
    {
        return new GameObject("LimbStatManager", typeof(LimbStatManager))
        {
            hideFlags = HideFlags.HideAndDontSave
        }.GetComponent<LimbStatManager>();
    }

    public LimbStatData Register(PersonBehaviour person, LimbStatParams settings, bool zooi)
    {
        if (!person || _persons.ContainsKey(person))
            return _persons.TryGetValue(person, out var existing) ? existing : null;

        var data = new LimbStatData { Person = person, ZooiSystem = zooi };
        data.Settings.Params = settings;

        var limbs = person.Limbs;
        if (limbs == null)
            return null;

        foreach (var lb in limbs)
        {
            if (!lb || _byLimb.ContainsKey(lb))
                continue;

            var entry = new LimbStatEntry
            {
                limb = lb,
                circ = lb.CirculationBehaviour
            };
            entry.Parent = data;
            entry.LinkToPerson(data);
            entry.SyncPhysicalProps();
            data.Entries[lb] = entry;
            _byLimb[lb] = entry;
            var phys = lb.PhysicalBehaviour;
            if (phys) _byPhys[phys] = entry;
        }

        data.RefreshCache();
        data.OnParamsChanged();
        _persons[person] = data;

        RuntimeDebug.LogInfo(
            $"[LimbStat] Registered {person.name} with {data.Entries.Count} limbs, Zooi={zooi}");

        return data;
    }

    public void Unregister(PersonBehaviour person)
    {
        if (!person || !_persons.TryGetValue(person, out var data))
            return;

        foreach (var lb in data.Entries.Keys)
        {
            _byLimb.Remove(lb);
            var phys = lb.PhysicalBehaviour;
            if (phys) _byPhys.Remove(phys);
        }
        _persons.Remove(person);

        RuntimeDebug.LogDebug($"[LimbStat] Unregistered {person.name}");
    }

    public bool TryGetData(PersonBehaviour person, out LimbStatData data)
        => _persons.TryGetValue(person, out data);

    public void RegisterLimbEntry(LimbBehaviour lb, LimbStatEntry entry)
    {
        if (lb && entry != null && !_byLimb.ContainsKey(lb))
        {
            _byLimb[lb] = entry;
            var phys = lb.PhysicalBehaviour;
            if (phys) _byPhys[phys] = entry;
        }
    }

    public void UnregisterLimbEntry(LimbBehaviour lb)
    {
        if (lb)
        {
            _byLimb.Remove(lb);
            var phys = lb.PhysicalBehaviour;
            if (phys) _byPhys.Remove(phys);
        }
    }

    private void FixedUpdate()
    {
        int personCount = _persons.Count;
        if (personCount == 0)
            return;

        float dt = Time.fixedDeltaTime;
        foreach (var data in _persons.Values)
        {
            if (!data.Person)
                continue;

            data.UpdateConsciousness();
            data.ProcessHealing();

            var cache = data.EntryCache;
            if (cache == null)
                continue;

            for (int i = 0; i < cache.Length; i++)
            {
                var e = cache[i];
                if (!e.limb)
                    continue;
                e.Recover(dt);
                e.TickParams();
                e.CompactDamagePoints();
            }
        }
    }

    private void Update()
    {
        if (--_cleanupTimer <= 0)
        {
            _cleanupTimer = 100;
            CleanUp();
        }
    }

    private void CleanUp()
    {
        _cleanupDead.Clear();
        foreach (var kv in _persons)
        {
            if (!kv.Key || !kv.Value.Person)
                _cleanupDead.Add(kv.Key);
        }
        for (int i = 0; i < _cleanupDead.Count; i++)
            Unregister(_cleanupDead[i]);
        _managedCoroutines.RemoveAll(c => c == null);
    }
}


public static class LimbStatPatches
{
    public static DynamicHarmonyManager Harmony { get; private set; }
    private static bool _initialized;

    public static void Initialize()
    {
        if (_initialized)
            return;
        _initialized = true;
        Harmony = new DynamicHarmonyManager("com.helper.limbstat");
        RuntimeDebug.LogInfo("[LimbStat] Initializing global patch system");
        TryCatchAction(
            () =>
            {
                RegisterLimbPatches();
                RegisterProjectilePatches();
                RuntimeDebug.LogInfo("[LimbStat] 27 global patches registered");
            },
            () => RuntimeDebug.LogError("[LimbStat] Failed to register patches")
        );
    }

    public static void Deinitialize()
    {
        Harmony?.RemoveAllById("com.saitama.limbstat");
        Harmony = null;
        _initialized = false;
        RuntimeDebug.LogInfo("[LimbStat] Patches removed");
    }
    public static void TryCatchAction(Action tryAction = null, Action catchAction = null)
    {
        try { tryAction?.Invoke(); }
        catch { catchAction?.Invoke(); }
    }

    private static void RegisterLimbPatches()
    {
        Harmony.AddPrefix(
            "ls_InfluenceMotor",
            typeof(LimbBehaviour),
            "InfluenceMotorSpeed",
            ctx =>
            {
                var lb = (LimbBehaviour)ctx.Instance;
                float inf = (float)ctx.Args[0];
                if (LimbStatManager.TryGetEntry(lb, out var e) && !e.Parent.ZooiSystem && inf == lb.PhysicalBehaviour.Charge * 0.5f)
                {
                    if (!(Random.value < (float)e.Settings.Collision))
                        ctx.CancelOriginal = true;
                    else
                        ctx.Args[0] = inf * (float)e.Settings.Collision;
                }
            },
            new[] { typeof(float).MakeByRefType() }
        );

        Harmony.AddPrefix(
            "ls_Shot",
            typeof(LimbBehaviour),
            "Shot",
            ctx =>
            {
                TryCatchAction(() =>
                {
                    if (LimbStatManager.TryGetEntry((LimbBehaviour)ctx.Instance, out var e))
                    {
                        var shot = (Shot)ctx.Args[0];
                        if (e.Parent != null && e.Parent.ZooiSystem)
                            e.HandleShot(shot);
                        else
                            e.Shot(shot);
                        ctx.CancelOriginal = true;
                    }
                });
            },
            new[] { typeof(Shot) }
        );

        Harmony.AddPrefix(
            "ls_Wince",
            typeof(LimbBehaviour),
            "Wince",
            ctx =>
            {
                if (LimbStatManager.TryGetEntry((LimbBehaviour)ctx.Instance, out var e))
                {
                    e.HandleWince(ctx.Args.Length > 0 && ctx.Args[0] != null ? (float)ctx.Args[0] : 1f);
                    ctx.CancelOriginal = true;
                }
            },
            new[] { typeof(float) }
        );

        Harmony.AddPrefix(
            "ls_ExitShot",
            typeof(LimbBehaviour),
            "ExitShot",
            ctx =>
            {
                TryCatchAction(() =>
                {
                    if (LimbStatManager.TryGetEntry((LimbBehaviour)ctx.Instance, out var e))
                    {
                        var shot = (Shot)ctx.Args[0];
                        if (e.Parent != null && e.Parent.ZooiSystem)
                            e.HandleExitShot(shot);
                        else
                            e.ExitShot(shot);
                        ctx.CancelOriginal = true;
                    }
                });
            },
            new[] { typeof(Shot) }
        );

        Harmony.AddPrefix(
            "ls_Stabbed",
            typeof(LimbBehaviour),
            "Stabbed",
            ctx =>
            {
                TryCatchAction(() =>
                {
                    if (LimbStatManager.TryGetEntry((LimbBehaviour)ctx.Instance, out var e))
                    {
                        var stab = (Stabbing)ctx.Args[0];
                        if (e.Parent != null && e.Parent.ZooiSystem)
                            e.HandleStabbed(stab);
                        else
                            e.Stabbed(stab);
                        ctx.CancelOriginal = true;
                    }
                });
            },
            new[] { typeof(Stabbing) }
        );

        Harmony.AddPrefix(
            "ls_CollisionEnter",
            typeof(LimbBehaviour),
            "OnCollisionEnter2D",
            ctx =>
            {
                TryCatchAction(() =>
                {
                    if (LimbStatManager.TryGetEntry((LimbBehaviour)ctx.Instance, out var e))
                    {
                        var c = (Collision2D)ctx.Args[0];
                        var data = LimbStatManager.Instance?.GetDataForEntry(e);
                        if (data != null && data.ZooiSystem)
                            e.ApplyImpactReaction(c);
                        else
                            e.ApplyCollisionForce(c);
                        ctx.CancelOriginal = true;
                    }
                });
            },
            new[] { typeof(Collision2D) }
        );

        Harmony.AddPrefix(
            "ls_CollisionStay",
            typeof(LimbBehaviour),
            "OnCollisionStay2D",
            ctx =>
            {
                TryCatchAction(() =>
                {
                    if (LimbStatManager.TryGetEntry((LimbBehaviour)ctx.Instance, out var e))
                    {
                        var c = (Collision2D)ctx.Args[0];
                        var data = LimbStatManager.Instance?.GetDataForEntry(e);
                        if (data != null && data.ZooiSystem)
                            e.EvaluateContactDamage(c);
                        else
                            e.EvaluateCrushingForce(c);
                        ctx.CancelOriginal = true;
                    }
                });
            },
            new[] { typeof(Collision2D) }
        );

        Harmony.AddPrefix(
            "ls_ActOnImpact",
            typeof(LimbBehaviour),
            "ActOnImpact",
            ctx =>
            {
                TryCatchAction(() =>
                {
                    if (LimbStatManager.TryGetEntry((LimbBehaviour)ctx.Instance, out var e))
                    {
                        e.HandleActOnImpact((float)ctx.Args[0], (Vector3)ctx.Args[1]);
                        ctx.CancelOriginal = true;
                    }
                });
            },
            new[] { typeof(float), typeof(Vector3) }
        );

        Harmony.AddPrefix(
            "ls_Slice",
            typeof(LimbBehaviour),
            "Slice",
            ctx =>
            {
                TryCatchAction(() =>
                {
                    if (LimbStatManager.TryGetEntry((LimbBehaviour)ctx.Instance, out var e))
                    {
                        e.HandleSlice();
                        ctx.CancelOriginal = true;
                    }
                });
            }
        );

        Harmony.AddPrefix(
            "ls_EMPHit",
            typeof(LimbBehaviour),
            "OnEMPHit",
            ctx =>
            {
                TryCatchAction(() =>
                {
                    if (LimbStatManager.TryGetEntry((LimbBehaviour)ctx.Instance, out var e))
                    {
                        e.HandleEMPHit();
                        ctx.CancelOriginal = true;
                    }
                });
            }
        );

        Harmony.AddPrefix(
            "ls_Crush",
            typeof(LimbBehaviour),
            "Crush",
            ctx =>
            {
                TryCatchAction(() =>
                {
                    if (LimbStatManager.TryGetEntry((LimbBehaviour)ctx.Instance, out var e))
                    {
                        e.HandleCrush();
                        ctx.CancelOriginal = true;
                    }
                });
            }
        );

        Harmony.AddPrefix(
            "ls_Disintegration",
            typeof(LimbBehaviour),
            "PhysicalBehaviour_OnDisintegration",
            ctx =>
            {
                TryCatchAction(() =>
                {
                    var limb = (LimbBehaviour)ctx.Instance;
                    if (limb && LimbStatManager.TryGetEntry(limb, out var e) && (bool)e.Settings.IgnoreCrush
                        && !LimbStatEntry._isRegenSystemCrush)
                        ctx.CancelOriginal = true;
                });
            },
            new[] { typeof(object), typeof(EventArgs) }
        );

        Harmony.AddPrefix(
            "ls_Damage",
            typeof(LimbBehaviour),
            "Damage",
            ctx =>
            {
                TryCatchAction(() =>
                {
                    if (LimbStatManager.TryGetEntry((LimbBehaviour)ctx.Instance, out var e))
                    {
                        e.HandleDamage((float)ctx.Args[0]);
                        ctx.CancelOriginal = true;
                    }
                });
            },
            new[] { typeof(float) }
        );

        Harmony.AddPrefix(
            "ls_BreakBone",
            typeof(LimbBehaviour),
            "BreakBone",
            ctx =>
            {
                TryCatchAction(() =>
                {
                    if (LimbStatManager.TryGetEntry((LimbBehaviour)ctx.Instance, out var e))
                    {
                        e.HandleBreakBone();
                        ctx.CancelOriginal = true;
                    }
                });
            }
        );

        Harmony.AddPrefix(
            "ls_CircBleed",
            typeof(CirculationBehaviour),
            "CreateBleedingParticle",
            ctx =>
            {
                TryCatchAction(() =>
                {
                    if (LimbStatManager.TryGetEntry((CirculationBehaviour)ctx.Instance, out var e)
                        && e.Parent != null && e.Parent.ZooiSystem)
                    {
                        e.CreateBleedingParticle(
                            (Vector2)ctx.Args[0],
                            (Vector2)ctx.Args[1],
                            ctx.Args.Length > 2 ? (float)ctx.Args[2] : 0f,
                            ctx.Args.Length > 3 && (bool)ctx.Args[3]
                        );
                        ctx.CancelOriginal = true;
                    }
                });
            },
            new[] { typeof(Vector2), typeof(Vector2), typeof(float), typeof(bool) }
        );

        Harmony.AddPrefix(
            "ls_CircShot",
            typeof(CirculationBehaviour),
            "Shot",
            ctx =>
            {
                TryCatchAction(() =>
                {
                    if (LimbStatManager.TryGetEntry((CirculationBehaviour)ctx.Instance, out var e)
                        && e.Parent != null && e.Parent.ZooiSystem)
                    {
                        e.HandleCirculationShot((Shot)ctx.Args[0]);
                        ctx.CancelOriginal = true;
                    }
                });
            },
            new[] { typeof(Shot) }
        );

        Harmony.AddPrefix(
            "ls_CircExitShot",
            typeof(CirculationBehaviour),
            "ExitShot",
            ctx =>
            {
                TryCatchAction(() =>
                {
                    if (LimbStatManager.TryGetEntry((CirculationBehaviour)ctx.Instance, out var e)
                        && e.Parent != null && e.Parent.ZooiSystem)
                    {
                        e.HandleCirculationExitShot((Shot)ctx.Args[0]);
                        ctx.CancelOriginal = true;
                    }
                });
            },
            new[] { typeof(Shot) }
        );

        Harmony.AddPrefix(
            "ls_CircCut",
            typeof(CirculationBehaviour),
            "Cut",
            ctx =>
            {
                TryCatchAction(() =>
                {
                    if (LimbStatManager.TryGetEntry((CirculationBehaviour)ctx.Instance, out var e)
                        && e.Parent != null && e.Parent.ZooiSystem)
                    {
                        e.HandleCirculationCut((Vector2)ctx.Args[0], (Vector2)ctx.Args[1]);
                        ctx.CancelOriginal = true;
                    }
                });
            },
            new[] { typeof(Vector2), typeof(Vector2) }
        );

        Harmony.AddPrefix(
            "ls_CircStabbed",
            typeof(CirculationBehaviour),
            "Stabbed",
            ctx =>
            {
                TryCatchAction(() =>
                {
                    if (LimbStatManager.TryGetEntry((CirculationBehaviour)ctx.Instance, out var e)
                        && e.Parent != null && e.Parent.ZooiSystem)
                    {
                        e.HandleCirculationStabbed((Stabbing)ctx.Args[0]);
                        ctx.CancelOriginal = true;
                    }
                });
            },
            new[] { typeof(Stabbing) }
        );

        Harmony.AddPrefix(
            "ls_CircUnstabbed",
            typeof(CirculationBehaviour),
            "Unstabbed",
            ctx =>
            {
                TryCatchAction(() =>
                {
                    if (LimbStatManager.TryGetEntry((CirculationBehaviour)ctx.Instance, out var e)
                        && e.Parent != null && e.Parent.ZooiSystem)
                    {
                        e.HandleCirculationUnstabbed((Stabbing)ctx.Args[0]);
                        ctx.CancelOriginal = true;
                    }
                });
            },
            new[] { typeof(Stabbing) }
        );

        RegisterShockThermalPatches();
    }

    private static void RegisterShockThermalPatches()
    {
        Harmony.AddPrefix(
            "ls_MgdFixed",
            typeof(LimbBehaviour),
            "ManagedFixedUpdate",
            ctx =>
            {
                var lb = (LimbBehaviour)ctx.Instance;
                if (!LimbStatManager.TryGetEntry(lb, out var e))
                    return;

                float sr = e.Settings.Charge, tr = e.Settings.Thermal;
                DurabilityReflectCache.SetLimbShotHeat(lb, 0f);

                for (int i = 0; i < lb.ConnectedLimbs.Count; i++)
                {
                    var cl = lb.ConnectedLimbs[i];
                    if ((bool)cl &&
                        !cl.PhysicalBehaviour.isDisintegrated &&
                        lb.NodeBehaviour.IsConnectedTo(cl.NodeBehaviour))
                    {
                        Utils.TransferEnergyFixedRate(cl.PhysicalBehaviour, lb.PhysicalBehaviour);
                        Utils.AverageTemperature(cl.PhysicalBehaviour, lb.PhysicalBehaviour);
                    }
                }

                float temp = lb.PhysicalBehaviour.Temperature, internalTemp = lb.InternalTemperature;
                lb.PhysicalBehaviour.Temperature = Mathf.Lerp(temp, internalTemp, lb.InternalToExternalTempTransferRate);
                lb.InternalTemperature = Mathf.Lerp(
                    internalTemp, temp,
                    (temp > internalTemp)
                        ? (lb.ExternalToInternalTempTransferRate * 0.3f)
                        : lb.ExternalToInternalTempTransferRate
                );

                if (lb.PhysicalBehaviour.IsTouchingSomething != lb.IsOnFloor)
                    lb.IsOnFloor = lb.PhysicalBehaviour.IsTouchingSomething;

                if (lb.HasBrain)
                {
                    if (lb.CirculationBehaviour.BloodFlow < 0.25f)
                        lb.Person.OxygenLevel -= Time.deltaTime;
                    if (lb.Health < lb.InitialHealth / 2f)
                    {
                        foreach (var l in lb.Person.Limbs)
                            if (l.NodeBehaviour.IsConnectedToRoot)
                                l.InfluenceMotorSpeed(0f, 0.3f);
                    }
                    if (lb.IsZombie)
                        lb.Person.BrainDamaged = false;
                    else if (UserPreferenceManager.Current.BrainDamage &&
                                !lb.IsAndroid &&
                                (lb.CirculationBehaviour.InternalBleedingIntensity > 0.5f ||
                                lb.Person.OxygenLevel <= 0.25f))
                        lb.Person.BrainDamaged |= Random.value > 0.999f;
                }

                if (!lb.IsZombie &&
                    !lb.IsAndroid &&
                    lb.CirculationBehaviour.InternalBleedingIntensity > 0.2f)
                {
                    float ibi = lb.CirculationBehaviour.InternalBleedingIntensity;
                    switch (lb.RoughClassification)
                    {
                        case LimbBehaviour.BodyPart.Head:
                            lb.Damage(((Random.value > 5f / ibi) ? 20f : 0.0004f) * ibi);
                            lb.Person.OxygenLevel -= 0.0001f * ibi;
                            lb.Person.Consciousness -= 0.0001f * ibi;
                            break;
                        case LimbBehaviour.BodyPart.Torso:
                            lb.Damage(0.0015f * ibi);
                            break;
                        case LimbBehaviour.BodyPart.Legs:
                        case LimbBehaviour.BodyPart.Arms:
                            lb.Numbness += 0.2f * ibi;
                            lb.Damage(0.001f * ibi);
                            break;
                    }

                    if (lb.NodeBehaviour.IsConnectedToRoot)
                    {
                        if (Random.value > 0.9999f)
                            lb.Person.Consciousness -= 0.001f * ibi;
                        if ((double)Random.value > 0.99)
                            lb.Person.Wince(Random.value * ibi * 60f);
                    }
                }

                DurabilityReflectCache.CalculateJointStress(lb);

                if ((float)tr > 0f)
                {
                    float _coreDev = Mathf.Abs(lb.InternalTemperature - lb.BodyTemperature);
                    if (_coreDev >= 3f)
                    {
                        lb.Numbness += (0.001f + _coreDev * 0.0004f) * Random.value * tr;
                        float _devScale = 1f + _coreDev / 5f;
                        lb.Damage(0.005f * Random.value * _devScale * tr);
                    }

                    if (lb.InternalTemperature >= lb.DiscomfortingHeatTemperature ||
                        lb.InternalTemperature <= lb.BodyTemperature - 3f)
                    {
                        float _disDelta = Mathf.Abs(lb.InternalTemperature - lb.BodyTemperature);
                        if (lb.InternalTemperature >= 100f)
                            lb.CirculationBehaviour.HealBleeding();
                        if (lb.HasBrain && !lb.IsParalysed)
                        {
                            if (lb.InternalTemperature > lb.BodyTemperature)
                            {
                                float _heatSeverity = 1f + (_disDelta - (lb.DiscomfortingHeatTemperature - lb.BodyTemperature)) / 30f;
                                lb.Person.Consciousness *= Mathf.Clamp(
                                    1f - 0.0005f * _heatSeverity * tr, 0.1f, 1f);
                                lb.Person.AddPain(20f * _heatSeverity * tr);
                            }
                            else
                                lb.Person.Consciousness *= Mathf.Clamp(
                                    1f - 0.001f * (_disDelta / 15f) * tr, 0.1f, 1f);
                        }
                        lb.Damage(Mathf.Max(0.0015f, _disDelta * 0.003f) * tr);
                    }
                }

                if ((float)sr > 0f &&
                    lb.PhysicalBehaviour.Charge > float.Epsilon &&
                    !lb.IsZombie)
                    lb.Damage(lb.PhysicalBehaviour.Charge * 0.01f * sr);

                if ((float)tr > 0f)
                {
                    if (lb.PhysicalBehaviour.Temperature > lb.PhysicalBehaviour.Properties.BurningTemperatureThreshold)
                    {
                        float _overBurn = lb.PhysicalBehaviour.Temperature - lb.PhysicalBehaviour.Properties.BurningTemperatureThreshold;
                        float _burnAmplifier = 1f + _overBurn / 2000f;
                        lb.Wince(0.1f * _burnAmplifier);
                        lb.Damage(
                            (0.0015f + _overBurn / 500f)
                            * tr
                            * _burnAmplifier
                        );
                    }
                    else if (lb.PhysicalBehaviour.Temperature < lb.FreezingTemperature)
                    {
                        float _belowFreeze = lb.FreezingTemperature - lb.PhysicalBehaviour.Temperature;
                        float _frostScale = 1f + _belowFreeze / 50f;
                        if (lb.SkinMaterialHandler.AcidProgress < 0.5f + lb.randomOffset / 90000f)
                            lb.SkinMaterialHandler.AcidProgress += 0.0005f * _frostScale;
                        lb.Damage(0.05f * _frostScale * tr);
                    }
                }

                if (lb.PhysicalBehaviour.Wetness > 0.25f && lb.IsAndroid)
                    lb.PhysicalBehaviour.Charge += 0.5f * sr;

                if (lb.CirculationBehaviour.HasCirculation)
                {
                    float n = (lb.InternalTemperature <= lb.BodyTemperature) ? 0.05f : 0.025f;
                    if (lb.PhysicalBehaviour.Temperature < lb.FreezingTemperature)
                        n *= 0.03f;
                    float _thermalRecovery = 1f / Mathf.Max(tr, 0.001f);
                    n *= _thermalRecovery;
                    lb.InternalTemperature = Mathf.Lerp(
                        lb.InternalTemperature, lb.BodyTemperature,
                        n * lb.BodyHeatProductionFactor);
                }

                if (lb.IsConsideredAlive)
                {
                    if ((float)tr > 0f && lb.PhysicalBehaviour.OnFire)
                    {
                        float _burnIntensity = lb.PhysicalBehaviour.BurnIntensity;
                        float _fireSeverity = 1f + _burnIntensity * 4f;
                        lb.SkinMaterialHandler.AcidProgress += Time.fixedDeltaTime * 0.01f * _fireSeverity;
                        lb.PhysicalBehaviour.BurnProgress += Time.fixedDeltaTime * 0.01f * _fireSeverity;
                        lb.Health -= Time.deltaTime * 0.5f
                                        * (lb.IsZombie ? 0.01f : 1f)
                                        * _fireSeverity
                                        * tr;
                        if (!lb.IsZombie &&
                            lb.NodeBehaviour.IsConnectedToRoot &&
                            !lb.IsParalysed)
                        {
                            if (UserPreferenceManager.Current.StopAnimationOnDamage)
                                lb.Person.OverridePoseIndex = -1;
                            lb.Person.AddPain(lb.PhysicalBehaviour.BurnIntensity * Time.deltaTime);
                        }
                    }
                    if (UserPreferenceManager.Current.AutoHealWounds &&
                        lb.RegenerateBurnProgressSpeed > float.Epsilon &&
                        lb.PhysicalBehaviour.BurnProgress > 0f)
                    {
                        lb.PhysicalBehaviour.BurnProgress -= Time.fixedDeltaTime
                                                                * lb.RegenerateBurnProgressSpeed
                                                                * 0.001f;
                        lb.PhysicalBehaviour.BurnProgress = Mathf.Max(0f, lb.PhysicalBehaviour.BurnProgress);
                    }
                }

                if (lb.CirculationBehaviour.BloodFlow > Mathf.Max(0.25f, 0.9f - Mathf.Clamp01(lb.Person.AdrenalineLevel)) &&
                    lb.Person.IsTouchingFloor &&
                    lb.Person.ActivePose.ShouldStandUpright &&
                    lb.IsCapable)
                {
                    if (lb.FakeUprightForce > 0.001f)
                        DurabilityReflectCache.FakeStandUpright(lb);
                    if (lb.PhysicalBehaviour.rigidbody.bodyType == RigidbodyType2D.Dynamic)
                    {
                        lb.PhysicalBehaviour.rigidbody.angularVelocity *= Mathf.Lerp(1f, 0.92f, lb.Person.ActivePose.DragInfluence);
                        lb.PhysicalBehaviour.rigidbody.velocity *= Mathf.Lerp(1f, 0.94f, lb.Person.ActivePose.DragInfluence);
                    }
                }

                float _realPhysTemp = lb.PhysicalBehaviour.Temperature;
                if ((float)tr < 1f && !lb.Frozen)
                    lb.PhysicalBehaviour.Temperature = Mathf.Lerp(
                        lb.BodyTemperature, _realPhysTemp, tr);

                if (lb.HasJoint)
                {
                    bool _effFrozen = (float)tr > 0f &&
                                        (lb.Frozen || lb.PhysicalBehaviour.Temperature <= lb.FreezingTemperature);
                    if (_effFrozen)
                    {
                        DurabilityReflectCache.SetMotorStrength(lb, 10f);
                        lb.InfluenceMotorSpeed(0f, Mathf.Clamp01(tr));
                    }
                    else
                    {
                        if (lb.IsConsideredAlive && lb.PhysicalBehaviour.Temperature <= lb.BodyTemperature - 15f)
                            lb.InfluenceMotorSpeed(Random.Range(-45, 45) * Mathf.Clamp01(tr));
                        DurabilityReflectCache.SetMotorStrengthToMuscleStrength(lb);
                        if (!DurabilityReflectCache.ApplyPoseOverrides(lb) &&
                            lb.IsActiveInCurrentPose &&
                            lb.IsConsideredAlive)
                            DurabilityReflectCache.MoveIntoPose(lb, lb.Person.ActivePose);
                    }

                    if (lb.IsConsideredAlive && lb.PhysicalBehaviour.Charge > float.Epsilon)
                        lb.InfluenceMotorSpeed(
                            -50f * lb.transform.root.localScale.x,
                            lb.PhysicalBehaviour.Charge * 0.5f * sr);

                    if (!lb.IsZombie || !lb.IsConsideredAlive)
                        lb.InfluenceMotorSpeed(0f, lb.SkinMaterialHandler.RottenProgress);
                }

                if ((float)tr < 1f && !lb.Frozen)
                    lb.PhysicalBehaviour.Temperature = _realPhysTemp;

                if (lb.ImmuneToDamage ||
                    lb.PhysicalBehaviour.rigidbody.bodyType != RigidbodyType2D.Dynamic ||
                    !(lb.SpeciesIdentity == "Human") ||
                    !(lb.GForcePassoutThreshold > float.Epsilon))
                {
                    ctx.CancelOriginal = true;
                    return;
                }

                var _gf = DurabilityReflectCache.GetLimbGForce(lb);
                float sqr = _gf.SustainedAcceleration.sqrMagnitude;
                if (sqr > lb.GForcePassoutThreshold * lb.GForcePassoutThreshold)
                {
                    if (lb.HasBrain)
                        lb.Person.Consciousness *= 0.5f;
                    if (sqr > lb.GForceDamageThreshold * lb.GForceDamageThreshold)
                        lb.Damage(sqr / 250f);
                }

                ctx.CancelOriginal = true;
            }
        );

        Harmony.AddPrefix(
            "ls_StunImpact",
            typeof(LimbBehaviour),
            "StunImpact",
            ctx =>
            {
                TryCatchAction(() =>
                {
                    var lb = (LimbBehaviour)ctx.Instance;
                    if (!LimbStatManager.TryGetEntry(lb, out var e))
                        return;
                    float sr = e.Settings.Charge;
                    if (lb.IsAndroid)
                    {
                        lb.PhysicalBehaviour.Charge += 10f * sr;
                        ctx.CancelOriginal = true;
                        return;
                    }
                    lb.PhysicalBehaviour.Charge += 1f * sr;
                    if (lb.NodeBehaviour.IsConnectedToRoot && !lb.IsParalysed)
                    {
                        lb.Person.AddPain(150f);
                        if (UserPreferenceManager.Current.StopAnimationOnDamage)
                            lb.Person.OverridePoseIndex = -1;
                    }
                    lb.StartCoroutine(Utils.DelayCoroutine(
                        Random.value * 0.8f,
                        () =>
                        {
                            lb.Numbness = 1f;
                            if (lb.NodeBehaviour.IsConnectedToRoot)
                                lb.Person.Consciousness = 0f;
                        }
                    ));
                    ctx.CancelOriginal = true;
                });
            }
        );

        Harmony.AddPostfix(
            "ls_MotorStr",
            typeof(LimbBehaviour),
            "get_MotorStrength",
            ctx =>
            {
                var lb = (LimbBehaviour)ctx.Instance;
                if (!LimbStatManager.TryGetEntry(lb, out var e))
                    return;
                float sr = e.Settings.Charge;
                if (Mathf.Approximately(sr, 1f))
                    return;

                float current = (float)ctx.Result;

                if (current > 0f && (lb.Broken || lb.IsParalysed))
                {
                    if (lb.PhysicalBehaviour.Charge * sr < 0.05f)
                    {
                        ctx.Result = 0f;
                        return;
                    }
                }

                if (current <= 0f)
                    return;

                float a =
                    (lb.Health / lb.InitialHealth + Mathf.Clamp01(lb.Person.AdrenalineLevel))
                    * Mathf.Pow(lb.CirculationBehaviour.GetAmountOfBlood(),
                        3f * lb.BloodMuscleStrengthRatio)
                    * Mathf.Pow(lb.CirculationBehaviour.BloodFlow,
                        3f * lb.BloodMuscleStrengthRatio)
                    * Mathf.Clamp01(lb.Person.Consciousness
                                    + Mathf.Clamp01(lb.Person.AdrenalineLevel))
                    * Mathf.Clamp01(1f - lb.PhysicalBehaviour.BurnProgress)
                    * Mathf.Clamp01(1f - lb.SkinMaterialHandler.AcidProgress)
                    * Mathf.Clamp01(1f - (lb.Numbness
                                            - Mathf.Clamp01(lb.Person.AdrenalineLevel)));

                float poseForce = (lb.Person.ActivePose != null && lb.IsActiveInCurrentPose)
                    ? lb.Person.ActivePose.ForceMultiplier
                    : 1f;

                float scaledFloor = lb.PhysicalBehaviour.Charge * 0.5f * sr;
                float massRatio = DurabilityReflectCache.GetMassStrengthRatio(lb);

                ctx.Result = lb.BaseStrength
                                * poseForce
                                * 0.9f
                                * Mathf.Clamp01(Mathf.Max(a, scaledFloor))
                                * massRatio;
            }
        );

        Harmony.AddPostfix(
            "ls_IsCapable",
            typeof(LimbBehaviour),
            "get_IsCapable",
            ctx =>
            {
                if ((bool)ctx.Result)
                    return;

                var lb = (LimbBehaviour)ctx.Instance;
                if (!LimbStatManager.TryGetEntry(lb, out var e))
                    return;
                float tr = e.Settings.Thermal;

                if (tr < 0.05f && !lb.Frozen)
                {
                    if (lb.NodeBehaviour.IsConnectedToRoot
                        && lb.Health > 1f
                        && lb.Person.Consciousness > 0.8f
                        && lb.Person.ShockLevel < 0.5f
                        && lb.Person.PainLevel < 0.5f
                        && lb.Numbness < 0.9f
                        && lb.CirculationBehaviour.InternalBleedingIntensity < 0.5f
                        && lb.CirculationBehaviour.HasCirculation
                        && !lb.Broken
                        && lb.CirculationBehaviour.BloodFlow > 0.25f)
                    {
                        ctx.Result = true;
                    }
                }
            }
        );

        Harmony.AddPrefix(
            "ls_PhysMgdFixed",
            typeof(PhysicalBehaviour),
            "ManagedFixedUpdate",
            ctx =>
            {
                var phys = (PhysicalBehaviour)ctx.Instance;
                // Only intercept registered objects — unregistered fall through to original
                if (!LimbStatManager.TryGetEntry(phys, out var e))
                    return;
                float sr = e.Settings.Charge;
                float tr = e.Settings.Thermal;

                if (phys.gameObject.isStatic)
                    return;

                float _chargeDecay = Mathf.Pow(0.95f, 1f / Mathf.Max(sr, 0.001f));
                phys.Charge *= _chargeDecay;

                if (UserPreferenceManager.Current.CollisionQuality == CollisionQuality.Dynamic)
                {
                    if (phys.rigidbody.velocity.sqrMagnitude > Global.main.DynamicSqrdVelocityThreshold)
                    {
                        if (phys.rigidbody.collisionDetectionMode != CollisionDetectionMode2D.Continuous)
                            phys.rigidbody.collisionDetectionMode = CollisionDetectionMode2D.Continuous;
                    }
                    else if (phys.rigidbody.collisionDetectionMode != CollisionDetectionMode2D.Discrete)
                        phys.rigidbody.collisionDetectionMode = CollisionDetectionMode2D.Discrete;
                }

                if (phys.SimulateTemperature && (float)tr > 0f)
                {
                    if (phys.AmbientTemperatureTransfer)
                    {
                        if (UserPreferenceManager.Current.AmbientTemperatureTransfer)
                            AmbientTemperatureGridBehaviour.Instance.TransferHeat(phys);
                        else
                            phys.Temperature = Mathf.Lerp(
                                phys.Temperature,
                                PhysicalBehaviour.AmbientTemperature,
                                0.0001f / phys.GetHeatCapacity()
                            );
                    }
                    if (phys.OnFire &&
                        phys.Temperature < phys.Properties.BurningTemperatureThreshold * 3f)
                        phys.Temperature = Mathf.Lerp(
                            phys.Temperature,
                            phys.Properties.BurningTemperatureThreshold * 3f,
                            0.01f * phys.TemperatureWhenBurningLerpMultiplier
                        );

                    bool bpLow = phys.BurnProgress <= 0.98f;
                    if (bpLow &&
                        phys.Temperature > phys.Properties.BurningTemperatureThreshold &&
                        phys.Properties.Flammability > float.Epsilon)
                    {
                        if (phys.BurnProgress < 0.2f)
                            phys.BurnProgress += 0.001f;
                        if ((double)Random.value > 0.9995 &&
                            phys.Properties.Flammability > float.Epsilon)
                            phys.Ignite(ignoreFlammability: true,
                                phys.transform.TransformPoint(phys.GetRandomGridPoint()));
                    }

                    if (phys.OnFire)
                        phys.BurnProgress += Mathf.Max(0f, phys.Temperature - phys.Properties.BurningTemperatureThreshold)
                                              * 6E-05f
                                              * (phys.Properties.Burnrate / 2f)
                                              * phys.BurningProgressionMultiplier;

                    if (phys.Temperature < phys.Properties.BurningTemperatureThreshold)
                    {
                        if (bpLow && phys.OnFire)
                            phys.BurnProgress += 2.5E-05f * (phys.Properties.Burnrate / 2f) * phys.BurningProgressionMultiplier;
                        if (phys.OnFire && Random.value > 0.995f)
                            phys.Extinguish();
                    }
                }

                if ((float)sr > 0f)
                {
                    phys.Temperature += phys.Charge * 0.0008f / phys.Properties.HeatTransferSpeedMultiplier * sr;
                    phys.Temperature = Mathf.Clamp(-273.15f, phys.Temperature, 5000000f);
                }

                DurabilityReflectCache.OnCollisionStayWithoutSleep(phys);

                var _dcs = DurabilityReflectCache.GetDecalControllers(phys);
                if (phys.IsUnderWater &&
                    Random.value > 0.95f &&
                    _dcs != null &&
                    _dcs.Any() &&
                    phys.rigidbody.velocity.sqrMagnitude > 2f)
                {
                    foreach (var dc in _dcs)
                        dc.Clear();
                }

                var _pens = phys.penetrations;
                for (int i = 0; i < _pens.Count; i++)
                {
                    var p = _pens[i];
                    DurabilityReflectCache.HandleStabRelease(phys, p);
                    if (!p.Stabber || !p.Victim)
                        continue;
                    if (p.Stabber.SimulateTemperature &&
                        p.Victim.SimulateTemperature &&
                        p.Active &&
                        (float)tr > 0f)
                        Utils.AverageTemperature(p.Victim, p.Stabber, 0.3f);
                    if ((float)sr > 0f)
                    {
                        if (p.Victim.Charge * 0.95f > phys.Charge)
                            phys.Charge = p.Victim.Charge * 0.95f * sr;
                        else if (phys.Charge * 0.95f > p.Victim.Charge)
                            p.Victim.Charge = phys.Charge * 0.95f * sr;
                    }
                }

                if (phys.OnFire)
                {
                    if (Random.value > 0.9993f)
                        phys.Extinguish();
                    Global.main.FireLoopSoundControllerBehaviour.FuzzySetVolumeAt(
                        phys.transform.position, Mathf.Clamp01(phys.BurnIntensity));
                }

                ctx.CancelOriginal = true;
            }
        );

        Harmony.AddPrefix(
            "ls_PhysMgdUpd",
            typeof(PhysicalBehaviour),
            "ManagedUpdate",
            ctx =>
            {
                var phys = (PhysicalBehaviour)ctx.Instance;
                if (!LimbStatManager.TryGetEntry(phys, out var e))
                    return;
                float sr = e.Settings.Charge;
                float tr = e.Settings.Thermal;

                float dt = Time.deltaTime;

                var _audioSrc = DurabilityReflectCache.GetPhysAudioSource(phys);
                if ((bool)_audioSrc && _audioSrc.enabled && !_audioSrc.isPlaying)
                    _audioSrc.enabled = false;
                var _sizzleHeat = DurabilityReflectCache.GetSizzleHeat(phys);
                if (_sizzleHeat > 0f)
                    DurabilityReflectCache.SetSizzleHeat(phys, _sizzleHeat - dt);
                if (phys.gameObject.isStatic)
                {
                    ctx.CancelOriginal = true;
                    return;
                }

                if (phys.BurnProgress < 0.3f &&
                    phys.ChargeBurns &&
                    (float)sr > 0f)
                    phys.BurnProgress += phys.Properties.Burnrate
                                          * phys.Charge
                                          * 0.01f
                                          * Time.timeScale
                                          * sr;

                phys.Wetness = Mathf.Clamp01(phys.Wetness - dt);

                if ((float)tr > 0f && phys.OnFire)
                {
                    phys.BurnIntensity += dt * 0.1f;
                    if (phys.BurnProgress >= 0.995f)
                        DurabilityReflectCache.SetOnFire(phys, false);
                    phys.BurnProgress = Mathf.Clamp01(phys.BurnProgress);
                    if ((double)phys.Wetness > 0.1)
                    {
                        phys.Extinguish();
                        phys.BurnIntensity = 0f;
                    }
                }
                else
                {
                    phys.BurnIntensity -= dt * 0.1f;
                }

                phys.BurnIntensity = Mathf.Clamp01(phys.BurnIntensity);
                DurabilityReflectCache.SetParticleEmission(phys);

                var _affectTimer = DurabilityReflectCache.GetAffectTimer(phys);
                _affectTimer += dt;
                if (_affectTimer > 1f)
                {
                    _affectTimer = 0f;
                    DurabilityReflectCache.SetAffectTimer(phys, _affectTimer);
                    DurabilityReflectCache.AffectSurroundings(phys);

                    if (phys.Deletable && phys.rigidbody.bodyType == RigidbodyType2D.Dynamic)
                    {
                        Vector3 pos = phys.transform.position;
                        if (Global.main.CameraControlBehaviour.BoundingBox.ContainsExpanded(
                                new Vector3(pos.x, pos.y, 0f), 50f))
                            DurabilityReflectCache.SetOobDuration(phys, 0f);
                        else
                        {
                            var _oobDur = DurabilityReflectCache.GetOobDuration(phys);
                            _oobDur += 1f;
                            DurabilityReflectCache.SetOobDuration(phys, _oobDur);
                            if (_oobDur >= 5f)
                            {
                                if ((bool)phys.transform.parent)
                                {
                                    if (!phys.isDisintegrated && phys.Disintegratable)
                                        phys.Disintegrate();
                                }
                                else
                                    Object.Destroy(phys.transform.root.gameObject);
                            }
                        }
                    }
                }

                if (phys.penetrations.Count > 0)
                {
                    for (int i = phys.penetrations.Count - 1; i >= 0; i--)
                    {
                        if (!phys.penetrations[i].Active)
                            phys.penetrations.RemoveAt(i);
                    }
                    foreach (var p in phys.penetrations)
                        p.Duration += dt;
                }

                DurabilityReflectCache.HandleSlidingSounds(phys);
                ctx.CancelOriginal = true;
            }
        );

        Harmony.AddPrefix(
            "ls_PhysColEnt",
            typeof(PhysicalBehaviour),
            "OnCollisionEnter2D",
            ctx =>
            {
                TryCatchAction(() =>
                {
                    var phys = (PhysicalBehaviour)ctx.Instance;
                    if (!LimbStatManager.TryGetEntry(phys, out var e))
                        return;
                    var col = (Collision2D)ctx.Args[0];

                    if (phys.gameObject.isStatic)
                        return;

                    var _cbpType = DurabilityReflectCache.Phys_ColliderBoolPairType;
                    var _contactBuf = DurabilityReflectCache.GetContactBuffer();

                    if (col.transform != phys.transform)
                    {
                        int _freeIdx = -1;
                        bool _hasSlot = DurabilityReflectCache.TryGetFreeSlot(phys, ref _freeIdx);
                        if (_hasSlot)
                        {
                            var _cbpArgs = DurabilityReflectCache.CBP_CtorArgs;
                            _cbpArgs[0] = true;
                            _cbpArgs[1] = col.collider;
                            var _pair = Activator.CreateInstance(_cbpType, _cbpArgs);
                            DurabilityReflectCache.GetOnCollisionStayBuffer(phys).SetValue(_pair, _freeIdx);
                        }
                    }

                    int cnt = col.GetContacts(_contactBuf);
                    if (cnt == 0)
                        return;

                    var fc = Utils.GetFirstValidContact(_contactBuf, cnt);
                    float avgImp = Utils.GetAverageImpulseRemoveOutliers(_contactBuf, cnt);

                    CameraShakeBehaviour.main.Shake(avgImp * 0.01f, phys.transform.position);
                    DurabilityReflectCache.HandleSounds(phys, col, avgImp);
                    DurabilityReflectCache.HandleStabbing(phys, col, fc);

                    if (Global.main.PhysicalObjectsInWorldByTransform.TryGetValue(col.transform, out var val))
                    {
                        if (phys.Properties.SizzleSounds != null &&
                            val.SimulateTemperature &&
                            phys.SimulateTemperature &&
                            Mathf.Abs(val.Temperature - phys.Temperature) > 10f &&
                            val.Temperature > phys.Temperature &&
                            val.Temperature >= phys.Properties.BurningTemperatureThreshold)
                            phys.Sizzle();

                        float sr = e.Settings.Charge;

                        float resistedCharge = phys.Charge * sr;

                        if ((float)sr > 0f &&
                            !(Random.value > 0.5f) &&
                            resistedCharge > val.Charge &&
                            phys.ConductOverride &&
                            val.ConductOverride &&
                            val.Properties.Conducting &&
                            phys.Properties.Conducting &&
                            Mathf.Abs(resistedCharge - val.Charge) > 15f)
                        {
                            val.rigidbody.AddForce(
                                0.2f * Mathf.Abs(resistedCharge - val.Charge)
                                * (val.transform.position - phys.transform.position).normalized,
                                ForceMode2D.Impulse
                            );
                            val.Charge = resistedCharge;
                            Object.Instantiate(
                                Resources.Load<GameObject>("Prefabs/BigZap"),
                                fc.point,
                                Quaternion.identity
                            );
                        }
                    }

                    ctx.CancelOriginal = true;
                });
            },
            new[] { typeof(Collision2D) }
        );

        Harmony.AddPrefix(
            "ls_PhysColStayNoSleep",
            typeof(PhysicalBehaviour),
            "OnCollisionStayWithoutSleep",
            ctx =>
            {
                TryCatchAction(() =>
                {
                    var phys = (PhysicalBehaviour)ctx.Instance;
                    if (!LimbStatManager.TryGetEntry(phys, out var e))
                        return;
                    if (phys.gameObject.isStatic)
                        return;
                    float sr = e.Settings.Charge;
                    float tr = e.Settings.Thermal;

                    var _collBuf = DurabilityReflectCache.GetOnCollisionStayBuffer(phys);

                    for (int i = 0; i < _collBuf.Length; i++)
                    {
                        var _entry = _collBuf.GetValue(i);
                        if (_entry == null || !DurabilityReflectCache.GetCBPActive(_entry))
                            continue;
                        var _coll = DurabilityReflectCache.GetCBPColl(_entry);
                        if (!_coll || !_coll.gameObject.activeSelf)
                        {
                            DurabilityReflectCache.SetCBPActive(_entry, false);
                            continue;
                        }
                        if (!Global.main.PhysicalObjectsInWorldByTransform.TryGetValue(
                                _coll.transform, out var val))
                            break;

                        if ((float)tr > 0f && phys.SimulateTemperature && val.SimulateTemperature)
                            Utils.AverageTemperature(phys, val);

                        if ((float)sr > 0f &&
                            !phys.ForceNoCharge &&
                            phys.Charge > val.Charge &&
                            phys.ConductOverride &&
                            val.ConductOverride &&
                            val.Properties.Conducting &&
                            phys.Properties.Conducting)
                            val.Charge = Mathf.Lerp(val.Charge, phys.Charge * sr, 0.8f);
                    }

                    ctx.CancelOriginal = true;
                });
            }
        );

        Harmony.AddPrefix(
            "ls_Shocked",
            typeof(PhysicalBehaviour),
            "Shocked",
            ctx =>
            {
                TryCatchAction(() =>
                {
                    var phys = (PhysicalBehaviour)ctx.Instance;
                    if (!LimbStatManager.TryGetEntry(phys, out var e))
                        return;
                    var zap = (Zap)ctx.Args[0];
                    float sr = e.Settings.Charge;
                    float tr = e.Settings.Thermal;

                    if (sr > 0f)
                        phys.Charge += Time.deltaTime * 25f * sr;
                    if (tr > 0f && Random.value > 0.9999f)
                        phys.Ignite(zap.position);

                    ctx.CancelOriginal = true;
                });
            },
            new[] { typeof(Zap) }
        );

        Harmony.AddPrefix(
            "ls_IgniteInternal",
            typeof(PhysicalBehaviour),
            "IgniteInternal",
            ctx =>
            {
                TryCatchAction(() =>
                {
                    var phys = (PhysicalBehaviour)ctx.Instance;
                    if (!LimbStatManager.TryGetEntry(phys, out var e))
                        return;
                    if ((float)e.Settings.Thermal <= 0f)
                        return;

                    bool ignoreFlammability = (bool)ctx.Args[0];
                    var _seed = DurabilityReflectCache.GetPhysSeed(phys);
                    if (!phys.gameObject.isStatic &&
                        (ignoreFlammability ||
                         !(Mathf.PerlinNoise(Time.time * 100f, _seed + 235.42f) > phys.Properties.Flammability)) &&
                        !(phys.Properties.Flammability <= float.Epsilon))
                        DurabilityReflectCache.SetOnFire(phys, true);

                    ctx.CancelOriginal = true;
                });
            },
            new[] { typeof(bool) }
        );

        Harmony.AddPrefix(
            "ls_PumpBeh",
            typeof(CirculationBehaviour),
            "PumpBehaviour",
            ctx =>
            {
                TryCatchAction(() =>
                {
                    var circ = (CirculationBehaviour)ctx.Instance;
                    float dt = (float)ctx.Args[0];
                    var lb = circ.Limb;
                    if (!lb || !LimbStatManager.TryGetEntry(lb, out var e))
                        return;

                    float sr = e.Settings.Charge;

                    if (circ.IsPump)
                    {
                        if (lb.PhysicalBehaviour.Charge * sr > 0.5f &&
                            (double)Random.value > 0.995 - (double)(lb.PhysicalBehaviour.Charge * sr / 500f))
                            circ.IsPump = false;
                    }
                    else if (circ.WasInitiallyPumping &&
                             (double)Random.value > 0.999 &&
                             lb.PhysicalBehaviour.Charge * sr > 0.001f &&
                             circ.BloodFlow < 0.1f)
                        circ.IsPump = true;

                    if (circ.IsPump && lb.NodeBehaviour.IsConnectedToRoot && !lb.Person.Braindead)
                        circ.BloodFlow = circ.TotalLiquidAmount;
                    else
                        circ.BloodFlow -= dt / 20f;

                    circ.BloodFlow = Mathf.Clamp(circ.BloodFlow, 0f, circ.TotalLiquidAmount);
                    ctx.CancelOriginal = true;
                });
            },
            new[] { typeof(float) }
        );

        Harmony.AddPrefix(
            "ls_DmgEdge",
            typeof(CirculationBehaviour),
            "HandleDamageEdgeCases",
            ctx =>
            {
                TryCatchAction(() =>
                {
                    var circ = (CirculationBehaviour)ctx.Instance;
                    var lb = circ.Limb;
                    if (!lb || !LimbStatManager.TryGetEntry(lb, out var e))
                        return;
                    float tr = e.Settings.Thermal;

                    float num = Mathf.Max(lb.PhysicalBehaviour.BurnProgress, lb.SkinMaterialHandler.AcidProgress);
                    if (num > 0.8f && lb.PhysicalBehaviour.Temperature > 0f)
                    {
                        float _severity = (num - 0.8f) / 0.2f;
                        float drain = Mathf.Lerp(0.02f, 0.25f, _severity) * (1f + lb.PhysicalBehaviour.BurnIntensity * 2f);
                        circ.Drain(drain * tr);
                    }
                    ctx.CancelOriginal = true;
                });
            }
        );

        Harmony.AddPrefix(
            "ls_HandleBleed",
            typeof(CirculationBehaviour),
            "HandleBleeding",
            ctx =>
            {
                TryCatchAction(() =>
                {
                    var circ = (CirculationBehaviour)ctx.Instance;
                    float dt = (float)ctx.Args[0];
                    var lb = circ.Limb;
                    if (!lb || !LimbStatManager.TryGetEntry(lb, out var e))
                        return;
                    float tr = e.Settings.Thermal;

                    if (circ.BleedingRate > 0.05f &&
                        lb.PhysicalBehaviour.Temperature > 0f &&
                        tr > 0f)
                    {
                        if (circ.BleedingRate < 1f)
                            circ.BleedingRate -= Time.deltaTime * 0.05f;
                        circ.Drain(dt / Mathf.Lerp(60f, 20f, circ.BloodFlow)
                                   * circ.BleedingRate * 0.15f * tr);
                    }
                    ctx.CancelOriginal = true;
                });
            },
            new[] { typeof(float) }
        );

        // 7. PhysicalBehaviour.Disintegrate — block when IgnoreCrush is enabled,
        //    but always allow when the regen system is doing its own cleanup.
        Harmony.AddPrefix(
            "ls_PhysDisintegrate",
            typeof(PhysicalBehaviour),
            "Disintegrate",
            ctx =>
            {
                TryCatchAction(() =>
                {
                    var phys = (PhysicalBehaviour)ctx.Instance;
                    if (LimbStatEntry._isRegenSystemCrush)
                    {
                        // Regen system is destroying an old limb. SyncPhysicalProps
                        // may have set Disintegratable/Deletable=false (due to
                        // IgnoreCrush), which would cause the vanilla Disintegrate()
                        // to return early or prevent GameObject cleanup.
                        if (!phys.Disintegratable)
                            phys.Disintegratable = true;
                        if (!phys.Deletable)
                            phys.Deletable = true;
                        return; // don't block
                    }
                    if (!LimbStatManager.TryGetEntry(phys, out var e))
                        return;
                    // Block if tracked by either system with IgnoreCrush on
                    if ((bool)e.Settings.IgnoreCrush ||
                        (RegenManager.Instance && RegenManager.Instance.TryGetData(e.limb, out _)))
                        ctx.CancelOriginal = true;
                });
            }
        );
    }

    private static void RegisterProjectilePatches()
    {
        Harmony.AddPrefix(
            "ls_Accel",
            typeof(AcceleratorBoltBehaviour),
            "OnHit",
            ctx =>
            {
                TryCatchAction(() =>
                {
                    var hit = (RaycastHit2D)ctx.Args[0];
                    if (LimbStatManager.TryGetEntry(
                            hit.collider.transform?.GetComponent<LimbBehaviour>(), out var e) &&
                        (bool)e.Settings.IgnoreBullets)
                        ctx.CancelOriginal = true;
                });
            },
            new[] { typeof(RaycastHit2D) }
        );

        RegisterBallisticPatch();

        Harmony.AddPrefix(
            "ls_BaseBolt",
            typeof(BaseBoltBehaviour),
            "DoHitCheck",
            ctx =>
            {
                TryCatchAction(() =>
                {
                    var inst = (BaseBoltBehaviour)ctx.Instance;
                    var rh = Physics2D.Raycast(
                        inst.transform.position,
                        inst.transform.right,
                        (float)ctx.Args[0],
                        (int)inst.Layers
                    );
                    if (rh.transform &&
                        LimbStatManager.TryGetEntry(
                            rh.collider.transform?.GetComponent<LimbBehaviour>(), out var e) &&
                        (bool)e.Settings.IgnoreBullets)
                        ctx.CancelOriginal = true;
                });
            },
            new[] { typeof(float) }
        );

        Harmony.AddPrefix(
            "ls_Blaster",
            typeof(BlasterboltBehaviour),
            "Impact",
            ctx =>
            {
                TryCatchAction(() =>
                {
                    var hit = (RaycastHit2D)ctx.Args[0];
                    if (LimbStatManager.TryGetEntry(
                            hit.collider.transform?.GetComponent<LimbBehaviour>(), out var e) &&
                        (bool)e.Settings.IgnoreBullets)
                    {
                        if (Global.main.PhysicalObjectsInWorldByTransform.TryGetValue(
                                hit.collider.transform, out var p) && p.AbsorbsLasers)
                            p.BurnProgress += 0.2f;
                        ctx.CancelOriginal = true;
                    }
                });
            },
            new[] { typeof(RaycastHit2D) }
        );

        Harmony.AddPrefix(
            "ls_FragRayFlag",
            typeof(ExplosionCreator),
            "FragmentationRay",
            ctx => { LimbStatEntry._isFragmentationRay = true; }
        );
        Harmony.AddFinalizer(
            "ls_FragRayFlag",
            typeof(ExplosionCreator),
            "FragmentationRay",
            ctx => { LimbStatEntry._isFragmentationRay = false; }
        );

        Harmony.AddPrefix(
            "ls_Explosion",
            typeof(ExplosionCreator),
            "FragmentationRay",
            ctx =>
            {
                TryCatchAction(() =>
                {
                    int idx = (int)ctx.Args[0];
                    uint rc = (uint)ctx.Args[1];
                    Vector3 pos = (Vector3)ctx.Args[2];
                    float range = (float)ctx.Args[3];
                    float frag = (float)ctx.Args[4];
                    float dismem = (float)ctx.Args[5];
                    float ang = 360f * (idx / (float)rc) * ((float)Math.PI / 180f);

                    var rh = Physics2D.Raycast(
                        pos,
                        new Vector2(Mathf.Cos(ang), Mathf.Sin(ang)),
                        range
                    );

                    if (!rh)
                    {
                        ctx.CancelOriginal = true;
                        return;
                    }

                    rh.transform.BroadcastMessage(
                        "OnFragmentHit", frag, SendMessageOptions.DontRequireReceiver);

                    if ((bool)rh.rigidbody)
                        rh.rigidbody.AddForceAtPosition(
                            (rh.transform.position - pos).normalized * frag,
                            rh.point,
                            ForceMode2D.Impulse
                        );

                    if (!rh.transform.CompareTag("Limb"))
                    {
                        ctx.CancelOriginal = true;
                        return;
                    }

                    LimbStatManager.TryGetEntry(
                        rh.transform.GetComponent<LimbBehaviour>(), out var eb);

                    if (eb != null)
                    {
                        if ((bool)eb.Settings.IgnoreBullets)
                        {
                            ctx.CancelOriginal = true;
                            return;
                        }
                        if ((float)eb.Settings.Explosion == 0f || (float)eb.Settings.Damage == 0f)
                        {
                            ctx.CancelOriginal = true;
                            return;
                        }
                    }

                    var limb = rh.transform.GetComponent<LimbBehaviour>();
                    limb.SkinMaterialHandler.AddDamagePoint(
                        Random.value > 0.5f ? DamageType.Bullet : DamageType.Blunt,
                        rh.point,
                        frag * Random.Range(3, 8)
                    );
                    limb.Damage(frag * 1.5f);

                    if (limb.SpeciesIdentity == "Android" || !(Random.value < dismem))
                    {
                        ctx.CancelOriginal = true;
                        return;
                    }

                    if (UserPreferenceManager.Current.ProceduralFragments &&
                        rh.distance < 3f &&
                        (double)Random.value < 0.25)
                        limb.StartCoroutine(Utils.DelayCoroutine(0.01f, () => limb.Crush()));
                    else
                        limb.Slice();

                    ctx.CancelOriginal = true;
                });
            },
            new[]
            {
                typeof(int),
                typeof(uint),
                typeof(Vector3),
                typeof(float),
                typeof(float),
                typeof(float)
            }
        );

        Harmony.AddPrefix(
            "ls_IonBolt",
            typeof(IonBoltBehaviour),
            "DoHitCheck",
            ctx =>
            {
                TryCatchAction(() =>
                {
                    var inst = (IonBoltBehaviour)ctx.Instance;
                    var rh = Physics2D.Raycast(
                        inst.transform.position,
                        inst.transform.right,
                        (float)ctx.Args[0],
                        (int)inst.Layers
                    );
                    if (rh.transform &&
                        LimbStatManager.TryGetEntry(
                            rh.collider.transform?.GetComponent<LimbBehaviour>(), out var e) &&
                        (bool)e.Settings.IgnoreBullets)
                        ctx.CancelOriginal = true;
                });
            },
            new[] { typeof(float) }
        );

        Harmony.AddPrefix(
            "ls_Stunner",
            typeof(StunnerBehaviour),
            "Update",
            ctx =>
            {
                var inst = (StunnerBehaviour)ctx.Instance;
                var r = inst.transform.right;
                r.Normalize();
                var rh = Physics2D.Raycast(
                    inst.transform.position,
                    r,
                    inst.Speed * Time.deltaTime,
                    (int)inst.mask
                );
                if (rh.transform &&
                    LimbStatManager.TryGetEntry(
                        rh.collider.transform?.GetComponent<LimbBehaviour>(), out var e) &&
                    (bool)e.Settings.IgnoreBullets)
                    ctx.CancelOriginal = true;
            }
        );
    }

    private static void RegisterBallisticPatch()
    {
        var iterType = AccessTools.FirstInner(
            typeof(BallisticsEmitter),
            t => t.Name.Contains("<BallisticIteration>d__")
        );

        Harmony.AddPrefix(
            "ls_Ballistic",
            iterType,
            "MoveNext",
            ctx =>
            {
                TryCatchAction(() =>
                {
                    var inst = ctx.Instance;
                    var type = inst.GetType();
                    var bf = DurabilityReflectCache.GetBallisticsFields(type);
                    var origin = bf.GetOrigin(inst);
                    var emitter = bf.GetEmitter(inst);
                    var dir = bf.GetDirection(inst);
                    var tracer = bf.GetTracer(inst);
                    int iter = bf.GetIteration(inst);

                    var oh = Physics2D.OverlapPoint(
                        origin, (int)emitter.LayersToHit, -0.1f, 0.1f);
                    if (oh != null &&
                        LimbStatManager.TryGetEntry(
                            oh.transform?.GetComponent<LimbBehaviour>(), out var be) &&
                        (bool)be.Settings.IgnoreBullets)
                    {
                        AdvanceBallistic(emitter, origin, dir, iter, tracer);
                        ctx.CancelOriginal = true;
                        return;
                    }

                    var rh = Physics2D.Raycast(
                        origin, dir, emitter.MaxRange,
                        (int)emitter.LayersToHit, -0.1f, 0.1f);

                    if (rh.rigidbody != null &&
                        LimbStatManager.TryGetEntry(
                            rh.rigidbody.transform?.GetComponent<LimbBehaviour>(), out var be2) &&
                        (bool)be2.Settings.IgnoreBullets)
                    {
                        AdvanceBallistic(emitter, origin, dir, iter, tracer);
                        ctx.CancelOriginal = true;
                    }
                });
            }
        );
    }

    private static void AdvanceBallistic(
        BallisticsEmitter emitter, Vector2 origin, Vector2 dir, int iter, LineRenderer tracer)
    {
        var args = DurabilityReflectCache.BallisticIterArgs;
        args[0] = origin + dir.normalized;
        args[1] = dir;
        args[2] = emitter.Cartridge.StartSpeed;
        args[3] = iter + 1;
        args[4] = 0f;
        args[5] = null;
        args[6] = tracer;
        args[7] = false;
        args[8] = false;
        args[9] = 0;
        var routine = (IEnumerator)DurabilityReflectCache.BallisticIteration_MI
            .Invoke(emitter, args);

        float n = Mathf.Clamp(emitter.Cartridge.StartSpeed, 0.05f, 1f);
        if ((bool)tracer)
        {
            tracer.SetPosition(1, Mathf.Clamp01(n) * emitter.MaxRange * Vector3.right);
            tracer.transform.position = origin + dir.normalized;
            tracer.transform.right = dir;
        }

        if (iter + 1 < emitter.MaxBallisticsIterations)
            emitter.ConnectedBehaviour.StartCoroutine(routine);
    }
}


public static class LimbStatUtility
{
    public static readonly int[] DefaultPunchLimbs = { 6, 9, 11, 13 };

    public static void BuffPerson(this PersonBehaviour person)
    {
        RuntimeDebug.LogInfo($"[LimbStat] BuffPerson {person.name} (Strongest)");
        person.BuffPerson(LimbStatPresets.Strongest);
    }

    public static void BuffPerson(
        this PersonBehaviour person, LimbStatParams settings,
        int[] punchLimbs = null, bool zooi = false)
    {
        var mgr = LimbStatManager.Instance;
        if (!mgr)
            return;

        var data = mgr.Register(person, settings, zooi);
        if (data == null)
            return;

        punchLimbs ??= Array.Empty<int>();

        foreach (var kv in data.Entries)
        {
            int idx = System.Array.IndexOf(person.Limbs, kv.Key);
            if (idx >= 0 && punchLimbs.Contains(idx))
                kv.Value.CanPunch.BaseValue = true;
        }

        if (settings.LimbRegen)
        {
            data.RegenPunchLimbs = punchLimbs;
            data.RegenPunchNames = new HashSet<string>();
            foreach (int idx in punchLimbs)
                if (idx >= 0 && idx < person.Limbs.Length)
                    data.RegenPunchNames.Add(person.Limbs[idx].name);

            var regenData = RegenManager.Instance.Register(person);
            if (regenData != null)
            {
                // Only apply initial settings on first setup — BuffPerson is
                // re-invoked by PerformAfterSpawn on every limb regeneration,
                // which would overwrite user changes made via context menu.
                if (data.RegenData == null)
                {
                    regenData.HealRate = settings.LimbRegenRate;
                    regenData.AnimationMode = (RegenAnimationMode)settings.RegenMode;
                    regenData.SeveredLimbHandling = (SeveredLimbHandling)settings.SeveredMode;
                    regenData.severedDelay = settings.RegenDelay;
                }
                regenData.LinkedStatData = data;
                data.RegenData = regenData;
            }
        }
    }

    public static void UnbuffPerson(this PersonBehaviour person)
    {
        var mgr = LimbStatManager.Instance;
        if (mgr)
        {
            if (mgr.TryGetData(person, out var data) && data.RegenData != null)
                RegenManager.Instance.Unregister(person);
            mgr.Unregister(person);
        }
    }
}

public static class LimbStatManagerExtensions
{
    public static LimbStatData GetDataForEntry(this LimbStatManager mgr, LimbStatEntry entry)
        => entry?.Parent;
}