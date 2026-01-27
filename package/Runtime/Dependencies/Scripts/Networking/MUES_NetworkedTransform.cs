using Fusion;
using Meta.XR.MultiplayerBlocks.Fusion;
using Meta.XR.MultiplayerBlocks.Shared;
using Oculus.Interaction;
using Oculus.Interaction.HandGrab;
using System.Threading.Tasks;
using System.Collections;
using UnityEngine;
using UnityEngine.Rendering;
using GLTFast;
using GLTFast.Materials;
using UnityEngine.Rendering.Universal;

#if UNITY_EDITOR
using UnityEditor;
#endif

public class MUES_NetworkedTransform : MUES_AnchoredNetworkBehaviour
{
    [Header("Ownership Settings")]
    [Tooltip("When enabled, only the client who spawned this object can grab/control it. If the spawner leaves, the host takes over.")]
    public bool spawnerOnlyGrab = false;

    [HideInInspector][Networked] public NetworkBool IsGrabbable { get; set; }   // Whether the object is grabbable
    [HideInInspector][Networked] public NetworkBool SpawnerControlsTransform { get; set; } // Synced version of spawnerOnlyGrab
    [HideInInspector][Networked] public int SpawnerPlayerId { get; set; } = -1; // The PlayerId of the client who spawned this object (-1 = not set)
    [HideInInspector][Networked] public NetworkString<_64> ModelFileName { get; set; }  // The filename of the model to load

    private Grabbable _grabbable;   // Grabbable component for interaction
    private GrabInteractable _grabInteractable; // GrabInteractable component for interaction
    private HandGrabInteractable _handGrabInteractable; // HandGrabInteractable component for interaction
    private TransferOwnershipFusion ownershipTransfer; // Component to transfer ownership on select

    private bool modelLoaded = false;   // Flag indicating if the model has been loaded
    private bool _isGrabbableOnSpawn = false; // Cached grabbable state from spawn time
    private int _lastKnownSpawnerPlayerId = -1; // Track SpawnerPlayerId changes for all clients
    private bool _lastKnownSpawnerControlsTransform = false; // Track SpawnerControlsTransform changes

    private Vector3 _lastLocalPosition; // Last cached local position
    private Quaternion _lastLocalRotation;  // Last cached local rotation
    private bool _isBeingGrabbed = false;   // Flag indicating if the object is currently being grabbed
    private float movementThreshold = 0.001f;   // Minimum movement distance to consider as moved
    private float rotationThreshold = 0.1f; // Minimum rotation angle to consider as moved

    /// <summary>
    /// Gets the reference transform for movement detection.
    /// </summary>
    private Transform ReferenceTransform
    {
        get
        {
            var net = MUES_Networking.Instance;
            if (net == null || net.isRemote) return null;

            return net.sceneParent;
        }
    }

    /// <summary>
    /// Checks if the local player is allowed to control this object.
    /// </summary>
    private bool IsLocalPlayerAllowedToControl
    {
        get
        {
            if (!SpawnerControlsTransform || Runner == null) return true;
            
            int localPlayerId = Runner.LocalPlayer.PlayerId;
            if (SpawnerPlayerId == localPlayerId) return true;

            try
            {
                if (Object != null && Object.IsValid && Object.HasInputAuthority) return true;
            }
            catch 
            {
                ConsoleMessage.Send(true, "Networked Transform - Error checking Input Authority", Color.yellow);    
            }
            
            if (Runner.IsSharedModeMasterClient)
            {
                bool spawnerStillConnected = false;
                foreach (var player in Runner.ActivePlayers)
                {
                    if (player.PlayerId == SpawnerPlayerId)
                    {
                        spawnerStillConnected = true;
                        break;
                    }
                }
                
                if (!spawnerStillConnected) return true;
            }
            
            return false;
        }
    }

