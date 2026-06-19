using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace ZeroReflect;

// ═══════════════════════════════════════════════════════════════════════
// Cache key — zero-allocation struct for dictionary lookups
// ═══════════════════════════════════════════════════════════════════════

/// <summary>
/// Composite cache key with precomputed hash.
/// Type arrays are compared structurally so different array instances
/// with identical element types produce the same key.
/// </summary>
public readonly struct CacheKey : IEquatable<CacheKey>
{
    public readonly Type Type;
    public readonly string Member;
    public readonly Type[] ArgTypes;      // null for fields/properties
    public readonly Type ReturnType;
    public readonly BindingFlags Flags;
    public readonly char Prefix;
    private readonly int _hashCode;

    public CacheKey(Type type, string member, Type[] argTypes, Type returnType, BindingFlags flags, char prefix)
    {
        Type = type ?? throw new ArgumentNullException(nameof(type));
        Member = member ?? throw new ArgumentNullException(nameof(member));
        ArgTypes = argTypes; // null ok — fields/properties have no args
        ReturnType = returnType ?? typeof(void);
        Flags = flags;
        Prefix = prefix;
        _hashCode = ComputeHash(type, member, argTypes, returnType, flags, prefix);
    }

    private static int ComputeHash(Type type, string member, Type[] argTypes, Type returnType, BindingFlags flags, char prefix)
    {
        unchecked
        {
            var hash = 17;
            hash = hash * 31 + RuntimeHelpers.GetHashCode(type);
            hash = hash * 31 + member.GetHashCode();
            hash = hash * 31 + RuntimeHelpers.GetHashCode(returnType);
            hash = hash * 31 + (int)flags;
            hash = hash * 31 + prefix;
            if (argTypes != null)
            {
                hash = hash * 31 + argTypes.Length;
                for (int i = 0; i < argTypes.Length; i++)
                    hash = hash * 31 + (argTypes[i] != null ? RuntimeHelpers.GetHashCode(argTypes[i]) : 0);
            }
            return hash;
        }
    }

    public bool Equals(CacheKey other) =>
        ReferenceEquals(Type, other.Type) &&
        Member == other.Member &&
        ReferenceEquals(ReturnType, other.ReturnType) &&
        Flags == other.Flags &&
        Prefix == other.Prefix &&
        ArrayEqual(ArgTypes, other.ArgTypes);

    public override bool Equals(object obj) => obj is CacheKey other && Equals(other);
    public override int GetHashCode() => _hashCode;

    private static bool ArrayEqual(Type[] a, Type[] b)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a == null || b == null) return false;
        if (a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++)
            if (!ReferenceEquals(a[i], b[i])) return false;
        return true;
    }
}

/// <summary>
/// Equality comparer for <see cref="CacheKey"/>. Singleton — pass to
/// <see cref="ConcurrentDictionary{TKey,TValue}"/> constructor.
/// </summary>
public sealed class CacheKeyComparer : IEqualityComparer<CacheKey>
{
    public static readonly CacheKeyComparer Instance = new();
    private CacheKeyComparer() { }
    public bool Equals(CacheKey x, CacheKey y) => x.Equals(y);
    public int GetHashCode(CacheKey obj) => obj.GetHashCode();
}

// ═══════════════════════════════════════════════════════════════════════
// Access — public API
// ═══════════════════════════════════════════════════════════════════════

/// <summary>
/// Zero-reflection-overhead access to private methods, fields, and properties
/// via expression-tree-compiled delegates with automatic caching.
///
/// <para/>
/// <b>Methods (0 args):</b> <c>CreateAction&lt;T&gt;</c> / <c>CreateFunc&lt;T, R&gt;</c> — shortest syntax.
/// <br/>
/// <b>Methods (any args):</b> <c>CreateDelegate&lt;TDelegate&gt;</c> — auto-infers instance type.
/// <br/>
/// <b>Fields/Properties:</b> <c>CreateFieldGetter</c> / <c>CreateFieldSetter</c> / <c>CreatePropertyGetter</c> / <c>CreatePropertySetter</c>.
/// <br/>
/// <b>Static:</b> <c>CreateStaticAction</c> / <c>CreateStaticFunc</c> with <c>Type[]</c>.
/// <br/>
/// <b>Generic methods:</b> pass <c>typeArguments: new[] { typeof(T) }</c> to any method API.
/// <br/>
/// <b><c>ref</c>/<c>out</c>:</b> use <see cref="CreateDelegate{TDelegate}(string, BindingFlags?, Type[])"/>
/// with a custom delegate type whose Invoke parameters are by-ref.
///
/// <example>
/// // 0-arg void method
/// var start = Access.CreateAction&lt;LimbStatusBehaviour&gt;("Start");
/// start(behaviour);
///
/// // 0-arg method with return
/// var isAlive = Access.CreateFunc&lt;LimbBehaviour, bool&gt;("IsAlive");
/// bool alive = isAlive(limb);
///
/// // Method with arguments — auto-infers instance type from delegate
/// var impact = Access.CreateDelegate&lt;Action&lt;LimbBehaviour, float, Vector3&gt;&gt;("ActOnImpact");
/// impact(limb, 15f, pos);
///
/// // Field getter (zero-boxing)
/// var getHealth = Access.CreateFieldGetter&lt;LimbBehaviour, float&gt;("Health");
/// float hp = getHealth(limb);
///
/// // Generic method
/// var getComp = Access.CreateDelegate&lt;Func&lt;LimbBehaviour, Collider&gt;&gt;("GetComponent",
///     typeArguments: new[] { typeof(Collider) });
///
/// // ref parameter via custom delegate
/// delegate void RefAction&lt;T1, T2&gt;(T1 instance, ref T2 value);
/// var tryGet = Access.CreateDelegate&lt;RefAction&lt;LimbBehaviour, float&gt;&gt;("TryGetHealth");
/// float hp = 0f; tryGet(limb, ref hp);
/// </example>
///
/// <para/>
/// <b>Limitations:</b><br/>
/// • Setter APIs require <c>T : class</c> — structs copy-on-write would silently discard mutations.
///   For struct field/property writers, use <see cref="CreateStructFieldSetter{T,F}"/> /
///   <see cref="CreateStructPropertySetter{T,P}"/> which pass the instance by <c>ref</c>.<br/>
/// • Generic method type inference from argument types is not automatic —
///   pass <c>typeArguments</c> explicitly.<br/>
/// • <c>ClearCache()</c> is available if assembly unloading is a concern.
/// </summary>
public static class Access
{
    // ═══════════════════════════════════════════════════════════════════
    // Constants
    // ═══════════════════════════════════════════════════════════════════
    public const BindingFlags DefaultInstance = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
    public const BindingFlags DefaultStatic = BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;

