using Fusion;
using Oculus.Interaction;
using Oculus.Interaction.HandGrab;
using System.Collections;
using UnityEngine;

public class MUES_Chair : MUES_AnchoredNetworkBehaviour
{   
    [Tooltip("If true, only the host can grab this chair.")]
    public bool onlyHostCanGrab = true;

    [Tooltip("Base size (X, Z) of the detection box.")]
    public Vector2 detectionBaseSize = new Vector2(0.5f, 0.5f);
    
    [Tooltip("Height of the detection box starting from the transform's pivot.")]
    public float detectionHeight = 1.2f;

    [Tooltip("Layer of the objects (Avatars) that trigger occupancy.")]
    public LayerMask detectionLayer;

    [Networked] public NetworkBool IsOccupied { get; set; } // Whether the chair is currently occupied

    private Vector2 detectionOffset = Vector2.zero; // Offset of the detection box from the chair's pivot
    private readonly Collider[] _results = new Collider[1]; // Reusable array for overlap results

    private Grabbable _grabbable;   // For general grab interactions
    private GrabInteractable _grabInteractable; // For grab interactions
    private HandGrabInteractable _handGrabInteractable; // For hand grab interactions

    private bool _wasMasterClient; // Track master client status
    private bool _isBeingGrabbed = false; // Track if chair is currently grabbed

    public override void Spawned()
    {
        base.Spawned();

        if (MUES_RoomVisualizer.Instance != null && !MUES_RoomVisualizer.Instance.chairsInScene.Contains(this))
            MUES_RoomVisualizer.Instance.chairsInScene.Add(this);

        _grabbable = GetComponent<Grabbable>();
        _grabInteractable = GetComponent<GrabInteractable>();
        _handGrabInteractable = GetComponent<HandGrabInteractable>();

        SetGrabbableComponentsEnabled(false);

        if (_grabbable != null)
            _grabbable.WhenPointerEventRaised += OnPointerEvent;

        _wasMasterClient = Runner.IsSharedModeMasterClient;
        
        StartCoroutine(InitChairRoutine());
    }

    /// <summary>
    /// Helper method to enable/disable all grabbable-related components at once.
    /// </summary>
    private void SetGrabbableComponentsEnabled(bool enabled)
    {
        if (_grabbable != null) _grabbable.enabled = enabled;
        if (_grabInteractable != null) _grabInteractable.enabled = enabled;
        if (_handGrabInteractable != null) _handGrabInteractable.enabled = enabled;
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
        UpdateGrabbableState();
    }

    public override void Render()
    {
        if (initialized && !Object.HasStateAuthority && !Object.HasInputAuthority && anchorReady)
            AnchorToWorld();

        if (Runner.IsSharedModeMasterClient != _wasMasterClient)
        {
            _wasMasterClient = Runner.IsSharedModeMasterClient;
            ConsoleMessage.Send(true, $"Chair - Master client status changed, updating grabbable state.", Color.cyan);
            UpdateGrabbableState();
        }
    }

    /// <summary>
    /// Handles pointer events for grab detection.
    /// </summary>
    private void OnPointerEvent(PointerEvent evt)
    {
        switch (evt.Type)
        {
            case PointerEventType.Select:
                _isBeingGrabbed = true;
                break;
            case PointerEventType.Unselect:
            case PointerEventType.Cancel:
                _isBeingGrabbed = false;
                break;
        }
    }

    /// <summary>
    /// Updates grabbable state based on hostCanGrab setting and client type.
    /// </summary>
    private void UpdateGrabbableState()
    {
        if (_grabbable == null)
        {
            _grabbable = GetComponent<Grabbable>();
            _grabInteractable = GetComponent<GrabInteractable>();
            _handGrabInteractable = GetComponent<HandGrabInteractable>();
        }
        
        if (_grabbable == null && _grabInteractable == null && _handGrabInteractable == null) 
            return;

        bool canGrab = !onlyHostCanGrab || Runner.IsSharedModeMasterClient;

        SetGrabbableComponentsEnabled(canGrab);

        ConsoleMessage.Send(true, $"Chair - Grabbable {(canGrab ? "enabled" : "disabled")} (onlyHostCanGrab={onlyHostCanGrab}, isMaster={Runner.IsSharedModeMasterClient})", Color.cyan);
    }

    /// <summary>
    /// Public method to force update grabbable state (called after migration).
    /// </summary>
    public void RefreshGrabbableState() => UpdateGrabbableState();

    public override void Despawned(NetworkRunner runner, bool hasState)
    {
        base.Despawned(runner, hasState);

        if (_grabbable != null)
            _grabbable.WhenPointerEventRaised -= OnPointerEvent;

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

    public override void FixedUpdateNetwork()
    {
        if (!initialized || !anchorReady) return;

        bool hasAuth = false;
        try
        {
            if (Object == null || !Object.IsValid) return;
            hasAuth = Object.HasStateAuthority || Object.HasInputAuthority;
        }
        catch { return; }

        if (hasAuth)
        {
            if (_isBeingGrabbed)
                WorldToAnchor();

            Vector3 localOffset = new Vector3(detectionOffset.x, detectionHeight * 0.5f, detectionOffset.y);
            Vector3 worldOffset = transform.rotation * localOffset; 
            Vector3 center = transform.position + worldOffset;

            Vector3 halfSize = new Vector3(detectionBaseSize.x * 0.5f, detectionHeight * 0.5f, detectionBaseSize.y * 0.5f);

            int hits = Physics.OverlapBoxNonAlloc(center, halfSize, _results, transform.rotation, detectionLayer);

            bool isHit = hits > 0;

            try
            {
                if (IsOccupied != isHit)
                    IsOccupied = isHit;
            }
            catch { }
        }
    }

    private void OnDrawGizmos()
    {
        bool isOccupiedSafe = false;

        if (Object != null && Object.IsValid)
            isOccupiedSafe = IsOccupied;

        Gizmos.color = isOccupiedSafe ? new Color(1f, 0f, 0f, 0.4f) : new Color(0f, 1f, 0f, 0.4f);

        Matrix4x4 oldMatrix = Gizmos.matrix;

        Vector3 localOffset = new Vector3(detectionOffset.x, detectionHeight * 0.5f, detectionOffset.y);
        Vector3 worldOffset = transform.rotation * localOffset;
        Vector3 center = transform.position + worldOffset;
        
        Gizmos.matrix = Matrix4x4.TRS(center, transform.rotation, Vector3.one);
        Vector3 size = new Vector3(detectionBaseSize.x, detectionHeight, detectionBaseSize.y);
        
        Gizmos.DrawCube(Vector3.zero, size);
        Gizmos.DrawWireCube(Vector3.zero, size);
        
        Gizmos.matrix = oldMatrix;
    }
}