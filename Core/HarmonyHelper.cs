using HarmonyLib;
using Mono.Cecil;
using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TMPro;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.EventSystems;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using UnityEngine.SocialPlatforms;
using UnityEngine.UI;
using UnityEngine.Video;
#pragma warning disable CS0618
#pragma warning disable CS0612
namespace HarmonyHelper;

using DebugTool;

public class PatchContext
{
    public readonly object Instance;
    public readonly object[] Args;
    public readonly MethodBase OriginalMethod;
    public readonly bool IsVoid;

    public object Result;            // 方法返回值 (可修改)
    public bool CancelOriginal;      // 是否拦截原方法 (仅 Prefix 有效)
    public Exception Exception;      // 捕获到的异常 (仅 Finalizer 有效)
    public Dictionary<string, object> State; // 跨阶段状态共享

    public PatchContext(object instance, object[] args, MethodBase original, object result, bool isVoid, Dictionary<string, object> state)
    {
        Instance = instance;
        Args = args;
        OriginalMethod = original;
        Result = result;
        IsVoid = isVoid;
        State = state;
    }

    public T GetArg<T>(int index) => (T)Args[index];
    public void SetArg(int index, object value) => Args[index] = value;
}

public class PatchRegistry
{
    public readonly bool IsVoid;
    private readonly Dictionary<string, Action<PatchContext>> _storage = new();
    private readonly Dictionary<string, Func<PatchContext, IEnumerator>> _coroutineStorage = new();
    private volatile Action<PatchContext>[] _cache = Array.Empty<Action<PatchContext>>();
    private volatile Func<PatchContext, IEnumerator>[] _coroutineCache = Array.Empty<Func<PatchContext, IEnumerator>>();

    public PatchRegistry(bool isVoid) => IsVoid = isVoid;

    public Action<PatchContext>[] GetCache() => _cache;
    public Func<PatchContext, IEnumerator>[] GetCoroutineCache() => _coroutineCache;

    public void Update(string id, Action<PatchContext> action)
    {
        lock (_storage)
        {
            _storage[id] = action;
            _cache = _storage.Values.ToArray();
        }
    }

    public void UpdateCoroutine(string id, Func<PatchContext, IEnumerator> func)
    {
        lock (_coroutineStorage)
        {
            _coroutineStorage[id] = func;
            _coroutineCache = _coroutineStorage.Values.ToArray();
        }
    }

    public void Remove(string id)
    {
        lock (_storage)
        {
            if (_storage.Remove(id))
                _cache = _storage.Values.ToArray();
        }
        lock (_coroutineStorage)
        {
            if (_coroutineStorage.Remove(id))
                _coroutineCache = _coroutineStorage.Values.ToArray();
        }
    }
}

public class DynamicHarmonyManager
{
    private readonly Harmony _harmonyInstance;
    private static readonly ConcurrentDictionary<MethodBase, MethodPatchSet> _masterRegistry = new();

    public DynamicHarmonyManager(string id)
    {
        _harmonyInstance = new Harmony(id);
        RuntimeDebug.LogDebug($"[Harmony] DynamicHarmonyManager created: '{id}'");
    }

    private class MethodPatchSet
    {
        public readonly PatchRegistry Prefixes;
        public readonly PatchRegistry Postfixes;
        public readonly PatchRegistry Finalizers;
        public readonly ConditionalWeakTable<object, PatchRegistry> InstancePrefixes = new();
        public readonly ConditionalWeakTable<object, PatchRegistry> InstancePostfixes = new();
        public readonly ConditionalWeakTable<object, PatchRegistry> InstanceFinalizers = new();
        public readonly ConditionalWeakTable<object, PatchRegistry> CoroutineInstancePrefixes = new();
        public readonly ConditionalWeakTable<object, PatchRegistry> CoroutineInstancePostfixes = new();
        public readonly ConditionalWeakTable<object, PatchRegistry> CoroutineInstanceFinalizers = new();
        public readonly ConcurrentDictionary<object, byte> InstancePrefixKeys = new();
        public readonly ConcurrentDictionary<object, byte> InstancePostfixKeys = new();
        public readonly ConcurrentDictionary<object, byte> InstanceFinalizerKeys = new();
        public readonly ConcurrentDictionary<object, byte> CoroutineInstancePrefixKeys = new();
        public readonly ConcurrentDictionary<object, byte> CoroutineInstancePostfixKeys = new();
        public readonly ConcurrentDictionary<object, byte> CoroutineInstanceFinalizerKeys = new();
        public MethodPatchSet(bool isVoid)
        {
            Prefixes = new PatchRegistry(isVoid);
            Postfixes = new PatchRegistry(isVoid);
            Finalizers = new PatchRegistry(isVoid);
        }
    }

    #region 注入接口

    public DynamicHarmonyManager AddPrefix(string id, Type t, string n, Action<PatchContext> a, Type[] p = null)
        => Register(id, t, n, p, a, "Prefix");

    public DynamicHarmonyManager AddPostfix(string id, Type t, string n, Action<PatchContext> a, Type[] p = null)
        => Register(id, t, n, p, a, "Postfix");

    public DynamicHarmonyManager AddFinalizer(string id, Type t, string n, Action<PatchContext> a, Type[] p = null)
        => Register(id, t, n, p, a, "Finalizer");

    public DynamicHarmonyManager AddInstancePrefix(string id, object instance, string n, Action<PatchContext> a, Type[] p = null)
        => Register(id, instance.GetType(), n, p, a, "Prefix", instance);

    public DynamicHarmonyManager AddInstancePostfix(string id, object instance, string n, Action<PatchContext> a, Type[] p = null)
        => Register(id, instance.GetType(), n, p, a, "Postfix", instance);

    public DynamicHarmonyManager AddInstanceFinalizer(string id, object instance, string n, Action<PatchContext> a, Type[] p = null)
        => Register(id, instance.GetType(), n, p, a, "Finalizer", instance);

    public DynamicHarmonyManager AddCoroutinePrefix(string id, Type t, string n, Func<PatchContext, IEnumerator> a, Type[] p = null)
        => RegisterCoroutine(id, t, n, p, a, "Prefix");

    public DynamicHarmonyManager AddCoroutinePostfix(string id, Type t, string n, Func<PatchContext, IEnumerator> a, Type[] p = null)
        => RegisterCoroutine(id, t, n, p, a, "Postfix");

    public DynamicHarmonyManager AddCoroutineFinalizer(string id, Type t, string n, Func<PatchContext, IEnumerator> a, Type[] p = null)
        => RegisterCoroutine(id, t, n, p, a, "Finalizer");

    public DynamicHarmonyManager AddCoroutineInstancePrefix(string id, object instance, string n, Func<PatchContext, IEnumerator> a, Type[] p = null)
        => RegisterCoroutine(id, instance.GetType(), n, p, a, "Prefix", instance);

    public DynamicHarmonyManager AddCoroutineInstancePostfix(string id, object instance, string n, Func<PatchContext, IEnumerator> a, Type[] p = null)
        => RegisterCoroutine(id, instance.GetType(), n, p, a, "Postfix", instance);

    public DynamicHarmonyManager AddCoroutineInstanceFinalizer(string id, object instance, string n, Func<PatchContext, IEnumerator> a, Type[] p = null)
        => RegisterCoroutine(id, instance.GetType(), n, p, a, "Finalizer", instance);

    public DynamicHarmonyManager AddGeneric(string id, Type t, string n, Type[] gArgs, Type[] pTypes, Action<PatchContext> a, string category = "Prefix")
    {
        var baseMethod = AccessTools.Method(t, n, pTypes);
        var genericMethod = baseMethod.MakeGenericMethod(gArgs);
        return Apply(id, genericMethod, category, null, false, (r, i, act) => r.Update(i, act), a);
    }

    private DynamicHarmonyManager Register(string id, Type type, string name, Type[] args, Action<PatchContext> act, string category, object instance = null)
    {
        var target = ResolveTarget(type, name, args);
        RuntimeDebug.LogDebug($"[Harmony] +{category} id='{id}' on {type.Name}.{name}" + (instance != null ? $" (instance)" : ""));
        return Apply(id, target, category, instance, false, (r, i, a) => r.Update(i, a), act);
    }

    private DynamicHarmonyManager RegisterCoroutine(string id, Type type, string name, Type[] args, Func<PatchContext, IEnumerator> act, string category, object instance = null)
    {
        var target = ResolveTarget(type, name, args);
        RuntimeDebug.LogDebug($"[Harmony] +{category}(coroutine) id='{id}' on {type.Name}.{name}" + (instance != null ? $" (instance)" : ""));
        return Apply(id, target, category, instance, true, (r, i, a) => r.UpdateCoroutine(i, a), act);
    }

    private static MethodBase ResolveTarget(Type type, string name, Type[] args)
    {
        var exactTarget = AccessTools.Method(type, name, args)
                          ?? AccessTools.PropertyGetter(type, name)
                          ?? AccessTools.PropertySetter(type, name);

        return exactTarget switch
        {
            MethodBase t when t != null => t,

            _ => AccessTools.GetDeclaredMethods(type)
                    .Where(m => m.Name == name)
                    .OrderByDescending(m => m.GetParameters().Length == (args?.Length ?? 0))
                    .ThenBy(m => m.IsGenericMethod ? 1 : 0)
                    .FirstOrDefault()
                    ?? throw new MissingMethodException($"Method {name} not found on {type.Name}")
        };
    }

    private delegate void RegistryUpdater<T>(PatchRegistry reg, string id, T act);

    private DynamicHarmonyManager Apply<T>(string id, MethodBase target, string category, object instance, bool isCoroutine, RegistryUpdater<T> updater, T act)
    {
        var set = _masterRegistry.GetOrAdd(target, m =>
        {
            bool isV = (m is MethodInfo mi) ? mi.ReturnType == typeof(void) : true;

            var p = new HarmonyMethod(AccessTools.Method(typeof(DynamicHarmonyManager), isV ? nameof(RouterPrefixVoid) : nameof(RouterPrefixResult)));
            var po = new HarmonyMethod(AccessTools.Method(typeof(DynamicHarmonyManager), isV ? nameof(RouterPostfixVoid) : nameof(RouterPostfixResult)));
            var fi = new HarmonyMethod(AccessTools.Method(typeof(DynamicHarmonyManager), isV ? nameof(RouterFinalizerVoid) : nameof(RouterFinalizerResult)));

            _harmonyInstance.Patch(m, p, po, null, fi);
            RuntimeDebug.LogInfo($"[Harmony] Patched: {m.DeclaringType?.Name}.{m.Name} (void={isV})");
            return new MethodPatchSet(isV);
        });

        if (instance != null)
        {
            var table = category switch
            {
                "Prefix" => isCoroutine ? set.CoroutineInstancePrefixes : set.InstancePrefixes,
                "Postfix" => isCoroutine ? set.CoroutineInstancePostfixes : set.InstancePostfixes,
                "Finalizer" => isCoroutine ? set.CoroutineInstanceFinalizers : set.InstanceFinalizers,
                _ => throw new ArgumentException()
            };
            var keySet = category switch
            {
                "Prefix" => isCoroutine ? set.CoroutineInstancePrefixKeys : set.InstancePrefixKeys,
                "Postfix" => isCoroutine ? set.CoroutineInstancePostfixKeys : set.InstancePostfixKeys,
                "Finalizer" => isCoroutine ? set.CoroutineInstanceFinalizerKeys : set.InstanceFinalizerKeys,
                _ => throw new ArgumentException()
            };
            var reg = table.GetValue(instance, _ => new PatchRegistry(set.Prefixes.IsVoid));
            keySet.TryAdd(instance, 0);
            updater(reg, id, act);
        }
        else
        {
            var reg = category switch { "Prefix" => set.Prefixes, "Postfix" => set.Postfixes, "Finalizer" => set.Finalizers, _ => throw new ArgumentException() };
            updater(reg, id, act);
        }

        return this;
    }

    #endregion

    #region Master 路由层 (分流 Void 与 Result)

    // --- PREFIX ---
    private static bool RouterPrefixResult(object __instance, object[] __args, MethodBase __originalMethod, ref object __result, ref object __state)
        => ExecutePrefix(__instance, __args, __originalMethod, ref __result, ref __state);

    private static bool RouterPrefixVoid(object __instance, object[] __args, MethodBase __originalMethod, ref object __state)
    {
        object d = null; return ExecutePrefix(__instance, __args, __originalMethod, ref d, ref __state);
    }

    // --- POSTFIX ---
    private static void RouterPostfixResult(object __instance, object[] __args, MethodBase __originalMethod, ref object __result, ref object __state)
        => ExecutePostfix(__instance, __args, __originalMethod, ref __result, ref __state);

    private static void RouterPostfixVoid(object __instance, object[] __args, MethodBase __originalMethod, ref object __state)
    {
        object d = null; ExecutePostfix(__instance, __args, __originalMethod, ref d, ref __state);
    }

    // --- FINALIZER ---
    private static Exception RouterFinalizerResult(object __instance, object[] __args, MethodBase __originalMethod, ref object __result, Exception __exception, ref object __state)
        => ExecuteFinalizer(__instance, __args, __originalMethod, ref __result, __exception, ref __state);

    private static Exception RouterFinalizerVoid(object __instance, object[] __args, MethodBase __originalMethod, Exception __exception, ref object __state)
    {
        object d = null; return ExecuteFinalizer(__instance, __args, __originalMethod, ref d, __exception, ref __state);
    }