    // ═══════════════════════════════════════════════════════════════════
    // Caches — ConcurrentDictionary + struct key (no lock, no StringBuilder)
    // ═══════════════════════════════════════════════════════════════════
    private static readonly ConcurrentDictionary<CacheKey, MethodInfo> _methodCache = new(CacheKeyComparer.Instance);
    private static readonly ConcurrentDictionary<CacheKey, Delegate> _delegateCache = new(CacheKeyComparer.Instance);

    // ═══════════════════════════════════════════════════════════════════
    // Instance methods — void return
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Convenience: <c>Action&lt;T&gt;</c> for a parameterless void instance method.
    /// </summary>
    /// <param name="methodName">Method name</param>
    /// <param name="flags">Optional <see cref="BindingFlags"/> (defaults to Instance | NonPublic | Public)</param>
    /// <param name="typeArguments">Optional generic type arguments for generic methods</param>
    public static Action<T> CreateAction<T>(string methodName, BindingFlags? flags = null, Type[] typeArguments = null)
        => CreateDelegate<Action<T>>(typeof(T), methodName, flags, typeArguments);

    /// <summary>
    /// Void instance method with any number of arguments. Instance type is <typeparamref name="T"/>.
    /// <br/>Returns <see cref="Delegate"/>; cast or use <see cref="CreateDelegate{TDelegate}(string, BindingFlags?, Type[])"/> for a typed result.
    /// <br/><c>Access.CreateAction&lt;LimbBehaviour&gt;("ActOnImpact", typeof(float), typeof(Vector3))</c>.
    /// </summary>
    /// <param name="methodName">Method name</param>
    /// <param name="argTypes">Method argument types</param>
    /// <param name="typeArguments">Optional generic type arguments for generic methods</param>
    public static Delegate CreateAction<T>(string methodName, Type[] argTypes, Type[] typeArguments = null)
    {
        var bf = DefaultInstance;
        var key = new CacheKey(typeof(T), methodName, argTypes, typeof(void), bf, 'M');
        return _delegateCache.GetOrAdd(key, _ =>
            CompileDynamicCall(typeof(T), methodName, bf, argTypes, typeof(void), isStatic: false, typeArguments));
    }

    /// <summary>
    /// Creates a compiled delegate by specifying the exact delegate type as a generic parameter.
    /// Infers instance type and argument types from <typeparamref name="TDelegate"/>'s <c>Invoke</c> signature.
    /// This is the most flexible overload — works for any number of arguments,
    /// and supports <c>ref</c>/<c>out</c> when the delegate's Invoke has by-ref parameters.
    /// <para/>
    /// <example>
    /// var act = Access.CreateDelegate&lt;Action&lt;LimbBehaviour, float, Vector3&gt;&gt;("ActOnImpact");
    /// var func = Access.CreateDelegate&lt;Func&lt;LimbBehaviour, float&gt;&gt;("GetMassStrengthRatio");
    /// </example>
    /// </summary>
    /// <typeparam name="TDelegate">The delegate type (Action&lt;T, ...&gt; or Func&lt;T, ..., R&gt;)</typeparam>
    /// <param name="instanceType">Declaring type</param>
    /// <param name="methodName">Method name</param>
    /// <param name="flags">Optional <see cref="BindingFlags"/></param>
    /// <param name="typeArguments">Optional generic type arguments for generic methods</param>
    public static TDelegate CreateDelegate<TDelegate>(Type instanceType, string methodName, BindingFlags? flags = null, Type[] typeArguments = null)
        where TDelegate : Delegate
    {
        var bf = flags ?? DefaultInstance;
        var delegateType = typeof(TDelegate);
        var invoke = delegateType.GetMethod("Invoke");
        var invokeParams = invoke.GetParameters();
        var invokeReturn = invoke.ReturnType;

        // First param is the instance; remaining are method args
        var argTypes = new Type[invokeParams.Length - 1];
        for (int i = 1; i < invokeParams.Length; i++)
            argTypes[i - 1] = invokeParams[i].ParameterType;

        // By-ref types stay as-is so Action<T, float> and Action<T, ref float>
        // produce distinct cache entries — they compile to different delegates.
        var key = new CacheKey(instanceType, methodName, argTypes, invokeReturn, bf, 'M');
        return (TDelegate)(object)_delegateCache.GetOrAdd(key, _ =>
            CompileDynamicCall(instanceType, methodName, bf, argTypes, invokeReturn,
                isStatic: false, typeArguments, typeof(TDelegate)));
    }

