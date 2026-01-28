using Fusion;
using System.Collections;
using System.Reflection;
using UnityEngine;

public class MUES_Chair : MUES_AnchoredNetworkBehaviour
{   
    [Tooltip("Base size (X, Z) of the detection box.")]
    public Vector2 detectionBaseSize = new Vector2(0.5f, 0.5f);
    
    [Tooltip("Height of the detection box starting from the transform's pivot.")]
    public float detectionHeight = 1.2f;

    [Tooltip("Layer of the objects (Avatars) that trigger occupancy.")]
    public LayerMask detectionLayer;

    [Networked] public NetworkBool IsOccupied { get; set; } // Indicates if the chair is currently occupied

    private Vector2 detectionOffset = Vector2.zero; // Offset of the detection box in local space
    private readonly Collider[] _results = new Collider[1]; // Reusable array for overlap results

    private MUES_NetworkedTransform _networkedTransform;    // Reference to the NetworkedTransform component

    private static readonly FieldInfo _isBeingGrabbedField = 
        typeof(MUES_NetworkedTransform).GetField("_isBeingGrabbed", BindingFlags.NonPublic | BindingFlags.Instance);    // Reflection field info for _isBeingGrabbed

    public override void Spawned()
    {
        base.Spawned();

        if (MUES_RoomVisualizer.Instance != null && !MUES_RoomVisualizer.Instance.chairsInScene.Contains(this))
            MUES_RoomVisualizer.Instance.chairsInScene.Add(this);

        _networkedTransform = GetComponent<MUES_NetworkedTransform>();
        
        StartCoroutine(InitChairRoutine());
    }

    /// <summary>
    /// Initializes the chair anchor and sets initial position.
    /// </summary>
    private IEnumerator InitChairRoutine()
    {
        yield return InitAnchorRoutine();

        if (Object.HasStateAuthority || Object.HasInputAuthority)
        {
            WorldToAnchor();
            ConsoleMessage.Send(true, $"Chair - Authority initialized anchor offset: {LocalAnchorOffset}", Color.cyan);
        }
        else
        {
            float timeout = 5f;
            float elapsed = 0f;
            
            while (LocalAnchorOffset == Vector3.zero && 
                   LocalAnchorRotationOffset == Quaternion.identity && 
                   elapsed < timeout)
            {
                elapsed += Time.deltaTime;
                yield return null;
            }

            AnchorToWorld();
            ConsoleMessage.Send(true, $"Chair - Non-authority applied anchor offset: {LocalAnchorOffset}", Color.cyan);
        }

        initialized = true;
    }

    /// <summary>
    /// Called every frame to update the visual representation for non-authority clients.
    /// </summary>
    public override void Render()
    {
        if (initialized && !Object.HasStateAuthority && !Object.HasInputAuthority && anchorReady)
            AnchorToWorld();
    }

    /// <summary>
    /// Gets called when the object is despawned.
    /// </summary>
    public override void Despawned(NetworkRunner runner, bool hasState)
    {
        base.Despawned(runner, hasState);

        if (MUES_RoomVisualizer.Instance != null && MUES_RoomVisualizer.Instance.chairsInScene != null)
        {
            try
            {
                MUES_RoomVisualizer.Instance.chairsInScene.Remove(this);
            }
            catch (System.Exception ex)
            {
                Debug.LogWarning($"Chair - Failed to remove from chairsInScene: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Called on the network tick to check for occupancy.
    /// </summary>
    public override void FixedUpdateNetwork()
    {
        if (!initialized || !anchorReady) return;

        try
        {
            if (Object == null || !Object.IsValid) return;
            if (!Object.HasStateAuthority && !Object.HasInputAuthority) return;
        }
        catch { return; }

        if (_networkedTransform != null && IsNetworkedTransformGrabbed())
            WorldToAnchor();

        GetDetectionBoxParams(out Vector3 center, out Vector3 halfSize);

        int hits = Physics.OverlapBoxNonAlloc(center, halfSize, _results, transform.rotation, detectionLayer);

        try
        {
            if (IsOccupied != (hits > 0))
                IsOccupied = hits > 0;
        }
        catch { }
    }

    /// <summary>
    /// Checks if the NetworkedTransform is currently being grabbed.
    /// </summary>
    private bool IsNetworkedTransformGrabbed()
    {
        if (_networkedTransform == null || _isBeingGrabbedField == null) return false;
        return (bool)_isBeingGrabbedField.GetValue(_networkedTransform);
    }

    /// <summary>
    /// Gets the parameters for the detection box.
    /// </summary>
    private void GetDetectionBoxParams(out Vector3 center, out Vector3 halfSize)
    {
        Vector3 localOffset = new Vector3(detectionOffset.x, detectionHeight * 0.5f, detectionOffset.y);
        center = transform.position + transform.rotation * localOffset;
        halfSize = new Vector3(detectionBaseSize.x * 0.5f, detectionHeight * 0.5f, detectionBaseSize.y * 0.5f);
    }

    private void OnDrawGizmos()
    {
        bool isOccupiedSafe = Object != null && Object.IsValid && IsOccupied;

        Gizmos.color = isOccupiedSafe ? new Color(1f, 0f, 0f, 0.4f) : new Color(0f, 1f, 0f, 0.4f);

        Matrix4x4 oldMatrix = Gizmos.matrix;

        GetDetectionBoxParams(out Vector3 center, out _);
        
        Gizmos.matrix = Matrix4x4.TRS(center, transform.rotation, Vector3.one);
        Vector3 size = new Vector3(detectionBaseSize.x, detectionHeight, detectionBaseSize.y);
        
        Gizmos.DrawCube(Vector3.zero, size);
        Gizmos.DrawWireCube(Vector3.zero, size);
        
        Gizmos.matrix = oldMatrix;
    }
}