    #endregion

    #region 执行引擎

    private static bool ExecutePrefix(object inst, object[] args, MethodBase ori, ref object res, ref object state)
    {
        if (!_masterRegistry.TryGetValue(ori, out var set)) return true;
        var ctx = CreateCtx(inst, args, ori, res, set.Prefixes.IsVoid, ref state);

        // 实例级同步
        if (inst != null && set.InstancePrefixes.TryGetValue(inst, out var instReg))
        {
            var instCache = instReg.GetCache();
            for (int i = 0; i < instCache.Length; i++)
            {
                try { instCache[i](ctx); } catch { }
                if (ctx.CancelOriginal) { res = ctx.Result; return false; }
            }
        }

        // 全局同步
        var cache = set.Prefixes.GetCache();
        for (int i = 0; i < cache.Length; i++)
        {
            try { cache[i](ctx); } catch { }
            if (ctx.CancelOriginal) { res = ctx.Result; return false; }
        }

        // 协程 (实例级 → 全局)
        if (inst != null && set.CoroutineInstancePrefixes.TryGetValue(inst, out var cInstReg))
            StartCoroutines(inst, cInstReg.GetCoroutineCache(), ctx);
        StartCoroutines(inst, set.Prefixes.GetCoroutineCache(), ctx);

        return true;
    }

    private static void ExecutePostfix(object inst, object[] args, MethodBase ori, ref object res, ref object state)
    {
        if (!_masterRegistry.TryGetValue(ori, out var set)) return;
        var ctx = CreateCtx(inst, args, ori, res, set.Postfixes.IsVoid, ref state);

        if (inst != null && set.InstancePostfixes.TryGetValue(inst, out var instReg))
        {
            var instCache = instReg.GetCache();
            for (int i = 0; i < instCache.Length; i++) { try { instCache[i](ctx); } catch { } }
        }

        var cache = set.Postfixes.GetCache();
        for (int i = 0; i < cache.Length; i++) { try { cache[i](ctx); } catch { } }

        if (inst != null && set.CoroutineInstancePostfixes.TryGetValue(inst, out var cInstReg))
            StartCoroutines(inst, cInstReg.GetCoroutineCache(), ctx);
        StartCoroutines(inst, set.Postfixes.GetCoroutineCache(), ctx);

        res = ctx.Result;
    }

    private static Exception ExecuteFinalizer(object inst, object[] args, MethodBase ori, ref object res, Exception ex, ref object state)
    {
        if (!_masterRegistry.TryGetValue(ori, out var set)) return ex;
        var ctx = CreateCtx(inst, args, ori, res, set.Finalizers.IsVoid, ref state);
        ctx.Exception = ex;

        if (inst != null && set.InstanceFinalizers.TryGetValue(inst, out var instReg))
        {
            var instCache = instReg.GetCache();
            for (int i = 0; i < instCache.Length; i++) { try { instCache[i](ctx); } catch { } }
        }

        var cache = set.Finalizers.GetCache();
        for (int i = 0; i < cache.Length; i++) { try { cache[i](ctx); } catch { } }

        if (inst != null && set.CoroutineInstanceFinalizers.TryGetValue(inst, out var cInstReg))
            StartCoroutines(inst, cInstReg.GetCoroutineCache(), ctx);
        StartCoroutines(inst, set.Finalizers.GetCoroutineCache(), ctx);

        res = ctx.Result;
        return ctx.Exception;
    }

    private static void StartCoroutines(object host, Func<PatchContext, IEnumerator>[] coroutines, PatchContext ctx)
    {
        if (coroutines.Length == 0) return;
        var monoHost = host as MonoBehaviour;
        if (monoHost == null)
        {
            if (host is GameObject go)
                monoHost = go.GetComponent<MonoBehaviour>() ?? CoroutineHost.Instance;
            else
                monoHost = CoroutineHost.Instance;
        }
        for (int i = 0; i < coroutines.Length; i++)
        {
            try { monoHost.StartCoroutine(coroutines[i](ctx)); } catch { }
        }
    }

    private static PatchContext CreateCtx(object inst, object[] args, MethodBase ori, object res, bool v, ref object state)
    {
        state ??= new Dictionary<string, object>();
        return new PatchContext(inst, args, ori, res, v, (Dictionary<string, object>)state);
    }

    private class CoroutineHost : MonoBehaviour
    {
        private static CoroutineHost _instance;
        public static CoroutineHost Instance
        {
            get
            {
                if (_instance == null)
                {
                    RuntimeDebug.LogDebug("[Harmony] Creating CoroutineHost singleton");
                    var go = new GameObject("DynamicHarmonyCoroutineHost") { hideFlags = HideFlags.HideAndDontSave };
                    DontDestroyOnLoad(go);
                    _instance = go.AddComponent<CoroutineHost>();
                }
                return _instance;
            }
        }
    }

    #endregion

    public void RemoveAllById(string id)
    {
        RuntimeDebug.LogDebug($"[Harmony] Removing all patches with id='{id}'");
        foreach (var set in _masterRegistry.Values)
        {
            set.Prefixes.Remove(id);
            set.Postfixes.Remove(id);
            set.Finalizers.Remove(id);

            foreach (var key in set.InstancePrefixKeys.Keys)
                if (set.InstancePrefixes.TryGetValue(key, out var r))
                    r.Remove(id);
            foreach (var key in set.InstancePostfixKeys.Keys)
                if (set.InstancePostfixes.TryGetValue(key, out var r))
                    r.Remove(id);
            foreach (var key in set.InstanceFinalizerKeys.Keys)
                if (set.InstanceFinalizers.TryGetValue(key, out var r))
                    r.Remove(id);
            foreach (var key in set.CoroutineInstancePrefixKeys.Keys)
                if (set.CoroutineInstancePrefixes.TryGetValue(key, out var r))
                    r.Remove(id);
            foreach (var key in set.CoroutineInstancePostfixKeys.Keys)
                if (set.CoroutineInstancePostfixes.TryGetValue(key, out var r))
                    r.Remove(id);
            foreach (var key in set.CoroutineInstanceFinalizerKeys.Keys)
                if (set.CoroutineInstanceFinalizers.TryGetValue(key, out var r))
                    r.Remove(id);
        }
    }

    public void RemoveInstance(string id, object instance)
    {
        if (instance == null) return;
        foreach (var set in _masterRegistry.Values)
        {
            if (set.InstancePrefixes.TryGetValue(instance, out var reg))
                reg.Remove(id);
            if (set.InstancePostfixes.TryGetValue(instance, out var reg2))
                reg2.Remove(id);
            if (set.InstanceFinalizers.TryGetValue(instance, out var reg3))
                reg3.Remove(id);
            if (set.CoroutineInstancePrefixes.TryGetValue(instance, out var reg4))
                reg4.Remove(id);
            if (set.CoroutineInstancePostfixes.TryGetValue(instance, out var reg5))
                reg5.Remove(id);
            if (set.CoroutineInstanceFinalizers.TryGetValue(instance, out var reg6))
                reg6.Remove(id);
        }
    }

    public void RemoveAllForInstance(object instance)
    {
        if (instance == null) return;
        foreach (var set in _masterRegistry.Values)
        {
            set.InstancePrefixes.Remove(instance);
            set.InstancePostfixes.Remove(instance);
            set.InstanceFinalizers.Remove(instance);
            set.CoroutineInstancePrefixes.Remove(instance);
            set.CoroutineInstancePostfixes.Remove(instance);
            set.CoroutineInstanceFinalizers.Remove(instance);
            set.InstancePrefixKeys.TryRemove(instance, out _);
            set.InstancePostfixKeys.TryRemove(instance, out _);
            set.InstanceFinalizerKeys.TryRemove(instance, out _);
            set.CoroutineInstancePrefixKeys.TryRemove(instance, out _);
            set.CoroutineInstancePostfixKeys.TryRemove(instance, out _);
            set.CoroutineInstanceFinalizerKeys.TryRemove(instance, out _);
        }
    }
}

public class MemoryAccessContext
{
    public object Instance;
    public object Value;
    public MemberInfo Member;
    public bool CancelOriginal;
    public bool IsWrite;
    public MemoryAccessContext(object instance, object value, MemberInfo member, bool isWrite)
    {
        Instance = instance;
        Value = value;
        Member = member;
        IsWrite = isWrite;
    }
}

public delegate void MemoryAccessHandler(MemoryAccessContext ctx);

public class MemoryAccessAnalyzer
{
    private readonly Harmony _harmony;
    private static readonly ConcurrentDictionary<MethodInfo, MemberInfo> _methodToMember = new();
    private static readonly ConcurrentDictionary<int, MemoryAccessHandler> _callbacks = new();
    private static readonly ConcurrentDictionary<int, MemberInfo> _indexToMember = new();
    private static readonly ConcurrentDictionary<Assembly, Type[]> _assemblyTypesCache = new();
    private static readonly ConcurrentDictionary<Type, MethodInfo[]> _typeMethodsCache = new();
    private static readonly ConcurrentDictionary<MethodInfo, MethodAnalysisInfo> _methodAnalysisCache = new();
    private static int _targetIdCounter = 0;
    private static readonly ConcurrentDictionary<MemberInfo, int> _memberToId = new();
    private static readonly HashSet<MethodBase> _patchedMethods = new();
    private static HarmonyMethod _sharedTranspiler;
    private class AssemblyCacheInfo
    {
        public Assembly Assembly;
        public bool HasPhysicalFile;
        public string Location;
    }
    private static readonly ConcurrentDictionary<Assembly, AssemblyCacheInfo> _cachedTargetAssemblies = new();
    private static bool _assemblyPatchApplied = false;
    private static readonly object _assemblyPatchLock = new object();
    [ThreadStatic]
    private static bool _isProcessingAssembly;
    private class MethodAnalysisInfo
    {
        public bool IsEmpty;
        public bool HasFieldOps;
        public bool HasCallOps;
        public List<CodeInstruction> ParsedInstructions;
    }
    private readonly List<MonitorRequest> _pendingRequests = new();
    private readonly object _pendingRequestsLock = new object();
    private class MonitorRequest
    {
        public MemberInfo Target;
        public int Index;
        public bool IsField;
        public string TargetName;
        public string DeclaringTypeFullName;
    }
    private static readonly ConcurrentDictionary<MethodBase, Dictionary<int, MemberInfo>> _prebuiltAccessPoints = new();
    // 追踪 (scope, memberId) 已应用关系，用于自动检测同一 scope 内的重复注册
    private static readonly ConcurrentDictionary<int, ConcurrentDictionary<string, byte>> _appliedScopeMembers = new();
    private static bool TryClaimScope(int memberId, string scope)
    {
        var scopes = _appliedScopeMembers.GetOrAdd(memberId, _ => new ConcurrentDictionary<string, byte>());
        return scopes.TryAdd(scope, 0);
    }
    private static bool IsScopeClaimed(int memberId, string scope)
    {
        return _appliedScopeMembers.TryGetValue(memberId, out var scopes) && scopes.ContainsKey(scope);
    }
    private static string MakeAssemblyScope(Assembly ass) => "asm:" + (ass.GetName().Name ?? ass.FullName);
    private static string MakeTypeScope(Type type) => "type:" + (type.FullName ?? type.Name);

    public MemoryAccessAnalyzer(string id)
    {
        _harmony = new Harmony(id);
        _sharedTranspiler = new HarmonyMethod(AccessTools.Method(typeof(MemoryAccessAnalyzer), nameof(UniversalTranspiler)));
        RuntimeDebug.LogInfo($"[MemoryAnalyzer] Created: '{id}'");
        lock (_assemblyPatchLock)
        {
            if (!_assemblyPatchApplied)
            {
                RuntimeDebug.LogDebug("[MemoryAnalyzer] Installing assembly-load hooks...");
                InitializeAssemblyCache();
                PatchAssemblyLoad(_harmony);
                _assemblyPatchApplied = true;
                RuntimeDebug.LogDebug($"[MemoryAnalyzer] Assembly hooks installed, {_cachedTargetAssemblies.Count} target assemblies cached");
            }
        }
    }

    #region 程序集增量缓存与拦截机制
    private static void InitializeAssemblyCache()
    {
        foreach (var ass in AppDomain.CurrentDomain.GetAssemblies())
        {
            ProcessNewAssembly(ass);
        }
    }

    private static readonly HashSet<MethodBase> _patchedAssemblyLoadMethods = new();

    private static void PatchAssemblyLoad(Harmony harmony)
    {
        var postfix = new HarmonyMethod(AccessTools.Method(typeof(MemoryAccessAnalyzer), nameof(AssemblyLoadPostfix)));
        var methods = typeof(Assembly).GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(m => m.Name == "Load" || m.Name == "LoadFrom" || m.Name == "LoadFile");
        int patched = 0, duplicates = 0;
        foreach (var method in methods)
        {
            if (method.IsGenericMethod) continue;
            lock (_patchedAssemblyLoadMethods)
            {
                if (_patchedAssemblyLoadMethods.Contains(method))
                {
                    RuntimeDebug.LogWarning($"[MemoryAnalyzer] Duplicate AssemblyLoad hook: '{method.Name}' already patched, skipping.");
                    duplicates++;
                    continue;
                }
            }
            try
            {
                harmony.Patch(method, postfix: postfix);
                lock (_patchedAssemblyLoadMethods) { _patchedAssemblyLoadMethods.Add(method); }
                patched++;
            }
            catch (Exception ex)
            {
                RuntimeDebug.LogError($"[MemoryAnalyzer] AssemblyLoad hook failed for {method.Name}: {ex.Message}");
            }
        }
        RuntimeDebug.LogDebug($"[MemoryAnalyzer] AssemblyLoad hooks: {patched} patched, {duplicates} duplicate(s) skipped.");
        AppDomain.CurrentDomain.AssemblyLoad += (sender, args) =>
        {
            if (args.LoadedAssembly != null) AssemblyLoadPostfix(args.LoadedAssembly);
        };
    }