    /// <summary>
    /// Auto-infers the instance type from <typeparamref name="TDelegate"/>'s first <c>Invoke</c> parameter.
    /// <br/>The shortest syntax for methods with arguments:
    /// <c>Access.CreateDelegate&lt;Action&lt;LimbBehaviour, float, Vector3&gt;&gt;("ActOnImpact")</c>.
    /// <br/>Also the recommended API for <c>ref</c>/<c>out</c> parameters — use a custom delegate type.
    /// </summary>
    /// <typeparam name="TDelegate">Delegate type whose first parameter is the instance type.</typeparam>
    /// <param name="methodName">Method name</param>
    /// <param name="flags">Optional <see cref="BindingFlags"/></param>
    /// <param name="typeArguments">Optional generic type arguments for generic methods</param>
    public static TDelegate CreateDelegate<TDelegate>(string methodName, BindingFlags? flags = null, Type[] typeArguments = null)
        where TDelegate : Delegate
    {
        var firstParam = typeof(TDelegate).GetMethod("Invoke").GetParameters()[0].ParameterType;
        // For by-ref instance params (e.g. ref MyStruct on a custom delegate),
        // use the element type for the cache key and as the declaring-type.
        var instanceType = firstParam.IsByRef ? firstParam.GetElementType() : firstParam;
        return CreateDelegate<TDelegate>(instanceType, methodName, flags, typeArguments);
    }

    // ═══════════════════════════════════════════════════════════════════
    // Instance methods — with return value (Func)
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Convenience: <c>Func&lt;T, R&gt;</c> for a parameterless instance method returning <typeparamref name="R"/>.
    /// </summary>
    /// <param name="methodName">Method name</param>
    /// <param name="flags">Optional <see cref="BindingFlags"/></param>
    /// <param name="typeArguments">Optional generic type arguments for generic methods</param>
    public static Func<T, R> CreateFunc<T, R>(string methodName, BindingFlags? flags = null, Type[] typeArguments = null)
        => CreateDelegate<Func<T, R>>(typeof(T), methodName, flags, typeArguments);

    /// <summary>
    /// Instance method with any number of arguments, returning <typeparamref name="R"/>.
    /// <br/>Returns <see cref="Delegate"/>; cast or use <see cref="CreateDelegate{TDelegate}(string, BindingFlags?, Type[])"/> for a typed result.
    /// <br/><c>Access.CreateFunc&lt;LimbBehaviour, bool&gt;("IsAlive")</c>.
    /// </summary>
    /// <param name="methodName">Method name</param>
    /// <param name="argTypes">Method argument types</param>
    /// <param name="typeArguments">Optional generic type arguments for generic methods</param>
    public static Delegate CreateFunc<T, R>(string methodName, Type[] argTypes, Type[] typeArguments = null)
    {
        var bf = DefaultInstance;
        var key = new CacheKey(typeof(T), methodName, argTypes, typeof(R), bf, 'M');
        return _delegateCache.GetOrAdd(key, _ =>
            CompileDynamicCall(typeof(T), methodName, bf, argTypes, typeof(R), isStatic: false, typeArguments));
    }

    // ═══════════════════════════════════════════════════════════════════
    // Instance field access (class only for setters)
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Creates a compiled delegate for reading an instance field.
    /// Eliminates the boxing overhead of <c>FieldInfo.GetValue()</c>.
    /// Safe for both classes and structs.
    /// </summary>
    /// <typeparam name="T">Declaring type (or a base type)</typeparam>
    /// <typeparam name="F">Field type</typeparam>
    /// <param name="fieldName">Field name</param>
    /// <param name="flags">Optional <see cref="BindingFlags"/></param>
    public static Func<T, F> CreateFieldGetter<T, F>(string fieldName, BindingFlags? flags = null)
    {
        var bf = flags ?? DefaultInstance;
        var key = new CacheKey(typeof(T), fieldName, null, typeof(F), bf, 'G');
        return (Func<T, F>)_delegateCache.GetOrAdd(key, _ =>
        {
            var fi = typeof(T).GetField(fieldName, bf)
                     ?? throw new MissingFieldException($"Field '{fieldName}' not found on {typeof(T).FullName}");
            var p = Expression.Parameter(typeof(T), "instance");
            var body = Expression.Field(p, fi);
            return Expression.Lambda<Func<T, F>>(
                typeof(F) != fi.FieldType ? Expression.Convert(body, typeof(F)) : body,
                p).Compile();
        });
    }

    /// <summary>
    /// Creates a compiled delegate for writing an instance field.
    /// Eliminates the boxing overhead of <c>FieldInfo.SetValue()</c>.
    /// Restricted to reference types — see <see cref="CreateStructFieldSetter{T,F}"/> for structs.
    /// </summary>
    /// <typeparam name="T">Declaring type — must be a class</typeparam>
    /// <typeparam name="F">Field type</typeparam>
    /// <param name="fieldName">Field name</param>
    /// <param name="flags">Optional <see cref="BindingFlags"/></param>
    public static Action<T, F> CreateFieldSetter<T, F>(string fieldName, BindingFlags? flags = null)
        where T : class
    {
        var bf = flags ?? DefaultInstance;
        var key = new CacheKey(typeof(T), fieldName, null, typeof(F), bf, 'S');
        return (Action<T, F>)_delegateCache.GetOrAdd(key, _ =>
        {
            var fi = typeof(T).GetField(fieldName, bf)
                     ?? throw new MissingFieldException($"Field '{fieldName}' not found on {typeof(T).FullName}");
            var p = Expression.Parameter(typeof(T), "instance");
            var v = Expression.Parameter(typeof(F), "value");
            var assign = Expression.Assign(
                Expression.Field(p, fi),
                typeof(F) != fi.FieldType ? Expression.Convert(v, fi.FieldType) : (Expression)v);
            return Expression.Lambda<Action<T, F>>(assign, p, v).Compile();
        });
    }