    public override void Spawned()
    {    
        _isGrabbableOnSpawn = IsGrabbable;
        _lastKnownSpawnerPlayerId = SpawnerPlayerId;
        _lastKnownSpawnerControlsTransform = SpawnerControlsTransform;
        
        if (Object.HasStateAuthority && SpawnerPlayerId == -1)
        {
            if (Object.InputAuthority == PlayerRef.None || Object.InputAuthority.PlayerId < 0)
            {
                SpawnerPlayerId = Runner.LocalPlayer.PlayerId;
                ConsoleMessage.Send(true, $"Networked Transform - SpawnerPlayerId set to LocalPlayer: {SpawnerPlayerId}", Color.cyan);
            }
            else
            {
                SpawnerPlayerId = Object.InputAuthority.PlayerId;
                ConsoleMessage.Send(true, $"Networked Transform - SpawnerPlayerId set to InputAuthority: {SpawnerPlayerId}", Color.cyan);
            }
        }
        
        ConsoleMessage.Send(true, $"Networked Transform - Spawned: IsGrabbable={IsGrabbable}, SpawnerControlsTransform={SpawnerControlsTransform}, SpawnerPlayerId={SpawnerPlayerId}, InputAuth={Object.InputAuthority}, LocalPlayer={Runner?.LocalPlayer}, LocalPlayerId={Runner?.LocalPlayer.PlayerId}", Color.magenta);
        
        DisableExistingGrabbableComponents();     
        StartCoroutine(InitRoutine());
    }

    /// <summary>
    /// Disables any existing grabbable components on the object until permissions are verified.
    /// </summary>
    private void DisableExistingGrabbableComponents()
    {
        var existingGrabbable = GetComponent<Grabbable>();
        var existingGrabInteractable = GetComponent<GrabInteractable>();
        var existingHandGrab = GetComponent<HandGrabInteractable>();
        var existingOwnershipTransfer = GetComponent<TransferOwnershipFusion>();
        
        if (existingGrabbable != null) existingGrabbable.enabled = false;
        if (existingGrabInteractable != null) existingGrabInteractable.enabled = false;
        if (existingHandGrab != null) existingHandGrab.enabled = false;
        if (existingOwnershipTransfer != null) existingOwnershipTransfer.enabled = false;
    }

    /// <summary>
    /// Gets the anchor and initializes the object's position and rotation based on networked data.
    /// </summary>
    IEnumerator InitRoutine()
    {
        yield return null;

        if ((Object.HasInputAuthority || Object.HasStateAuthority) && !SpawnerControlsTransform && spawnerOnlyGrab)
        {
            SpawnerControlsTransform = true;
            ConsoleMessage.Send(true, $"Networked Transform - Applied inspector spawnerOnlyGrab={spawnerOnlyGrab} to network", Color.cyan);
        }

        transform.GetPositionAndRotation(out Vector3 spawnWorldPos, out Quaternion spawnWorldRot);
        bool hadValidSpawnPos = spawnWorldPos != Vector3.zero;
        
        ConsoleMessage.Send(true, $"Networked Transform - Cached spawn: Pos={spawnWorldPos}, Rot={spawnWorldRot.eulerAngles}, valid={hadValidSpawnPos}", Color.cyan);

        yield return InitAnchorRoutine();

        while (MUES_SessionMeta.Instance == null)
        {
            ConsoleMessage.Send(true, "Networked Transform - Waiting for session meta...", Color.yellow);
            yield return null;
        }

        ownershipTransfer = GetComponent<TransferOwnershipFusion>();

        bool isSpawner = Object.HasInputAuthority || (Runner.GameMode == GameMode.Shared && Object.HasStateAuthority);
        
        if (isSpawner)
        {
            yield return null;
            
            if (hadValidSpawnPos)
            {
                transform.SetPositionAndRotation(spawnWorldPos, spawnWorldRot);
                ConsoleMessage.Send(true, $"Networked Transform - Restored spawn position after parenting: {spawnWorldPos}", Color.cyan);
            }
            
            if (transform.position == Vector3.zero)
                ConsoleMessage.Send(true, "Networked Transform - WARNING: Position is still at origin after restore!", Color.red);
            
            WorldToAnchor();
            ConsoleMessage.Send(true, $"Networked Transform - WorldToAnchor set: Pos={transform.position}, Offset={LocalAnchorOffset}, RotOffset={LocalAnchorRotationOffset.eulerAngles}", Color.cyan);
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

            if (elapsed >= timeout)
                ConsoleMessage.Send(true, $"Networked Transform - Timeout waiting for anchor offset!", Color.yellow);
            else
                ConsoleMessage.Send(true, $"Networked Transform - Received anchor offset: {LocalAnchorOffset}, rot: {LocalAnchorRotationOffset.eulerAngles}", Color.cyan);
            
            if (anchorReady && anchor != null)
            {
                AnchorToWorld();
                ConsoleMessage.Send(true, $"Networked Transform - AnchorToWorld applied: NewPos={transform.position}, NewRot={transform.rotation.eulerAngles}", Color.cyan);
            }
            else 
                ConsoleMessage.Send(true, "Networked Transform - Anchor not ready for AnchorToWorld!", Color.red);
        }

        yield return LoadModelIfNeeded();
        UpdateGrabbableState();
        
        CacheCurrentPosition();
        initialized = true;
        
        ConsoleMessage.Send(true, $"Networked Transform - Init complete. Final Pos={transform.position}, Rot={transform.rotation.eulerAngles}", Color.green);
    }

