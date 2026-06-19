using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;
using UnityEngine.Events;
using FishUtility;

namespace FishUtility;

public class StabberFixer : MonoBehaviour
{
    public SharpAxis SharpAxis
    {
        set
        {
            needChangeSharpAxis = true;
            m_SharpAxis = value;
        }
    }
    private bool needChangeSharpAxis = false;
    private SharpAxis m_SharpAxis;
    private PhysicalBehaviour physicalBehaviour;
    public Action<BoxCollider2D> OnFixed;
    private void Start()
    {
        physicalBehaviour = gameObject.GetComponent<PhysicalBehaviour>();
        StartCoroutine(FixCor());
    }
    private IEnumerator FixCor()
    {
        yield return new WaitForEndOfFrame();
        if (needChangeSharpAxis)
        {
            var newProperties = Instantiate(physicalBehaviour.Properties);
            newProperties.name += "Changed";
            newProperties.Sharp = true;
            newProperties.SharpAxes = new SharpAxis[] { m_SharpAxis };
            physicalBehaviour.Properties = newProperties;
        }
        gameObject.GetComponents<Collider2D>().ForEach(c => c.Destroy());
        var boxCollider = gameObject.AddComponent<BoxCollider2D>();
        yield return new WaitForEndOfFrame();
        physicalBehaviour.ResetColliderArray();
        yield return new WaitForEndOfFrame();
        physicalBehaviour.BakeColliderGridPoints();
        OnFixed?.Invoke(boxCollider);
    }
}

public class BladeSharp : MonoBehaviour, Messages.IOnBeforeSerialise, Messages.IOnAfterDeserialise, Messages.IOnGripped, Messages.IOnDrop
{
    public class SoftConnection
    {
        [NonSerialized]
        public FrictionJoint2D joint;
        public PhysicalBehaviour phys;
        public Collider2D coll;
        [NonSerialized]
        public bool shouldBeDeleted;
        public SoftConnection(FrictionJoint2D joint, PhysicalBehaviour phys, Collider2D coll)
        {
            this.joint = joint;
            this.phys = phys;
            this.coll = coll;
            shouldBeDeleted = false;
        }
    }
    [SkipSerialisation]
    public PhysicalBehaviour PhysicalBehaviour;
    [SkipSerialisation]
    public List<Collider2D> SharpColliders = new List<Collider2D>();

    // MODIFICATION: Added a HashSet to efficiently ignore specified colliders.
    [SkipSerialisation]
    public HashSet<Collider2D> IgnoreColliders = new HashSet<Collider2D>();