    /// <summary>
    /// Creates a compiled delegate for writing an instance field on a <b>struct</b>.
    /// The instance is passed <c>ref</c> so mutations are applied to the original value.
    /// Returns a <see cref="Delegate"/>; cast to a compatible delegate type for invocation.
    /// <para/>
    /// <example>
    /// delegate void StructFieldSetter&lt;T, F&gt;(ref T instance, F value);
    /// var set = Access.CreateStructFieldSetter&lt;MyStruct, int&gt;("_value");
    /// var typed = (StructFieldSetter&lt;MyStruct, int&gt;)set;
    /// MyStruct s = new(); typed(ref s, 42);
    /// </example>
    /// </summary>
    /// <typeparam name="T">Declaring struct type</typeparam>
    /// <typeparam name="F">Field type</typeparam>
    /// <param name="fieldName">Field name</param>
    /// <param name="flags">Optional <see cref="BindingFlags"/></param>
    public static Delegate CreateStructFieldSetter<T, F>(string fieldName, BindingFlags? flags = null)
        where T : struct
    {
        var bf = flags ?? DefaultInstance;
        var key = new CacheKey(typeof(T), fieldName, null, typeof(F), bf, 's');
        return _delegateCache.GetOrAdd(key, _ =>
        {
            var fi = typeof(T).GetField(fieldName, bf)
                     ?? throw new MissingFieldException($"Field '{fieldName}' not found on {typeof(T).FullName}");
            var byRefType = typeof(T).MakeByRefType();
            var p = Expression.Parameter(byRefType, "instance");
            var v = Expression.Parameter(typeof(F), "value");
            var assign = Expression.Assign(
                Expression.Field(p, fi),
                typeof(F) != fi.FieldType ? Expression.Convert(v, fi.FieldType) : (Expression)v);
            var delegateType = Expression.GetActionType(byRefType, typeof(F));
            return Expression.Lambda(delegateType, assign, p, v).Compile();
        });
    }

    /// <summary>
    /// Creates a compiled delegate for reading an instance field on a struct.
    /// This overload exists for API clarity; <see cref="CreateFieldGetter{T,F}"/> also works for structs.
    /// </summary>
    public static Func<T, F> CreateStructFieldGetter<T, F>(string fieldName, BindingFlags? flags = null)
        where T : struct
        => CreateFieldGetter<T, F>(fieldName, flags);

    // ═══════════════════════════════════════════════════════════════════
    // Instance property access (class only for setters)
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Creates a compiled delegate for reading an instance property.
    /// Safe for both classes and structs.
    /// </summary>
    /// <typeparam name="T">Declaring type (or a base type)</typeparam>
    /// <typeparam name="P">Property type</typeparam>
    /// <param name="propertyName">Property name</param>
    /// <param name="flags">Optional <see cref="BindingFlags"/></param>
    public static Func<T, P> CreatePropertyGetter<T, P>(string propertyName, BindingFlags? flags = null)
    {
        var bf = flags ?? DefaultInstance;
        var key = new CacheKey(typeof(T), propertyName, null, typeof(P), bf, 'G');
        return (Func<T, P>)_delegateCache.GetOrAdd(key, _ =>
        {
            var pi = typeof(T).GetProperty(propertyName, bf)
                     ?? throw new MissingMemberException($"Property '{propertyName}' not found on {typeof(T).FullName}");
            if (pi.GetMethod == null)
                throw new InvalidOperationException($"Property '{propertyName}' on {typeof(T).FullName} has no getter.");
            var p = Expression.Parameter(typeof(T), "instance");
            var body = Expression.Property(p, pi);
            return Expression.Lambda<Func<T, P>>(
                typeof(P) != pi.PropertyType ? Expression.Convert(body, typeof(P)) : body,
                p).Compile();
        });
    }

    /// <summary>
    /// Creates a compiled delegate for writing an instance property.
    /// Restricted to reference types — see <see cref="CreateStructPropertySetter{T,P}"/> for structs.
    /// </summary>
    /// <typeparam name="T">Declaring type — must be a class</typeparam>
    /// <typeparam name="P">Property type</typeparam>
    /// <param name="propertyName">Property name</param>
    /// <param name="flags">Optional <see cref="BindingFlags"/></param>
    public static Action<T, P> CreatePropertySetter<T, P>(string propertyName, BindingFlags? flags = null)
        where T : class
    {
        var bf = flags ?? DefaultInstance;
        var key = new CacheKey(typeof(T), propertyName, null, typeof(P), bf, 'S');
        return (Action<T, P>)_delegateCache.GetOrAdd(key, _ =>
        {
            var pi = typeof(T).GetProperty(propertyName, bf)
                     ?? throw new MissingMemberException($"Property '{propertyName}' not found on {typeof(T).FullName}");
            if (pi.SetMethod == null)
                throw new InvalidOperationException($"Property '{propertyName}' on {typeof(T).FullName} has no setter.");
            var p = Expression.Parameter(typeof(T), "instance");
            var v = Expression.Parameter(typeof(P), "value");
            var assign = Expression.Assign(
                Expression.Property(p, pi),
                typeof(P) != pi.PropertyType ? Expression.Convert(v, pi.PropertyType) : (Expression)v);
            return Expression.Lambda<Action<T, P>>(assign, p, v).Compile();
        });
    }

