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
using Debug = UnityEngine.Debug;
using FishUtility;
#pragma warning disable CS0618
#pragma warning disable CS0612

namespace FishUtility;

public static class AssetBundleLoader
{
    public static T LoadAssetFromAssetBundle<T>(string bundlePath, string assetName) where T : UnityEngine.Object
    {
        AssetBundle bundle = AssetBundle.LoadFromFile(Path.Combine(ModAPI.Metadata.MetaLocation, bundlePath));
        if (bundle == null)
        {
            Debug.Log("Failed to load AssetBundle!");
            return default;
        }
        T asset = bundle.LoadAsset<T>(assetName);
        if (asset == null)
            Debug.Log($"Failed to load {typeof(T).Name} '{assetName}' from AssetBundle!");
        bundle.Unload(false);
        return asset;
    }

    public static Material LoadMaterialFromAssetBundle(string bundlePath, string materialName)
        => LoadAssetFromAssetBundle<Material>(bundlePath, materialName);

    public static TMP_FontAsset LoadFontFromAssetBundle(string bundlePath, string fontAssetName)
        => LoadAssetFromAssetBundle<TMP_FontAsset>(bundlePath, fontAssetName);

    public static GameObject LoadPrefabFromAssetBundle(string bundlePath, string prefabName)
        => LoadAssetFromAssetBundle<GameObject>(bundlePath, prefabName);
}

public static partial class Utility
{
    public static string modPath => ModAPI.Metadata.MetaLocation;

    public static T CreateHelper<T>(string name) where T : Component => new GameObject(name, typeof(T)) { hideFlags = HideFlags.HideAndDontSave }.GetComponent<T>();