    public static void AssemblyLoadPostfix(Assembly __result)
    {
        if (__result == null || _isProcessingAssembly) return;
        try
        {
            _isProcessingAssembly = true;
            ProcessNewAssembly(__result);
        }
        finally
        {
            _isProcessingAssembly = false;
        }
    }

    private static void ProcessNewAssembly(Assembly ass)
    {
        if (ass == null || ass.IsDynamic || _cachedTargetAssemblies.ContainsKey(ass)) return;
        string name = ass.GetName().Name;
        bool isTarget = !name.StartsWith("System") &&
                        !name.StartsWith("UnityEngine") &&
                        !name.StartsWith("UnityEditor") &&
                        !name.StartsWith("Unity.") &&
                        !name.StartsWith("Mono.") &&
                        !name.StartsWith("mscorlib") &&
                        !name.StartsWith("netstandard") &&
                        !name.StartsWith("0Harmony") &&
                        !name.StartsWith("Microsoft.") &&
                        !name.StartsWith("Newtonsoft.Json");
        if (isTarget)
        {
            bool hasPhysical = false;
            string location = string.Empty;
            try
            {
                location = ass.Location;
                hasPhysical = !string.IsNullOrEmpty(location) && File.Exists(location);
            }
            catch { }
            _cachedTargetAssemblies.TryAdd(ass, new AssemblyCacheInfo
            {
                Assembly = ass,
                HasPhysicalFile = hasPhysical,
                Location = location
            });
            RuntimeDebug.LogDebug($"[MemoryAnalyzer] Cached target assembly: {name}");
        }
    }
    #endregion

    public void RegisterVariable(Type targetType, string memberName, MemoryAccessHandler onAccess)
    {
        var target = AccessTools.Field(targetType, memberName) ?? (MemberInfo)AccessTools.Property(targetType, memberName);
        if (target == null) throw new MissingMemberException(targetType.Name, memberName);
        if (target is PropertyInfo pi && pi.GetIndexParameters().Length > 0)
            throw new NotSupportedException("不支持索引器。");

        int index = _memberToId.GetOrAdd(target, _ => Interlocked.Increment(ref _targetIdCounter));
        bool isNew = _callbacks.TryAdd(index, onAccess);
        if (!isNew)
        {
            // 不同 scope 注册同一变量时，合并回调
            var existing = _callbacks[index];
            _callbacks[index] = (MemoryAccessHandler)Delegate.Combine(existing, onAccess);
            RuntimeDebug.LogInfo($"[MemoryAnalyzer] Merged callback for '{targetType.Name}.{memberName}' (id={index}) — total handlers: {_callbacks[index].GetInvocationList().Length}");
        }
        else
        {
            _indexToMember[index] = target;
            RuntimeDebug.LogInfo($"[MemoryAnalyzer] Monitoring: {targetType.Name}.{memberName} (id={index})");
        }

        if (target is PropertyInfo prop)
        {
            var g = prop.GetGetMethod(true);
            var s = prop.GetSetMethod(true);
            if (g != null) _methodToMember[g] = target;
            if (s != null) _methodToMember[s] = target;
        }
        lock (_pendingRequestsLock)
        {
            if (!_pendingRequests.Any(r => r.Target == target))
            {
                _pendingRequests.Add(new MonitorRequest
                {
                    Target = target,
                    Index = index,
                    IsField = target is FieldInfo,
                    TargetName = target.Name,
                    DeclaringTypeFullName = target.DeclaringType.FullName?.Replace('+', '/')
                });
            }
        }
    }

    /// <summary>
    /// 注册变量监控，并仅在指定程序集内异步扫描应用（注册+Apply一步完成，默认异步）。
    /// 同一程序集对同一变量多次注册时，自动舍弃后面的注册。
    /// </summary>
    public Task RegisterVariable(Assembly targetAssembly, Type targetType, string memberName, MemoryAccessHandler onAccess)
    {
        if (targetAssembly == null) throw new ArgumentNullException(nameof(targetAssembly));
        // 预解析 target，提前检测 scope 重复注册
        var target = AccessTools.Field(targetType, memberName) ?? (MemberInfo)AccessTools.Property(targetType, memberName);
        if (target == null) throw new MissingMemberException(targetType.Name, memberName);
        if (target is PropertyInfo pi && pi.GetIndexParameters().Length > 0)
            throw new NotSupportedException("不支持索引器。");
        int index = _memberToId.GetOrAdd(target, _ => Interlocked.Increment(ref _targetIdCounter));
        string scope = MakeAssemblyScope(targetAssembly);
        if (IsScopeClaimed(index, scope))
        {
            RuntimeDebug.LogWarning($"[MemoryAnalyzer] Duplicate registration discarded: '{targetType.Name}.{memberName}' already registered for assembly '{targetAssembly.GetName().Name}'.");
            return Task.CompletedTask;
        }
        RegisterVariable(targetType, memberName, onAccess);
        RuntimeDebug.LogInfo($"[MemoryAnalyzer] RegisterVariable: auto-applying to assembly {targetAssembly.GetName().Name}...");
        return Task.Run(() => ApplyPendingCorepublic(new[] { targetAssembly }, null, scope));
    }

    /// <summary>
    /// 注册变量监控，并仅在指定类型所在的程序集内异步扫描应用（泛型版本，默认异步）。
    /// </summary>
    public Task RegisterVariable<TAssembly>(Type targetType, string memberName, MemoryAccessHandler onAccess)
    {
        return RegisterVariable(typeof(TAssembly).Assembly, targetType, memberName, onAccess);
    }

    /// <summary>
    /// 检查指定成员是否已被注册监控。
    /// </summary>
    public bool IsRegistered(Type targetType, string memberName)
    {
        var target = AccessTools.Field(targetType, memberName) ?? (MemberInfo)AccessTools.Property(targetType, memberName);
        return target != null && _memberToId.ContainsKey(target);
    }

    /// <summary>
    /// 尝试获取指定成员注册的回调数量。返回 -1 表示未注册。
    /// </summary>
    public int GetRegisteredCallbackCount(Type targetType, string memberName)
    {
        var target = AccessTools.Field(targetType, memberName) ?? (MemberInfo)AccessTools.Property(targetType, memberName);
        if (target == null || !_memberToId.TryGetValue(target, out int index))
            return -1;
        if (_callbacks.TryGetValue(index, out var handler) && handler != null)
            return handler.GetInvocationList().Length;
        return 0;
    }

    /// <summary>
    /// 对所有已缓存的程序集扫描并应用挂起的监控请求（默认异步，防止占用主线程）。
    /// </summary>
    public Task ApplyAll()
    {
        lock (_pendingRequestsLock)
        {
            if (_pendingRequests.Count == 0) return Task.CompletedTask;
        }
        RuntimeDebug.LogInfo($"[MemoryAnalyzer] ApplyAll: {_pendingRequests.Count} pending requests, launching background scan...");
        return Task.Run(ApplyPendingCore);
    }

    [Obsolete("默认已是异步，请使用 ApplyAll()。")]
    public Task ApplyAllAsync() => ApplyAll();

    /// <summary>
    /// 仅对指定程序集扫描并应用挂起的监控请求（默认异步，防止占用主线程）。
    /// </summary>
    public Task ApplyToAssembly(Assembly targetAssembly)
    {
        if (targetAssembly == null) throw new ArgumentNullException(nameof(targetAssembly));
        lock (_pendingRequestsLock)
        {
            if (_pendingRequests.Count == 0) return Task.CompletedTask;
        }
        string scope = MakeAssemblyScope(targetAssembly);
        RuntimeDebug.LogInfo($"[MemoryAnalyzer] ApplyToAssembly: {targetAssembly.GetName().Name}...");
        return Task.Run(() => ApplyPendingCorepublic(new[] { targetAssembly }, null, scope));
    }

    /// <summary>
    /// 仅对指定程序集扫描并应用挂起的监控请求（按程序集名称查找，默认异步）。
    /// </summary>
    public Task ApplyToAssembly(string assemblyName)
    {
        if (string.IsNullOrEmpty(assemblyName)) throw new ArgumentNullException(nameof(assemblyName));
        var ass = AppDomain.CurrentDomain.GetAssemblies()
            .FirstOrDefault(a => a.GetName().Name == assemblyName);
        if (ass == null) throw new ArgumentException($"Assembly not found: {assemblyName}");
        return ApplyToAssembly(ass);
    }

    /// <summary>
    /// 仅对指定类型所在的程序集扫描并应用挂起的监控请求（泛型版本，默认异步）。
    /// </summary>
    public Task ApplyToAssembly<T>()
    {
        return ApplyToAssembly(typeof(T).Assembly);
    }

    /// <summary>
    /// 仅对指定类型扫描并应用挂起的监控请求（只扫描该类的方法，默认异步）。
    /// </summary>
    public Task ApplyToType(Type targetType)
    {
        if (targetType == null) throw new ArgumentNullException(nameof(targetType));
        lock (_pendingRequestsLock)
        {
            if (_pendingRequests.Count == 0) return Task.CompletedTask;
        }
        string scope = MakeTypeScope(targetType);
        RuntimeDebug.LogInfo($"[MemoryAnalyzer] ApplyToType: {targetType.FullName}...");
        return Task.Run(() => ApplyPendingCorepublic(new[] { targetType.Assembly }, new[] { targetType }, scope));
    }

    /// <summary>
    /// 仅对指定类型扫描并应用挂起的监控请求（泛型版本，默认异步）。
    /// </summary>
    public Task ApplyToType<T>()
    {
        return ApplyToType(typeof(T));
    }

    [Obsolete("默认已是异步，请使用 ApplyToAssembly(Assembly)。")]
    public Task ApplyToAssemblyAsync(Assembly targetAssembly) => ApplyToAssembly(targetAssembly);

    [Obsolete("默认已是异步，请使用 ApplyToType(Type)。")]
    public Task ApplyToTypeAsync(Type targetType) => ApplyToType(targetType);

    private void ApplyPendingCore()
    {
        ApplyPendingCorepublic(null, null, null);
    }

    private void ApplyPendingCorepublic(IEnumerable<Assembly> targetAssemblies, IEnumerable<Type> targetTypes, string scope = null)
    {
        List<MonitorRequest> requestsToProcess;
        lock (_pendingRequestsLock)
        {
            if (_pendingRequests.Count == 0) return;
            requestsToProcess = _pendingRequests.ToList();
            _pendingRequests.Clear();
        }

        // scope 过滤：同一 scope 内已应用的变量自动跳过，放回 pending 供其他 scope 使用
        if (scope != null)
        {
            var filtered = new List<MonitorRequest>();
            int skipped = 0;
            foreach (var r in requestsToProcess)
            {
                if (IsScopeClaimed(r.Index, scope))
                {
                    lock (_pendingRequestsLock) { _pendingRequests.Add(r); }
                    skipped++;
                }
                else
                {
                    filtered.Add(r);
                }
            }
            if (skipped > 0)
                RuntimeDebug.LogWarning($"[MemoryAnalyzer] Scope '{scope}': {skipped} request(s) already applied, skipped.");
            if (filtered.Count == 0)
            {
                RuntimeDebug.LogInfo($"[MemoryAnalyzer] Scope '{scope}': all {requestsToProcess.Count} request(s) already applied, nothing to do.");
                return;
            }
            requestsToProcess = filtered;
        }

        // 确定要扫描的程序集范围
        List<AssemblyCacheInfo> assembliesToScan;
        if (targetAssemblies != null)
        {
            var assSet = new HashSet<Assembly>(targetAssemblies);
            assembliesToScan = _cachedTargetAssemblies.Values
                .Where(c => assSet.Contains(c.Assembly))
                .ToList();
            // 未缓存的程序集先加入缓存再扫描
            foreach (var ass in targetAssemblies)
            {
                if (!_cachedTargetAssemblies.ContainsKey(ass))
                {
                    ProcessNewAssembly(ass);
                    if (_cachedTargetAssemblies.TryGetValue(ass, out var info))
                        assembliesToScan.Add(info);
                }
            }
        }
        else
        {
            assembliesToScan = _cachedTargetAssemblies.Values.ToList();
        }

        string scopeDesc = targetTypes != null
            ? $"{targetTypes.Count()} type(s)"
            : (targetAssemblies != null ? $"{targetAssemblies.Count()} assembly(s)" : $"{assembliesToScan.Count} assemblies");
        RuntimeDebug.LogInfo($"[MemoryAnalyzer] Scanning {requestsToProcess.Count} request(s) across {scopeDesc}...");

        var methodsToPatch = new ConcurrentBag<MethodBase>();
        var pendingTargetsSet = new HashSet<MemberInfo>(requestsToProcess.Select(r => r.Target));

        foreach (var r in requestsToProcess)
        {
            if (r.Target is PropertyInfo pi)
            {
                if (pi.GetGetMethod(true) is MethodInfo getM) pendingTargetsSet.Add(getM);
                if (pi.GetSetMethod(true) is MethodInfo setM) pendingTargetsSet.Add(setM);
            }
        }

        bool hasFields = requestsToProcess.Any(r => r.IsField);
        bool hasProps = requestsToProcess.Any(r => !r.IsField);

        var sw = Stopwatch.StartNew();

        if (targetTypes != null)
        {
            // 精细扫描：仅扫描指定类型的方法
            foreach (var type in targetTypes)
            {
                ScanTypeBatch(type, hasFields, hasProps, pendingTargetsSet, methodsToPatch);
            }
        }
        else
        {
            Parallel.ForEach(assembliesToScan, cacheInfo =>
            {
                ScanInMemoryAssemblyBatch(cacheInfo.Assembly, hasFields, hasProps, pendingTargetsSet, methodsToPatch);
            });
        }
        sw.Stop();

        var methodsToPatchList = methodsToPatch.Distinct().ToList();
        RuntimeDebug.LogInfo($"[MemoryAnalyzer] Scan done in {sw.ElapsedMilliseconds}ms — {methodsToPatchList.Count} methods to transpile");

        int patched = 0;
        int duplicatePatches = 0;
        foreach (var m in methodsToPatchList)
        {
            lock (_patchedMethods)
            {
                if (_patchedMethods.Contains(m))
                {
                    RuntimeDebug.LogWarning($"[MemoryAnalyzer] Duplicate patch: '{m.DeclaringType?.FullName}.{m.Name}' already transpiled by this analyzer — re-applying.");
                    _harmony.Unpatch(m, HarmonyPatchType.Transpiler, _harmony.Id);
                    duplicatePatches++;
                }
                try
                {
                    _harmony.Patch(m, transpiler: _sharedTranspiler);
                    _patchedMethods.Add(m);
                    patched++;
                }
                catch (Exception ex)
                {
                    RuntimeDebug.LogError($"[MemoryAnalyzer] Patch failed for {m.DeclaringType?.Name}.{m.Name}: {ex.Message}");
                }
            }
        }
        if (duplicatePatches > 0)
            RuntimeDebug.LogWarning($"[MemoryAnalyzer] {duplicatePatches} duplicate patch(es) detected and re-applied.");
        RuntimeDebug.LogInfo($"[MemoryAnalyzer] Transpiler applied to {patched}/{methodsToPatchList.Count} methods");

        // 标记 scope：成功后记录 (memberId, scope) 已应用关系
        if (scope != null)
        {
            foreach (var r in requestsToProcess)
            {
                TryClaimScope(r.Index, scope);
            }
            RuntimeDebug.LogDebug($"[MemoryAnalyzer] Scope '{scope}': claimed {requestsToProcess.Count} request(s).");
        }
    }
    // ====================== 修改结束 ======================