    /// <summary>
    /// Creates a compiled delegate for writing an instance property on a <b>struct</b>.
    /// The instance is passed <c>ref</c> so mutations are applied to the original value.
    /// </summary>
    /// <typeparam name="T">Declaring struct type</typeparam>
    /// <typeparam name="P">Property type</typeparam>
    /// <param name="propertyName">Property name</param>
    /// <param name="flags">Optional <see cref="BindingFlags"/></param>
    public static Delegate CreateStructPropertySetter<T, P>(string propertyName, BindingFlags? flags = null)
        where T : struct
    {
        var bf = flags ?? DefaultInstance;
        var key = new CacheKey(typeof(T), propertyName, null, typeof(P), bf, 's');
        return _delegateCache.GetOrAdd(key, _ =>
        {
            var pi = typeof(T).GetProperty(propertyName, bf)
                     ?? throw new MissingMemberException($"Property '{propertyName}' not found on {typeof(T).FullName}");
            if (pi.SetMethod == null)
                throw new InvalidOperationException($"Property '{propertyName}' on {typeof(T).FullName} has no setter.");
            var byRefType = typeof(T).MakeByRefType();
            var p = Expression.Parameter(byRefType, "instance");
            var v = Expression.Parameter(typeof(P), "value");
            var assign = Expression.Assign(
                Expression.Property(p, pi),
                typeof(P) != pi.PropertyType ? Expression.Convert(v, pi.PropertyType) : (Expression)v);
            var delegateType = Expression.GetActionType(byRefType, typeof(P));
            return Expression.Lambda(delegateType, assign, p, v).Compile();
        });
    }

    /// <summary>
    /// Creates a compiled delegate for reading an instance property on a struct.
    /// This overload exists for API clarity; <see cref="CreatePropertyGetter{T,P}"/> also works for structs.
    /// </summary>
    public static Func<T, P> CreateStructPropertyGetter<T, P>(string propertyName, BindingFlags? flags = null)
        where T : struct
        => CreatePropertyGetter<T, P>(propertyName, flags);

    // ═══════════════════════════════════════════════════════════════════
    // Static methods
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Static method with any number of arguments. All <typeparamref name="TDelegate"/>
    /// parameters become method arguments (there is no instance parameter).
    /// This is the static counterpart of <see cref="CreateDelegate{TDelegate}(string, BindingFlags?, Type[])"/>.
    /// <para/>
    /// <example>
    /// var alloc = Access.CreateStaticDelegate&lt;Action&lt;int&gt;&gt;(typeof(SomeUtil), "Allocate");
    /// var parse = Access.CreateStaticDelegate&lt;Func&lt;string, bool&gt;&gt;(typeof(Parser), "TryParse");
    /// </example>
    /// </summary>
    /// <typeparam name="TDelegate">Delegate type whose parameters match the method's parameters</typeparam>
    /// <param name="declaringType">Type that declares the static method</param>
    /// <param name="methodName">Method name</param>
    /// <param name="flags">Optional <see cref="BindingFlags"/> (defaults to Static | NonPublic | Public)</param>
    /// <param name="typeArguments">Optional generic type arguments for generic methods</param>
    public static TDelegate CreateStaticDelegate<TDelegate>(Type declaringType, string methodName,
        BindingFlags? flags = null, Type[] typeArguments = null)
        where TDelegate : Delegate
    {
        var bf = flags ?? DefaultStatic;
        var delegateType = typeof(TDelegate);
        var invoke = delegateType.GetMethod("Invoke");
        var invokeParams = invoke.GetParameters();
        var invokeReturn = invoke.ReturnType;

        var argTypes = new Type[invokeParams.Length];
        for (int i = 0; i < invokeParams.Length; i++)
            argTypes[i] = invokeParams[i].ParameterType;

        var key = new CacheKey(declaringType, methodName, argTypes, invokeReturn, bf, 'M');
        return (TDelegate)(object)_delegateCache.GetOrAdd(key, _ =>
            CompileDynamicCall(declaringType, methodName, bf, argTypes, invokeReturn,
                isStatic: true, typeArguments, typeof(TDelegate)));
    }

    /// <summary>
    /// Static void method. <typeparamref name="T"/> is the declaring type.
    /// <br/><c>Access.CreateStaticAction&lt;SomeUtil&gt;("Allocate")</c>.
    /// </summary>
    /// <param name="methodName">Method name</param>
    /// <param name="argTypes">Method argument types</param>
    /// <param name="typeArguments">Optional generic type arguments for generic methods</param>
    public static Delegate CreateStaticAction<T>(string methodName, Type[] argTypes, Type[] typeArguments = null)
    {
        var bf = DefaultStatic;
        var key = new CacheKey(typeof(T), methodName, argTypes, typeof(void), bf, 'M');
        return _delegateCache.GetOrAdd(key, _ =>
            CompileDynamicCall(typeof(T), methodName, bf, argTypes, typeof(void), isStatic: true, typeArguments));
    }

    /// <summary>
    /// Static method returning <typeparamref name="R"/>. <typeparamref name="T"/> is the declaring type.
    /// <br/><c>Access.CreateStaticFunc&lt;SomeUtil, bool&gt;("Validate", typeof(float))</c>.
    /// </summary>
    /// <param name="methodName">Method name</param>
    /// <param name="argTypes">Method argument types</param>
    /// <param name="typeArguments">Optional generic type arguments for generic methods</param>
    public static Delegate CreateStaticFunc<T, R>(string methodName, Type[] argTypes, Type[] typeArguments = null)
    {
        var bf = DefaultStatic;
        var key = new CacheKey(typeof(T), methodName, argTypes, typeof(R), bf, 'M');
        return _delegateCache.GetOrAdd(key, _ =>
            CompileDynamicCall(typeof(T), methodName, bf, argTypes, typeof(R), isStatic: true, typeArguments));
    }

    // ═══════════════════════════════════════════════════════════════════
    // Static field access
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Creates a compiled delegate for reading a static field.
    /// </summary>
    /// <typeparam name="T">Declaring type</typeparam>
    /// <typeparam name="F">Field type</typeparam>
    /// <param name="fieldName">Field name</param>
    /// <param name="flags"><see cref="BindingFlags"/> (defaults to Static | NonPublic | Public)</param>
    public static Func<F> CreateStaticFieldGetter<T, F>(string fieldName, BindingFlags? flags = null)
    {
        var bf = flags ?? DefaultStatic;
        var key = new CacheKey(typeof(T), fieldName, null, typeof(F), bf, 'G');
        return (Func<F>)_delegateCache.GetOrAdd(key, _ =>
        {
            var fi = typeof(T).GetField(fieldName, bf)
                     ?? throw new MissingFieldException($"Static field '{fieldName}' not found on {typeof(T).FullName}");
            var body = Expression.Field(null, fi);
            return Expression.Lambda<Func<F>>(
                typeof(F) != fi.FieldType ? Expression.Convert(body, typeof(F)) : body).Compile();
        });
    }