    public readonly static System.Random Random = new System.Random();
    public static LayerMask mask = LayerMask.GetMask("Objects", "CollidingDebris", "Debris", "ImmobilityField", "Bounds");
    public enum LimbIdFromName { Head, UpperBody, MiddleBody, LowerBody, UpperLegFront, LowerLegFront, FootFront, UpperLeg, LowerLeg, Foot, UpperArmFront, LowerArmFront, UpperArm, LowerArm }
    private static readonly Dictionary<string, (KeyCode mainKey, KeyCode? modifier)> PunctuationMap = new Dictionary<string, (KeyCode, KeyCode?)>
    {
        { "!", (KeyCode.Alpha1, KeyCode.LeftShift) },
        { "@", (KeyCode.Alpha2, KeyCode.LeftShift) },
        { "#", (KeyCode.Alpha3, KeyCode.LeftShift) },
        { "$", (KeyCode.Alpha4, KeyCode.LeftShift) },
        { "%", (KeyCode.Alpha5, KeyCode.LeftShift) },
        { "^", (KeyCode.Alpha6, KeyCode.LeftShift) },
        { "&", (KeyCode.Alpha7, KeyCode.LeftShift) },
        { "*", (KeyCode.Alpha8, KeyCode.LeftShift) },
        { "(", (KeyCode.Alpha9, KeyCode.LeftShift) },
        { ")", (KeyCode.Alpha0, KeyCode.LeftShift) },
        { "-", (KeyCode.Minus, null) },
        { "=", (KeyCode.Equals, null) },
        { ",", (KeyCode.Comma, null) },
        { ".", (KeyCode.Period, null) },
        { "/", (KeyCode.Slash, null) },
        { ";", (KeyCode.Semicolon, null) },
        { "'", (KeyCode.Quote, null) },
        { "[", (KeyCode.LeftBracket, null) },
        { "]", (KeyCode.RightBracket, null) },
        { "\\", (KeyCode.Backslash, null) }
    };
    public static SpawnableAsset[] allAssets = FindTypesInWorld<SpawnableAsset>();
    public static Dictionary<Vector3, Action> GravityPoints = new Dictionary<Vector3, Action>();
    public static List<Action> actions = new List<Action>();
    private static FieldInfo Head = typeof(PersonBehaviour).GetField("Head", BindingFlags.NonPublic | BindingFlags.Instance);
    private static FieldInfo Node = typeof(ConnectedNodeBehaviour).GetField("<IsConnectedToRoot>k__BackingField", BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
    public static GameObject pulsePrefab = FindTypesInWorld<GameObject>().FirstOrDefault(obj => obj.name == "pulse");
    private static GameObject smokePrefab = ModAPI.FindSpawnable("Particle Projector").Prefab.transform.Find("smoke").gameObject;
    public static GameObject personPrefab = ModAPI.FindSpawnable("Human").Prefab;
    public static List<RagdollPose> Poses = personPrefab.GetPerson().Poses;
    public static Sprite BarSprite = personPrefab.GetComponentInChildren<LimbBehaviour>().LimbStatus.GetComponent<LimbStatusBehaviour>().BarSprite;
    public static PhysicalProperties ExcaliburProperties = UnityEngine.Object.Instantiate(ModAPI.FindSpawnable("Excalibur").Prefab.GetPhysicalProperties());
    public static LayerMask ToRemove = 9728;
    public const float Inf = float.PositiveInfinity, FloatMax = float.MaxValue, NaN = float.NaN;
    public const int IntMax = int.MaxValue;
    public static Color smokeColor = Color.white;
    public static Color[] cyberBlue = new Color[] {
        new Color(0f, 0.1f, 0.3f),
        new Color(0f, 0.4f, 0.8f),
        new Color(0.8f, 0.9f, 1f),
        new Color(0f, 0.4f, 0.8f),
        new Color(0f, 0.1f, 0.3f)
    };
    public static Color[] magmaEmber = new Color[] {
        new Color(0.2f, 0f, 0f),
        new Color(0.8f, 0.1f, 0f),
        new Color(1f, 0.9f, 0.3f),
        new Color(0.8f, 0.1f, 0f),
        new Color(0.2f, 0f, 0f)
    };
    public static Color[] ghostEmerald = new Color[] {
        new Color(0f, 0.2f, 0.1f),
        new Color(0f, 0.7f, 0.3f),
        new Color(0.6f, 1f, 0.8f),
        new Color(0f, 0.7f, 0.3f),
        new Color(0.2f, 0.2f, 0.1f)
    };
    public static Color[] Rainbow = new Color[] { Color.red, new Color(1.0f, 0.65f, 0.0f), Color.yellow, Color.green, Color.cyan, Color.blue, new Color(0.5f, 0.0f, 0.5f), Color.magenta };
    public static string Version = "1.28 alpha 2";
    public static bool InCurrentVersion = GameVersion.Version.Equals(Version);
    public static Vector3 MousePosition
    {
        get
        {
            Vector3 mousePosition = Input.mousePosition;
            mousePosition.z = 0f - Camera.main.transform.position.z;
            return Camera.main.ScreenToWorldPoint(mousePosition);
        }
    }
    public static Scene currentScene => SceneManager.GetActiveScene();
    public static void Destroy(this UnityEngine.Object @object) => TryCatchAction(() => UnityEngine.Object.Destroy(@object));
    public static void Destroy(this UnityEngine.Object @object, float time) => TryCatchAction(() => UnityEngine.Object.Destroy(@object, time));
    public static void BetterDestroy<T>(this GameObject Instance) where T : Component => Instance.GetComponents<T>().ForEach(component => component.Destroy());
    public static GameObject GetRoot(this GameObject instance)
    {
        var root = instance.transform.root;
        if (instance.HasComponent<Undraggable>() && root.name == "WORLD")
            return null;
        Func<Transform, GameObject> getTop = null;
        getTop = (transform) => (transform.NotMain()) ? getTop(transform.parent) : transform.gameObject;
        return (root.name != "WORLD") ? root.gameObject : getTop(instance.transform);
    }
    public static Transform GetRoot(this Transform instance)
    {

        var root = instance.root;
        if (instance.gameObject.HasComponent<Undraggable>() && root.name == "WORLD")
            return null;
        Func<Transform, Transform> getTop = null;
        getTop = (transform) => (transform.NotMain()) ? getTop(transform.parent) : transform;
        return (root.name != "WORLD") ? root : getTop(instance.transform);
    }

    public static bool NotMain(this Transform instance) => instance.parent.name.ToUpper().Contains("WORLD") && instance.parent.name.ToUpper().Contains("MAP") && !MapRegistry.GetAllMaps().Any(map => map.name == instance.parent.name.Replace("(Clone)", ""));

    public static async Task LikeWorkshopItem(ulong workshopItemId)
    {
        var sUgcType = Type.GetType("Stea" + "mworks.Ste" + "amUGC, Facepunch.Stea" + "mworks.Win64, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null");
        var publishedFileIdType = Type.GetType("Stea" + "mworks.Data.Publi" + "shedFileId, Face" + "punch.Stea" + "mworks.Win64, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null");
        var fileId = Activator.CreateInstance(publishedFileIdType);
        publishedFileIdType.GetField("Value").SetValue(fileId, workshopItemId);
        var queryFileAsyncMethod = sUgcType.GetMethod("QueryFileAsync", new Type[] { publishedFileIdType });
        var task = (Task)queryFileAsyncMethod.Invoke(null, new object[] { fileId });
        await task;
        var resultProperty = task.GetType().GetProperty("Result");
        var item = resultProperty.GetValue(task);
        var voteMethod = item.GetType().GetMethod("Vote", new Type[] { typeof(bool) });
        var voteTask = (Task)voteMethod.Invoke(item, new object[] { true });
        await voteTask;
    }
    public static bool HaveBeenSelected(this GameObject obj) => SelectionController.Main.SelectedObjects.Contains(obj.GetPhysicalBehaviour());
    public static float average(this Vector2 vector2) => (Mathf.Abs(vector2.x) + Mathf.Abs(vector2.y)) / 2f;
    public static float average(this Vector3 vector3) => (Mathf.Abs(vector3.x) + Mathf.Abs(vector3.y)) / 2f;
    public static Vector3 GetAveragePosition<T>(this IEnumerable<T> children) where T : Component => children.Select(t => t.transform.position).Aggregate(Vector3.zero, (acc, pos) => acc + pos) / children.Count();
    public static Vector3 GetAveragePosition(this IEnumerable<GameObject> children) => children.Select(t => t.transform.position).Aggregate(Vector3.zero, (acc, pos) => acc + pos) / children.Count();
    public static Vector3 GetVectorAverage(this IEnumerable<Vector3> children) => children.Aggregate(Vector3.zero, (acc, pos) => acc + pos) / children.Count();
    public static Vector3 GetAveragePosition(this Transform transform) => transform.Cast<Transform>().Select(t => t.position).Aggregate(Vector3.zero, (acc, pos) => acc + pos) / transform.childCount;


    public static Vector2 ToVector2(this Quaternion rotation) => new Vector2(Mathf.Cos(rotation.eulerAngles.z * Mathf.Deg2Rad), Mathf.Sin(rotation.eulerAngles.z * Mathf.Deg2Rad));

    public static JointAngleLimits2D ToLimits(this Vector2 vector2)
    {
        JointAngleLimits2D limits = new JointAngleLimits2D();
        limits.min = vector2.x;
        limits.max = vector2.y;
        return limits;
    }
    public static void AccurateFixColliders(this GameObject self, float Accurate = 10f, bool RefreshOutline = true)
    {
        self.transform.localScale *= Accurate;
        self.FixColliders();
        if (RefreshOutline)
            self.GetComponent<PhysicalBehaviour>().RefreshOutline();
        self.transform.localScale /= Accurate;
    }
    public static JointAngleLimits2D Reverse(this JointAngleLimits2D limits)
    {
        JointAngleLimits2D _limits = new JointAngleLimits2D();
        _limits.max = 0f - limits.min;
        _limits.min = 0f - limits.max;
        return _limits;
    }

    public static Vector2 ToVector2(this JointAngleLimits2D limits) => new Vector2(limits.min, limits.max);

    public static Vector3 GetAverageScale(this Transform transform) => transform.Cast<Transform>().Select(t => t.localScale).Aggregate(Vector3.zero, (acc, size) => acc + size) / transform.childCount;
    public static Quaternion RotateTo(this Transform transform, Transform target) => Quaternion.Euler(0, 0, Mathf.Atan2(target.position.y - transform.position.y, target.position.x - transform.position.x) * Mathf.Rad2Deg);
    public static Quaternion RotateTo(this Vector3 position, Vector3 targetPosition) => Quaternion.Euler(0, 0, Mathf.Atan2(targetPosition.y - position.y, targetPosition.x - position.x) * Mathf.Rad2Deg);
    public static Vector2 GetDirection(this Transform transform, Vector2 barrelDirection) => transform.TransformDirection(barrelDirection) * transform.localScale.x;
    public static float GetAverageImpulse(this Collision2D collision) => Utils.GetAverageImpulse(collision.contacts, collision.contacts.Length);

    public static T GetPrivate<T>(this object instance, string name) => (T)instance.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance).GetValue(instance);
    public static T GetRuntimePrivate<T>(this object instance, string name) => (T)instance.GetType().GetRuntimeFields().FirstOrDefault(f => f.Name == name)?.GetValue(instance);
    public static T GetPrivateProperty<T>(this object instance, string name) => (T)(instance.GetType().GetProperty(name, BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public) ?? null).GetValue(instance);
    public static void SetPrivate<T>(this object instance, string name, T Value) => instance.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance).SetValue(instance, Value);
    public static void SetRuntimePrivate<T>(this object instance, string name, T Value) => instance.GetType().GetRuntimeFields().FirstOrDefault(f => f.Name == name)?.SetValue(instance, Value);
    public static void SetRuntimePrivateProp<T>(this object instance, string name, T value) => instance.GetType().GetRuntimeProperty(name)?.SetMethod?.Invoke(instance, new object[] { value });
    public static void SetPrivateProp<T>(this object instance, string name, T value) => TryCatchAction(() => instance.GetType().GetProperty(name, BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public)?.SetValue(instance, value));
    private static bool ShouldSerializeMember(MemberInfo member) => !(member.GetCustomAttribute<ObsoleteAttribute>() != null || member.GetCustomAttribute<NonSerializedAttribute>() != null || member.GetCustomAttribute<SkipSerialisationAttribute>() != null);
    public static void CopyProperties<T>(this T source, T destination, params string[] names) where T : Component
    {
        if (source == null || destination == null)
            throw new ArgumentNullException("source or destination cannot be null");
        foreach (var property in typeof(T).GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
        {
            if (names.Contains(property.Name) || !ShouldSerializeMember(property))
                continue;
            if (property.CanRead && property.CanWrite)
                property.SetValue(destination, property.GetValue(source));
            else if (!property.CanWrite)
                destination.SetPrivateProp(property.Name, source.GetPrivateProperty<object>(property.Name));
        }

        foreach (var field in typeof(T).GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
        {
            if (!ShouldSerializeMember(field) || names.Contains(field.Name))
                continue;
            field.SetValue(destination, field.GetValue(source));
        }
    }

    public static object InvokePrivateMethod(this object obj, string methodName, params object[] args) => obj.GetType().GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Instance).Invoke(obj, args);
    public static T CreateInstance<T>() where T : class => (T)Activator.CreateInstance(typeof(T));

    public static GameObject FindPrefab(string name) => Resources.Load<GameObject>(name);
    public static string GenerateRandomString(int length) => new string(Enumerable.Repeat("abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789", length).Select(s => s[Random.Next(s.Length)]).ToArray());/*!@#$%^&*()-_+={}[]|\\:;\"'<>,.?*/
    public static void OpenLink(string url) => Type.GetType("UnityEngine.Application, UnityEngine.CoreModule").GetMethod("OpenURL", BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static).Invoke(null, new object[] { url });
    public static bool HasComponent<T>(this Component instance) where T : Component => instance != null && instance.GetComponent<T>() != null;

    public static LimbBehaviour[] GetBiggestPart(PersonBehaviour personBehaviour)
    {
        List<List<LimbBehaviour>> parts = new List<List<LimbBehaviour>>();
        List<LimbBehaviour> visited = new List<LimbBehaviour>();
        void ExploreLimb(LimbBehaviour limb, ref List<LimbBehaviour> part)
        {
            if (visited.Contains(limb) || !limb.gameObject.activeSelf) return;
            visited.Add(limb);
            part.Add(limb);
            foreach (LimbBehaviour connected in limb.ConnectedLimbs)
                if (connected.NodeBehaviour.IsConnectedTo(limb.NodeBehaviour))
                    ExploreLimb(connected, ref part);
        }

        foreach (LimbBehaviour limb in personBehaviour.Limbs)
            if (!visited.Contains(limb) && limb.gameObject.activeSelf)
            {
                List<LimbBehaviour> part = new List<LimbBehaviour>();
                ExploreLimb(limb, ref part);
                parts.Add(part);
            }
        return parts.OrderByDescending(p => p.Count).FirstOrDefault()?.ToArray() ?? new LimbBehaviour[0];
    }

    public static void SetSkin(this PersonBehaviour person, Texture2D skin, Texture2D flesh, Texture2D bone) => person.SetBodyTextures(skin, flesh, bone, (skin.width / 18));

    public static LimbBehaviour GetHead(this PersonBehaviour Person) => Head.GetValue(Person) as LimbBehaviour;
    public static List<LimbBehaviour> GetLimbs(this GameObject i)
    {
        var aL = i.transform.root.GetComponentsInChildren<LimbBehaviour>().ToList();
        return i.transform.root.TryGetComponent(out PersonBehaviour p) ? aL.Concat(p.Limbs.Where(l => l != null)).Distinct().ToList() : aL;
    }
    public static PersonBehaviour GetPerson(this GameObject Instance) => Instance.transform.root.gameObject.TryGetComponent(out PersonBehaviour Person) ? Person : null;
    public static GripBehaviour GetGrip(this GameObject Instance) => Instance.TryGetComponent(out GripBehaviour Grip) ? Grip : null;
    public static List<GameObject> GetChildren(this GameObject Instance) => Instance.transform.Cast<Transform>().Select(t => t.gameObject).ToList();
    public static Collider2D[] GetPersonColliders(this PersonBehaviour person, bool withGrips = true)
    {
        var personColliders = person.GetColliders();
        return withGrips ? personColliders.Concat(person.GetComponentsInChildren<GripBehaviour>().Where(g => g.CurrentlyHolding != null && g.CurrentlyHolding.GetComponent<Collider2D>() != null).Select(g => g.CurrentlyHolding.GetComponent<Collider2D>())).ToArray() : personColliders;
    }
    public static Collider2D[] GetColliders<T>(this T Instance) where T : Component => Instance.transform.root.GetComponentsInChildren<Collider2D>();
    public static Collider2D[] GetColliders(this GameObject Instance) => Instance.transform.root.GetComponentsInChildren<Collider2D>();
    public static bool Check(Type t) => t.Assembly.GetName().Name is var n && (n == "Assembly-CSharp" || n.StartsWith("Unity"));
    public static void SetHaed(this PersonBehaviour Person, LimbBehaviour newHead) => Head.SetValue(Person, newHead);
    public static void SetNode(this LimbBehaviour Limb, bool enabled) => Node.SetValue(Limb.NodeBehaviour, enabled);
    public static void IgnoreCollision(this GameObject[] gameObjects, bool ignore = true) => gameObjects.ForEach(gameObject => gameObjects.Where(gameObjectFilter => gameObjectFilter != gameObject).ForEach(gameObjectToCollision => Physics2D.IgnoreCollision(gameObject.GetComponent<Collider2D>(), gameObjectToCollision.GetComponent<Collider2D>(), ignore)));

    public static T[] FindTypesInWorld<T>() => Resources.FindObjectsOfTypeAll(typeof(T)) as T[];
    public static Material GetMaterial(string name) => FindTypesInWorld<Material>().FirstOrDefault(shader => shader.name == name);

    public static void ForEach<T>(this object obj, Action<T> action) where T : Component => (obj is GameObject ? (GameObject)obj : ((Component)obj).gameObject).GetComponentsInChildren<T>().ForEach(action);
    public static IEnumerable<T> ForEach<T>(this IEnumerable<T> Source, Action<T> action)
    {
        Source.ToList().ForEach(action);
        return Source;
    }

    public static int GetLayerMask(params int[] layerIndexes)
    {
        int mask = 0;
        foreach (int layerIndex in layerIndexes)
            mask |= (1 << layerIndex);
        return mask;
    }

    public static ItemButtonBehaviour GetItemButtonBehaviour(SpawnableAsset spawnableAsset) => CatalogBehaviour.Main.GetPrivate<List<ItemButtonBehaviour>>("items").FirstOrDefault(itemButton => itemButton.Item == spawnableAsset);
    public static PhysicalProperties GetPhysicalProperties(this GameObject gameObject) => gameObject.TryGetComponent(out PhysicalBehaviour physicalBehaviour) ? physicalBehaviour.Properties : null;
    public static PhysicalBehaviour GetPhysicalBehaviour(this GameObject gameObject) => gameObject.GetComponent<PhysicalBehaviour>();
    public static Rigidbody2D GetRigidbody(this GameObject gameObject) => gameObject.GetComponent<Rigidbody2D>();
    public static SpriteRenderer GetSpriteRenderer(this GameObject gameObject) => gameObject.GetComponent<SpriteRenderer>();

    public static void RemoveAll<TKey, TValue>(this Dictionary<TKey, TValue> dictionary, Predicate<KeyValuePair<TKey, TValue>> match)
    {
        if (dictionary == null) throw new ArgumentNullException(nameof(dictionary));
        if (match == null) throw new ArgumentNullException(nameof(match));

        var keysToRemove = new List<TKey>();
        foreach (var kvp in dictionary)
            if (match(kvp)) keysToRemove.Add(kvp.Key);

        foreach (var key in keysToRemove)
            dictionary.Remove(key);
    }

    public static ContextMenuButton CreateButton(Func<bool> Condition, string Text, Action action = null) => new ContextMenuButton(Condition, Text, Text, Text, new UnityAction[] { delegate () { if (Condition()) action?.Invoke(); } });
    public static ContextMenuButton CreateButton(Func<bool> Condition, string Desc, Func<string> Text, Action action = null) => new ContextMenuButton(Condition, Desc, Text, Desc, new UnityAction[] { delegate () { if (Condition()) action?.Invoke(); } });

    public static Texture2D ScaleTexture(this Texture2D source, int scaleFactor)
    {
        int newWidth = Mathf.RoundToInt(source.width * scaleFactor), newHeight = Mathf.RoundToInt(source.height * scaleFactor);

        Texture2D scaledTexture = new Texture2D(newWidth, newHeight);

        for (int x = 0; x < newWidth; x++)
            for (int y = 0; y < newHeight; y++)
                scaledTexture.SetPixel(x, y, source.GetPixel(Mathf.FloorToInt(x / scaleFactor), Mathf.FloorToInt(y / scaleFactor)));

        scaledTexture.filterMode = FilterMode.Point;
        scaledTexture.Apply();

        return scaledTexture;
    }
    public static Texture2D EmptyTexture(this Texture originalTexture)
    {
        if (originalTexture == null || !(originalTexture is Texture2D)) return null;

        Texture2D originalTexture2D = (Texture2D)originalTexture;
        Texture2D emptyTexture = new Texture2D(originalTexture2D.width, originalTexture2D.height);
        emptyTexture.SetPixels(new Color[emptyTexture.width * emptyTexture.height]);
        emptyTexture.filterMode = FilterMode.Point;
        emptyTexture.Apply();

        return emptyTexture;
    }
    public static Texture2D Readable(this Texture2D source)
    {
        RenderTexture renderTex = RenderTexture.GetTemporary(
                    source.width,
                    source.height,
                    0,
                    RenderTextureFormat.Default,
                    RenderTextureReadWrite.Linear);

        Graphics.Blit(source, renderTex);
        RenderTexture previous = RenderTexture.active;
        RenderTexture.active = renderTex;
        Texture2D readableText = new Texture2D(source.width, source.height);
        readableText.ReadPixels(new Rect(0, 0, renderTex.width, renderTex.height), 0, 0);
        readableText.Apply();
        RenderTexture.active = previous;
        RenderTexture.ReleaseTemporary(renderTex);
        return readableText;
    }

    public static Sprite ApplyColorizeEffect(this Sprite sprite, Color targetColor, float factor = 1.0f)
    {
        var tex = sprite.texture;
        var newTex = new Texture2D(tex.width, tex.height, tex.format, tex.mipmapCount > 1);
        var pixels = tex.GetPixels();

        for (int i = 0; i < pixels.Length; i++)
        {
            float r = Mathf.Lerp(pixels[i].r, targetColor.r, factor);
            float g = Mathf.Lerp(pixels[i].g, targetColor.g, factor);
            float b = Mathf.Lerp(pixels[i].b, targetColor.b, factor);

            pixels[i] = new Color(r, g, b, pixels[i].a);
        }

        newTex.filterMode = FilterMode.Point;
        newTex.SetPixels(pixels);
        newTex.Apply();
        return Sprite.Create(newTex, sprite.rect, sprite.pivot / sprite.rect.size, sprite.pixelsPerUnit);
    }

    public static Sprite LoadSprite(string path, bool withoutIncludeModPath = false, FilterMode filterMode = FilterMode.Point, float pixels = 35) => Utils.LoadSprite((withoutIncludeModPath == true ? path : modPath + "\\" + path), filterMode, pixels, false);
    public static (Sprite sprite, Texture2D texture) GetSlicedSprite(this Texture2D texture, Rect rect, float pixelsPerUnit = 35)
    {
        Texture2D clonedTexture = new Texture2D(Mathf.FloorToInt(rect.width), Mathf.FloorToInt(rect.height), texture.format, texture.mipmapCount > 1);

        Color[] pixels = texture.GetPixels(
            Mathf.FloorToInt(rect.x),
            Mathf.FloorToInt(rect.y),
            Mathf.FloorToInt(rect.width),
            Mathf.FloorToInt(rect.height)
        );

        clonedTexture.filterMode = FilterMode.Point;
        clonedTexture.SetPixels(pixels);
        clonedTexture.Apply();

        return (Sprite.Create(clonedTexture, new Rect(0, 0, rect.width, rect.height), Vector2.one * 0.5f, pixelsPerUnit), clonedTexture);
    }

    public static Texture2D LoadTexture(string path) => Utils.LoadTexture(modPath + "\\" + path);
    public static AudioClip LoadSound(string path) => Utils.FileToAudioClip(modPath + "\\" + path);

    public static LimbBehaviour GetNearestLimb(this Vector2 vector, PersonBehaviour exclude = null)
    {
        float ClosestDest = Mathf.Infinity;
        LimbBehaviour Target = null;
        foreach (LimbBehaviour targets in LimbBehaviourManager.Limbs)
        {
            if (targets.isActiveAndEnabled && targets.gameObject.activeSelf && targets.HasBrain && targets.IsConsideredAlive && targets.Person && targets.Person.IsAlive() && targets.Person != exclude)
            {
                float distanceToEnemy = (targets.transform.position - (Vector3)vector).sqrMagnitude;
                if (distanceToEnemy < ClosestDest)
                {
                    ClosestDest = distanceToEnemy;
                    Target = targets;
                }
            }
        }
        return Target;
    }

    public static List<Vector2> GetColliderGridPoints(this GameObject gameObject)
    {
        List<Vector2> list = new List<Vector2>();
        Collider2D[] colliders = gameObject.GetComponents<Collider2D>().Where(c => !c.isTrigger).ToArray();

        var bounds = new Bounds(gameObject.transform.position, Vector3.zero);
        foreach (var col in colliders)
            bounds.Encapsulate(col.bounds);
        bounds.size *= 0.999f;

        int gridSize = Mathf.Max(1, 4);
        Vector2 gridSpacing = bounds.size / gridSize;
        Vector2 gridMin = bounds.min;
        bool IsInsideOf(Collider2D[] colls, Vector2 globalPoint) => colls.Any(c => c.OverlapPoint(globalPoint));

        for (int j = 0; j <= gridSize; j++)
            for (int k = 0; k <= gridSize; k++)
            {
                Vector2 pos = gridMin + new Vector2(k * gridSpacing.x, j * gridSpacing.y);
                if (list.All(p => Vector2.Distance(p, pos) >= 0.025f) && IsInsideOf(colliders, pos))
                    list.Add(gameObject.transform.InverseTransformPoint(pos));
            }

        foreach (var col in colliders)
        {
            if (col is PolygonCollider2D poly)
                list.AddRange(poly.points.Select(p => p + poly.offset));
            else if (col is BoxCollider2D box)
            {
                Vector2 halfSize = box.size / 2f;
                list.AddRange(new[] { new Vector2(halfSize.x, halfSize.y), new Vector2(-halfSize.x, halfSize.y), new Vector2(-halfSize.x, -halfSize.y), new Vector2(halfSize.x, -halfSize.y) }.Select(p => p + box.offset));
            }
            else if (col is CircleCollider2D circle)
            {
                int points = Mathf.Clamp(Mathf.CeilToInt(6.2831855f * circle.radius / 0.25f), 6, 32);
                list.AddRange(Enumerable.Range(0, points).Select(i => new Vector2(Mathf.Cos(6.2831855f * i / points), Mathf.Sin(6.2831855f * i / points)) * circle.radius + circle.offset));
            }
        }

        return list;
    }

    public static bool BeingHeldByGripper(this PhysicalBehaviour phys) => UnityEngine.Object.FindObjectsOfType<GripBehaviour>().FirstOrDefault(x => x.CurrentlyHolding == phys) != null;

    public static void AdvancedSpriteChange(this SpriteRenderer spriteRenderer, Sprite sprite, bool refresh = true, bool fixColliders = true)
    {
        spriteRenderer.sprite = sprite;
        if (spriteRenderer.transform.parent && spriteRenderer.transform.parent.TryGetComponent(out SpriteRenderer Pspriterender))
        {
            spriteRenderer.sortingLayerName = Pspriterender.sortingLayerName;
            spriteRenderer.sortingOrder = Pspriterender.sortingOrder + 1;
        }
        if (fixColliders) spriteRenderer.gameObject.FixColliders();
        if (spriteRenderer.TryGetComponent(out PhysicalBehaviour Phys))
        {
            Phys.RecalculateMassBasedOnSize();
            if (refresh) Phys.RefreshOutline();
        }
    }

    public static void AdvancedSpriteChange(this GameObject Instance, Sprite Sprite, bool Refresh = false, bool Fixcoll = true)
    {
        SpriteRenderer spriterender = Instance.GetComponent<SpriteRenderer>();
        spriterender.sprite = Sprite;
        if (Instance.transform.parent && Instance.transform.parent.TryGetComponent(out SpriteRenderer Pspriterender))
        {
            spriterender.sortingLayerName = Pspriterender.sortingLayerName;
            spriterender.sortingOrder = Pspriterender.sortingOrder + 1;
        }
        if (Refresh) Instance.GetComponent<PhysicalBehaviour>().RefreshOutline();
        if (Fixcoll) Instance.FixColliders();
    }

    public static void ChangeSpecificLimbSprite(this LimbBehaviour limbBehaviour, Sprite skin, Texture2D flash, Texture2D bone, Texture2D damage)
    {
        var limbSpriteRenderer = limbBehaviour.GetComponent<SpriteRenderer>();
        limbSpriteRenderer.sprite = skin;
        limbSpriteRenderer.material.SetTexture("_FleshTex", flash);
        limbSpriteRenderer.material.SetTexture("_BoneTex", bone);
        limbSpriteRenderer.material.SetTexture("_DamageTex", damage);
    }
    public static void ChangeSpecificLimbSprite(this SpriteRenderer spriteRenderer, Sprite skin, Texture2D flash, Texture2D bone, Texture2D damage, (bool flashEnabled, bool boneEnabled, bool damageEnabled) textureEnabled)
    {
        spriteRenderer.sprite = skin;
        spriteRenderer.material.SetTexture("_FleshTex", textureEnabled.flashEnabled ? flash : flash.EmptyTexture());
        spriteRenderer.material.SetTexture("_BoneTex", textureEnabled.boneEnabled ? bone : bone.EmptyTexture());
        spriteRenderer.material.SetTexture("_DamageTex", textureEnabled.damageEnabled ? damage : damage.EmptyTexture());
    }

    public static Material DeepCloneMaterial(this Material originalMaterial)
    {
        Material newMaterial = new Material(originalMaterial.shader);
        newMaterial.CopyPropertiesFromMaterial(originalMaterial);
        return newMaterial;
    }

    public static RenderTexture CaptureDoGRenderTexture(Camera cam, Material dogMat, Vector3 targetWorldPos, int downSample = 2)
    {
        if (cam == null || dogMat == null) return null;

        // 降采样计算分辨率
        int width = Screen.width / downSample;
        int height = Screen.height / downSample;

        // 申请两张临时的 RenderTexture
        RenderTexture source = RenderTexture.GetTemporary(width, height, 24);
        RenderTexture destination = RenderTexture.GetTemporary(width, height, 0);

        cam.targetTexture = source;
        cam.Render();
        cam.targetTexture = null;

        Vector3 screenPoint = cam.WorldToScreenPoint(targetWorldPos);
        Vector4 blurCenter = new Vector4(screenPoint.x / Screen.width, screenPoint.y / Screen.height, 0, 0);
        dogMat.SetVector("_BlurCenter", blurCenter);

        Graphics.Blit(source, destination, dogMat);

        RenderTexture.ReleaseTemporary(source);

        return destination;
    }

    public static AudioSource CreateAudioSource(this GameObject gameObject, float minDistance = 5, float maxDistance = 30, bool add = true)
    {
        var audioSource = gameObject.AddComponent<AudioSource>();
        audioSource.spatialBlend = 1;
        audioSource.minDistance = minDistance;
        audioSource.maxDistance = maxDistance;
        if (add)
            Global.main.AddAudioSource(audioSource);
        return audioSource;
    }

    public static void DoOnce(string nameOperation, Action action)
    {
        if (PlayerPrefs.GetInt(nameOperation, 0) == 0)
        {
            PlayerPrefs.SetInt(nameOperation, 1);
            action.Invoke();
        }
    }

    public static void DestroyMult(this IEnumerable<UnityEngine.Object> Objs, bool Immediate = false) => Objs.ForEach(x =>
    {
        if (!Immediate)
            x?.Destroy();
        else
            UnityEngine.Object.DestroyImmediate(x);
    });

    public static void AddMultipleComponents(this GameObject Instance, params Type[] Types) => Types.ForEach(c =>
    {
        if (!Instance.GetComponent(c)) Instance.AddComponent(c);
    });


    public static bool TryConvertToKeyCode(string input, out KeyCode mainKey, out KeyCode? modifierKey)
    {
        mainKey = KeyCode.None;
        modifierKey = null;

        if (string.IsNullOrWhiteSpace(input))
            return false;

        input = input.Trim();

        // 处理标点符号
        if (PunctuationMap.ContainsKey(input))
        {
            (mainKey, modifierKey) = PunctuationMap[input];
            return true;
        }

        if (int.TryParse(input, out int number) && number >= 0 && number <= 9)
        {
            mainKey = (KeyCode)Enum.Parse(typeof(KeyCode), $"Alpha{number}");
            return true;
        }

        string normalizedInput = char.ToUpper(input[0]) + input.Substring(1).ToLower();
        if (Enum.TryParse<KeyCode>(normalizedInput, out KeyCode result))
        {
            mainKey = result;
            return true;
        }

        return false;
    }

    public static PhysicalBehaviour InitializePhysicalComponent(this GameObject gameObject)
    {
        gameObject.layer = LayerMask.NameToLayer("Objects");
        gameObject.GetOrAddComponent<Rigidbody2D>();
        gameObject.GetOrAddComponent<SpriteRenderer>();
        gameObject.GetOrAddComponent<BoxCollider2D>();
        var physicalBehaviour = gameObject.AddComponent<PhysicalBehaviour>();
        physicalBehaviour.Properties = ModAPI.FindPhysicalProperties("Metal");
        physicalBehaviour.SpawnSpawnParticles = false;
        gameObject.AddComponent<AudioSourceTimeScaleBehaviour>();
        physicalBehaviour.OverrideShotSounds = Array.Empty<AudioClip>();
        physicalBehaviour.OverrideImpactSounds = Array.Empty<AudioClip>();
        return physicalBehaviour;
    }

    // idk why some feature didnt work well.
    public static IEnumerable<(GameObject gameObject, Vector2 pos, Vector2 dir)> CastRays(this (Vector2 origin, Vector2 direction) data, int rayCount = 16, float spreadAngle = 45f, float emitterSize = 0.5f, float range = 10f, List<Transform> except = null)
    {
        var buffer = new RaycastHit2D[1024];
        return Enumerable.Range(0, rayCount).SelectMany(i =>
        {
            float n = (float)i / ((float)rayCount - 1f);
            Vector2 dir = Quaternion.Euler(0f, 0f, n * spreadAngle - spreadAngle / 2f) * data.direction;
            return buffer.Take(Physics2D.RaycastNonAlloc(data.origin + Vector2.Perpendicular(data.direction) * (n * emitterSize - emitterSize / 2f), dir, buffer, range, mask)).Select(hit => (hit.transform.gameObject, hit.point, dir));
        }).Where(hit => hit.gameObject.GetRoot() != null && (except?.Contains(hit.gameObject.GetRoot().transform) != true)).GroupBy(hit => hit.gameObject).Select(group => group.First());
    }

    public static List<(GameObject gameObject, Vector2 pos, Vector2 dir)> CastRays(this (Vector2 origin, Vector2 direction) data, int rayCount = 16, float spreadAngle = 45f, float emitterSize = 0.5f, float range = 10f, params Transform[] except)
    {
        var results = new List<(GameObject gameObject, Vector2 pos, Vector2 dir)>();
        if (rayCount <= 0) return results;
        var processedObjects = new HashSet<GameObject>();
        var exceptSet = (except != null && except.Length > 0) ? new HashSet<Transform>(except) : null;
        var buffer = new RaycastHit2D[64];
        for (int i = 0; i < rayCount; i++)
        {
            float n = (rayCount == 1) ? 0.5f : (float)i / (rayCount - 1f);
            Vector2 dir = data.direction;
            if (spreadAngle > 0.01f)
            {
                float angle = n * spreadAngle - spreadAngle / 2f;
                dir = Quaternion.Euler(0, 0, angle) * data.direction;
            }
            Vector2 rayOrigin = data.origin;
            if (emitterSize > 0.01f)
            {
                Vector2 perpendicular = new Vector2(-data.direction.y, data.direction.x);
                float offset = n * emitterSize - emitterSize / 2f;
                rayOrigin += perpendicular * offset;
            }
            int hitCount = Physics2D.RaycastNonAlloc(rayOrigin, dir, buffer, range, mask);
            for (int j = 0; j < hitCount; j++)
            {
                RaycastHit2D hit = buffer[j];
                if (hit.transform == null) continue;
                Transform root = hit.transform.GetRoot();
                if (root == null) continue;
                if (exceptSet != null && exceptSet.Contains(root)) continue;
                var hitObj = hit.transform.gameObject;
                if (processedObjects.Contains(hitObj)) continue;
                processedObjects.Add(hitObj);
                results.Add((hitObj, hit.point, dir));
            }
        }
        return results;
    }

    public static (Vector2 point, float distance) GetDistanceFromGround(this GameObject gameObject)
    {
        var transform = gameObject.transform;
        var hit = Physics2D.Raycast(transform.position, Vector2.down, Inf, LayerMask.GetMask("Objects", "Bounds"));
        if (hit && !ReferenceEquals(hit.collider.transform.root, transform.root))
            return (hit.point, hit.distance);
        return (default, float.PositiveInfinity);
    }

    private static readonly RaycastHit2D[] _raycastBuffer = new RaycastHit2D[8];
    public static (Vector2 point, float distance) GetDistanceFromGround(this Vector2 currentPosition, params Transform[] expectedRoot)
    {
        int mask = LayerMask.GetMask("Objects", "Bounds");
        int hitCount = Physics2D.RaycastNonAlloc(currentPosition, Vector2.down, _raycastBuffer, float.PositiveInfinity, mask);
        if (hitCount == 0) return (default, float.PositiveInfinity);

        var excludeSet = (expectedRoot != null && expectedRoot.Length > 0) ? new HashSet<Transform>(expectedRoot) : null;

        for (int i = 0; i < hitCount; i++)
        {
            var hit = _raycastBuffer[i];
            if (hit.collider == null) continue;
            var hitRoot = hit.collider.transform.root;
            if (hitRoot == null) continue;
            if ((excludeSet != null && excludeSet.Contains(hitRoot)))
                continue;
            return (hit.point, hit.distance);
        }
        return (default, float.PositiveInfinity);
    }

    [RequireComponent(typeof(Dont))]
    [DisallowMultipleComponent]
    public class Dont : MonoBehaviour
    {
        private void OnDisable() => enabled = true;
        private void OnDestroy() => enabled = false;
    }

    public static Dont OnlyOneTimeAction(this GameObject gameObject, UnityAction Action)
    {
        if (!gameObject.GetComponent<Dont>()) Action.Invoke();
        return gameObject.GetOrAddComponent<Dont>();
    }

    public class InvokerOnStart : MonoBehaviour
    {
        public Action ActionForInvoke;

        public void Start()
        {
            ActionForInvoke.Invoke();
            Destroy(this);
        }
    }

    public class InvokerAfterDelay : MonoBehaviour
    {
        public Action ActionForInvoke = null;
        public float Delay = 0f;
        private void Start() => StartCoroutine(Delayer());
        private IEnumerator Delayer()
        {
            yield return new WaitForSeconds(Delay);
            ActionForInvoke.Invoke();
            Destroy(gameObject);
        }
    }

    public static void InvokeOnStart(this GameObject gameObject, Action action) => gameObject.AddComponent<InvokerOnStart>().ActionForInvoke = action;

    public static void ResetPose(this PersonBehaviour person) => person.OverridePoseIndex = -1;

    public static Vector2 BezierCurve(float t, Vector2 pointA, Vector2 pointB, Vector2 handleA, Vector2 handleB) => Mathf.Pow((1 - t), 3) * pointA + 3 * Mathf.Pow((1 - t), 2) * t * handleA + 3 * (1 - t) * Mathf.Pow(t, 2) * handleB + Mathf.Pow(t, 3) * pointB;

    public static NoCollide NoCollideAtoB(GameObject A, GameObject B)
    {
        var NoCollide = B.AddComponent<NoCollide>();
        NoCollide.NoCollideSetA = A.GetComponentsInChildren<Collider2D>();
        NoCollide.NoCollideSetB = B.GetComponentsInChildren<Collider2D>();
        return NoCollide;
    }

    public static void IgnoreCollision(IEnumerable<Collider2D> A, IEnumerable<Collider2D> B, bool ignore = true) => A.ForEach(a => B.Where(b => b != a).ForEach(b => Physics2D.IgnoreCollision(a, b, ignore)));

    public static void CreateAudioWhenUse(this GameObject obj, Func<bool> condition, AudioClip[] Clip)
    {
        var source = obj.AddComponent<AudioSource>();
        source.spread = 100f;
        source.volume = 8f;
        source.minDistance = 18f;
        source.spatialBlend = 1f;
        source.dopplerLevel = 0f;
        Global.main.AddAudioSource(source);
        obj.AddComponent<UseEventTrigger>().Action = () =>
        {
            if (condition())
                source.PlayOneShot(Clip.PickRandom());
        };
    }

    public static int CreatePose(this PersonBehaviour perosn, bool ShouldStandUpright, float UprightForceMultiplier, float AnimationSpeedMultiplier, float Rigidity, float[] rotations)
    {
        var pose = new RagdollPose { ShouldStandUpright = ShouldStandUpright, ForceMultiplier = 1f, State = PoseState.Sitting, UprightForceMultiplier = UprightForceMultiplier, AnimationSpeedMultiplier = AnimationSpeedMultiplier, Rigidity = Rigidity, ShouldStumble = false, Angles = new List<RagdollPose.LimbPose>() };

        pose.Angles.AddRange(Enumerable.Range(0, rotations.Length).Select(i => new RagdollPose.LimbPose(perosn.Limbs[i], rotations[i])));

        pose.ConstructDictionary();

        perosn.Poses.Add(pose);
        return perosn.Poses.Count - 1;
    }

    public static void CreateWhiteOverlay(float duration = 1f, bool glint = true)
    {
        GameObject canvasObj = new GameObject("WhiteOverlayCanvas");
        var whiteCanvas = canvasObj.AddComponent<Canvas>();
        canvasObj.AddComponent<CanvasScaler>();
        canvasObj.AddComponent<GraphicRaycaster>();

        whiteCanvas.renderMode = RenderMode.ScreenSpaceOverlay;
        whiteCanvas.sortingOrder = 9999;

        GameObject imageObj = new GameObject("WhiteOverlayImage");
        imageObj.transform.SetParent(canvasObj.transform);
        var whiteImage = imageObj.AddComponent<Image>();
        whiteImage.color = new Color(1, 1, 1, 1);
        whiteImage.raycastTarget = false;

        RectTransform rectTransform = whiteImage.GetComponent<RectTransform>();
        rectTransform.anchorMin = Vector2.zero;
        rectTransform.anchorMax = Vector2.one;
        rectTransform.offsetMin = Vector2.zero;
        rectTransform.offsetMax = Vector2.zero;

        Global.main.StartCoroutine(FadeOutWhiteOverlay(true));

        IEnumerator FadeOutWhiteOverlay(bool easeIn)
        {
            if (glint)
                yield return new WaitForSecondsRealtime(Mathf.Min(0.1f, duration));

            float elapsed = 0.0f;
            while (elapsed < duration)
            {
                elapsed += Time.unscaledDeltaTime; // Use unscaledDeltaTime to ignore time scale
                float t = elapsed / duration;
                t = easeIn ? t * t : 1 - (1 - t) * (1 - t); // 先慢后快（easeIn）或先快后慢
                whiteImage.color = new Color(1, 1, 1, 1 - t);
                yield return null;
            }

            whiteImage.color = new Color(1, 1, 1, 0);
            Destroy(whiteCanvas.gameObject);
        }

    }

    public static GameObject CreateOverlay(float duration = 1f, bool glint = true, RenderTexture customRT = null, Material invertMaterial = null)
    {
        GameObject canvasObj = new GameObject("WhiteOverlayCanvas");
        var whiteCanvas = canvasObj.AddComponent<Canvas>();
        canvasObj.AddComponent<CanvasScaler>();
        canvasObj.AddComponent<GraphicRaycaster>();

        whiteCanvas.renderMode = RenderMode.ScreenSpaceOverlay;
        whiteCanvas.sortingOrder = 9999;

        GameObject imageObj = new GameObject("WhiteOverlayImage");
        imageObj.transform.SetParent(canvasObj.transform);

        // 【核心修改 1】使用 RawImage 替代 Image
        var rawImage = imageObj.AddComponent<RawImage>();
        rawImage.raycastTarget = false;

        RectTransform rectTransform = rawImage.GetComponent<RectTransform>();
        rectTransform.anchorMin = Vector2.zero;
        rectTransform.anchorMax = Vector2.one;
        rectTransform.offsetMin = Vector2.zero;
        rectTransform.offsetMax = Vector2.zero;

        if (customRT != null)
        {
            rawImage.texture = customRT; // 直接赋值 RenderTexture
            rawImage.color = Color.white;
            AdjustImageFill(rectTransform, customRT);
        }
        else
        {
            rawImage.color = new Color(1, 1, 1, 1);
        }

        Global.main.StartCoroutine(FadeOutWhiteOverlay());

        IEnumerator FadeOutWhiteOverlay()
        {
            float flashDuration = 0.05f;
            yield return new WaitForSecondsRealtime(flashDuration);

            if (invertMaterial != null)
            {
                rawImage.material = invertMaterial;
            }
            yield return new WaitForSecondsRealtime(flashDuration);
            rawImage.material = null;
            if (glint)
                yield return new WaitForSeconds(duration > 0.1f ? 0.1f : duration);
            float elapsedTime = 0.0f;
            while (elapsedTime < duration)
            {
                elapsedTime += Time.deltaTime;
                float alpha = Mathf.Lerp(1.0f, 0.0f, elapsedTime / duration);
                rawImage.color = new Color(rawImage.color.r, rawImage.color.g, rawImage.color.b, alpha);
                yield return null;
            }

            rawImage.color = new Color(rawImage.color.r, rawImage.color.g, rawImage.color.b, 0);
            if (customRT != null)
            {
                RenderTexture.ReleaseTemporary(customRT);
            }
            Destroy(whiteCanvas.gameObject);
        }

        void AdjustImageFill(RectTransform _rectTransform, RenderTexture rt)
        {
            float texAspect = (float)rt.width / rt.height;
            float screenAspect = (float)Screen.width / Screen.height;

            if (texAspect > screenAspect)
                _rectTransform.localScale = new Vector3(1, texAspect / screenAspect, 1);
            else
                _rectTransform.localScale = new Vector3(screenAspect / texAspect, 1, 1);
        }

        return imageObj;
    }

    public static GameObject CreateOverlay(float duration = 1f, bool glint = true, Sprite customSprite = null)
    {
        GameObject canvasObj = new GameObject("WhiteOverlayCanvas");
        var whiteCanvas = canvasObj.AddComponent<Canvas>();
        canvasObj.AddComponent<CanvasScaler>();
        canvasObj.AddComponent<GraphicRaycaster>();

        whiteCanvas.renderMode = RenderMode.ScreenSpaceOverlay;
        whiteCanvas.sortingOrder = 9999;

        GameObject imageObj = new GameObject("WhiteOverlayImage");
        imageObj.transform.SetParent(canvasObj.transform);
        var whiteImage = imageObj.AddComponent<Image>();
        whiteImage.raycastTarget = false;

        RectTransform rectTransform = whiteImage.GetComponent<RectTransform>();
        rectTransform.anchorMin = Vector2.zero;
        rectTransform.anchorMax = Vector2.one;
        rectTransform.offsetMin = Vector2.zero;
        rectTransform.offsetMax = Vector2.zero;

        if (customSprite != null)
        {
            whiteImage.sprite = customSprite;
            whiteImage.color = Color.white;
            whiteImage.preserveAspect = false;
            AdjustImageFill(rectTransform, customSprite);
        }
        else
            whiteImage.color = new Color(1, 1, 1, 1);

        Global.main.StartCoroutine(FadeOutWhiteOverlay());

        IEnumerator FadeOutWhiteOverlay()
        {
            if (glint)
                yield return new WaitForSeconds(duration > 0.1f ? 0.1f : duration);

            float elapsedTime = 0.0f;
            while (elapsedTime < duration)
            {
                elapsedTime += Time.deltaTime;
                float alpha = Mathf.Lerp(1.0f, 0.0f, elapsedTime / duration);
                whiteImage.color = new Color(whiteImage.color.r, whiteImage.color.g, whiteImage.color.b, alpha);
                yield return null;
            }

            whiteImage.color = new Color(whiteImage.color.r, whiteImage.color.g, whiteImage.color.b, 0);
            Destroy(whiteCanvas.gameObject);
        }

        void AdjustImageFill(RectTransform _rectTransform, Sprite sprite)
        {
            float spriteAspect = sprite.rect.width / sprite.rect.height;
            float screenAspect = (float)Screen.width / Screen.height;

            if (spriteAspect > screenAspect)
                _rectTransform.localScale = new Vector3(1, spriteAspect / screenAspect, 1);
            else
                _rectTransform.localScale = new Vector3(screenAspect / spriteAspect, 1, 1);
        }

        return imageObj;
    }

    public static void MakePhysSharp(this PhysicalBehaviour Phys, SharpAxis SharpAxis)
    {
        PhysicalProperties Prop = UnityEngine.Object.Instantiate(Phys.Properties);
        Prop.Sharp = true;
        Prop.SharpAxes = new[] { SharpAxis };
        Phys.Properties = Prop;
    }

    public static bool IsInCone(Vector2 point, Vector2 position, Vector2 direction, float angle) => Vector2.Angle(direction, (point - position).normalized) < angle / 2f;

    public static void NoChildCollide(this GameObject instance)
    {
        var componentsInChildren = instance.GetComponentsInChildren<Collider2D>();
        componentsInChildren.ForEach(collider2D => componentsInChildren.ForEach(collider2D2 =>
        {
            if (collider2D && collider2D2 && collider2D != collider2D2)
                Physics2D.IgnoreCollision(collider2D, collider2D2);
        }));
    }
    public static void NoChildCollide(this GameObject instance, IEnumerable<Collider2D> with)
    {
        var componentsInChildren = instance.GetComponentsInChildren<Collider2D>().Concat(with).ToArray();
        componentsInChildren.ForEach(collider2D => componentsInChildren.ForEach(collider2D2 =>
        {
            if (collider2D && collider2D2 && collider2D != collider2D2)
                Physics2D.IgnoreCollision(collider2D, collider2D2);
        }));
    }

    public static void ChangeLimbHealthBar(this LimbBehaviour Limb, UnityAction<GameObject> Action = null)
    {
        var LimbBar = (GameObject)typeof(LimbBehaviour).GetField("myStatus", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(Limb);
        var Status = LimbBar.GetComponent<LimbStatusBehaviour>();
        Action?.Invoke(LimbBar);
    }

    public static void CreateUseEventTrigger(this GameObject obj, Action action) => obj.AddComponent<UseEventTrigger>().Action = action;

    public static void FixKnockout()
    {
        var limbs = LimbBehaviourManager.Limbs;
        if (limbs == null) return;
        int i = 0;
        while (i < limbs.Count)
        {
            var limb = limbs[i];
            if (limb == null)
            {
                RemoveAtUnordered(limbs, i);
                continue;
            }
            try
            {
                limb.ManagedFixedUpdate();
                limb.ManagedUpdate();
                i++;
            }
            catch
            {
                RemoveAtUnordered(limbs, i);
            }
        }
    }

    private static void RemoveAtUnordered<T>(List<T> list, int index)
    {
        int lastIndex = list.Count - 1;
        list[index] = list[lastIndex];
        list.RemoveAt(lastIndex);
    }

    public static Vector2 GetDirection(this Vector2 v, Vector2 other)
    {
        Vector2 direction = other - v;
        float x = direction.x == 0 ? 0 : direction.x / (Mathf.Abs(direction.x)), y = direction.y == 0 ? 0 : direction.y / (Mathf.Abs(direction.y));
        return new Vector2(x, y);
    }

    public static GameObject CreateGlobalButton(string text, UnityAction action = null)
    {
        var contextMenu = (ContextMenuBehaviour)typeof(ContextMenuBehaviour).GetField("Instance", BindingFlags.NonPublic | BindingFlags.Static).GetValue(null);
        var newButton = GameObject.Instantiate(contextMenu.ButtonPrefab, contextMenu.ButtonParent);
        newButton.name = text;
        var updater = newButton.GetComponent<TextUpdaterBehaviour>();
        if (updater != null)
            UnityEngine.Object.Destroy(updater);

        var go = newButton.transform.Find("Text").gameObject;
        var tmp = go.GetComponent<TextMeshProUGUI>();
        tmp.text = text;
        newButton.GetComponent<Button>().onClick.AddListener(() =>
        {
            contextMenu.Hide();
            action?.Invoke();
        });
        return newButton;
    }

    public static void SetSprite(this GameObject gameObject, Sprite sprite, bool fixColliders = true, string sortingLayerName = "Default", int sortingOrder = 4)
    {
        var renderer = gameObject.GetOrAddComponent<SpriteRenderer>();
        renderer.sprite = sprite;
        renderer.sortingLayerName = sortingLayerName;
        renderer.sortingOrder = sortingOrder;
        if (fixColliders)
            gameObject.FixColliders();
        if (gameObject.TryGetComponent(out PhysicalBehaviour physicalBehaviour))
            physicalBehaviour.RefreshOutline();
    }

    public static Texture2D FlipVertically(this Texture2D original)
    {
        int width = original.width, height = original.height;
        Texture2D m_texture = new Texture2D(width, height);
        for (int y = 0; y < height; y++)
        {
            Color[] row = original.GetPixels(0, y, width, 1);
            m_texture.SetPixels(0, height - 1 - y, width, 1, row);
        }
        m_texture.Apply();
        m_texture.filterMode = FilterMode.Point;
        return m_texture;
    }

    public static Rect GetWorldBounds(IEnumerable<SpriteRenderer> sprites)
    {
        float minX = float.MaxValue, minY = float.MaxValue;
        float maxX = float.MinValue, maxY = float.MinValue;

        foreach (var sr in sprites)
        {
            var b = sr.bounds;            // world‐space AABB
            minX = Mathf.Min(minX, b.min.x);
            minY = Mathf.Min(minY, b.min.y);
            maxX = Mathf.Max(maxX, b.max.x);
            maxY = Mathf.Max(maxY, b.max.y);
        }

        return Rect.MinMaxRect(minX, minY, maxX, maxY);
    }

    public static SpriteRenderer CreateSpriteObject(this Transform Parent, Vector3 LocalPosition, Vector3 LocalScale, Sprite Sprite, bool ShouldGlow = false, UnityAction<GameObject> Action = null)
    {
        GameObject SpriteObject = new GameObject($"SpriteObject_{Parent.name}_{UnityEngine.Random.Range(-99999, 99999)}", typeof(Optout));
        SpriteObject.transform.SetParent(Parent);
        SpriteObject.transform.localPosition = LocalPosition;
        SpriteObject.transform.rotation = SpriteObject.transform.parent.rotation;
        SpriteObject.transform.localScale = LocalScale;
        SpriteRenderer SpriteObjectRenderer = SpriteObject.GetOrAddComponent<SpriteRenderer>();
        if (Sprite != null)
            AdvancedSpriteChange(SpriteObjectRenderer, Sprite, false);
        if (ShouldGlow)
            SpriteObjectRenderer.sharedMaterial = ModAPI.FindMaterial("VeryBright");
        Action?.Invoke(SpriteObject);
        return SpriteObjectRenderer;
    }

    public static GameObject CreateChildSprite(this Transform parent, string name, Sprite sprite, UnityAction<GameObject> Action = null)
    {
        var obj = new GameObject($"SpriteObject_{name}_{UnityEngine.Random.Range(-99999, 99999)}", typeof(Optout));
        obj.transform.SetParent(parent);
        obj.transform.localPosition = new Vector3(0, 0f);
        obj.transform.rotation = parent.rotation;
        obj.transform.localScale = new Vector3(1f, 1f);
        var obj_sprite = obj.AddComponent<SpriteRenderer>();
        obj_sprite.sprite = sprite;
        var m_sprite = parent.gameObject.GetComponent<SpriteRenderer>();
        obj_sprite.sortingLayerName = m_sprite.sortingLayerName;
        obj_sprite.sortingOrder = m_sprite.sortingOrder + 1;
        Action?.Invoke(obj);
        return obj;
    }

    public static GameObject CreateGhost(Vector3 pos, Quaternion rot, Vector3 localScale, Sprite m_sprite, Color color, int sortingLayerID = default, float time = 10f, UnityAction<GameObject> action = null)
    {
        var ghost = new GameObject($"GhostObject_{UnityEngine.Random.Range(-99999, 99999)}");
        ghost.transform.position = pos;
        ghost.transform.rotation = rot;
        ghost.transform.localScale = localScale;
        var spriteRenderer = ghost.AddComponent<SpriteRenderer>();
        spriteRenderer.sprite = m_sprite;
        if (sortingLayerID != default)
            spriteRenderer.sortingLayerID = sortingLayerID;
        spriteRenderer.color = color;
        Global.main.StartCoroutine(ColorFade(spriteRenderer, time));
        IEnumerator ColorFade(SpriteRenderer render, float Delta)
        {
            var FinalColor = new Color(render.color.r, render.color.g, render.color.b, 0f);
            while (render.gameObject.activeInHierarchy && render.color != FinalColor)
            {
                render.color = Color.Lerp(render.color, FinalColor, Time.deltaTime * Delta);
                yield return new WaitForEndOfFrame();
            }
            UnityEngine.Object.Destroy(render.gameObject);
            yield return new WaitForEndOfFrame();
        }
        action?.Invoke(ghost);
        return ghost;
    }

    public static void DisintegrationEffect(Vector3 Pos, Color color, float speed)
    {
        GameObject DisintegrationObject = ModAPI.CreateParticleEffect("Disintegration", Pos);
        foreach (AudioSource AudioSource in DisintegrationObject.GetComponents<AudioSource>())
            UnityEngine.Object.Destroy(AudioSource);
        foreach (ParticleSystem ParticleSystem in DisintegrationObject.GetComponentsInChildren<ParticleSystem>())
        {
            var Main = ParticleSystem.main;
            Main.simulationSpeed *= speed;
            Main.startColor = color;
        }
    }

    public static void Integrate(this PhysicalBehaviour phys)
    {
        phys.gameObject.isStatic = false;
        phys.gameObject.ForEach<Collider2D>(col => phys.transform.root.gameObject.ForEach<Collider2D>(col2 => { col.enabled = true; Physics2D.IgnoreCollision(col, col2); }));
        phys.gameObject.ForEach<Renderer>(renderer => renderer.enabled = true);
        phys.gameObject.ForEach<Rigidbody2D>(rigidbody => rigidbody.simulated = true);
        if (phys.TryGetComponent(out BloodTankBehaviour component))
            component.enabled = true;
        phys.gameObject.SetActive(true);
        phys.isDisintegrated = false;
    }

    public static void PunchEffect(float Mult, float AddToResult, Transform transform, Collision2D collision)
    {
        PhysicalBehaviour Phys;
        if (!Global.main.PhysicalObjectsInWorldByTransform.TryGetValue(collision.transform, out Phys))
            return;
        float num = 1.1f * Mathf.Pow(Mathf.Clamp(collision.relativeVelocity.magnitude, 8f, 1000f), 1.25f) * Mult + AddToResult;
        if (num == 0f)
            return;
        Vector2 Point = Phys.spriteRenderer.bounds.ClosestPoint(collision.GetContact(0).point);
        if (collision.collider.TryGetComponent(out LimbBehaviour limbBehaviour))
        {
            limbBehaviour.SkinMaterialHandler.AddDamagePoint(DamageType.Bullet, Point, num / limbBehaviour.transform.lossyScale.magnitude * 2f);
            limbBehaviour.Damage(num);
        }
        float num2 = 140f;
        if (num > 2100f)
            num2 = num / 15f;
        int num4 = (int)Math.Ceiling((double)(num / num2));
        float damage = num / (float)num4;
        Vector2 normal = (Point - (Vector2)transform.position).normalized;
        CameraShakeBehaviour.main.Shake(0.05f, Point, 1f);
        InvokeMultTime(num4, delegate (int i)
        {
            Phys.gameObject.SendMessage("Shot", new Shot(normal, Point, damage, false, null), SendMessageOptions.DontRequireReceiver);
        });
        void InvokeMultTime(int Count, Action<int> MulltAction)
        {
            for (int i = 0; i < Count; i++)
                if (MulltAction != null)
                    MulltAction(i);
        }
    }

    public static PolygonCollider2D CreateTightPolygonCollider(IEnumerable<SpriteRenderer> sprites, Transform parent = null)
    {
        if (sprites == null || !sprites.Any())
        {
            Debug.LogWarning("傳入的 SpriteRenderer 列表為空，無法創建碰撞箱。");
            return null;
        }

        List<Vector2> allWorldPoints = new List<Vector2>();
        List<Vector2> spriteShapePoints = new List<Vector2>();

        foreach (var sr in sprites)
        {
            if (sr == null || sr.sprite == null) continue;

            int shapeCount = sr.sprite.GetPhysicsShapeCount();
            for (int i = 0; i < shapeCount; i++)
            {
                sr.sprite.GetPhysicsShape(i, spriteShapePoints);
                foreach (var localPoint in spriteShapePoints)
                {
                    Vector2 worldPoint = sr.transform.TransformPoint(localPoint);
                    allWorldPoints.Add(worldPoint);
                }
            }
        }

        if (allWorldPoints.Count < 3)
        {
            Debug.LogWarning("有效的頂點總數少於3個，無法構成多邊形。");
            return null;
        }

        List<Vector2> hullPoints = GetConvexHull(allWorldPoints);

        GameObject colliderObject = new GameObject("Generated_TightCollider");
        if (parent != null)
        {
            colliderObject.transform.SetParent(parent);
        }

        Rect bounds = GetWorldBounds(sprites);
        colliderObject.transform.position = bounds.center;

        PolygonCollider2D polygonCollider = colliderObject.AddComponent<PolygonCollider2D>();

        Vector2[] localHullPoints = new Vector2[hullPoints.Count];
        for (int i = 0; i < hullPoints.Count; i++)
        {
            localHullPoints[i] = colliderObject.transform.InverseTransformPoint(hullPoints[i]);
        }

        polygonCollider.points = localHullPoints;

        return polygonCollider;
    }

    private static List<Vector2> GetConvexHull(List<Vector2> points)
    {
        if (points.Count <= 3)
            return new List<Vector2>(points);

        points.Sort((a, b) =>
            a.x == b.x ? a.y.CompareTo(b.y) : a.x.CompareTo(b.x));

        List<Vector2> upperHull = new List<Vector2>();
        List<Vector2> lowerHull = new List<Vector2>();

        for (int i = 0; i < points.Count; i++)
        {
            while (lowerHull.Count >= 2 &&
                   CrossProduct(lowerHull[lowerHull.Count - 2], lowerHull[lowerHull.Count - 1], points[i]) <= 0)
            {
                lowerHull.RemoveAt(lowerHull.Count - 1);
            }
            lowerHull.Add(points[i]);
        }

        for (int i = points.Count - 1; i >= 0; i--)
        {
            while (upperHull.Count >= 2 &&
                   CrossProduct(upperHull[upperHull.Count - 2], upperHull[upperHull.Count - 1], points[i]) <= 0)
            {
                upperHull.RemoveAt(upperHull.Count - 1);
            }
            upperHull.Add(points[i]);
        }

        lowerHull.RemoveAt(lowerHull.Count - 1);
        upperHull.RemoveAt(upperHull.Count - 1);

        List<Vector2> result = new List<Vector2>(lowerHull);
        result.AddRange(upperHull);

        return result;
    }

    private static float CrossProduct(Vector2 p1, Vector2 p2, Vector2 p3)
    {
        return (p2.x - p1.x) * (p3.y - p1.y) - (p2.y - p1.y) * (p3.x - p1.x);
    }

    public static float TotalMass(this GameObject obj) => obj.transform.root.GetComponentsInChildren<Rigidbody2D>().Sum(r => r.mass);

    public static void TryCatchAction(Action tryAction = null, Action catchAction = null)
    {
        try { tryAction?.Invoke(); }
        catch { catchAction?.Invoke(); }
    }

    public static GameObject InstantiatePrefab(SpawnableAsset Substitute, Vector3 pos, Quaternion rot, UnityAction<GameObject> afterSpawn = null)
    {
        var SpawnObject = UnityEngine.Object.Instantiate(Substitute.Prefab, pos, rot);
        SpawnObject.AddComponent<AudioSourceTimeScaleBehaviour>();
        SpawnObject.transform.name = Substitute.name;
        SpawnObject.GetOrAddComponent<SerialiseInstructions>().OriginalSpawnableAsset = Substitute;
        CatalogBehaviour.PerformMod(Substitute, SpawnObject);
        afterSpawn?.Invoke(SpawnObject);
        return SpawnObject;
    }

    private static Camera _cachedCamera;
    private const int SNAPSHOT_LAYER = 22;
    private static Vector3[] _corners = new Vector3[8];

    public static Vector2 GetLocalSize(Renderer r)
    {
        Bounds b = default;

        if (r is SpriteRenderer sr && sr.sprite != null)
            b = sr.sprite.bounds;
        else if (r.GetComponent<MeshFilter>() is MeshFilter mf && mf.sharedMesh != null)
            b = mf.sharedMesh.bounds;
        else
            return Vector2.one;

        return new Vector2(b.size.x, b.size.y);
    }

    private static Camera GetCachedCamera()
    {
        if (_cachedCamera) return _cachedCamera;

        var go = new GameObject("Global_Snapshot_Cam");
        go.hideFlags = HideFlags.HideAndDontSave;

        _cachedCamera = go.AddComponent<Camera>();
        _cachedCamera.enabled = false;

        var mainCam = Camera.main;
        if (mainCam)
        {
            _cachedCamera.CopyFrom(mainCam);
        }

        _cachedCamera.enabled = false;
        _cachedCamera.allowHDR = false;
        _cachedCamera.orthographic = true;
        _cachedCamera.cullingMask = 1 << SNAPSHOT_LAYER;
        _cachedCamera.clearFlags = CameraClearFlags.SolidColor;
        _cachedCamera.backgroundColor = Color.clear;
        _cachedCamera.useOcclusionCulling = false;
        _cachedCamera.allowDynamicResolution = false;

        if (Application.isPlaying)
            UnityEngine.Object.DontDestroyOnLoad(go);

        return _cachedCamera;
    }

    public static Texture2D CaptureSingleObject2D(this GameObject source, float pixelsPerUnit = 100f, float padding = 0.1f)
    {
        if (!source) return null;
        return CaptureMultipleObjects2D(new GameObject[] { source }, pixelsPerUnit, padding);
    }

    public static Texture2D CaptureMultipleObjects2D(this IEnumerable<GameObject> sources, float pixelsPerUnit = 100f, float padding = 0.1f)
    {
        if (sources == null) return null;

        List<Renderer> allRenderers = new List<Renderer>();
        UnityEngine.SceneManagement.Scene? targetScene = null;

        foreach (var source in sources)
        {
            if (!source) continue;
            if (targetScene == null) targetScene = source.scene;
            allRenderers.AddRange(source.GetComponentsInChildren<Renderer>());
        }

        if (allRenderers.Count == 0) return null;

        Bounds worldBounds = new Bounds(Vector3.zero, Vector3.zero);
        bool hasBounds = false;
        float detectedPPU = 0f;

        foreach (var r in allRenderers)
        {
            if (!r.enabled || !r.gameObject.activeInHierarchy) continue;
            if (r.name == "Outline") continue;
            if (r is SpriteRenderer srCheck && !srCheck.sprite) continue;
            Vector3 scale = r.transform.lossyScale;
            if (Mathf.Approximately(scale.x, 0f) || Mathf.Approximately(scale.y, 0f)) continue;
            if (r.sharedMaterial && r.sharedMaterial.name.Contains("LightSprite")) continue;
            if (r.material && r.material.name.Contains("LightSprite")) continue;
            if (r.GetComponent<Light>() != null) continue;
            if (r.GetComponent("LightSprite") != null) continue;
            if (r.name.Contains("LightSprite") || r.name.Contains("Light")) continue;
            if (r is ParticleSystemRenderer) continue;
            if (r is TrailRenderer) continue;

            bool useLocalCorners = false;
            Bounds localBounds = default;

            if (r is SpriteRenderer sr && sr.sprite) { localBounds = sr.sprite.bounds; useLocalCorners = true; if (sr.sprite.pixelsPerUnit > detectedPPU) detectedPPU = sr.sprite.pixelsPerUnit; }
            else if (r is SkinnedMeshRenderer smr) { localBounds = smr.localBounds; useLocalCorners = true; }
            else if (r is MeshRenderer && r.GetComponent<MeshFilter>() is MeshFilter mf && mf.sharedMesh) { localBounds = mf.sharedMesh.bounds; useLocalCorners = true; }

            if (useLocalCorners && localBounds.size != Vector3.zero)
            {
                Matrix4x4 localToWorld = r.transform.localToWorldMatrix;
                GetBoundsCorners(localBounds, _corners);

                for (int i = 0; i < 8; i++)
                {
                    Vector3 worldPt = localToWorld.MultiplyPoint3x4(_corners[i]);
                    if (!hasBounds) { worldBounds = new Bounds(worldPt, Vector3.zero); hasBounds = true; }
                    else { worldBounds.Encapsulate(worldPt); }
                }
            }
            else
            {
                if (r.bounds.size != Vector3.zero)
                {
                    if (!hasBounds) { worldBounds = new Bounds(r.bounds.center, r.bounds.size); hasBounds = true; }
                    else { worldBounds.Encapsulate(r.bounds); }
                }
            }
        }

        if (!hasBounds) return null;

        float effectivePPU = Mathf.Max(pixelsPerUnit, detectedPPU);
        float paddedSizeX = worldBounds.size.x * (1f + padding);
        float paddedSizeY = worldBounds.size.y * (1f + padding);

        int width = Mathf.Max(4, Mathf.CeilToInt(paddedSizeX * effectivePPU));
        int height = Mathf.Max(4, Mathf.CeilToInt(paddedSizeY * effectivePPU));

        Camera cam = GetCachedCamera();
        if (targetScene.HasValue) cam.scene = targetScene.Value;

        cam.aspect = (float)width / height;

        cam.orthographicSize = paddedSizeY / 2f;

        cam.transform.rotation = Quaternion.identity;
        cam.transform.position = worldBounds.center;

        float zExtent = worldBounds.extents.z + 10f;
        cam.nearClipPlane = -zExtent;
        cam.farClipPlane = zExtent;

        Dictionary<GameObject, int> layerBackup = new Dictionary<GameObject, int>();
        foreach (var r in allRenderers)
        {
            if (!r.enabled || !r.gameObject.activeInHierarchy) continue;
            if (r.name == "Outline") continue;
            if (r is SpriteRenderer srCheck2 && !srCheck2.sprite) continue;
            Vector3 s = r.transform.lossyScale;
            if (Mathf.Approximately(s.x, 0f) || Mathf.Approximately(s.y, 0f)) continue;
            if (r.sharedMaterial && r.sharedMaterial.name.Contains("LightSprite")) continue;
            if (r.material && r.material.name.Contains("LightSprite")) continue;
            if (r.GetComponent<Light>() != null) continue;
            if (r.GetComponent("LightSprite") != null) continue;
            if (r.name.Contains("LightSprite") || r.name.Contains("Light")) continue;
            if (r is ParticleSystemRenderer) continue;
            if (r is TrailRenderer) continue;
            if (!layerBackup.ContainsKey(r.gameObject))
            {
                layerBackup[r.gameObject] = r.gameObject.layer;
                r.gameObject.layer = SNAPSHOT_LAYER;
            }
        }

        int superSample = 4;
        int maxTexSize = SystemInfo.maxTextureSize;
        while (superSample > 1 && (width * superSample > maxTexSize || height * superSample > maxTexSize))
            superSample--;
        int rtWidth = width * superSample;
        int rtHeight = height * superSample;
        RenderTextureFormat rtFormat = cam.allowHDR ? RenderTextureFormat.DefaultHDR : RenderTextureFormat.ARGB32;
        RenderTexture rt = RenderTexture.GetTemporary(rtWidth, rtHeight, 24, rtFormat);
        rt.antiAliasing = 8;
        RenderTexture oldActive = RenderTexture.active;
        Texture2D resultTex = null;

        try
        {
            cam.targetTexture = rt;
            cam.Render();

            RenderTexture.active = rt;
            resultTex = new Texture2D(rtWidth, rtHeight, TextureFormat.RGBA32, false);
            resultTex.ReadPixels(new Rect(0, 0, rtWidth, rtHeight), 0, 0);
            resultTex.Apply(false, false);
        }
        catch
        {
            if (resultTex) UnityEngine.Object.Destroy(resultTex);
            return null;
        }
        finally
        {
            cam.targetTexture = null;
            RenderTexture.active = oldActive;
            RenderTexture.ReleaseTemporary(rt);

            foreach (var kvp in layerBackup)
            {
                if (kvp.Key) kvp.Key.layer = kvp.Value;
            }
        }

        return resultTex;
    }

    private static void GetBoundsCorners(Bounds b, Vector3[] corners)
    {
        Vector3 min = b.min;
        Vector3 max = b.max;
        corners[0] = new Vector3(min.x, min.y, min.z);
        corners[1] = new Vector3(min.x, min.y, max.z);
        corners[2] = new Vector3(min.x, max.y, min.z);
        corners[3] = new Vector3(min.x, max.y, max.z);
        corners[4] = new Vector3(max.x, min.y, min.z);
        corners[5] = new Vector3(max.x, min.y, max.z);
        corners[6] = new Vector3(max.x, max.y, min.z);
        corners[7] = new Vector3(max.x, max.y, max.z);
    }
    private static Bounds GetLocal2DBounds(Renderer src)
    {
        if (src is SpriteRenderer sr && sr.sprite != null)
        {
            Bounds b = sr.sprite.bounds;
            return new Bounds(
                b.center,
                new Vector3(b.size.x, b.size.y, 0f)
            );
        }

        MeshFilter mf = src.GetComponent<MeshFilter>();
        if (mf != null && mf.sharedMesh != null)
        {
            Bounds b = mf.sharedMesh.bounds;
            return new Bounds(
                b.center,
                new Vector3(b.size.x, b.size.y, 0f)
            );
        }

        return new Bounds(Vector3.zero, Vector3.one);
    }

    private static void SetLayerRecursively(Transform trans, int newLayer, Dictionary<Transform, int> backup)
    {
        backup[trans] = trans.gameObject.layer;
        trans.gameObject.layer = newLayer;
        foreach (Transform child in trans)
        {
            SetLayerRecursively(child, newLayer, backup);
        }
    }

    public static void CreateFolder(string path) => Type.GetType("System.IO.Directory").GetMethods().FirstOrDefault(method => method.Name == "CreateDirectory").Invoke(null, new[] { path });

    public static void InvokeOnObject<T>(this T @object, Action<T> action) where T : UnityEngine.Object => action.Invoke(@object);
}

#pragma warning restore CS0612
#pragma warning restore CS0618