    [SkipSerialisation]
    public float ConnectionStrength = 1600f;
    [SkipSerialisation]
    public float MinSpeed = 0.01f;
    [SkipSerialisation]
    public float MinSoftness = 0f;
    [SkipSerialisation]
    public Vector2 Tip = Vector2.up;
    [SkipSerialisation]
    public LayerMask LayerMask = 10752;
    [HideInInspector]
    public Guid[] SerialisableVictims = new Guid[0];
    private readonly Collider2D[] buffer = new Collider2D[16];
    private int bufferLength;
    public float MultDamage = 1f;
    public bool ShouldSlice = false;
    public bool SliceOnlyDeads = true;
    public bool NotCollideWithGrip = true;
    public bool SimulateCollision = true;
    public UnityAction<GameObject> OnLimb = null;
    public PersonBehaviour gripPerson = null;
    public float SliceChance { get => m_SliceChance; set => m_SliceChance = Mathf.Clamp(value, 0, 1); }
    public float m_SliceChance;
    [SkipSerialisation]
    public readonly Dictionary<PhysicalBehaviour, SoftConnection> softConnections = new Dictionary<PhysicalBehaviour, SoftConnection>();
    private void FixedUpdate()
    {
        bool flag = PhysicalBehaviour.rigidbody.GetRelativePointVelocity(Tip).magnitude > MinSpeed;
        ProcessSoftConnections(flag, flag ? 10f : ConnectionStrength);
    }
    private readonly HashSet<Collider2D> m_cachedPersonColliders = new HashSet<Collider2D>();
    private void ProcessSoftConnections(bool shouldSaw = true, float connectionStrength = 2f)
    {
        foreach (var connection in softConnections.Values)
        {
            connection.shouldBeDeleted = true;
        }

        // Cache grip-person colliders as HashSet once per frame, outside the sharpCollider loop
        m_cachedPersonColliders.Clear();
        if (gripPerson && NotCollideWithGrip)
        {
            var arr = gripPerson.GetPersonColliders();
            if (arr != null)
                m_cachedPersonColliders.UnionWith(arr);
        }
        bool hasPersonColliders = m_cachedPersonColliders.Count > 0;

        foreach (var sharpCollider in SharpColliders)
        {
            bufferLength = sharpCollider.OverlapCollider(new ContactFilter2D { layerMask = LayerMask, useLayerMask = true }, buffer);

            for (int i = 0; i < bufferLength; i++)
            {
                var collider2D = buffer[i];

                if (IgnoreColliders.Contains(collider2D) ||
                    collider2D.transform.root == transform.root ||
                    !Global.main.PhysicalObjectsInWorldByTransform.TryGetValue(collider2D.transform, out var value) ||
                    (hasPersonColliders && m_cachedPersonColliders.Contains(collider2D)))
                {
                    continue;
                }

                if (softConnections.TryGetValue(value, out var existingConnection))
                {
                    if (existingConnection.coll && existingConnection.joint && existingConnection.phys && existingConnection.phys.gameObject.activeInHierarchy)
                    {
                        existingConnection.shouldBeDeleted = false;
                        existingConnection.joint.anchor = existingConnection.phys.transform.InverseTransformPoint(GetHitPoint(collider2D, sharpCollider));
                        existingConnection.joint.maxForce = connectionStrength;
                        existingConnection.joint.maxTorque = connectionStrength;
                    }
                }
                else if (shouldSaw && value.Properties.Softness >= MinSoftness)
                {
                    SoftConnect(value, collider2D, connectionStrength, sharpCollider);
                }
            }
        }

        List<PhysicalBehaviour> keysToRemove = null;

        foreach (var kvp in softConnections)
        {
            var connection = kvp.Value;
            bool isInvalid = connection.shouldBeDeleted || !connection.phys || !connection.joint;
            if (!isInvalid)
            {
                if (!connection.phys.gameObject.activeInHierarchy)
                {
                    isInvalid = true;
                }
            }

            if (isInvalid)
            {
                if (keysToRemove == null)
                    keysToRemove = new List<PhysicalBehaviour>();

                keysToRemove.Add(kvp.Key);

                if (connection.coll && connection.joint && connection.joint.connectedBody)
                {
                    var connectedCollider = connection.joint.connectedBody.GetComponent<Collider2D>();
                    if (connectedCollider)
                        IgnoreCollisionStackController.IgnoreCollisionSubstituteMethod(connection.coll, connectedCollider, ignore: false);
                }

                if (connection.joint)
                    Destroy(connection.joint);
            }
        }

        if (keysToRemove != null)
        {
            foreach (var key in keysToRemove)
            {
                softConnections.Remove(key);
            }
        }
    }
    private void OnDisable() => OnDestroy();
    private void OnDestroy()
    {
        softConnections.ForEach(x => x.Value.joint.Destroy());
        softConnections.Clear();
    }
    public void OnBeforeSerialise() => SerialisableVictims = softConnections.Where(p => !p.Value.shouldBeDeleted && (bool)p.Value.phys && (bool)p.Value.joint && (bool)p.Value.coll).Select(p => p.Value.phys.GetComponent<SerialisableIdentity>()?.UniqueIdentity ?? default).ToArray();
    public void OnAfterDeserialise(List<GameObject> gameobjects)
    {
        var source = gameobjects.SelectMany(c => c.GetComponentsInChildren<SerialisableIdentity>());
        SerialisableVictims.ForEach(id =>
        {
            var serialisableIdentity = source.FirstOrDefault(s => s.UniqueIdentity == id);
            if (serialisableIdentity != null)
                if (serialisableIdentity.TryGetComponent(out PhysicalBehaviour component))
                {
                    var sharpCollider = SharpColliders.FirstOrDefault();
                    if (sharpCollider != null)
                        SoftConnect(component, component.GetComponent<Collider2D>(), 2f, sharpCollider);
                }
        });
    }
    private void SoftConnect(PhysicalBehaviour otherPhys, Collider2D coll, float connectionStrength, Collider2D sharpCollider)
    {
        if ((bool)otherPhys && (bool)coll)
        {
            Vector2 hitPoint = GetHitPoint(coll, sharpCollider);
            var frictionJoint2D = otherPhys.gameObject.AddComponent<FrictionJoint2D>();
            frictionJoint2D.autoConfigureConnectedAnchor = true;
            frictionJoint2D.enableCollision = true;
            frictionJoint2D.maxForce = connectionStrength;
            frictionJoint2D.connectedBody = PhysicalBehaviour.rigidbody;
            frictionJoint2D.maxTorque = connectionStrength;
            frictionJoint2D.anchor = otherPhys.transform.InverseTransformPoint(hitPoint);

            IgnoreCollisionStackController.IgnoreCollisionSubstituteMethod(coll, sharpCollider);

            var n = (transform.position - otherPhys.transform.position).normalized;
            var stabbing = new Stabbing(PhysicalBehaviour, otherPhys, n, hitPoint);
            otherPhys.SendMessage("Stabbed", stabbing, SendMessageOptions.DontRequireReceiver);
            otherPhys.SendMessage("Shot", new Shot(n, hitPoint, 75 * MultDamage), SendMessageOptions.DontRequireReceiver);
            otherPhys.SendMessage("Shot", new Shot(n, hitPoint, 75 * MultDamage), SendMessageOptions.DontRequireReceiver);
            if (otherPhys.gameObject.TryGetComponent(out LimbBehaviour limbBehaviour))
            {
                limbBehaviour.SkinMaterialHandler.AddDamagePoint(DamageType.Bullet, hitPoint, 60f * MultDamage / limbBehaviour.transform.lossyScale.magnitude);
            }

            if (ShouldSlice && UnityEngine.Random.value > 1 - SliceChance)
            {
                if (SliceOnlyDeads)
                    OnLimb?.Invoke(otherPhys.gameObject);
                else
                    otherPhys.SendMessage("Slice");
            }

            gameObject.SendMessage("Lodged", stabbing, SendMessageOptions.DontRequireReceiver);
            if (SimulateCollision)
                BroadcastCollisionEnter2D(coll, sharpCollider, otherPhys, n, hitPoint);
            softConnections.Add(otherPhys, new SoftConnection(frictionJoint2D, otherPhys, coll));
        }
    }
    private Vector2 GetHitPoint(Collider2D coll, Collider2D sharpCollider) => sharpCollider.ClosestPoint((Vector2)coll.ClosestPoint(transform.position));