    /// <summary>
    /// Creates a compiled delegate for writing a static field.
    /// Restricted to reference types — static fields on value types are rare and
    /// share the same semantics (no copy issue).
    /// </summary>
    /// <typeparam name="T">Declaring type</typeparam>
    /// <typeparam name="F">Field type</typeparam>
    /// <param name="fieldName">Field name</param>
    /// <param name="flags"><see cref="BindingFlags"/> (defaults to Static | NonPublic | Public)</param>
    public static Action<F> CreateStaticFieldSetter<T, F>(string fieldName, BindingFlags? flags = null)
    {
        var bf = flags ?? DefaultStatic;
        var key = new CacheKey(typeof(T), fieldName, null, typeof(F), bf, 'S');
        return (Action<F>)_delegateCache.GetOrAdd(key, _ =>
        {
            var fi = typeof(T).GetField(fieldName, bf)
                     ?? throw new MissingFieldException($"Static field '{fieldName}' not found on {typeof(T).FullName}");
            if (fi.IsLiteral || fi.IsInitOnly)
                throw new InvalidOperationException($"Field '{fieldName}' on {typeof(T).FullName} is readonly.");
            var v = Expression.Parameter(typeof(F), "value");
            var assign = Expression.Assign(
                Expression.Field(null, fi),
                typeof(F) != fi.FieldType ? Expression.Convert(v, fi.FieldType) : (Expression)v);
            return Expression.Lambda<Action<F>>(assign, v).Compile();
        });
    }

    // ═══════════════════════════════════════════════════════════════════
    // Static property access
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Creates a compiled delegate for reading a static property.
    /// </summary>
    /// <typeparam name="T">Declaring type</typeparam>
    /// <typeparam name="P">Property type</typeparam>
    /// <param name="propertyName">Property name</param>
    /// <param name="flags"><see cref="BindingFlags"/> (defaults to Static | NonPublic | Public)</param>
    public static Func<P> CreateStaticPropertyGetter<T, P>(string propertyName, BindingFlags? flags = null)
    {
        var bf = flags ?? DefaultStatic;
        var key = new CacheKey(typeof(T), propertyName, null, typeof(P), bf, 'G');
        return (Func<P>)_delegateCache.GetOrAdd(key, _ =>
        {
            var pi = typeof(T).GetProperty(propertyName, bf)
                     ?? throw new MissingMemberException($"Static property '{propertyName}' not found on {typeof(T).FullName}");
            if (pi.GetMethod == null)
                throw new InvalidOperationException($"Property '{propertyName}' on {typeof(T).FullName} has no getter.");
            var body = Expression.Property(null, pi);
            return Expression.Lambda<Func<P>>(
                typeof(P) != pi.PropertyType ? Expression.Convert(body, typeof(P)) : body).Compile();
        });
    }

    /// <summary>
    /// Creates a compiled delegate for writing a static property.
    /// </summary>
    /// <typeparam name="T">Declaring type</typeparam>
    /// <typeparam name="P">Property type</typeparam>
    /// <param name="propertyName">Property name</param>
    /// <param name="flags"><see cref="BindingFlags"/> (defaults to Static | NonPublic | Public)</param>
    public static Action<P> CreateStaticPropertySetter<T, P>(string propertyName, BindingFlags? flags = null)
    {
        var bf = flags ?? DefaultStatic;
        var key = new CacheKey(typeof(T), propertyName, null, typeof(P), bf, 'S');
        return (Action<P>)_delegateCache.GetOrAdd(key, _ =>
        {
            var pi = typeof(T).GetProperty(propertyName, bf)
                     ?? throw new MissingMemberException($"Static property '{propertyName}' not found on {typeof(T).FullName}");
            if (pi.SetMethod == null)
                throw new InvalidOperationException($"Property '{propertyName}' on {typeof(T).FullName} has no setter.");
            var v = Expression.Parameter(typeof(P), "value");
            var assign = Expression.Assign(
                Expression.Property(null, pi),
                typeof(P) != pi.PropertyType ? Expression.Convert(v, pi.PropertyType) : (Expression)v);
            return Expression.Lambda<Action<P>>(assign, v).Compile();
        });
    }

    // ═══════════════════════════════════════════════════════════════════
    // Cache management
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Clears all cached delegates and method infos.
    /// Useful for debugging or when assemblies are reloaded.
    /// </summary>
    public static void ClearCache()
    {
        _delegateCache.Clear();
        _methodCache.Clear();
    }

    /// <summary>
    /// Returns the number of cached compiled delegates.
    /// </summary>
    public static int CacheCount => _delegateCache.Count;

    /// <summary>
    /// Returns the number of cached <see cref="MethodInfo"/> lookups.
    /// </summary>
    public static int MethodCacheCount => _methodCache.Count;