    /// <summary>
    /// Caches the current position relative to the reference transform or in world space.
    /// </summary>
    private void CacheCurrentPosition()
    {
        var refTransform = ReferenceTransform;
        
        if (refTransform != null)
        {
            _lastLocalPosition = refTransform.InverseTransformPoint(transform.position);
            _lastLocalRotation = Quaternion.Inverse(refTransform.rotation) * transform.rotation;
        }
        else
        {
            _lastLocalPosition = transform.position;
            _lastLocalRotation = transform.rotation;
        }
    }

    /// <summary>
    /// Checks if the object has moved significantly.
    /// </summary>
    private bool HasMovedSignificantly()
    {
        var refTransform = ReferenceTransform;

        Vector3 currentPos;
        Quaternion currentRot;

        if (refTransform != null)
        {
            currentPos = refTransform.InverseTransformPoint(transform.position);
            currentRot = Quaternion.Inverse(refTransform.rotation) * transform.rotation;
        }
        else
        {
            currentPos = transform.position;
            currentRot = transform.rotation;
        }

        float posDelta = Vector3.Distance(_lastLocalPosition, currentPos);
        float rotDelta = Quaternion.Angle(_lastLocalRotation, currentRot);

        bool hasMoved = posDelta > movementThreshold || rotDelta > rotationThreshold;

        if (hasMoved)
        {
            _lastLocalPosition = currentPos;
            _lastLocalRotation = currentRot;
        }

        return hasMoved;
    }

    /// <summary>
    /// Gets executed at fixed network intervals to update the networked anchor offsets.
    /// </summary>
    public override void FixedUpdateNetwork()
    {
        bool hasAuth = false;
        try
        {
            if (Object == null || !Object.IsValid) return;
            hasAuth = Object.HasInputAuthority || Object.HasStateAuthority;
        }
        catch { return; }
        
        if (!hasAuth || !initialized || !anchorReady) return;
        
        if (_isBeingGrabbed || HasMovedSignificantly())
            WorldToAnchor();
    }

    /// <summary>
    /// Called every frame to update the visual representation for non-authority clients.
    /// </summary>
    public override void Render()
    {
        if (!initialized || !anchorReady) return;
        
        bool hasAuth = false;
        try
        {
            if (Object == null || !Object.IsValid) return;
            hasAuth = Object.HasInputAuthority || Object.HasStateAuthority;
        }
        catch { return; }
        
        if (!hasAuth)
            AnchorToWorld();

        if (_lastKnownSpawnerPlayerId != SpawnerPlayerId)
        {
            ConsoleMessage.Send(true, $"Networked Transform - SpawnerPlayerId changed from {_lastKnownSpawnerPlayerId} to {SpawnerPlayerId} on {gameObject.name}", Color.cyan);
            _lastKnownSpawnerPlayerId = SpawnerPlayerId;
            UpdateGrabbableState();
        }

        if (_lastKnownSpawnerControlsTransform != SpawnerControlsTransform)
        {
            ConsoleMessage.Send(true, $"Networked Transform - SpawnerControlsTransform changed from {_lastKnownSpawnerControlsTransform} to {SpawnerControlsTransform} on {gameObject.name}", Color.cyan);
            _lastKnownSpawnerControlsTransform = SpawnerControlsTransform;
            UpdateGrabbableState();
        }

        if (Time.frameCount % 60 == 0 && _grabbable != null)
        {
            bool shouldBeEnabled = IsLocalPlayerAllowedToControl;
            if (_grabbable.enabled != shouldBeEnabled)
                UpdateGrabbableState();
        }
    }