    public void UnmonitorVariable(Type targetType, string memberName, MemoryAccessHandler onAccess)
    {
        var target = AccessTools.Field(targetType, memberName) ?? (MemberInfo)AccessTools.Property(targetType, memberName);
        if (target == null) return;
        if (_memberToId.TryGetValue(target, out int index))
        {
            if (_callbacks.TryGetValue(index, out var existing))
            {
                var newDelegate = (MemoryAccessHandler)Delegate.Remove(existing, onAccess);
                if (newDelegate == null)
                {
                    _callbacks.TryRemove(index, out _);
                    _memberToId.TryRemove(target, out _);
                    RuntimeDebug.LogInfo($"[MemoryAnalyzer] Unmonitored: {targetType.Name}.{memberName} (id={index})");
                }
                else _callbacks[index] = newDelegate;
            }
        }
    }

    public static object InterceptValueTypeWrite<TInstance>(ref TInstance instance, object value, int index, out bool cancel)
    {
        var ctx = new MemoryAccessContext(null, value, _indexToMember[index], true);
        ctx.Instance = instance;
        if (_callbacks.TryGetValue(index, out var action))
        {
            action(ctx);
            if (ctx.Instance != null)
            {
                if (ctx.Instance is TInstance typedInst)
                    instance = typedInst;
                else
                    throw new InvalidCastException($"MemoryAccessAnalyzer: 类型不匹配。");
            }
            else throw new InvalidOperationException($"MemoryAccessAnalyzer: 无法将 null 分配给值类型。");
        }
        cancel = ctx.CancelOriginal;
        return ctx.Value;
    }

    public static object InterceptValueTypeRead<TInstance>(ref TInstance instance, object value, int index)
    {
        var ctx = new MemoryAccessContext(null, value, _indexToMember[index], false);
        ctx.Instance = instance;
        if (_callbacks.TryGetValue(index, out var action))
        {
            action(ctx);
            if (ctx.Instance != null)
            {
                if (ctx.Instance is TInstance typedInst)
                    instance = typedInst;
                else
                    throw new InvalidCastException($"MemoryAccessAnalyzer: 类型不匹配。");
            }
            else throw new InvalidOperationException($"MemoryAccessAnalyzer: 无法将 null 分配给值类型。");
        }
        return ctx.Value;
    }