    // ═══════════════════════════════════════════════════════════════════
    // public: Method resolution with caching
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Resolves a MethodInfo by name and parameter types.
    /// Results are cached in <see cref="_methodCache"/> so repeated lookups
    /// for the same (type, name, args, flags) skip the full method scan.
    ///
    /// Supports:
    /// - Optional parameters (fills trailing unprovided args with defaults)
    /// - Loose type matching (e.g. delegate takes object but method takes RagdollPose)
    /// - Generic methods (when <paramref name="typeArguments"/> is provided)
    /// </summary>
    private static MethodInfo ResolveMethod(Type type, string methodName, BindingFlags flags,
        Type[] providedArgTypes, Type[] typeArguments)
    {
        // Build the cache key.  By-ref types are kept as-is so M(float) and M(ref float)
        // produce distinct cache entries — they resolve to different MethodInfos.
        // Return type is unused for method resolution, so we pass typeof(void).
        var lookUpArgTypes = providedArgTypes ?? [];
        var key = new CacheKey(type, methodName, lookUpArgTypes, typeof(void), flags, 'R');

        return _methodCache.GetOrAdd(key, _ =>
        {
            if (typeArguments != null && typeArguments.Length > 0)
                return ResolveGenericMethod(type, methodName, flags, providedArgTypes, typeArguments);

            return ResolveNonGenericMethod(type, methodName, flags, providedArgTypes);
        });
    }

    /// <summary>
    /// Resolves a non-generic method (or open generic w/o type args — treated as non-generic for compat).
    /// </summary>
    private static MethodInfo ResolveNonGenericMethod(Type type, string methodName, BindingFlags flags,
        Type[] providedArgTypes)
    {
        // Phase 1: exact parameter-type match via built-in GetMethod
        try
        {
            var mi = type.GetMethod(methodName, flags, null, providedArgTypes, null);
            if (mi != null)
                return mi;
        }
        catch (AmbiguousMatchException)
        {
            // Fall through to manual resolution
        }

        // Phase 2: manual search with optional-param and assignability support
        MethodInfo bestMatch = null;
        int bestScore = int.MaxValue;

        foreach (var mi in type.GetMethods(flags))
        {
            if (mi.Name != methodName)
                continue;

            // Skip generic method definitions — callers must use typeArguments
            if (mi.IsGenericMethodDefinition)
                continue;

            var pis = mi.GetParameters();

            // Require at least as many params as the delegate provides
            if (pis.Length < providedArgTypes.Length)
                continue;

            // Check first N params — allow assignable (not just exact) match
            int score = 0;
            bool match = true;
            for (int i = 0; i < providedArgTypes.Length; i++)
            {
                var pt = pis[i].ParameterType;
                var at = providedArgTypes[i];

                // Penalise by-ref mismatch: e.g. delegate passes float but method takes ref float
                if (at.IsByRef != pt.IsByRef)
                    score += 10;

                // Strip by-ref for type comparison
                if (at.IsByRef) at = at.GetElementType();
                if (pt.IsByRef) pt = pt.GetElementType();

                if (pt == at)
                {
                    // Exact match — no penalty
                }
                else if (pt.IsAssignableFrom(at))
                {
                    score += 1; // small penalty for assignable-but-not-exact
                }
                else if (at.IsAssignableFrom(pt))
                {
                    score += 2; // looser match (e.g. object → RagdollPose)
                }
                else
                {
                    match = false;
                    break;
                }
            }
            if (!match)
                continue;

            // Remaining (trailing) params must be optional
            for (int i = providedArgTypes.Length; i < pis.Length; i++)
            {
                if (!pis[i].IsOptional)
                {
                    match = false;
                    break;
                }
                score += 100; // heavy penalty — prefer fewer optional params
            }
            if (!match)
                continue;

            if (score < bestScore)
            {
                bestScore = score;
                bestMatch = mi;
                if (score == 0)
                    break; // perfect match — stop searching
            }
        }

        if (bestMatch == null)
            throw new MissingMethodException(
                $"Method '{type.FullName}.{methodName}({ArgNames(providedArgTypes)})' not found with flags {flags}.");

        return bestMatch;
    }

    /// <summary>
    /// Resolves and closes a generic method definition.
    /// </summary>
    private static MethodInfo ResolveGenericMethod(Type type, string methodName, BindingFlags flags,
        Type[] providedArgTypes, Type[] typeArguments)
    {
        MethodInfo bestMatch = null;
        int bestScore = int.MaxValue;

        foreach (var mi in type.GetMethods(flags))
        {
            if (mi.Name != methodName)
                continue;
            if (!mi.IsGenericMethodDefinition)
                continue;
            if (mi.GetGenericArguments().Length != typeArguments.Length)
                continue;

            MethodInfo closed;
            try
            {
                closed = mi.MakeGenericMethod(typeArguments);
            }
            catch (ArgumentException)
            {
                // The only remaining failure mode is a generic constraint violation
                // (type arg count was already verified above). Skip this candidate.
                continue;
            }

            var pis = closed.GetParameters();

            if (pis.Length < providedArgTypes.Length)
                continue;

            int score = 0;
            bool match = true;
            for (int i = 0; i < providedArgTypes.Length; i++)
            {
                var pt = pis[i].ParameterType;
                var at = providedArgTypes[i];

                // Penalise by-ref mismatch
                if (at.IsByRef != pt.IsByRef)
                    score += 10;

                if (at.IsByRef) at = at.GetElementType();
                if (pt.IsByRef) pt = pt.GetElementType();

                if (pt == at)
                {
                    // Exact match
                }
                else if (pt.IsAssignableFrom(at))
                {
                    score += 1;
                }
                else if (at.IsAssignableFrom(pt))
                {
                    score += 2;
                }
                else
                {
                    match = false;
                    break;
                }
            }
            if (!match)
                continue;

            for (int i = providedArgTypes.Length; i < pis.Length; i++)
            {
                if (!pis[i].IsOptional)
                {
                    match = false;
                    break;
                }
                score += 100;
            }
            if (!match)
                continue;

            if (score < bestScore)
            {
                bestScore = score;
                bestMatch = closed;
                if (score == 0)
                    break;
            }
        }

        if (bestMatch == null)
            throw new MissingMethodException(
                $"Generic method '{type.FullName}.{methodName}<{ArgNames(typeArguments)}>({ArgNames(providedArgTypes)})' " +
                $"not found with flags {flags}.");

        return bestMatch;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static string ArgNames(Type[] types)
    {
        if (types == null || types.Length == 0) return "";
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < types.Length; i++)
        {
            if (i > 0) sb.Append(", ");
            sb.Append(types[i].Name);
        }
        return sb.ToString();
    }

