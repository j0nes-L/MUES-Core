using System.Collections.Generic;
using UnityEngine;

public class ScalableTableComponent : MonoBehaviour
{
    [Tooltip("Prefab for the table leg. Should have a child object for the leg shaft.")]
    public GameObject legPrefab;

    [Tooltip("The thickness of the table top in world units")]
    public float tableTopThickness = 0.05f;

    Transform tableTop;
    Renderer topRend;

    float legInset = .05f;
    float legSpacingStep = 3f;

    class LegData
    {
        public Transform root;
        public Transform shaft;
        public Renderer shaftRenderer;
        public Renderer baseRenderer;
        public Vector3 baseLocalScale;
        public Vector3 shaftBaseLocalScale;
    }

    readonly List<LegData> legs = new List<LegData>();

    Vector3 previousParentScale;
    bool initialized = false;

    void Awake()
    {
        tableTop = transform.GetChild(0);
        topRend = tableTop.GetComponent<Renderer>();
    }

    void Start()
    {
        Invoke(nameof(DelayedInit), 0.1f);
    }

    void DelayedInit()
    {
        if (transform.parent != null)
        {
            previousParentScale = transform.parent.localScale;
            ApplyTransform();
            initialized = true;
        }
    }

    void LateUpdate()
    {
        if (transform.parent == null) return;

        if (!initialized || transform.parent.localScale != previousParentScale)
        {
            ApplyTransform();
            previousParentScale = transform.parent.localScale;
            initialized = true;
        }
    }

    void ApplyTransform()
    {
        if (!legPrefab || !topRend) return;
        if (transform.parent == null || transform.parent.localScale == Vector3.zero) return;

        float parentScaleY = Mathf.Max(0.001f, transform.parent.localScale.y);
        float fixedYScale = tableTopThickness / parentScaleY;
        tableTop.localScale = new Vector3(tableTop.localScale.x, fixedYScale, tableTop.localScale.z);

        var lb = topRend.localBounds;
        var c = lb.center;
        var e = lb.extents;

        float invSx = 1f / Mathf.Max(1e-6f, transform.localScale.x);
        float invSy = 1f / Mathf.Max(1e-6f, transform.localScale.y);
        float invSz = 1f / Mathf.Max(1e-6f, transform.localScale.z);

        float insetX = legInset * invSx;
        float insetZ = legInset * invSz;
        float yBottom = lb.min.y;

        Vector3[] localCorners =
        {
            new Vector3(c.x - (e.x - insetX), yBottom, c.z - (e.z - insetZ)),
            new Vector3(c.x + (e.x - insetX), yBottom, c.z - (e.z - insetZ)),
            new Vector3(c.x - (e.x - insetX), yBottom, c.z + (e.z - insetZ)),
            new Vector3(c.x + (e.x - insetX), yBottom, c.z + (e.z - insetZ))
        };

        int xDiv = Mathf.FloorToInt(transform.localScale.x / legSpacingStep);
        int zDiv = Mathf.FloorToInt(transform.localScale.z / legSpacingStep);

        var desired = new List<Vector3>();
        AddLegLine(localCorners[0], localCorners[1], xDiv, desired);
        AddLegLine(localCorners[2], localCorners[3], xDiv, desired);
        AddLegLine(localCorners[0], localCorners[2], zDiv, desired);
        AddLegLine(localCorners[1], localCorners[3], zDiv, desired);

        for (int i = desired.Count - 1; i >= 0; i--)
        {
            for (int j = i - 1; j >= 0; j--)
            {
                if ((desired[i] - desired[j]).sqrMagnitude < 1e-6f)
                {
                    desired.RemoveAt(i);
                    break;
                }
            }
        }

        while (legs.Count < desired.Count)
        {
            var legRoot = Instantiate(legPrefab, transform).transform;
            legRoot.rotation = transform.rotation;

            Transform shaft = legRoot.childCount > 0 ? legRoot.GetChild(0) : null;
            Renderer shaftR = shaft ? shaft.GetComponent<Renderer>() : null;
            Renderer baseR = legRoot.GetComponent<Renderer>();

            legs.Add(new LegData
            {
                root = legRoot,
                shaft = shaft,
                shaftRenderer = shaftR,
                baseRenderer = baseR,
                baseLocalScale = legRoot.localScale,
                shaftBaseLocalScale = shaft != null ? shaft.localScale : Vector3.one
            });
        }

        while (legs.Count > desired.Count)
        {
            int idx = legs.Count - 1;
            if (legs[idx].root) Destroy(legs[idx].root.gameObject);
            legs.RemoveAt(idx);
        }

        float floorY = MUES_RoomVisualizer.floorHeight;
        float tableBottomWorldY = topRend.bounds.min.y;

        for (int i = 0; i < desired.Count; i++)
        {
            var ld = legs[i];
            Vector3 localPos = desired[i];
            Vector3 worldCorner = tableTop.TransformPoint(localPos);

            Vector3 worldBasePos = new Vector3(worldCorner.x, floorY, worldCorner.z);
            ld.root.position = worldBasePos;

            ld.root.localScale = new Vector3(
                ld.baseLocalScale.x * invSx,
                ld.baseLocalScale.y * invSy,
                ld.baseLocalScale.z * invSz
            );

            if (ld.baseRenderer != null)
            {
                float visualMinY = ld.baseRenderer.bounds.min.y;
                if (visualMinY < floorY - 0.001f)
                {
                    float correction = floorY - visualMinY;
                    ld.root.position += new Vector3(0, correction, 0);
                }
            }

            // 3. Scale Shaft
            if (ld.shaft != null)
            {
                float shaftStartY = floorY;
                if (ld.baseRenderer != null) shaftStartY = ld.baseRenderer.bounds.max.y;

                shaftStartY = Mathf.Max(shaftStartY, floorY);

                float desiredShaftHeight = Mathf.Max(0.01f, tableBottomWorldY - shaftStartY);
                float rootScaleY = ld.root.lossyScale.y;
                
                float shaftLocalY = (shaftStartY - ld.root.position.y) / Mathf.Max(0.0001f, rootScaleY);
                ld.shaft.localPosition = new Vector3(ld.shaft.localPosition.x, shaftLocalY, ld.shaft.localPosition.z);

                float meshHeight = 1f;
                if (ld.shaftRenderer != null)
                {
                     var mf = ld.shaft.GetComponent<MeshFilter>();
                     if (mf && mf.sharedMesh) meshHeight = mf.sharedMesh.bounds.size.y;
                     else meshHeight = ld.shaftRenderer.bounds.size.y / Mathf.Max(0.0001f, ld.shaft.lossyScale.y);
                }
                
                float requiredScaleY = desiredShaftHeight / (Mathf.Max(0.0001f, rootScaleY) * Mathf.Max(0.0001f, meshHeight));
                
                Vector3 newShaftScale = ld.shaftBaseLocalScale;
                newShaftScale.y = requiredScaleY;
                ld.shaft.localScale = newShaftScale;

                if (ld.shaftRenderer != null)
                    ld.shaftRenderer.enabled = true;
            }
        }
    }

    void AddLegLine(Vector3 start, Vector3 end, int divisions, List<Vector3> list)
    {
        list.Add(start);
        for (int i = 0; i < divisions; i++)
        {
            float t = (i + 1f) / (divisions + 1f);
            list.Add(Vector3.Lerp(start, end, t));
        }
        list.Add(end);
    }
}