    // --- Cached reflection for BroadcastCollisionEnter2D ---
    private static Type s_Collision2DType;
    private static FieldInfo s_fi_Collider, s_fi_OtherCollider, s_fi_Rigidbody, s_fi_OtherRigidbody, s_fi_RelativeVelocity, s_fi_Enabled, s_fi_ContactCount, s_fi_ReusedContacts;
    private static FieldInfo s_fi_cp_Point, s_fi_cp_Normal, s_fi_cp_RelativeVelocity, s_fi_cp_Collider, s_fi_cp_OtherCollider, s_fi_cp_Rigidbody, s_fi_cp_OtherRigidbody, s_fi_cp_Enabled;
    private static bool s_cached;

    private static void EnsureFieldsCached()
    {
        if (s_cached) return;
        var bf = BindingFlags.NonPublic | BindingFlags.Instance;
        s_Collision2DType = typeof(Collision2D);
        s_fi_Collider = s_Collision2DType.GetField("m_Collider", bf);
        s_fi_OtherCollider = s_Collision2DType.GetField("m_OtherCollider", bf);
        s_fi_Rigidbody = s_Collision2DType.GetField("m_Rigidbody", bf);
        s_fi_OtherRigidbody = s_Collision2DType.GetField("m_OtherRigidbody", bf);
        s_fi_RelativeVelocity = s_Collision2DType.GetField("m_RelativeVelocity", bf);
        s_fi_Enabled = s_Collision2DType.GetField("m_Enabled", bf);
        s_fi_ContactCount = s_Collision2DType.GetField("m_ContactCount", bf);
        s_fi_ReusedContacts = s_Collision2DType.GetField("m_ReusedContacts", bf);

        var cpType = typeof(ContactPoint2D);
        s_fi_cp_Point = cpType.GetField("m_Point", bf);
        s_fi_cp_Normal = cpType.GetField("m_Normal", bf);
        s_fi_cp_RelativeVelocity = cpType.GetField("m_RelativeVelocity", bf);
        s_fi_cp_Collider = cpType.GetField("m_Collider", bf);
        s_fi_cp_OtherCollider = cpType.GetField("m_OtherCollider", bf);
        s_fi_cp_Rigidbody = cpType.GetField("m_Rigidbody", bf);
        s_fi_cp_OtherRigidbody = cpType.GetField("m_OtherRigidbody", bf);
        s_fi_cp_Enabled = cpType.GetField("m_Enabled", bf);

        s_cached = true;
    }