    // ═══════════════════════════════════════════════════════════════════
    // public: Dynamic call compilation (any number of args)
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Core engine: compiles an expression-tree call for a method with any number of arguments.
    /// Uses <c>Expression.GetActionType</c> / <c>Expression.GetFuncType</c> to build the correct
    /// delegate type at runtime, so there is no parameter-count limit beyond what the BCL delegates support.
    ///
    /// <c>ref</c>/<c>out</c> parameters are supported: when <paramref name="argTypes"/> contains
    /// by-ref types (<c>IsByRef == true</c>), the corresponding <see cref="ParameterExpression"/>
    /// is created with the by-ref type and passed directly to the call.
    /// </summary>
    private static Delegate CompileDynamicCall(
        Type instanceType, string methodName, BindingFlags flags,
        Type[] argTypes, Type returnType, bool isStatic, Type[] typeArguments,
        Type knownDelegateType = null)
    {
        var mi = ResolveMethod(instanceType, methodName, flags, argTypes ?? [], typeArguments);

        var paramList = new List<ParameterExpression>((argTypes?.Length ?? 0) + 1);

        // Instance parameter (only for non-static calls)
        if (!isStatic)
            paramList.Add(Expression.Parameter(instanceType, "instance"));

        // Argument parameters — preserve by-ref types for ref/out
        var argExprs = new Expression[argTypes?.Length ?? 0];
        if (argTypes != null)
        {
            for (int i = 0; i < argTypes.Length; i++)
            {
                var p = Expression.Parameter(argTypes[i], $"arg{i}");
                paramList.Add(p);
                argExprs[i] = p;
            }
        }

        // Build method call expression
        var callArgs = BuildCallArgs(mi, argExprs);
        Expression call = isStatic
            ? Expression.Call(mi, callArgs)
            : Expression.Call(paramList[0], mi, callArgs);

        // Handle return type conversion if needed
        if (returnType != typeof(void) && returnType != mi.ReturnType)
        {
            // If return type differs but one is by-ref version of the other, strip/adjust
            var effectiveReturn = returnType.IsByRef ? returnType.GetElementType() : returnType;
            var effectiveMiReturn = mi.ReturnType.IsByRef ? mi.ReturnType.GetElementType() : mi.ReturnType;
            if (effectiveReturn != effectiveMiReturn)
                call = Expression.Convert(call, returnType);
        }

        // Build the correct delegate type at runtime.
        // When a known delegate type is supplied (e.g. from CreateDelegate<TDelegate>),
        // use it directly — this is required for ref/out parameters since
        // Expression.GetActionType/GetFuncType can't represent by-ref generic params.
        Type delegateType;
        if (knownDelegateType != null)
        {
            delegateType = knownDelegateType;
        }
        else if (returnType == typeof(void))
        {
            delegateType = Expression.GetActionType(paramList.Select(p => p.Type).ToArray());
        }
        else
        {
            var paramTypes = paramList.Select(p => p.Type).ToArray();
            var funcTypes = new Type[paramTypes.Length + 1];
            Array.Copy(paramTypes, funcTypes, paramTypes.Length);
            funcTypes[paramTypes.Length] = returnType;
            delegateType = Expression.GetFuncType(funcTypes);
        }

        return Expression.Lambda(delegateType, call, paramList).Compile();
    }

    // ═══════════════════════════════════════════════════════════════════
    // public: Call argument building
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Builds the final Expression[] for a method call, handling:
    /// - <c>ref</c>/<c>out</c> parameter passing (by-ref types flow through directly)
    /// - Optional trailing parameters (fills with Expression.Default / Expression.Constant)
    /// - Loose type matching (wraps with Expression.Convert when arg type differs from param type)
    /// </summary>
    private static Expression[] BuildCallArgs(MethodInfo mi, Expression[] provided)
    {
        var pis = mi.GetParameters();
        var args = new Expression[pis.Length];

        // Map provided args, inserting converts as needed
        for (int i = 0; i < provided.Length; i++)
        {
            var paramType = pis[i].ParameterType;
            var argType = provided[i].Type;

            if (paramType == argType)
            {
                // Exact match (including by-ref ↔ by-ref)
                args[i] = provided[i];
            }
            else if (paramType.IsByRef && argType.IsByRef)
            {
                // Both are by-ref but to different element types — convert the element
                // e.g. ref object → ref RagdollPose
                args[i] = provided[i]; // by-ref compatible at IL level
            }
            else if (paramType.IsByRef && !argType.IsByRef)
            {
                // Value type passed to ref param — need a temporary variable
                // This is unusual; wrap with a block that assigns then passes by ref
                // For simplicity, treat as convertible
                args[i] = provided[i]; // Expression.Call handles the ref temporary
            }
            else if (!paramType.IsByRef && argType.IsByRef)
            {
                // ref argument passed to by-value parameter — dereference
                // (This shouldn't happen in practice but handle it)
                args[i] = provided[i]; // IL handles the deref
            }
            else
            {
                // Neither is by-ref: loose type match
                args[i] = Expression.Convert(provided[i], paramType);
            }
        }

        // Fill trailing optional params with their actual default values.
        for (int i = provided.Length; i < pis.Length; i++)
        {
            var pi = pis[i];
            args[i] = pi.HasDefaultValue
                ? Expression.Constant(pi.DefaultValue, pi.ParameterType)
                : Expression.Default(pi.ParameterType);
        }

        return args;
    }
}