    #region Model Loading

    /// <summary>
    /// Loads the model from the ModelFileName property if it's set.
    /// </summary>
    private IEnumerator LoadModelIfNeeded()
    {
        float timeout = 10f;
        float elapsed = 0f;

        while (elapsed < timeout)
        {
            if (Object == null || !Object.IsValid) yield break;

            if (!string.IsNullOrEmpty(ModelFileName.ToString())) break;

            elapsed += Time.deltaTime;
            yield return null;
        }

        if (Object == null || !Object.IsValid) yield break;

        string modelName = ModelFileName.ToString();
        if (string.IsNullOrEmpty(modelName))
        {
            ConsoleMessage.Send(true, "Networked Transform - No model filename set - using static prefab.", Color.cyan);
            yield break;
        }

        if (modelLoaded || transform.childCount > 0)
        {
            ConsoleMessage.Send(true, "Networked Transform - Model already loaded.", Color.yellow);
            yield break;
        }

        ConsoleMessage.Send(true, $"Networked Transform - Loading model: {modelName}", Color.cyan);

        if (MUES_NetworkedObjectManager.Instance == null)
        {
            ConsoleMessage.Send(true, "Networked Transform - NetworkedObjectManager not available.", Color.red);
            yield break;
        }

        var fetchTask = MUES_NetworkedObjectManager.Instance.FetchModelFromServer(modelName);
        while (!fetchTask.IsCompleted)
            yield return null;

        string localPath = fetchTask.Result;
        if (string.IsNullOrEmpty(localPath))
        {
            ConsoleMessage.Send(true, $"Networked Transform - Failed to fetch model: {modelName}", Color.red);
            yield break;
        }

        var loadTask = LoadModelLocally(localPath);
        while (!loadTask.IsCompleted)
            yield return null;

        modelLoaded = true;

        if (_isGrabbableOnSpawn)
        {
            InitGrabbableComponents();
            
            yield return null;

            if (Object == null || !Object.IsValid) yield break;

            UpdateGrabbableState();
            
            bool isAllowed = false;
            try { isAllowed = IsLocalPlayerAllowedToControl; } catch {}

            ConsoleMessage.Send(true, $"Networked Transform - Model loaded and grabbable initialized. IsAllowed={isAllowed}", Color.green);
            
            if (anchorReady)
            {
                if (Object.HasInputAuthority || Object.HasStateAuthority) WorldToAnchor();
                else AnchorToWorld();
            }
        }
    }

    /// <summary>
    /// Loads a GLB model and attaches it as a child of this transform.
    /// </summary>
    private async Task LoadModelLocally(string path)
    {
        if (MUES_NetworkedObjectManager.Instance != null)
            await MUES_NetworkedObjectManager.Instance.WaitForInstantiationPermit();

        try
        {
            IMaterialGenerator materialGenerator = CreateMaterialGenerator();
            var gltfImport = new GltfImport(materialGenerator: materialGenerator);

            var settings = new ImportSettings
            {
                GenerateMipMaps = true,
                AnisotropicFilterLevel = 3,
                NodeNameMethod = NameImportMethod.OriginalUnique
            };

            bool success = false;
            try 
            {
                success = await gltfImport.Load($"file://{path}", settings);
            }
            catch (System.Exception ex)
            {
                ConsoleMessage.Send(true, $"Networked Transform - Exception checking GLB: {ex.Message}", Color.red);
            }

            if (!success)
            {
                ConsoleMessage.Send(true, $"Networked Transform - Failed to load GLB: {path}. Deleting potential corrupt file.", Color.red);
                try {
                    if (System.IO.File.Exists(path)) System.IO.File.Delete(path);
                }
                catch {}
                return;
            }

            ConsoleMessage.Send(true, "Networked Transform - Starting GLTF Instantiation...", Color.cyan);
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();

            var wrapper = new GameObject("GLTF_Wrapper");
            wrapper.transform.SetParent(transform, false);
            wrapper.transform.localPosition = Vector3.zero;
            wrapper.transform.localRotation = Quaternion.identity;

            bool instantiateSuccess = await gltfImport.InstantiateMainSceneAsync(wrapper.transform);

            stopwatch.Stop();
            ConsoleMessage.Send(true, $"Networked Transform - GLTF Instantation took {stopwatch.ElapsedMilliseconds}ms.", Color.cyan);

            if (!instantiateSuccess)
            {
                ConsoleMessage.Send(true, $"Networked Transform - Failed to instantiate GLB: {path}", Color.red);
                return;
            }

            ConsoleMessage.Send(true, "Networked Transform - Starting Collider Generation...", Color.cyan);
            await AddMeshCollidersAsync(wrapper.transform);

            ConsoleMessage.Send(true, $"Networked Transform - Model loaded successfully: {path}", Color.green);
        }
        finally
        {
            if (MUES_NetworkedObjectManager.Instance != null) MUES_NetworkedObjectManager.Instance.ReleaseInstantiationPermit();
        }
    }