    private void BroadcastCollisionEnter2D(Collider2D otherCollider, Collider2D sharpCollider, PhysicalBehaviour otherPhys, Vector2 normal, Vector2 hitPoint)
    {
        EnsureFieldsCached();
        try
        {
#pragma warning disable SYSLIB0050
            var collision = (Collision2D)System.Runtime.Serialization.FormatterServices.GetUninitializedObject(s_Collision2DType);
#pragma warning restore SYSLIB0050

            s_fi_Collider?.SetValue(collision, otherCollider.GetInstanceID());
            s_fi_OtherCollider?.SetValue(collision, sharpCollider.GetInstanceID());
            s_fi_Rigidbody?.SetValue(collision, otherPhys.rigidbody.GetInstanceID());
            s_fi_OtherRigidbody?.SetValue(collision, PhysicalBehaviour.rigidbody.GetInstanceID());

            var relVel = PhysicalBehaviour.rigidbody.GetRelativePointVelocity(hitPoint);
            s_fi_RelativeVelocity?.SetValue(collision, relVel);
            s_fi_Enabled?.SetValue(collision, 1);

            object boxedCp = new ContactPoint2D();
            s_fi_cp_Point?.SetValue(boxedCp, hitPoint);
            s_fi_cp_Normal?.SetValue(boxedCp, normal);
            s_fi_cp_RelativeVelocity?.SetValue(boxedCp, relVel);
            s_fi_cp_Collider?.SetValue(boxedCp, otherCollider.GetInstanceID());
            s_fi_cp_OtherCollider?.SetValue(boxedCp, sharpCollider.GetInstanceID());
            s_fi_cp_Rigidbody?.SetValue(boxedCp, otherPhys.rigidbody.GetInstanceID());
            s_fi_cp_OtherRigidbody?.SetValue(boxedCp, PhysicalBehaviour.rigidbody.GetInstanceID());
            s_fi_cp_Enabled?.SetValue(boxedCp, 1);

            s_fi_ContactCount?.SetValue(collision, 1);
            s_fi_ReusedContacts?.SetValue(collision, new[] { (ContactPoint2D)boxedCp });

            gameObject.SendMessage("OnCollisionEnter2D", collision, SendMessageOptions.DontRequireReceiver);
            otherPhys.gameObject.SendMessage("OnCollisionEnter2D", collision, SendMessageOptions.DontRequireReceiver);
        }
        catch { }
    }

    public void OnGripped(GripBehaviour gripper) => gripPerson = gripper.transform.root.GetComponent<PersonBehaviour>();

    public void OnDrop(GripBehaviour formerGripper)
    {
        if (formerGripper.transform.root.GetComponent<PersonBehaviour>() == gripPerson)
            gripPerson = null;
    }
}