    public static bool RouterWriteInst<TInstance>(int index, ref TInstance instance, ref object value)
    {
        if (_callbacks.TryGetValue(index, out var action))
        {
            var member = _indexToMember.TryGetValue(index, out var m) ? $"{m.DeclaringType?.Name}.{m.Name}" : "?";
            RuntimeDebug.LogDebug($"[MemoryAnalyzer] RouterWriteInst INVOKED: id={index} member={member} oldValue={value} instance={instance}");
            var ctx = new MemoryAccessContext(instance, value, _indexToMember[index], true);
            try
            {
                action(ctx);
                RuntimeDebug.LogDebug($"[MemoryAnalyzer] RouterWriteInst RESULT: id={index} newValue={ctx.Value} cancel={ctx.CancelOriginal}");
            }
            catch (Exception ex)
            {
                RuntimeDebug.LogError($"[MemoryAnalyzer] RouterWriteInst callback ERROR: id={index} {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
            }
            if (ctx.Instance != null)
            {
                if (ctx.Instance is TInstance typedInst) instance = typedInst;
                else throw new InvalidCastException();
            }
            else if (!typeof(TInstance).IsValueType) instance = default;
            value = ctx.Value;
            return ctx.CancelOriginal;
        }
        RuntimeDebug.LogDebug($"[MemoryAnalyzer] RouterWriteInst MISS: id={index} — no callback registered in _callbacks");
        return false;
    }

    public static void RouterReadInst<TInstance>(int index, ref TInstance instance, ref object value)
    {
        if (_callbacks.TryGetValue(index, out var action))
        {
            var ctx = new MemoryAccessContext(instance, value, _indexToMember[index], false);
            action(ctx);
            if (ctx.Instance != null)
            {
                if (ctx.Instance is TInstance typedInst) instance = typedInst;
                else throw new InvalidCastException();
            }
            else if (!typeof(TInstance).IsValueType) instance = default;
            value = ctx.Value;
        }
    }

    public static bool RouterWriteStatic(int index, ref object value)
    {
        if (_callbacks.TryGetValue(index, out var action))
        {
            var ctx = new MemoryAccessContext(null, value, _indexToMember[index], true);
            action(ctx);
            value = ctx.Value;
            return ctx.CancelOriginal;
        }
        return false;
    }

    public static void RouterReadStatic(int index, ref object value)
    {
        if (_callbacks.TryGetValue(index, out var action))
        {
            var ctx = new MemoryAccessContext(null, value, _indexToMember[index], false);
            action(ctx);
            value = ctx.Value;
        }
    }

    private static bool IsPrefix(OpCode opcode) => opcode == OpCodes.Volatile || opcode == OpCodes.Unaligned;

    private static MemberInfo GetActualTarget(MemberInfo m)
    {
        if (m is MethodInfo mi && _methodToMember.TryGetValue(mi, out var target))
            return target;
        return m;
    }

    private static bool IsMonitoredInstruction(CodeInstruction inst)
    {
        if (inst.operand is not MemberInfo m) return false;
        var actual = GetActualTarget(m);
        return TryGetMemberId(actual, out _);
    }

    public static IEnumerable<CodeInstruction> UniversalTranspiler(IEnumerable<CodeInstruction> instructions, ILGenerator il, MethodBase __originalMethod)
    {
        var codes = instructions.ToList();
        var newCodes = new List<CodeInstruction>(codes.Count + 256);
        var accessDict = new Dictionary<int, MemberInfo>();
        bool hasPrebuilt = __originalMethod != null && _prebuiltAccessPoints.TryGetValue(__originalMethod, out accessDict);
        int hookCount = 0;
        RuntimeDebug.LogDebug($"[MemoryAnalyzer] Transpiler invoked: {__originalMethod?.DeclaringType?.FullName}.{__originalMethod?.Name} ({codes.Count} insns, prebuilt={hasPrebuilt}, accessDict={accessDict?.Count ?? 0})");
        for (int i = 0; i < codes.Count; i++)
        {
            var inst = codes[i];
            if (IsPrefix(inst.opcode))
            {
                int peekIdx = i + 1;
                while (peekIdx < codes.Count && IsPrefix(codes[peekIdx].opcode))
                    peekIdx++;
                if (peekIdx < codes.Count && IsMonitoredInstruction(codes[peekIdx]))
                {
                    continue;
                }
            }
            bool isPatchTarget = false;
            MemberInfo member = null;
            if (hasPrebuilt && accessDict.TryGetValue(i, out var cachedMember))
            {
                if (inst.operand is MemberInfo current &&
                    (current == cachedMember ||
                     GetActualTarget(current) == GetActualTarget(cachedMember) ||
                     (current.MetadataToken == cachedMember.MetadataToken && current.Module == cachedMember.Module)))
                {
                    isPatchTarget = true;
                    member = cachedMember;
                }
                else
                {
                    if (inst.operand is MemberInfo fallback)
                    {
                        member = fallback;
                        isPatchTarget = TryGetMemberId(GetActualTarget(fallback), out _);
                    }
                }
            }
            else if (inst.operand is MemberInfo currentOperand)
            {
                member = currentOperand;
                isPatchTarget = TryGetMemberId(GetActualTarget(currentOperand), out _);
            }
            if (isPatchTarget)
            {
                MemberInfo actualTarget = GetActualTarget(member);
                if (actualTarget is FieldInfo fi && (inst.opcode == OpCodes.Ldflda || inst.opcode == OpCodes.Ldsflda))
                {
                    newCodes.Add(inst);
                    continue;
                }
                if (!TryGetMemberId(actualTarget, out int memberId))
                {
                    RuntimeDebug.LogWarning($"[MemoryAnalyzer] Hook skipped: {__originalMethod?.DeclaringType?.Name}.{__originalMethod?.Name} @ IL_{i:D4} — actualTarget '{actualTarget.DeclaringType?.Name}.{actualTarget.Name}' not found in _memberToId (Mono ref mismatch?)");
                    newCodes.Add(inst);
                    continue;
                }
                bool isWrite = false, isStatic = false;
                Type valType = null, declType = actualTarget.DeclaringType;
                if (actualTarget is FieldInfo fi2)
                {
                    isWrite = (inst.opcode == OpCodes.Stfld || inst.opcode == OpCodes.Stsfld);
                    valType = fi2.FieldType;
                    isStatic = fi2.IsStatic;
                }
                else if (actualTarget is PropertyInfo pi)
                {
                    isWrite = ((MethodInfo)member).ReturnType == typeof(void);
                    valType = pi.PropertyType;
                    isStatic = ((MethodInfo)member).IsStatic;
                }
                var prefixes = new List<CodeInstruction>();
                int prefixIdx = i - 1;
                while (prefixIdx >= 0 && (codes[prefixIdx].opcode == OpCodes.Volatile || codes[prefixIdx].opcode == OpCodes.Unaligned))
                {
                    prefixes.Insert(0, codes[prefixIdx]);
                    prefixIdx--;
                }
                var hook = new List<CodeInstruction>();
                var valLoc = il.DeclareLocal(valType);
                var objValLoc = il.DeclareLocal(typeof(object));
                LocalBuilder instLoc = null;
                if (!isStatic && !declType.IsValueType)
                    instLoc = il.DeclareLocal(declType);
                if (isWrite)
                {
                    var cancelLoc = il.DeclareLocal(typeof(bool));
                    var skipLabel = il.DefineLabel();
                    if (!isStatic && declType.IsValueType)
                    {
                        hook.Add(new CodeInstruction(OpCodes.Stloc, valLoc));
                        hook.Add(new CodeInstruction(OpCodes.Dup));
                        hook.Add(new CodeInstruction(OpCodes.Ldloc, valLoc));
                        if (valType.IsValueType || valType.IsGenericParameter)
                            hook.Add(new CodeInstruction(OpCodes.Box, valType));
                        hook.Add(new CodeInstruction(OpCodes.Ldc_I4, memberId));
                        hook.Add(new CodeInstruction(OpCodes.Ldloca, cancelLoc));
                        var interceptMethod = AccessTools.Method(typeof(MemoryAccessAnalyzer), nameof(InterceptValueTypeWrite)).MakeGenericMethod(declType);
                        hook.Add(new CodeInstruction(OpCodes.Call, interceptMethod));
                        hook.Add(new CodeInstruction(OpCodes.Unbox_Any, valType));
                        hook.Add(new CodeInstruction(OpCodes.Stloc, valLoc));
                        hook.Add(new CodeInstruction(OpCodes.Ldloc, cancelLoc));
                        var execLabel = il.DefineLabel();
                        hook.Add(new CodeInstruction(OpCodes.Brfalse, execLabel));
                        hook.Add(new CodeInstruction(OpCodes.Pop));
                        hook.Add(new CodeInstruction(OpCodes.Br, skipLabel));
                        var execTarget = new CodeInstruction(OpCodes.Ldloc, valLoc);
                        execTarget.labels.Add(execLabel);
                        hook.Add(execTarget);
                    }
                    else
                    {
                        hook.Add(new CodeInstruction(OpCodes.Stloc, valLoc));
                        if (!isStatic) hook.Add(new CodeInstruction(OpCodes.Stloc, instLoc));
                        hook.Add(new CodeInstruction(OpCodes.Ldloc, valLoc));
                        if (valType.IsValueType || valType.IsGenericParameter)
                            hook.Add(new CodeInstruction(OpCodes.Box, valType));
                        hook.Add(new CodeInstruction(OpCodes.Stloc, objValLoc));
                        hook.Add(new CodeInstruction(OpCodes.Ldc_I4, memberId));
                        if (isStatic)
                        {
                            hook.Add(new CodeInstruction(OpCodes.Ldloca, objValLoc));
                            hook.Add(new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(MemoryAccessAnalyzer), nameof(RouterWriteStatic))));
                        }
                        else
                        {
                            hook.Add(new CodeInstruction(OpCodes.Ldloca, instLoc));
                            hook.Add(new CodeInstruction(OpCodes.Ldloca, objValLoc));
                            hook.Add(new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(MemoryAccessAnalyzer), nameof(RouterWriteInst)).MakeGenericMethod(declType)));
                        }
                        hook.Add(new CodeInstruction(OpCodes.Stloc, cancelLoc));
                        hook.Add(new CodeInstruction(OpCodes.Ldloc, objValLoc));
                        hook.Add(new CodeInstruction(OpCodes.Unbox_Any, valType));
                        hook.Add(new CodeInstruction(OpCodes.Stloc, valLoc));
                        hook.Add(new CodeInstruction(OpCodes.Ldloc, cancelLoc));
                        hook.Add(new CodeInstruction(OpCodes.Brtrue, skipLabel));
                        if (!isStatic) hook.Add(new CodeInstruction(OpCodes.Ldloc, instLoc));
                        hook.Add(new CodeInstruction(OpCodes.Ldloc, valLoc));
                    }
                    foreach (var prefix in prefixes)
                    {
                        var clonedPrefix = prefix.Clone();
                        clonedPrefix.labels.Clear();
                        hook.Add(clonedPrefix);
                    }
                    var clonedInst = inst.Clone(); clonedInst.labels.Clear(); hook.Add(clonedInst);
                    var nop = new CodeInstruction(OpCodes.Nop); nop.labels.Add(skipLabel); hook.Add(nop);
                }
                else
                {
                    if (!isStatic && declType.IsValueType)
                    {
                        hook.Add(new CodeInstruction(OpCodes.Dup));
                        foreach (var prefix in prefixes)
                        {
                            var clonedPrefix = prefix.Clone(); clonedPrefix.labels.Clear(); hook.Add(clonedPrefix);
                        }
                        var clonedInst = inst.Clone(); clonedInst.labels.Clear(); hook.Add(clonedInst);
                        if (valType.IsValueType || valType.IsGenericParameter)
                            hook.Add(new CodeInstruction(OpCodes.Box, valType));
                        hook.Add(new CodeInstruction(OpCodes.Ldc_I4, memberId));
                        var interceptReadMethod = AccessTools.Method(typeof(MemoryAccessAnalyzer), nameof(InterceptValueTypeRead)).MakeGenericMethod(declType);
                        hook.Add(new CodeInstruction(OpCodes.Call, interceptReadMethod));
                        hook.Add(new CodeInstruction(OpCodes.Unbox_Any, valType));
                    }
                    else
                    {
                        if (!isStatic)
                        {
                            hook.Add(new CodeInstruction(OpCodes.Dup));
                            hook.Add(new CodeInstruction(OpCodes.Stloc, instLoc));
                        }
                        foreach (var prefix in prefixes)
                        {
                            var clonedPrefix = prefix.Clone(); clonedPrefix.labels.Clear(); hook.Add(clonedPrefix);
                        }
                        var clonedInst = inst.Clone(); clonedInst.labels.Clear(); hook.Add(clonedInst);
                        hook.Add(new CodeInstruction(OpCodes.Stloc, valLoc));
                        hook.Add(new CodeInstruction(OpCodes.Ldloc, valLoc));
                        if (valType.IsValueType || valType.IsGenericParameter)
                            hook.Add(new CodeInstruction(OpCodes.Box, valType));
                        hook.Add(new CodeInstruction(OpCodes.Stloc, objValLoc));
                        hook.Add(new CodeInstruction(OpCodes.Ldc_I4, memberId));
                        if (isStatic)
                        {
                            hook.Add(new CodeInstruction(OpCodes.Ldloca, objValLoc));
                            hook.Add(new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(MemoryAccessAnalyzer), nameof(RouterReadStatic))));
                        }
                        else
                        {
                            hook.Add(new CodeInstruction(OpCodes.Ldloca, instLoc));
                            hook.Add(new CodeInstruction(OpCodes.Ldloca, objValLoc));
                            hook.Add(new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(MemoryAccessAnalyzer), nameof(RouterReadInst)).MakeGenericMethod(declType)));
                        }
                        hook.Add(new CodeInstruction(OpCodes.Ldloc, objValLoc));
                        hook.Add(new CodeInstruction(OpCodes.Unbox_Any, valType));
                    }
                }
                var originalTargetInst = prefixes.Count > 0 ? prefixes[0] : inst;
                if (hook.Count > 0)
                {
                    hook[0].labels = originalTargetInst.labels.ToList();
                }
                hookCount++;
                RuntimeDebug.LogDebug($"[MemoryAnalyzer] Hook: {__originalMethod?.DeclaringType?.Name}.{__originalMethod?.Name} @ IL_{i:D4} — {(isWrite ? "WRITE" : "READ")} {actualTarget.DeclaringType?.Name}.{actualTarget.Name}");
                newCodes.AddRange(hook);
                continue;
            }
            newCodes.Add(inst);
        }
        if (hookCount > 0)
            RuntimeDebug.LogInfo($"[MemoryAnalyzer] Transpiler done: {__originalMethod?.DeclaringType?.FullName}.{__originalMethod?.Name} — {hookCount} hook(s) emitted");
        return newCodes;
    }

    private void ScanInMemoryAssemblyBatch(Assembly ass, bool hasFields, bool hasProps, HashSet<MemberInfo> pendingTargetsSet, ConcurrentBag<MethodBase> methodsToPatch)
    {
        var types = _assemblyTypesCache.GetOrAdd(ass, a => GetLoadableTypes(a).Where(t => !t.IsInterface && !t.IsEnum).ToArray());
        Parallel.ForEach(types, type =>
        {
            ScanTypeBatch(type, hasFields, hasProps, pendingTargetsSet, methodsToPatch);
        });
    }

    /// <summary>
    /// 对单个类型的全部方法进行扫描，匹配挂起的监控目标。
    /// 由 ScanInMemoryAssemblyBatch（按程序集批量）和 ApplyPendingCorepublic（按类型精细扫描）共享。
    /// </summary>
    private void ScanTypeBatch(Type type, bool hasFields, bool hasProps, HashSet<MemberInfo> pendingTargetsSet, ConcurrentBag<MethodBase> methodsToPatch)
    {
        if (type.IsInterface || type.IsEnum) return;
        var methods = _typeMethodsCache.GetOrAdd(type, t =>
            AccessTools.GetDeclaredMethods(t).Where(m => !m.IsAbstract).ToArray());
        for (int j = 0; j < methods.Length; j++)
        {
            var method = methods[j];
            var info = _methodAnalysisCache.GetOrAdd(method, m => AnalyzeMethodFeatures(m));
            if (info.IsEmpty) continue;
            if ((hasFields && info.HasFieldOps) || (hasProps && info.HasCallOps))
            {
                if (info.ParsedInstructions == null)
                {
                    lock (info)
                    {
                        if (info.ParsedInstructions == null)
                        {
                            try { info.ParsedInstructions = PatchProcessor.GetOriginalInstructions(method).ToList(); }
                            catch { info.IsEmpty = true; continue; }
                        }
                    }
                }
                var insts = info.ParsedInstructions;
                Dictionary<int, MemberInfo> accessDict = null;
                for (int k = 0; k < insts.Count; k++)
                {
                    if (insts[k].operand is MemberInfo opMember)
                    {
                        MemberInfo matched = TryMatchPendingTarget(opMember, pendingTargetsSet);
                        if (matched != null)
                        {
                            if (accessDict == null) accessDict = new Dictionary<int, MemberInfo>();
                            accessDict[k] = matched;
                        }
                    }
                }
                if (accessDict != null)
                {
                    _prebuiltAccessPoints.TryAdd(method, accessDict);
                    methodsToPatch.Add(method);
                    RuntimeDebug.LogDebug($"[MemoryAnalyzer] Matched: {method.DeclaringType?.FullName}.{method.Name} — {accessDict.Count} target(s) @ offsets [{string.Join(",", accessDict.Keys.Select(k => k.ToString()).ToArray())}]");
                }
                else if (info.HasFieldOps || info.HasCallOps)
                {
                    // 方法有 field/call 操作但没匹配到具体目标 → 可能是间接访问模式
                    RuntimeDebug.LogDebug($"[MemoryAnalyzer] Scanned but no match: {method.DeclaringType?.FullName}.{method.Name} ({insts.Count} insns, hasField={info.HasFieldOps}, hasCall={info.HasCallOps})");
                }
            }
        }
        // 递归扫描嵌套类型（编译器生成的 IEnumerator 状态机、闭包等）
        var nestedTypes = type.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic);
        if (nestedTypes.Length > 0)
        {
            RuntimeDebug.LogDebug($"[MemoryAnalyzer] Drilling into {nestedTypes.Length} nested type(s) of {type.FullName}: [{string.Join(", ", nestedTypes.Select(t => t.Name).ToArray())}]");
            foreach (var nested in nestedTypes)
            {
                ScanTypeBatch(nested, hasFields, hasProps, pendingTargetsSet, methodsToPatch);
            }
        }
    }

    /// <summary>
    /// 在 pendingTargetsSet 中查找匹配的 MemberInfo，先用引用相等，失败后用 MetadataToken+Module 兜底。
    /// 解决 Mono 下同一方法/字段通过不同反射路径获取的 MethodInfo 引用不一致问题。
    /// </summary>
    private static MemberInfo TryMatchPendingTarget(MemberInfo operand, HashSet<MemberInfo> pendingTargetsSet)
    {
        if (pendingTargetsSet.Contains(operand))
            return operand;
        // Mono 引用不一致兜底：比较 MetadataToken + Module
        int token = operand.MetadataToken;
        Module module = operand.Module;
        foreach (var candidate in pendingTargetsSet)
        {
            if (candidate.MetadataToken == token && candidate.Module == module)
            {
                RuntimeDebug.LogDebug($"[MemoryAnalyzer] Token-fallback match: {operand.Name} (0x{token:X8}) → {candidate.DeclaringType?.Name}.{candidate.Name}");
                return candidate;
            }
        }
        // 再尝试通过 GetActualTarget 做间接匹配（属性 getter/setter → PropertyInfo）
        var actual = GetActualTarget(operand);
        if (!ReferenceEquals(actual, operand) && pendingTargetsSet.Contains(actual))
        {
            RuntimeDebug.LogDebug($"[MemoryAnalyzer] ActualTarget match: {operand.DeclaringType?.Name}.{operand.Name} → {actual.DeclaringType?.Name}.{actual.Name}");
            return actual;
        }
        if (!ReferenceEquals(actual, operand))
        {
            int actualToken = actual.MetadataToken;
            Module actualModule = actual.Module;
            foreach (var candidate in pendingTargetsSet)
            {
                if (candidate.MetadataToken == actualToken && candidate.Module == actualModule)
                {
                    RuntimeDebug.LogDebug($"[MemoryAnalyzer] ActualTarget-token match: {actual.Name} (0x{actualToken:X8}) → {candidate.DeclaringType?.Name}.{candidate.Name}");
                    return candidate;
                }
            }
        }
        return null;
    }

    /// <summary>
    /// MetadataToken 感知的 _memberToId 查找，解决 Mono 下 MethodInfo 引用不一致问题。
    /// </summary>
    private static bool TryGetMemberId(MemberInfo member, out int id)
    {
        if (_memberToId.TryGetValue(member, out id))
            return true;
        // Mono 兜底：遍历字典找 MetadataToken + Module 匹配
        int token = member.MetadataToken;
        Module module = member.Module;
        foreach (var kv in _memberToId)
        {
            if (kv.Key.MetadataToken == token && kv.Key.Module == module)
            {
                id = kv.Value;
                RuntimeDebug.LogDebug($"[MemoryAnalyzer] TryGetMemberId token-fallback: {member.DeclaringType?.Name}.{member.Name} (0x{token:X8}) → id={id}");
                return true;
            }
        }
        return false;
    }

    private static MethodAnalysisInfo AnalyzeMethodFeatures(MethodInfo method)
    {
        try
        {
            var body = method.GetMethodBody();
            var bytes = body?.GetILAsByteArray();
            if (bytes == null || bytes.Length == 0) return new MethodAnalysisInfo { IsEmpty = true };
            bool hasF = false, hasC = false;
            for (int i = 0; i < bytes.Length; i++)
            {
                byte b = bytes[i];
                // 字段直访: ldfld(0x7B) ~ stsfld(0x80)
                if (!hasF && b >= 0x7B && b <= 0x80) hasF = true;
                // 间接访存（可能通过 ldflda 取地址后写入）:
                // ldind_i(0x46)~ldind_r8(0x4F), ldind_ref(0xDF) + stind_i(0x51)~stind_r8(0x56), stind_ref(0xDF)
                if (!hasF && ((b >= 0x46 && b <= 0x4F) || (b >= 0x51 && b <= 0x56) || b == 0xDF)) hasF = true;
                // ldobj(0x71) / stobj(0x81) via managed pointer
                if (!hasF && (b == 0x71 || b == 0x81)) hasF = true;
                // ldelema(0x8F) - 数组元素取地址
                if (!hasF && b == 0x8F) hasF = true;
                // call(0x28) / callvirt(0x6F) / calli(0x29) for property accessors
                if (!hasC && (b == 0x28 || b == 0x6F || b == 0x29)) hasC = true;
                if (hasF && hasC) break;
            }
            return new MethodAnalysisInfo { HasFieldOps = hasF, HasCallOps = hasC };
        }
        catch
        {
            return new MethodAnalysisInfo { IsEmpty = true };
        }
    }

    private static IEnumerable<Type> GetLoadableTypes(Assembly assembly)
    {
        try { return assembly.GetTypes(); }
        catch (ReflectionTypeLoadException e) { return e.Types.Where(t => t != null); }
    }
}