    /// <summary>
    /// Creates the appropriate material generator based on the current render pipeline.
    /// </summary>
    private IMaterialGenerator CreateMaterialGenerator()
    {
        var renderPipelineAsset = GraphicsSettings.currentRenderPipeline;
        
        if (renderPipelineAsset != null && 
            renderPipelineAsset.GetType().Name.Contains("Universal"))
        {
            var urpAsset = renderPipelineAsset as UniversalRenderPipelineAsset;
            if (urpAsset != null)
                return new UniversalRPMaterialGenerator(urpAsset);
        }

        return new BuiltInMaterialGenerator();
    }

    /// <summary>
    /// Recursively adds MeshColliders to all children with MeshFilters.
    /// </summary>
    private async Task AddMeshCollidersAsync(Transform parent)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var stepWatch = new System.Diagnostics.Stopwatch(); 
        int processedCount = 0;

        var nodesToProcess = new System.Collections.Generic.List<Transform>();
        GetChildrenRecursive(parent, nodesToProcess);

        ConsoleMessage.Send(true, $"Networked Transform - Processing {nodesToProcess.Count} nodes for colliders.", Color.cyan);

        foreach (Transform child in nodesToProcess)
        {
            stepWatch.Restart();

            if (child.TryGetComponent<MeshFilter>(out var filter) && filter.sharedMesh != null)
            {
                if (!child.TryGetComponent<MeshCollider>(out _))
                {
                    if (filter.sharedMesh.vertexCount > 5000) 
                    {
                        MeshCollider col = child.gameObject.AddComponent<MeshCollider>();
                        col.sharedMesh = filter.sharedMesh;
                        col.convex = false; 
                    }
                    else
                    {
                        MeshCollider col = child.gameObject.AddComponent<MeshCollider>();
                        col.sharedMesh = filter.sharedMesh;
                        col.convex = true;
                    }
                }
            }
            
            stepWatch.Stop();
            if (stepWatch.ElapsedMilliseconds > 20)
                ConsoleMessage.Send(true, $"Networked Transform - Single collider gen took {stepWatch.ElapsedMilliseconds}ms for {child.name} (Verts: {child.GetComponent<MeshFilter>()?.sharedMesh.vertexCount})", Color.yellow);

            processedCount++;

            if (stopwatch.ElapsedMilliseconds > 8)
            {
                await Task.Yield(); 
                stopwatch.Restart();
            }
        }
        ConsoleMessage.Send(true, "Networked Transform - Collider Generation finished.", Color.green);
    }

    /// <summary>
    /// Gets all child transforms recursively.
    /// </summary>
    private void GetChildrenRecursive(Transform parent, System.Collections.Generic.List<Transform> list)
    {
        foreach (Transform child in parent)
        {
            list.Add(child);
            if (child.childCount > 0)
                GetChildrenRecursive(child, list);
        }
    }

    #endregion

    #region Grabbable Components

    /// <summary>
    /// Initializes the necessary components to make the object grabbable in a networked environment.
    /// </summary>
    public void InitGrabbableComponents()
    {
        if (_grabbable == null)
        {
            Rigidbody rb = gameObject.GetComponent<Rigidbody>();
            if (rb == null)
            {
                rb = gameObject.AddComponent<Rigidbody>();
                rb.isKinematic = true;
                rb.useGravity = false;
            }

            _grabbable = gameObject.AddComponent<Grabbable>();
            _grabInteractable = gameObject.AddComponent<GrabInteractable>();
            _handGrabInteractable = gameObject.AddComponent<HandGrabInteractable>();

            _grabbable.InjectOptionalRigidbody(rb);

            _grabInteractable.InjectOptionalPointableElement(_grabbable);
            _grabInteractable.InjectRigidbody(rb);

            _handGrabInteractable.InjectOptionalPointableElement(_grabbable);
            _handGrabInteractable.InjectRigidbody(rb);

            gameObject.AddComponent<TransferOwnershipOnSelect>();
            
            ownershipTransfer = gameObject.GetComponent<TransferOwnershipFusion>();
            if (ownershipTransfer == null)
                ownershipTransfer = gameObject.AddComponent<TransferOwnershipFusion>();

            _grabbable.WhenPointerEventRaised += OnPointerEvent;

            SetGrabbableComponentsEnabled(false);

            ConsoleMessage.Send(true, $"Networked Transform - Grabbable components added (initially disabled). Grabbable={_grabbable != null}, GrabInteractable={_grabInteractable != null}, HandGrab={_handGrabInteractable != null}", Color.green);
        }
        else 
        {
            ConsoleMessage.Send(true, "Networked Transform - Object already grabbable", Color.yellow);
        }
    }

    /// <summary>
    /// Helper method to enable/disable all grabbable-related components at once.
    /// </summary>
    private void SetGrabbableComponentsEnabled(bool enabled)
    {
        if (_grabbable != null) _grabbable.enabled = enabled;
        if (_grabInteractable != null) _grabInteractable.enabled = enabled;
        if (_handGrabInteractable != null) _handGrabInteractable.enabled = enabled;
        if (ownershipTransfer != null) ownershipTransfer.enabled = enabled;
    }

    /// <summary>
    /// Handles pointer events for grab detection.
    /// </summary>
    private void OnPointerEvent(PointerEvent evt)
    {
        switch (evt.Type)
        {
            case PointerEventType.Select:
                OnGrabbed();
                break;
            case PointerEventType.Unselect:
            case PointerEventType.Cancel:
                OnReleased();
                break;
        }
    }

    /// <summary>
    /// Called when the object is grabbed.
    /// </summary>
    public void OnGrabbed() => _isBeingGrabbed = true;

    /// <summary>
    /// Called when the object is released.
    /// </summary>
    public void OnReleased()
    {
        _isBeingGrabbed = false;
        CacheCurrentPosition();
    }

    /// <summary>
    /// Updates the grabbable state based on network properties.
    /// </summary>
    private void UpdateGrabbableState()
    {
        if (_grabbable == null)
        {
            _grabbable = GetComponent<Grabbable>();
            _grabInteractable = GetComponent<GrabInteractable>();
            _handGrabInteractable = GetComponent<HandGrabInteractable>();
            ownershipTransfer = GetComponent<TransferOwnershipFusion>();
        }
        
        if (_grabbable == null && _grabInteractable == null && _handGrabInteractable == null) 
            return;

        bool isAllowed = true;
        try
        {
            isAllowed = IsLocalPlayerAllowedToControl;
        }
        catch
        {
            isAllowed = false;
        }

        SetGrabbableComponentsEnabled(isAllowed);

        if (!isAllowed)
        {
            try
            {
                ConsoleMessage.Send(true, $"Networked Transform - Grabbable DISABLED for local player (SpawnerControlsTransform={SpawnerControlsTransform}, SpawnerPlayerId={SpawnerPlayerId}, LocalPlayer={Runner?.LocalPlayer.PlayerId}) on {gameObject.name}", Color.yellow);
            }
            catch
            {
                ConsoleMessage.Send(true, $"Networked Transform - Grabbable DISABLED on {gameObject.name}", Color.yellow);
            }
        }
        else
            ConsoleMessage.Send(true, $"Networked Transform - Grabbable ENABLED for local player on {gameObject.name}", Color.green);
    }

    /// <summary>
    /// Public method to force update grabbable state (called after migration).
    /// </summary>
    public void RefreshGrabbableState() => UpdateGrabbableState();

    /// <summary>
    /// Called by the new master client to take over ownership if the original spawner left.
    /// </summary>
    public void TransferSpawnerOwnership()
    {
        if (Runner == null || !SpawnerControlsTransform) return;
        
        bool spawnerStillConnected = false;
        foreach (var player in Runner.ActivePlayers)
        {
            if (player.PlayerId == SpawnerPlayerId)
            {
                spawnerStillConnected = true;
                break;
            }
        }
        
        if (!spawnerStillConnected)
            StartCoroutine(TransferSpawnerOwnershipAsync());
    }

    /// <summary>
    /// Async coroutine to properly wait for StateAuthority before transferring spawner ownership.
    /// </summary>
    private IEnumerator TransferSpawnerOwnershipAsync()
    {
        if (Runner == null || Object == null || !Object.IsValid) yield break;
        
        ConsoleMessage.Send(true, $"Networked Transform - Starting async spawner ownership transfer for {gameObject.name}", Color.cyan);
        
        bool hasAuth = false;
        try
        {
            hasAuth = Object.HasStateAuthority;
            if (!hasAuth)
            {
                Object.RequestStateAuthority();
                ConsoleMessage.Send(true, $"Networked Transform - Requested StateAuthority for {gameObject.name}", Color.cyan);
            }
        }
        catch (System.Exception ex)
        {
            ConsoleMessage.Send(true, $"Networked Transform - Error requesting StateAuthority: {ex.Message}", Color.yellow);
            yield break;
        }
        
        float timeout = 3f;
        float elapsed = 0f;
        
        while (elapsed < timeout)
        {
            if (Object == null || !Object.IsValid) yield break;
            
            try
            {
                if (Object.HasStateAuthority)
                {
                    hasAuth = true;
                    break;
                }
            }
            catch { yield break; }
            
            elapsed += Time.deltaTime;
            yield return null;
        }
        
        if (!hasAuth)
        {
            ConsoleMessage.Send(true, $"Networked Transform - Timeout waiting for StateAuthority on {gameObject.name}", Color.yellow);
            yield break;
        }
        
        bool spawnerStillConnected = false;
        foreach (var player in Runner.ActivePlayers)
        {
            if (player.PlayerId == SpawnerPlayerId)
            {
                spawnerStillConnected = true;
                break;
            }
        }
        
        if (!spawnerStillConnected)
        {
            try
            {
                int oldSpawnerId = SpawnerPlayerId;
                SpawnerPlayerId = Runner.LocalPlayer.PlayerId;
                
                Object.AssignInputAuthority(Runner.LocalPlayer);              
                ConsoleMessage.Send(true, $"Networked Transform - Transferred spawner ownership from {oldSpawnerId} to {SpawnerPlayerId}", Color.green);
                
                UpdateGrabbableState();
            }
            catch (System.Exception ex)
            {
                ConsoleMessage.Send(true, $"Networked Transform - Error transferring ownership: {ex.Message}", Color.yellow);
            }
        }
    }

    private void OnDestroy()
    {
        if (_grabbable != null)
            _grabbable.WhenPointerEventRaised -= OnPointerEvent;
    }

    #endregion
}

#if UNITY_EDITOR

[CustomEditor(typeof(MUES_NetworkedTransform))]
public class MUES_NetworkedTransformEditor : Editor
{
    public override void OnInspectorGUI()
    {
        DrawDefaultInspector();

        MUES_NetworkedTransform obj = (MUES_NetworkedTransform)target;

        bool hasGrabbable = obj.GetComponent<Grabbable>() != null;

        if (!hasGrabbable)
        {
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Editor Only:", EditorStyles.boldLabel);

            if (GUILayout.Button("Make Grabbable"))
            {
                obj.InitGrabbableComponents();
                EditorUtility.SetDirty(obj);
            }

            EditorGUILayout.Space();
        }
    }
}

#endif