public static class ObjectTracker
{
    private enum OriginPriority { Inheritance = 1, Low = 2, High = 3 }

    private class OriginRecord
    {
        public Assembly Origin;
        public Type OriginType;
        public long Tick;
        public OriginPriority Priority;
        public bool SpawnResolved;
    }

    private static ConditionalWeakTable<UnityEngine.Object, OriginRecord> OriginMap = new ConditionalWeakTable<UnityEngine.Object, OriginRecord>();
    private static long GlobalTick;
    public static bool AllowItSelf = true;
    private static Harmony _trackerHarmony;
    private static Assembly _trackerAssembly;
    private static ConcurrentDictionary<Assembly, bool> _assembliesThatLoaded = new ConcurrentDictionary<Assembly, bool>();

    private struct ModSourceInfo
    {
        public Assembly RegistrantAssembly;
        public Assembly LogicAssembly;
        public Type LogicType;
        public ModMetaData Meta;
    }

    private static ConditionalWeakTable<object, Assembly> _registrantByModification = new ConditionalWeakTable<object, Assembly>();
    private static Dictionary<SpawnableAsset, ModSourceInfo> _sourceInfoBySpawnable = new Dictionary<SpawnableAsset, ModSourceInfo>();
    private static Dictionary<Assembly, ModMetaData> _metaByAssembly = new Dictionary<Assembly, ModMetaData>();
    private static ConditionalWeakTable<UnityEngine.Object, SpawnableAsset> _spawnableByObject = new ConditionalWeakTable<UnityEngine.Object, SpawnableAsset>();
    private static FieldInfo _afterSpawnByAssetField;
    private static FieldInfo _beforeSpawnByAssetField;

    public static void Init()
    {
        if (_trackerHarmony != null) return;

        RuntimeDebug.LogDebug("[ObjectTracker] Initializing...");
        Stopwatch sw = Stopwatch.StartNew();
        _trackerHarmony = new Harmony("com.tracker.core");
        _trackerAssembly = typeof(ObjectTracker).Assembly;

        int hookCount = 0;

        try
        {
            var cloneMethod = typeof(UnityEngine.Object).GetMethod("public_CloneSingle", BindingFlags.NonPublic | BindingFlags.Static);
            if (cloneMethod != null)
            {
                _trackerHarmony.Patch(cloneMethod, postfix: new HarmonyMethod(typeof(ObjectTracker), nameof(CaptureClonePostfix)));
                hookCount++;
            }
        }
        catch { RuntimeDebug.LogWarning("[ObjectTracker] public_CloneSingle patch failed (native method), skipped."); }

        var instantiateMethods = typeof(UnityEngine.Object).GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(m => m.Name == "Instantiate");
        foreach (var m in instantiateMethods)
        {
            try
            {
                _trackerHarmony.Patch(m, postfix: new HarmonyMethod(typeof(ObjectTracker), nameof(CaptureInstantiatePostfix)));
                hookCount++;
            }
            catch { }
        }

        var createInstanceMethods = typeof(ScriptableObject).GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(m => m.Name == "CreateInstance");
        foreach (var m in createInstanceMethods)
        {
            try
            {
                _trackerHarmony.Patch(m, postfix: new HarmonyMethod(typeof(ObjectTracker), nameof(CaptureCreateInstancePostfix)));
                hookCount++;
            }
            catch { }
        }

        var loadMethods = typeof(Resources).GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(m => m.Name == "Load" && !m.IsGenericMethod);
        foreach (var m in loadMethods)
        {
            try
            {
                _trackerHarmony.Patch(m, postfix: new HarmonyMethod(typeof(ObjectTracker), nameof(CaptureResourcesLoadPostfix)));
                hookCount++;
            }
            catch { }
        }

        try
        {
            var createMethod = typeof(GameObject).GetMethod("public_CreateGameObject", BindingFlags.NonPublic | BindingFlags.Static);
            if (createMethod != null)
            {
                _trackerHarmony.Patch(createMethod, postfix: new HarmonyMethod(typeof(ObjectTracker), nameof(CaptureCreatePostfix)));
                hookCount++;
            }
        }
        catch { RuntimeDebug.LogWarning("[ObjectTracker] public_CreateGameObject patch failed (native method), skipped."); }

        var goCtors = typeof(GameObject).GetConstructors(BindingFlags.Public | BindingFlags.Instance);
        foreach (var ctor in goCtors)
        {
            try
            {
                _trackerHarmony.Patch(ctor, postfix: new HarmonyMethod(typeof(ObjectTracker), nameof(CaptureCtorPostfix)));
                hookCount++;
            }
            catch { }
        }

        try
        {
            var addComp = typeof(GameObject).GetMethod("AddComponent", BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(Type) }, null);
            if (addComp != null) { _trackerHarmony.Patch(addComp, postfix: new HarmonyMethod(typeof(ObjectTracker), nameof(CaptureAddComponentPostfix))); hookCount++; }
        }
        catch { RuntimeDebug.LogWarning("[ObjectTracker] AddComponent patch failed, skipped."); }

        try
        {
            var setParent = typeof(Transform).GetMethod("SetParent", new[] { typeof(Transform), typeof(bool) });
            if (setParent != null) { _trackerHarmony.Patch(setParent, postfix: new HarmonyMethod(typeof(ObjectTracker), nameof(CaptureSetParentPostfix))); hookCount++; }
        }
        catch { RuntimeDebug.LogWarning("[ObjectTracker] SetParent patch failed, skipped."); }

        try
        {
            var performBeforeSpawn = typeof(CatalogBehaviour).GetMethod("PerformBeforeSpawn", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(SpawnableAsset), typeof(GameObject) }, null);
            if (performBeforeSpawn != null) { _trackerHarmony.Patch(performBeforeSpawn, prefix: new HarmonyMethod(typeof(ObjectTracker), nameof(OverridePerformBeforeSpawnPrefix))); hookCount++; }
        }
        catch { RuntimeDebug.LogWarning("[ObjectTracker] PerformBeforeSpawn patch failed, skipped."); }

        try
        {
            var modAPIRegister = typeof(CatalogBehaviour).Assembly.GetType("ModAPI")?.GetMethod("Register", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(CatalogBehaviour).Assembly.GetType("Modification") }, null);
            if (modAPIRegister != null) { _trackerHarmony.Patch(modAPIRegister, prefix: new HarmonyMethod(typeof(ObjectTracker), nameof(CaptureModAPIRegisterPrefix))); hookCount++; }
        }
        catch { RuntimeDebug.LogWarning("[ObjectTracker] ModAPI.Register patch failed, skipped."); }

        try
        {
            var applyModification = typeof(CatalogBehaviour).GetMethod("ApplyModification", BindingFlags.NonPublic | BindingFlags.Instance);
            if (applyModification != null) { _trackerHarmony.Patch(applyModification, postfix: new HarmonyMethod(typeof(ObjectTracker), nameof(CaptureApplyModificationPostfix))); hookCount++; }
        }
        catch { RuntimeDebug.LogWarning("[ObjectTracker] ApplyModification patch failed, skipped."); }

        try
        {
            var populateMethod = typeof(CatalogBehaviour).GetMethod("Populate", BindingFlags.NonPublic | BindingFlags.Instance);
            if (populateMethod != null) { _trackerHarmony.Patch(populateMethod, postfix: new HarmonyMethod(typeof(ObjectTracker), nameof(CapturePopulatePostfix))); hookCount++; }
        }
        catch { RuntimeDebug.LogWarning("[ObjectTracker] Populate patch failed, skipped."); }

        var assemblyLoadMethods = typeof(Assembly).GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(m => m.Name.StartsWith("Load"));
        foreach (var m in assemblyLoadMethods)
        {
            try
            {
                _trackerHarmony.Patch(m, postfix: new HarmonyMethod(typeof(ObjectTracker), nameof(CaptureAssemblyLoadPostfix)));
                hookCount++;
            }
            catch { }
        }

        sw.Stop();
        RuntimeDebug.LogInfo($"[ObjectTracker] Initialized: {hookCount} hooks in {sw.ElapsedMilliseconds}ms");
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void CaptureClonePostfix(UnityEngine.Object __result, UnityEngine.Object __0)
    {
        RecordOrigin(__result, checkStack: true, OriginPriority.Low);
        PropagateSpawnable(__0, __result);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void CaptureInstantiatePostfix(UnityEngine.Object __result, UnityEngine.Object __0)
    {
        RecordOrigin(__result, checkStack: true, OriginPriority.Low);
        PropagateSpawnable(__0, __result);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void CaptureCreateInstancePostfix(ScriptableObject __result) => RecordOrigin(__result, checkStack: true, OriginPriority.Low);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void CaptureResourcesLoadPostfix(UnityEngine.Object __result) => RecordOrigin(__result, checkStack: true, OriginPriority.Low);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void CaptureCtorPostfix(GameObject __instance) => RecordOrigin(__instance, checkStack: true, OriginPriority.Low);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void OverridePerformBeforeSpawnPrefix(SpawnableAsset asset, GameObject instance)
    {
        if (asset == null || instance == null) return;

        OriginMap.GetOrCreateValue(instance).SpawnResolved = true;
        _spawnableByObject.Remove(instance);
        _spawnableByObject.Add(instance, asset);

        ModSourceInfo sourceInfo;
        if (!_sourceInfoBySpawnable.TryGetValue(asset, out sourceInfo))
            return;

        Assembly resolvedAssembly = ResolveBestAssembly(sourceInfo);
        Type resolvedType = ResolveBestType(sourceInfo);
        if (resolvedAssembly == null) return;

        RecordOriginWithAssembly(instance, resolvedAssembly, resolvedType, OriginPriority.High);
    }

    private static Assembly ResolveBestAssembly(ModSourceInfo info)
    {
        if (info.LogicAssembly != null && !ModInterceptor.IsSystemOrGameAssembly(info.LogicAssembly))
            return info.LogicAssembly;
        if (info.RegistrantAssembly != null && !ModInterceptor.IsSystemOrGameAssembly(info.RegistrantAssembly))
            return info.RegistrantAssembly;
        return null;
    }

    private static Type ResolveBestType(ModSourceInfo info)
    {
        if (info.LogicType != null && !ModInterceptor.IsSystemOrGameAssembly(info.LogicType.Assembly))
            return info.LogicType;
        return null;
    }

    private static void CaptureModAPIRegisterPrefix(object modification)
    {
        if (modification == null) return;

        var st = new System.Diagnostics.StackTrace(1, false);
        int depth = Math.Min(st.FrameCount, 32);
        for (int i = 0; i < depth; i++)
        {
            var asm = st.GetFrame(i)?.GetMethod()?.DeclaringType?.Assembly;
            if (asm == null) continue;
            if (asm == _trackerAssembly) continue;
            if (asm == typeof(Harmony).Assembly) continue;
            if (ModInterceptor.IsSystemOrGameAssembly(asm)) continue;

            _registrantByModification.Add(modification, asm);
            return;
        }
    }

    private static void CaptureApplyModificationPostfix(object mod, object meta, SpawnableAsset spawnable)
    {
        if (mod == null || spawnable == null) return;

        _registrantByModification.TryGetValue(mod, out var registrantAsm);

        Assembly logicAsm = null;
        var modType = mod.GetType();
        var afterSpawnProp = modType.GetProperty("AfterSpawn");
        var beforeSpawnProp = modType.GetProperty("BeforeSpawn");

        Type logicType = null;
        if (afterSpawnProp != null)
        {
            var afterSpawn = afterSpawnProp.GetValue(mod) as Delegate;
            if (afterSpawn != null)
            {
                logicAsm = afterSpawn.Method.DeclaringType?.Assembly;
                logicType = afterSpawn.Method.DeclaringType;
            }
        }
        if (logicAsm == null && beforeSpawnProp != null)
        {
            var beforeSpawn = beforeSpawnProp.GetValue(mod) as Delegate;
            if (beforeSpawn != null)
            {
                logicAsm = beforeSpawn.Method.DeclaringType?.Assembly;
                logicType = beforeSpawn.Method.DeclaringType;
            }
        }

        if (logicAsm != null && ModInterceptor.IsSystemOrGameAssembly(logicAsm))
        {
            logicAsm = null;
            logicType = null;
        }

        var metaData = meta as ModMetaData;
        var info = new ModSourceInfo
        {
            RegistrantAssembly = registrantAsm,
            LogicAssembly = logicAsm,
            LogicType = logicType,
            Meta = metaData
        };

        _sourceInfoBySpawnable[spawnable] = info;

        if (logicAsm != null && metaData != null)
            _metaByAssembly[logicAsm] = metaData;
        if (registrantAsm != null && metaData != null && !_metaByAssembly.ContainsKey(registrantAsm))
            _metaByAssembly[registrantAsm] = metaData;
    }

    private static void CapturePopulatePostfix()
    {
        _sourceInfoBySpawnable.Clear();
        _metaByAssembly.Clear();

        var main = CatalogBehaviour.Main;
        if (main == null) return;

        if (_afterSpawnByAssetField == null)
        {
            _afterSpawnByAssetField = typeof(CatalogBehaviour).GetField("afterSpawnByAsset",
                BindingFlags.NonPublic | BindingFlags.Instance);
            _beforeSpawnByAssetField = typeof(CatalogBehaviour).GetField("beforeSpawnByAsset",
                BindingFlags.NonPublic | BindingFlags.Instance);
        }

        var afterSpawnByAsset = _afterSpawnByAssetField?.GetValue(main) as IDictionary;
        var beforeSpawnByAsset = _beforeSpawnByAssetField?.GetValue(main) as IDictionary;

        ScanDelegateDictionary(afterSpawnByAsset);
        ScanDelegateDictionary(beforeSpawnByAsset);
        BuildMetaReverseLookup();
    }

    private static void ScanDelegateDictionary(IDictionary dict)
    {
        if (dict == null) return;

        foreach (DictionaryEntry entry in dict)
        {
            var spawnable = entry.Key as SpawnableAsset;
            var del = entry.Value as Delegate;
            if (spawnable == null || del == null) continue;

            if (_sourceInfoBySpawnable.ContainsKey(spawnable))
                continue;

            var logicType = del.Method.DeclaringType;
            var logicAsm = logicType?.Assembly;
            if (logicAsm == null || ModInterceptor.IsSystemOrGameAssembly(logicAsm))
                continue;

            _sourceInfoBySpawnable[spawnable] = new ModSourceInfo
            {
                LogicAssembly = logicAsm,
                LogicType = logicType
            };
        }
    }

    private static void BuildMetaReverseLookup()
    {
        var modScriptsField = typeof(CatalogBehaviour).Assembly
            .GetType("ModLoader")?
            .GetField("ModScripts", BindingFlags.Public | BindingFlags.Static);

        if (modScriptsField == null) return;

        var modScripts = modScriptsField.GetValue(null) as IDictionary;
        if (modScripts == null) return;

        foreach (DictionaryEntry entry in modScripts)
        {
            var meta = entry.Key as ModMetaData;
            if (meta == null) continue;

            var scriptsType = entry.Value?.GetType();
            var loadedAsm = scriptsType?.GetProperty("LoadedAssembly",
                BindingFlags.Public | BindingFlags.Instance)?.GetValue(entry.Value) as Assembly;

            if (loadedAsm != null && !_metaByAssembly.ContainsKey(loadedAsm))
                _metaByAssembly[loadedAsm] = meta;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void CaptureCreatePostfix(GameObject __0)
    {
        if (__0 != null)
            RecordOrigin(__0, checkStack: true, OriginPriority.Low);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void CaptureAddComponentPostfix(Component __result)
    {
        var go = __result?.gameObject;
        if (go == null) return;

        if (OriginMap.TryGetValue(go, out var rec) && rec.SpawnResolved && rec.Origin == null)
            return;

        RecordOrigin(go, checkStack: true, OriginPriority.Low);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void CaptureSetParentPostfix(Transform __instance, Transform parent)
    {
        if (__instance != null && parent != null)
            HandleInheritance(__instance.gameObject, parent.gameObject);
    }

    private static void PropagateSpawnable(UnityEngine.Object source, UnityEngine.Object target)
    {
        if (source == null || target == null) return;
        if (_spawnableByObject.TryGetValue(source, out var asset))
        {
            _spawnableByObject.Remove(target);
            _spawnableByObject.Add(target, asset);
        }
    }

    private static void HandleInheritance(GameObject child, GameObject parent)
    {
        var childRec = OriginMap.GetOrCreateValue(child);
        if (childRec.Origin != null && childRec.Priority > OriginPriority.Inheritance) return;

        if (OriginMap.TryGetValue(parent, out var parentRec) && parentRec.Origin != null)
        {
            childRec.Origin = parentRec.Origin;
            childRec.OriginType = parentRec.OriginType;
            childRec.Priority = OriginPriority.Inheritance;
            childRec.Tick = Interlocked.Increment(ref GlobalTick);
        }

        PropagateSpawnable(parent, child);
    }

    private static void RecordOrigin(UnityEngine.Object obj, bool checkStack, OriginPriority priority)
    {
        if (obj == null) return;

        var rec = OriginMap.GetOrCreateValue(obj);

        if (rec.Origin != null && priority < rec.Priority) return;

        Assembly originAsm = null;
        Type originType = null;

        if (checkStack)
            ResolveOriginFromStack(out originAsm, out originType);

        if (originAsm == null) return;

        if (rec.Origin != null && priority == rec.Priority)
        {
            if (IsGuidAssemblyName(originAsm) || !HasLoadedAssemblies(rec.Origin)) return;
        }

        rec.Origin = originAsm;
        rec.OriginType = originType;
        rec.Priority = priority;
        rec.Tick = Interlocked.Increment(ref GlobalTick);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void RecordOriginWithAssembly(UnityEngine.Object obj, Assembly origin, Type originType = null, OriginPriority priority = OriginPriority.High)
    {
        if (obj == null || origin == null) return;
        var rec = OriginMap.GetOrCreateValue(obj);
        if (rec.Origin != null && priority < rec.Priority) return;
        if (rec.Origin != null && priority == rec.Priority)
        {
            if (IsGuidAssemblyName(origin) || !HasLoadedAssemblies(rec.Origin)) return;
        }
        rec.Origin = origin;
        rec.OriginType = originType;
        rec.Priority = priority;
        rec.Tick = Interlocked.Increment(ref GlobalTick);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsGuidAssemblyName(Assembly asm)
    {
        return Guid.TryParse(asm.GetName().Name, out _);
    }

    private static void CaptureAssemblyLoadPostfix()
    {
        var st = new System.Diagnostics.StackTrace(2, false);
        int depth = Math.Min(st.FrameCount, 32);
        for (int i = 0; i < depth; i++)
        {
            var frame = st.GetFrame(i);
            if (frame == null) continue;
            var asm = frame.GetMethod()?.DeclaringType?.Assembly;
            if (asm == null) continue;
            if (asm == _trackerAssembly) continue;
            if (asm == typeof(Harmony).Assembly) continue;
            if (ModInterceptor.IsSystemOrGameAssembly(asm)) continue;
            _assembliesThatLoaded.TryAdd(asm, true);
            return;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool HasLoadedAssemblies(Assembly asm)
    {
        return asm != null && _assembliesThatLoaded.ContainsKey(asm);
    }

    private static bool IsGameAssembly(Assembly asm)
    {
        if (asm == null) return false;
        string name = asm.GetName().Name;
        return name == "Assembly-CSharp" || name == "Assembly-CSharp-firstpass" || name == "BouncyCastle.Crypto";
    }

    private static void ResolveOriginFromStack(out Assembly originAsm, out Type originType)
    {
        originAsm = null;
        originType = null;

        var st = new System.Diagnostics.StackTrace(false);
        int depth = Math.Min(st.FrameCount, 16);

        for (int i = 0; i < depth; i++)
        {
            var method = st.GetFrame(i).GetMethod();
            var declType = method?.DeclaringType;
            var asm = declType?.Assembly;

            if (asm == null) continue;
            if (asm == typeof(Harmony).Assembly) continue;

            if (ModInterceptor.IsSystemOrGameAssembly(asm))
            {
                if (IsGameAssembly(asm)) return;
                continue;
            }

            if (asm == _trackerAssembly)
            {
                if (!AllowItSelf || declType == typeof(ObjectTracker) || declType.DeclaringType == typeof(ObjectTracker))
                    continue;
                originAsm = asm;
                originType = declType;
                return;
            }

            originAsm = asm;
            originType = declType;
            return;
        }
    }

    public static Assembly GetOrigin(GameObject go)
    {
        if (go == null) return null;
        return OriginMap.TryGetValue(go, out var rec) ? rec.Origin : null;
    }

    public static Type GetOriginType(GameObject go)
    {
        if (go == null) return null;
        return OriginMap.TryGetValue(go, out var rec) ? rec.OriginType : null;
    }

    public static Assembly GetFinalOrigin(GameObject root)
    {
        if (root == null) return null;

        var rootOrigin = GetOrigin(root);
        if (rootOrigin != null) return rootOrigin;

        var weights = new Dictionary<Assembly, int>();

        void AddWeight(Assembly asm, int w)
        {
            if (asm == null) return;
            if (!weights.ContainsKey(asm)) weights[asm] = 0;
            weights[asm] += w;
        }

        try
        {
            AddWeight(GetOrigin(root), 3);
            var transforms = root.GetComponentsInChildren<Transform>(true);
            foreach (var t in transforms)
                if (t.gameObject != root)
                    AddWeight(GetOrigin(t.gameObject), 1);
        }
        catch { }

        return weights.OrderByDescending(kv => kv.Value).FirstOrDefault().Key;
    }

    public static Type GetFinalOriginType(GameObject root)
    {
        if (root == null) return null;

        var rootOriginType = GetOriginType(root);
        if (rootOriginType != null) return rootOriginType;

        var weights = new Dictionary<Type, int>();

        void AddWeight(Type t, int w)
        {
            if (t == null) return;
            if (!weights.ContainsKey(t)) weights[t] = 0;
            weights[t] += w;
        }

        try
        {
            AddWeight(GetOriginType(root), 3);
            var transforms = root.GetComponentsInChildren<Transform>(true);
            foreach (var t in transforms)
                if (t.gameObject != root)
                    AddWeight(GetOriginType(t.gameObject), 1);
        }
        catch { }

        return weights.OrderByDescending(kv => kv.Value).FirstOrDefault().Key;
    }

    public static SpawnableAsset GetSpawnableAsset(GameObject go)
    {
        if (go == null) return null;
        _spawnableByObject.TryGetValue(go, out var asset);
        return asset;
    }

    public static bool TryGetSpawnableAsset(GameObject go, out SpawnableAsset asset)
    {
        asset = null;
        if (go == null) return false;
        return _spawnableByObject.TryGetValue(go, out asset);
    }

    public static void Clear()
    {
        OriginMap = new ConditionalWeakTable<UnityEngine.Object, OriginRecord>();
        _spawnableByObject = new ConditionalWeakTable<UnityEngine.Object, SpawnableAsset>();
        Interlocked.Exchange(ref GlobalTick, 0);
    }
}
public static class ModInterceptor
{
    private const string HARMONY_ID = "com.interceptor.blocker";
    public static bool DebugMode = false;

    private static readonly HashSet<string> SystemPrefixes = new HashSet<string> { "System", "mscorlib", "netstandard", "UnityEngine", "Unity.", "Mono.", "Microsoft.", "Newtonsoft", "Harmony", "MonoMod" };
    private static readonly HashSet<string> GameAssemblies = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Assembly-CSharp", "Assembly-CSharp-firstpass", "BouncyCastle.Crypto" };

    private static readonly ConcurrentDictionary<string, byte> _processedAsmSimpleNames = new ConcurrentDictionary<string, byte>();
    private static readonly ConcurrentDictionary<string, byte> _processedTypeFullNames = new ConcurrentDictionary<string, byte>();

    public static readonly ConcurrentDictionary<Assembly, byte> BannedAssemblies = new ConcurrentDictionary<Assembly, byte>();
    public static readonly ConcurrentDictionary<Type, byte> BannedTypes = new ConcurrentDictionary<Type, byte>();
    public static readonly ConcurrentDictionary<object, byte> BannedInstances = new ConcurrentDictionary<object, byte>();

    private static readonly ConcurrentDictionary<MethodBase, byte> _patchedMethodCache = new ConcurrentDictionary<MethodBase, byte>();

    private static Harmony _harmony;
    private static readonly object _scanLock = new object();

    public static bool InitHarmony()
    {
        if (_harmony != null) return true;
        try
        {
            _harmony = new Harmony(HARMONY_ID);
            RuntimeDebug.LogInfo($"[ModInterceptor] Harmony initialized: '{HARMONY_ID}'");
            return true;
        }
        catch (Exception ex)
        {
            RuntimeDebug.LogError($"[ModInterceptor] Failed to initialize Harmony: {ex.Message}");
            return false;
        }
    }

    private static string GetSimpleName(Assembly asm)
    {
        if (asm == null) return "Unknown";
        try { return asm.GetName().Name; }
        catch { return asm.FullName.Split(',')[0]; }
    }

    public static bool IsSystemOrGameAssembly(Assembly assembly)
    {
        if (assembly == null)
            return true;
        string name = GetSimpleName(assembly);
        return SystemPrefixes.Any(prefix => name.StartsWith(prefix)) || GameAssemblies.Contains(name);
    }

    public static async Task InterceptAssembly(Assembly root, string[] excludedTypeNames = null)
    {
        if (root == null || IsSystemOrGameAssembly(root)) return;

        string simpleName = GetSimpleName(root);

        if (_processedAsmSimpleNames.ContainsKey(simpleName))
            return;

        if (!InitHarmony()) return;

        RuntimeDebug.LogInfo($"[ModInterceptor] InterceptAssembly: '{simpleName}'");

        var excludedSet = excludedTypeNames != null && excludedTypeNames.Length > 0
            ? new HashSet<string>(excludedTypeNames, StringComparer.Ordinal)
            : null;

        var scanRes = await Task.Run(() =>
        {
            lock (_scanLock)
            {
                return PerformScan(root);
            }
        });

        scanRes.ExcludedTypeNames = excludedSet;

        await MainThreadExecutor.RunAsync(() => ApplyPatch(scanRes));
    }

    public static void InterceptTypeFully(Type type, string[] excludedMethodNames = null)
    {
        if (type == null) return;
        if (!InitHarmony()) return;

        BannedTypes.TryAdd(type, 1);
        int processedCount = EnsureProtection(type, excludedMethodNames);
        RuntimeDebug.LogInfo($"[ModInterceptor] Intercepted type: {type.Name} ({processedCount} methods protected)");
    }

    public static void UninterceptType(Type type)
    {
        if (type == null) return;

        BannedTypes.TryRemove(type, out _);

        string typeKey = type.FullName ?? type.Name;
        _processedTypeFullNames.TryRemove(typeKey, out _);
        RuntimeDebug.LogInfo($"[ModInterceptor] Unintercepted type: {type.Name}");
    }

    public static void InterceptInstance(object instance, string[] excludedMethodNames = null)
    {
        if (instance == null) return;
        if (!InitHarmony()) return;

        if (BannedInstances.TryAdd(instance, 1))
        {
            var t = instance.GetType();
            var count = EnsureProtection(t, excludedMethodNames);
            RuntimeDebug.LogDebug($"[ModInterceptor] Intercepted instance: {t.Name} ({count} methods protected)");
        }
    }

    public static void UninterceptInstance(object instance)
    {
        if (instance == null) return;
        BannedInstances.TryRemove(instance, out _);
        RuntimeDebug.LogDebug($"[ModInterceptor] Unintercepted instance: {instance.GetType().Name}");
    }

    private class ScanRes
    {
        public HashSet<Assembly> Assemblies = new HashSet<Assembly>();
        public Dictionary<Assembly, List<Type>> Types = new Dictionary<Assembly, List<Type>>();
        public HashSet<string> ExcludedTypeNames;
        public int CachedSkippedCount = 0;
    }

    private static ScanRes PerformScan(Assembly root)
    {
        var res = new ScanRes();
        var q = new Queue<Assembly>();
        q.Enqueue(root);
        var visitedInThisScan = new HashSet<Assembly> { root };

        while (q.Count > 0)
        {
            var cur = q.Dequeue();
            string simpleName = GetSimpleName(cur);

            if (_processedAsmSimpleNames.ContainsKey(simpleName))
            {
                res.CachedSkippedCount++;
                continue;
            }

            res.Assemblies.Add(cur);
            res.Types[cur] = GetTypesCached(cur).Where(t => !IsCompilerGenerated(t)).ToList();

            foreach (var dep in GetDeps(cur))
            {
                if (!visitedInThisScan.Contains(dep) && !IsSystemOrGameAssembly(dep))
                {
                    visitedInThisScan.Add(dep);
                    q.Enqueue(dep);
                }
            }
        }
        RuntimeDebug.LogDebug($"[ModInterceptor] Scan found {res.Assemblies.Count} assemblies, {res.Types.Sum(kvp => kvp.Value.Count)} types (cached skipped: {res.CachedSkippedCount})");
        return res;
    }

    private static void ApplyPatch(ScanRes res)
    {
        int totalProcessed = 0;
        int typeSkipped = 0;

        foreach (var asm in res.Assemblies)
        {
            BannedAssemblies.TryAdd(asm, 1);
            _processedAsmSimpleNames.TryAdd(GetSimpleName(asm), 1);
        }

        foreach (var kvp in res.Types)
        {
            foreach (var type in kvp.Value)
            {
                string typeKey = type.FullName;
                if (string.IsNullOrEmpty(typeKey)) typeKey = type.Name;

                if (res.ExcludedTypeNames != null && res.ExcludedTypeNames.Contains(typeKey))
                    continue;

                if (_processedTypeFullNames.ContainsKey(typeKey))
                {
                    typeSkipped++;
                    continue;
                }

                totalProcessed += EnsureProtection(type, null);

                _processedTypeFullNames.TryAdd(typeKey, 1);
            }
        }

        RuntimeDebug.LogInfo($"[ModInterceptor] ApplyPatch done: {res.Assemblies.Count} assemblies banned, {totalProcessed} methods protected, {typeSkipped} types skipped");
    }

    private static int EnsureProtection(Type type, string[] excludedMethodNames = null)
    {
        if (type == null || IsCompilerGenerated(type)) return 0;

        int patchCount = 0;

        var prefix = new HarmonyMethod(AccessTools.Method(typeof(ModInterceptor), nameof(BlockPrefix)))
        {
            priority = Priority.First,
            before = new[] { "*" }
        };

        var finalizer = new HarmonyMethod(AccessTools.Method(typeof(ModInterceptor), nameof(BlockFinalizer)))
        {
            priority = Priority.Last,
            after = new[] { "*" }
        };

        var excludedSet = excludedMethodNames != null && excludedMethodNames.Length > 0
            ? new HashSet<string>(excludedMethodNames, StringComparer.Ordinal)
            : null;

        var methods = GetMethodsCached(type);

        foreach (var method in methods)
        {
            if (method.DeclaringType == typeof(ModInterceptor) ||
                method.DeclaringType?.Assembly == typeof(Harmony).Assembly)
                continue;

            if (_patchedMethodCache.ContainsKey(method))
                continue;

            if (excludedSet != null && excludedSet.Contains(method.Name))
                continue;

            try
            {
                var patchInfo = Harmony.GetPatchInfo(method);
                bool needsPatch = true;

                if (patchInfo != null)
                {
                    if (patchInfo.Prefixes.Any(p => p.owner == HARMONY_ID))
                        needsPatch = false;

                    var ownersToRemove = new HashSet<string>();
                    var allPatches = patchInfo.Prefixes.Concat(patchInfo.Postfixes).Concat(patchInfo.Transpilers).Concat(patchInfo.Finalizers);

                    foreach (var patch in allPatches)
                        if (patch.owner != HARMONY_ID)
                            ownersToRemove.Add(patch.owner);

                    foreach (var owner in ownersToRemove)
                        _harmony.Unpatch(method, HarmonyPatchType.All, owner);
                }

                if (needsPatch)
                {
                    _harmony.Patch(method, prefix, postfix: null, transpiler: null, finalizer: finalizer);
                    patchCount++;
                }

                _patchedMethodCache.TryAdd(method, 1);
            }
            catch
            {
            }
        }
        return patchCount;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Exception BlockFinalizer(Exception __exception, MethodBase __originalMethod, object __instance)
    {
        if (__exception == null) return null;

        try
        {
            var t = __originalMethod?.DeclaringType;
            if (t != null)
            {
                if (BannedAssemblies.ContainsKey(t.Assembly) || BannedTypes.ContainsKey(t))
                    return null;
                if (__instance != null && BannedInstances.TryGetValue(__instance, out _))
                    return null;
            }
        }
        catch { }

        return __exception;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool BlockPrefix(MethodBase __originalMethod, object __instance)
    {
        var t = __originalMethod.DeclaringType;
        if (t == null) return true;

        if (BannedAssemblies.ContainsKey(t.Assembly) || BannedTypes.ContainsKey(t))
            return false;

        if (__instance != null && BannedInstances.TryGetValue(__instance, out _))
            return false;

        return true;
    }

    public static void IgnoreAssembly(Assembly[] assemblies)
    {
        if (assemblies == null || assemblies.Length == 0) return;

        int removedAsmCount = 0;
        int removedTypeCount = 0;

        foreach (var asm in assemblies)
        {
            if (asm == null) continue;

            string simpleName = GetSimpleName(asm);

            if (BannedAssemblies.TryRemove(asm, out _))
                removedAsmCount++;

            _processedAsmSimpleNames.TryRemove(simpleName, out _);

            var types = GetTypesCached(asm);
            foreach (var type in types)
            {
                if (type == null) continue;

                if (BannedTypes.TryRemove(type, out _))
                    removedTypeCount++;

                string typeKey = type.FullName ?? type.Name;
                _processedTypeFullNames.TryRemove(typeKey, out _);
            }
        }
    }

    public static void IgnoreAssemblyByName(string[] assemblyNames)
    {
        if (assemblyNames == null || assemblyNames.Length == 0) return;

        var loadedAssemblies = AppDomain.CurrentDomain.GetAssemblies();
        var targetAssemblies = new List<Assembly>();

        foreach (var name in assemblyNames)
        {
            if (string.IsNullOrEmpty(name)) continue;

            var matched = loadedAssemblies.Where(asm =>
            {
                string simpleName = GetSimpleName(asm);
                return simpleName.Equals(name, StringComparison.OrdinalIgnoreCase) ||
                       simpleName.Contains(name);
            });

            targetAssemblies.AddRange(matched);
        }

        if (targetAssemblies.Count > 0)
            IgnoreAssembly(targetAssemblies.ToArray());
    }

    public static void UnpatchAll()
    {
        BannedAssemblies.Clear();
        BannedTypes.Clear();
        BannedInstances.Clear();
        _processedAsmSimpleNames.Clear();
        _processedTypeFullNames.Clear();

        _patchedMethodCache.Clear();
    }

    private static IEnumerable<Assembly> GetDeps(Assembly asm)
    {
        var refs = new HashSet<Assembly>();
        try
        {
            var referencedAssemblies = asm.GetReferencedAssemblies();
            var loadedMap = AppDomain.CurrentDomain.GetAssemblies().ToDictionary(x => x.FullName, x => x);

            foreach (var n in referencedAssemblies)
            {
                try
                {
                    if (loadedMap.TryGetValue(n.FullName, out var existingAsm))
                    {
                        refs.Add(existingAsm);
                    }
                    else
                    {
                        var a = Assembly.Load(n);
                        if (a != null) refs.Add(a);
                    }
                }
                catch { }
            }
        }
        catch { }
        return refs;
    }

    private static ConcurrentDictionary<Assembly, Type[]> _tCache = new ConcurrentDictionary<Assembly, Type[]>();
    public static Type[] GetTypesCached(Assembly a) => _tCache.GetOrAdd(a, asm =>
    {
        try
        {
            return asm.GetTypes();
        }
        catch (ReflectionTypeLoadException e)
        {
            return e.Types.Where(t => t != null).ToArray();
        }
        catch
        {
            return new Type[0];
        }
    });

    private static ConcurrentDictionary<Type, MethodInfo[]> _mCache = new ConcurrentDictionary<Type, MethodInfo[]>();
    public static MethodInfo[] GetMethodsCached(Type t) => _mCache.GetOrAdd(t, type =>
    {
        return type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
                   .Where(m => !m.IsAbstract && !m.IsGenericMethod && !m.IsConstructor).ToArray();
    });

    private static bool IsCompilerGenerated(Type t) => t.Name.Contains("<") || t.IsDefined(typeof(CompilerGeneratedAttribute), false);
}

public static class LimbCrashDefender
{
    private static readonly HashSet<LimbBehaviour> LimbsToRemove = new HashSet<LimbBehaviour>();
    private static DynamicHarmonyManager _harmonyManager;

    public static void Initialize()
    {
        if (_harmonyManager != null) return;
        _harmonyManager = new DynamicHarmonyManager("com.helper.limbdefender");
        string[] targetMethods = { nameof(LimbBehaviour.ManagedUpdate), nameof(LimbBehaviour.ManagedFixedUpdate), nameof(LimbBehaviour.ManagedLateUpdate) };
        foreach (var methodName in targetMethods)
        {
            _harmonyManager.AddFinalizer("limb_exception_swallower", typeof(LimbBehaviour), methodName, ctx =>
            {
                if (ctx.Exception != null)
                {
                    if (ctx.Instance is LimbBehaviour brokenLimb)
                    {
                        LimbsToRemove.Add(brokenLimb);
                    }
                    ctx.Exception = null;
                }
            });
        }
        _harmonyManager.AddPostfix("limb_safe_cleanup", typeof(LimbBehaviourManager), "LateUpdate", ctx =>
        {
            if (LimbsToRemove.Count > 0)
            {
                var limbs = LimbBehaviourManager.Limbs;
                if (limbs != null && limbs.Count > 0)
                {
                    limbs.RemoveAll(limb => LimbsToRemove.Contains(limb));
                }
                LimbsToRemove.Clear();
            }
        });
    }

    public static void Unload()
    {
        if (_harmonyManager != null)
        {
            _harmonyManager.RemoveAllById("limb_exception_swallower");
            _harmonyManager.RemoveAllById("limb_safe_cleanup");
            LimbsToRemove.Clear();
        }
    }
}
#pragma warning restore CS0612
#pragma warning restore CS0618
